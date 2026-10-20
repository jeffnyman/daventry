"""Converts ScummVM's engines/agi/detection_tables.h into Daventry's AgiGameTable.g.cs.

The macros from the header are mirrored one to one as Python functions, so an entry is expanded
exactly as the C preprocessor would expand it. Run from anywhere:

    python tools/PortTables/agi_detection.py [path/to/scummvm] [path/to/daventry]
"""

import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCUMMVM = Path(sys.argv[1]) if len(sys.argv) > 1 else HERE.parents[2] / "scummvm"
DAVENTRY = Path(sys.argv[2]) if len(sys.argv) > 2 else HERE.parents[1]

SOURCE = SCUMMVM / "engines" / "agi" / "detection_tables.h"
DETECTION_CPP = SCUMMVM / "engines" / "agi" / "detection.cpp"
OUTPUT = DAVENTRY / "src" / "Daventry.Agi" / "Detection" / "AgiGameTable.g.cs"

AD_NO_SIZE = -1

LANGUAGES = {
    "EN_ANY": "en", "ES_ESP": "es", "ET_EST": "et", "FR_FRA": "fr", "HE_ISR": "he",
    "IT_ITA": "it", "NL_NLD": "nl", "PL_POL": "pl", "RU_RUS": "ru", "DE_DEU": "de",
    "UNK_LANG": None,
}

PLATFORMS = {
    "kPlatformDOS": "Dos", "kPlatformAmiga": "Amiga", "kPlatformAtariST": "AtariSt",
    "kPlatformApple2": "Apple2", "kPlatformApple2GS": "Apple2Gs", "kPlatformMacintosh": "Macintosh",
    "kPlatformCoCo3": "CoCo3", "kPlatformCoCo": "CoCo", "kPlatformC64": "C64", "kPlatformWindows": "Windows",
}

GAME_TYPES = {
    "GType_PreAGI": "PreAgi", "GType_V1": "V1", "GType_V2": "V2", "GType_V3": "V3",
    "GType_A2": "Apple2", "GType_GAL": "Gal",
}

FEATURES = {
    "GF_AGIMOUSE": "AgiMouse", "GF_AGDS": "Agds", "GF_AGI256": "Agi256", "GF_FANMADE": "FanMade",
    "GF_2GSOLDSOUND": "Apple2GsOldSound",
}

ADGF = {
    "ADGF_NO_FLAGS": None, "ADGF_UNSTABLE": "Unstable", "ADGF_UNSUPPORTED": "Unsupported",
    "ADGF_PIRATED": "Pirated", "ADGF_USEEXTRAASTITLE": "UseExtraAsTitle", "ADGF_AUTOGENTARGET": None,
}


class Entry:
    def __init__(self, id, extra, files, lang, platform, flags, gid, interp, features, ver):
        self.id = id
        self.extra = extra
        self.files = files
        self.lang = lang
        self.platform = platform
        self.flags = flags
        self.gid = gid
        self.interp = interp
        self.features = features
        self.ver = ver


# ---- the macros, in the order the header defines them ----

def GAME_LVFPN(id, extra, fname, md5, size, lang, ver, features, gid, platform, interp, guioptions):
    return Entry(id, extra, [(fname, md5, size)], lang, platform, "ADGF_NO_FLAGS", gid, interp, features, ver)


def GAME_LVFPN2(id, extra, f1, m1, s1, f2, m2, s2, lang, ver, features, gid, platform, interp, guioptions):
    return Entry(id, extra, [(f1, m1, s1), (f2, m2, s2)], lang, platform, "ADGF_NO_FLAGS", gid, interp, features, ver)


def GAME_LVFPNF(id, name, fname, md5, size, lang, ver, features, gid, platform, interp, guioptions):
    return Entry(id, name, [(fname, md5, size)], lang, platform, "ADGF_USEEXTRAASTITLE", gid, interp, features, ver)


def GAME_LVFPN_FLAGS(id, msg, fname, md5, size, lang, ver, features, gid, platform, interp, guioptions, flags):
    return Entry(id, msg, [(fname, md5, size)], lang, platform, flags, gid, interp, features, ver)


def GAME_LVFPN2_FLAGS(id, msg, f1, m1, s1, f2, m2, s2, lang, ver, features, gid, platform, interp, guioptions, flags):
    return Entry(id, msg, [(f1, m1, s1), (f2, m2, s2)], lang, platform, flags, gid, interp, features, ver)


def A2(id, extra, md5, ver, gid):
    return GAME_LVFPN_FLAGS(id, extra, "*", md5, AD_NO_SIZE, "EN_ANY", ver, 0, gid, "kPlatformApple2", "GType_A2", None, "ADGF_UNSTABLE")


