using Daventry.Adl.Detection;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;

namespace Daventry.Adl;

/// <summary>
/// Recognises the Hi-Res Adventures and creates their engines. Port of ScummVM's ADL metaengine.
/// </summary>
public sealed class AdlEngineProvider : IEngineProvider
{
    public const string EngineId = "adl";

    public string Id => EngineId;

    public ILog Log { get; init; } = NullLog.Instance;

    /// <summary>Colour graphics; null takes each game's default (monochrome for Mystery House, colour for the rest).</summary>
    public bool? Color { get; init; }

    /// <summary>Darken every other line, as on a CRT.</summary>
    public bool Scanlines { get; init; }

    /// <summary>Draw the text window in white even in colour mode, ScummVM's default.</summary>
    public bool MonoText { get; init; } = true;

    /// <summary>The checkered cursor of later Apple II models.</summary>
    public bool Apple2eCursor { get; init; }

    public IReadOnlyList<DetectedGame> Detect(IGameContainer game) => new AdlDetector(game, Log).Detect();

    public Engine Create(IHost host, IGameContainer game, DetectedGame detected)
    {
        if (detected is not AdlDetectedGame adl)
        {
            throw new ArgumentException("Not an ADL detection result.", nameof(detected));
        }

        if (adl.Unverified)
        {
            Log.Warning("adl.detect", $"{adl.Title}: the disk images are not in ScummVM's table. The game may not play correctly.");
        }

        var description = adl.Description;
        var options = new DisplayOptions(Color ?? description.ColorByDefault, Scanlines, MonoText, Apple2eCursor);

        return description.GameType switch
        {
            GameType.Hires0 => new HiResBaseEngine(host, game, adl, options),
            GameType.Hires1 when description.Version == GameVersion.Hr1Vf2 => new HiRes1EngineVf(host, game, adl, options),
            GameType.Hires1 => new HiRes1Engine(host, game, adl, options),
            GameType.Hires2 => new HiRes2Engine(host, game, adl, options),
            GameType.Hires3 => new HiRes3Engine(host, game, adl, options),
            GameType.Hires4 when description.Platform == AdlPlatform.Atari8Bit => new HiRes4EngineAtari(host, game, adl, options),
            GameType.Hires4 when description.Version == GameVersion.Hr4Lng => new HiRes4EngineLng(host, game, adl, options),
            GameType.Hires4 when description.Version == GameVersion.Hr4V11 => new HiRes4EngineV11(host, game, adl, options),
            GameType.Hires4 => new HiRes4EngineV10(host, game, adl, options),
            GameType.Hires5 => new HiRes5Engine(host, game, adl, options),
            GameType.Hires6 => new HiRes6Engine(host, game, adl, options),
            _ => throw new NotSupportedException($"{adl.Title}: unsupported game type {description.GameType}."),
        };
    }
}
