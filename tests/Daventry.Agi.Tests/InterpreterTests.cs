using Daventry.Core.Graphics;
using Daventry.Core.Hosting;
using Daventry.Core.Input;
using Daventry.Core.Storage;
using Xunit.Abstractions;

namespace Daventry.Agi.Tests;

/// <summary>Runs real game scripts headlessly. Skipped when the sierra-games folder is absent.</summary>
public class InterpreterTests(ITestOutputHelper output)
{
    private static string? GameFolder(string name)
    {
        var folder = RealGameTests.AgiGameFolders()
            .Select(args => (string)args[0])
            .FirstOrDefault(f => f.Length > 0 && Path.GetFileName(f) == name);
        return folder;
    }

    private static (HeadlessHost Host, AgiEngine Engine) Start(string folder)
    {
        var game = new DirectoryGameContainer(folder);
        var detected = (AgiDetectedGame)new AgiEngineProvider().Detect(game)[0];
        var host = new HeadlessHost();
        var engine = new AgiEngine(host, game, detected) { Random = new Random(1) };
        engine.Initialize();
        return (host, engine);
    }

    private static void Run(HeadlessHost host, AgiEngine engine, int ticks, int enterEvery = 0)
    {
        for (int tick = 1; tick <= ticks; tick++)
        {
            if (enterEvery > 0 && tick % enterEvery == 0)
            {
                host.Input.PushKey(Key.Return, ascii: '\r');
            }

            var result = engine.Tick(tick == 1 ? TimeSpan.Zero : host.TickInterval);
            Assert.True(result.Continue, $"engine quit at tick {tick}");
        }
    }

    [Fact]
    public void KingsQuest1_ReachesTheFirstRoomWithEgoDrawn()
    {
        var folder = GameFolder("King's Quest 1 (AGI)");
        if (folder is null)
        {
            output.WriteLine("game not found, skipping");
            return;
        }

        var (host, engine) = Start(folder);
        Run(host, engine, 1500, enterEvery: 60);

        var ego = engine.State.ScreenObjTable[AgiGameState.ScreenObjectsEgoEntry];
        output.WriteLine($"room {engine.GetVar(AgiVar.CurrentRoom)}, ego at {ego.XPos},{ego.YPos} view {ego.CurrentViewNr}, instructions {engine.InstructionCounter}");

        Assert.Equal(1, engine.GetVar(AgiVar.CurrentRoom));
        Assert.True(engine.State.PictureShown);
        Assert.True(ego.HasFlags(ViewFlags.Animated | ViewFlags.Drawn));
        Assert.True(engine.Text.PromptIsEnabled());
        Assert.True(engine.Text.StatusEnabled);
        Assert.NotNull(host.Graphics.LastFrame);
    }

    [Fact]
    public void ShakeScreen_PresentsTheDisplayShifted()
    {
        var folder = GameFolder("King's Quest 1 (AGI)");
        if (folder is null)
        {
            output.WriteLine("game not found, skipping");
            return;
        }

        var (host, engine) = Start(folder);
        Run(host, engine, 1500, enterEvery: 60);

        // the shake offset the opcode sets reaches the host as a shifted copy with a black edge
        engine.Gfx.ShakeOffset = engine.Gfx.ShakePixels;
        engine.Gfx.DisplayDirty = true;
        engine.Tick(host.TickInterval);
        var expected = new Surface8(engine.Gfx.Display.Width, engine.Gfx.Display.Height);
        expected.CopyFromShifted(engine.Gfx.Display, 8, 4);
        Assert.Equal((8, 4), engine.Gfx.ShakePixels);
        Assert.Equal(expected.Pixels, host.Graphics.LastFrame!.Pixels);

        engine.Gfx.ShakeOffset = (0, 0);
        engine.Gfx.DisplayDirty = true;
        engine.Tick(host.TickInterval);
        Assert.Equal(engine.Gfx.Display.Pixels, host.Graphics.LastFrame!.Pixels);
    }

    [Fact]
    public void KingsQuest1_LookOpensAMessageBox()
    {
        var folder = GameFolder("King's Quest 1 (AGI)");
        if (folder is null)
        {
            return;
        }

        var (host, engine) = Start(folder);
        Run(host, engine, 1100, enterEvery: 60);
        Assert.True(engine.Text.PromptIsEnabled());

        foreach (char c in "look")
        {
            host.Input.PushKey(Key.None, ascii: c);
        }

        host.Input.PushKey(Key.Return, ascii: '\r');
        Run(host, engine, 30);

        Assert.True(engine.Text.MessageState.WindowActive, "no message box after 'look'");
        Assert.Equal("look", engine.Text.PromptPrevious);
    }

