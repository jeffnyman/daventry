using System.Buffers.Binary;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Daventry.Sci.Engine;
using Xunit.Abstractions;

namespace Daventry.Sci.Tests;

/// <summary>
/// kFileIO: the files a game writes go to the host's save store as "&lt;game id&gt;-&lt;name&gt;",
/// as ScummVM names them, and are read back from there before the game's own files. The
/// real-game tests skip without the library.
/// </summary>
public sealed class FileIOTests(ITestOutputHelper output)
{
    private const int OpenOrCreate = 0;
    private const int OpenOrFail = 1;
    private const int Create = 2;

    /// <summary>
    /// Writes, reads, seeks, lists, renames, copies and deletes a file through the kernel calls,
    /// in an SCI01 game.
    /// </summary>
    [Fact]
    public void FilesRoundTripThroughTheKernelCalls()
    {
        using var run = Boot("King's Quest 1 (SCI)");
        if (run is null)
        {
            return;
        }

        var s = run.Engine.State!;
        var saves = run.Saves;
        string prefix = s.FilePrefix + "-";

        // a file that does not exist cannot be opened for reading
        Assert.Equal(RegT.Signal, KernelFunctions.FileIOOpen(s, Args(Str(s, "notes.txt"), Num(OpenOrFail))));
        Assert.Equal(0, KernelFunctions.FileIOExists(s, Args(Str(s, "notes.txt"))).Offset);

        var handle = KernelFunctions.FileIOOpen(s, Args(Str(s, "NOTES.TXT"), Num(Create)));
        Assert.NotEqual(RegT.Signal, handle);
        Assert.Equal(13, KernelFunctions.FileIOWriteString(s, Args(handle, Str(s, "first\r\nsecond"))).Offset);
        Assert.Equal(RegT.True, KernelFunctions.FileIOClose(s, Args(handle)));

        // the name is lowered and gets the game's prefix
        Assert.Equal([prefix + "notes.txt"], saves.List(prefix));
        Assert.Equal(1, KernelFunctions.FileIOExists(s, Args(Str(s, "notes.txt"))).Offset);

        // lines are read without their CR/LF, and the count includes the feed
        handle = KernelFunctions.FileIOOpen(s, Args(Str(s, "notes.txt"), Num(OpenOrFail)));
        var buffer = Buffer(s, 40);
        Assert.Equal(buffer, KernelFunctions.FileIOReadString(s, Args(buffer, Num(40), handle)));
        Assert.Equal("first", s.SegMan.GetString(buffer));
        Assert.Equal(buffer, KernelFunctions.FileIOReadString(s, Args(buffer, Num(40), handle)));
        Assert.Equal("second", s.SegMan.GetString(buffer));
        Assert.Equal(RegT.Null, KernelFunctions.FileIOReadString(s, Args(buffer, Num(40), handle)));
        Assert.Equal("", s.SegMan.GetString(buffer));

        // SCI16 seeks answer with success, and raw reads give the bytes
        Assert.Equal(1, KernelFunctions.FileIOSeek(s, Args(handle, Num(2), Num(0))).Offset);
        Assert.Equal(3, KernelFunctions.FileIOReadRaw(s, Args(handle, buffer, Num(3))).Offset);
        Assert.Equal("rst", s.SegMan.GetString(buffer)[..3]);
        KernelFunctions.FileIOClose(s, Args(handle));

        // opening for writing starts the file afresh
        handle = KernelFunctions.FileIOOpen(s, Args(Str(s, "other.txt"), Num(OpenOrCreate)));
        KernelFunctions.FileIOWriteString(s, Args(handle, Str(s, "x")));
        KernelFunctions.FileIOClose(s, Args(handle));

        // the listing gives the names without the prefix
        var found = KernelFunctions.FileIOFindFirst(s, Args(Str(s, "*.txt"), buffer, Num(0)));
        var names = new List<string>();
        while (!found.IsNull)
        {
            names.Add(s.SegMan.GetString(found));
            found = KernelFunctions.FileIOFindNext(s, Args());
        }

        Assert.Equal(["notes.txt", "other.txt"], names);

        Assert.Equal(RegT.Null, KernelFunctions.FileIOCopy(s, Args(Str(s, "notes.txt"), Str(s, "copy.txt"))));
        Assert.Equal(RegT.Null, KernelFunctions.FileIORename(s, Args(Str(s, "other.txt"), Str(s, "moved.txt"))));
        Assert.Equal(RegT.Signal, KernelFunctions.FileIORename(s, Args(Str(s, "missing.txt"), Str(s, "moved.txt"))));
        Assert.Equal(1, KernelFunctions.FileIOUnlink(s, Args(Str(s, "notes.txt"))).Offset);
        Assert.Equal(0, KernelFunctions.FileIOUnlink(s, Args(Str(s, "notes.txt"))).Offset);
        Assert.Equal([prefix + "copy.txt", prefix + "moved.txt"], saves.List(prefix));
        Assert.Equal("first\r\nsecond"u8.ToArray(), saves.ReadAllBytes(prefix + "copy.txt"));

        // a game's own file is read when the store has none of that name
        handle = KernelFunctions.FileIOOpen(s, Args(Str(s, "resource.map"), Num(OpenOrFail)));
        Assert.NotEqual(RegT.Signal, handle);
        KernelFunctions.FileIOClose(s, Args(handle));
        Assert.Equal(1, KernelFunctions.FileIOExists(s, Args(Str(s, "RESOURCE.MAP"))).Offset);

        Assert.Empty(run.Warnings);
    }

