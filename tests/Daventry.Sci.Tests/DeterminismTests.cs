using Daventry.Core.Hosting;
using Daventry.Core.Input;
using Daventry.Core.Storage;
using Xunit.Abstractions;

namespace Daventry.Sci.Tests;

/// <summary>
/// A headless run is the same every time: the clock the scripts read advances by a fixed step
/// per read inside a tick instead of the real time the tick took, and kRandom follows the host's
/// seed. Two runs of the same game with the same inputs must present the same frames on the same
/// ticks, whatever else the machine is doing. Skips without the library.
/// </summary>
public sealed class DeterminismTests(ITestOutputHelper output)
{
    // the game, the ticks to run, and how often Enter is pressed; KQ5 CD runs its speed benchmark
    // (a kGetTime busy loop) and speech-timed scenes, QFG1 VGA merges SCI1.1 palettes with the
    // timestamps the clock provides, QFG4 is SCI32 and rolls its dice through kRandom
    public static IEnumerable<object[]> Games() =>
    [
        ["King's Quest 5 (CD)", 1500, 0],
        ["Quest for Glory 1 (VGA)", 1200, 150],
        ["Quest for Glory 4", 900, 0],
    ];

    [Theory]
    [MemberData(nameof(Games))]
    public void TwoHeadlessRunsPresentTheSameFrames(string gameName, int ticks, int enterEvery)
    {
        var folder = RealGameTests.SciGameFolders().Select(f => (string)f[0]).FirstOrDefault(f => f.Contains(gameName, StringComparison.OrdinalIgnoreCase));
        if (folder is null)
        {
            output.WriteLine($"{gameName} not found, skipping");
            return;
        }

        var first = RunOnce(folder, ticks, enterEvery);
        var second = RunOnce(folder, ticks, enterEvery);

        output.WriteLine($"{gameName}: {first.Frames.Count} frames presented in {ticks} ticks, room {first.Room}");
        Assert.Equal(first.Room, second.Room);
        Assert.Equal(first.Frames.Count, second.Frames.Count);
        for (int i = 0; i < first.Frames.Count; i++)
        {
            Assert.True(first.Frames[i] == second.Frames[i], $"frame {i} (tick {first.Frames[i].Tick}) differs between the runs");
        }
    }

    private static (List<(int Tick, ulong Hash)> Frames, int Room) RunOnce(string folder, int ticks, int enterEvery)
    {
        var host = new HeadlessHost();
        var provider = new SciEngineProvider();
        var game = new DirectoryGameContainer(folder);
        using var engine = (SciEngine)provider.Create(host, game, (SciDetectedGame)provider.Detect(game)[0]);
        engine.Initialize();

        var frames = new List<(int Tick, ulong Hash)>();
        int presented = 0;
        for (int tick = 1; tick <= ticks; tick++)
        {
            if (enterEvery > 0 && tick % enterEvery == 0)
            {
                host.Input.PushKey(Key.Return, ascii: '\r');
            }

            var result = engine.Tick(tick == 1 ? TimeSpan.Zero : host.TickInterval);
            Assert.True(result.Continue, $"the engine stopped at tick {tick}");
            if (host.Graphics.PresentCount != presented)
            {
                presented = host.Graphics.PresentCount;
                frames.Add((tick, Fnv1a(host.Graphics.LastFrame!.Pixels)));
            }
        }

        return (frames, engine.State!.CurrentRoomNumber);
    }

    private static ulong Fnv1a(byte[] bytes)
    {
        ulong hash = 14695981039346656037;
        foreach (byte b in bytes)
        {
            hash = (hash ^ b) * 1099511628211;
        }

        return hash;
    }
}
