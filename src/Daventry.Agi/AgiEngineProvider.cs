using Daventry.Agi.Detection;
using Daventry.Agi.Sound;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;

namespace Daventry.Agi;

/// <summary>
/// Recognises AGI games with ScummVM's MD5 table, falling back to a directory-file check, and
/// creates the engine for them. Only AGI v2 and v3 games run; preAGI, booter and Apple II
/// variants are reported but refused.
/// </summary>
public sealed class AgiEngineProvider : IEngineProvider
{
    public const string EngineId = "agi";

    public string Id => EngineId;

    /// <summary>Synthesiser used for the engines this provider creates.</summary>
    public AgiSoundEmulation SoundEmulation { get; init; } = AgiSoundEmulation.Pcjr;

    public ILog Log { get; init; } = NullLog.Instance;

    public IReadOnlyList<DetectedGame> Detect(IGameContainer game) => new AgiDetector(game, Log).Detect();

    public Engine Create(IHost host, IGameContainer game, DetectedGame detected)
    {
        if (detected is not AgiDetectedGame agiGame)
        {
            throw new ArgumentException("Not an AGI detection result.", nameof(detected));
        }

        if (agiGame.Type is not (AgiGameType.V2 or AgiGameType.V3))
        {
            throw new NotSupportedException($"{agiGame.Title}: {agiGame.Type} games are not supported yet.");
        }

        if ((agiGame.Flags & AgiDetectionFlags.Pirated) != 0)
        {
            // ScummVM refuses these. Daventry runs them, since the data files are the user's own,
            // but cracked copies may have altered logic that the per-game workarounds do not expect.
            Log.Warning("agi.detect", $"{agiGame.Title} ({agiGame.Extra}): the data files match a known cracked copy. It may not play correctly.");
        }

        if ((agiGame.Flags & AgiDetectionFlags.Unsupported) != 0)
        {
            throw new NotSupportedException($"{agiGame.Title}: this variant is marked unsupported.");
        }

        return new AgiEngine(host, game, agiGame, soundEmulation: SoundEmulation);
    }
}
