using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Daventry.Sci.Detection;
using Daventry.Sci.Resources;

namespace Daventry.Sci;

/// <summary>
/// A detected SCI game. <see cref="GameId"/> is ScummVM's id ("kq4sci", "sci-fanmade");
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
/// Recognises SCI games with ScummVM's MD5 table and resource-based fallback. The engine itself
/// is not ported yet, so <see cref="Create"/> refuses.
/// </summary>
public sealed class SciEngineProvider : IEngineProvider
{
    public const string EngineId = "sci";

    public string Id => EngineId;

    public ILog Log { get; init; } = NullLog.Instance;

    public IReadOnlyList<DetectedGame> Detect(IGameContainer game) => new SciDetector(game, Log).Detect();

    public Engine Create(IHost host, IGameContainer game, DetectedGame detected)
        => throw new NotSupportedException("The SCI engine has not been ported yet.");
}
