using Daventry.Core.Storage;
using Daventry.Sci.Detection;
using Xunit.Abstractions;

namespace Daventry.Sci.Tests;

public class DetectionTests(ITestOutputHelper output)
{
    [Fact]
    public void TableCoversEveryGameIdAndEveryMappedId()
    {
        Assert.True(SciGameTable.Entries.Length > 600);
        Assert.All(SciGameTable.Entries, e => Assert.True(SciGameTable.Titles.ContainsKey(e.GameId), e.GameId));
        Assert.All(SciGameTable.Entries, e => Assert.All(e.Files, f => Assert.True(f.Md5 is null || f.Md5.Length == 32)));
        Assert.Contains(SciGameTable.GameIds, m => m.GameId == "kq4sci" && m.SierraId == "kq4" && m.Gid == SciGameId.Kq4);
        Assert.Contains(SciGameTable.GameIds, m => m.GameId == "hoyle4" && m.Version == SciVersion.V1_1);
    }

    public static IEnumerable<object[]> Expectations()
    {
        yield return ["King's Quest 4 (SCI)", "kq4sci", SciGameId.Kq4, SciVersion.V0Early];
        yield return ["King's Quest 1 (SCI)", "kq1sci", SciGameId.Kq1, SciVersion.V01];
        yield return ["Quest for Glory 2", "qfg2", SciGameId.Qfg2, SciVersion.V1EgaOnly];
        yield return ["Quest for Glory 1 (EGA)", "qfg1", SciGameId.Qfg1, SciVersion.V0Late];
        yield return ["Quest for Glory 1 (VGA)", "qfg1vga", SciGameId.Qfg1vga, SciVersion.V1_1];
        yield return ["Quest for Glory 3", "qfg3", SciGameId.Qfg3, SciVersion.V1_1];
        yield return ["Quest for Glory 4", "qfg4", SciGameId.Qfg4, SciVersion.V2];
        yield return ["King's Quest 5 (Diskette)", "kq5", SciGameId.Kq5, SciVersion.V1Early];
        yield return ["King's Quest 5 (CD)", "kq5", SciGameId.Kq5, SciVersion.V1Late];
        yield return ["King's Quest 6 (1993, CD)", "kq6", SciGameId.Kq6, SciVersion.V1_1];
        yield return ["Leisure Suit Larry 1 (VGA)", "lsl1sci", SciGameId.Lsl1, SciVersion.V1Middle];
        yield return ["Police Quest 1 (VGA)", "pq1sci", SciGameId.Pq1, SciVersion.V1_1];
        yield return ["Police Quest 4", "pq4", SciGameId.Pq4, SciVersion.V2];
        yield return ["Space Quest 1 (VGA)", "sq1sci", SciGameId.Sq1, SciVersion.V1Late];
        yield return ["Conquests of Camelot", "camelot", SciGameId.Camelot, SciVersion.V0Late];
        yield return ["Laura Bow 1 - The Colonel's Bequest", "laurabow", SciGameId.Laurabow, SciVersion.V0Late];
    }

    [Theory]
    [MemberData(nameof(Expectations))]
    public void IdentifiesKnownLibraryGames(string folderName, string gameId, SciGameId gid, SciVersion version)
    {
        var folder = RealGameTests.SciGameFolders().Select(a => (string)a[0]).FirstOrDefault(f => f.Length > 0 && Path.GetFileName(f) == folderName);
        if (folder is null)
        {
            output.WriteLine($"{folderName} not found, skipping");
            return;
        }

        var found = new SciEngineProvider().Detect(new DirectoryGameContainer(folder));
        var detected = Assert.IsType<SciDetectedGame>(found[0]);
        output.WriteLine($"{detected}  gid={detected.Gid} version={detected.Version} views={detected.ViewType} platform={detected.Platform} lang={detected.Language} flags={detected.Flags} identified={detected.Identified}" + (found.Count > 1 ? $" (+{found.Count - 1} more)" : string.Empty));

        Assert.Equal(gameId, detected.GameId);
        Assert.Equal(gid, detected.Gid);
        Assert.Equal(version, detected.Version);
    }

    [Fact]
    public void ReportsHowEveryLibraryGameWasIdentified()
    {
        var folders = RealGameTests.SciGameFolders().Select(a => (string)a[0]).Where(f => f.Length > 0).ToList();
        if (folders.Count == 0)
        {
            return;
        }

        int byTable = 0;
        foreach (var folder in folders)
        {
            var found = new SciEngineProvider().Detect(new DirectoryGameContainer(folder));
            Assert.NotEmpty(found);
            var detected = Assert.IsType<SciDetectedGame>(found[0]);
            byTable += detected.Identified ? 1 : 0;
            string others = found.Count > 1 ? " also: " + string.Join("; ", found.Skip(1).Select(f => $"{f.PlatformName} {f.Extra}")) : string.Empty;
            output.WriteLine($"{Path.GetFileName(folder),-36} {(detected.Identified ? "table   " : "fallback")} {detected}{others}");
            Assert.NotEqual("sci-fanmade", detected.GameId);
        }

        output.WriteLine($"{byTable} of {folders.Count} identified by MD5");
    }
}
