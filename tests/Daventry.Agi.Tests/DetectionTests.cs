using Daventry.Core.Storage;

namespace Daventry.Agi.Tests;

public class DetectionTests
{
    private static readonly byte[] Empty = [];

    [Fact]
    public void DetectsAgiV2Layout()
    {
        var game = new MemoryGameContainer("kq1")
            .Add("LOGDIR", Empty).Add("PICDIR", Empty).Add("VIEWDIR", Empty).Add("SNDDIR", Empty)
            .Add("VOL.0", Empty).Add("OBJECT", Empty).Add("WORDS.TOK", Empty);

        var found = new AgiEngineProvider().Detect(game);

        var detected = Assert.Single(found);
        Assert.Equal("agi", detected.EngineId);
        Assert.Equal("agi-v2", detected.GameId);
    }

    [Fact]
    public void DetectsAgiV3Layout_CaseInsensitive()
    {
        var game = new MemoryGameContainer("kq4")
            .Add("kq4dir", Empty).Add("kq4vol.0", Empty).Add("object", Empty).Add("words.tok", Empty);

        var found = new AgiEngineProvider().Detect(game);

        var detected = Assert.Single(found);
        Assert.Equal("agi-v3", detected.GameId);
    }

    [Fact]
    public void IgnoresFoldersWithoutVocabulary()
    {
        var game = new MemoryGameContainer("x").Add("LOGDIR", Empty).Add("VOL.0", Empty);
        Assert.Empty(new AgiEngineProvider().Detect(game));
    }
}
