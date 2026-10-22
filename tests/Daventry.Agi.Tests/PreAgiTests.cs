using Daventry.Agi.PreAgi;
using Daventry.Core.Hosting;
using Daventry.Core.Input;
using Daventry.Core.Storage;
using Xunit.Abstractions;

namespace Daventry.Agi.Tests;

/// <summary>
/// Runs the preAGI games headlessly: Winnie the Pooh and Mickey's Space Adventure. Skipped when
/// the sierra-games folder is absent. The games log every string they draw at debug level, which
/// is what the content assertions read; the flow is driven by the engines' wait states.
/// </summary>
public class PreAgiTests(ITestOutputHelper output)
{
    private const string WinnieFolder = "Winnie_the_Pooh_-_In_the_Hundred_Acre_Wood";
    private const string MickeyFolder = "Mickeys_Space_Adventure";

    private sealed class Session : IDisposable
    {
        public List<string> Text { get; } = [];

        public List<string> Warnings { get; } = [];

        public required HeadlessHost Host { get; init; }

        public required PreAgiEngine Engine { get; init; }

        public void Run(int ticks) => Host.Continue(Engine, ticks);

        public void Press(Key key, int ticksAfter = 10, KeyModifiers modifiers = KeyModifiers.None)
        {
            char ascii = key switch
            {
                Daventry.Core.Input.Key.Return => (char)13,
                Daventry.Core.Input.Key.Escape => (char)27,
                Daventry.Core.Input.Key.Space => ' ',
                _ => default,
            };
            Host.Input.PushKey(key, modifiers, ascii);
            Run(ticksAfter);
        }

        /// <summary>Presses a key every few ticks until the condition holds. Returns false when it never did.</summary>
        public bool PressUntil(Func<bool> condition, Key key = Daventry.Core.Input.Key.Return, int maxPresses = 60, int ticksEach = 20)
        {
            for (int i = 0; i < maxPresses; i++)
            {
                if (condition())
                {
                    return true;
                }

                Press(key, ticksEach);
            }

            return condition();
        }

        /// <summary>At a menu and not inside a message or inventory screen opened from it.</summary>
        public bool AtMenu() => Engine.AwaitingMenuSelection && !Engine.AwaitingKey;

        public bool Drew(string fragment) => Text.Any(t => t.Contains(fragment, StringComparison.Ordinal));

        public void Dispose() => Engine.Dispose();
    }

    private sealed class SessionLog : ILog
    {
        public Session? Session { get; set; }

        public void Write(LogLevel level, string category, string message)
        {
            if (Session is null)
            {
                return;
            }

            if (category == PreAgiEngine.TextLogCategory)
            {
                Session.Text.Add(message);
            }
            else if (level >= LogLevel.Warning)
            {
                Session.Warnings.Add($"{category}: {message}");
            }
        }
    }

    private static string? GameFolder(string name)
        => RealGameTests.AllDetectedFolders().Select(args => (string)args[0]).FirstOrDefault(f => f.Length > 0 && Path.GetFileName(f) == name);

    private Session? Start(string folderName, int seed, ISaveStore? saves = null)
    {
        var folder = GameFolder(folderName);
        if (folder is null)
        {
            output.WriteLine($"{folderName} not found, skipping");
            return null;
        }

        var game = new DirectoryGameContainer(folder);
        var detected = (AgiDetectedGame)new AgiEngineProvider().Detect(game)[0];
        var log = new SessionLog();
        var host = new HeadlessHost(log, saves);
        var engine = (PreAgiEngine)new AgiEngineProvider().Create(host, game, detected);
        engine.Random = new Random(seed);
        var session = new Session { Host = host, Engine = engine };
        log.Session = session;
        host.Run(engine, 1);
        return session;
    }

    private void DumpText(Session s)
    {
        foreach (var line in s.Text.TakeLast(12))
        {
            output.WriteLine("  " + line);
        }

        foreach (var warning in s.Warnings.Distinct())
        {
            output.WriteLine("  warning: " + warning);
        }
    }

    // ---- Winnie the Pooh ----

    /// <summary>Through the intro (two waits and three songs a key cuts short) and the room texts to the playroom menu.</summary>
    private static void WinnieIntoThePlayroom(Session s)
    {
        s.Run(230);
        Assert.True(s.PressUntil(s.AtMenu), "the playroom menu never came up");
    }

    /// <summary>Opens the toy box: option 3, then three messages, then its own menu.</summary>
    private static void WinnieOpenTheToyBox(Session s)
    {
        s.Press(Key.D3, 30);
        Assert.True(s.PressUntil(() => s.AtMenu() && s.Drew("1. Save this game.")), "the toy box menu never came up");
    }

