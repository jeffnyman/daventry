using System.Buffers.Binary;
using Daventry.Agi.Graphics;
using Daventry.Agi.Sound;
using Daventry.Core.Hosting;
using Daventry.Core.Input;
using Daventry.Core.Storage;

namespace Daventry.Agi.PreAgi;

/// <summary>
/// Winnie the Pooh in the Hundred Acre Wood (1985). Port of ScummVM's WinnieEngine.
/// <para>
/// Each room is a file with a picture, a few description strings, menu options and a tiny
/// script per option; each object is a file with a picture and four strings. Ten objects are
/// scattered over the wood at random and the player returns them to their owners by carrying
/// one at a time. The DOS and Amiga versions are supported; the Apple II, C64 and CoCo
/// versions have another picture format that ScummVM does not play either.
/// </para>
/// </summary>
public sealed class WinnieEngine : PreAgiEngine
{
    private const int SaveGameVersion = 1;
    private const int XorKey = 0x80;
    private const string SaveFileName = "winnie.savegame";

    private const string RoomDos = "rooms/rm.{0:00}";
    private const string RoomAmiga = "rooms/room.{0}";
    private const string ObjDos = "obj.{0:00}";
    private const string ObjAmiga = "objects/object.{0}";
    private const string SndDos = "snd.{0:00}";
    private const string FileLogo = "logo";
    private const string FileTitle = "title";

    private const string Intro0 = "                 PRESENT";
    private const string Intro1 = "       TM designates trademark of\n          Sierra On-Line, Inc.\n    (c) 1985 Walt Disney Productions";

    private const string Help0 = "The <SPACE BAR> moves the pointer.      Press <RETURN> when it is by the choice you want.  Press the <Backspace> key to see what you just finished reading.";
    private const string Help1 = "Press <C> to see what you are carrying. <Ctrl-S> turns the sound off and on.    <ESC> takes you to the playroom (in caseyou get lost or want to save the game).";

    private const string GameOver0 = "Congratulations!!  You did it!  You     returned everything that was lost.  Now,Christopher Robin invites you to a Hero party.";
    private const string GameOver1 = "The good news is:  YOU are the Hero!!   The bad news is:  you have to find the  party by yourself.  Good luck!";

    private const string Owl0 = "\"For example, that object you are       carrying now is interesting.  I know    I've seen it before.  Hmmm.  Let me     think about this . . .\"";

    private const string Wind0 = "Oh, no!  The Blustery Wind begins to    howl.  It has returned, and mixed up    all the objects in the Wood.";
    private const string Wind1 = "But don't worry.  Everyone still has theobjects you returned to them.\n\n             (Today must be Winds-day!)";
    private const string TiggerText = "\"Hallooooo, there!!!!  It's ME, Tigger! Let's BOUNCE!\"";
    private const string MistText = "Oh, look out!  The mysterious mist is   coming in.  It gets so thick that you   can't see through it.  Just keep walkingand it will soon clear up.";

    private const string Song0 = "Winnie-the-Pooh, Winnie-the-Pooh, Tubby little cubby all stuffed with fluff,    He's Winnie-the-Pooh, Winnie-the-Pooh,  Willy, nilly, silly, old bear.";
    private const string Song1 = "Deep in the Hundred Acre Wood,          Where Christopher Robin plays,          You will find the enchanted neighborhoodof Christopher's childhood days.";
    private const string Song2 = "A donkey named Eeyore is his friend,    and Kanga and little Roo.  There's      Rabbit and Piglet and there's Owl       But most of all Winnie-the-Pooh!";

    private const string Nsew = "North  South  East  West";
    private const string Take = "Take";
    private const string Drop = "Drop";
    private const string CantGo = "\nSorry, but you can't go that way.";
    private const string CantTake = "You can't take it.  You can only carry  one object at a time.";
    private const string CantDrop = "You can't drop it.  Another object is   already here.";
    private const string WrongPlace = "\nOk, but it doesn't belong here.";
    private const string Ok = "\nOk.";

    private const string Inventory0 = "You are carrying nothing.";
    private const string Inventory1 = "Number of objects still missing:  {0}";

    private const string AmigaHelpEdited0 = "Use the mouse to select your choice fromthe bottom of the screen or the menu.\nTo use the keyboard instead of the\nmouse, press the NUMBER or first LETTER.";
    private const string AmigaHelpEdited1 = "From the keyboard you can also press:\n  <C> to see what you're carrying and\n      how you're doing.\n  <CTRL-S> to turn the sound on or off.";

    private const int MaxObjMissing = 10;
    private const int MaxRoom = 62;
    private const int MaxObj = 40;
    private const int MaxRoomNormal = 57;
    private const int MaxRoomTeleport = 30;
    private const int MaxRoomObj = 42;
    private const int MaxBlock = 4;
    private const int MaxStr = 6;
    private const int MaxObjStr = 4;
    private const int MaxObjStrEnd = 2;
    private const int MaxFlag = 40;
    private const int MaxOption = 3;
    private const int MaxDir = 4;
    private const int MaxMovesUntilWind = 150;
    private static readonly TimeSpan TimerInterval = TimeSpan.FromMinutes(10);

    private const int RowMenu = 21;
    private const int RowOption1 = 21;
    private const int RowOption4 = 24;
    private const int ColMenu = 0;
    private const int ColOption = 1;
    private const int ColNsew = 1;
    private const int ColNorth = 1;
    private const int ColSouth = 8;
    private const int ColEast = 15;
    private const int ColWest = 21;
    private const int ColTake = 29;
    private const int ColDrop = 35;

    private const int OfsRoom = 0x5400;
    private const int OfsObj = 0x0800;

    private const int PicWidth = 140;
    private const int PicHeight = 159;
    private const int PicX0 = 10;
    private const int PicY0 = 0;

    // selections
    private const int SelHome = -2;
    private const int SelBack = -1;
    private const int SelOpt1 = 0;
    private const int SelOpt3 = 2;
    private const int SelNorth = 3;
    private const int SelSouth = 4;
    private const int SelEast = 5;
    private const int SelWest = 6;
    private const int SelTake = 7;
    private const int SelDrop = 8;
    private const int SelRealOpt1 = 9;
    private const int SelRealOpt3 = 11;
    private const int SelTimerEvent = 12;
    private const int SelLast = SelRealOpt3;

    private const int RoomNone = -1;
    private const int RoomPicnic = 2;
    private const int RoomHome = 28;
    private const int RoomParty = 58;
    private const int RoomMist = 59;
    private const int RoomTigger = 61;

