using Daventry.Adl.Detection;
using Daventry.Core.Formats;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;

namespace Daventry.Adl;

/// <summary>
/// Hi-Res Adventure #6: The Dark Crystal (1982). Room descriptions run through the global
/// commands, text has singular and plural variants for when Kira is with Jen, and the suns move
/// together as the moves go by. Port of ScummVM's HiRes6Engine.
/// </summary>
public sealed class HiRes6Engine : AdlEngineV5
{
    private const int SectorsPerTrack = 16;
    private const int BytesPerSector = 256;
    private const int Regions = 3;
    private const int ItemCount = 15;

    // a combined total of 91 rooms
    private static readonly byte[] RoomsPerRegion = [35, 29, 27];

    private byte _currVerb, _currNoun;

    public HiRes6Engine(IHost host, IGameContainer game, AdlDetectedGame detected, DisplayOptions options)
        : base(host, game, detected, options)
    {
    }

    protected override async Task GameLoop()
    {
        await base.GameLoop();

        // variable 25 starts at 5 and counts down every 160 moves; at 0 the game ends. It decides
        // what "LOOK SUNS" shows. Variable 39 advances the suns on game events, so even a fast
        // player sees them getting closer together as the game goes on.
        if (GetVar(39) != 0)
        {
            if (GetVar(39) < GetVar(25))
            {
                SetVar(25, GetVar(39));
            }

            SetVar(39, 0);
        }

        if (GetVar(25) != 0)
        {
            if (GetVar(25) > 5)
            {
                throw new InvalidDataException($"Variable 25 has unexpected value {GetVar(25)}");
            }

            if ((6 - GetVar(25)) * 160 == _state.Moves)
            {
                SetVar(25, GetVar(25) - 1);
            }
        }
    }

    protected override void SetupOpcodeTables()
    {
        base.SetupOpcodeTables();
        ActOpcodes[0x1e] = e => OFluteSound(e);
    }

    protected override async ValueTask<int> GoDirection(ScriptEnv e, Direction dir)
    {
        Trace(() => $"\tGO_{DirStr(dir)}()");

        byte room = GetCurRoom().Connections[(int)dir];

        if (room == 0)
        {
            // invalid directions are not penalised at an escapable Garthim encounter
            if (GetVar(33) == 2)
            {
                SetVar(34, GetVar(34) + 1);
            }

            await PrintMessage(MessageIdTable.CantGoThere);
            return -1;
        }

        SwitchRoom(room);

        // going to another room escapes an escapable Garthim encounter
        if (GetVar(33) == 2)
        {
            await PrintMessage(102);
            SetVar(33, 0);
        }

        return -1;
    }

    private async ValueTask<int> OFluteSound(ScriptEnv e)
    {
        Trace(() => "\tFLUTE_SOUND()");

        await PlayTones([new Tone(1072.0, 587.6), new Tone(1461.0, 495.8), new Tone(0.0, 1298.7)], false);

        LinesPrinted = 0;
        return 0;
    }

    protected override bool CanSaveGameStateCurrentlyCore()
    {
        if (!CanSaveNow)
        {
            return false;
        }

        // back up the variables this test may change
        byte var2 = GetVar(2);
        byte var24 = GetVar(24);
        bool abortScript = AbortScript;

        bool result = base.CanSaveGameStateCurrentlyCore();

        SetVar(2, var2);
        SetVar(24, var24);
        AbortScript = abortScript;
        return result;
    }

    /// <summary>Reads sectors from the last down to the first of each track, skipping the catalog track.</summary>
    private static SeekableReadStream LoadSectors(DiskImage disk, int track, int sector = SectorsPerTrack - 1, int count = SectorsPerTrack)
    {
        var buf = new byte[count * BytesPerSector];
        int p = 0;

        while (count-- > 0)
        {
            var stream = disk.CreateReadStream(track, sector) ?? throw new InvalidDataException("Error loading from disk image");
            if (stream.Read(buf.AsSpan(p, BytesPerSector)) < BytesPerSector)
            {
                throw new InvalidDataException("Error loading from disk image");
            }

            p += BytesPerSector;
            if (sector > 0)
            {
                --sector;
            }
            else
            {
                ++track;

                // skip the VTOC track
                if (track == 17)
                {
                    ++track;
                }

                sector = SectorsPerTrack - 1;
            }
        }

        return new SeekableReadStream(buf);
    }

