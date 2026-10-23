using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Xunit.Abstractions;

namespace Daventry.Sci.Tests;

/// <summary>Title screens that once drew wrong on every host. Skips without the library.</summary>
public sealed class TitleScreenTests(ITestOutputHelper output)
{
    /// <summary>
    /// Laura Bow 1 writes its title and credits with kDisplay and a background colour of -1
    /// (no background), passed as 0xffff. Read unsigned, that filled the text boxes white and
    /// the white text vanished into them: the title was a row of white blocks.
    /// </summary>
    [Fact]
    public void LauraBow1TitleShowsItsText()
    {
        var folder = RealGameTests.SciGameFolders().Select(f => (string)f[0]).FirstOrDefault(f => f.Contains("Laura Bow 1", StringComparison.OrdinalIgnoreCase));
        if (folder is null)
        {
            output.WriteLine("Laura Bow 1 not found, skipping");
            return;
        }

        var host = new HeadlessHost();
        var provider = new SciEngineProvider();
        var game = new DirectoryGameContainer(folder);
        using var engine = (SciEngine)provider.Create(host, game, (SciDetectedGame)provider.Detect(game)[0]);
        Assert.Equal(900, host.Run(engine, 900));

        // the title banner across the top: white text on black, so the box is neither solid
        // white nor solid black
        var frame = host.Graphics.LastFrame!;
        int white = 0;
        int black = 0;
        for (int y = 20; y < 44; y++)
        {
            for (int x = 40; x < 280; x++)
            {
                byte p = frame[x, y];
                if (p == 15)
                {
                    white++;
                }
                else if (p == 0)
                {
                    black++;
                }
            }
        }

        output.WriteLine($"title banner: {white} white and {black} black pixels of {240 * 24}");
        Assert.True(white > 500, "no title text is drawn");
        Assert.True(black > 500, "the title box is filled solid; the text is lost in it");
    }
}
