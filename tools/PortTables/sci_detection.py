"""Converts ScummVM's engines/sci/detection_tables.h and the id tables of detection.cpp and
detection_internal.cpp into Daventry's SciGameTable.g.cs.

    python tools/PortTables/sci_detection.py [path/to/scummvm] [path/to/daventry]
"""

import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCUMMVM = Path(sys.argv[1]) if len(sys.argv) > 1 else HERE.parents[2] / "scummvm"
DAVENTRY = Path(sys.argv[2]) if len(sys.argv) > 2 else HERE.parents[1]

SCI = SCUMMVM / "engines" / "sci"
OUTPUT = DAVENTRY / "src" / "Daventry.Sci" / "Detection" / "SciGameTable.g.cs"

LANGUAGES = {
    "EN_ANY": "en", "EN_USA": "en", "EN_GRB": "en", "DE_DEU": "de", "FR_FRA": "fr", "ES_ESP": "es",
    "IT_ITA": "it", "RU_RUS": "ru", "HE_ISR": "he", "KO_KOR": "ko", "JA_JPN": "ja", "PL_POL": "pl",
    "PT_BRA": "pt", "NL_NLD": "nl", "HU_HUN": "hu", "ZH_CHN": "zh", "ZH_TWN": "zh", "CS_CZE": "cs",
    "SV_SWE": "sv", "DA_DNK": "da", "NB_NOR": "nb", "FI_FIN": "fi", "UNK_LANG": None,
}

PLATFORMS = {
    "kPlatformDOS": "Dos", "kPlatformWindows": "Windows", "kPlatformAmiga": "Amiga",
    "kPlatformAtariST": "AtariSt", "kPlatformMacintosh": "Macintosh", "kPlatformFMTowns": "FmTowns",
    "kPlatformPC98": "Pc98", "kPlatformLinux": "Linux",
}

FLAGS = {
    "ADGF_NO_FLAGS": None, "ADGF_ADDON": "Addon", "ADGF_AUTOGENTARGET": "AutoGenTarget",
    "ADGF_UNSTABLE": "Unstable", "ADGF_TESTING": "Testing", "ADGF_PIRATED": "Pirated",
    "ADGF_UNSUPPORTED": "Unsupported", "ADGF_WARNING": "Warning", "ADGF_ADDENGLISH": "AddEnglish",
    "ADGF_MACRESFORK": "MacResFork", "ADGF_USEEXTRAASTITLE": "UseExtraAsTitle",
    "ADGF_DROPLANGUAGE": "DropLanguage", "ADGF_DROPPLATFORM": "DropPlatform", "ADGF_CD": "Cd",
    "ADGF_DVD": "Dvd", "ADGF_DEMO": "Demo", "ADGF_REMASTERED": "Remastered",
}

VERSIONS = {
    "SCI_VERSION_NONE": "None", "SCI_VERSION_0_EARLY": "V0Early", "SCI_VERSION_0_LATE": "V0Late",
    "SCI_VERSION_01": "V01", "SCI_VERSION_1_EGA_ONLY": "V1EgaOnly", "SCI_VERSION_1_EARLY": "V1Early",
    "SCI_VERSION_1_MIDDLE": "V1Middle", "SCI_VERSION_1_LATE": "V1Late", "SCI_VERSION_1_1": "V1_1",
    "SCI_VERSION_2": "V2", "SCI_VERSION_2_1_EARLY": "V2_1Early", "SCI_VERSION_2_1_MIDDLE": "V2_1Middle",
    "SCI_VERSION_2_1_LATE": "V2_1Late", "SCI_VERSION_3": "V3",
}


def strip_comments(text):
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    text = re.sub(r"//[^\n]*", "", text)
    return strip_defines(text)


def strip_defines(text):
    """Removes #define lines, including backslash continuations; the table defines GUIO macros inline."""
    lines, skipping = [], False
    for line in text.split("\n"):
        if skipping or line.lstrip().startswith("#"):
            skipping = line.rstrip().endswith("\\")
            continue
        lines.append(line)
    return "\n".join(lines)


def c_string(token):
    assert token.startswith('"') and token.endswith('"'), token
    return token[1:-1].replace('\\"', '"').replace("\\'", "'").replace("\\\\", "\\")


