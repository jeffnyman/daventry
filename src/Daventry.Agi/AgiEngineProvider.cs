using Daventry.Core.Hosting;
using Daventry.Core.Storage;

namespace Daventry.Agi;

/// <summary>
/// Recognises AGI games by their directory files. This is the structural check only; the
/// MD5-based identification from ScummVM's detection_tables.h comes later.
/// </summary>
public sealed class AgiEngineProvider : IEngineProvider
{
    public const string EngineId = "agi";

    public string Id => EngineId;

    public IReadOnlyList<DetectedGame> Detect(IGameContainer game)
    {
        if (!game.Exists("OBJECT") || !game.Exists("WORDS.TOK"))
        {
            return [];
        }

        // AGI v2: separate LOGDIR, PICDIR, VIEWDIR, SNDDIR and VOL.n files.
        if (game.Exists("LOGDIR") && game.Exists("VIEWDIR") && game.Exists("PICDIR") && game.Exists("VOL.0"))
        {
            return [new DetectedGame(EngineId, "agi-v2", $"AGI v2 game ({game.Name})", Platform: "dos")];
        }

        // AGI v3: a single <prefix>DIR with <prefix>VOL.0, for example KQ4DIR and KQ4VOL.0.
        foreach (var name in game.EnumerateFiles())
        {
            if (!name.EndsWith("DIR", StringComparison.OrdinalIgnoreCase) || name.Length <= 3)
            {
                continue;
            }

            var prefix = name[..^3];
            if (game.Exists(prefix + "VOL.0"))
            {
                return [new DetectedGame(EngineId, "agi-v3", $"AGI v3 game ({prefix})", Platform: "dos")];
            }
        }

        return [];
    }

    public Engine Create(IHost host, IGameContainer game, DetectedGame detected)
        => new AgiEngine(host, game, detected);
}
