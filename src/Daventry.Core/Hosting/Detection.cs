using Daventry.Core.Storage;

namespace Daventry.Core.Hosting;

/// <summary>A game an engine provider recognised inside a container.</summary>
/// <param name="EngineId">Which engine runs it, for example "agi" or "sci".</param>
/// <param name="GameId">Stable short identifier, for example "kq1".</param>
/// <param name="Title">Display title.</param>
/// <param name="Extra">Version or release notes, such as "2.0F 1987-05-05".</param>
/// <param name="Language">ISO language code if known.</param>
/// <param name="PlatformName">Original platform if known, such as "dos" or "amiga".</param>
public record DetectedGame(
    string EngineId,
    string GameId,
    string Title,
    string? Extra = null,
    string? Language = null,
    string? PlatformName = null)
{
    public sealed override string ToString()
        => string.IsNullOrEmpty(Extra) ? $"{Title} [{EngineId}:{GameId}]" : $"{Title} ({Extra}) [{EngineId}:{GameId}]";
}

/// <summary>Detects and instantiates one engine family.</summary>
public interface IEngineProvider
{
    string Id { get; }

    IReadOnlyList<DetectedGame> Detect(IGameContainer game);

    Engine Create(IHost host, IGameContainer game, DetectedGame detected);
}

/// <summary>The set of engines a host knows about. This is what makes one player run both AGI and SCI.</summary>
public sealed class EngineRegistry
{
    private readonly List<IEngineProvider> _providers = [];

    public IReadOnlyList<IEngineProvider> Providers => _providers;

    public EngineRegistry Add(IEngineProvider provider)
    {
        _providers.Add(provider);
        return this;
    }

    public IReadOnlyList<DetectedGame> Detect(IGameContainer game)
        => _providers.SelectMany(p => p.Detect(game)).ToList();

    /// <summary>
    /// Detects the game in a folder, or in a subfolder up to <paramref name="maxDepth"/> levels
    /// down when the folder itself holds none: some releases keep their data one folder in
    /// (Codename Iceman's ICE, Space Quest 6's CDROM, a nested King's Quest 7). Returns the
    /// container the games were found in, or null when there is none.
    /// </summary>
    public (DirectoryGameContainer Container, IReadOnlyList<DetectedGame> Games)? DetectInDirectory(string path, int maxDepth = 2)
    {
        var container = new DirectoryGameContainer(path);
        var games = Detect(container);
        if (games.Count > 0)
        {
            return (container, games);
        }

        if (maxDepth <= 0)
        {
            return null;
        }

        foreach (var subfolder in Directory.EnumerateDirectories(path).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (DetectInDirectory(subfolder, maxDepth - 1) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    public Engine Create(IHost host, IGameContainer game, DetectedGame detected)
    {
        var provider = _providers.FirstOrDefault(p => p.Id == detected.EngineId)
            ?? throw new InvalidOperationException($"No engine registered with id '{detected.EngineId}'.");
        return provider.Create(host, game, detected);
    }
}
