using System.Text;
using Daventry.Adl.Detection;
using Daventry.Audio;
using Daventry.Core.Formats;
using Daventry.Core.Graphics;
using Daventry.Core.Hosting;
using Daventry.Core.Input;
using Daventry.Core.Storage;

namespace Daventry.Adl;

public enum Direction
{
    North,
    South,
    East,
    West,
    Up,
    Down,
    Total,
}

public sealed class Room
{
    public byte Description { get; set; }

    public byte[] Connections { get; } = new byte[(int)Direction.Total];

    public DataBlock? Data { get; set; }

    public byte Picture { get; set; }

    public byte CurPicture { get; set; }

    public bool IsFirstTime { get; set; } = true;
}

public sealed class Command
{
    public byte Room { get; set; }

    public byte Verb { get; set; }

    public byte Noun { get; set; }

    public byte NumCond { get; set; }

    public byte NumAct { get; set; }

    public List<byte> Script { get; } = [];
}

/// <summary>Walks one command's conditions and actions. Port of ScummVM's ScriptEnv.</summary>
public abstract class ScriptEnv(Command command, byte room, byte verb, byte noun)
{
    public enum OpType
    {
        Done,
        Cond,
        Act,
    }

    public Command Command { get; } = command;

    public byte Noun { get; } = noun;

    protected int Ip { get; set; }

    public byte Op => Command.Script[Ip];

    public bool IsMatch =>
        (Command.Room == AdlEngine.IdiAny || Command.Room == room)
        && (Command.Verb == AdlEngine.IdiAny || Command.Verb == verb)
        && (Command.Noun == AdlEngine.IdiAny || Command.Noun == Noun);

    public abstract OpType GetOpType();

    /// <summary>The opcode's arguments, 1-based for easier comparison with the original engine.</summary>
    public byte Arg(int i) => Command.Script[Ip + i];

    public abstract void Next(int numArgs);
}

/// <summary>The 6502 interpreters keep counts of conditions and actions. Port of ScriptEnv_6502.</summary>
public sealed class ScriptEnv6502(Command command, byte room, byte verb, byte noun) : ScriptEnv(command, room, verb, noun)
{
    private int _remCond = command.NumCond;
    private int _remAct = command.NumAct;

    public override OpType GetOpType() => _remCond > 0 ? OpType.Cond : _remAct > 0 ? OpType.Act : OpType.Done;

    public override void Next(int numArgs)
    {
        Ip += numArgs + 1;
        if (_remCond > 0)
        {
            --_remCond;
        }
        else if (_remAct > 0)
        {
            --_remAct;
        }
    }
}

public sealed class Item
{
    public byte Id { get; set; }

    public byte Noun { get; set; }

    public byte Region { get; set; }

    public byte Room { get; set; }

    public byte Picture { get; set; }

    public bool IsShape { get; set; }

    public int PositionX { get; set; }

    public int PositionY { get; set; }

    public int State { get; set; }

    public byte Description { get; set; }

    public List<byte> RoomPictures { get; } = [];

    public bool IsOnScreen { get; set; }
}

public sealed class GameTime
{
    public byte Hours { get; set; } = 12;

    public byte Minutes { get; set; }
}

public sealed class RoomState
{
    public byte Picture { get; set; }

    public byte IsFirstTime { get; set; }
}

public sealed class Region
{
    public List<byte> Vars { get; } = [];

    public List<RoomState> Rooms { get; } = [];
}

public sealed class State
{
    public List<Region> Regions { get; } = [];

    public List<Room> Rooms { get; } = [];

    public List<Item> Items { get; } = [];

    public List<byte> Vars { get; } = [];

    public byte Region { get; set; }

    public byte PrevRegion { get; set; }

    public byte Room { get; set; } = 1;

    public byte CurPicture { get; set; }

    public ushort Moves { get; set; } = 1;

    public bool IsDark { get; set; }

    public GameTime Time { get; } = new();
}

public sealed class RoomData
{
    public string Description { get; set; } = string.Empty;

    public Dictionary<byte, DataBlock?> Pictures { get; } = [];

    public List<Command> Commands { get; } = [];
}

/// <summary>
/// The ADL interpreter, port of ScummVM's AdlEngine (engines/adl/adl.cpp). ADL is the engine
/// Sierra On-Line, still On-Line Systems, wrote for its Apple II Hi-Res Adventures: rooms with a
/// picture and a description, a two-word parser, and scripts of conditions and actions matched
/// against the verb and noun typed. The per-game classes (HiRes1Engine to HiRes6Engine) load the
/// data from the disks and override what differs.
/// <para>
/// The C++ engine blocks on the keyboard, on delays and on the speaker. Here the game runs as an
/// async method: wherever the original waited, the port awaits the <see cref="Scheduler"/>, and
/// <see cref="Tick"/> feeds it host input and engine time. Action opcodes are async for that reason;
/// condition opcodes never wait and stay synchronous.
/// </para>
/// </summary>
public abstract class AdlEngine : Engine
{
    public const byte IdoActSave = 0x0f;
    public const byte IdoActLoad = 0x10;
    public const byte IdiCurRoom = 0xfc;
    public const byte IdiVoidRoom = 0xfd;
    public const byte IdiAny = 0xfe;
    public const int IdiWordSize = 8;

    public const int ItemNotMoved = 0;
    public const int ItemDropped = 1;
    public const int ItemDoesntMove = 2;

    /// <summary>ScummVM's SAVEGAME_VERSION and SAVEGAME_NAME_LEN for ADL.</summary>
    public const byte SaveGameVersion = 0;
    public const int SaveGameNameLength = 32;

    /// <summary>Every line of text the game prints goes to the log at debug level under this category.</summary>
    public const string TextLogCategory = "adl.text";

    /// <summary>Script traces go to the log at debug level under this category.</summary>
    public const string ScriptLogCategory = "adl.script";

    private const uint SaveTag = 0x41444C3A; // "ADL:"
    private const uint ThumbnailTag = 0x54484D42; // "THMB"
    private const int ThumbnailWidth = 160;
    private const int ThumbnailHeight = 110;
    private const int PollIntervalMs = 16;

    private readonly Queue<InputEvent> _events = new();
    private readonly ClockedSource _audio;
    private TimeSpan _engineTime;
    private Task? _task;
    private bool _quitRequested;
    private int? _pendingRestore;
    private byte _saveVerb, _saveNoun, _restoreVerb, _restoreNoun;

    protected AdlEngine(IHost host, IGameContainer game, AdlDetectedGame detected, DisplayOptions options)
        : base(host, game, detected)
    {
        Description = detected;
        Random = host.RandomSeed is { } seed ? new Random(seed) : new Random();
        Display = new DisplayA2(() => Scheduler.Now, options);
        Speaker = new AdlSpeaker(host.Audio.SampleRate);
        _audio = new ClockedSource(Speaker, host.Audio.SampleRate);
        Display.Bell = () => Speaker.Play(BellTones(1));
        Display.LineCompleted += line =>
        {
            if (line.Length > 0)
            {
                Host.Log.Debug(TextLogCategory, line);
            }
        };
        TraceScripts = Environment.GetEnvironmentVariable("DAVENTRY_TRACE_ADL") == "1";
    }

