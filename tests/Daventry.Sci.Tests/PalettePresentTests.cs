using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Xunit.Abstractions;

namespace Daventry.Sci.Tests;

/// <summary>
/// A palette change recolours the screen without touching a pixel, so it has to present a frame
/// on its own (the KQ6 CD intro fades its text in with kPaletteSetIntensity). Skips without the library.
/// </summary>
public class PalettePresentTests(ITestOutputHelper output)
{
    [Fact]
    public void PaletteOnlyChangePresentsAFrame()
    {
        string? folder = null;
        foreach (var row in RealGameTests.SciGameFolders())
        {
            var candidate = (string)row[0];
            if (candidate.Length > 0 && Path.GetFileName(candidate) == "King's Quest 6 (1993, CD)")
            {
                folder = candidate;
            }
        }

        if (folder is null)
        {
            output.WriteLine("King's Quest 6 (1993, CD) not found, skipping");
            return;
        }

        var host = new HeadlessHost(NullLog.Instance, new MemorySaveStore());
        var provider = new SciEngineProvider();
        var game = new DirectoryGameContainer(folder);
        using var engine = (SciEngine)provider.Create(host, game, (SciDetectedGame)provider.Detect(game)[0]);
        engine.Initialize();
        for (int tick = 1; tick <= 300; tick++)
        {
            Assert.True(engine.Tick(tick == 1 ? TimeSpan.Zero : host.TickInterval).Continue);
        }

        // find a tick that presents nothing on its own
        int quiet = 0;
        int before = host.Graphics.PresentCount;
        for (int tick = 0; tick < 120 && quiet == 0; tick++)
        {
            engine.Tick(host.TickInterval);
            if (host.Graphics.PresentCount == before)
            {
                quiet++;
            }

            before = host.Graphics.PresentCount;
        }

        Assert.True(quiet > 0, "every tick of the intro presented a frame, so the test cannot isolate a palette change");

        // a palette-only change on the next tick has to reach the host as a new frame
        engine.Palette.KernelSetIntensity(1, 255, 50, true);
        engine.Tick(host.TickInterval);
        output.WriteLine($"{host.Graphics.PresentCount - before} frame(s) presented after the intensity change");
        Assert.True(host.Graphics.PresentCount > before, "the palette change did not present a frame");
    }
}
