"""Converts ScummVM's engines/sci/engine/script_patches.cpp into Daventry's ScriptPatchTables.g.cs.

    python tools/PortTables/sci_script_patches.py [path/to/scummvm] [path/to/daventry]

Every `static const uint16 xxx[]` signature or patch becomes a ushort array with the SIG_* and
PATCH_* macros expanded the way script_patches.h defines them, every
`static const SciScriptPatcherEntry xxxSignatures[]` table becomes an array of entries, and the
game switch of ScriptPatcher::processScript becomes ForGame(). The selector name table is kept
in order, since the SELECTOR_xxx values index it.
"""

import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCUMMVM = Path(sys.argv[1]) if len(sys.argv) > 1 else HERE.parents[2] / "scummvm"
DAVENTRY = Path(sys.argv[2]) if len(sys.argv) > 2 else HERE.parents[1]

SOURCE = SCUMMVM / "engines" / "sci" / "engine" / "script_patches.cpp"
OUTPUT = DAVENTRY / "src" / "Daventry.Sci" / "Engine" / "ScriptPatchTables.g.cs"

SIG_END = 0xFFFF
SIG_MAGICDWORD = 0xF000
SIG_CODE_ADDTOOFFSET = 0xE000
SIG_CODE_SELECTOR16 = 0x9000
SIG_CODE_SELECTOR8 = 0x8000
SIG_CODE_UINT16 = 0x1000
PATCH_CODE_GETORIGINALBYTES = 0xB000
PATCH_CODE_GETORIGINALBYTE = 0xC000
PATCH_CODE_GETORIGINALUINT16 = 0xD000

# constants from other headers that appear in the tables (kScummVMSleepId, kPhant2VolumeMax, ...)
CONSTANT_HEADERS = [
    SCUMMVM / "engines" / "sci" / "engine" / "kernel.h",
    SCUMMVM / "engines" / "sci" / "engine" / "guest_additions.h",
]
EXTRA_CONSTANTS = {
    "SAVEGAMEID_OFFICIALRANGE_START": 100,  # savegame.h
}


def header_constants() -> dict[str, int]:
    """The `kName = value` enum members of the headers the tables draw on."""
    constants = dict(EXTRA_CONSTANTS)
    for header in CONSTANT_HEADERS:
        text = strip_comments(header.read_text(encoding="utf-8"))
        for name, value in re.findall(r"\b(k\w+)\s*=\s*(0x[0-9A-Fa-f]+|\d+)\b", text):
            constants.setdefault(name, int(value, 0))
    return constants


def game_id(gid: str) -> str:
    """GID_CNICK_KQ -> SciGameId.CnickKq, the naming of the generated enum."""
    return "SciGameId." + "".join(
        part.capitalize() for part in gid[4:].lower().split("_")
    )


def strip_comments(text: str) -> str:
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.DOTALL)
    return re.sub(r"//[^\n]*", "", text)


def split_top_level(body: str) -> list[str]:
    """Splits on the commas that are not inside parentheses."""
    depth = 0
    current = ""
    tokens = []
    for ch in body:
        if ch == "(":
            depth += 1
        elif ch == ")":
            depth -= 1
        if ch == "," and depth == 0:
            tokens.append(current.strip())
            current = ""
        else:
            current += ch
    if current.strip():
        tokens.append(current.strip())
    return [t for t in tokens if t]


def make_macros(selectors: dict[str, int]) -> dict:
    """The SIG_* and PATCH_* macros as Python functions producing tuples of uint16 words."""

    def uint16(value: int) -> tuple[int, ...]:
        return (SIG_CODE_UINT16 | (value & 0xFF), value >> 8)

    def add_to_offset(offset: int) -> tuple[int, ...]:
        return (SIG_CODE_ADDTOOFFSET | offset,)

    def get_original_bytes(offset: int, length: int) -> tuple[int, ...]:
        return (PATCH_CODE_GETORIGINALBYTES | offset, length & 0xFFFF)

    def get_original_byte(offset: int, adjust: int = 0) -> tuple[int, ...]:
        return (PATCH_CODE_GETORIGINALBYTE | offset, adjust & 0xFFFF)

    def get_original_uint16(offset: int, adjust: int = 0) -> tuple[int, ...]:
        return (PATCH_CODE_GETORIGINALUINT16 | offset, adjust & 0xFFFF)

    env = {
        "__builtins__": {},
        "SIG_END": (SIG_END,),
        "PATCH_END": (SIG_END,),
        "SIG_MAGICDWORD": (SIG_MAGICDWORD,),
        "SIG_UINT16": uint16,
        "PATCH_UINT16": uint16,
        "SIG_ADDTOOFFSET": add_to_offset,
        "PATCH_ADDTOOFFSET": add_to_offset,
        "PATCH_GETORIGINALBYTES": get_original_bytes,
        "PATCH_GETORIGINALBYTE": get_original_byte,
        "PATCH_GETORIGINALBYTEADJUST": get_original_byte,
        "PATCH_GETORIGINALUINT16": get_original_uint16,
        "PATCH_GETORIGINALUINT16ADJUST": get_original_uint16,
    }
    env.update(header_constants())
    return env


SELECTOR_RE = re.compile(r"\b(SIG|PATCH)_SELECTOR(8|16)\s*\(\s*(\w+)\s*\)")