def A2_CP(id, extra, md5, ver, gid):
    return GAME_LVFPN_FLAGS(id, extra, "*", md5, AD_NO_SIZE, "EN_ANY", ver, 0, gid, "kPlatformApple2", "GType_A2", None, "ADGF_UNSTABLE")


def A2_GAL(id, extra, md5, ver, gid):
    return GAME_LVFPN_FLAGS(id, extra, "*", md5, AD_NO_SIZE, "EN_ANY", ver, 0, gid, "kPlatformApple2", "GType_GAL", None, "ADGF_UNSUPPORTED")


def BOOTER(id, extra, md5, ver, gid):
    return GAME_LVFPN(id, extra, "*", md5, AD_NO_SIZE, "EN_ANY", ver, 0, gid, "kPlatformDOS", "GType_V1", None)


def BOOTER_UNSTABLE(id, extra, md5, ver, gid):
    return GAME_LVFPN_FLAGS(id, extra, "*", md5, AD_NO_SIZE, "EN_ANY", ver, 0, gid, "kPlatformDOS", "GType_V1", None, "ADGF_UNSTABLE")


def BOOTER_GAL(id, extra, md5, ver, gid):
    return GAME_LVFPN_FLAGS(id, extra, "*", md5, AD_NO_SIZE, "EN_ANY", ver, 0, gid, "kPlatformDOS", "GType_GAL", None, "ADGF_UNSUPPORTED")


def GAME(id, extra, md5, ver, gid):
    return GAME_LVFPN(id, extra, "logdir", md5, AD_NO_SIZE, "EN_ANY", ver, 0, gid, "kPlatformDOS", "GType_V2", None)


def GAME3(id, extra, fname, md5, ver, gid):
    return GAME_LVFPN(id, extra, fname, md5, AD_NO_SIZE, "EN_ANY", ver, 0, gid, "kPlatformDOS", "GType_V3", None)


GAME3_CP = GAME3


def GAME3_PIRATED(id, extra, fname, md5, ver, gid):
    return GAME_LVFPN_FLAGS(id, extra, fname, md5, AD_NO_SIZE, "EN_ANY", ver, 0, gid, "kPlatformDOS", "GType_V3", None, "ADGF_PIRATED")


def GAME_P(id, extra, md5, ver, gid, platform):
    return GAME_LVFPN(id, extra, "logdir", md5, AD_NO_SIZE, "EN_ANY", ver, 0, gid, platform, "GType_V2", None)


GAME_P_CP = GAME_P


def GAME_PO(id, extra, md5, ver, gid, platform, guioptions):
    return GAME_P(id, extra, md5, ver, gid, platform)


def GAME_FP(id, extra, md5, ver, flags, gid, platform):
    return GAME_LVFPN(id, extra, "logdir", md5, AD_NO_SIZE, "EN_ANY", ver, flags, gid, platform, "GType_V2", None)


def GAME_FPO(id, extra, md5, ver, flags, gid, platform, guioptions):
    return GAME_FP(id, extra, md5, ver, flags, gid, platform)


def GAME_F(id, extra, md5, ver, flags, gid):
    return GAME_FP(id, extra, md5, ver, flags, gid, "kPlatformDOS")


def GAME_FO(id, extra, md5, ver, flags, gid, guioptions):
    return GAME_F(id, extra, md5, ver, flags, gid)


def GAME_FSO(id, extra, fname, md5, size, ver, flags, gid, guioptions):
    return GAME_LVFPN(id, extra, fname, md5, size, "EN_ANY", ver, flags, gid, "kPlatformDOS", "GType_V2", None)


def GAME_PS(id, extra, md5, size, ver, gid, platform):
    return GAME_LVFPN(id, extra, "logdir", md5, size, "EN_ANY", ver, 0, gid, platform, "GType_V2", None)


GAME_PS_CP = GAME_PS


def GAME_LPS(id, extra, md5, size, lang, ver, gid, platform):
    return GAME_LVFPN(id, extra, "logdir", md5, size, lang, ver, 0, gid, platform, "GType_V2", None)


def GAME_LFPS(id, extra, md5, size, lang, ver, flags, gid, platform):
    return GAME_LVFPN(id, extra, "logdir", md5, size, lang, ver, flags, gid, platform, "GType_V2", None)


def GAME3_P(id, extra, fname, md5, ver, flags, gid, platform):
    return GAME_LVFPN(id, extra, fname, md5, AD_NO_SIZE, "EN_ANY", ver, flags, gid, platform, "GType_V3", None)


GAME3_P_CP = GAME3_P


def GAME3_PO(id, extra, fname, md5, ver, flags, gid, platform, guioptions):
    return GAME3_P(id, extra, fname, md5, ver, flags, gid, platform)


def GAMEpre_P(id, extra, f1, m1, s1, f2, m2, s2, ver, gid, platform):
    return GAME_LVFPN2(id, extra, f1, m1, s1, f2, m2, s2, "EN_ANY", ver, 0, gid, platform, "GType_PreAGI", None)