    // script opcodes
    private const int OpGotoRoom = 0x06;
    private const int OpPrintMsg = 0x08;
    private const int OpPrintStr = 0x0A;
    private const int OpDropObj = 0x0C;
    private const int OpFlagClear = 0x0E;
    private const int OpFlagSet = 0x10;
    private const int OpGameOver = 0x12;
    private const int OpWalkMist = 0x14;
    private const int OpPlaySound = 0x16;
    private const int OpSaveGame = 0x18;
    private const int OpLoadGame = 0x1A;
    private const int OpOwlHelp = 0x1C;
    private const int OpGotoRnd = 0x1E;
    private const int OpOption0 = 0x15;
    private const int OpOption2 = 0x17;

    private const int ObjStrDesc = 0;
    private const int ObjStrTake = 1;
    private const int ObjStrDrop = 2;
    private const int ObjStrHelp = 3;

    private const int ParOk = 0;
    private const int ParGoto = 1;
    private const int ParBack = 2;
    private const int ParReload = 3;

    private readonly PictureMgrMickeyWinnie _picture;
    private readonly SoundGenPcjr _soundGen;
    private readonly bool _pcSpeakerWind;
    private readonly SaveState _state = new();
    private readonly bool _isAmiga;
    private readonly bool _isBigEndian;
    private readonly int _roomOffset;
    private readonly int _objOffset;
    private int _room;
    private int _mist;
    private bool _doWind;
    private bool _tiggerOrMist;
    private bool _timerEnabled;
    private TimeSpan _timerStart;
    private readonly (int Left, int Top, int Right, int Bottom) _hotspotNorth = (20, 0, (PicWidth + 10) * 2, 10);
    private readonly (int Left, int Top, int Right, int Bottom) _hotspotSouth = (20, PicHeight - 10, (PicWidth + 10) * 2, PicHeight);
    private readonly (int Left, int Top, int Right, int Bottom) _hotspotEast = (PicWidth * 2, 0, (PicWidth + 10) * 2, PicHeight);
    private readonly (int Left, int Top, int Right, int Bottom) _hotspotWest = (20, 0, 30, PicHeight);

    public WinnieEngine(IHost host, IGameContainer game, AgiDetectedGame detected, AgiSoundEmulation soundEmulation = AgiSoundEmulation.Pcjr, AgiRenderMode renderMode = AgiRenderMode.Ega)
        : base(host, game, detected, renderMode)
    {
        _picture = new PictureMgrMickeyWinnie(Gfx, detected.Platform, host.Log);
        _isAmiga = detected.Platform == AgiPlatform.Amiga;
        _pcSpeakerWind = soundEmulation == AgiSoundEmulation.PcSpeaker;

        // DOS uses AGIv1 sounds, which only the PCjr sequencer understands
        _soundGen = new SoundGenPcjr(host.Audio.SampleRate, 0x2001);
        AddAudioSource(_soundGen);

        switch (detected.Platform)
        {
            case AgiPlatform.Amiga:
            case AgiPlatform.CoCo:
                _isBigEndian = true;
                _roomOffset = 0;
                _objOffset = 0;
                break;
            default:
                _isBigEndian = false;
                _roomOffset = OfsRoom;
                _objOffset = OfsObj;
                break;
        }
    }

    /// <summary>The room the player is in.</summary>
    public int Room => _room;

    /// <summary>The object Pooh carries, or 0.</summary>
    public int ObjectHeld => _state.ObjHave;

    public int ObjectsMissing => _state.ObjMiss;

    /// <summary>Port of the WTP_SAVE_GAME struct.</summary>
    private sealed class SaveState
    {
        public byte Sound;
        public byte Moves;
        public byte ObjMiss;
        public byte ObjRet;
        public byte ObjHave;
        public readonly byte[] Flags = new byte[MaxFlag];
        public readonly byte[] UsedObj = new byte[MaxObjMissing];
        public readonly byte[] ObjRoom = new byte[MaxRoomObj];
    }

    /// <summary>Port of WTP_ROOM_HDR.</summary>
    private sealed class RoomHeader
    {
        public byte RoomNumber;
        public byte ObjId;
        public ushort OfsPic;
        public ushort FileLen;
        public readonly sbyte[] RoomNew = new sbyte[MaxDir];
        public byte ObjX;
        public byte ObjY;
        public readonly ushort[] OfsDesc = new ushort[MaxBlock];
        public readonly ushort[] OfsBlock = new ushort[MaxBlock];
        public readonly ushort[] OfsStr = new ushort[MaxStr];
        public readonly ushort[,] OfsOpt = new ushort[MaxBlock, MaxBlock];
    }

    /// <summary>Port of WTP_OBJ_HDR.</summary>
    private sealed class ObjHeader
    {
        public ushort FileLen;
        public ushort ObjId;
        public readonly ushort[] OfsEndStr = new ushort[MaxObjStrEnd];
        public readonly ushort[] OfsStr = new ushort[MaxObjStr];
        public ushort OfsPic;
    }

    // ---- data files ----

    private ushort Read16(ReadOnlySpan<byte> buffer, int offset)
        => _isBigEndian ? BinaryPrimitives.ReadUInt16BigEndian(buffer[offset..]) : BinaryPrimitives.ReadUInt16LittleEndian(buffer[offset..]);

    private RoomHeader ParseRoomHeader(ReadOnlySpan<byte> buffer)
    {
        var hdr = new RoomHeader();
        int p = 0;
        hdr.RoomNumber = buffer[p++];
        hdr.ObjId = buffer[p++];
        hdr.OfsPic = Read16(buffer, p);
        p += 2;
        hdr.FileLen = Read16(buffer, p);
        p += 2;
        p += 2; // reserved0
        for (int i = 0; i < MaxDir; i++)
        {
            hdr.RoomNew[i] = (sbyte)buffer[p++];
        }

        hdr.ObjX = buffer[p++];
        hdr.ObjY = buffer[p++];
        p += 2; // reserved1
        for (int i = 0; i < MaxBlock; i++)
        {
            hdr.OfsDesc[i] = Read16(buffer, p);
            p += 2;
        }

        for (int i = 0; i < MaxBlock; i++)
        {
            hdr.OfsBlock[i] = Read16(buffer, p);
            p += 2;
        }

        for (int i = 0; i < MaxStr; i++)
        {
            hdr.OfsStr[i] = Read16(buffer, p);
            p += 2;
        }

        p += 4; // reserved2
        for (int i = 0; i < MaxBlock; i++)
        {
            for (int j = 0; j < MaxBlock; j++)
            {
                hdr.OfsOpt[i, j] = Read16(buffer, p);
                p += 2;
            }
        }

        return hdr;
    }