def parse_words(
    name: str, body: str, selectors: dict[str, int], env: dict
) -> list[int]:
    words: list[int] = []
    for token in split_top_level(body):
        # selectors are bare identifiers, some of which Python cannot evaluate (new, ...)
        def selector(m: re.Match) -> str:
            code = SIG_CODE_SELECTOR8 if m.group(2) == "8" else SIG_CODE_SELECTOR16
            if m.group(3) not in selectors:
                raise SystemExit(f"{name}: unknown selector {m.group(3)}")
            return str(code | selectors[m.group(3)])

        expression = SELECTOR_RE.sub(selector, token)
        # C integer division, and octal literals such as 0005
        expression = expression.replace("/", "//")
        expression = re.sub(r"\b0([0-7]+)\b", r"0o\1", expression)
        try:
            value = eval(expression, env)
        except Exception as ex:
            raise SystemExit(f"{name}: cannot evaluate '{token}': {ex}") from ex
        for word in value if isinstance(value, tuple) else (value,):
            if not 0 <= word <= 0xFFFF:
                raise SystemExit(f"{name}: word {word} out of range in '{token}'")
            words.append(word)
    if not words or words[-1] != SIG_END:
        raise SystemExit(f"{name}: not terminated")
    return words


def main() -> None:
    text = SOURCE.read_text(encoding="utf-8")
    code = strip_comments(text)

    # the selector name table and the enum that indexes it
    table_match = re.search(
        r"selectorNameTable\[\]\s*=\s*\{(.*?)\};", code, flags=re.DOTALL
    )
    if table_match is None:
        raise SystemExit("selectorNameTable not found")
    table_body = table_match.group(1)
    selector_names = re.findall(r'"([^"]+)"', table_body)
    enum_match = re.search(
        r"enum ScriptPatcherSelectors\s*\{(.*?)\};", code, flags=re.DOTALL
    )
    if enum_match is None:
        raise SystemExit("ScriptPatcherSelectors not found")
    enum_body = enum_match.group(1)
    enum_names = re.findall(r"SELECTOR_(\w+)", enum_body)
    if enum_names != selector_names:
        raise SystemExit("selectorNameTable and ScriptPatcherSelectors differ")
    selectors = {name: i for i, name in enumerate(selector_names)}
    env = make_macros(selectors)

    arrays: dict[str, list[int]] = {}
    for match in re.finditer(
        r"static const uint16 (\w+)\[\]\s*=\s*\{(.*?)\};", code, flags=re.DOTALL
    ):
        arrays[match.group(1)] = parse_words(
            match.group(1), match.group(2), selectors, env
        )

    entry_re = re.compile(
        r"\{\s*(true|false)\s*,\s*(\d+)\s*,\s*\"((?:[^\"\\]|\\.)*)\"\s*,\s*(-?\d+)\s*,\s*(\w+)\s*,\s*(\w+)\s*\}"
    )
    tables: dict[str, list[tuple]] = {}
    for match in re.finditer(
        r"static const SciScriptPatcherEntry (\w+)\[\]\s*=\s*\{(.*?)\n\};",
        code,
        flags=re.DOTALL,
    ):
        name = match.group(1)
        entries = []
        for row in entry_re.finditer(match.group(2)):
            active, script, description, count, sig, patch = row.groups()
            for array in (sig, patch):
                if array not in arrays:
                    raise SystemExit(f"{name}: unknown array {array}")
            entries.append((active, int(script), description, int(count), sig, patch))
        tables[name] = entries

    games = re.findall(r"case (GID_\w+):\s*signatureTable = (\w+);", code)
    for _, table in games:
        if table not in tables:
            raise SystemExit(f"unknown table {table} in processScript")

    used = {
        array for entries in tables.values() for e in entries for array in (e[4], e[5])
    }
    lines = [
        "// <auto-generated>",
        "// Generated by tools/PortTables/sci_script_patches.py from ScummVM's engines/sci/engine/script_patches.cpp.",
        "// Do not edit by hand; rerun the script after updating the ScummVM checkout.",
        "// </auto-generated>",
        "",
        "#nullable enable",
        "",
        "namespace Daventry.Sci.Engine;",
        "",
        "/// <summary>The script patches of ScummVM, game by game. See script_patches.cpp for why each exists.</summary>",
        "public static class ScriptPatchTables",
        "{",
        "    /// <summary>The selectors the signatures refer to, by the index the SELECTOR16 and SELECTOR8 commands carry.</summary>",
        "    public static readonly string[] SelectorNames =",
        "    [",
    ]
    lines.extend(f'        "{name}",' for name in selector_names)
    lines.append("    ];")
    lines.append("")

    for name, words in arrays.items():
        if name not in used:
            continue
        lines.append(
            f"    private static readonly ushort[] {name} = [{', '.join(f'0x{w:04X}' for w in words)}];"
        )
        lines.append("")

    for name, entries in tables.items():
        lines.append(f"    private static readonly ScriptPatchEntry[] {name} =")
        lines.append("    [")
        for active, script, description, count, sig, patch in entries:
            lines.append(
                f'        new({active}, {script}, "{description}", {count}, {sig}, {patch}),'
            )
        lines.append("    ];")
        lines.append("")

    lines.append(
        "    /// <summary>The patch table of a game, or null when ScummVM has none for it.</summary>"
    )
    lines.append(
        "    public static ScriptPatchEntry[]? ForGame(SciGameId gameId) => gameId switch"
    )
    lines.append("    {")
    for gid, table in games:
        lines.append(f"        {game_id(gid)} => {table},")
    lines.append("        _ => null,")
    lines.append("    };")
    lines.append("}")
    lines.append("")
    OUTPUT.write_text("\n".join(lines), encoding="utf-8", newline="\n")
    unused = len(arrays) - len(used)
    print(
        f"wrote {OUTPUT} with {len(tables)} tables, {sum(len(e) for e in tables.values())} entries, "
        f"{len(used)} arrays ({unused} unused skipped), {len(games)} games, {len(selector_names)} selectors"
    )


if __name__ == "__main__":
    main()
