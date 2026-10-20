using Daventry.Core.Hosting;

namespace Daventry.Agi;

/// <summary>Interpreter family. Mirrors ScummVM's AgiGameType.</summary>
public enum AgiGameType
{
    PreAgi = 0,
    V1 = 1,
    V2 = 2,
    V3 = 3,
    Apple2 = 4,
    Gal = 5,
}

/// <summary>Original platform, as ScummVM's detection table records it.</summary>
public enum AgiPlatform
{
    Dos,
    Amiga,
    AtariSt,
    Apple2,
    Apple2Gs,
    Macintosh,
    CoCo3,
    CoCo,
    C64,
    Windows,
}

/// <summary>Mirrors ScummVM's AgiGameFeatures.</summary>
[Flags]
public enum AgiGameFeatures
{
    None = 0,
    AgiMouse = 1 << 0,
    Agds = 1 << 1,
    Agi256 = 1 << 2,
    FanMade = 1 << 3,
    Apple2GsOldSound = 1 << 5,
}

/// <summary>Which game a detection entry is, mirroring ScummVM's GID_* values. Per-game workarounds key on this.</summary>
public enum AgiGameId
{
    Agidemo,
    Bc,
    Ddp,
    Goldrush,
    Kq1,
    Kq2,
    Kq3,
    Kq4,
    Lsl1,
    Mh1,
    Mh2,
    Mixedup,
    Pq1,
    Sq1,
    Sq2,
    Xmascard,
    Fanmade,
    Getouttasq,
    Mickey,
    Winnie,
    Troll,
}

/// <summary>Detection entry flags that matter to Daventry, from ScummVM's ADGF_* set.</summary>
[Flags]
public enum AgiDetectionFlags
{
    None = 0,

    /// <summary>ScummVM marks this variant as not fully working.</summary>
    Unstable = 1 << 0,

    /// <summary>ScummVM refuses to run this variant.</summary>
    Unsupported = 1 << 1,

    /// <summary>A known cracked copy, which ScummVM refuses to run.</summary>
    Pirated = 1 << 2,

    /// <summary>The entry's extra text is the game title (fan games).</summary>
    UseExtraAsTitle = 1 << 3,
}

/// <summary>
/// A detected AGI game with the interpreter details the engine needs. <see cref="Version"/> is
/// the BCD-ish interpreter version ScummVM uses, for example 0x2917 for 2.917 and 0x3149 for 3.002.149.
/// <see cref="GameId"/> is ScummVM's target id ("kq1", "agi-fanmade"); <see cref="Gid"/> is the
/// enum the engine's per-game workarounds test.
/// </summary>
public sealed record AgiDetectedGame(
    string GameId,
    string Title,
    AgiGameType Type,
    ushort Version,
    AgiPlatform Platform = AgiPlatform.Dos,
    AgiGameFeatures Features = AgiGameFeatures.None,
    string? Extra = null,
    string? Language = null,
    AgiGameId Gid = AgiGameId.Fanmade,
    AgiDetectionFlags Flags = AgiDetectionFlags.None)
    : DetectedGame(AgiEngineProvider.EngineId, GameId, Title, Extra, Language, Platform.ToString().ToLowerInvariant())
{
    /// <summary>True when identified by MD5 rather than by the fallback file-name check.</summary>
    public bool Identified { get; init; }

    /// <summary>MD5 of the directory file that identified the game, stored in saved games. Null for fallback matches.</summary>
    public string? Md5 { get; init; }
}