    [Fact]
    public void KingsQuest1_ArrowKeyMovesEgo()
    {
        var folder = GameFolder("King's Quest 1 (AGI)");
        if (folder is null)
        {
            return;
        }

        var (host, engine) = Start(folder);
        Run(host, engine, 1100, enterEvery: 60);

        var ego = engine.State.ScreenObjTable[AgiGameState.ScreenObjectsEgoEntry];
        int startX = ego.XPos;
        host.Input.PushKey(Key.Right);
        Run(host, engine, 120);

        output.WriteLine($"ego moved from x={startX} to x={ego.XPos}");
        Assert.True(ego.XPos > startX, "ego did not walk right");
    }

    [Fact]
    public void KingsQuest1_EscapeOpensTheMenuAndHelpShowsAMessage()
    {
        var folder = GameFolder("King's Quest 1 (AGI)");
        if (folder is null)
        {
            return;
        }

        var (host, engine) = Start(folder);
        Run(host, engine, 1100, enterEvery: 60);
        Assert.True(engine.Menu.IsAvailable());
        Assert.Equal(["Info", "File", "Game", "Action", "Special", "Speed"], engine.Menu.Menus.Select(m => m.Text.Trim()).ToArray());

        host.Input.PushKey(Key.Escape, ascii: '\u001b');
        Run(host, engine, 5);
        Assert.True(engine.CycleInnerLoopIsActive, "menu did not open");
        Assert.Equal(CycleInnerLoopType.MenuViaKeyboard, engine.State.CycleInnerLoopType);
        Assert.Equal(0, engine.Menu.DrawnMenuNr);

        host.Input.PushKey(Key.Right);
        Run(host, engine, 3);
        Assert.Equal(1, engine.Menu.DrawnMenuNr);

        host.Input.PushKey(Key.Escape, ascii: '\u001b');
        Run(host, engine, 5);
        Assert.False(engine.CycleInnerLoopIsActive, "Escape did not close the menu");

        // Info > Help is the second item of the first menu
        host.Input.PushKey(Key.Escape, ascii: '\u001b');
        Run(host, engine, 5);
        host.Input.PushKey(Key.Down);
        Run(host, engine, 3);
        host.Input.PushKey(Key.Return, ascii: '\r');
        Run(host, engine, 30);

        Assert.False(engine.CycleInnerLoopIsActive && engine.State.CycleInnerLoopType == CycleInnerLoopType.MenuViaKeyboard, "menu still open after Enter");
        Assert.True(engine.Text.MessageState.WindowActive, "Help did not open a message box");
    }

    [Fact]
    public void KingsQuest1_TabShowsTheInventoryScreenAndAKeyReturns()
    {
        var folder = GameFolder("King's Quest 1 (AGI)");
        if (folder is null)
        {
            return;
        }

        var (host, engine) = Start(folder);
        Run(host, engine, 1100, enterEvery: 60);

        host.Input.PushKey(Key.Tab, ascii: '\t');
        Run(host, engine, 10);

        Assert.False(engine.State.GfxMode, "status did not switch to text mode");
        var item = Assert.Single(engine.Inventory.Items);
        Assert.Equal("nothing", item.Name);
        Assert.Equal(-1, engine.Inventory.ActiveItemNr);

        // black text on a white page: the frame is mostly white with some black, nothing else
        var frame = host.Graphics.LastFrame!;
        var counts = new int[256];
        foreach (byte pixel in frame.Pixels)
        {
            counts[pixel]++;
        }

        Assert.True(counts[15] > frame.Pixels.Length / 2, "page is not white");
        Assert.True(counts[0] > 0, "no black text");
        Assert.Equal(frame.Pixels.Length, counts[0] + counts[15]);

        host.Input.PushKey(Key.Space, ascii: ' ');
        Run(host, engine, 10);
        Assert.True(engine.State.GfxMode, "did not return to the game");
    }

