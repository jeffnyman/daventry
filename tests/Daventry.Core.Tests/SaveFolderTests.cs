using Daventry.Core.Storage;

namespace Daventry.Core.Tests;

public class SaveFolderTests
{
    [Fact]
    public void PerGameFoldersAreMergedIntoOne()
    {
        var root = Path.Combine(Path.GetTempPath(), "daventry-saves-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "kq1"));
            Directory.CreateDirectory(Path.Combine(root, "qfg1"));
            Directory.CreateDirectory(Path.Combine(root, "qfg2"));
            File.WriteAllBytes(Path.Combine(root, "kq1", "kq1.001"), [1]);
            File.WriteAllBytes(Path.Combine(root, "qfg1", "qfg1-glory1.sav"), [2]);
            File.WriteAllBytes(Path.Combine(root, "qfg2", "qfg2.000"), [3]);
            File.WriteAllBytes(Path.Combine(root, "qfg2.000"), [4]);

            Assert.Equal(2, FileSaveStore.MergeSubfolders(root));

            var store = new FileSaveStore(root);
            Assert.Equal([1], store.ReadAllBytes("kq1.001"));
            Assert.Equal(["qfg1-glory1.sav"], store.ListMatching("qfg1-*"));
            Assert.False(Directory.Exists(Path.Combine(root, "kq1")));
            Assert.False(Directory.Exists(Path.Combine(root, "qfg1")));

            // a name already taken is left in its folder
            Assert.Equal([4], store.ReadAllBytes("qfg2.000"));
            Assert.True(File.Exists(Path.Combine(root, "qfg2", "qfg2.000")));

            Assert.Equal(0, FileSaveStore.MergeSubfolders(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void AMissingFolderIsLeftAlone()
        => Assert.Equal(0, FileSaveStore.MergeSubfolders(Path.Combine(Path.GetTempPath(), "daventry-none-" + Guid.NewGuid().ToString("N"))));
}
