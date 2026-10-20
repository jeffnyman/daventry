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

/// <summary>Original platform. Only the ones the AGI engine distinguishes.</summary>
public enum AgiPlatform
{
    Dos,
    Amiga,
    AtariSt,
    Apple2,
    Apple2Gs,
    Macintosh,
    CoCo3,
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

/// <summary>
/// A detected AGI game with the interpreter details the engine needs. <see cref="Version"/> is
/// the BCD-ish interpreter version ScummVM uses, for example 0x2917 for 2.917 and 0x3149 for 3.002.149.
/// </summary>
public sealed record AgiDetectedGame(
    string GameId,
    string Title,
    AgiGameType Type,
    ushort Version,
    AgiPlatform Platform = AgiPlatform.Dos,
    AgiGameFeatures Features = AgiGameFeatures.None,
    string? Extra = null,
    string? Language = null)
    : DetectedGame(AgiEngineProvider.EngineId, GameId, Title, Extra, Language, Platform.ToString().ToLowerInvariant());
