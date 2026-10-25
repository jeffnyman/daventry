using Daventry.Core.Storage;

namespace Daventry.Core.Tests;

public class SavePatternTests
{
    [Theory]
    [InlineData("kq1.001", "kq1.###", true)]
    [InlineData("kq1.0a1", "kq1.###", false)]
    [InlineData("KQ1.001", "kq1.###", true)]
    [InlineData("lsl3-larry3.drv", "lsl3-*", true)]
    [InlineData("lsl3-larry3.drv", "lsl3-*.drv", true)]
    [InlineData("lsl3-larry3.drv", "lsl3-*.txt", false)]
    [InlineData("qfg1-glory1.sav", "qfg1-*", true)]
    [InlineData("qfg1vga-glory1.sav", "qfg1-*", false)]
    [InlineData("abc", "a?c", true)]
    [InlineData("ac", "a?c", false)]
    [InlineData("a#c", "a\\#c", true)]
    [InlineData("a1c", "a\\#c", false)]
    [InlineData("abc", "a\\*c", true)] // only # and the backslash can be escaped
    [InlineData("abc", "*", true)]
    [InlineData("", "*", true)]
    [InlineData("abc", "abc*", true)]
    [InlineData("abcabd", "*abd", true)]
    public void MatchesLikeScummVM(string name, string pattern, bool expected)
        => Assert.Equal(expected, SavePattern.Match(name, pattern));

    [Fact]
    public void CaseIsHonouredWhenAskedFor()
    {
        Assert.False(SavePattern.Match("ABC", "abc", ignoreCase: false));
        Assert.True(SavePattern.Match("abc", "abc", ignoreCase: false));
    }

    [Fact]
    public void WildcardsStopAtExcludedCharacters()
    {
        // kDeviceInfo compares paths with '/' excluded from the wildcards
        Assert.True(SavePattern.Match("c:/sierra", "c:/*", false, "/"));
        Assert.False(SavePattern.Match("c:/sierra/kq1", "c:/*", false, "/"));
    }

    [Fact]
    public void TheStoreListsMatchesAndRenamesFiles()
    {
        var store = new MemorySaveStore();
        store.WriteAllBytes("kq1.001", [1]);
        store.WriteAllBytes("kq1.002", [2]);
        store.WriteAllBytes("kq1-notes.txt", [3]);

        Assert.Equal(["kq1.001", "kq1.002"], store.ListMatching("kq1.###"));
        Assert.Equal(["kq1-notes.txt"], store.ListMatching("kq1-*"));

        Assert.True(store.Rename("kq1-notes.txt", "kq1-moved.txt"));
        Assert.False(store.Exists("kq1-notes.txt"));
        Assert.Equal([3], store.ReadAllBytes("kq1-moved.txt"));
        Assert.False(store.Rename("kq1-notes.txt", "kq1-other.txt"));

        Assert.True(store.Copy("kq1.001", "kq1.003"));
        Assert.Equal([1], store.ReadAllBytes("kq1.003"));
        Assert.True(store.Exists("kq1.001"));
    }
}
