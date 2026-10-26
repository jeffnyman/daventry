using Daventry.Core.Storage;
using Xunit.Abstractions;

namespace Daventry.Adl.Tests;

/// <summary>The seven Hi-Res Adventures played headless. Skipped without the game folders.</summary>
public sealed class GameTests(ITestOutputHelper output)
{
    /// <summary>What each game needs typed before its first room, and that room's description.</summary>
    public static TheoryData<int, string[], string> FirstRooms => new()
    {
        { 0, [], "YOU ARE IN FRONT OF A BUILDING." },
        { 1, ["key: ", "G"], "YOU ARE IN THE FRONT YARD OF A LARGE" },
        { 2, [], "YOU ARE IN THE VILLAGE OF SERENIA." },
        { 3, [], "YOU ARE STANDING ON MAIN STREET LOOKING" },
        { 4, ["key:2", "key: "], "YOU ARE ON A 3-WAY ROAD IN A SMALL TOWN" },
        { 5, ["key: ", "1"], "YOU ARE IN FRONT OF YOUR OWN HOUSE." },
        { 6, [], "JEN IS IN A BEAUTIFUL MOUNTAIN VALLEY." },
    };

    [Theory]
    [MemberData(nameof(FirstRooms))]
    public void ReachesTheFirstRoomWithItsPicture(int number, string[] start, string description)
    {
        var folder = AdlGames.Folder(number);
        if (folder is null)
        {
            return;
        }

        using var run = Start(folder, start);

        Assert.Contains(description, run.Screen);
        Assert.Contains("ENTER COMMAND?", run.Screen);

        // the picture area holds a drawing: more than one colour in the top 160 lines (in true
        // colour, with ScummVM's NTSC emulation on as by default)
        var trueColor = run.Engine.Display.TrueColorScreen;
        Assert.NotNull(trueColor);
        var colors = trueColor.Pixels.Take(DisplayA2.ScreenWidth * 320).Distinct().Count();
        Assert.True(colors > 1, "the picture is blank");
        Assert.Empty(run.Warnings);
    }

    [Theory]
    [InlineData(0, new string[0], "LOOK", "I SEE NOTHING SPECIAL.")]
    [InlineData(1, new[] { "key: ", "G" }, "NORTH", "PORCH")]
    [InlineData(2, new string[0], "INVENTORY", "A POCKET KNIFE")]
    [InlineData(3, new string[0], "LOOK", "THERE IS NOTHING SPECIAL TO SEE.")]
    [InlineData(4, new[] { "key:2", "key: " }, "LOOK", "YOU SEE NOTHING SPECIAL.")]
    [InlineData(5, new[] { "key: ", "1" }, "LOOK", "AS YOU AWAKEN FROM A HEAVY NIGHT'S")]
    [InlineData(6, new string[0], "LOOK", "URSU, WISEST OF OUR RACE, IS")]
    public void AnswersACommand(int number, string[] start, string command, string answer)
    {
        var folder = AdlGames.Folder(number);
        if (folder is null)
        {
            return;
        }

        using var run = Start(folder, start);
        string printed = run.Command(command);
        output.WriteLine(printed);

        Assert.Contains(answer, printed);
    }

    [Fact]
    public void UnknownWordsGetTheParsersMessage()
    {
        var folder = AdlGames.Folder(2);
        if (folder is null)
        {
            return;
        }

        using var run = Start(folder, []);
        string printed = run.Command("XYZZY");

        Assert.Contains("XYZZY", printed);
        Assert.Equal(1, run.Engine.GameState.Moves);
    }

