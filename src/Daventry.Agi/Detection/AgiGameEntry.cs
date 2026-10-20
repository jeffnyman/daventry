namespace Daventry.Agi.Detection;

/// <summary>One file a detection entry checks: name, MD5 of its first 5000 bytes, and size or -1 for any.</summary>
public readonly record struct AgiDetectionFile(string Name, string Md5, int Size)
{
    public const int AnySize = -1;

    /// <summary>Disk-image entries use "*" as the file name; those need the booter loader, which is not ported.</summary>
    public bool IsDiskImage => Name == "*";
}

/// <summary>One row of ScummVM's AGI detection table.</summary>
public sealed record AgiGameEntry(
    string GameId,
    string Extra,
    AgiDetectionFile[] Files,
    string? Language,
    AgiPlatform Platform,
    AgiDetectionFlags Flags,
    AgiGameId Gid,
    AgiGameType Type,
    AgiGameFeatures Features,
    ushort Version);

/// <summary>The detection table. The entries live in the generated AgiGameTable.g.cs.</summary>
public static partial class AgiGameTable
{
    /// <summary>How many bytes of each file ScummVM hashes.</summary>
    public const int Md5Bytes = 5000;

    /// <summary>The title ScummVM shows for an entry.</summary>
    public static string TitleFor(AgiGameEntry entry)
    {
        if ((entry.Flags & AgiDetectionFlags.UseExtraAsTitle) != 0)
        {
            return entry.Extra;
        }

        return Titles.TryGetValue(entry.GameId, out var title) ? title : entry.GameId;
    }
}
