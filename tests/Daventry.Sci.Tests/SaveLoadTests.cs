using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Daventry.Sci.Engine;
using Xunit.Abstractions;

namespace Daventry.Sci.Tests;

/// <summary>
/// Saving and restoring through the engine: a game runs, is saved to the host's store, runs
/// on, and is restored at the next safe point. The real-game tests skip without the library.
/// </summary>
public class SaveLoadTests(ITestOutputHelper output)
{
    // the game, how often Enter is pressed to get past its title screens, and the tick to save at;
    // KQ6 CD's title room has no picture of its own, so a save made there cannot be replayed
    // (ScummVM's Game::replay fails the same way), which is why the SCI1.1 case is QFG1 VGA
    private static readonly (string Name, int EnterEvery, int SaveTick)[] Games =
    [
        ("King's Quest 1 (SCI)", 0, 900), // SCI01: the script string heap and SCI0 object layout
        ("King's Quest 5 (CD)", 0, 900), // SCI1: clones, lists, a running music play list
        ("Quest for Glory 1 (VGA)", 150, 2400), // SCI1.1: the heap string block, past the character screens
    ];

    public static IEnumerable<object[]> GameNames() => Games.Select(g => new object[] { g.Name, g.EnterEvery, g.SaveTick });

    [Theory]
    [MemberData(nameof(GameNames))]
    public void SaveAndRestoreRoundTrip(string gameName, int enterEvery, int saveTick)
    {
        var folder = FindFolder(gameName);
        if (folder is null)
        {
            output.WriteLine($"{gameName} not found, skipping");
            return;
        }

        var warnings = new List<string>();
        var saves = new MemorySaveStore();
        var host = new HeadlessHost(new ListLog(warnings), saves);
        var provider = new SciEngineProvider();
        var game = new DirectoryGameContainer(folder);
        var detected = (SciDetectedGame)provider.Detect(game)[0];
        using var engine = (SciEngine)provider.Create(host, game, detected);
        engine.Initialize();

        Run(engine, host, 1, saveTick, enterEvery);
        var state = engine.State!;
        ushort roomAtSave = state.CurrentRoomNumber;
        Assert.True(engine.SaveGame(1, "round trip"), "saving failed");

        var listed = engine.ListSavegames();
        Assert.Single(listed);
        Assert.Equal(1, listed[0].Id);
        Assert.Equal("round trip", listed[0].Name);
        Assert.Equal(Savegame.CurrentVersion, listed[0].Version);
        output.WriteLine($"{gameName}: saved in room {roomAtSave}, {saves.List(string.Empty)[0]}");

        Run(engine, host, saveTick + 1, saveTick + 300, enterEvery);
        engine.RequestRestore(1);
        Run(engine, host, saveTick + 301, saveTick + 900, enterEvery);

        Assert.Equal(roomAtSave, state.CurrentRoomNumber);
        Assert.Equal(1, state.LastRestoredSaveId);
        Assert.NotNull(host.Graphics.LastFrame);
        output.WriteLine($"{gameName}: restored, room {state.CurrentRoomNumber}, {warnings.Count} warnings");
        foreach (var warning in warnings.Distinct().Take(10))
        {
            output.WriteLine("  " + warning);
        }

        Assert.DoesNotContain(warnings, w => w.Contains("sci.save"));
    }

    /// <summary>Every object's properties, by segment and offset, including clones.</summary>
    private static Dictionary<(int Segment, int Offset), RegT[]> Snapshot(EngineState s)
    {
        var result = new Dictionary<(int, int), RegT[]>();
        var segments = s.SegMan.Segments;
        for (int seg = 1; seg < segments.Count; seg++)
        {
            switch (segments[seg])
            {
                case Script script:
                    foreach (var obj in script.Objects.Values)
                    {
                        result[(seg, obj.Pos.Offset)] = Enumerable.Range(0, obj.VarCount).Select(obj.GetVariable).ToArray();
                    }

                    break;
                case CloneTable clones:
                    for (int i = 0; i < clones.Count; i++)
                    {
                        if (clones.IsValidEntry(i))
                        {
                            var obj = clones.At(i);
                            result[(seg, i)] = Enumerable.Range(0, obj.VarCount).Select(obj.GetVariable).ToArray();
                        }
                    }

                    break;
            }
        }

        return result;
    }

