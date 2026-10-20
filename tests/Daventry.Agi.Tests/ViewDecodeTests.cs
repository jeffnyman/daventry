using Daventry.Agi.Graphics;
using Daventry.Agi.Resources;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Xunit.Abstractions;

namespace Daventry.Agi.Tests;

/// <summary>Decodes every view of every real AGI game in the sierra-games folder.</summary>
public class ViewDecodeTests(ITestOutputHelper output)
{
    [Theory]
    [MemberData(nameof(RealGameTests.AgiGameFolders), MemberType = typeof(RealGameTests))]
    public void EveryViewDecodes(string folder)
    {
        if (folder.Length == 0)
        {
            return;
        }

        var game = new DirectoryGameContainer(folder);
        var detected = (AgiDetectedGame)new AgiEngineProvider().Detect(game)[0];
        var resources = new AgiResourceManager(game, detected, NullLog.Instance);
        resources.Initialize();
        var gfx = new GfxMgr(AgiRenderMode.Ega, detected.Version, detected.Gid);
        resources.ViewDecoder = new AgiViewDecoder(detected, gfx);

        int views = 0;
        int loops = 0;
        int cels = 0;
        int mirrored = 0;
        var broken = new List<string>();
        for (int i = 0; i < AgiDirectory.MaxEntries; i++)
        {
            if (resources.Directory.View[i].IsEmpty)
            {
                continue;
            }

            AgiView? view;
            try
            {
                view = resources.LoadView(i);
            }
            catch (AgiResourceException ex)
            {
                broken.Add($"view {i}: {ex.Message}");
                continue;
            }

            if (view is null)
            {
                continue; // unreadable in the original data, covered by RealGameTests
            }

            views++;
            foreach (var loop in view.Loops)
            {
                loops++;
                foreach (var cel in loop.Cels)
                {
                    cels++;
                    Assert.Equal(cel.Width * cel.Height, cel.RawBitmap.Length);
                    Assert.True(cel.Width > 0 && cel.Height > 0);
                    if (cel.Mirrored)
                    {
                        mirrored++;
                    }
                }
            }
        }

        output.WriteLine($"{game.Name}: {views} views, {loops} loops, {cels} cels, {mirrored} mirrored cels");
        Assert.Empty(broken);
    }
}
