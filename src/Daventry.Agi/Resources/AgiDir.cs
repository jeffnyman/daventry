namespace Daventry.Agi.Resources;

/// <summary>Mirrors ScummVM's RESOURCETYPE_* values.</summary>
public enum AgiResourceType
{
    Logic = 1,
    Sound = 2,
    View = 3,
    Picture = 4,
}

/// <summary>Mirrors ScummVM's RES_* flags on AgiDir.</summary>
[Flags]
public enum AgiResourceFlags : byte
{
    None = 0,
    Loaded = 0x01,
    Compressed = 0x40,

    /// <summary>Picture uses a nibble instead of a byte as the F0 and F2 parameters (AGI v3).</summary>
    PictureV3NibbleParm = 0x80,
}

/// <summary>One directory entry: where a resource lives in the volume files. Mirrors ScummVM's AgiDir.</summary>
public sealed class AgiDir
{
    /// <summary>Offset value meaning "no resource". Mirrors ScummVM's _EMPTY.</summary>
    public const uint Empty = 0xFFFFF;

    public byte Volume { get; set; } = 0xFF;

    public uint Offset { get; set; } = Empty;

    /// <summary>Uncompressed length, known once the resource has been read from its volume.</summary>
    public uint Length { get; set; }

    /// <summary>Compressed length, known once the resource has been read from its volume.</summary>
    public uint CompressedLength { get; set; }

    public AgiResourceFlags Flags { get; set; }

    public bool IsEmpty => Offset == Empty;

    public bool IsLoaded => (Flags & AgiResourceFlags.Loaded) != 0;

    public void Reset()
    {
        Volume = 0xFF;
        Offset = Empty;
        Length = 0;
        CompressedLength = 0;
        Flags = AgiResourceFlags.None;
    }
}

/// <summary>The four resource directories of an AGI game.</summary>
public sealed class AgiDirectory
{
    /// <summary>Mirrors ScummVM's MAX_DIRECTORY_ENTRIES.</summary>
    public const int MaxEntries = 256;

    public AgiDir[] Logic { get; } = Create();

    public AgiDir[] Picture { get; } = Create();

    public AgiDir[] View { get; } = Create();

    public AgiDir[] Sound { get; } = Create();

    public AgiDir[] this[AgiResourceType type] => type switch
    {
        AgiResourceType.Logic => Logic,
        AgiResourceType.Picture => Picture,
        AgiResourceType.View => View,
        AgiResourceType.Sound => Sound,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public int Count(AgiResourceType type) => this[type].Count(d => !d.IsEmpty);

    private static AgiDir[] Create()
    {
        var entries = new AgiDir[MaxEntries];
        for (int i = 0; i < entries.Length; i++)
        {
            entries[i] = new AgiDir();
        }

        return entries;
    }
}

public sealed class AgiResourceException(string message) : Exception(message);
