using Daventry.Core.Hosting;
using Daventry.Core.Input;
using Daventry.Core.Storage;
using Xunit.Abstractions;

namespace Daventry.Sci.Tests;

/// <summary>
/// The title-screen buttons of the games whose scripts only accept a click on a feature the
/// mouse hovered first: a headless click has to move the mouse, press, and release later, as
/// a hand does. Both games were checked with a real mouse in the SDL window on 2026-10-22.
/// Skips without the library.
/// </summary>
public sealed class TitleButtonTests(ITestOutputHelper output)
{
    /// <summary>KQ6 CD: Escape twice through the intro, then Play at (245,190) on the title (room 100) enters the beach (room 200).</summary>
    [Fact]
    public void Kq6CdPlayButtonEntersTheGame()
    {
        var folder = FindFolder("Quest 6 (1993");
        if (folder is null)
        {
            output.WriteLine("KQ6 CD not found, skipping");
            return;
        }

        var warnings = new List<string>();
        var host = new HeadlessHost(new ListLog(warnings));
        var provider = new SciEngineProvider();
        var game = new DirectoryGameContainer(folder);
        using var engine = (SciEngine)provider.Create(host, game, (SciDetectedGame)provider.Detect(game)[0]);

        Assert.Equal(700, host.Run(engine, 700));
        host.Input.PushKey(Key.Escape, ascii: '\u001b');
        Assert.Equal(600, host.Continue(engine, 600));
        host.Input.PushKey(Key.Escape, ascii: '\u001b');
        Assert.Equal(900, host.Continue(engine, 900));
        Assert.Equal(100, engine.State!.CurrentRoomNumber);

        HeadlessClicks.Click(host, engine, 245, 190);
        Assert.Equal(500, host.Continue(engine, 500));

        output.WriteLine($"room {engine.State.CurrentRoomNumber} after Play, {warnings.Count} warnings");
        Assert.Equal(200, engine.State.CurrentRoomNumber);
        Assert.DoesNotContain(warnings, w => w.Contains("invalid object"));
    }

    /// <summary>
    /// SQ6: Start New Game at (320,202) in the 640x480 screen (the scripts see it at (160,85)
    /// in their 320x200 coordinates) starts the intro, which plays in the same room 500.
    /// </summary>
    [Fact]
    public void Sq6StartNewGameButtonStartsTheIntro()
    {
        var folder = FindFolder("Space Quest 6");
        if (folder is null)
        {
            output.WriteLine("SQ6 not found, skipping");
            return;
        }

        var warnings = new List<string>();
        var host = new HeadlessHost(new ListLog(warnings));
        var provider = new SciEngineProvider();
        var game = new DirectoryGameContainer(folder);
        using var engine = (SciEngine)provider.Create(host, game, (SciDetectedGame)provider.Detect(game)[0]);

        Assert.Equal(2400, host.Run(engine, 2400));
        var title = host.Graphics.LastFrame!.Pixels.ToArray();
        HeadlessClicks.Click(host, engine, 320, 202);
        Assert.Equal(900, host.Continue(engine, 900));

        // the menu box (x 150-490, y 160-320 of the 640x480 screen) is gone; the opening scene is a
        // spotlight on a black stage, so the rest of the screen stays mostly black either way
        var frame = host.Graphics.LastFrame!;
        int same = 0;
        int compared = 0;
        for (int y = 160; y < 320; y++)
        {
            for (int x = 150; x < 490; x++)
            {
                int i = y * frame.Width + x;
                compared++;
                if (title[i] == frame.Pixels[i])
                {
                    same++;
                }
            }
        }

        double matching = (double)same / compared;
        output.WriteLine($"{matching:P1} of the title menu is still showing after Start New Game, {warnings.Count} warnings");
        Assert.True(matching < 0.5, $"{matching:P1} of the title menu is still showing; the button did not react");
        Assert.True(frame.Pixels.Any(p => p != 0), "nothing is drawn after the button");
        Assert.DoesNotContain(warnings, w => w.Contains("invalid object"));
    }

    private static string? FindFolder(string namePart)
        => RealGameTests.SciGameFolders().Select(f => (string)f[0]).FirstOrDefault(f => f.Contains(namePart, StringComparison.OrdinalIgnoreCase)); // the data may sit in a subfolder (SQ6's CDROM)

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

/// <summary>A click as the Player does it headless: move, then press, then release six ticks apart.</summary>
internal static class HeadlessClicks
{
    public static void Click(HeadlessHost host, SciEngine engine, int x, int y)
    {
        host.Input.Mouse = new MouseState(x, y, false, false, false);
        host.Input.Push(InputEvent.MouseMove(x, y));
        host.Continue(engine, 6);
        host.Input.Mouse = new MouseState(x, y, true, false, false);
        host.Input.Push(InputEvent.MouseDown(MouseButton.Left, x, y));
        host.Continue(engine, 6);
        host.Input.Mouse = new MouseState(x, y, false, false, false);
        host.Input.Push(InputEvent.MouseUp(MouseButton.Left, x, y));
    }
}
