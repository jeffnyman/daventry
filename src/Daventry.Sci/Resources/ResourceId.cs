namespace Daventry.Sci.Resources;

/// <summary>Resource types. The numeric values match the type nibbles in resource maps and volumes (SCI0 to SCI2 numbering).</summary>
public enum ResourceType
{
    View = 0,
    Pic,
    Script,
    Text,
    Sound,
    Memory,
    Vocab,
    Font,
    Cursor,
    Patch,
    Bitmap,
    Palette,
    CdAudio = 12,
    Audio,
    Sync,
    Message,
    Map,
    Heap,
    Audio36,
    Sync36,
    Translation,

    // SCI2.1 and later
    Robot,
    Vmd,
    Chunk,
    Animation,

    // SCI3
    Etc,
    Duck,
    Clut,
    Tga,
    Zzz,

    // Macintosh only
    MacIconBarPictN,
    MacIconBarPictS,
    MacPict,
    Rave,
    Invalid,
}

/// <summary>Layout of resource.map and the volume files. Mirrors ScummVM's ResVersion.</summary>
public enum ResVersion
{
    Unknown,
    Sci0Sci1Early,
    Sci1Middle,
    Kq5FmTowns,
    Sci1Late,
    Sci11,
    Sci11Mac,
    Sci2,
    Sci3,
}

public enum ResourceCompression
{
    Unknown = -1,
    None = 0,

    /// <summary>LZW used in SCI0.</summary>
    Lzw,

    /// <summary>Huffman used for vector pictures.</summary>
    Huffman,

    /// <summary>LZW used in SCI01 and SCI1.</summary>
    Lzw1,

    /// <summary>LZW plus view post-processing.</summary>
    Lzw1View,

    /// <summary>LZW plus picture post-processing.</summary>
    Lzw1Pic,

    /// <summary>DCL implode used in SCI1.1.</summary>
    Dcl,

    /// <summary>STACpack (LZS) used in SCI32.</summary>
    StacPack,
}

/// <summary>Graphics flavour of the game's views. Mirrors ScummVM's ViewType.</summary>
public enum ViewType
{
    Unknown,

    /// <summary>EGA SCI0 and SCI1, and Amiga SCI0 and SCI1 ECS with 16 colours.</summary>
    Ega,

    /// <summary>Amiga SCI1 ECS with 32 colours.</summary>
    Amiga,

    /// <summary>Amiga SCI1 AGA with 64 colours (Longbow).</summary>
    Amiga64,

    /// <summary>VGA SCI1 with 256 colours.</summary>
    Vga,

    /// <summary>VGA SCI1.1 and newer with 256 colours.</summary>
    Vga11,
}

/// <summary>Identifies one resource: type and number, plus the noun/verb/cond/seq tuple for audio36 and sync36.</summary>
public readonly record struct ResourceId(ResourceType Type, ushort Number, uint Tuple = 0)
{
    public ResourceId(ResourceType type, ushort number, byte noun, byte verb, byte cond, byte seq)
        : this(type, number, ((uint)noun << 24) | ((uint)verb << 16) | ((uint)cond << 8) | seq)
    {
    }

    public static readonly string[] TypeNames =
    [
        "view", "pic", "script", "text", "sound",
        "memory", "vocab", "font", "cursor",
        "patch", "bitmap", "palette", "cdaudio",
        "audio", "sync", "message", "map", "heap",
        "audio36", "sync36", "xlate", "robot", "vmd",
        "chunk", "animation", "etc", "duck", "clut",
        "tga", "zzz", "macibin", "macibis", "macpict",
        "rave",
    ];

    /// <summary>File-name suffixes of SCI1 and later patch files; the SCI3 script suffix is csc rather than scr.</summary>
    public static readonly string[] TypeSuffixes =
    [
        "v56", "p56", "scr", "tex", "snd",
        "", "voc", "fon", "cur", "pat",
        "bit", "pal", "cda", "aud", "syn",
        "msg", "map", "hep", "", "",
        "trn", "rbt", "vmd", "chk", "",
        "etc", "duk", "clu", "tga", "zzz",
        "", "", "", "",
    ];

    public static string TypeName(ResourceType type) => (int)type < TypeNames.Length ? TypeNames[(int)type] : "invalid";

    public static string TypeSuffix(ResourceType type) => (int)type < TypeSuffixes.Length ? TypeSuffixes[(int)type] : string.Empty;

    public override string ToString()
    {
        string text = $"{TypeName(Type)}.{Number}";
        if (Tuple != 0)
        {
            text += $"({Tuple >> 24}, {(Tuple >> 16) & 0xFF}, {(Tuple >> 8) & 0xFF}, {Tuple & 0xFF})";
        }

        return text;
    }

    /// <summary>The base36 file name of an audio36 or sync36 patch ("@0CS0M00.0X1" style).</summary>
    public string ToPatchNameBase36(bool sci32)
    {
        char prefix = sci32
            ? (Type == ResourceType.Audio36 ? 'A' : 'S')
            : (Type == ResourceType.Audio36 ? '@' : '#');
        return prefix
            + IntToBase36(Number, 3)
            + IntToBase36(Tuple >> 24, 2)
            + IntToBase36((Tuple >> 16) & 0xFF, 2)
            + "."
            + IntToBase36((Tuple >> 8) & 0xFF, 2)
            + IntToBase36(Tuple & 0xFF, 1);
    }

    private static string IntToBase36(uint number, int minChars)
    {
        var chars = new char[minChars];
        for (int i = minChars - 1; i >= 0; i--)
        {
            uint digit = number % 36;
            chars[i] = (char)(digit < 10 ? '0' + digit : 'A' + digit - 10);
            number /= 36;
        }

        return new string(chars);
    }

    /// <summary>Parses a base36 patch name: type character, 3 digits of number, 2 of noun, 2 of verb, '.', 2 of cond, 1 of seq.</summary>
    public static ResourceId FromPatchNameBase36(ResourceType type, string fileName)
    {
        ushort number = (ushort)ParseBase36(fileName, 1, 3);
        byte noun = (byte)ParseBase36(fileName, 4, 2);
        byte verb = (byte)ParseBase36(fileName, 6, 2);
        byte cond = (byte)ParseBase36(fileName, 9, 2);
        byte seq = (byte)ParseBase36(fileName, 11, 1);
        return new ResourceId(type, number, noun, verb, cond, seq);
    }

    private static uint ParseBase36(string text, int start, int length)
    {
        uint value = 0;
        for (int i = start; i < start + length && i < text.Length; i++)
        {
            char c = char.ToUpperInvariant(text[i]);
            uint digit = c is >= '0' and <= '9' ? (uint)(c - '0') : c is >= 'A' and <= 'Z' ? (uint)(c - 'A' + 10) : 0;
            value = value * 36 + digit;
        }

        return value;
    }
}
