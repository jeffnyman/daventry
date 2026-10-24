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

    // ---- Troll's Tale ----

    private const string TrollFolder = "Trolls_Tale";

    /// <summary>Through the logo, "have you played this game before?" (yes) and the credits to the first room.</summary>
    private static void TrollIntoTheFirstRoom(Session s)
    {
        s.Run(400);
        s.Press(Key.Space, 30);
        Assert.True(s.Drew("HAVE YOU PLAYED THIS GAME BEFORE ?"), "the question was not drawn");
        s.Press(Key.Y, 30);
        Assert.True(s.PressUntil(s.AtMenu, Key.Space), "the first room's menu never came up");
    }

    /// <summary>Moves the pointer to an option (the space bar steps through them), picks it, and answers any messages.</summary>
    private static void TrollChoose(Session s, int option)
    {
        for (int i = 1; i < option; i++)
        {
            s.Press(Key.Space, 5);
        }

        s.Press(Key.Return, 30);
        Assert.True(s.PressUntil(s.AtMenu, Key.Space), "the menu did not come back");
    }

    /// <summary>Pixels of the Troll's line colour in the picture area.</summary>
    private static int TrollColoredPixels(Session s)
    {
        var frame = s.Host.Graphics.LastFrame;
        Assert.NotNull(frame);
        int pictureRows = frame.Height * 168 / 200;
        return frame.Pixels.Take(frame.Width * pictureRows).Count(p => p == 11);
    }

    [Fact]
    public void Troll_IntroLeadsToTheFirstRoom()
    {
        using var s = Start(TrollFolder, seed: 1);
        if (s is null)
        {
            return;
        }

        s.Run(100);
        Assert.True(s.Drew("SIERRA ON-LINE INC."), "the presents screen was not drawn");

        s.Run(300);
        Assert.True(s.Drew("Copyright 1984 Sierra On-Line Inc."), "the logo's caption was not drawn");
        var frame = s.Host.Graphics.LastFrame;
        Assert.NotNull(frame);
        Assert.True(frame.Pixels.Distinct().Count() > 6, "the logo picture was not drawn");

        TrollIntoTheFirstRoom(s);
        DumpText(s);

        Assert.True(s.Drew("Written by MIKE MACCHESNEY"), "the credits were not drawn");
        Assert.True(s.Drew(" You are facing a dark cave."), "the room description was not drawn");
        Assert.True(s.Drew("1.GO into the CAVE."), "the menu was not drawn");
        var troll = (TrollEngine)s.Engine;
        Assert.Equal(0, troll.CurrentRoom);
        Assert.Equal(16, troll.TreasuresLeft);
        Assert.Empty(s.Warnings);
    }

    [Fact]
    public void Troll_TheTutorialTeachesTheMenus()
    {
        using var s = Start(TrollFolder, seed: 1);
        if (s is null)
        {
            return;
        }

        s.Run(400);
        s.Press(Key.Space, 30);
        s.Press(Key.N, 30);
        Assert.True(s.Drew("First press the <space bar>."), "the tutorial did not start");

        s.Press(Key.Space, 30);
        Assert.True(s.AtMenu(), "the practice menu did not come up");

        // turn the screen green, then pick the surprise
        s.Press(Key.Return, 30);
        Assert.True(s.AtMenu(), "the practice menu did not come back");
        s.Press(Key.Space, 5);
        s.Press(Key.Space, 5);
        s.Press(Key.Return, 30);
        Assert.True(s.Drew("Would you like more practice ?"), "the practice question was not drawn");

        s.Press(Key.N, 30);
        Assert.True(s.PressUntil(s.AtMenu, Key.Space), "the game never started after the tutorial");
        DumpText(s);

        Assert.True(s.Drew("Treasures of MARK, the Dwarf King."), "the story was not told");
        Assert.True(s.Drew("The TROLL has hidden these Treasures:"), "the treasures were not listed");
        Assert.True(s.Drew("Written by MIKE MACCHESNEY"), "the credits were not drawn");
        Assert.Empty(s.Warnings);
    }

    [Fact]
    public void Troll_TakingTheFlashlightAndListingTheTreasures()
    {
        using var s = Start(TrollFolder, seed: 1);
        if (s is null)
        {
            return;
        }

        TrollIntoTheFirstRoom(s);
        var troll = (TrollEngine)s.Engine;

        s.Press(Key.F, 20);
        Assert.True(s.Drew("TREASURES FOUND") && s.Drew("NONE"), "the empty treasure list was not drawn");
        Assert.True(s.PressUntil(s.AtMenu, Key.Space), "the menu did not return after the treasure list");
        Assert.Equal(0, troll.Moves);

        TrollChoose(s, 2); // Go SOUTH into the field
        Assert.Equal(2, troll.CurrentRoom);
        Assert.True(s.Drew("There is a flashlight in this field."), "the field was not described");

        TrollChoose(s, 1); // TAKE the FLASHLIGHT
        DumpText(s);
        Assert.True(s.Drew("THERE ARE STILL 15 TREASURES TO FIND"), "the treasure count was not drawn");
        Assert.Equal(15, troll.TreasuresLeft);
        Assert.Equal(2, troll.Moves);

        s.Press(Key.F, 20);
        Assert.True(s.Drew("15 TREASURES TO FIND"), "the treasure list did not count the flashlight");
        Assert.True(s.PressUntil(s.AtMenu, Key.Space), "the menu did not return after the treasure list");
        Assert.Empty(s.Warnings);
    }

    /// <summary>
    /// The Troll appears at random (one time in three) in the rooms past the guard. While it is
    /// in the picture its treasure cannot be taken; walking away and back makes it leave.
    /// </summary>
    [Fact]
    public void Troll_TheTrollGuardsTheTreasureUntilThePictureChanges()
    {
        using var s = Start(TrollFolder, seed: 1);
        if (s is null)
        {
            return;
        }

        TrollIntoTheFirstRoom(s);
        var troll = (TrollEngine)s.Engine;

        TrollChoose(s, 2); // Go SOUTH into the field
        TrollChoose(s, 1); // TAKE the FLASHLIGHT
        TrollChoose(s, 1); // Go to the mountains in the WEST
        Assert.Equal(4, troll.CurrentRoom);
        TrollChoose(s, 2); // GO to a FIELD near the cave
        TrollChoose(s, 2); // GO toward the CAVE
        Assert.Equal(0, troll.CurrentRoom);
        TrollChoose(s, 2); // Turn ON FLASHLIGHT and ENTER CAVE
        Assert.Equal(6, troll.CurrentRoom);
        TrollChoose(s, 1); // Go to the tunnel in the WEST
        TrollChoose(s, 3); // Go through the door on the LEFT
        Assert.Equal(10, troll.CurrentRoom);
        TrollChoose(s, 1); // Go up the LEFT stairs
        Assert.Equal(15, troll.CurrentRoom);

        // down the road to the money and back up again until the Troll is there
        int noTroll = -1;
        bool sawTroll = false;
        for (int i = 0; i < 20 && !sawTroll; i++)
        {
            TrollChoose(s, 1); // GO down the ROAD
            Assert.Equal(18, troll.CurrentRoom);
            if (troll.IsTrollAway)
            {
                noTroll = TrollColoredPixels(s);
                TrollChoose(s, 2); // GO back up the ROAD
                continue;
            }

            sawTroll = true;
        }

        Assert.True(sawTroll, "the Troll never came");
        Assert.True(TrollColoredPixels(s) > noTroll + 500, "the Troll was not drawn");

        TrollChoose(s, 1); // TAKE the MONEY
        Assert.True(s.Drew("THE TROLL WON'T LET YOU TAKE IT!!!"), "the Troll did not stop the take");
        Assert.Equal(16 - 1, troll.TreasuresLeft);

        while (!troll.IsTrollAway)
        {
            TrollChoose(s, 2); // GO back up the ROAD
            TrollChoose(s, 1); // GO down the ROAD
        }

        TrollChoose(s, 1); // TAKE the MONEY
        DumpText(s);
        Assert.True(s.Drew("You now have a dollar and a cent. You"), "the money was not taken");
        Assert.Equal(16 - 2, troll.TreasuresLeft);
        Assert.Empty(s.Warnings);
    }
}
