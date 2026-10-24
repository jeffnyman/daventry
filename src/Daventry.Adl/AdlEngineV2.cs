using Daventry.Adl.Detection;
using Daventry.Core.Formats;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;

namespace Daventry.Adl;

/// <summary>
/// ADL v2, the engine of Mission Asteroid, Wizard and the Princess, Cranston Manor and later games:
/// data read straight from disk sectors, a four-line text window that waits for Return when it
/// overflows, the game clock, and more opcodes. Port of ScummVM's AdlEngine_v2.
/// </summary>
public abstract class AdlEngineV2 : AdlEngine
{
    protected AdlEngineV2(IHost host, IGameContainer game, AdlDetectedGame detected, DisplayOptions options)
        : base(host, game, detected, options)
    {
    }

    protected V2Strings StringsV2 { get; } = new();

    protected int MaxLines { get; set; } = 4;

    protected DiskImage? Disk { get; set; }

    protected int CurrentVolume { get; set; }

    protected List<DataBlock?> ItemPics { get; } = [];

    protected bool ItemRemoved { get; set; }

    protected byte RoomOnScreen { get; set; }

    protected byte PicOnScreen { get; set; }

    protected int ItemsOnScreen { get; set; }

    protected List<byte> BrokenRooms { get; } = [];

    /// <summary>The container file holding a disk volume of this release.</summary>
    protected string GetDiskImageName(int volume)
        => Description.Volumes.TryGetValue(volume, out var name) ? name : throw new InvalidOperationException($"Disk volume {volume} not found");

    /// <summary>Opens the disk image of a volume.</summary>
    protected DiskImage OpenDisk(int volume)
    {
        string name = GetDiskImageName(volume);
        return DiskImage.Open(Game, name, Host.Log) ?? throw new InvalidDataException($"Failed to open disk image '{name}'");
    }

    protected void MapExeStrings(List<string> strings)
    {
        if (strings.Count < 11)
        {
            throw new InvalidDataException("Not enough strings found in executable");
        }

        // parser messages
        Strings.VerbError = strings[2];
        Strings.NounError = strings[3];
        Strings.EnterCommand = strings[4];

        // line feeds
        Strings.LineFeeds = strings[0];

        // opcode strings
        StringsV2.SaveInsert = strings[5];
        StringsV2.SaveReplace = strings[6];
        StringsV2.RestoreInsert = strings[7];
        StringsV2.RestoreReplace = strings[8];
        Strings.PlayAgain = strings[9];
        Strings.PressReturn = strings[10];
    }

    protected void InsertDisk(int volume)
    {
        Disk = OpenDisk(volume);
        CurrentVolume = volume;
    }

    protected override void SetupOpcodeTables()
    {
        base.SetupOpcodeTables();

        CondOpcodes[0x01] = e => OIsFirstTime(e);
        CondOpcodes[0x02] = e => OIsRandomGT(e);
        CondOpcodes[0x04] = e => OIsNounNotInRoom(e);
        CondOpcodes[0x07] = e => OIsCarryingSomething(e);

        Resize(ActOpcodes, 0x21);
        ActOpcodes[0x0c] = e => OMoveAllItems(e);
        ActOpcodes[0x1e] = e => OTellTime(e);
        ActOpcodes[0x1f] = e => OSetRoomFromVar(e);
        ActOpcodes[0x20] = e => OInitDisk(e);
    }

    protected override void InitState()
    {
        base.InitState();

        LinesPrinted = 0;
        PicOnScreen = 0;
        RoomOnScreen = 0;
        ItemRemoved = false;
        ItemsOnScreen = 0;
    }

    protected override byte RoomArg(byte room) => room == IdiCurRoom ? _state.Room : room;

    protected override void AdvanceClock()
    {
        var time = _state.Time;
        time.Minutes += 5;

        if (time.Minutes == 60)
        {
            time.Minutes = 0;
            ++time.Hours;

            if (time.Hours == 13)
            {
                time.Hours = 1;
            }
        }
    }