def split_top_level(text, separator=","):
    """Splits on separators that are not inside braces, parentheses or strings."""
    parts, depth, current, in_string, i = [], 0, [], False, 0
    while i < len(text):
        c = text[i]
        if in_string:
            current.append(c)
            if c == "\\":
                current.append(text[i + 1])
                i += 1
            elif c == '"':
                in_string = False
        elif c == '"':
            in_string = True
            current.append(c)
        elif c in "{(":
            depth += 1
            current.append(c)
        elif c in "})":
            depth -= 1
            current.append(c)
        elif c == separator and depth == 0:
            parts.append("".join(current).strip())
            current = []
        else:
            current.append(c)
        i += 1
    tail = "".join(current).strip()
    if tail:
        parts.append(tail)
    return parts


def top_level_braces(text):
    """Yields the text inside each top-level {...} of an initializer list."""
    depth, start, in_string, i = 0, None, False, 0
    while i < len(text):
        c = text[i]
        if in_string:
            if c == "\\":
                i += 1
            elif c == '"':
                in_string = False
        elif c == '"':
            in_string = True
        elif c == "{":
            if depth == 0:
                start = i + 1
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                yield text[start:i]
        i += 1


class Entry:
    def __init__(self, game_id, extra, files, lang, platform, flags):
        self.game_id = game_id
        self.extra = extra
        self.files = files
        self.lang = lang
        self.platform = platform
        self.flags = flags


def parse_file(token):
    fields = split_top_level(token.strip())
    name = c_string(fields[0])
    md5 = None if fields[2] in ("NULL", "nullptr", "0") else c_string(fields[2])
    size = -1 if fields[3] == "AD_NO_SIZE" else int(fields[3])
    return (name, md5, size)


def parse_entry(body):
    fields = split_top_level(body)
    game_id = c_string(fields[0])
    extra_token = fields[1]
    if extra_token.startswith("_s("):
        extra_token = extra_token[3:-1].strip()  # translatable string marker
    extra = "GAME_NOT_IMPLEMENTED" if "GAME_NOT_IMPLEMENTED" in extra_token else c_string(extra_token)
    files = [parse_file(f) for f in top_level_braces(fields[2].strip()[1:-1]) if "AD_LISTEND" not in f and f.strip()]
    lang = fields[3].replace("Common::", "")
    platform = fields[4].replace("Common::", "")
    flags = [f.strip() for f in fields[5].split("|")]
    return Entry(game_id, extra, files, lang, platform, flags)


def expand_fanmade(call):
    name, args = re.match(r"(FANMADE\w*)\((.*)\)\s*$", call, re.S).groups()
    args = [a.strip() for a in split_top_level(args)]
    title = c_string(args[0])
    map_md5, map_size, res_md5, res_size = c_string(args[1]), int(args[2]), c_string(args[3]), int(args[4])
    if name == "FANMADE_L":
        volume, lang = c_string(args[5]), args[6].replace("Common::", "")
    elif name == "FANMADE11":
        volume, lang = "resource.000", "EN_ANY"
    else:
        volume, lang = "resource.001", "EN_ANY"
    return Entry("sci-fanmade", title, [("resource.map", map_md5, map_size), (volume, res_md5, res_size)],
                 lang, "kPlatformDOS", ["ADGF_USEEXTRAASTITLE", "ADGF_AUTOGENTARGET"])


def parse_entries(text):
    text = strip_comments(text)
    start = text.index("static const struct ADGameDescription SciGameDescriptions[] = {") + len("static const struct ADGameDescription SciGameDescriptions[] = {")
    end = text.index("AD_TABLE_END_MARKER", start)
    body = text[start:end]
    entries = []
    for item in split_top_level(body):
        item = item.strip()
        if not item:
            continue
        if item.startswith("FANMADE"):
            entries.append(expand_fanmade(item))
        elif item.startswith("{"):
            entries.append(parse_entry(item[1:item.rindex("}")]))
        else:
            raise ValueError("unexpected table item: " + item[:60])
    return entries


def parse_titles(text):
    text = strip_comments(text)
    start = text.index("static const PlainGameDescriptor s_sciGameTitles[]")
    end = text.index("};", start)
    return re.findall(r'\{"([^"]+)",\s*"((?:[^"\\]|\\.)*)"\}', text[start:end])


def parse_id_table(text):
    text = strip_comments(text)
    start = text.index("const GameIdStrToEnum gameIdStrToEnum[]")
    end = text.index("};", start)
    rows = []
    for m in re.finditer(r'\{\s*"([^"]*)",\s*"([^"]*)",\s*(GID_\w+),\s*(true|false),\s*(SCI_VERSION_\w+)\s*\}', text[start:end]):
        rows.append(m.groups())
    return rows