    public AdlDetectedGame Description { get; }

    public FrameScheduler Scheduler { get; } = new();

    public DisplayA2 Display { get; }

    public AdlSpeaker Speaker { get; }

    /// <summary>Seedable for reproducible runs.</summary>
    public Random Random { get; set; }

    /// <summary>Log every condition and action as it runs, ScummVM's "Script" debug channel (or DAVENTRY_TRACE_ADL=1).</summary>
    public bool TraceScripts { get; set; }

    /// <summary>True while the game waits for a key: for hosts and tests that type into it.</summary>
    public bool AwaitingInput { get; private set; }

    /// <summary>True while a full text window waits for Return before printing more.</summary>
    public bool InTextOverflow { get; protected set; }

    /// <summary>Host events queued and not yet read by the game.</summary>
    internal int PendingEvents => _events.Count;

    /// <summary>The game's state: rooms, items and variables.</summary>
    public State GameState => _state;

    public bool ShouldQuit => _quitRequested;

    /// <summary>Time the engine has run.</summary>
    public TimeSpan EngineTime => _engineTime;

    protected GraphicsManV1 Graphics { get; set; } = null!;

    protected bool TextMode { get; set; }

    protected List<CondOpcode?> CondOpcodes { get; } = [];

    protected List<ActOpcode?> ActOpcodes { get; } = [];

    /// <summary>Message strings in the data files.</summary>
    protected List<DataBlock?> Messages { get; } = [];

    /// <summary>Picture data.</summary>
    protected Dictionary<byte, DataBlock?> Pictures { get; } = [];

    /// <summary>Where dropped items are drawn.</summary>
    protected List<(int X, int Y)> ItemOffsets { get; } = [];

    protected List<Command> RoomCommands { get; } = [];

    protected List<Command> GlobalCommands { get; } = [];

    /// <summary>Data related to the current room.</summary>
    protected RoomData CurrentRoomData { get; } = new();

    protected Dictionary<string, int> Verbs { get; } = new(StringComparer.Ordinal);

    protected Dictionary<string, int> Nouns { get; } = new(StringComparer.Ordinal);

    protected List<string> PriVerbs { get; } = [];

    protected List<string> PriNouns { get; } = [];

    protected EngineStrings Strings { get; } = new();

    protected int VerbErrorPos { get; set; } = 19;

    protected int NounErrorPos { get; set; } = 30;

    protected MessageIds MessageIdTable { get; } = new();

    protected State _state = new();

    protected int LinesPrinted { get; set; }

    protected bool IsRestarting { get; set; }

    protected bool IsRestoring { get; set; }

    protected bool IsQuitting { get; set; }

    protected bool CanSaveNow { get; set; }

    protected bool CanRestoreNow { get; set; }

    protected bool AbortScript { get; set; }

    protected GameType GameType => Description.Description.GameType;

    protected GameVersion GameVersion => Description.Description.Version;

    protected string Language => Description.Description.Language;

    protected AdlPlatform Platform => Description.Description.Platform;

    protected delegate int CondOpcode(ScriptEnv e);

    protected delegate ValueTask<int> ActOpcode(ScriptEnv e);

    // ---- host plumbing ----

    public override void Initialize()
    {
        Display.Init();
        Host.Graphics.SetMode(DisplayA2.ScreenWidth, DisplayA2.ScreenHeight);
        Host.Graphics.SetPalette(DisplayA2.Palette);
        Host.Graphics.SetTitle($"Daventry - {Detected.Title}");

        try
        {
            Host.Audio.Start(_audio);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Host.Log.Warning("adl", $"Audio output unavailable, running silent: {ex.Message}");
        }

        // runs synchronously until the first await, then returns here
        using (NoSynchronizationContext.Enter())
        {
            _task = RunAsync();
        }

        Present();
    }

    public override TickResult Tick(TimeSpan elapsed)
    {
        using var noContext = NoSynchronizationContext.Enter();
        _engineTime += elapsed;

        while (Host.Input.TryRead(out var inputEvent))
        {
            if (inputEvent.Type == InputEventType.Quit)
            {
                _quitRequested = true;
                return TickResult.Quit;
            }

            _events.Enqueue(inputEvent);
        }

        _audio.Update(_engineTime);
        Scheduler.Advance(_engineTime);

        if (_task is { IsFaulted: true })
        {
            var inner = _task.Exception?.InnerException ?? _task.Exception!;
            throw new InvalidOperationException($"{Detected.Title} failed: {inner.Message}", inner);
        }

        Present();

        if (_task is null || _task.IsCompleted || ShouldQuit)
        {
            return TickResult.Quit;
        }

        var wakeAt = Scheduler.WakeAt ?? _engineTime;
        var delay = wakeAt - _engineTime;
        return delay > TimeSpan.Zero ? TickResult.After(delay) : TickResult.Immediately;
    }

    public override void Dispose()
    {
        Host.Audio.Stop();
        base.Dispose();
    }

    public void QuitGame() => _quitRequested = true;

    /// <summary>
    /// Restores a slot the next time the game waits for a command, as ScummVM's menu does; the game
    /// then runs the global commands once and asks again.
    /// </summary>
    public void RequestRestore(int slot) => _pendingRestore = slot;

    /// <summary>Saves to a slot if the game would accept "SAVE GAME" now, as ScummVM's menu does.</summary>
    public bool SaveGame(int slot, string description)
        => CanSaveGameStateCurrently() && SaveGameState(slot, description);

    public string GetSaveStateName(int slot) => $"{Description.GameId}.s{slot:00}";

    private void Present()
    {
        if (Display.Dirty)
        {
            Host.Graphics.Present(Display.Screen);
            Display.Dirty = false;
        }
    }

    // ---- run ----

    private async Task RunAsync()
    {
        SetupOpcodeTables();
        Init();

        await RunIntro();
        InitState();
        Display.PrintAsciiString(Strings.LineFeeds);

        Display.SetMode(Adl.Display.Mode.Mixed);

        while (!(IsQuitting || ShouldQuit))
        {
            await GameLoop();
        }
    }

    protected virtual Task RunIntro() => Task.CompletedTask;

    protected abstract void Init();

    protected abstract void InitGameState();

    protected abstract void DrawItems();

    protected abstract void DrawItem(Item item, int x, int y);

    protected abstract void LoadRoom(byte roomNr);

    protected abstract Task ShowRoom();

    protected virtual void SwitchRegion(byte region)
    {
    }