    protected override async Task RunIntro()
    {
        InsertDisk(0);

        var stream = LoadSectors(Disk!, 11, 1, 96);

        Display.SetMode(Adl.Display.Mode.Graphics);
        Display.LoadFrameBuffer(stream);
        Display.RenderGraphics();
        await Delay(256 * 8609 / 1000);

        Display.LoadFrameBuffer(stream);
        Display.RenderGraphics();
        await Delay(256 * 8609 / 1000);

        Display.LoadFrameBuffer(stream);

        // the copyright string from the boot file
        var files = AppleDosFiles.Open(OpenDisk(0));
        var boot = files.CreateReadStream("\u0008\u0008\u0008\u0008\u0008\u0008");
        string copyright = ReadStringAt(boot, 0x103, (byte)Display.AsciiToNative('\r'));

        Display.RenderGraphics();
        Display.Home();
        Display.SetMode(Adl.Display.Mode.Mixed);
        Display.MoveCursorTo(0, 21);
        Display.PrintString(copyright);
        await Delay(256 * 8609 / 1000);
    }

    protected override void Init()
    {
        Graphics = new GraphicsManV3(Display);

        InsertDisk(0);

        LoadRegionLocations(Read(0x3, 0xf, 0x05), Regions);
        LoadRegionInitDataOffsets(Read(0x5, 0xa, 0x07), Regions);

        var stream = LoadSectors(Disk!, 0x7);

        Strings.VerbError = ReadStringAt(stream, 0x666);
        Strings.NounError = ReadStringAt(stream, 0x6bd);
        Strings.EnterCommand = ReadStringAt(stream, 0x6e9);

        Strings.LineFeeds = ReadStringAt(stream, 0x408);

        StringsV2.SaveInsert = ReadStringAt(stream, 0xad8);
        StringsV2.SaveReplace = ReadStringAt(stream, 0xb95);
        StringsV2.RestoreInsert = ReadStringAt(stream, 0xc07);
        Strings.PlayAgain = ReadStringAt(stream, 0xcdf, 0xff);

        MessageIdTable.CantGoThere = 249;
        MessageIdTable.DontUnderstand = 247;
        MessageIdTable.ItemDoesntMove = 253;
        MessageIdTable.ItemNotHere = 254;
        MessageIdTable.ThanksForPlaying = 252;

        stream = LoadSectors(Disk!, 0x6, 0xb, 2);
        stream.Seek(0x16);
        LoadItemDescriptions(stream, ItemCount);

        LoadDroppedItemOffsets(Read(0x8, 0x9, 0x16), 16);
        LoadItemPicIndex(Read(0xb, 0xd, 0x08), ItemCount);
    }

    protected override void InitGameState()
    {
        ResizeVars(_state.Vars, 40);

        InsertDisk(0);

        LoadItems(Read(0x3, 0xe, 0x03));

        InitRegions(RoomsPerRegion);

        LoadRegion(1);

        _currVerb = _currNoun = 0;
    }

    protected override async Task ShowRoom()
    {
        _state.CurPicture = GetCurRoom().CurPicture;

        bool redrawPic = false;

        if (GetVar(26) == 0xfe)
        {
            SetVar(26, 0);
        }
        else if (GetVar(26) != 0xff)
        {
            SetVar(26, _state.Room);
        }

        if (_state.Room != RoomOnScreen)
        {
            LoadRoom(_state.Room);

            if (GetVar(26) < 0x80 && GetCurRoom().IsFirstTime)
            {
                SetVar(26, 0);
            }

            Graphics.ClearScreen();

            if (!_state.IsDark)
            {
                redrawPic = true;
            }
        }
        else if (GetCurRoom().CurPicture != PicOnScreen || ItemRemoved)
        {
            redrawPic = true;
        }

        if (redrawPic)
        {
            RoomOnScreen = _state.Room;
            PicOnScreen = GetCurRoom().CurPicture;

            DrawPic(GetCurRoom().CurPicture);
            ItemRemoved = false;
            ItemsOnScreen = 0;

            foreach (var item in _state.Items)
            {
                item.IsOnScreen = false;
            }
        }

        if (!_state.IsDark)
        {
            DrawItems();
        }

        Display.RenderGraphics();
        SetVar(2, 0xff);
        await PrintString(CurrentRoomData.Description);
    }