    private ObjHeader ParseObjHeader(ReadOnlySpan<byte> buffer)
    {
        var hdr = new ObjHeader();
        int p = 0;
        if (_isAmiga)
        {
            // these two fields are little endian on Amiga
            hdr.FileLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer);
            hdr.ObjId = BinaryPrimitives.ReadUInt16LittleEndian(buffer[2..]);
        }
        else
        {
            hdr.FileLen = Read16(buffer, 0);
            hdr.ObjId = Read16(buffer, 2);
        }

        p = 4;
        for (int i = 0; i < MaxObjStrEnd; i++)
        {
            hdr.OfsEndStr[i] = Read16(buffer, p);
            p += 2;
        }

        for (int i = 0; i < MaxObjStr; i++)
        {
            hdr.OfsStr[i] = Read16(buffer, p);
            p += 2;
        }

        hdr.OfsPic = Read16(buffer, p);
        return hdr;
    }

    private string RoomFileName(int room) => string.Format(_isAmiga ? RoomAmiga : RoomDos, room);

    private string ObjFileName(int obj) => string.Format(_isAmiga ? ObjAmiga : ObjDos, obj);

    /// <summary>Reads a room file into a zero-padded 4 KB buffer and parses its header.</summary>
    private byte[] ReadRoom(int room, out RoomHeader header)
    {
        var buffer = new byte[4096];
        var data = TryReadFile(RoomFileName(room));
        if (data is not null)
        {
            data.AsSpan(0, Math.Min(data.Length, buffer.Length)).CopyTo(buffer);
        }

        header = ParseRoomHeader(buffer);
        return buffer;
    }

    /// <summary>Reads an object file into a zero-padded 2 KB buffer; returns the file length.</summary>
    private byte[] ReadObj(int obj, out int length)
    {
        var buffer = new byte[2048];
        var data = TryReadFile(ObjFileName(obj));
        length = 0;
        if (data is not null)
        {
            length = Math.Min(data.Length, buffer.Length);
            data.AsSpan(0, length).CopyTo(buffer);
        }

        return buffer;
    }

    // ---- game state ----

    private void Randomize()
    {
        for (int i = 0; i < MaxObjMissing; i++)
        {
            int obj;
            bool done = false;
            do
            {
                obj = Rnd(MaxObj); // 1-40
                done = true;
                for (int j = 0; j < MaxObjMissing; j++)
                {
                    if (_state.UsedObj[j] == obj)
                    {
                        done = false;
                        break;
                    }
                }
            }
            while (!done);

            _state.UsedObj[i] = (byte)obj;

            int room;
            do
            {
                room = Rnd(MaxRoomNormal); // 1-57
                done = true;
                for (int j = 0; j < MaxRoomObj; j++)
                {
                    if (_state.ObjRoom[j] == room)
                    {
                        done = false;
                        break;
                    }
                }
            }
            while (!done);

            _state.ObjRoom[obj] = (byte)room;
        }
    }

    private int GetObjInRoom(int room)
    {
        for (int obj = 1; obj < MaxRoomObj; obj++)
        {
            if (_state.ObjRoom[obj] == room)
            {
                return obj;
            }
        }

        return 0;
    }

    private void SetTakeDrop(int[] canSel)
    {
        canSel[SelTake] = GetObjInRoom(_room);
        canSel[SelDrop] = _state.ObjHave;
    }

    // ---- intro and main loop ----

    protected override async Task RunAsync()
    {
        Init();
        Randomize();
        await IntroAsync();
        await GameLoopAsync();
    }

    private void Init()
    {
        SoundOn = true;
        _state.Sound = 1;
        _state.ObjMiss = MaxObjMissing;
        _state.ObjRet = 0;
        _state.Flags[0] = 1;
        _state.Flags[1] = 1;
        _room = RoomHome;

        _mist = -1;
        _doWind = false;
        _tiggerOrMist = false; // tigger appears first
        StopTimer(); // timer starts after intro
    }

    private async Task IntroAsync()
    {
        DrawPic(FileLogo);
        PrintStr(Intro0);
        await WaitAsync(1600);

        DrawPic(FileTitle);
        PrintStr(Intro1);
        await WaitAsync(1600);

        if (!await PlaySoundAsync(WinnieSound.Pooh0))
        {
            return;
        }

        if (!await PlaySoundAsync(WinnieSound.Pooh1))
        {
            return;
        }

        await PlaySoundAsync(WinnieSound.Pooh2);
    }

    private async Task GameLoopAsync()
    {
        RoomHeader hdr = new();
        byte[] roomData = [];
        int decodePhase = 0;

        StartTimer();

        while (!ShouldQuit)
        {
            switch (decodePhase)
            {
                case 0:
                    if (_state.ObjMiss == 0 && _room == RoomPicnic)
                    {
                        _room = RoomParty;
                        StopTimer();
                    }

                    roomData = ReadRoom(_room, out hdr);
                    DrawRoomPic();
                    decodePhase = 1;
                    break;
                case 1:
                    if (GetObjInRoom(_room) != 0)
                    {
                        PrintObjStr(GetObjInRoom(_room), ObjStrDesc);
                        await GetSelectionAsync(SelectionType.AnyKey);
                    }

                    decodePhase = 2;
                    break;
                case 2:
                    for (int block = 0; block < MaxBlock; block++)
                    {
                        if (await ParserAsync(hdr.OfsDesc[block] - _roomOffset, block, roomData) == ParBack)
                        {
                            decodePhase = 1;
                            break;
                        }
                    }

                    if (decodePhase == 2)
                    {
                        decodePhase = 3;
                    }

                    break;
                case 3:
                    for (int block = 0; block < MaxBlock; block++)
                    {
                        int result = await ParserAsync(hdr.OfsBlock[block] - _roomOffset, block, roomData);
                        if (result == ParGoto)
                        {
                            decodePhase = 0;
                            break;
                        }

                        if (result == ParBack)
                        {
                            decodePhase = 2;
                            break;
                        }

                        if (result == ParReload)
                        {
                            // start over at block zero
                            break;
                        }
                    }

                    break;
            }
        }
    }

    // ---- the room script parser ----

    private async Task<int> ParserAsync(int startPc, int index, byte[] buffer)
    {
        var hdr = ParseRoomHeader(buffer);
        int newRoom = 0;
        var canSel = new int[SelLast + 1];
        string menu = string.Empty;

        while (!ShouldQuit)
        {
            int pc = startPc;

            // check if block is to be run
            int block = At(buffer, pc++);
            if (block == 0)
            {
                return ParOk;
            }

            int blockFlag = At(buffer, pc++);
            if (_state.Flags[block] != blockFlag)
            {
                return ParOk;
            }

            // extract text from block
            int opcode = At(buffer, pc);
            switch (opcode)
            {
                case 0:
                case OpOption0:
                case OpOption0 + 1:
                case OpOption2:
                    Array.Clear(canSel);

                    // check if NSEW directions should be displayed
                    if (hdr.RoomNew[0] != 0)
                    {
                        canSel[SelNorth] = canSel[SelSouth] = canSel[SelEast] = canSel[SelWest] = 1;
                    }

                    // check if object in room or player carrying one
                    SetTakeDrop(canSel);

                    // check which rows have a menu option
                    for (int sel = 0; sel < MaxOption; sel++)
                    {
                        opcode = At(buffer, pc++);
                        if (opcode != 0)
                        {
                            canSel[opcode - OpOption0] = 1;
                            canSel[sel + SelRealOpt1] = opcode - 0x14;
                        }
                    }

                    // extract menu string
                    menu = Xor80(ReadCString(buffer, pc, 120));
                    break;
                default:
                    // print description
                    PrintStrWinnie(ReadCString(buffer, pc));
                    return await GetSelectionAsync(SelectionType.Backspace) == 1 ? ParOk : ParBack;
            }

            // input handler
            int iSel = 0;
            bool done = false;
            while (!done && !ShouldQuit)
            {
                // run wind if it's time
                if (_doWind)
                {
                    await WindAsync();
                }

                // get menu selection
                iSel = await GetMenuSelAsync(menu, canSel);

                if (iSel == SelTimerEvent)
                {
                    StopTimer();
                    if (!_tiggerOrMist)
                    {
                        await TiggerAsync();
                    }
                    else
                    {
                        await MistAsync();
                    }

                    _tiggerOrMist = !_tiggerOrMist;
                    return ParGoto;
                }

                if (++_state.Moves == MaxMovesUntilWind)
                {
                    _doWind = true;
                }

                // process selection
                switch (iSel)
                {
                    case SelHome:
                        switch (_room)
                        {
                            case RoomHome:
                            case RoomMist:
                            case RoomTigger:
                                break;
                            default:
                                _room = RoomHome;
                                return ParGoto;
                        }

                        break;
                    case SelBack:
                        return ParBack;
                    case >= SelOpt1 and <= SelOpt3:
                        done = true;
                        break;
                    case SelNorth:
                    case SelSouth:
                    case SelEast:
                    case SelWest:
                    {
                        int dir = iSel - SelNorth;
                        if (hdr.RoomNew[dir] == RoomNone)
                        {
                            PrintStr(CantGo);
                            await GetSelectionAsync(SelectionType.AnyKey);
                        }
                        else
                        {
                            _room = hdr.RoomNew[dir];
                            return ParGoto;
                        }

                        break;
                    }

                    case SelTake:
                        await TakeObjAsync(_room);
                        SetTakeDrop(canSel);
                        break;
                    case SelDrop:
                    {
                        bool droppedInRightRoom = await DropObjAsync(_room);
                        SetTakeDrop(canSel);
                        if (droppedInRightRoom)
                        {
                            // reload room so that the dropped object's flag takes effect
                            return ParReload;
                        }

                        break;
                    }
                }
            }

            if (ShouldQuit)
            {
                return ParOk;
            }

            // jump to the script block of the selected option
            pc = hdr.OfsOpt[index, iSel] - _roomOffset;

            opcode = At(buffer, pc);
            if (opcode == 0)
            {
                pc++;
            }

            // process script
            do
            {
                opcode = At(buffer, pc++);
                switch (opcode)
                {
                    case OpGotoRoom:
                        opcode = At(buffer, pc++);
                        newRoom = opcode;
                        break;
                    case OpPrintMsg:
                        opcode = At(buffer, pc++);
                        PrintRoomStr(_room, opcode);
                        await GetSelectionAsync(SelectionType.AnyKey);
                        break;
                    case OpPrintStr:
                        opcode = At(buffer, pc++);
                        PrintRoomStr(_room, opcode);
                        break;
                    case OpDropObj:
                        pc++;
                        opcode = -1;
                        DropObjRnd();
                        break;
                    case OpFlagClear:
                        opcode = At(buffer, pc++);
                        _state.Flags[opcode] = 0;
                        break;
                    case OpFlagSet:
                        opcode = At(buffer, pc++);
                        _state.Flags[opcode] = 1;
                        break;
                    case OpGameOver:
                        await GameOverAsync();
                        break;
                    case OpWalkMist:
                        _mist--;
                        if (_mist == 0)
                        {
                            StartTimer();
                            _room = Rnd(MaxRoomTeleport); // 1-30
                            return ParGoto;
                        }

                        break;
                    case OpPlaySound:
                        opcode = At(buffer, pc++);
                        await PlaySoundAsync((WinnieSound)opcode);
                        break;
                    case OpSaveGame:
                        SaveGame();
                        await GetSelectionAsync(SelectionType.AnyKey);
                        _room = RoomHome;
                        return ParGoto;
                    case OpLoadGame:
                        LoadGame();
                        _room = RoomHome;
                        return ParGoto;
                    case OpOwlHelp:
                        pc++;
                        await ShowOwlHelpAsync();
                        break;
                    case OpGotoRnd:
                        // Amiga changed opcode 1E to display its very long platform-specific
                        // help messages in the first room
                        if (_isAmiga)
                        {
                            await ShowAmigaHelpAsync();
                            break;
                        }

                        StartTimer();
                        _room = Rnd(MaxRoomTeleport); // 1-30
                        return ParGoto;
                    default:
                        opcode = 0;
                        break;
                }
            }
            while (opcode != 0 && !ShouldQuit);

            if (newRoom != 0)
            {
                _room = newRoom;
                return ParGoto;
            }

            if (_room == RoomTigger && _isAmiga)
            {
                // Amiga removed Tigger's opcode that goes to a random room
                StartTimer();
                _room = Rnd(MaxRoomTeleport); // 1-30
                return ParGoto;
            }

            if (block == 1)
            {
                return ParOk;
            }
        }

        return ParOk;
    }

    private static byte At(byte[] buffer, int offset) => offset >= 0 && offset < buffer.Length ? buffer[offset] : (byte)0;

    // ---- actions ----

    private async Task KeyHelpAsync()
    {
        await PlaySoundAsync(WinnieSound.KeyHelp);
        PrintStr(Help0);
        await GetSelectionAsync(SelectionType.AnyKey);
        PrintStr(Help1);
        await GetSelectionAsync(SelectionType.AnyKey);
    }

    private async Task InventoryAsync()
    {
        if (_state.ObjHave != 0)
        {
            PrintObjStr(_state.ObjHave, ObjStrTake);
        }
        else
        {
            ClearTextArea();
            DrawStr(RowMenu, ColMenu, DefaultAttribute, Inventory0);
        }

        DrawStr(RowOption4, ColMenu, DefaultAttribute, string.Format(Inventory1, _state.ObjMiss));
        await GetSelectionAsync(SelectionType.AnyKey);
    }

    private void PrintObjStr(int obj, int str)
    {
        var buffer = ReadObj(obj, out _);
        var hdr = ParseObjHeader(buffer);
        PrintStrWinnie(ReadCString(buffer, hdr.OfsStr[str] - _objOffset));
    }

    private bool IsRightObj(int room, int obj, out int code)
    {
        ReadRoom(room, out var roomHdr);
        var objData = ReadObj(obj, out _);
        var objHdr = ParseObjHeader(objData);

        // must return the object id before the workarounds are applied below. Dropping the board
        // (objId 34) must set flag 34 to change the room above the bridge, but the pine cones and
        // sticks (objId 11) must not.
        code = objHdr.ObjId;

        // The pine cones and sticks have an id that does not exist in any room. The game worked
        // around this by using the correct id for the bridge.
        int objId = objHdr.ObjId;
        if (objId == 11)
        {
            objId = 34; // bridge
        }

        // Eeyore's popped balloon is assigned to Piglet in the data file. The game's executable
        // must contain a hard-coded workaround, because the balloon is correctly assigned to
        // Eeyore at runtime.
        if (obj == 25 && objId == 8)
        {
            objId = 7; // Eeyore
        }

        return roomHdr.ObjId == objId;
    }

    private async Task TakeObjAsync(int room)
    {
        if (_state.ObjHave != 0)
        {
            // player is already carrying an object, can't take
            PrintStr(CantTake);
            await GetSelectionAsync(SelectionType.AnyKey);
            return;
        }

        int obj = GetObjInRoom(room);
        _state.ObjHave = (byte)obj;
        _state.ObjRoom[obj] = 0;

        PrintStr(Ok);
        await PlaySoundAsync(WinnieSound.Take);

        DrawRoomPic();

        // print object "take" string
        PrintObjStr(_state.ObjHave, ObjStrTake);
        await GetSelectionAsync(SelectionType.AnyKey);

        // set the has-lantern flag when taking the lantern
        if (obj == 18)
        {
            _state.Flags[0x0D] = 1;
        }
    }

    /// <summary>Returns true if the object was dropped in the right room.</summary>
    private async Task<bool> DropObjAsync(int room)
    {
        if (GetObjInRoom(room) != 0)
        {
            // there already is an object in the room, can't drop
            PrintStr(CantDrop);
            await GetSelectionAsync(SelectionType.AnyKey);
            return false;
        }

        // clear the has-lantern flag when dropping the lantern
        if (_state.ObjHave == 18)
        {
            _state.Flags[0x0D] = 0;
        }

        if (IsRightObj(room, _state.ObjHave, out int code))
        {
            // object has been dropped in the right place
            await PlaySoundAsync(WinnieSound.DropOk);
            PrintObjStr(_state.ObjHave, ObjStrDrop);
            await GetSelectionAsync(SelectionType.AnyKey);

            // increase amount of objects returned, decrease amount of objects missing
            _state.ObjMiss--;
            _state.ObjRet++;

            // xor the dropped object with 0x80 to signify it has been dropped in the right place
            for (int i = 0; i < MaxObjMissing; i++)
            {
                if (_state.UsedObj[i] == _state.ObjHave)
                {
                    _state.UsedObj[i] ^= XorKey;
                    break;
                }
            }

            // set flag according to dropped object's id
            _state.Flags[code] = 1;

            // player is carrying nothing
            _state.ObjHave = 0;

            if (_state.ObjMiss == 0)
            {
                // all objects returned, tell player to find party
                await PlaySoundAsync(WinnieSound.Fanfare);
                PrintStr(GameOver0);
                await GetSelectionAsync(SelectionType.AnyKey);
                PrintStr(GameOver1);
                await GetSelectionAsync(SelectionType.AnyKey);
            }

            return true;
        }

        // drop object in the given room
        _state.ObjRoom[_state.ObjHave] = (byte)room;

        // object has been dropped in the wrong place
        PrintStr(WrongPlace);
        await PlaySoundAsync(WinnieSound.Drop);

        // draw the object by redrawing the room and reprinting the message. The original just
        // drew the object.
        DrawRoomPic();
        PrintStr(WrongPlace);
        await GetSelectionAsync(SelectionType.AnyKey);

        // print object description
        PrintObjStr(_state.ObjHave, ObjStrDesc);
        await GetSelectionAsync(SelectionType.AnyKey);

        _state.ObjHave = 0;
        return false;
    }

    private void DropObjRnd()
    {
        if (_state.ObjHave == 0)
        {
            return;
        }

        int room;
        bool done;
        do
        {
            room = Rnd(MaxRoomNormal); // 1-57
            done = room != _room;
            for (int j = 0; j < MaxRoomObj; j++)
            {
                if (_state.ObjRoom[j] == room)
                {
                    done = false;
                }
            }
        }
        while (!done);

        _state.ObjRoom[_state.ObjHave] = (byte)room;
        _state.ObjHave = 0;
    }

    private async Task WindAsync()
    {
        _doWind = false;
        _state.Moves = 0;
        if (_state.ObjMiss == 0)
        {
            return;
        }

        PrintStr(Wind0);
        await PlaySoundAsync(WinnieSound.Wind0);
        await GetSelectionAsync(SelectionType.AnyKey);

        PrintStr(Wind1);
        await PlaySoundAsync(WinnieSound.Wind0);
        await GetSelectionAsync(SelectionType.AnyKey);

        // randomize positions of objects at large
        for (int i = 0; i < MaxObjMissing; i++)
        {
            if ((_state.UsedObj[i] & XorKey) != 0)
            {
                continue;
            }

            if (_state.UsedObj[i] == _state.ObjHave)
            {
                continue; // skip inventory object
            }

            int room;
            bool done;
            do
            {
                room = Rnd(MaxRoomNormal); // 1-57
                done = true;
                for (int j = 0; j < MaxRoomObj; j++)
                {
                    if (_state.ObjRoom[j] == room)
                    {
                        done = false;
                    }
                }
            }
            while (!done);

            _state.ObjRoom[_state.UsedObj[i]] = (byte)room;
        }
    }

    private async Task MistAsync()
    {
        _mist = 1 + Rnd(4); // 2-5 walks through the mist

        PrintStr(MistText);
        await GetSelectionAsync(SelectionType.AnyKey);

        _room = RoomMist;
        DrawRoomPic();
    }

    private async Task TiggerAsync()
    {
        PrintStr(TiggerText);
        await GetSelectionAsync(SelectionType.AnyKey);

        _room = RoomTigger;
        DrawRoomPic();

        DropObjRnd();
    }

    private void StartTimer()
    {
        _timerEnabled = true;
        _timerStart = EngineTime;
    }

    private void StopTimer() => _timerEnabled = false;

    private async Task ShowOwlHelpAsync()
    {
        if (_state.ObjHave != 0)
        {
            PrintStr(Owl0);
            await GetSelectionAsync(SelectionType.AnyKey);
            PrintObjStr(_state.ObjHave, ObjStrHelp);
            await GetSelectionAsync(SelectionType.AnyKey);
        }

        if (GetObjInRoom(_room) != 0)
        {
            PrintStr(Owl0);
            await GetSelectionAsync(SelectionType.AnyKey);
            PrintObjStr(GetObjInRoom(_room), ObjStrHelp);
            await GetSelectionAsync(SelectionType.AnyKey);
        }
    }

    private async Task ShowAmigaHelpAsync()
    {
        // print edited versions of Amiga help text that fit in four lines
        PrintStr(AmigaHelpEdited0);
        await GetSelectionAsync(SelectionType.AnyKey);
        PrintStr(AmigaHelpEdited1);
        await GetSelectionAsync(SelectionType.AnyKey);
    }

    private async Task GameOverAsync()
    {
        // sing the Pooh song forever
        while (!ShouldQuit)
        {
            PrintStr(Song0);
            await PlaySoundAsync(WinnieSound.Pooh0);
            PrintStr(Song1);
            await PlaySoundAsync(WinnieSound.Pooh1);
            PrintStr(Song2);
            await PlaySoundAsync(WinnieSound.Pooh2);
            await GetSelectionAsync(SelectionType.AnyKey);
        }
    }

    // ---- the menu ----

    private void DrawMenu(string menu, int iSel, int[] canSel)
    {
        ClearTextArea();
        DrawStr(RowMenu, ColMenu, DefaultAttribute, menu);

        if (canSel[SelNorth] != 0)
        {
            DrawStr(RowOption4, ColNsew, DefaultAttribute, Nsew);
        }

        if (canSel[SelTake] != 0)
        {
            DrawStr(RowOption4, ColTake, DefaultAttribute, Take);
        }

        if (canSel[SelDrop] != 0)
        {
            DrawStr(RowOption4, ColDrop, DefaultAttribute, Drop);
        }

        int row = 0;
        int col = 0;
        switch (iSel)
        {
            case >= SelOpt1 and <= SelOpt3:
                row = RowOption1 + iSel;
                col = ColOption;
                break;
            case SelNorth:
                row = RowOption4;
                col = ColNorth;
                break;
            case SelSouth:
                row = RowOption4;
                col = ColSouth;
                break;
            case SelEast:
                row = RowOption4;
                col = ColEast;
                break;
            case SelWest:
                row = RowOption4;
                col = ColWest;
                break;
            case SelTake:
                row = RowOption4;
                col = ColTake;
                break;
            case SelDrop:
                row = RowOption4;
                col = ColDrop;
                break;
        }

        DrawStr(row, col - 1, DefaultAttribute, ">");
    }

    private static void IncMenuSel(ref int iSel, int[] canSel)
    {
        do
        {
            iSel++;
            if (iSel > SelDrop)
            {
                iSel = SelOpt1;
            }
        }
        while (canSel[iSel] == 0);
    }

    private static void DecMenuSel(ref int iSel, int[] canSel)
    {
        do
        {
            iSel--;
            if (iSel < SelOpt1)
            {
                iSel = SelDrop;
            }
        }
        while (canSel[iSel] == 0);
    }

    private static void ClrMenuSel(ref int iSel, int[] canSel)
    {
        iSel = SelOpt1;
        while (iSel < canSel.Length && canSel[iSel] == 0)
        {
            iSel++;
        }
    }

    private static void GetMenuMouseSel(ref int iSel, int[] canSel, int x, int y)
    {
        switch (y)
        {
            case RowOption1:
            case RowOption1 + 1:
            case RowOption1 + 2:
                if (canSel[y - RowOption1] != 0)
                {
                    iSel = y - RowOption1;
                }

                break;
            case RowOption4:
                if (canSel[SelNorth] != 0 && x > ColNorth - 1 && x < 6)
                {
                    iSel = SelNorth;
                }

                if (canSel[SelSouth] != 0 && x > ColSouth - 1 && x < 13)
                {
                    iSel = SelSouth;
                }

                if (canSel[SelEast] != 0 && x > ColEast - 1 && x < 19)
                {
                    iSel = SelEast;
                }

                if (canSel[SelWest] != 0 && x > ColWest - 1 && x < 25)
                {
                    iSel = SelWest;
                }

                if (canSel[SelTake] != 0 && x > ColTake - 1 && x < 33)
                {
                    iSel = SelTake;
                }

                if (canSel[SelDrop] != 0 && x > ColDrop - 1 && x < 39)
                {
                    iSel = SelDrop;
                }

                break;
        }
    }

    private static bool Contains(in (int Left, int Top, int Right, int Bottom) rect, int x, int y)
        => x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;

    /// <summary>The selection is valid, or the help is shown and the selection reset.</summary>
    private async Task<bool> MakeSelAsync(int sel, int[] canSel, Action<int> setSel)
    {
        if (canSel[sel] != 0)
        {
            return true;
        }

        await KeyHelpAsync();
        int reset = 0;
        ClrMenuSel(ref reset, canSel);
        setSel(reset);
        return false;
    }

    /// <summary>Lets the player pick a menu entry; returns the selection, or the timer event.</summary>
    private async Task<int> GetMenuSelAsync(string menu, int[] canSel)
    {
        AwaitingMenuSelection = true;
        try
        {
            return await GetMenuSelCoreAsync(menu, canSel);
        }
        finally
        {
            AwaitingMenuSelection = false;
        }
    }

    private async Task<int> GetMenuSelCoreAsync(string menu, int[] canSel)
    {
        int iSel = 0;
        ClrMenuSel(ref iSel, canSel);
        DrawMenu(menu, iSel, canSel);

        while (!ShouldQuit)
        {
            while (TryDequeueEvent(out var ev))
            {
                switch (ev.Type)
                {
                    case InputEventType.Quit:
                        return iSel;
                    case InputEventType.MouseMove:
                        GetMenuMouseSel(ref iSel, canSel, ev.X / 8, ev.Y / 8);
                        break;
                    case InputEventType.MouseUp when ev.Button == MouseButton.Left:
                    {
                        // click to move
                        int hotspot = -1;
                        if (canSel[SelNorth] != 0 && Contains(_hotspotNorth, ev.X, ev.Y))
                        {
                            hotspot = SelNorth;
                        }
                        else if (canSel[SelSouth] != 0 && Contains(_hotspotSouth, ev.X, ev.Y))
                        {
                            hotspot = SelSouth;
                        }
                        else if (canSel[SelWest] != 0 && Contains(_hotspotWest, ev.X, ev.Y))
                        {
                            hotspot = SelWest;
                        }
                        else if (canSel[SelEast] != 0 && Contains(_hotspotEast, ev.X, ev.Y))
                        {
                            hotspot = SelEast;
                        }

                        if (hotspot >= 0)
                        {
                            iSel = hotspot;
                            await MakeSelAsync(iSel, canSel, s => iSel = s);
                            return iSel;
                        }

                        if (TryConfirm(ref iSel, canSel))
                        {
                            return iSel;
                        }

                        break;
                    }

                    case InputEventType.MouseUp when ev.Button == MouseButton.Right:
                        return SelBack;
                    case InputEventType.KeyDown:
                        switch (ev.Key)
                        {
                            case Key.Escape:
                                return SelHome;
                            case Key.Backspace:
                                return SelBack;
                            case Key.C:
                                await InventoryAsync();
                                break;
                            case Key.Space:
                            case Key.Right:
                            case Key.Down:
                                IncMenuSel(ref iSel, canSel);
                                break;
                            case Key.Left:
                            case Key.Up:
                                DecMenuSel(ref iSel, canSel);
                                break;
                            case Key.D1:
                            case Key.D2:
                            case Key.D3:
                                iSel = ev.Key - Key.D1;
                                if (canSel[iSel + SelRealOpt1] != 0)
                                {
                                    return iSel;
                                }

                                await KeyHelpAsync();
                                ClrMenuSel(ref iSel, canSel);
                                break;
                            case Key.N:
                                iSel = SelNorth;
                                if (await MakeSelAsync(iSel, canSel, s => iSel = s))
                                {
                                    return iSel;
                                }

                                break;
                            case Key.S:
                                if ((ev.Modifiers & KeyModifiers.Ctrl) != 0)
                                {
                                    SoundOn = !SoundOn;
                                    break;
                                }

                                iSel = SelSouth;
                                if (await MakeSelAsync(iSel, canSel, s => iSel = s))
                                {
                                    return iSel;
                                }

                                break;
                            case Key.E:
                                iSel = SelEast;
                                if (await MakeSelAsync(iSel, canSel, s => iSel = s))
                                {
                                    return iSel;
                                }

                                break;
                            case Key.W:
                                iSel = SelWest;
                                if (await MakeSelAsync(iSel, canSel, s => iSel = s))
                                {
                                    return iSel;
                                }

                                break;
                            case Key.T:
                                iSel = SelTake;
                                if (await MakeSelAsync(iSel, canSel, s => iSel = s))
                                {
                                    return iSel;
                                }

                                break;
                            case Key.D:
                                iSel = SelDrop;
                                if (await MakeSelAsync(iSel, canSel, s => iSel = s))
                                {
                                    return iSel;
                                }

                                break;
                            case Key.Return:
                            case Key.KeypadEnter:
                                if (TryConfirm(ref iSel, canSel))
                                {
                                    return iSel;
                                }

                                break;
                            default:
                                // show help if the control/alt/shift keys are not pressed
                                if (!HasNonStickyModifier(ev))
                                {
                                    await KeyHelpAsync();
                                    ClrMenuSel(ref iSel, canSel);
                                }

                                break;
                        }

                        break;
                }

                DrawMenu(menu, iSel, canSel);
            }

            await Poll();

            if (_timerEnabled && EngineTime - _timerStart >= TimerInterval && _room <= MaxRoomTeleport)
            {
                return SelTimerEvent;
            }
        }

        return iSel;
    }

    /// <summary>Return or a click on the highlighted entry: options map to their real option index.</summary>
    private static bool TryConfirm(ref int iSel, int[] canSel)
    {
        if (iSel is >= SelOpt1 and <= SelOpt3)
        {
            for (int sel2 = 0; sel2 < MaxOption; sel2++)
            {
                if (iSel == canSel[sel2 + SelRealOpt1] - 1)
                {
                    iSel = sel2;
                    return true;
                }
            }

            return false;
        }

        return canSel[iSel] != 0;
    }

    // ---- pictures ----

    private void DrawPic(string name)
    {
        string fileName = _isAmiga ? "misc/" + name : name + ".pic";
        var buffer = TryReadFile(fileName);
        if (buffer is null)
        {
            return;
        }

        _picture.SetOffset(PicX0, PicY0);
        _picture.DecodePictureFromBuffer(buffer, buffer.Length, true, PicWidth, PicHeight);
        _picture.ShowPicture(PicX0, PicY0, PicWidth, PicHeight);
    }

    private void DrawObjPic(int obj, int x0, int y0)
    {
        if (obj == 0)
        {
            return;
        }

        var buffer = ReadObj(obj, out int objSize);
        var hdr = ParseObjHeader(buffer);
        int start = hdr.OfsPic - _objOffset;
        if (start < 0 || start >= buffer.Length)
        {
            return;
        }

        _picture.SetOffset(x0, y0);
        _picture.DecodePictureFromBuffer(buffer[start..], objSize, false, PicWidth, PicHeight);
        _picture.ShowPicture(PicX0, PicY0, PicWidth, PicHeight);
    }

    private void DrawRoomPic()
    {
        int obj = GetObjInRoom(_room);

        // clear gfx screen
        Gfx.ClearDisplay(0);

        // read room picture
        var buffer = ReadRoom(_room, out var roomHdr);
        int start = roomHdr.OfsPic - _roomOffset;
        if (start < 0 || start >= buffer.Length)
        {
            return;
        }

        // draw room picture
        _picture.SetOffset(PicX0, PicY0);
        _picture.DecodePictureFromBuffer(buffer[start..], 4096, true, PicWidth, PicHeight);
        _picture.ShowPicture(PicX0, PicY0, PicWidth, PicHeight);

        // draw object picture
        DrawObjPic(obj, PicX0 + roomHdr.ObjX, PicY0 + roomHdr.ObjY);
    }

    // ---- sound ----

    private enum WinnieSound
    {
        Pooh0 = 1,
        Tigger,
        Take,
        Drop,
        DropOk,
        Fanfare,
        Pooh1,
        KeyHelp,
        Pooh2,
        Wind0,
        Wind1,
    }

    /// <summary>Plays a DOS sound to the end; returns false when a key or click skipped it. Only DOS sound is supported.</summary>
    private async Task<bool> PlaySoundAsync(WinnieSound sound)
    {
        if (_isAmiga)
        {
            Host.Log.Warning("agi.preagi", $"STUB: playSound({(int)sound})");
            return false;
        }

        // DOS version tests a platform global to choose the wind sound. Sound 10 is designed for
        // PCjr, sound 11 for PC speaker.
        if (sound == WinnieSound.Wind0 && _pcSpeakerWind)
        {
            sound = WinnieSound.Wind1;
        }

        var data = TryReadFile(string.Format(SndDos, (int)sound));
        if (data is null)
        {
            return false;
        }

        var agiSound = AgiSound.CreateFromRawResource(data, 0, AgiPlatform.Dos, isAgiV1: true, Host.Log);
        if (agiSound is null)
        {
            return false;
        }

        _soundGen.SoundEnabled = SoundOn;
        _soundGen.Play(agiSound);

        // loop until the sound is done; a key or mouse button skips it
        bool skipped = false;
        while (!ShouldQuit && !skipped && !_soundGen.Finished)
        {
            while (TryDequeueEvent(out var ev))
            {
                switch (ev.Type)
                {
                    case InputEventType.KeyDown:
                        // don't interrupt if a modifier is pressed
                        if (HasNonStickyModifier(ev))
                        {
                            continue;
                        }

                        goto case InputEventType.MouseUp;
                    case InputEventType.MouseUp:
                        _soundGen.Stop();
                        skipped = true;
                        break;
                }
            }

            await Poll();
        }

        return !ShouldQuit && !skipped;
    }

    // ---- strings ----

    private void PrintRoomStr(int room, int str)
    {
        var buffer = ReadRoom(room, out var hdr);
        PrintStrWinnie(ReadCString(buffer, hdr.OfsStr[str - 1] - _roomOffset));
    }

    private void PrintStrWinnie(string message)
    {
        if (_isAmiga)
        {
            PrintStr(message);
        }
        else
        {
            PrintStrXor(message);
        }
    }

    // ---- saved games ----

    private void SaveGame()
    {
        try
        {
            using var stream = Host.Saves.OpenWrite(SaveFileName);
            using var writer = new BinaryWriter(stream);
            writer.Write((byte)'W');
            writer.Write((byte)'I');
            writer.Write((byte)'N');
            writer.Write((byte)'N');
            writer.Write((byte)SaveGameVersion);

            writer.Write(_state.Sound);
            writer.Write(_state.Moves);
            writer.Write(_state.ObjMiss);
            writer.Write(_state.ObjRet);
            writer.Write(_state.ObjHave);
            writer.Write(_state.Flags);
            writer.Write(_state.UsedObj);
            writer.Write(_state.ObjRoom);
        }
        catch (IOException ex)
        {
            Host.Log.Warning("agi.preagi", $"Can't write file '{SaveFileName}': {ex.Message}");
        }
    }

    private void LoadGame()
    {
        byte[] data;
        try
        {
            using var stream = Host.Saves.OpenRead(SaveFileName);
            if (stream is null)
            {
                return;
            }

            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            data = memory.ToArray();
        }
        catch (IOException ex)
        {
            Host.Log.Warning("agi.preagi", $"Can't read file '{SaveFileName}': {ex.Message}");
            return;
        }

        if (data.Length < 5)
        {
            return;
        }

        int p;
        if (data[0] == 'W' && data[1] == 'I' && data[2] == 'N' && data[3] == 'N')
        {
            int saveVersion = data[4];
            if (saveVersion != SaveGameVersion)
            {
                Host.Log.Warning("agi.preagi", $"Old save game version ({saveVersion}, current version is {SaveGameVersion}). Will try and read anyway, but don't be surprised if bad things happen");
            }

            p = 5;
            _state.Sound = data[p++];
            _state.Moves = data[p++];
            _state.ObjMiss = data[p++];
            _state.ObjRet = data[p++];
            _state.ObjHave = data[p++];
        }
        else
        {
            // This is probably a save from the original interpreter: it stores its variables as
            // 16-bit little endian integers of which only the low byte is used. (ScummVM takes
            // the high byte here and so reads zeros from such saves.)
            Host.Log.Warning("agi.preagi", "No header found in save game, assuming it came from the original interpreter");
            p = 2; // skip unused field
            _state.Sound = data[p];
            p += 2;
            _state.Moves = data[p];
            p += 2;
            _state.ObjMiss = data[p];
            p += 2;
            _state.ObjRet = data[p];
            p += 2;
            p += 6; // three unused fields
            _state.ObjHave = data[p];
            p += 2;
            p += 6; // three unused fields
        }

        if (data.Length < p + MaxFlag + MaxObjMissing + MaxRoomObj)
        {
            Host.Log.Warning("agi.preagi", "Save game is truncated");
            return;
        }

        data.AsSpan(p, MaxFlag).CopyTo(_state.Flags);
        p += MaxFlag;
        data.AsSpan(p, MaxObjMissing).CopyTo(_state.UsedObj);
        p += MaxObjMissing;
        data.AsSpan(p, MaxRoomObj).CopyTo(_state.ObjRoom);

        // Saved games from the original interpreter have 2 more 16-bit fields here which are ignored
    }
}