    protected async Task CheckTextOverflow(char c)
    {
        if (c != Display.AsciiToNative('\r'))
        {
            return;
        }

        ++LinesPrinted;

        if (LinesPrinted >= MaxLines)
        {
            await HandleTextOverflow();
        }
    }

    /// <summary>The text window is full: ring, and wait for Return (ringing three times at any other key).</summary>
    protected async Task HandleTextOverflow()
    {
        LinesPrinted = 0;
        Display.RenderText();

        await Bell();

        InTextOverflow = true;
        try
        {
            while (true)
            {
                byte key = await InputKey(false);

                if (ShouldQuit)
                {
                    return;
                }

                if (key == Display.AsciiToNative('\r'))
                {
                    break;
                }

                await Bell(3);
            }
        }
        finally
        {
            InTextOverflow = false;
        }
    }

    protected override string LoadMessage(int idx)
    {
        if (Messages[idx] is { } block)
        {
            return ReadString(block.CreateReadStream(), 0xff);
        }

        return string.Empty;
    }

    protected override async Task PrintString(string str)
    {
        var s = str.ToCharArray();
        int textWidth = Display.TextWidth;
        int endPos = textWidth - 1;
        int startPos = 0;
        int pos = 0;

        char spaceChar = Display.AsciiToNative(' ');
        char returnChar = Display.AsciiToNative('\r');

        while (pos < s.Length)
        {
            s[pos] = Display.AsciiToNative(s[pos]);

            if (pos == endPos)
            {
                while (s[pos] != spaceChar && s[pos] != returnChar)
                {
                    if (pos-- == startPos)
                    {
                        throw new InvalidDataException("Word wrapping failed");
                    }
                }

                s[pos] = returnChar;
                endPos = pos + textWidth;
                startPos = pos + 1;
            }

            ++pos;
        }

        foreach (char c in s)
        {
            await CheckTextOverflow(c);
            Display.PrintChar(c);
        }

        await CheckTextOverflow(returnChar);
        Display.PrintChar(returnChar);
        Display.RenderText();
    }

    protected override void DrawItem(Item item, int x, int y)
    {
        item.IsOnScreen = true;

        if (item.Picture == 0 || item.Picture - 1 >= ItemPics.Count || ItemPics[item.Picture - 1] is not { } block)
        {
            Host.Log.Warning("adl", $"Item picture {item.Picture} not found");
            return;
        }

        var stream = block.CreateReadStream();
        stream.ReadByte(); // skip clear opcode
        Graphics.DrawPic(stream, x, y);
    }

    protected override void LoadRoom(byte roomNr)
    {
        if (BrokenRooms.Contains(roomNr))
        {
            Host.Log.Warning("adl", $"Attempt to load non-existent room {roomNr}");
            CurrentRoomData.Description = string.Empty;
            CurrentRoomData.Pictures.Clear();
            CurrentRoomData.Commands.Clear();
            return;
        }

        var room = GetRoom(roomNr);
        var stream = room.Data?.CreateReadStream() ?? throw new InvalidDataException($"Room {roomNr} has no data");

        int descOffset = stream.ReadUInt16LE();
        int commandOffset = stream.ReadUInt16LE();

        CurrentRoomData.Pictures.Clear();

        // there's no picture count; the original always checks at most five pictures, and the
        // description offset bounds the search
        int picCount = (descOffset - 4) / 5;

        for (int i = 0; i < picCount; ++i)
        {
            byte nr = stream.ReadByte();
            CurrentRoomData.Pictures[nr] = ReadDataBlockPtr(stream);
        }

        CurrentRoomData.Description = ReadStringAt(stream, descOffset, 0xff);

        CurrentRoomData.Commands.Clear();
        if (commandOffset != 0)
        {
            stream.Seek(commandOffset);
            ReadCommands(stream, CurrentRoomData.Commands);
        }

        ApplyRoomWorkarounds(roomNr);
    }