def gid_name(gid):
    """GID_KQ1 -> Kq1, GID_CNICK_KQ -> CnickKq, matching the SciGameId enum."""
    parts = gid[len("GID_"):].lower().split("_")
    return "".join(p[0].upper() + p[1:] for p in parts)


def cs(value):
    return "null" if value is None else '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'


def emit(entries, titles, id_rows):
    lines = [
        "// <auto-generated>",
        "// Generated by tools/PortTables/sci_detection.py from ScummVM's engines/sci/detection_tables.h,",
        "// detection.cpp and detection_internal.cpp. Do not edit by hand.",
        "// </auto-generated>",
        "",
        "namespace Daventry.Sci.Detection;",
        "",
        "public static partial class SciGameTable",
        "{",
        "    /// <summary>Titles by ScummVM game id.</summary>",
        "    public static readonly IReadOnlyDictionary<string, string> Titles = new Dictionary<string, string>",
        "    {",
    ]
    for game_id, title in titles:
        lines.append(f"        [{cs(game_id)}] = {cs(c_string(chr(34) + title + chr(34)))},")
    lines += [
        "    };",
        "",
        "    /// <summary>ScummVM game id, Sierra game object name, enum value, SCI32 flag and the version that disambiguates shared names.</summary>",
        "    public static readonly SciGameIdMapping[] GameIds =",
        "    [",
    ]
    for game_id, sierra, gid, sci32, version in id_rows:
        lines.append(f"        new({cs(game_id)}, {cs(sierra)}, SciGameId.{gid_name(gid)}, {sci32}, SciVersion.{VERSIONS[version]}),")
    lines += [
        "    ];",
        "",
        f"    /// <summary>The {len(entries)} known game variants, in ScummVM's order.</summary>",
        "    public static readonly SciGameEntry[] Entries =",
        "    [",
    ]
    for e in entries:
        files = ", ".join(f"new({cs(n)}, {cs(m)}, {s})" for n, m, s in e.files)
        flags = [FLAGS[f] for f in e.flags if FLAGS[f]]
        flags_cs = " | ".join(f"SciDetectionFlags.{f}" for f in flags) or "SciDetectionFlags.None"
        lines.append(
            f"        new({cs(e.game_id)}, {cs(e.extra)}, [{files}], {cs(LANGUAGES[e.lang])}, "
            f"SciPlatform.{PLATFORMS[e.platform]}, {flags_cs}),"
        )
    lines += ["    ];", "}", ""]
    return "\n".join(lines)


def parse_gids(text):
    text = strip_comments(text)
    start = text.index("enum SciGameId {")
    end = text.index("};", start)
    return re.findall(r"\b(GID_\w+)", text[start:end])


def emit_enum(gids):
    lines = [
        "// <auto-generated>",
        "// Generated by tools/PortTables/sci_detection.py from ScummVM's engines/sci/detection.h. Do not edit by hand.",
        "// </auto-generated>",
        "",
        "namespace Daventry.Sci;",
        "",
        "/// <summary>Which game is running, mirroring ScummVM's SciGameId. Per-game workarounds key on this.</summary>",
        "public enum SciGameId",
        "{",
    ]
    lines += [f"    {gid_name(g)}," for g in gids]
    lines += ["}", ""]
    return "\n".join(lines)


def main():
    gids = parse_gids((SCI / "detection.h").read_text(encoding="utf-8"))
    enum_output = DAVENTRY / "src" / "Daventry.Sci" / "SciGameId.g.cs"
    enum_output.write_text(emit_enum(gids), encoding="utf-8", newline="\n")
    print(f"wrote {enum_output} with {len(gids)} ids")
    entries = parse_entries((SCI / "detection_tables.h").read_text(encoding="utf-8"))
    titles = parse_titles((SCI / "detection.cpp").read_text(encoding="utf-8"))
    id_rows = parse_id_table((SCI / "detection_internal.cpp").read_text(encoding="utf-8"))
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(emit(entries, titles, id_rows), encoding="utf-8", newline="\n")
    print(f"wrote {OUTPUT} with {len(entries)} entries, {len(titles)} titles, {len(id_rows)} id mappings")


if __name__ == "__main__":
    main()