    protected override string FormatVerbError(string verb)
    {
        var err = Strings.VerbError.ToCharArray();

        for (int i = 0; i < verb.Length; ++i)
        {
            err[i + 24] = verb[i];
        }

        char spaceChar = Display.AsciiToNative(' ');
        err[32] = spaceChar;

        int pos = 24;
        while (err[pos] != spaceChar)
        {
            ++pos;
        }

        err[pos] = Display.AsciiToNative('.');
        return new string(err);
    }

    protected override string FormatNounError(string verb, string noun)
    {
        var err = Strings.NounError.ToCharArray();

        for (int i = 0; i < noun.Length; ++i)
        {
            err[i + 24] = noun[i];
        }

        char spaceChar = Display.AsciiToNative(' ');

        for (int i = 35; i > 31; --i)
        {
            err[i] = spaceChar;
        }

        int pos = 24;
        while (err[pos] != spaceChar)
        {
            ++pos;
        }

        err[pos + 1] = Display.AsciiToNative('I');
        err[pos + 2] = Display.AsciiToNative('S');
        err[pos + 3] = Display.AsciiToNative('.');
        return new string(err);
    }

    protected override void LoadState(SeekableReadStream stream)
    {
        base.LoadState(stream);
        _state.Moves = (ushort)((GetVar(39) << 8) | GetVar(24));
        SetVar(39, 0);
    }

    protected override void SaveState(BinaryWriter writer)
    {
        // the move counter is stuffed into variables to save it
        SetVar(24, _state.Moves & 0xff);
        SetVar(39, _state.Moves >> 8);
        base.SaveState(writer);
        SetVar(39, 0);
    }

    protected override async Task PrintString(string str)
    {
        var s = new System.Text.StringBuilder();
        int found = 0;

        // variable 27 is 1 when Kira is present and 0 otherwise; it chooses between the singular
        // and plural variants of a string. The original's corner cases are not emulated, hence
        // the check
        if (GetVar(27) > 1)
        {
            throw new InvalidDataException($"Invalid value {GetVar(27)} encountered for variable 27");
        }

        foreach (char c in str)
        {
            if (c == '%')
            {
                ++found;
                if (found == 3)
                {
                    found = 0;
                }
            }
            else if (found == 0 || found - 1 == GetVar(27))
            {
                s.Append(c);
            }
        }

        // variables 2 and 26 control the printing of room descriptions
        if (GetVar(2) == 0xff)
        {
            if (GetVar(26) == 0)
            {
                // the special room description " "
                if (str.Length == 1 && Display.AsciiToNative(str[0]) == Display.AsciiToNative(' '))
                {
                    SetVar(2, 160);
                }
                else
                {
                    await base.PrintString(s.ToString());
                    SetVar(2, 1);
                }
            }
            else if (GetVar(26) == 0xff)
            {
                // storing the room number in a variable allows range comparisons
                SetVar(26, _state.Room);
                SetVar(2, 1);
            }
            else
            {
                SetVar(2, 80);
            }

            await DoAllCommands(GlobalCommands, _currVerb, _currNoun);
        }
        else
        {
            await base.PrintString(s.ToString());
        }
    }

    private SeekableReadStream Read(int track, int sector, int offset = 0, int size = 0)
        => Disk!.CreateReadStream(track, sector, offset, size)
            ?? throw new InvalidDataException($"Error reading disk image at track {track}, sector {sector}");
}
