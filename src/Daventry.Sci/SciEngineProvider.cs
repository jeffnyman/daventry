using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Daventry.Sci.Resources;

namespace Daventry.Sci;

/// <summary>A detected SCI game: the resource layer's findings until the MD5 table is ported.</summary>
public sealed record SciDetectedGame(
    string GameId,
    string Title,
    SciVersion Version,
    ViewType ViewType,
    string SierraId,
    string? Extra = null)
    : DetectedGame(SciEngineProvider.EngineId, GameId, Title, Extra, null, "dos");

/// <summary>
/// Recognises SCI games by their resource files and reports the interpreter generation the
/// resource manager detects. The engine itself is not ported yet, so <see cref="Create"/> refuses.
/// </summary>
public sealed class SciEngineProvider : IEngineProvider
{
    public const string EngineId = "sci";

    public string Id => EngineId;

    public ILog Log { get; init; } = NullLog.Instance;

    public IReadOnlyList<DetectedGame> Detect(IGameContainer game)
    {
        bool sci16 = game.Exists("resource.map") && (game.Exists("resource.000") || game.Exists("resource.001"));
        bool sci32 = game.Exists("resmap.000") && game.Exists("ressci.000");
        if (!sci16 && !sci32)
        {
            return [];
        }

        var resMan = new ResourceManager(game, Log);
        try
        {
            resMan.Init();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            Log.Warning("sci.detect", $"{game.Name}: {ex.Message}");
            return [];
        }

        if (resMan.Version == SciVersion.None)
        {
            return [];
        }

        string sierraId = string.Empty;
        try
        {
            sierraId = resMan.FindSierraGameId();
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            Log.Warning("sci.detect", $"{game.Name}: could not read the game object: {ex.Message}");
        }

        string gameId = sierraId.Length > 0 ? "sci-" + sierraId.ToLowerInvariant() : "sci-unknown";
        return [new SciDetectedGame(gameId, $"{(sierraId.Length > 0 ? sierraId : game.Name)} ({resMan.Version.Describe()})", resMan.Version, resMan.ViewType, sierraId)];
    }

    public Engine Create(IHost host, IGameContainer game, DetectedGame detected)
        => throw new NotSupportedException("The SCI engine has not been ported yet.");
}
