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