    /// <summary>
    /// kDeviceInfo hands the scripts a name to throw away and deletes the save itself, the way
    /// SCI0 games delete their saves.
    /// </summary>
    [Fact]
    public void DeviceInfoDeletesTheSaveItNames()
    {
        using var run = Boot("King's Quest 1 (SCI)");
        if (run is null)
        {
            return;
        }

        var s = run.Engine.State!;
        Assert.True(run.Engine.SaveGame(3, "to delete"));
        Assert.Contains(Savegame.SaveName(s, 3), run.Saves.List(s.SaveNamePrefix));

        var buffer = Buffer(s, 40);
        KernelFunctions.DeviceInfo(s, Args(Num(8), buffer, Str(s, "kq1"), Num(Savegame.OfficialRangeStart + 3)));
        Assert.Equal("__throwaway", s.SegMan.GetString(buffer));
        Assert.DoesNotContain(Savegame.SaveName(s, 3), run.Saves.List(s.SaveNamePrefix));
    }

    /// <summary>
    /// KQ7 and RAMA read their saves from catalogue files, which are made up from the saved games:
    /// KQ7's lists each save's number and name, RAMA's adds a count and a table of free slots.
    /// </summary>
    [Fact]
    public void CataloguesAreMadeFromTheSavedGames()
    {
        using var run = Boot("King's Quest 1 (SCI)");
        if (run is null)
        {
            return;
        }

        var s = run.Engine.State!;
        Assert.True(run.Engine.SaveGame(1, "first"));
        Assert.True(run.Engine.SaveGame(3, "third"));
        var saves = Savegame.ListSavegames(s);

        var kq7 = SciFile.MakeCatalogue(s, 10, Savegame.MaxSaveNameLength, string.Empty, false).ToArray();
        Assert.Equal(2 * (2 + 36) + 2, kq7.Length);
        for (int i = 0; i < 2; i++)
        {
            var entry = kq7.AsSpan(i * 38, 38);
            Assert.Equal(saves[i].Id - 1, BinaryPrimitives.ReadUInt16LittleEndian(entry));
            Assert.Equal(saves[i].Name, System.Text.Encoding.Latin1.GetString(entry[2..]).TrimEnd('\0'));
        }

        Assert.Equal(0xFFFF, BinaryPrimitives.ReadUInt16LittleEndian(kq7.AsSpan(^2)));

        var rama = SciFile.MakeCatalogue(s, 100, 20, "ramasg.%d", true).ToArray();
        Assert.Equal(2 + 2 * (2 + 12 + 20) + 100 * 2 + 2, rama.Length);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(rama));
        var first = rama.AsSpan(2, 34);
        Assert.Equal($"ramasg.{saves[0].Id - 1}", System.Text.Encoding.Latin1.GetString(first[..12]).TrimEnd('\0'));
        Assert.Equal(saves[0].Id - 1, BinaryPrimitives.ReadUInt16LittleEndian(first[32..]));
        var freeSlots = rama.AsSpan(2 + 2 * 34, 200);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(freeSlots[0..])); // save 1 is slot 0
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(freeSlots[2..]));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(freeSlots[4..])); // save 3 is slot 2
    }

    /// <summary>
    /// Leisure Suit Larry 7 writes its preferences to larry7.prf on its first start and reads
    /// them on the next, instead of writing them anew.
    /// </summary>
    [Fact]
    public void Larry7KeepsItsPreferencesBetweenRuns()
    {
        var folder = FindFolder("Larry 7");
        if (folder is null)
        {
            output.WriteLine("Leisure Suit Larry 7 not found, skipping");
            return;
        }

        var saves = new MemorySaveStore();
        var firstRun = RunGame(folder, saves, 300);
        Assert.Contains("lsl7-larry7.prf", saves.List("lsl7-"));
        Assert.Contains(firstRun, m => m.Contains("kFileIO(open): larry7.prf, 0x2"));
        var preferences = saves.ReadAllBytes("lsl7-larry7.prf");

        var secondRun = RunGame(folder, saves, 300);
        Assert.Contains(secondRun, m => m.Contains("opened file 'larry7.prf'"));
        Assert.DoesNotContain(secondRun, m => m.Contains("kFileIO(open): larry7.prf, 0x2"));
        Assert.Equal(preferences, saves.ReadAllBytes("lsl7-larry7.prf"));
    }

    /// <summary>
    /// RAMA asks kResCheck for its videos before playing them; they are files in the VMD and ROBOT
    /// folders, not resources, so the check falls back to the game's files as in ScummVM.
    /// </summary>
    [Fact]
    public void RamaFindsItsVideosWithResCheck()
    {
        using var run = Boot("RAMA");
        if (run is null)
        {
            return;
        }

        var s = run.Engine.State!;
        RegT Type(Resources.ResourceType type)
            => Num(0x80 | Enumerable.Range(0, 0x80).First(t => s.ResMan.ConvertResType((byte)t) == type));

        Assert.Equal(1, KernelFunctions.ResCheck(s, Args(Type(Resources.ResourceType.Vmd), Num(100))).Offset);
        Assert.Equal(1, KernelFunctions.ResCheck(s, Args(Type(Resources.ResourceType.Robot), Num(1000))).Offset);
        Assert.Equal(0, KernelFunctions.ResCheck(s, Args(Type(Resources.ResourceType.Vmd), Num(1))).Offset);
    }

    /// <summary>
    /// Phantasmagoria keeps its games in files of its own, rewritten in memory and written back on
    /// close: naming a new game and starting chapter one writes phantsg.dir (the list of names) and
    /// phantsg.0, and the next start offers "Continue old game" from them.
    /// </summary>
    [Fact]
    public void PhantasmagoriaKeepsItsGamesInItsOwnFiles()
    {
        var folder = FindFolder("Phantasmagoria1");
        if (folder is null)
        {
            output.WriteLine("Phantasmagoria not found, skipping");
            return;
        }

        var saves = new MemorySaveStore();
        var host = new HeadlessHost(NullLog.Instance, saves);
        var provider = new SciEngineProvider();
        var game = new DirectoryGameContainer(folder);
        using (var engine = provider.Create(host, game, provider.Detect(game)[0]))
        {
            host.Run(engine, 2600);
            Click(host, engine, 45, 185); // Start new game
            host.Continue(engine, 800);
            foreach (char c in "Adrienne")
            {
                host.Input.PushKey(Core.Input.Key.None, ascii: c);
            }

            host.Input.PushKey(Core.Input.Key.Return, ascii: '\r');
            host.Continue(engine, 1200);
            Click(host, engine, 225, 73); // chapter one
            host.Continue(engine, 600);
        }

        Assert.Contains("phantasmagoria-phantsg.dir", saves.List("phantasmagoria-"));
        Assert.Contains("phantasmagoria-phantsg.0", saves.List("phantasmagoria-"));
        Assert.Contains("ADRIENNE\n", System.Text.Encoding.ASCII.GetString(saves.ReadAllBytes("phantasmagoria-phantsg.dir")!));

        var log = new List<string>();
        var second = new HeadlessHost(new MessageLog(log), saves);
        using (var engine = provider.Create(second, game, provider.Detect(game)[0]))
        {
            second.Run(engine, 2800);
        }

        Assert.Contains(log, m => m.Contains("kFileIO(fileExists) phantsg.0 -> True"));
    }

    /// <summary>A click as a hand makes it, as the player's --click does: move, press, hold, release.</summary>
    private static void Click(HeadlessHost host, Core.Hosting.Engine engine, int x, int y)
    {
        host.Input.Mouse = new Core.Input.MouseState(x, y, false, false, false);
        host.Input.Push(Core.Input.InputEvent.MouseMove(x, y));
        host.Continue(engine, 6);
        host.Input.Mouse = new Core.Input.MouseState(x, y, true, false, false);
        host.Input.Push(Core.Input.InputEvent.MouseDown(Core.Input.MouseButton.Left, x, y));
        host.Continue(engine, 6);
        host.Input.Mouse = new Core.Input.MouseState(x, y, false, false, false);
        host.Input.Push(Core.Input.InputEvent.MouseUp(Core.Input.MouseButton.Left, x, y));
    }

    private static List<string> RunGame(string folder, ISaveStore saves, int ticks)
    {
        var messages = new List<string>();
        var host = new HeadlessHost(new MessageLog(messages), saves);
        var provider = new SciEngineProvider();
        var game = new DirectoryGameContainer(folder);
        using var engine = (SciEngine)provider.Create(host, game, provider.Detect(game)[0]);
        Assert.Equal(ticks, host.Run(engine, ticks));
        return messages;
    }

    private sealed class BootedGame(SciEngine engine, MemorySaveStore saves, List<string> warnings) : IDisposable
    {
        public SciEngine Engine { get; } = engine;
        public MemorySaveStore Saves { get; } = saves;
        public List<string> Warnings { get; } = warnings;

        public void Dispose() => Engine.Dispose();
    }

    private BootedGame? Boot(string name)
    {
        var folder = FindFolder(name);
        if (folder is null)
        {
            output.WriteLine($"{name} not found, skipping");
            return null;
        }

        var warnings = new List<string>();
        var saves = new MemorySaveStore();
        var host = new HeadlessHost(new WarningLog(warnings), saves);
        var provider = new SciEngineProvider();
        var game = new DirectoryGameContainer(folder);
        var engine = (SciEngine)provider.Create(host, game, provider.Detect(game)[0]);
        host.Run(engine, 60);
        return new BootedGame(engine, saves, warnings);
    }

    private static string? FindFolder(string name)
        => RealGameTests.SciGameFolders().Select(f => (string)f[0]).FirstOrDefault(f => f.Contains(name, StringComparison.OrdinalIgnoreCase));

    private static KernelArgs Args(params RegT[] values) => new(values, 0, values.Length);

    private static RegT Num(int value) => RegT.Make(0, value);

    private static RegT Buffer(EngineState s, int size)
    {
        s.SegMan.AllocDynmem(size, "test buffer", out var id);
        return id;
    }

    private static RegT Str(EngineState s, string text)
    {
        var id = Buffer(s, text.Length + 1);
        s.SegMan.Strcpy(id, text);
        return id;
    }

    private sealed class WarningLog(List<string> warnings) : ILog
    {
        public void Write(LogLevel level, string category, string message)
        {
            if (level >= LogLevel.Warning)
            {
                warnings.Add($"{category}: {message}");
            }
        }
    }

    private sealed class MessageLog(List<string> messages) : ILog
    {
        public void Write(LogLevel level, string category, string message) => messages.Add($"{category}: {message}");
    }
}