    [Theory]
    [MemberData(nameof(GameNames))]
    public void ObjectsSurviveTheSerializer(string gameName, int enterEvery, int saveTick)
    {
        var folder = FindFolder(gameName);
        if (folder is null)
        {
            output.WriteLine($"{gameName} not found, skipping");
            return;
        }

        var host = new HeadlessHost(new ListLog([]), new MemorySaveStore());
        var provider = new SciEngineProvider();
        var game = new DirectoryGameContainer(folder);
        using var engine = (SciEngine)provider.Create(host, game, (SciDetectedGame)provider.Detect(game)[0]);
        engine.Initialize();
        Run(engine, host, 1, saveTick, enterEvery);

        var state = engine.State!;
        var before = Snapshot(state);
        var stream = new MemoryStream();
        Savegame.Save(state, stream, "objects", string.Empty);
        stream.Position = 0;
        Assert.True(Savegame.Restore(state, stream), "restore failed");
        var after = Snapshot(state);

        // the play list reconstruction writes loop, priority and volume into the sound objects
        var soundObjects = state.Sound.Music.PlayList.Select(e => ((int)e.SoundObj.Segment, (int)e.SoundObj.Offset)).ToHashSet();

        int differences = 0;
        foreach (var (key, vars) in before)
        {
            if (soundObjects.Contains(key))
            {
                continue;
            }

            if (!after.TryGetValue(key, out var restored))
            {
                differences++;
                if (differences <= 10)
                {
                    output.WriteLine($"object {key} missing after restore ({state.SegMan.GetObjectName(new RegT((ushort)key.Segment, (ushort)key.Offset))})");
                }

                continue;
            }

            if (!vars.SequenceEqual(restored))
            {
                differences++;
                if (differences <= 10)
                {
                    int first = Enumerable.Range(0, Math.Min(vars.Length, restored.Length)).FirstOrDefault(i => vars[i] != restored[i], -1);
                    output.WriteLine($"object {key} ({state.SegMan.GetObjectName(new RegT((ushort)key.Segment, (ushort)key.Offset))}): {vars.Length} -> {restored.Length} vars, first difference at {first}: {(first >= 0 ? vars[first] : "-")} -> {(first >= 0 && first < restored.Length ? restored[first] : "-")}");
                }
            }
        }

        output.WriteLine($"{gameName}: {before.Count} objects, {after.Count} after restore, {differences} differences, {stream.Length} bytes");
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(0, differences);
    }

    [Fact]
    public void MetadataSurvivesTheSerializer()
    {
        var stream = new MemoryStream();
        var saver = SaveSerializer.ForSaving(stream);
        saver.SyncVersion(Savegame.CurrentVersion);
        saver.SyncString("hello");
        saver.SyncInt16(-5);
        saver.SyncUInt32(123456789);
        saver.SyncReg(new RegT(7, 9));
        saver.Skip(4, 14, 18); // not present in the current version

        stream.Position = 0;
        var loader = SaveSerializer.ForLoading(stream);
        Assert.True(loader.SyncVersion(Savegame.CurrentVersion));
        Assert.Equal("hello", loader.SyncString(string.Empty));
        Assert.Equal(-5, loader.SyncInt16(0));
        Assert.Equal(123456789u, loader.SyncUInt32(0));
        Assert.Equal(new RegT(7, 9), loader.SyncReg(RegT.Null));
        loader.Skip(4, 14, 18);
        Assert.False(loader.Eos);
        Assert.Equal(stream.Length, stream.Position);
    }

    private static void Run(SciEngine engine, HeadlessHost host, int fromTick, int toTick, int enterEvery = 0)
    {
        int framesPerTick = (int)(host.Audio.SampleRate * host.TickInterval.TotalSeconds);
        for (int tick = fromTick; tick <= toTick; tick++)
        {
            if (enterEvery > 0 && tick % enterEvery == 0)
            {
                host.Input.PushKey(Core.Input.Key.Return, ascii: '\r');
            }

            var result = engine.Tick(tick == 1 ? TimeSpan.Zero : host.TickInterval);
            host.Audio.Pull(framesPerTick);
            Assert.True(result.Continue, $"the engine stopped at tick {tick}");
        }
    }

    private static string? FindFolder(string name)
    {
        foreach (var row in RealGameTests.SciGameFolders())
        {
            var folder = (string)row[0];
            if (!string.IsNullOrEmpty(folder) && Path.GetFileName(folder) == name)
            {
                return folder;
            }
        }

        return null;
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
