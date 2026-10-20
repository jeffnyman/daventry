using System.Text;
using Daventry.Agi.Detection;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Xunit.Abstractions;

namespace Daventry.Agi.Tests;

public class DetectionTests(ITestOutputHelper output)
{
    private static readonly byte[] Empty = [];

    [Fact]
    public void FallsBackToTheV2LayoutAsAFanGame()
    {
        var game = new MemoryGameContainer("kq1")
            .Add("LOGDIR", Empty).Add("PICDIR", Empty).Add("VIEWDIR", Empty).Add("SNDDIR", Empty)
            .Add("VOL.0", Empty).Add("OBJECT", Empty).Add("WORDS.TOK", Empty);

        var found = new AgiEngineProvider().Detect(game);

        var detected = Assert.IsType<AgiDetectedGame>(Assert.Single(found));
        Assert.Equal("agi", detected.EngineId);
        Assert.Equal("agi-fanmade", detected.GameId);
        Assert.Equal("Unknown v2 Game", detected.Title);
        Assert.Equal(AgiGameType.V2, detected.Type);
        Assert.Equal(0x2917, detected.Version);
        Assert.False(detected.Identified);
    }

    [Fact]
    public void FallsBackToTheV3Layout_CaseInsensitive()
    {
        var game = new MemoryGameContainer("kq4")
            .Add("kq4dir", Empty).Add("kq4vol.0", Empty).Add("object", Empty).Add("words.tok", Empty);

        var detected = Assert.IsType<AgiDetectedGame>(Assert.Single(new AgiEngineProvider().Detect(game)));
        Assert.Equal("Unknown v3 Game", detected.Title);
        Assert.Equal(AgiGameType.V3, detected.Type);
        Assert.Equal(0x3149, detected.Version);
    }

    [Fact]
    public void IgnoresFoldersWithoutVocabulary()
    {
        var game = new MemoryGameContainer("x").Add("LOGDIR", Empty).Add("VOL.0", Empty);
        Assert.Empty(new AgiEngineProvider().Detect(game));
    }

    [Fact]
    public void FallbackReadsAWinAgiProjectFile()
    {
        var wag = new List<byte>();
        void Property(WagFileParser.PropertyCode code, string data)
        {
            wag.Add((byte)code);
            wag.Add(1);
            wag.Add(0);
            wag.Add((byte)data.Length);
            wag.Add((byte)(data.Length >> 8));
            wag.AddRange(Encoding.Latin1.GetBytes(data));
        }

        Property(WagFileParser.PropertyCode.GameId, "sq2");
        Property(WagFileParser.PropertyCode.GameDescription, "Space Quest II remake");
        Property(WagFileParser.PropertyCode.InterpreterVersion, "3.002086");
        Property(WagFileParser.PropertyCode.GameVersion, "0.9");
        Property(WagFileParser.PropertyCode.GameLastEdit, "2004-01-02");
        wag.AddRange(Encoding.Latin1.GetBytes("WINAGI v1.0     "));

        var game = new MemoryGameContainer("remake")
            .Add("LOGDIR", Empty).Add("PICDIR", Empty).Add("VIEWDIR", Empty).Add("SNDDIR", Empty)
            .Add("VOL.0", Empty).Add("OBJECT", Empty).Add("WORDS.TOK", Empty).Add("remake.wag", wag.ToArray());

        var detected = Assert.IsType<AgiDetectedGame>(Assert.Single(new AgiEngineProvider().Detect(game)));
        Assert.Equal("sq2", detected.GameId);
        Assert.Equal("Space Quest II remake", detected.Title);
        Assert.Equal("0.9 2004-01-02", detected.Extra);
        Assert.Equal(0x3086, detected.Version);
        Assert.Equal(AgiGameType.V3, detected.Type);
    }

    [Fact]
    public void WagVersionStringsConvertLikeScummVm()
    {
        static ushort Convert(string text)
            => WagFileParser.ConvertToAgiVersionNumber(new WagFileParser.Property(WagFileParser.PropertyCode.InterpreterVersion, 0, 0, text));

        Assert.Equal(0x2440, Convert("2.44"));
        Assert.Equal(0x2917, Convert("2.917"));
        Assert.Equal(0x3086, Convert("3.002086"));
        Assert.Equal(0x2440, Convert("2,44"));
        Assert.Equal(0, Convert("abc"));
        Assert.Equal(0, Convert("2.9a"));
    }

    [Fact]
    public void TableHasEveryKnownSierraTitle()
    {
        Assert.True(AgiGameTable.Entries.Length > 400);
        foreach (var gid in Enum.GetValues<AgiGameId>())
        {
            Assert.Contains(AgiGameTable.Entries, e => e.Gid == gid);
        }

        Assert.All(AgiGameTable.Entries, e => Assert.All(e.Files, f => Assert.Equal(32, f.Md5.Length)));
    }

    [Fact]
    public void IdentifiesEveryGameInTheLibrary()
    {
        var folders = RealGameTests.AllDetectedFolders().Select(args => (string)args[0]).Where(f => f.Length > 0).ToList();
        if (folders.Count == 0)
        {
            output.WriteLine("library not found, skipping");
            return;
        }

        var unidentified = new List<string>();
        foreach (var folder in folders)
        {
            var found = new AgiEngineProvider().Detect(new DirectoryGameContainer(folder));
            var detected = Assert.IsType<AgiDetectedGame>(found[0]);
            output.WriteLine($"{Path.GetFileName(folder),-32} -> {detected}  gid={detected.Gid} type={detected.Type} version={detected.Version:X4} platform={detected.Platform}" +
                (found.Count > 1 ? $"  (+{found.Count - 1} more)" : string.Empty));
            if (!detected.Identified)
            {
                unidentified.Add(Path.GetFileName(folder));
            }
        }

        Assert.True(unidentified.Count == 0, "not in the MD5 table: " + string.Join(", ", unidentified));
    }

    [Theory]
    [InlineData("Mickeys_Space_Adventure", AgiGameId.Mickey)]
    [InlineData("Winnie_the_Pooh_-_In_the_Hundred_Acre_Wood", AgiGameId.Winnie)]
    [InlineData("Trolls_Tale", AgiGameId.Troll)]
    public void IdentifiesPreAgiGamesAndRefusesToRunThem(string folderName, AgiGameId gid)
    {
        var folder = RealGameTests.AllDetectedFolders().Select(args => (string)args[0]).FirstOrDefault(f => f.Length > 0 && Path.GetFileName(f) == folderName);
        if (folder is null)
        {
            output.WriteLine($"{folderName} not found, skipping");
            return;
        }

        var provider = new AgiEngineProvider();
        var game = new DirectoryGameContainer(folder);
        var detected = Assert.IsType<AgiDetectedGame>(provider.Detect(game)[0]);
        output.WriteLine(detected.ToString());

        Assert.Equal(gid, detected.Gid);
        Assert.Equal(AgiGameType.PreAgi, detected.Type);
        Assert.True(detected.Identified);

        var ex = Assert.Throws<NotSupportedException>(() => provider.Create(new HeadlessHost(), game, detected));
        output.WriteLine(ex.Message);
    }
}