    protected virtual async Task GameLoop()
    {
        int verb = 0, noun = 0;
        IsRestarting = false;

        // when restoring from the launcher, no input is read on the first iteration, so that
        // restoring there and in-game bring the game to the same state
        if (!IsRestoring)
        {
            await ShowRoom();

            if (IsRestarting)
            {
                return;
            }

            CanSaveNow = CanRestoreNow = true;
            (verb, noun) = await GetInput();
            CanSaveNow = CanRestoreNow = false;

            if (ShouldQuit)
            {
                return;
            }

            LinesPrinted = 0;

            // after a restore from the menu, no command was typed, so this set is skipped
            if (!IsRestoring)
            {
                await CheckInput((byte)verb, (byte)noun);
            }
        }

        if (IsRestoring)
        {
            // restoring from the menu or launcher. As "RESTORE GAME" does not end command
            // processing, neither does this: a restored game always runs the global commands
            // and moves the counter on before the first input
            Display.PrintAsciiString("\r");
            IsRestoring = false;
            verb = _restoreVerb;
            noun = _restoreNoun;
        }

        // restarting does end command processing
        if (IsRestarting)
        {
            return;
        }

        await DoAllCommands(GlobalCommands, (byte)verb, (byte)noun);

        if (IsRestarting)
        {
            return;
        }

        AdvanceClock();
        _state.Moves++;
    }

    protected virtual void SetupOpcodeTables()
    {
        Resize(CondOpcodes, 0x0b);
        CondOpcodes[0x03] = e => OIsItemInRoom(e);
        CondOpcodes[0x05] = e => OIsMovesGT(e);
        CondOpcodes[0x06] = e => OIsVarEQ(e);
        CondOpcodes[0x09] = e => OIsCurPicEQ(e);
        CondOpcodes[0x0a] = e => OIsItemPicEQ(e);

        Resize(ActOpcodes, 0x1e);
        ActOpcodes[0x01] = e => OVarAdd(e);
        ActOpcodes[0x02] = e => OVarSub(e);
        ActOpcodes[0x03] = e => OVarSet(e);
        ActOpcodes[0x04] = e => OListInv(e);
        ActOpcodes[0x05] = e => OMoveItem(e);
        ActOpcodes[0x06] = e => OSetRoom(e);
        ActOpcodes[0x07] = e => OSetCurPic(e);
        ActOpcodes[0x08] = e => OSetPic(e);
        ActOpcodes[0x09] = e => OPrintMsg(e);
        ActOpcodes[0x0a] = e => OSetLight(e);
        ActOpcodes[0x0b] = e => OSetDark(e);
        ActOpcodes[0x0d] = e => OQuit(e);
        ActOpcodes[0x0f] = e => OSave(e);
        ActOpcodes[0x10] = e => ORestore(e);
        ActOpcodes[0x11] = e => ORestart(e);
        ActOpcodes[0x12] = e => OPlaceItem(e);
        ActOpcodes[0x13] = e => OSetItemPic(e);
        ActOpcodes[0x14] = e => OResetPic(e);
        ActOpcodes[0x15] = e => GoDirection(e, Direction.North);
        ActOpcodes[0x16] = e => GoDirection(e, Direction.South);
        ActOpcodes[0x17] = e => GoDirection(e, Direction.East);
        ActOpcodes[0x18] = e => GoDirection(e, Direction.West);
        ActOpcodes[0x19] = e => GoDirection(e, Direction.Up);
        ActOpcodes[0x1a] = e => GoDirection(e, Direction.Down);
        ActOpcodes[0x1b] = e => OTakeItem(e);
        ActOpcodes[0x1c] = e => ODropItem(e);
        ActOpcodes[0x1d] = e => OSetRoomPic(e);
    }

    protected static void Resize<T>(List<T?> list, int size)
        where T : class
    {
        while (list.Count < size)
        {
            list.Add(null);
        }

        if (list.Count > size)
        {
            list.RemoveRange(size, list.Count - size);
        }
    }

    protected virtual void InitState()
    {
        _state = new State();
        InitGameState();
    }

    protected virtual void SwitchRoom(byte roomNr)
    {
        GetCurRoom().CurPicture = GetCurRoom().Picture;
        _state.Room = roomNr;
    }

    protected virtual byte RoomArg(byte room) => room;

    protected virtual void AdvanceClock()
    {
    }

    protected void LoadDroppedItemOffsets(SeekableReadStream stream, int count)
    {
        for (int i = 0; i < count; ++i)
        {
            int x = stream.ReadByte();
            int y = stream.ReadByte();
            ItemOffsets.Add((x, y));
        }
    }

    // ---- strings ----

    /// <summary>A string up to a terminator byte, optionally XORed with a repeating key.</summary>
    protected static string ReadString(SeekableReadStream stream, byte until = 0, string key = "")
    {
        var str = new StringBuilder();
        while (true)
        {
            byte b = stream.ReadByte();

            if (stream.Eos)
            {
                throw new InvalidDataException("Error reading string");
            }

            if (b == until)
            {
                break;
            }

            if (key.Length > 0)
            {
                b ^= (byte)key[str.Length % key.Length];
            }

            str.Append((char)b);
        }

        return str.ToString();
    }

    protected static string ReadStringAt(SeekableReadStream stream, int offset, byte until = 0)
    {
        stream.Seek(offset);
        return ReadString(stream, until);
    }

    /// <summary>Collects the strings printed by the executable: each follows a JSR to the print routine.</summary>
    protected static void ExtractExeStrings(SeekableReadStream stream, ushort printAddr, List<string> strings)
    {
        uint window = 0;

        while (true)
        {
            window <<= 8;
            window |= stream.ReadByte();

            if (stream.Eos)
            {
                return;
            }

            if ((window & 0xffffff) == (0x200000U | printAddr))
            {
                strings.Add(ReadString(stream));
            }
        }
    }

    protected abstract Task PrintString(string str);

    protected abstract string LoadMessage(int idx);

    protected virtual Task PrintMessage(int idx) => PrintString(LoadMessage(idx));

    protected virtual string GetItemDescription(Item item)
        => item.Description > 0 ? LoadMessage(item.Description) : string.Empty;

    // ---- waiting and input ----

    /// <summary>Takes the next host event; true for a key press. Ctrl+Q quits, as ScummVM's keymap does.</summary>
    protected bool PollEvent(out InputEvent ev)
    {
        if (!_events.TryDequeue(out ev))
        {
            return false;
        }

        if (ev.Type == InputEventType.KeyDown && ev.Key == Key.Q && (ev.Modifiers & KeyModifiers.Ctrl) != 0)
        {
            QuitGame();
            return false;
        }

        return ev.Type == InputEventType.KeyDown;
    }

    protected FrameScheduler.FrameAwaitable Poll() => Scheduler.Delay(PollIntervalMs);

    protected async Task Delay(int ms)
    {
        var end = Scheduler.Now + TimeSpan.FromMilliseconds(ms);
        while (!ShouldQuit && Scheduler.Now < end)
        {
            PollEvent(out _);
            var remaining = end - Scheduler.Now;
            var poll = TimeSpan.FromMilliseconds(PollIntervalMs);
            await Scheduler.Delay(remaining < poll ? remaining : poll);
        }
    }

