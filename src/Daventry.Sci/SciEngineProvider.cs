using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Daventry.Sci.Detection;
using Daventry.Sci.Resources;

namespace Daventry.Sci;

/// <summary>
/// A detected SCI game. <see cref="DetectedGame.GameId"/> is ScummVM's id ("kq4sci", "sci-fanmade");
/// <see cref="Gid"/> is the enum the engine's per-game code tests.
/// </summary>
public sealed record SciDetectedGame(
    string GameId,
    string Title,
    SciGameId Gid,
    SciVersion Version,
    ViewType ViewType,
    string SierraId,
    SciPlatform Platform = SciPlatform.Dos,
    string? Extra = null,
    string? Language = null,
    SciDetectionFlags Flags = SciDetectionFlags.None)
    : DetectedGame(SciEngineProvider.EngineId, GameId, Title, Extra, Language, Platform.ToString().ToLowerInvariant())
{
    /// <summary>True when matched by the MD5 table rather than by the game object name.</summary>
    public bool Identified { get; init; }

    /// <summary>MD5 of the entry's first file, for saved games. Null for fallback matches.</summary>
    public string? Md5 { get; init; }

    public bool IsCd => (Flags & SciDetectionFlags.Cd) != 0;

    public bool IsDemo => (Flags & SciDetectionFlags.Demo) != 0;
}

/// <summary>
/// Recognises SCI games with ScummVM's MD5 table and resource-based fallback, and creates the
/// engine. The interpreter is not ported yet: the engine only runs as a graphics viewer.
/// </summary>
public sealed class SciEngineProvider : IEngineProvider
{
    public const string EngineId = "sci";

    public string Id => EngineId;

    public ILog Log { get; init; } = NullLog.Instance;

    /// <summary>
    /// Plays the digital sample of a sound effect when the game ships one next to the synthesized
    /// version, as a Sound Blaster did; off, only the AdLib version plays. ScummVM's default is on.
    /// </summary>
    public bool PreferDigitalSfx { get; init; } = true;

    /// <summary>The sound hardware the music plays through: AdLib (the default), PCjr/Tandy, the PC speaker, General MIDI or the MT-32.</summary>
    public Sound.SciSoundDevice SoundDevice { get; init; } = Sound.SciSoundDevice.AdLib;

    /// <summary>The SoundFont for <see cref="Sound.SciSoundDevice.GeneralMidi"/>; without one, General MIDI falls back to AdLib.</summary>
    public Audio.SoundFontBank? SoundFont { get; init; }

    /// <summary>How much work the General MIDI synthesiser does: full quality by default, <see cref="Audio.GeneralMidiOptions.Light"/> for a browser.</summary>
    public Audio.GeneralMidiOptions? GeneralMidiOptions { get; init; }

    /// <summary>The ROMs for <see cref="Sound.SciSoundDevice.Mt32"/>; without them, the MT-32 falls back to AdLib.</summary>
    public Audio.Mt32.Mt32RomSet? Mt32Roms { get; init; }

    public IReadOnlyList<DetectedGame> Detect(IGameContainer game) => new SciDetector(game, Log).Detect();

    public Core.Hosting.Engine Create(IHost host, IGameContainer game, DetectedGame detected)
    {
        if (detected is not SciDetectedGame sciGame)
        {
            throw new ArgumentException("Not an SCI detection result.", nameof(detected));
        }

        // the Windows releases run with what the port has of them (kPlatform, the INI profile, the
        // kernel signatures); detection puts a DOS match first when a CD carries both
        if (sciGame.Platform is not (SciPlatform.Dos or SciPlatform.Windows))
        {
            throw new NotSupportedException($"{sciGame.Title}: only the DOS and Windows versions are supported so far.");
        }

        return new SciEngine(host, game, sciGame, PreferDigitalSfx, SoundDevice, SoundFont, GeneralMidiOptions, Mt32Roms);
    }
}
