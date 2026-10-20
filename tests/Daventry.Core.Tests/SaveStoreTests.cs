using Daventry.Core.Storage;

namespace Daventry.Core.Tests;

public class SaveStoreTests
{
    [Fact]
    public void MemorySaveStore_RoundTrips()
    {
        var store = new MemorySaveStore();
        using (var w = store.OpenWrite("kq1.001"))
        {
            w.Write([1, 2, 3]);
        }

        Assert.Equal(["kq1.001"], store.List("kq1"));
        using var r = store.OpenRead("kq1.001");
        Assert.NotNull(r);
        var buffer = new byte[3];
        Assert.Equal(3, r.Read(buffer));
        Assert.Equal([1, 2, 3], buffer);
        Assert.True(store.Delete("kq1.001"));
        Assert.Null(store.OpenRead("kq1.001"));
    }

    [Fact]
    public void FileSaveStore_RejectsPathTraversal()
    {
        var store = new FileSaveStore(Path.Combine(Path.GetTempPath(), "daventry-tests"));
        Assert.Throws<ArgumentException>(() => store.OpenRead("../x"));
    }
}