    protected async Task<string> InputString(byte prompt = 0)
    {
        var s = new StringBuilder();

        if (prompt > 0)
        {
            Display.PrintString(((char)prompt).ToString());
        }

        while (true)
        {
            byte b = await InputKey();

            if (ShouldQuit || IsRestoring)
            {
                return string.Empty;
            }

            if (b == 0)
            {
                continue;
            }

            if (b == ('\r' | 0x80))
            {
                s.Append((char)b);
                Display.PrintString(((char)b).ToString());
                return s.ToString();
            }

            if (b < 0xa0)
            {
                if (b == (8 | 0x80) && s.Length > 0)
                {
                    Display.MoveCursorBackward();
                    Display.SetCharAtCursor((byte)Display.AsciiToNative(' '));
                    s.Length--;
                }
            }
            else if (s.Length < 255)
            {
                s.Append((char)b);
                Display.PrintString(((char)b).ToString());
            }
        }
    }

    protected async Task<byte> InputKey(bool showCursor = true)
    {
        byte key = 0;

        if (showCursor)
        {
            Display.ShowCursor(true);
        }

        AwaitingInput = true;
        try
        {
            while (!ShouldQuit && !IsRestoring && key == 0)
            {
                if (_pendingRestore is { } slot && CanRestoreNow)
                {
                    _pendingRestore = null;
                    LoadGameState(slot);
                    break;
                }

                if (PollEvent(out var ev))
                {
                    key = ev.Key switch
                    {
                        Key.Return => ConvertKey(13),
                        Key.Backspace => ConvertKey(8),
                        _ => ev.Ascii is >= (char)0x20 and < (char)0x80 ? ConvertKey(ev.Ascii) : (byte)0,
                    };
                }

                Display.RenderText();
                await Poll();
            }
        }
        finally
        {
            AwaitingInput = false;
        }

        Display.ShowCursor(false);
        return key;
    }

    protected async Task WaitKey(int ms = 0, Key key = Key.None)
    {
        var start = Scheduler.Now;
        AwaitingInput = true;
        try
        {
            while (!ShouldQuit)
            {
                if (PollEvent(out var ev) && (key == Key.None || ev.Key == key))
                {
                    return;
                }

                if (ms != 0 && Scheduler.Now - start >= TimeSpan.FromMilliseconds(ms))
                {
                    return;
                }

                await Poll();
            }
        }
        finally
        {
            AwaitingInput = false;
        }
    }

    private static byte ConvertKey(int ascii)
    {
        ascii = char.ToUpperInvariant((char)ascii);

        if (ascii >= 0x80)
        {
            return 0;
        }

        ascii |= 0x80;
        return ascii is >= 0x80 and <= 0xe0 ? (byte)ascii : (byte)0;
    }

    protected virtual async Task<string> GetLine()
    {
        while (true)
        {
            string line = await InputString((byte)Display.AsciiToNative('?'));

            if (ShouldQuit || IsRestoring)
            {
                return string.Empty;
            }

            if (line[0] == ('\r' | 0x80))
            {
                // an empty line switches between the picture and full-screen text
                TextMode = !TextMode;
                Display.SetMode(TextMode ? Adl.Display.Mode.Text : Adl.Display.Mode.Mixed);
                continue;
            }

            // remove the return
            return line[..^1];
        }
    }

    protected string GetWord(string line, ref int index)
    {
        char spaceChar = Display.AsciiToNative(' ');
        var str = new string(spaceChar, 8).ToCharArray();
        int copied = 0;

        // skip initial whitespace
        while (true)
        {
            if (index == line.Length)
            {
                return new string(str);
            }

            if (line[index] != spaceChar)
            {
                break;
            }

            ++index;
        }

        // copy up to 8 characters
        while (true)
        {
            if (copied < 8)
            {
                str[copied++] = line[index];
            }

            index++;

            if (index == line.Length || line[index] == spaceChar)
            {
                return new string(str);
            }
        }
    }

    protected virtual string FormatVerbError(string verb)
    {
        string err = Strings.VerbError;

        if (VerbErrorPos + verb.Length > err.Length)
        {
            throw new InvalidDataException("Failed to format verb error string");
        }

        return Replace(err, VerbErrorPos, verb);
    }

    protected virtual string FormatNounError(string verb, string noun)
    {
        string err = Strings.NounError;

        if (VerbErrorPos + verb.Length > err.Length || NounErrorPos + noun.Length > err.Length)
        {
            throw new InvalidDataException("Failed to format noun error string");
        }

        return Replace(Replace(err, VerbErrorPos, verb), NounErrorPos, noun);
    }

    protected static string Replace(string str, int position, string with)
        => string.Concat(str.AsSpan(0, position), with, str.AsSpan(position + with.Length));

    protected static string SetChar(string str, int position, char c)
        => string.Concat(str.AsSpan(0, position), c.ToString(), str.AsSpan(position + 1));

    protected virtual async Task<(int Verb, int Noun)> GetInput()
    {
        while (true)
        {
            Display.PrintString(Strings.EnterCommand);
            string line = await GetLine();

            if (ShouldQuit || IsRestoring)
            {
                return (0, 0);
            }

            int index = 0;
            string verbString = GetWord(line, ref index);

            if (!Verbs.TryGetValue(verbString, out int verb))
            {
                Display.PrintString(FormatVerbError(verbString));
                continue;
            }

            string nounString = GetWord(line, ref index);

            if (!Nouns.TryGetValue(nounString, out int noun))
            {
                Display.PrintString(FormatNounError(verbString, nounString));
                continue;
            }

            return (verb, noun);
        }
    }

    // ---- words and commands ----

    protected void LoadWords(SeekableReadStream stream, Dictionary<string, int> map, List<string> pri, int count = 0)
    {
        int index = 0;

        map.Clear();
        pri.Clear();

        // WORKAROUND: several games contain word lists without a terminator
        switch (GameType)
        {
            case GameType.Hires3:
                count = ReferenceEquals(map, Verbs) ? 72 : 113;
                break;
            case GameType.Hires5:
                if (_state.Region == 15 && ReferenceEquals(map, Nouns))
                {
                    count = 81;
                }

                break;
        }

        var buf = new byte[IdiWordSize];
        while (true)
        {
            ++index;

            if (stream.Read(buf) < IdiWordSize)
            {
                throw new InvalidDataException("Error reading word list");
            }

            string word = Latin1(buf);
            map.TryAdd(word, index);
            pri.Add(ToAscii(word));

            byte synonyms = stream.ReadByte();

            if (stream.Eos)
            {
                throw new InvalidDataException("Error reading word list");
            }

            if (synonyms == 0xff || index == count)
            {
                break;
            }

            for (int i = 0; i < synonyms; ++i)
            {
                if (stream.Read(buf) < IdiWordSize)
                {
                    throw new InvalidDataException("Error reading word list");
                }

                map.TryAdd(Latin1(buf), index);
            }
        }
    }