    protected override async Task ShowRoom()
    {
        bool redrawPic = false;

        _state.CurPicture = GetCurRoom().CurPicture;

        if (_state.Room != RoomOnScreen)
        {
            LoadRoom(_state.Room);
            Graphics.ClearScreen();

            if (!_state.IsDark)
            {
                redrawPic = true;
            }
        }
        else if (_state.CurPicture != PicOnScreen || ItemRemoved)
        {
            redrawPic = true;
        }

        if (redrawPic)
        {
            RoomOnScreen = _state.Room;
            PicOnScreen = _state.CurPicture;

            DrawPic(_state.CurPicture);
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
        await PrintString(CurrentRoomData.Description);
    }

    protected override async Task TakeItem(byte noun)
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

                bool itemIsHere = item.State == ItemDropped
                    || item.RoomPictures.Any(pic => pic == GetCurRoom().CurPicture || pic == IdiAny);

                if (itemIsHere)
                {
                    if (!await IsInventoryFull())
                    {
                        item.Room = IdiAny;
                        ItemRemoved = true;
                        item.State = ItemDropped;
                    }

                    return;
                }
            }
        }

        await PrintMessage(MessageIdTable.ItemNotHere);
    }

    protected override void DrawItems()
    {
        foreach (var item in _state.Items)
        {
            // skip items not in this room
            if (item.Region == _state.Region && item.Room == _state.Room && !item.IsOnScreen)
            {
                if (item.State == ItemDropped)
                {
                    // draw a dropped item if in normal view
                    if (GetCurRoom().Picture == GetCurRoom().CurPicture && ItemsOnScreen < ItemOffsets.Count)
                    {
                        var (x, y) = ItemOffsets[ItemsOnScreen++];
                        DrawItem(item, x, y);
                    }
                }
                else
                {
                    // draw a fixed item if the current view is in its picture list
                    foreach (byte pic in item.RoomPictures)
                    {
                        if (pic == _state.CurPicture || pic == IdiAny)
                        {
                            DrawItem(item, item.PositionX, item.PositionY);
                            break;
                        }
                    }
                }
            }
        }
    }

    protected virtual void AdjustDataBlockPtr(ref byte track, ref byte sector, ref byte offset, ref byte size)
    {
    }

    protected virtual DataBlock? ReadDataBlockPtr(SeekableReadStream f)
    {
        byte track = f.ReadByte();
        byte sector = f.ReadByte();
        byte offset = f.ReadByte();
        byte size = f.ReadByte();

        if (f.Eos)
        {
            throw new InvalidDataException("Error reading DataBlockPtr");
        }

        if (track == 0 && sector == 0 && offset == 0 && size == 0)
        {
            return null;
        }

        AdjustDataBlockPtr(ref track, ref sector, ref offset, ref size);
        return Disk!.GetDataBlock(track, sector, offset, size);
    }

    protected void LoadItems(SeekableReadStream stream)
    {
        byte id;
        while ((id = stream.ReadByte()) != 0xff && !stream.Eos)
        {
            var item = new Item
            {
                Id = id,
                Noun = stream.ReadByte(),
                Room = stream.ReadByte(),
                Picture = stream.ReadByte(),
                Region = stream.ReadByte(),
                PositionX = stream.ReadByte(),
                PositionY = stream.ReadByte(),
                State = stream.ReadByte(),
                Description = stream.ReadByte(),
            };

            stream.ReadByte(); // struct size

            int picListSize = stream.ReadByte();

            // flag to keep track of what has been drawn on the screen
            stream.ReadByte();

            for (int i = 0; i < picListSize; ++i)
            {
                item.RoomPictures.Add(stream.ReadByte());
            }

            _state.Items.Add(item);
        }

        if (stream.Eos)
        {
            throw new InvalidDataException("Error loading items");
        }
    }

    protected void LoadRooms(SeekableReadStream stream, int count)
    {
        for (int i = 0; i < count; ++i)
        {
            var room = new Room();

            stream.ReadByte(); // number
            for (int j = 0; j < 6; ++j)
            {
                room.Connections[j] = stream.ReadByte();
            }

            room.Data = ReadDataBlockPtr(stream);
            room.Picture = stream.ReadByte();
            room.CurPicture = stream.ReadByte();
            room.IsFirstTime = stream.ReadByte() != 0;

            _state.Rooms.Add(room);
        }

        if (stream.Eos)
        {
            throw new InvalidDataException("Error loading rooms");
        }
    }

    protected void LoadMessages(SeekableReadStream stream, int count)
    {
        for (int i = 0; i < count; ++i)
        {
            Messages.Add(ReadDataBlockPtr(stream));
        }
    }

    protected void LoadPictures(SeekableReadStream stream)
    {
        byte picNr;
        while ((picNr = stream.ReadByte()) != 0xff)
        {
            if (stream.Eos)
            {
                throw new InvalidDataException("Error reading global pic list");
            }

            Pictures[picNr] = ReadDataBlockPtr(stream);
        }
    }

    protected void LoadItemPictures(SeekableReadStream stream, int count)
    {
        for (int i = 0; i < count; ++i)
        {
            stream.ReadByte(); // number
            ItemPics.Add(ReadDataBlockPtr(stream));
        }
    }

    protected virtual Task<bool> IsInventoryFull() => Task.FromResult(false);

    protected async Task<int> AskForSlot(string question)
    {
        while (true)
        {
            Display.PrintString(question);

            string input = await InputString();

            if (ShouldQuit)
            {
                return -1;
            }

            if (input.Length > 0 && input[0] >= Display.AsciiToNative('A') && input[0] <= Display.AsciiToNative('O'))
            {
                return input[0] - Display.AsciiToNative('A');
            }
        }
    }

    protected override bool CanSaveGameStateCurrentlyCore()
    {
        if (!CanSaveNow)
        {
            return false;
        }

        // back up the first visit flag, as this test may change it
        bool isFirstTime = GetCurRoom().IsFirstTime;
        bool result = base.CanSaveGameStateCurrentlyCore();
        GetCurRoom().IsFirstTime = isFirstTime;
        return result;
    }

    // ---- opcodes ----

    protected virtual int OIsFirstTime(ScriptEnv e)
    {
        Trace(() => "\t&& IS_FIRST_TIME()");

        bool oldFlag = GetCurRoom().IsFirstTime;
        GetCurRoom().IsFirstTime = false;
        return oldFlag ? 0 : -1;
    }

    protected virtual int OIsRandomGT(ScriptEnv e)
    {
        Trace(() => $"\t&& RAND() > {e.Arg(1)}");
        int rnd = Random.Next(256);
        return rnd > e.Arg(1) ? 1 : -1;
    }

    protected virtual int OIsNounNotInRoom(ScriptEnv e)
    {
        Trace(() => $"\t&& NO_SUCH_ITEMS_IN_ROOM({ItemRoomStr(e.Arg(1))})");

        foreach (var item in _state.Items)
        {
            if (item.Noun == e.Noun && item.Room == RoomArg(e.Arg(1)))
            {
                return -1;
            }
        }

        return 1;
    }

    protected virtual int OIsCarryingSomething(ScriptEnv e)
    {
        Trace(() => "\t&& IS_CARRYING_SOMETHING()");
        return _state.Items.Any(item => item.Room == IdiAny) ? 0 : -1;
    }

    protected override ValueTask<int> OMoveItem(ScriptEnv e)
    {
        Trace(() => $"\tSET_ITEM_ROOM({ItemStr(e.Arg(1))}, {ItemRoomStr(e.Arg(2))})");

        byte room = RoomArg(e.Arg(2));
        var item = GetItem(e.Arg(1));

        if (item.Room == RoomOnScreen)
        {
            PicOnScreen = 0;
        }

        // items that move from the inventory to a room are "dropped"
        if (item.Room == IdiAny && room != IdiVoidRoom)
        {
            item.State = ItemDropped;
        }

        item.Room = room;
        return Done(2);
    }

    protected override ValueTask<int> OSetCurPic(ScriptEnv e)
    {
        Trace(() => $"\tSET_CURPIC({e.Arg(1)})");
        GetCurRoom().CurPicture = _state.CurPicture = e.Arg(1);
        return Done(1);
    }

    protected override ValueTask<int> OSetPic(ScriptEnv e)
    {
        Trace(() => $"\tSET_PIC({e.Arg(1)})");
        GetCurRoom().Picture = GetCurRoom().CurPicture = _state.CurPicture = e.Arg(1);
        return Done(1);
    }

    protected virtual ValueTask<int> OMoveAllItems(ScriptEnv e)
    {
        Trace(() => $"\tMOVE_ALL_ITEMS({ItemRoomStr(e.Arg(1))}, {ItemRoomStr(e.Arg(2))})");

        byte room1 = RoomArg(e.Arg(1));

        if (room1 == _state.Room)
        {
            PicOnScreen = 0;
        }

        byte room2 = RoomArg(e.Arg(2));

        foreach (var item in _state.Items)
        {
            if (item.Room == room1)
            {
                item.Room = room2;
                if (room1 == IdiAny)
                {
                    item.State = ItemDropped;
                }
            }
        }

        return Done(2);
    }

    protected override async ValueTask<int> OSave(ScriptEnv e)
    {
        Trace(() => "\tSAVE_GAME()");

        int slot = await AskForSlot(StringsV2.SaveInsert);

        if (slot < 0)
        {
            return -1;
        }

        SaveGameState(slot, string.Empty);

        Display.PrintString(StringsV2.SaveReplace);
        await InputString();
        return 0;
    }

    protected override async ValueTask<int> ORestore(ScriptEnv e)
    {
        Trace(() => "\tRESTORE_GAME()");

        int slot = await AskForSlot(StringsV2.RestoreInsert);

        if (slot < 0)
        {
            return -1;
        }

        LoadGameState(slot);
        IsRestoring = false;

        Display.PrintString(StringsV2.RestoreReplace);
        await InputString();
        PicOnScreen = 0;
        RoomOnScreen = 0;
        return 0;
    }

    protected override ValueTask<int> OPlaceItem(ScriptEnv e)
    {
        Trace(() => $"\tPLACE_ITEM({ItemStr(e.Arg(1))}, {ItemRoomStr(e.Arg(2))}, ({e.Arg(3)}, {e.Arg(4)}))");

        var item = GetItem(e.Arg(1));
        item.Room = RoomArg(e.Arg(2));
        item.PositionX = e.Arg(3);
        item.PositionY = e.Arg(4);
        item.State = ItemNotMoved;
        return Done(4);
    }

    protected virtual async ValueTask<int> OTellTime(ScriptEnv e)
    {
        Trace(() => "\tTELL_TIME()");

        string time = StringsV2.Time;

        if (time.Length <= 16)
        {
            throw new InvalidDataException("Invalid time string");
        }

        char zeroChar = Display.AsciiToNative('0');

        time = SetChar(time, 12, (char)(zeroChar + _state.Time.Hours / 10));
        time = SetChar(time, 13, (char)(zeroChar + _state.Time.Hours % 10));
        time = SetChar(time, 15, (char)(zeroChar + _state.Time.Minutes / 10));
        time = SetChar(time, 16, (char)(zeroChar + _state.Time.Minutes % 10));

        await PrintString(time);
        return 0;
    }

    protected virtual ValueTask<int> OSetRoomFromVar(ScriptEnv e)
    {
        Trace(() => $"\tROOM = VAR[{e.Arg(1)}]");
        GetCurRoom().CurPicture = GetCurRoom().Picture;
        _state.Room = GetVar(e.Arg(1));
        return Done(1);
    }

    protected virtual ValueTask<int> OInitDisk(ScriptEnv e)
    {
        Trace(() => "\tINIT_DISK()");
        Display.PrintAsciiString("NOT REQUIRED\r");
        return Done(0);
    }

    /// <summary>The v2 opcode strings.</summary>
    protected sealed class V2Strings
    {
        public string Time { get; set; } = string.Empty;

        public string SaveInsert { get; set; } = string.Empty;

        public string SaveReplace { get; set; } = string.Empty;

        public string RestoreInsert { get; set; } = string.Empty;

        public string RestoreReplace { get; set; } = string.Empty;
    }
}