def GAMEpre_PU(id, msg, f1, m1, s1, f2, m2, s2, ver, gid, platform):
    return GAME_LVFPN2_FLAGS(id, msg, f1, m1, s1, f2, m2, s2, "EN_ANY", ver, 0, gid, platform, "GType_PreAGI", None, "ADGF_UNSUPPORTED")


def GAMEpre_PO(id, extra, f1, m1, s1, f2, m2, s2, ver, gid, platform, guioptions):
    return GAMEpre_P(id, extra, f1, m1, s1, f2, m2, s2, ver, gid, platform)


def GAMEpre_PS(id, extra, fname, md5, size, ver, gid, platform):
    return GAME_LVFPN(id, extra, fname, md5, size, "EN_ANY", ver, 0, gid, platform, "GType_PreAGI", None)


def GAMEpre_PSU(id, msg, fname, md5, size, ver, gid, platform):
    return GAME_LVFPN_FLAGS(id, msg, fname, md5, size, "EN_ANY", ver, 0, gid, platform, "GType_PreAGI", None, "ADGF_UNSUPPORTED")


def GAME3_PS(id, extra, fname, md5, size, ver, flags, gid, platform):
    return GAME_LVFPN(id, extra, fname, md5, size, "EN_ANY", ver, flags, gid, platform, "GType_V3", None)


def GAME3_PSO(id, extra, fname, md5, size, ver, flags, gid, platform, guioptions):
    return GAME3_PS(id, extra, fname, md5, size, ver, flags, gid, platform)


def FANMADE_ILVFO(id, name, md5, lang, ver, features, guioptions):
    return GAME_LVFPNF(id, name, "logdir", md5, AD_NO_SIZE, lang, ver, ("GF_FANMADE", features), "GID_FANMADE", "kPlatformDOS", "GType_V2", None)


def FANMADE_ISVPO(id, name, md5, size, ver, platform, guioptions):
    return GAME_LVFPNF(id, name, "logdir", md5, size, "EN_ANY", ver, "GF_FANMADE", "GID_FANMADE", platform, "GType_V2", None)


def FANMADE_SVP(name, md5, size, ver, platform):
    return FANMADE_ISVPO("agi-fanmade", name, md5, size, ver, platform, None)


def FANMADE_LVFO(name, md5, lang, ver, features, guioptions):
    return FANMADE_ILVFO("agi-fanmade", name, md5, lang, ver, features, guioptions)


def FANMADE_LF(name, md5, lang, features):
    return FANMADE_LVFO(name, md5, lang, 0x2917, features, None)


def FANMADE_LFO(name, md5, lang, features, guioptions):
    return FANMADE_LVFO(name, md5, lang, 0x2917, features, guioptions)


def FANMADE_V(name, md5, ver):
    return FANMADE_LVFO(name, md5, "EN_ANY", ver, 0, None)


def FANMADE_F(name, md5, features):
    return FANMADE_LF(name, md5, "EN_ANY", features)


def FANMADE_FO(name, md5, features, guioptions):
    return FANMADE_LFO(name, md5, "EN_ANY", features, guioptions)


def FANMADE_L(name, md5, lang):
    return FANMADE_LF(name, md5, lang, 0)


def FANMADE_O(name, md5, guioptions):
    return FANMADE_FO(name, md5, 0, guioptions)


def FANMADE_P(name, md5, platform):
    return FANMADE_SVP(name, md5, AD_NO_SIZE, 0x2917, platform)


def FANMADE_S(name, fname, md5, size):
    return GAME_LVFPNF("agi-fanmade", name, fname, md5, size, "EN_ANY", 0x2917, "GF_FANMADE", "GID_FANMADE", "kPlatformDOS", "GType_V2", None)


def FANMADE(name, md5):
    return FANMADE_F(name, md5, 0)


MACROS = {name: obj for name, obj in globals().items() if callable(obj) and name[0].isupper() and name not in ("Entry",)}


# ---- parsing ----

def strip_comments(text):
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    return re.sub(r"//[^\n]*", "", text)


def split_args(text):
    """Splits a macro argument list on top-level commas, honouring string literals."""
    args, depth, current, in_string, i = [], 0, [], False, 0
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
        elif c == "(":
            depth += 1
            current.append(c)
        elif c == ")":
            depth -= 1
            current.append(c)
        elif c == "," and depth == 0:
            args.append("".join(current).strip())
            current = []
        else:
            current.append(c)
        i += 1
    if "".join(current).strip():
        args.append("".join(current).strip())
    return args


def c_string(token):
    assert token.startswith('"') and token.endswith('"'), token
    body = token[1:-1]
    return body.replace('\\"', '"').replace("\\'", "'").replace("\\\\", "\\")


