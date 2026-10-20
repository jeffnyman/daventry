using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Xunit.Abstractions;

namespace Daventry.Sci.Tests;

/// <summary>
/// Boots every SCI0 to SCI1.1 game in the sierra-games folder through the interpreter and runs
/// it headless for a few seconds: the scripts must run without a VM error and draw something.
/// </summary>
public class VmTests(ITestOutputHelper output)
{
    private const int Ticks = 600;

    public static IEnumerable<object[]> SciGameFolders() => RealGameTests.SciGameFolders();

    [Theory]
    [MemberData(nameof(SciGameFolders))]
    public void EveryGameBootsAndDrawsItsFirstRoom(string folder)
    {
        if (string.IsNullOrEmpty(folder))
        {
            output.WriteLine("sierra-games folder not found, skipping");
            return;
        }

        var warnings = new List<string>();
        var host = new HeadlessHost(new ListLog(warnings));
        var provider = new SciEngineProvider();
        var game = new DirectoryGameContainer(folder);
        var detected = provider.Detect(game);
        if (detected.Count == 0)
        {
            output.WriteLine($"{Path.GetFileName(folder)}: not detected, skipping");
            return;
        }

        var sciGame = (SciDetectedGame)detected[0];
        if (sciGame.Version >= SciVersion.V2)
        {
            output.WriteLine($"{Path.GetFileName(folder)}: {sciGame.Version.Describe()}, skipping");
            return;
        }

        using var engine = (SciEngine)provider.Create(host, game, sciGame);
        int ran = host.Run(engine, Ticks);

        var frame = host.Graphics.LastFrame;
        Assert.NotNull(frame);
        int lit = frame.Pixels.Count(p => p != 0);
        var rooms = engine.State is { } s ? s.CurrentRoomNumber.ToString() : "-";
        output.WriteLine($"{Path.GetFileName(folder)}: {sciGame.Version.Describe()}, {ran} ticks, room {rooms}, {lit} lit pixels, {warnings.Count} warnings");
        foreach (var warning in warnings.Distinct().Take(10))
        {
            output.WriteLine("  " + warning);
        }

        Assert.Equal(Ticks, ran);
        Assert.True(lit > 0, "nothing was drawn");
    }

    private sealed class ListLog(List<string> warnings) : ILog
    {
        public void Write(LogLevel level, string category, string message)
        {
            if (level >= LogLevel.Warning)
            {
                warnings.Add($"{category}: {message}");
            }
        }
    }
}
