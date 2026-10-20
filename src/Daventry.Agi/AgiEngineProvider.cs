using Daventry.Agi.Resources;
using Daventry.Agi.Sound;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;

namespace Daventry.Agi;

/// <summary>
/// Recognises AGI games by their directory files. This is the structural check only; the
/// MD5-based identification from ScummVM's detection_tables.h comes later and will fill in the
/// real game id, title and interpreter version.
/// </summary>
public sealed class AgiEngineProvider : IEngineProvider
{
    public const string EngineId = "agi";

    /// <summary>Interpreter version assumed for v2 games until detection identifies the real one.</summary>
    public const ushort DefaultV2Version = 0x2917;

    /// <summary>Interpreter version assumed for v3 games until detection identifies the real one.</summary>
    public const ushort DefaultV3Version = 0x3149;

    public string Id => EngineId;

    public IReadOnlyList<DetectedGame> Detect(IGameContainer game)
    {
        if (!game.Exists(AgiLoader.ObjectsFile) || !game.Exists(AgiLoader.WordsFile))
        {
            return [];
        }

        // AGI v2: separate LOGDIR, PICDIR, VIEWDIR, SNDDIR and VOL.n files.
        if (game.Exists(AgiLoader.LogDir) && game.Exists(AgiLoader.ViewDir) && game.Exists(AgiLoader.PicDir) && game.Exists("vol.0"))
        {
            return [new AgiDetectedGame("agi-v2", $"AGI v2 game ({game.Name})", AgiGameType.V2, DefaultV2Version)];
        }

        // AGI v3: a single <prefix>DIR with <prefix>VOL.0, for example KQ4DIR and KQ4VOL.0.
        foreach (var name in game.EnumerateFiles())
        {
            if (!name.EndsWith(AgiLoader.DirSuffix, StringComparison.OrdinalIgnoreCase) || name.Length <= 3)
            {
                continue;
            }

            var prefix = name[..^3];
            if (game.Exists(prefix + "vol.0"))
            {
                return [new AgiDetectedGame("agi-v3", $"AGI v3 game ({prefix})", AgiGameType.V3, DefaultV3Version)];
            }
        }

        return [];
    }

    /// <summary>Synthesiser used for the engines this provider creates.</summary>
    public AgiSoundEmulation SoundEmulation { get; init; } = AgiSoundEmulation.Pcjr;

    public Engine Create(IHost host, IGameContainer game, DetectedGame detected)
    {
        if (detected is not AgiDetectedGame agiGame)
        {
            throw new ArgumentException("Not an AGI detection result.", nameof(detected));
        }

        return new AgiEngine(host, game, agiGame, soundEmulation: SoundEmulation);
    }
}