    protected void ReadCommands(SeekableReadStream stream, List<Command> commands)
    {
        commands.Clear();

        while (true)
        {
            var command = new Command { Room = stream.ReadByte() };

            if (command.Room == 0xff)
            {
                return;
            }

            command.Verb = stream.ReadByte();
            command.Noun = stream.ReadByte();

            int scriptSize = (byte)(stream.ReadByte() - 6);

            command.NumCond = stream.ReadByte();
            command.NumAct = stream.ReadByte();

            for (int i = 0; i < scriptSize; ++i)
            {
                command.Script.Add(stream.ReadByte());
            }

            if (stream.Eos)
            {
                throw new InvalidDataException("Failed to read commands");
            }

            if (command.NumCond == 0 && command.Script[0] == IdoActSave)
            {
                _saveVerb = command.Verb;
                _saveNoun = command.Noun;
            }

            if (command.NumCond == 0 && command.Script[0] == IdoActLoad)
            {
                _restoreVerb = command.Verb;
                _restoreNoun = command.Noun;
            }

            commands.Add(command);
        }
    }

    protected static void RemoveCommand(List<Command> commands, int idx)
    {
        if (idx >= commands.Count)
        {
            throw new InvalidOperationException($"Command {idx} not found");
        }

        commands.RemoveAt(idx);
    }

    protected static Command GetCommand(List<Command> commands, int idx)
        => idx < commands.Count ? commands[idx] : throw new InvalidOperationException($"Command {idx} not found");

    protected void RemoveMessage(int idx)
    {
        if (Messages[idx] is null)
        {
            throw new InvalidOperationException($"Message {idx} not found");
        }

        Messages[idx] = null;
    }

    protected async Task CheckInput(byte verb, byte noun)
    {
        // try the room's command list first
        if (await DoOneCommand(CurrentRoomData.Commands, verb, noun))
        {
            return;
        }

        // if no match was found, try the global list
        if (!await DoOneCommand(RoomCommands, verb, noun))
        {
            await PrintMessage(MessageIdTable.DontUnderstand);
        }
    }

    protected virtual bool IsInputValid(byte verb, byte noun, out bool isAny)
        => IsInputValid(CurrentRoomData.Commands, verb, noun, out isAny) || IsInputValid(RoomCommands, verb, noun, out isAny);

    protected virtual bool IsInputValid(List<Command> commands, byte verb, byte noun, out bool isAny)
    {
        isAny = false;
        foreach (var cmd in commands)
        {
            var env = CreateScriptEnv(cmd, _state.Room, verb, noun);
            if (MatchCommand(env))
            {
                if (cmd.Verb == IdiAny || cmd.Noun == IdiAny)
                {
                    isAny = true;
                }

                return true;
            }
        }

        return false;
    }

    protected virtual void ApplyRoomWorkarounds(byte roomNr)
    {
    }

    protected virtual void ApplyRegionWorkarounds()
    {
    }

    // ---- graphics and sound ----

    protected void DrawPic(byte pic, int x = 0, int y = 0)
    {
        DataBlock? block;
        if (!CurrentRoomData.Pictures.TryGetValue(pic, out block) && !Pictures.TryGetValue(pic, out block))
        {
            throw new InvalidDataException($"Picture {pic} not found");
        }

        Graphics.DrawPic(block!.CreateReadStream(), x, y);
    }

    private static List<Tone> BellTones(int count)
    {
        var tones = new List<Tone>();
        for (int i = 0; i < count - 1; ++i)
        {
            tones.Add(new Tone(940.0, 100.0));
            tones.Add(new Tone(0.0, 12.0));
        }

        tones.Add(new Tone(940.0, 100.0));
        return tones;
    }

    public Task Bell(int count = 1) => PlayTones(BellTones(count), false);

    /// <summary>Plays tones on the speaker and waits for them; with <paramref name="allowSkip"/> a key stops them and returns true.</summary>
    protected async Task<bool> PlayTones(IReadOnlyList<Tone> tones, bool isMusic, bool allowSkip = false)
    {
        Speaker.Play(tones);

        while (!ShouldQuit && Speaker.IsPlaying)
        {
            bool key = PollEvent(out _);

            if (allowSkip && key)
            {
                // FIXME in ScummVM too: the key press is not preserved
                Speaker.Stop();
                return true;
            }

            await Poll();
        }

        return false;
    }

    // ---- state ----

    protected Region GetRegion(int i)
        => i >= 1 && i <= _state.Regions.Count ? _state.Regions[i - 1] : throw new InvalidOperationException($"Region {i} out of range [1, {_state.Regions.Count}]");

    protected Room GetRoom(int i)
        => i >= 1 && i <= _state.Rooms.Count ? _state.Rooms[i - 1] : throw new InvalidOperationException($"Room {i} out of range [1, {_state.Rooms.Count}]");

    protected Region GetCurRegion() => GetRegion(_state.Region);

    protected Room GetCurRoom() => GetRoom(_state.Room);

    protected Item GetItem(int i)
        => _state.Items.FirstOrDefault(item => item.Id == i) ?? throw new InvalidOperationException($"Item {i} not found");

    protected byte GetVar(int i)
        => i < _state.Vars.Count ? _state.Vars[i] : throw new InvalidOperationException($"Variable {i} out of range [0, {_state.Vars.Count - 1}]");

    protected void SetVar(int i, int value)
    {
        if (i >= _state.Vars.Count)
        {
            throw new InvalidOperationException($"Variable {i} out of range [0, {_state.Vars.Count - 1}]");
        }

        _state.Vars[i] = (byte)value;
    }

    protected static void ResizeVars(List<byte> vars, int size)
    {
        while (vars.Count < size)
        {
            vars.Add(0);
        }
    }

    protected virtual async Task TakeItem(byte noun)
    {
        foreach (var item in _state.Items)
        {
            if (item.Noun == noun && item.Room == _state.Room && item.Region == _state.Region)
            {
                if (item.State == ItemDoesntMove)
                {
                    await PrintMessage(MessageIdTable.ItemDoesntMove);
                    return;
                }

                if (item.State == ItemDropped)
                {
                    item.Room = IdiAny;
                    return;
                }

                foreach (byte pic in item.RoomPictures)
                {
                    if (pic == GetCurRoom().CurPicture)
                    {
                        item.Room = IdiAny;
                        item.State = ItemDropped;
                        return;
                    }
                }
            }
        }

        await PrintMessage(MessageIdTable.ItemNotHere);
    }

    protected virtual async Task DropItem(byte noun)
    {
        foreach (var item in _state.Items)
        {
            if (item.Noun == noun && item.Room == IdiAny)
            {
                item.Room = _state.Room;
                item.Region = _state.Region;
                item.State = ItemDropped;
                return;
            }
        }

        await PrintMessage(MessageIdTable.DontUnderstand);
    }

    protected bool MatchCommand(ScriptEnv env)
    {
        if (!env.IsMatch)
        {
            return false;
        }

        if (TraceScripts)
        {
            OpDebug($"IF\n\tROOM == {RoomStr(env.Command.Room)}");
            OpDebug($"\t&& SAID({VerbStr(env.Command.Verb)}, {NounStr(env.Command.Noun)})");
        }

        while (env.GetOpType() == ScriptEnv.OpType.Cond)
        {
            byte op = env.Op;

            if (op >= CondOpcodes.Count || CondOpcodes[op] is not { } opcode)
            {
                throw new InvalidDataException($"Unimplemented condition opcode {op:x2}");
            }

            int numArgs = opcode(env);

            if (numArgs < 0)
            {
                if (TraceScripts)
                {
                    OpDebug("FAIL\n");
                }

                return false;
            }

            env.Next(numArgs);
        }

        return true;
    }

