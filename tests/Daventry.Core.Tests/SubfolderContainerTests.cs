using Daventry.Core.Hosting;
using Daventry.Core.Storage;

namespace Daventry.Core.Tests;

/// <summary>Folder trees in a container (a browser folder upload) and finding the game inside one.</summary>
public sealed class SubfolderContainerTests
{
    private static MemoryGameContainer Tree() => new MemoryGameContainer("upload")
        .Add("readme.txt", [1])
        .Add("King's Quest 7/setup.exe", [2])
        .Add("King's Quest 7/King's Quest 7/RESOURCE.MAP", [3, 3])
        .Add("King's Quest 7/King's Quest 7/AVI/1.RBT", [4, 4, 4])
        .Add("King's Quest 7/King's Quest 7/AVI/deeper/x.bin", [5]);

    [Fact]
    public void AViewShowsAFolderOneLevelDeepWithRelativeNames()
    {
        var view = new SubfolderGameContainer(Tree(), "King's Quest 7/King's Quest 7");

        Assert.Equal("King's Quest 7", view.Name);
        Assert.Equal(["AVI/1.RBT", "RESOURCE.MAP"], view.EnumerateFiles().Order(StringComparer.Ordinal));
        Assert.True(view.Exists(@"avi\1.rbt"));
        Assert.Equal(3, view.GetLength("avi/1.rbt"));
        Assert.Equal([3, 3], view.ReadAllBytes("resource.map"));
        Assert.False(view.Exists("AVI/deeper/x.bin"));
        Assert.Throws<FileNotFoundException>(() => view.Open("setup.exe"));
    }

    [Fact]
    public void TheTopOfTheTreeKeepsItsName()
    {
        var view = new SubfolderGameContainer(Tree(), string.Empty);

        Assert.Equal("upload", view.Name);
        Assert.Equal(["King's Quest 7/setup.exe", "readme.txt"], view.EnumerateFiles().Order(StringComparer.Ordinal));
        Assert.Equal(["King's Quest 7"], SubfolderGameContainer.Subfolders(Tree(), string.Empty));
        Assert.Equal(["King's Quest 7/King's Quest 7"], SubfolderGameContainer.Subfolders(Tree(), "King's Quest 7"));
    }

    [Fact]
    public void DetectionLooksDownTheTreeLikeItDoesInADirectory()
    {
        var registry = new EngineRegistry().Add(new MapProvider());

        var found = registry.DetectInContainer(Tree());

        Assert.NotNull(found);
        Assert.Equal("King's Quest 7/King's Quest 7", found.Value.Container.Folder);
        Assert.Equal("kq7", Assert.Single(found.Value.Games).GameId);
        Assert.Null(registry.DetectInContainer(Tree(), maxDepth: 1));
        Assert.Null(registry.DetectInContainer(new MemoryGameContainer("empty")));
    }

    /// <summary>Recognises any folder with a RESOURCE.MAP.</summary>
    private sealed class MapProvider : IEngineProvider
    {
        public string Id => "test";

        public IReadOnlyList<DetectedGame> Detect(IGameContainer game)
            => game.Exists("RESOURCE.MAP") ? [new DetectedGame(Id, "kq7", "King's Quest VII")] : [];

        public Engine Create(IHost host, IGameContainer game, DetectedGame detected) => throw new NotSupportedException();
    }
}
