using Daventry.Adl.Detection;
using Daventry.Core.Formats;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;

namespace Daventry.Adl;

/// <summary>
/// Hi-Res Adventure #5: Time Zone (1982), twelve disk sides and 41 regions of history. Items
/// carried into another era can vanish, the inventory has a weight limit, and the time machine
/// flashes its lights. Port of ScummVM's HiRes5Engine.
/// </summary>
public sealed class HiRes5Engine : AdlEngineV4
{
    private const int Clock = 1022727; // Apple II CPU clock rate
    private const int Regions = 41;
    private const int ItemCount = 69;

    // a combined total of 1213 rooms
    private static readonly byte[] RoomsPerRegion =
    [
        6, 16, 24, 57, 40, 30, 76, 40,
        54, 38, 44, 21, 26, 42, 49, 32,
        59, 69, 1, 1, 1, 1, 1, 18,
        25, 13, 28, 28, 11, 23, 9, 31,
        6, 29, 29, 34, 9, 10, 95, 86,
        1,
    ];

    private readonly List<byte> _itemTimeLimits = [];
    private readonly List<Tone> _song = [];
    private string _itemTimeLimitMessage = string.Empty;
    private string _carryingTooMuchMessage = string.Empty;
    private bool _doAnimation;

    public HiRes5Engine(IHost host, IGameContainer game, AdlDetectedGame detected, DisplayOptions options)
        : base(host, game, detected, options)
    {
    }

    protected override async Task<string> GetLine()
    {
        if (_doAnimation)
        {
            await AnimateLights();
            _doAnimation = false;
        }

        return await base.GetLine();
    }

    private void DrawLight(int index, byte color)
    {
        ReadOnlySpan<byte> xCoord = [189, 161, 133, 105, 77];
        const int yCoord = 72;

        for (int yDelta = 0; yDelta < 4; ++yDelta)
        {
            for (int xDelta = 0; xDelta < 7; ++xDelta)
            {
                Display.PutPixel(xCoord[index] + xDelta, yCoord + yDelta, color);
            }
        }

        Display.RenderGraphics();
    }

    private async Task AnimateLights()
    {
        int index;
        byte color = 0x2a;

        for (index = 4; index >= 0; --index)
        {
            DrawLight(index, color);
        }

        index = 4;

        while (!ShouldQuit)
        {
            DrawLight(index, (byte)(color ^ 0x7f));

            // the original has a delay here; ScummVM leaves it out as it is slower than the original already
            const int loopCycles = 25;
            int period = (index + 1) << 4;
            double freq = Clock / 2.0 / (period * loopCycles);
            double len = 128 * period * loopCycles * 1000 / (double)Clock;

            if (await PlayTones([new Tone(freq, len)], false, true))
            {
                break;
            }

            DrawLight(index, (byte)(color ^ 0xff));

            if (--index < 0)
            {
                index = 4;
                color ^= 0xff;
            }
        }
    }

    protected override void SetupOpcodeTables()
    {
        base.SetupOpcodeTables();

        ActOpcodes[0x0b] = e => OCheckItemTimeLimits(e);
        ActOpcodes[0x13] = e => OStartAnimation(e);
        ActOpcodes[0x1e] = e => OWinGame(e);
    }

    protected override async Task<bool> IsInventoryFull()
    {
        int weight = 0;

        foreach (var item in _state.Items)
        {
            if (item.Room == IdiAny)
            {
                weight = (byte)(weight + item.Description);
            }
        }

        if (weight >= 100)
        {
            Display.PrintString(_carryingTooMuchMessage);
            await InputString();
            return true;
        }

        return false;
    }

    private void LoadSong(SeekableReadStream stream)
    {
        while (true)
        {
            byte period = stream.ReadByte();

            if (stream.Eos)
            {
                throw new InvalidDataException("Error loading song");
            }

            if (period == 0xff)
            {
                return;
            }

            byte length = stream.ReadByte();

            if (stream.Eos)
            {
                throw new InvalidDataException("Error loading song");
            }

            const int loopCycles = 20; // delay loop cycles

            // this ignores the cycles spent on overflow handling and speaker control, so the
            // tones are slightly higher than on original hardware, as in ScummVM
            double freq = period != 0 ? Clock / 2.0 / (period * loopCycles) : 0.0;
            double len = (length > 0 ? length - 1 : 255) * 256 * loopCycles * 1000 / (double)Clock;

            _song.Add(new Tone(freq, len));
        }
    }

    private async ValueTask<int> OCheckItemTimeLimits(ScriptEnv e)
    {
        Trace(() => $"\tCHECK_ITEM_TIME_LIMITS(VARS[{e.Arg(1)}])");

        bool lostAnItem = false;

        foreach (var item in _state.Items)
        {
            byte room = item.Room;
            byte region = item.Region;

            if (room == IdiAny || room == IdiCurRoom || (room == _state.Room && region == _state.Region))
            {
                if (GetVar(e.Arg(1)) < _itemTimeLimits[item.Id - 1])
                {
                    item.Room = IdiVoidRoom;
                    lostAnItem = true;
                }
            }
        }

        if (lostAnItem)
        {
            Display.PrintString(_itemTimeLimitMessage);
            await InputString();
        }

        return 1;
    }