    protected async Task DoActions(ScriptEnv env)
    {
        if (TraceScripts)
        {
            OpDebug("THEN");
        }

        while (env.GetOpType() == ScriptEnv.OpType.Act)
        {
            byte op = env.Op;

            if (op >= ActOpcodes.Count || ActOpcodes[op] is not { } opcode)
            {
                throw new InvalidDataException($"Unimplemented action opcode {op:x2}");
            }

            int numArgs = await opcode(env);

            if (numArgs < 0)
            {
                if (TraceScripts)
                {
                    OpDebug("ABORT\n");
                }

                return;
            }

            env.Next(numArgs);
        }

        if (TraceScripts)
        {
            OpDebug("END\n");
        }
    }

    protected async Task<bool> DoOneCommand(List<Command> commands, byte verb, byte noun)
    {
        foreach (var cmd in commands.ToArray())
        {
            var env = CreateScriptEnv(cmd, _state.Room, verb, noun);
            if (MatchCommand(env))
            {
                await DoActions(env);
                return true;
            }

            if (AbortScript)
            {
                AbortScript = false;
                return false;
            }
        }

        return false;
    }

    protected async Task DoAllCommands(List<Command> commands, byte verb, byte noun)
    {
        foreach (var cmd in commands.ToArray())
        {
            var env = CreateScriptEnv(cmd, _state.Room, verb, noun);
            if (MatchCommand(env))
            {
                await DoActions(env);

                // the original long jumps on restart, so this aborts here
                if (IsRestarting)
                {
                    return;
                }
            }

            if (AbortScript)
            {
                AbortScript = false;
                return;
            }
        }
    }

    protected virtual ScriptEnv CreateScriptEnv(Command cmd, byte room, byte verb, byte noun) => new ScriptEnv6502(cmd, room, verb, noun);

    // ---- saving ----

    protected virtual void LoadState(SeekableReadStream stream)
    {
        _state.Room = stream.ReadByte();
        _state.Moves = stream.ReadByte();
        _state.IsDark = stream.ReadByte() != 0;
        _state.Time.Hours = stream.ReadByte();
        _state.Time.Minutes = stream.ReadByte();

        uint size = stream.ReadUInt32BE();
        if (size != _state.Rooms.Count)
        {
            throw new InvalidDataException($"Room count mismatch (expected {_state.Rooms.Count}; found {size})");
        }

        foreach (var room in _state.Rooms)
        {
            room.Picture = stream.ReadByte();
            room.CurPicture = stream.ReadByte();
            room.IsFirstTime = stream.ReadByte() != 0;
        }

        // NOTE: the current picture is part of the original's save state; ScummVM reconstructs it
        _state.CurPicture = GetCurRoom().CurPicture;

        size = stream.ReadUInt32BE();
        if (size != _state.Items.Count)
        {
            throw new InvalidDataException($"Item count mismatch (expected {_state.Items.Count}; found {size})");
        }

        foreach (var item in _state.Items)
        {
            item.Room = stream.ReadByte();
            item.Picture = stream.ReadByte();
            item.PositionX = stream.ReadByte();
            item.PositionY = stream.ReadByte();
            item.State = stream.ReadByte();
        }

        size = stream.ReadUInt32BE();
        if (size != _state.Vars.Count)
        {
            throw new InvalidDataException($"Variable count mismatch (expected {_state.Vars.Count}; found {size})");
        }

        for (int i = 0; i < size; ++i)
        {
            _state.Vars[i] = stream.ReadByte();
        }
    }

    protected virtual void SaveState(BinaryWriter writer)
    {
        writer.Write(_state.Room);
        writer.Write((byte)_state.Moves);
        writer.Write(_state.IsDark ? (byte)1 : (byte)0);
        writer.Write(_state.Time.Hours);
        writer.Write(_state.Time.Minutes);

        WriteUInt32BE(writer, (uint)_state.Rooms.Count);
        foreach (var room in _state.Rooms)
        {
            writer.Write(room.Picture);
            writer.Write(room.CurPicture);
            writer.Write(room.IsFirstTime ? (byte)1 : (byte)0);
        }

        WriteUInt32BE(writer, (uint)_state.Items.Count);
        foreach (var item in _state.Items)
        {
            writer.Write(item.Room);
            writer.Write(item.Picture);
            writer.Write((byte)item.PositionX);
            writer.Write((byte)item.PositionY);
            writer.Write((byte)item.State);
        }

        WriteUInt32BE(writer, (uint)_state.Vars.Count);
        foreach (byte v in _state.Vars)
        {
            writer.Write(v);
        }
    }

    protected static void WriteUInt32BE(BinaryWriter writer, uint value)
    {
        writer.Write((byte)(value >> 24));
        writer.Write((byte)(value >> 16));
        writer.Write((byte)(value >> 8));
        writer.Write((byte)value);
    }

    private static void WriteUInt16BE(BinaryWriter writer, ushort value)
    {
        writer.Write((byte)(value >> 8));
        writer.Write((byte)value);
    }

    /// <summary>Loads a slot in ScummVM's ADL save format; true on success.</summary>
    protected bool LoadGameState(int slot)
    {
        string fileName = GetSaveStateName(slot);
        using var file = Host.Saves.OpenRead(fileName);
        if (file is null)
        {
            Host.Log.Warning("adl", $"Failed to open file '{fileName}'");
            return false;
        }

        using var memory = new MemoryStream();
        file.CopyTo(memory);
        var stream = new SeekableReadStream(memory.ToArray());

        if (stream.ReadUInt32BE() != SaveTag)
        {
            Host.Log.Warning("adl", $"No header found in '{fileName}'");
            return false;
        }

        byte saveVersion = stream.ReadByte();
        if (saveVersion != SaveGameVersion)
        {
            Host.Log.Warning("adl", $"Save game version {saveVersion} not supported");
            return false;
        }

        // description, then the save time
        stream.Skip(SaveGameNameLength);
        stream.Skip(6);
        stream.ReadUInt32BE(); // play time

        SkipThumbnail(stream);

        InitState();
        LoadState(stream);

        if (stream.Eos)
        {
            throw new InvalidDataException($"Failed to load game '{fileName}'");
        }

        IsRestoring = true;
        return true;
    }

    protected bool CanSaveGameStateCurrently() => CanSaveGameStateCurrentlyCore();

    /// <summary>Whether the game accepts "SAVE GAME" now; restoring state a test changes is up to overrides.</summary>
    protected virtual bool CanSaveGameStateCurrentlyCore()
    {
        if (!CanSaveNow)
        {
            return false;
        }

        // the game must currently accept the command "SAVE GAME"
        foreach (var cmd in CurrentRoomData.Commands)
        {
            var env = CreateScriptEnv(cmd, _state.Room, _saveVerb, _saveNoun);
            if (MatchCommand(env))
            {
                return env.Op == IdoActSave;
            }
        }

        foreach (var cmd in RoomCommands)
        {
            var env = CreateScriptEnv(cmd, _state.Room, _saveVerb, _saveNoun);
            if (MatchCommand(env))
            {
                return env.Op == IdoActSave;
            }
        }

        return false;
    }