    [Fact]
    public void Winnie_IntroLeadsToThePlayroomMenu()
    {
        using var s = Start(WinnieFolder, seed: 1);
        if (s is null)
        {
            return;
        }

        s.Run(60);
        Assert.True(s.Drew("PRESENT"), "the logo screen's caption was not drawn");
        var frame = s.Host.Graphics.LastFrame;
        Assert.NotNull(frame);
        Assert.True(frame.Pixels.Count(p => p != 0) > 2000, "the logo picture was not drawn");

        WinnieIntoThePlayroom(s);
        DumpText(s);

        Assert.True(s.Drew("Christopher Robin's playroom"), "the playroom description was not drawn");
        Assert.True(s.Drew("1. Ask, \"What should I do?\""), "the playroom menu was not drawn");
        Assert.True(s.Drew("North  South  East  West"), "the directions were not drawn");
        Assert.Equal(28, ((WinnieEngine)s.Engine).Room);
        Assert.Empty(s.Warnings);
    }

    [Fact]
    public void Winnie_InventoryAndWalkingNorth()
    {
        using var s = Start(WinnieFolder, seed: 1);
        if (s is null)
        {
            return;
        }

        WinnieIntoThePlayroom(s);
        s.Press(Key.C, 20);
        Assert.True(s.Drew("You are carrying nothing."), "the inventory was not drawn");
        Assert.True(s.Drew("Number of objects still missing:  10"), "the missing count was not drawn");
        Assert.True(s.PressUntil(s.AtMenu), "the menu did not return after the inventory");

        s.Press(Key.N, 60);
        Assert.True(s.PressUntil(s.AtMenu), "the next room's menu never came up");
        DumpText(s);
        var winnie = (WinnieEngine)s.Engine;
        Assert.NotEqual(28, winnie.Room);
        Assert.Empty(s.Warnings);
    }

    [Fact]
    public void Winnie_SavesInTheToyBoxAndLoadsAgain()
    {
        var saves = new MemorySaveStore();
        using (var s = Start(WinnieFolder, seed: 1, saves))
        {
            if (s is null)
            {
                return;
            }

            WinnieIntoThePlayroom(s);
            WinnieOpenTheToyBox(s);

            // 1. Save this game; 1. Sure
            s.Press(Key.D1, 30);
            Assert.True(s.PressUntil(() => s.AtMenu() && s.Drew("1. Sure, that's OK.")), "the save confirmation never came up");
            s.Press(Key.D1, 30);
            Assert.True(s.PressUntil(s.AtMenu), "the playroom menu did not return after saving");
            DumpText(s);
            Assert.Contains("winnie.savegame", saves.List(string.Empty));
        }

        using (var s = Start(WinnieFolder, seed: 2, saves))
        {
            Assert.NotNull(s);
            WinnieIntoThePlayroom(s);
            WinnieOpenTheToyBox(s);

            // 2. Play my old game; 1. I know
            s.Press(Key.D2, 30);
            Assert.True(s.PressUntil(() => s.AtMenu() && s.Drew("1. I know.")), "the load confirmation never came up");
            s.Press(Key.D1, 30);
            Assert.True(s.PressUntil(s.AtMenu), "the playroom menu did not return after loading");
            DumpText(s);
            Assert.Equal(28, ((WinnieEngine)s.Engine).Room);
            Assert.Empty(s.Warnings);
        }
    }

    /// <summary>A save from the original interpreter, 116 bytes of little endian words, loads too.</summary>
    [Fact]
    public void Winnie_LoadsAnOriginalInterpreterSave()
    {
        var folder = GameFolder(WinnieFolder);
        if (folder is null || !File.Exists(Path.Combine(folder, "Savegame")))
        {
            output.WriteLine("original save not found, skipping");
            return;
        }

        var saves = new MemorySaveStore();
        using (var stream = saves.OpenWrite("winnie.savegame"))
        {
            stream.Write(File.ReadAllBytes(Path.Combine(folder, "Savegame")));
        }

        using var s = Start(WinnieFolder, seed: 3, saves);
        Assert.NotNull(s);
        WinnieIntoThePlayroom(s);
        WinnieOpenTheToyBox(s);
        s.Press(Key.D2, 30);
        Assert.True(s.PressUntil(() => s.AtMenu() && s.Drew("1. I know.")), "the load confirmation never came up");
        s.Press(Key.D1, 30);
        Assert.True(s.PressUntil(s.AtMenu), "the playroom menu did not return after loading");
        DumpText(s);

        var winnie = (WinnieEngine)s.Engine;
        Assert.Equal(28, winnie.Room);
        Assert.Equal(10, winnie.ObjectsMissing);
        Assert.Equal(0, winnie.ObjectHeld);
        Assert.Contains(s.Warnings, w => w.Contains("original interpreter"));
    }

