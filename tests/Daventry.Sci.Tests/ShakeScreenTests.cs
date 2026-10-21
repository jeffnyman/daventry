using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Xunit.Abstractions;

namespace Daventry.Sci.Tests;

/// <summary>kShakeScreen's offset reaches the host as a shifted frame. Skips without the library.</summary>
public class ShakeScreenTests(ITestOutputHelper output)
{
    [Fact]
    public void ShakeOffsetPresentsTheFrameShifted()
    {
        string? folder = null;
        foreach (var row in RealGameTests.SciGameFolders())
        {
            var candidate = (string)row[0];
            if (candidate.Length > 0 && Path.GetFileName(candidate) == "King's Quest 1 (SCI)")
            {
                folder = candidate;
            }
        }

        if (folder is null)
        {
            output.WriteLine("King's Quest 1 (SCI) not found, skipping");
            return;
        }

        var host = new HeadlessHost(NullLog.Instance, new MemorySaveStore());
        var provider = new SciEngineProvider();
        var game = new DirectoryGameContainer(folder);
        using var engine = (SciEngine)provider.Create(host, game, (SciDetectedGame)provider.Detect(game)[0]);
        engine.Initialize();
        for (int tick = 1; tick <= 600; tick++)
        {
            Assert.True(engine.Tick(tick == 1 ? TimeSpan.Zero : host.TickInterval).Continue);
        }

        var screen = engine.State!.Screen;
        screen.DisplayDirty = true;
        engine.Tick(host.TickInterval);
        var still = host.Graphics.LastFrame!.Pixels.ToArray();

        screen.ShakeOffset = (10, 10);
        screen.DisplayDirty = true;
        engine.Tick(host.TickInterval);
        var shaken = host.Graphics.LastFrame!.Pixels;
        int width = host.Graphics.LastFrame.Width;
        int height = host.Graphics.LastFrame.Height;

        // the uncovered edge is black
        Assert.All(shaken.Take(width * 10), p => Assert.Equal(0, p));
        Assert.All(Enumerable.Range(0, height).Select(y => shaken[y * width]), p => Assert.Equal(0, p));

        // the rest is the picture moved 10 right and 10 down; animation between the two ticks may
        // change a few pixels, so most of the frame has to match rather than all of it
        int compared = 0;
        int same = 0;
        for (int y = 10; y < height; y++)
        {
            for (int x = 10; x < width; x++)
            {
                compared++;
                if (shaken[y * width + x] == still[(y - 10) * width + x - 10])
                {
                    same++;
                }
            }
        }

        output.WriteLine($"{same} of {compared} pixels match the shifted still frame");
        Assert.True(same > compared * 0.98, "the shaken frame is not the shifted picture");

        screen.ShakeOffset = (0, 0);
        screen.DisplayDirty = true;
        engine.Tick(host.TickInterval);
        Assert.Equal(screen.Display.Pixels.Length, host.Graphics.LastFrame!.Pixels.Length);
    }
}