    /// <summary>Saves to a slot in ScummVM's ADL save format; true on success.</summary>
    protected bool SaveGameState(int slot, string description)
    {
        string fileName = GetSaveStateName(slot);
        try
        {
            using var memory = new MemoryStream();
            using (var writer = new BinaryWriter(memory, Encoding.Latin1, leaveOpen: true))
            {
                WriteUInt32BE(writer, SaveTag);
                writer.Write(SaveGameVersion);

                var name = new byte[SaveGameNameLength];
                string text = description.Length > 0 ? description : $"Save {(char)('A' + slot)}";
                for (int i = 0; i < Math.Min(text.Length, SaveGameNameLength - 1); i++)
                {
                    name[i] = (byte)text[i];
                }

                writer.Write(name);

                var now = DateTime.Now;
                WriteUInt16BE(writer, (ushort)(now.Year - 1900));
                writer.Write((byte)(now.Month - 1));
                writer.Write((byte)now.Day);
                writer.Write((byte)now.Hour);
                writer.Write((byte)now.Minute);

                WriteUInt32BE(writer, (uint)_engineTime.TotalMilliseconds);

                WriteThumbnail(writer);
                SaveState(writer);
            }

            using var file = Host.Saves.OpenWrite(fileName);
            memory.Position = 0;
            memory.CopyTo(file);
            return true;
        }
        catch (IOException ex)
        {
            Host.Log.Warning("adl", $"Failed to save game '{fileName}': {ex.Message}");
            return false;
        }
    }

    /// <summary>A small picture of the screen in ScummVM's THMB block (version 2, RGB565, big-endian).</summary>
    private void WriteThumbnail(BinaryWriter writer)
    {
        WriteUInt32BE(writer, ThumbnailTag);
        WriteUInt32BE(writer, (uint)(4 + 4 + 1 + 2 + 2 + 9 + ThumbnailWidth * ThumbnailHeight * 2));
        writer.Write((byte)2);
        WriteUInt16BE(writer, ThumbnailWidth);
        WriteUInt16BE(writer, ThumbnailHeight);
        writer.Write([2, 3, 2, 3, 8, 11, 5, 0, 0]);

        var screen = Display.Screen;
        var palette = DisplayA2.Palette;
        for (int ty = 0; ty < ThumbnailHeight; ty++)
        {
            int y = ty * screen.Height / ThumbnailHeight;
            for (int tx = 0; tx < ThumbnailWidth; tx++)
            {
                int x = tx * screen.Width / ThumbnailWidth;
                Rgb c = palette[screen.Pixels[y * screen.Width + x]];
                WriteUInt16BE(writer, (ushort)(((c.R >> 3) << 11) | ((c.G >> 2) << 5) | (c.B >> 3)));
            }
        }
    }

    private static void SkipThumbnail(SeekableReadStream stream)
    {
        int position = stream.Pos;
        if (stream.ReadUInt32BE() != ThumbnailTag)
        {
            stream.Seek(position);
            return;
        }

        uint size = stream.ReadUInt32BE();
        stream.Seek(position + (int)size);
    }

    // ---- opcodes ----

    protected static ValueTask<int> Done(int numArgs) => new(numArgs);

    protected virtual int OIsItemInRoom(ScriptEnv e)
    {
        Trace(() => $"\t&& GET_ITEM_ROOM({ItemStr(e.Arg(1))}) == {ItemRoomStr(e.Arg(2))}");
        return GetItem(e.Arg(1)).Room == RoomArg(e.Arg(2)) ? 2 : -1;
    }

    protected virtual int OIsMovesGT(ScriptEnv e)
    {
        Trace(() => $"\t&& MOVES > {e.Arg(1)}");
        return _state.Moves > e.Arg(1) ? 1 : -1;
    }

    protected virtual int OIsVarEQ(ScriptEnv e)
    {
        Trace(() => $"\t&& VARS[{e.Arg(1)}] == {e.Arg(2)}");
        return GetVar(e.Arg(1)) == e.Arg(2) ? 2 : -1;
    }

    protected virtual int OIsCurPicEQ(ScriptEnv e)
    {
        Trace(() => $"\t&& GET_CURPIC() == {e.Arg(1)}");
        return _state.CurPicture == e.Arg(1) ? 1 : -1;
    }

    protected virtual int OIsItemPicEQ(ScriptEnv e)
    {
        Trace(() => $"\t&& GET_ITEM_PIC({ItemStr(e.Arg(1))}) == {e.Arg(2)}");
        return GetItem(e.Arg(1)).Picture == e.Arg(2) ? 2 : -1;
    }

    protected virtual ValueTask<int> OVarAdd(ScriptEnv e)
    {
        Trace(() => $"\tVARS[{e.Arg(2)}] += {e.Arg(1)}");
        SetVar(e.Arg(2), GetVar(e.Arg(2)) + e.Arg(1));
        return Done(2);
    }

    protected virtual ValueTask<int> OVarSub(ScriptEnv e)
    {
        Trace(() => $"\tVARS[{e.Arg(2)}] -= {e.Arg(1)}");
        SetVar(e.Arg(2), GetVar(e.Arg(2)) - e.Arg(1));
        return Done(2);
    }

    protected virtual ValueTask<int> OVarSet(ScriptEnv e)
    {
        Trace(() => $"\tVARS[{e.Arg(1)}] = {e.Arg(2)}");
        SetVar(e.Arg(1), e.Arg(2));
        return Done(2);
    }

    protected virtual async ValueTask<int> OListInv(ScriptEnv e)
    {
        Trace(() => "\tLIST_INVENTORY()");

        foreach (var item in _state.Items.ToArray())
        {
            if (item.Room == IdiAny)
            {
                await PrintString(GetItemDescription(item));
            }
        }

        return 0;
    }

    protected virtual ValueTask<int> OMoveItem(ScriptEnv e)
    {
        Trace(() => $"\tSET_ITEM_ROOM({ItemStr(e.Arg(1))}, {ItemRoomStr(e.Arg(2))})");
        GetItem(e.Arg(1)).Room = e.Arg(2);
        return Done(2);
    }

    protected virtual ValueTask<int> OSetRoom(ScriptEnv e)
    {
        Trace(() => $"\tROOM = {e.Arg(1)}");
        SwitchRoom(e.Arg(1));
        return Done(1);
    }

    protected virtual ValueTask<int> OSetCurPic(ScriptEnv e)
    {
        Trace(() => $"\tSET_CURPIC({e.Arg(1)})");
        GetCurRoom().CurPicture = e.Arg(1);
        return Done(1);
    }

    protected virtual ValueTask<int> OSetPic(ScriptEnv e)
    {
        Trace(() => $"\tSET_PIC({e.Arg(1)})");
        GetCurRoom().Picture = GetCurRoom().CurPicture = e.Arg(1);
        return Done(1);
    }