    [Fact]
    public async Task KingsQuest1_SaveAndRestoreRoundTripsTheGameState()
    {
        var folder = GameFolder("King's Quest 1 (AGI)");
        if (folder is null)
        {
            return;
        }

        var (host, engine) = Start(folder);
        Run(host, engine, 1100, enterEvery: 60);
        host.Input.PushKey(Key.Right);
        Run(host, engine, 60);

        var ego = engine.State.ScreenObjTable[AgiGameState.ScreenObjectsEgoEntry];
        int savedX = ego.XPos;
        int savedY = ego.YPos;
        byte savedScore = engine.GetVar(AgiVar.Score);

        Assert.True(engine.SaveGame(3, "by the castle"));

        // the file is in ScummVM's layout: tag, description, version 11, thumbnail block
        using (var stream = host.Saves.OpenRead(engine.GetSaveStateName(3))!)
        {
            var header = new byte[4 + 31 + 1 + 4];
            stream.ReadExactly(header);
            Assert.Equal("AGI:", System.Text.Encoding.ASCII.GetString(header, 0, 4));
            Assert.Equal("by the castle", System.Text.Encoding.ASCII.GetString(header, 4, 13));
            Assert.Equal(AgiEngine.SaveGameCurrentVersion, header[35]);
            Assert.Equal("THMB", System.Text.Encoding.ASCII.GetString(header, 36, 4));
        }

        Assert.True(engine.GetSavegameInformation(3, out var description, out uint date, out _, out bool valid));
        Assert.True(valid);
        Assert.Equal("by the castle", description);
        Assert.Equal((uint)DateTime.Now.Year, date >> 16);
        Assert.Equal([3], engine.GetSavegameSlotIds());

        // walk away, then restore
        host.Input.PushKey(Key.Left);
        Run(host, engine, 90);
        Assert.NotEqual(savedX, ego.XPos);

        Assert.True(await engine.DoLoadAsync(3, false));

        // ego was still walking right when saved, so compare before the game runs on
        output.WriteLine($"saved at {savedX},{savedY}, restored at {ego.XPos},{ego.YPos}");
        Assert.Equal(savedX, ego.XPos);
        Assert.Equal(savedY, ego.YPos);
        Assert.Equal(savedScore, engine.GetVar(AgiVar.Score));
        Assert.Equal(1, engine.GetVar(AgiVar.CurrentRoom));
        Assert.True(engine.State.PictureShown);
        Assert.True(ego.HasFlags(ViewFlags.Animated | ViewFlags.Drawn));

        Run(host, engine, 30);
        Assert.True(ego.XPos > savedX, "ego did not keep walking after the restore");
    }

    [Fact]
    public void KingsQuest1_SaveAndRestoreThroughTheGamesOwnDialogs()
    {
        var folder = GameFolder("King's Quest 1 (AGI)");
        if (folder is null)
        {
            return;
        }

        var (host, engine) = Start(folder);
        Run(host, engine, 1100, enterEvery: 60);
        var ego = engine.State.ScreenObjTable[AgiGameState.ScreenObjectsEgoEntry];
        int savedX = ego.XPos;

        // F5 is Save Game: pick the first slot, describe it, confirm
        host.Input.PushKey(Key.F5);
        Run(host, engine, 10);
        Assert.Equal(CycleInnerLoopType.SystemUiSelectSavedGameSlot, engine.State.CycleInnerLoopType);
        Assert.True(engine.CycleInnerLoopIsActive, "slot chooser did not open");
        Assert.Equal(1, engine.SystemUI.SavedGames[engine.SystemUI.SavedGameSelectedSlotNr].SlotId);

        host.Input.PushKey(Key.Return, ascii: '\r');
        Run(host, engine, 10);
        Assert.Equal(CycleInnerLoopType.GetString, engine.State.CycleInnerLoopType);
        foreach (char c in "quest")
        {
            host.Input.PushKey(Key.None, ascii: c);
        }

        host.Input.PushKey(Key.Return, ascii: '\r');
        Run(host, engine, 10);
        Assert.Equal(CycleInnerLoopType.SystemUiVerification, engine.State.CycleInnerLoopType);
        host.Input.PushKey(Key.Return, ascii: '\r');
        Run(host, engine, 30);

        Assert.False(engine.CycleInnerLoopIsActive);
        Assert.Equal([1], engine.GetSavegameSlotIds());
        Assert.True(engine.GetSavegameInformation(1, out var description, out _, out _, out _));
        Assert.Equal("quest", description);

        host.Input.PushKey(Key.Right);
        Run(host, engine, 90);
        Assert.NotEqual(savedX, ego.XPos);

        // F7 is Restore Game: pick the slot, confirm
        host.Input.PushKey(Key.F7);
        Run(host, engine, 10);
        Assert.Equal(CycleInnerLoopType.SystemUiSelectSavedGameSlot, engine.State.CycleInnerLoopType);
        host.Input.PushKey(Key.Return, ascii: '\r');
        Run(host, engine, 10);
        Assert.Equal(CycleInnerLoopType.SystemUiVerification, engine.State.CycleInnerLoopType);
        host.Input.PushKey(Key.Return, ascii: '\r');
        Run(host, engine, 30);

        output.WriteLine($"saved at x={savedX}, restored at x={ego.XPos}");
        Assert.Equal(savedX, ego.XPos);
        Assert.True(engine.Text.PromptIsEnabled());
    }

    [Fact]
    public void SpaceQuest2_IntroRunsIntoTheGame()
    {
        var folder = GameFolder("Space Quest 2");
        if (folder is null)
        {
            return;
        }

        var (host, engine) = Start(folder);
        Run(host, engine, 2000, enterEvery: 90);

        output.WriteLine($"room {engine.GetVar(AgiVar.CurrentRoom)}, score {engine.GetVar(AgiVar.Score)} of {engine.GetVar(AgiVar.MaxScore)}");
        Assert.True(engine.State.PictureShown);
        Assert.Equal(250, engine.GetVar(AgiVar.MaxScore));
    }
}