    /// <summary>
    /// Saving through the menu path (what ScummVM's menu and the Player's --save use) and restoring
    /// brings the state back, for each engine generation: v1 Mystery House, v2 Wizard, v4 Time
    /// Zone (regions reloaded from disk) and v5 The Dark Crystal.
    /// </summary>
    /// <para>
    /// The menu save follows ScummVM's rule, accepted only when the game would take "SAVE GAME"
    /// then: in the opening rooms of Time Zone and The Dark Crystal a catch-all command answers
    /// the first thing typed, so there the save waits for one move.
    /// </para>
    [Theory]
    [InlineData(1, new[] { "key: ", "G" }, "NORTH", false, "next")]
    [InlineData(2, new string[0], "LOOK", false, "next")]
    [InlineData(5, new[] { "key: ", "1" }, "LOOK", true, "reset")]
    [InlineData(6, new string[0], "LOOK", true, "same")]
    public void SavesAndRestoresThroughTheMenu(int number, string[] start, string command, bool refusedAtFirst, string moves)
    {
        var folder = AdlGames.Folder(number);
        if (folder is null)
        {
            return;
        }

        using var run = Start(folder, start);
        if (refusedAtFirst)
        {
            Assert.False(run.Engine.SaveGame(3, "too early"));
            run.Command(command);
        }

        var state = run.Engine.GameState;
        byte room = state.Room;
        int savedMoves = state.Moves;

        Assert.True(run.Engine.SaveGame(3, "test save"));
        var file = run.Saves.OpenRead(run.Engine.GetSaveStateName(3));
        Assert.NotNull(file);
        using (file)
        {
            var header = new byte[5];
            file.ReadExactly(header);
            Assert.Equal("ADL:"u8.ToArray(), header[..4]);
            Assert.Equal(0, header[4]);
        }

        run.Command(command);
        run.Command(command);
        Assert.True(run.Engine.GameState.Moves > savedMoves || run.Engine.GameState.Room != room);

        run.Engine.RequestRestore(3);
        Assert.True(run.RunUntilPrompt());

        // v1 and v2 run the global commands once after a restore and count a move; v4 and v5
        // jump back to the game loop. Time Zone's saves keep no move counter, so it starts again
        // at 1; The Dark Crystal keeps it in two variables. All as in ScummVM.
        Assert.Equal(room, run.Engine.GameState.Room);
        int expected = moves switch { "next" => savedMoves + 1, "reset" => 1, _ => savedMoves };
        Assert.Equal(expected, run.Engine.GameState.Moves);
    }

    [Fact]
    public void TheGamesOwnSaveAndRestoreCommandsWork()
    {
        var folder = AdlGames.Folder(2);
        if (folder is null)
        {
            return;
        }

        using var run = Start(folder, []);

        Assert.Contains("ENTER A LETTER FROM A-O", run.Command("SAVE GAME"));
        Assert.Contains("PRESS RETURN", run.Command("B"));
        run.Command(string.Empty);
        Assert.NotNull(run.Saves.OpenRead("hires2.s01"));

        // drop the knife, then restore: the knife is carried again
        run.Command("DROP KNIFE");
        Assert.DoesNotContain("A POCKET KNIFE", run.Command("INVENTORY"));

        run.Command("RESTORE GAME");
        run.Command("B");
        run.Command(string.Empty);
        Assert.Contains("A POCKET KNIFE", run.Command("INVENTORY"));
    }

    [Fact]
    public void RunsAreDeterministic()
    {
        var folder = AdlGames.Folder(6);
        if (folder is null)
        {
            return;
        }

        byte[] Play()
        {
            using var run = Start(folder, []);
            run.Command("LOOK");
            run.Command("SOUTH");
            run.Command("INVENTORY");
            var pixels = run.Engine.Display.TrueColorScreen!.Pixels.SelectMany(BitConverter.GetBytes);
            return [.. pixels, .. System.Text.Encoding.ASCII.GetBytes(run.Screen)];
        }

        Assert.Equal(Play(), Play());
    }

    [Fact]
    public void TheTextWindowWaitsForReturnWhenItFills()
    {
        var folder = AdlGames.Folder(5);
        if (folder is null)
        {
            return;
        }

        using var run = Start(folder, ["key: ", "1"]);
        Assert.True(run.RunUntilInput());
        run.Type("LOOK");
        Assert.True(run.RunUntilInput());

        // the long opening description stops after four lines and rings
        Assert.True(run.Engine.InTextOverflow);
        run.Type("X", enter: false);
        Assert.True(run.RunUntilInput());
        Assert.True(run.Engine.InTextOverflow, "a key other than Return must not page on");

        run.Type(string.Empty);
        Assert.True(run.RunUntilInput());
        Assert.Contains("SIGNIFICANT PORTIONS OF IT", run.Screen);
    }

    private static AdlRunner Start(string folder, string[] start, ISaveStore? saves = null)
    {
        var run = new AdlRunner(folder, saves);
        foreach (string text in start)
        {
            Assert.True(run.RunUntilInput(4000), $"no prompt before '{text}': {run.Screen}");
            // "key:X" presses one key, for menus and "press any key" screens; anything else is a line
            if (text.StartsWith("key:", StringComparison.Ordinal))
            {
                run.Type(text[4..], enter: false);
            }
            else
            {
                run.Type(text);
            }
        }

        Assert.True(run.RunUntilPrompt(4000), $"no first prompt: {run.Screen}");
        return run;
    }
}