    private ValueTask<int> OStartAnimation(ScriptEnv e)
    {
        Trace(() => "\tSTART_ANIMATION()");
        _doAnimation = true;
        return Done(0);
    }

    private async ValueTask<int> OWinGame(ScriptEnv e)
    {
        Trace(() => "\tWIN_GAME()");

        await ShowRoom();
        await PlayTones(_song, true);

        return await OQuit(e);
    }

    protected override async Task RunIntro()
    {
        InsertDisk(2);

        var stream = Read(0x10, 0x0, 0x00, 31);

        Display.SetMode(Adl.Display.Mode.Graphics);
        Display.LoadFrameBuffer(stream);
        Display.RenderGraphics();

        await InputKey();

        Display.Home();
        Display.SetMode(Adl.Display.Mode.Text);

        string menu = ReadString(Read(0x03, 0xc, 0x34, 1));

        while (!ShouldQuit)
        {
            Display.Home();
            Display.PrintString(menu);

            string cmd = await InputString();

            // the backup and format options are ignored
            if (cmd.Length > 0 && cmd[0] == Display.AsciiToNative('1'))
            {
                break;
            }
        }
    }

    protected override void Init()
    {
        Graphics = new GraphicsManV3(Display);

        InsertDisk(2);

        LoadRegionLocations(Read(0x5, 0x0, 0x02), Regions);
        LoadRegionInitDataOffsets(Read(0xd, 0x2, 0x04), Regions);

        var stream = Read(0x7, 0xe);
        Strings.VerbError = ReadStringAt(stream, 0x4f);
        Strings.NounError = ReadStringAt(stream, 0x8e);
        Strings.EnterCommand = ReadStringAt(stream, 0xbc);

        Strings.LineFeeds = ReadString(Read(0x7, 0xc));

        stream = Read(0x8, 0x3, 0x00, 2);
        StringsV2.SaveInsert = ReadStringAt(stream, 0x66);
        StringsV2.SaveReplace = ReadStringAt(stream, 0x112);
        StringsV2.RestoreInsert = ReadStringAt(stream, 0x180);
        Strings.PlayAgain = ReadStringAt(stream, 0x247, 0xff);

        MessageIdTable.CantGoThere = 110;
        MessageIdTable.DontUnderstand = 112;
        MessageIdTable.ItemDoesntMove = 114;
        MessageIdTable.ItemNotHere = 115;
        MessageIdTable.ThanksForPlaying = 113;

        LoadItemDescriptions(Read(0xe, 0x1, 0x13, 4), ItemCount);
        LoadDroppedItemOffsets(Read(0x8, 0xd, 0xfd, 1), 16);
        LoadItemPicIndex(Read(0xb, 0xa, 0x05, 1), ItemCount);

        stream = Read(0x7, 0x8, 0x01);
        for (int i = 0; i < ItemCount; ++i)
        {
            _itemTimeLimits.Add(stream.ReadByte());
        }

        if (stream.Eos)
        {
            throw new InvalidDataException("Failed to read item time limits");
        }

        _itemTimeLimitMessage = ReadString(Read(0x8, 0x2, 0x2d));
        _carryingTooMuchMessage = ReadString(Read(0x8, 0x7, 0x02));

        LoadSong(Read(0xc, 0xb, 0x20));
    }

    protected override void InitGameState()
    {
        ResizeVars(_state.Vars, 40);

        InsertDisk(2);

        LoadItems(Read(0x5, 0x1, 0x00, 3));

        InitRegions(RoomsPerRegion);

        LoadRegion(1);
        _state.Room = 5;

        _doAnimation = false;
    }

    protected override void ApplyRegionWorkarounds()
    {
        // WORKAROUNDs from ScummVM: remove or fix buggy commands
        switch (_state.Region)
        {
            case 3:
                // "USE PIN" references a missing message, but cannot be triggered because the
                // "USE" verb is shadowed; removed anyway so that script dumping can proceed
                RemoveCommand(RoomCommands, 12);
                break;
            case 14:
                // "WITH SHOVEL" references a missing message: game-breaking in the original but
                // unlikely, as "DIG" doesn't ask what to dig with
                RemoveCommand(RoomCommands, 0);
                break;
            case 32:
                // a broken message right before the game restarts, probably about falling down
                // some stairs
                RemoveMessage(29);
                break;
            case 40:
                // locking the gate prints a broken message followed by "O.K."
                RemoveMessage(172);
                break;
        }
    }

    protected override void ApplyRoomWorkarounds(byte roomNr)
    {
        // WORKAROUND from ScummVM: "GET WATER" references a missing message when you already
        // have water; it should be 117 instead of 17
        if (_state.Region == 17 && roomNr == 49)
        {
            GetCommand(CurrentRoomData.Commands, 8).Script[4] = 117;
        }
    }

    private SeekableReadStream Read(int track, int sector, int offset = 0, int size = 0)
        => Disk!.CreateReadStream(track, sector, offset, size)
            ?? throw new InvalidDataException($"Error reading disk image at track {track}, sector {sector}");
}