def convert_arg(token):
    if token.startswith('"'):
        return c_string(token)
    if token == "AD_NO_SIZE":
        return AD_NO_SIZE
    if token.startswith("Common::"):
        return token[len("Common::"):]
    if token.startswith("GAMEOPTIONS_"):
        return None
    if "|" in token:
        return tuple(t.strip() for t in token.split("|"))
    if re.fullmatch(r"0x[0-9a-fA-F]+|\d+", token):
        return int(token, 0)
    return token


def flatten_features(value):
    if value in (0, "0", None):
        return []
    if isinstance(value, tuple):
        result = []
        for v in value:
            result.extend(flatten_features(v))
        return result
    return [value]


def parse_entries(text):
    text = strip_comments(text)
    start = text.index("static const AGIGameDescription gameDescriptions[]")
    end = text.index("AD_TABLE_END_MARKER", start)
    body = text[start:end]
    entries = []
    for match in re.finditer(r"\b([A-Z][A-Za-z0-9_]*)\s*\(", body):
        name = match.group(1)
        if name not in MACROS:
            continue
        depth, i = 1, match.end()
        while depth:
            if body[i] == "(":
                depth += 1
            elif body[i] == ")":
                depth -= 1
            elif body[i] == '"':
                i += 1
                while body[i] != '"':
                    i += 2 if body[i] == "\\" else 1
            i += 1
        args = [convert_arg(a) for a in split_args(body[match.end():i - 1])]
        entries.append(MACROS[name](*args))
    return entries


def parse_titles(text):
    text = strip_comments(text)
    start = text.index("static const PlainGameDescriptor agiGames[]")
    end = text.index("};", start)
    return re.findall(r'\{"([^"]+)",\s*"((?:[^"\\]|\\.)*)"\}', text[start:end])


# ---- emitting ----

def cs(value):
    if value is None:
        return "null"
    return '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'


def gid_name(gid):
    name = gid[len("GID_"):].lower()
    return name[0].upper() + name[1:]


def emit(entries, titles):
    lines = [
        "// <auto-generated>",
        "// Generated by tools/PortTables/agi_detection.py from ScummVM's engines/agi/detection_tables.h.",
        "// Do not edit by hand; rerun the script after updating the ScummVM checkout.",
        "// </auto-generated>",
        "",
        "namespace Daventry.Agi.Detection;",
        "",
        "public static partial class AgiGameTable",
        "{",
        "    /// <summary>Titles by ScummVM game id, from detection.cpp's agiGames table.</summary>",
        "    public static readonly IReadOnlyDictionary<string, string> Titles = new Dictionary<string, string>",
        "    {",
    ]
    for game_id, title in titles:
        lines.append(f"        [{cs(game_id)}] = {cs(c_string(chr(34) + title + chr(34)))},")
    lines += [
        "    };",
        "",
        f"    /// <summary>The {len(entries)} known game variants, in ScummVM's order.</summary>",
        "    public static readonly AgiGameEntry[] Entries =",
        "    [",
    ]
    for e in entries:
        files = ", ".join(f"new({cs(f)}, {cs(m)}, {s})" for f, m, s in e.files)
        features = flatten_features(e.features)
        features_cs = " | ".join(f"AgiGameFeatures.{FEATURES[f]}" for f in features) or "AgiGameFeatures.None"
        flag = ADGF[e.flags]
        flags_cs = f"AgiDetectionFlags.{flag}" if flag else "AgiDetectionFlags.None"
        lines.append(
            f"        new({cs(e.id)}, {cs(e.extra)}, [{files}], {cs(LANGUAGES[e.lang])}, "
            f"AgiPlatform.{PLATFORMS[e.platform]}, {flags_cs}, AgiGameId.{gid_name(e.gid)}, "
            f"AgiGameType.{GAME_TYPES[e.interp]}, {features_cs}, 0x{e.ver:04X}),"
        )
    lines += ["    ];", "}", ""]
    return "\n".join(lines)


def fix_language_in_features(entries):
    """ScummVM passes Common::FR_FRA as the features of one FANMADE_F entry; move it to the language."""
    for e in entries:
        features = flatten_features(e.features)
        languages = [f for f in features if f in LANGUAGES]
        if languages:
            print(f"note: {e.extra!r} has language {languages[0]} in its features field, using it as the language")
            e.lang = languages[0]
            e.features = tuple(f for f in features if f not in LANGUAGES)


def main():
    entries = parse_entries(SOURCE.read_text(encoding="utf-8"))
    fix_language_in_features(entries)
    titles = parse_titles(DETECTION_CPP.read_text(encoding="utf-8"))
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(emit(entries, titles), encoding="utf-8", newline="\n")
    print(f"wrote {OUTPUT} with {len(entries)} entries and {len(titles)} titles")


if __name__ == "__main__":
    main()
