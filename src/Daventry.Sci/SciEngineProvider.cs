using Daventry.Core.Hosting;
using Daventry.Core.Storage;

namespace Daventry.Sci;

/// <summary>
/// Recognises SCI games by RESOURCE.MAP. The engine itself is not ported yet; this exists so the
/// player already has the two-engine shape.
/// </summary>
public sealed class SciEngineProvider : IEngineProvider
{
    public const string EngineId = "sci";

    public string Id => EngineId;

    public IReadOnlyList<DetectedGame> Detect(IGameContainer game)
    {
        if (!game.Exists("RESOURCE.MAP"))
        {
            return [];
        }

        bool hasVolume = game.Exists("RESOURCE.000") || game.Exists("RESOURCE.001");
        return hasVolume ? [new DetectedGame(EngineId, "sci-unknown", $"SCI game ({game.Name})", Platform: "dos")] : [];
    }

    public Engine Create(IHost host, IGameContainer game, DetectedGame detected)
        => throw new NotSupportedException("The SCI engine has not been ported yet.");
}