    // ---- Mickey's Space Adventure ----

    /// <summary>Logo, title and copyright, theme, no saved game, the landing and the intro message, to the first menu.</summary>
    private static void MickeyIntoTheFirstRoom(Session s)
    {
        Assert.True(s.PressUntil(() => s.Drew("(Y/N)?")), "the load prompt never came up");
        s.Press(Key.N, 60);
        Assert.True(s.PressUntil(s.AtMenu), "the first room's menu never came up");
    }

    [Fact]
    public void Mickey_IntroLeadsToTheCountryRoadMenu()
    {
        using var s = Start(MickeyFolder, seed: 1);
        if (s is null)
        {
            return;
        }

        s.Run(10);
        var frame = s.Host.Graphics.LastFrame;
        Assert.NotNull(frame);
        Assert.True(frame.Pixels.Count(p => p != 0) > 2000, "the Sierra logo was not drawn");

        MickeyIntoTheFirstRoom(s);
        DumpText(s);

        Assert.True(s.Drew("COPYRIGHT 1986 THE WALT DISNEY COMPANY."), "the copyright was not drawn");
        Assert.True(s.Drew("MICKEY AND PLUTO ARE WALKING"), "the room description was not drawn");
        Assert.True(s.Drew("THE HILL TO THE NORTH!"), "the intro message was not drawn");
        Assert.True(s.Drew("LOOK") && s.Drew("GO") && s.Drew("NORTH"), "the menu was not drawn");
        var mickey = (MickeyEngine)s.Engine;
        Assert.Equal(13, mickey.Room);
        Assert.Equal(0, mickey.Planet);
        Assert.Empty(s.Warnings);
    }

    [Fact]
    public void Mickey_InventoryAndLookingAtTheHills()
    {
        using var s = Start(MickeyFolder, seed: 1);
        if (s is null)
        {
            return;
        }

        MickeyIntoTheFirstRoom(s);

        s.Press(Key.C, 20);
        Assert.True(s.Drew("MICKEY IS CARRYING THE FOLLOWING:"), "the inventory was not drawn");
        Assert.True(s.Drew("NO CRYSTALS"), "the crystal count was not drawn");
        Assert.True(s.PressUntil(s.AtMenu), "the menu did not return after the inventory");

        // GO HILLS: GO is the second word of the first row, HILLS the third of the second
        s.Press(Key.Right, 20);
        s.Press(Key.Return, 20);
        s.Press(Key.Right, 20);
        s.Press(Key.Right, 20);
        s.Press(Key.Return, 20);
        s.Press(Key.Return, 60);
        DumpText(s);
        Assert.True(s.Drew("MICKEY IS ALREADY IN THE HILLS."), "the game did not answer the command");
        Assert.Empty(s.Warnings);
    }

    [Fact]
    public void Mickey_GoesEastAndSavesAndLoads()
    {
        var saves = new MemorySaveStore();
        int roomAfterMove;
        using (var s = Start(MickeyFolder, seed: 1, saves))
        {
            if (s is null)
            {
                return;
            }

            MickeyIntoTheFirstRoom(s);

            // GO EAST
            s.Press(Key.Right, 20);
            s.Press(Key.Return, 20);
            s.Press(Key.Right, 20);
            s.Press(Key.Return, 20);
            s.Press(Key.Return, 60);
            Assert.True(s.PressUntil(s.AtMenu), "the next room's menu never came up");
            DumpText(s);

            var mickey = (MickeyEngine)s.Engine;
            roomAfterMove = mickey.Room;
            Assert.NotEqual(13, roomAfterMove);
            Assert.Empty(s.Warnings);

            using var stream = saves.OpenWrite("mickey.s01");
            mickey.WriteSaveGame(stream);
        }

        // the intro offers to continue a saved game: yes, slot 1
        using (var s = Start(MickeyFolder, seed: 2, saves))
        {
            Assert.NotNull(s);
            Assert.True(s.PressUntil(() => s.Drew("(Y/N)?")), "the load prompt never came up");
            s.Press(Key.Y, 30);
            s.Press(Key.D1, 60);
            Assert.True(s.PressUntil(s.AtMenu), "the restored room's menu never came up");
            DumpText(s);

            Assert.Equal(roomAfterMove, ((MickeyEngine)s.Engine).Room);
            Assert.Empty(s.Warnings);
        }
    }
}
