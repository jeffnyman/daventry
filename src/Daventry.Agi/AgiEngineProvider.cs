using Daventry.Agi.Detection;
using Daventry.Agi.PreAgi;
using Daventry.Agi.Sound;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;

namespace Daventry.Agi;

/// <summary>
/// Recognises AGI games with ScummVM's MD5 table, falling back to a directory-file check, and
/// creates the engine for them. AGI v2 and v3 games run on the interpreter; the preAGI games
/// Mickey's Space Adventure, Winnie the Pooh and Troll's Tale have engines of their own. Booter
/// disk images and Apple II disk images are recognised, as ScummVM does, but refused.
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

        if ((agiGame.Flags & AgiDetectionFlags.Unsupported) != 0)
        {
            throw new NotSupportedException($"{agiGame.Title}: {agiGame.Extra ?? "this variant is marked unsupported."}");
        }

        if (agiGame.Type == AgiGameType.PreAgi)
        {
            return CreatePreAgi(host, game, agiGame);
        }

        if (agiGame.Type is not (AgiGameType.V2 or AgiGameType.V3))
        {
            throw new NotSupportedException(agiGame.Type switch
            {
                AgiGameType.V1 => $"{agiGame.Title}: PC booter disks (AGI v1) are not supported yet.",
                AgiGameType.Apple2 => $"{agiGame.Title}: the Apple II version is not supported yet.",
                _ => $"{agiGame.Title}: {agiGame.Type} games are not supported yet.",
            });
        }

        if ((agiGame.Flags & AgiDetectionFlags.Pirated) != 0)
        {
            // ScummVM refuses these. Daventry runs them, since the data files are the user's own,
            // but cracked copies may have altered logic that the per-game workarounds do not expect.
            Log.Warning("agi.detect", $"{agiGame.Title} ({agiGame.Extra}): the data files match a known cracked copy. It may not play correctly.");
        }

        return new AgiEngine(host, game, agiGame, soundEmulation: SoundEmulation);
    }

    private Engine CreatePreAgi(IHost host, IGameContainer game, AgiDetectedGame agiGame) => agiGame.Gid switch
    {
        AgiGameId.Mickey => new MickeyEngine(host, game, agiGame),
        AgiGameId.Troll => new TrollEngine(host, game, agiGame),
        AgiGameId.Winnie when agiGame.Platform is AgiPlatform.Dos or AgiPlatform.Amiga => new WinnieEngine(host, game, agiGame, SoundEmulation),
        AgiGameId.Winnie => throw new NotSupportedException($"{agiGame.Title}: the {agiGame.Platform} version is not supported yet."),
        _ => throw new NotSupportedException($"{agiGame.Title}: this preAGI game is not supported yet."),
    };
}
