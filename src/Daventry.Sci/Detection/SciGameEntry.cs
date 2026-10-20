namespace Daventry.Sci.Detection;

/// <summary>Entry flags from ScummVM's ADGF_* set.</summary>
[Flags]
public enum SciDetectionFlags
{
    None = 0,
    Addon = 1 << 0,
    AutoGenTarget = 1 << 1,
    Unstable = 1 << 2,
    Testing = 1 << 3,
    Pirated = 1 << 4,
    Unsupported = 1 << 5,
    Warning = 1 << 6,
    AddEnglish = 1 << 7,

    /// <summary>The MD5 is of the Macintosh resource fork, which Daventry cannot compute.</summary>
    MacResFork = 1 << 8,
    UseExtraAsTitle = 1 << 9,
    DropLanguage = 1 << 10,
    DropPlatform = 1 << 11,
    Cd = 1 << 12,
    Dvd = 1 << 13,
    Demo = 1 << 14,
    Remastered = 1 << 15,
}

/// <summary>One file an entry checks: name, MD5 of the first 5000 bytes (null means existence only), size (-1 means any).</summary>
public readonly record struct SciDetectionFile(string Name, string? Md5, long Size)
{
    public const long AnySize = -1;
}

/// <summary>One row of ScummVM's SCI detection table.</summary>
public sealed record SciGameEntry(
    string GameId,
    string Extra,
    SciDetectionFile[] Files,
    string? Language,
    SciPlatform Platform,
    SciDetectionFlags Flags);

/// <summary>Maps a ScummVM game id to the Sierra game object name and the engine's enum. Port of GameIdStrToEnum.</summary>
public sealed record SciGameIdMapping(string GameId, string SierraId, SciGameId Gid, bool IsSci32, SciVersion Version);

public static partial class SciGameTable
{
    /// <summary>How many bytes of each file ScummVM hashes.</summary>
    public const int Md5Bytes = 5000;

    public static string TitleFor(SciGameEntry entry)
    {
        if ((entry.Flags & SciDetectionFlags.UseExtraAsTitle) != 0)
        {
            return entry.Extra;
        }

        return TitleFor(entry.GameId);
    }

    public static string TitleFor(string gameId) => Titles.TryGetValue(gameId, out var title) ? title : gameId;

    public static SciGameId GidFor(string gameId)
    {
        foreach (var mapping in GameIds)
        {
            if (mapping.GameId == gameId)
            {
                return mapping.Gid;
            }
        }

        return SciGameId.Fanmade;
    }
}
