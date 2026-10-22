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
    public void FileSaveStore_InflatesScummVmCompressedSaves()
    {
        var directory = Path.Combine(Path.GetTempPath(), "daventry-tests", Path.GetRandomFileName());
        var store = new FileSaveStore(directory);
        byte[] payload = [.. "AGI:a save written through a gzip stream"u8];
        using (var w = store.OpenWrite("kq1.001"))
        using (var gzip = new System.IO.Compression.GZipStream(w, System.IO.Compression.CompressionMode.Compress))
        {
            gzip.Write(payload);
        }

        using (var w = store.OpenWrite("kq1.002"))
        {
            w.Write(payload);
        }

        foreach (var name in new[] { "kq1.001", "kq1.002" })
        {
            using var r = store.OpenRead(name);
            Assert.NotNull(r);
            Assert.True(r.CanSeek);
            var buffer = new byte[payload.Length];
            r.ReadExactly(buffer);
            Assert.Equal(payload, buffer);
            Assert.Equal(payload.Length, r.Length);
        }

        Directory.Delete(directory, true);
    }

    [Fact]
    public void FileSaveStore_RejectsPathTraversal()
    {
        var store = new FileSaveStore(Path.Combine(Path.GetTempPath(), "daventry-tests"));
        Assert.Throws<ArgumentException>(() => store.OpenRead("../x"));
    }
}