    protected virtual async ValueTask<int> OPrintMsg(ScriptEnv e)
    {
        Trace(() => $"\tPRINT({MsgStr(e.Arg(1))})");
        await PrintMessage(e.Arg(1));
        return 1;
    }

    protected virtual ValueTask<int> OSetLight(ScriptEnv e)
    {
        Trace(() => "\tLIGHT()");
        _state.IsDark = false;
        return Done(0);
    }

    protected virtual ValueTask<int> OSetDark(ScriptEnv e)
    {
        Trace(() => "\tDARK()");
        _state.IsDark = true;
        return Done(0);
    }

    protected virtual ValueTask<int> OSave(ScriptEnv e)
    {
        Trace(() => "\tSAVE_GAME()");
        SaveGameState(0, string.Empty);
        return Done(0);
    }

    protected virtual ValueTask<int> ORestore(ScriptEnv e)
    {
        Trace(() => "\tRESTORE_GAME()");
        LoadGameState(0);
        IsRestoring = false;
        return Done(0);
    }

    protected virtual async ValueTask<int> ORestart(ScriptEnv e)
    {
        Trace(() => "\tRESTART_GAME()");

        Display.PrintString(Strings.PlayAgain);
        string input = await InputString();

        if (input.Length == 0 || input[0] != Display.AsciiToNative('N'))
        {
            IsRestarting = true;
            Graphics.ClearScreen();
            Display.RenderGraphics();
            Display.PrintString(Strings.PressReturn);
            InitState();
            Display.PrintAsciiString(Strings.LineFeeds);
            return -1;
        }

        return await OQuit(e);
    }

    protected virtual async ValueTask<int> OQuit(ScriptEnv e)
    {
        Trace(() => "\tQUIT_GAME()");

        await PrintMessage(MessageIdTable.ThanksForPlaying);

        // wait for a key so the player gets a chance to read the thank-you message
        Display.PrintAsciiString("PRESS ANY KEY TO QUIT");
        await InputKey();

        // IsRestarting aborts the current game loop iteration
        IsQuitting = IsRestarting = true;
        return -1;
    }

    protected virtual ValueTask<int> OPlaceItem(ScriptEnv e)
    {
        Trace(() => $"\tPLACE_ITEM({ItemStr(e.Arg(1))}, {ItemRoomStr(e.Arg(2))}, ({e.Arg(3)}, {e.Arg(4)}))");
        var item = GetItem(e.Arg(1));
        item.Room = RoomArg(e.Arg(2));
        item.PositionX = e.Arg(3);
        item.PositionY = e.Arg(4);
        return Done(4);
    }

    protected virtual ValueTask<int> OSetItemPic(ScriptEnv e)
    {
        Trace(() => $"\tSET_ITEM_PIC({ItemStr(e.Arg(2))}, {e.Arg(1)})");
        GetItem(e.Arg(2)).Picture = e.Arg(1);
        return Done(2);
    }

    protected virtual ValueTask<int> OResetPic(ScriptEnv e)
    {
        Trace(() => "\tRESET_PIC()");
        GetCurRoom().CurPicture = GetCurRoom().Picture;
        return Done(0);
    }

    protected virtual async ValueTask<int> GoDirection(ScriptEnv e, Direction dir)
    {
        Trace(() => $"\tGO_{DirStr(dir)}()");

        byte room = GetCurRoom().Connections[(int)dir];

        if (room == 0)
        {
            await PrintMessage(MessageIdTable.CantGoThere);
            return -1;
        }

        SwitchRoom(room);
        return -1;
    }

    protected virtual async ValueTask<int> OTakeItem(ScriptEnv e)
    {
        Trace(() => "\tTAKE_ITEM()");
        await TakeItem(e.Noun);
        return 0;
    }

    protected virtual async ValueTask<int> ODropItem(ScriptEnv e)
    {
        Trace(() => "\tDROP_ITEM()");
        await DropItem(e.Noun);
        return 0;
    }

    protected virtual ValueTask<int> OSetRoomPic(ScriptEnv e)
    {
        Trace(() => $"\tSET_ROOM_PIC({e.Arg(1)}, {e.Arg(2)})");
        GetRoom(e.Arg(1)).Picture = GetRoom(e.Arg(1)).CurPicture = e.Arg(2);
        return Done(2);
    }

    // ---- debug strings ----

    protected void Trace(Func<string> message)
    {
        if (TraceScripts)
        {
            OpDebug(message());
        }
    }

    private void OpDebug(string message) => Host.Log.Debug(ScriptLogCategory, message);

    /// <summary>Native text as ASCII: high bits cleared. Port of Console::toAscii.</summary>
    public static string ToAscii(string str)
    {
        var chars = str.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)(chars[i] & 0x7f);
        }

        return new string(chars);
    }

    protected static string Latin1(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            chars[i] = (char)bytes[i];
        }

        return new string(chars);
    }

    protected string ItemStr(int i)
    {
        var item = GetItem(i);
        string name = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (item.Noun > 0 && item.Noun - 1 < PriNouns.Count)
        {
            name += "/" + PriNouns[item.Noun - 1];
        }

        string desc = GetItemDescription(item);
        if (desc.Length > 0)
        {
            name += "/" + ToAscii(desc).TrimEnd('\r');
        }

        return name;
    }

    protected static string ItemRoomStr(int i) => i switch
    {
        IdiAny => "CARRYING",
        IdiVoidRoom => "GONE",
        IdiCurRoom => "HERE",
        _ => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    protected static string RoomStr(int i) => i == IdiAny ? "*" : i.ToString(System.Globalization.CultureInfo.InvariantCulture);

    protected string VerbStr(int i) => i == IdiAny ? "*" : $"{i}/{(i - 1 >= 0 && i - 1 < PriVerbs.Count ? PriVerbs[i - 1] : "<INVALID>")}";

    protected string NounStr(int i) => i == IdiAny ? "*" : $"{i}/{(i - 1 >= 0 && i - 1 < PriNouns.Count ? PriNouns[i - 1] : "<INVALID>")}";

    protected string MsgStr(int i) => $"{i}/{ToAscii(LoadMessage(i)).TrimEnd('\r')}";

    protected static string DirStr(Direction dir) => dir.ToString().ToUpperInvariant();

    /// <summary>Strings the executables print, found by <see cref="ExtractExeStrings"/> or read at fixed offsets.</summary>
    protected sealed class EngineStrings
    {
        public string EnterCommand { get; set; } = string.Empty;

        public string VerbError { get; set; } = string.Empty;

        public string NounError { get; set; } = string.Empty;

        public string PlayAgain { get; set; } = string.Empty;

        public string PressReturn { get; set; } = string.Empty;

        public string LineFeeds { get; set; } = string.Empty;
    }

    /// <summary>Messages the engine prints outside of scripts.</summary>
    protected sealed class MessageIds
    {
        public int CantGoThere { get; set; }

        public int DontUnderstand { get; set; }

        public int ItemDoesntMove { get; set; }

        public int ItemNotHere { get; set; }

        public int ThanksForPlaying { get; set; }
    }
}
