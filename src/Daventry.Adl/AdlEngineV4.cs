using Daventry.Adl.Detection;
using Daventry.Core.Formats;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;

namespace Daventry.Adl;

/// <summary>
/// ADL v4 (Time Zone): the world is split into regions, each with its own rooms, words, messages
/// and commands loaded from the disk when the player enters it, and its own saved variables and
/// room states. Port of ScummVM's AdlEngine_v4.
/// </summary>
public abstract class AdlEngineV4 : AdlEngineV3
{
    protected AdlEngineV4(IHost host, IGameContainer game, AdlDetectedGame detected, DisplayOptions options)
        : base(host, game, detected, options)
    {
    }

    protected enum RegionChunkType
    {
        Unknown,
        Messages,
        GlobalPics,
        Verbs,
        Nouns,
        Rooms,
        RoomCmds,
        GlobalCmds,
    }

    /// <summary>Base track and sector of each region.</summary>
    protected List<(byte Track, byte Sector)> RegionLocations { get; } = [];

    /// <summary>Where each region's seven initial data blocks are, relative to its location, and on which disk.</summary>
    protected List<(byte Track, byte Sector, byte Offset, byte Volume)> RegionInitDataOffsets { get; } = [];

    protected SeekableReadStream? ItemPicIndex { get; set; }

    protected override async Task GameLoop()
    {
        int verb, noun;
        IsRestarting = false;

        if (IsRestoring)
        {
            // restored from the launcher; this version long jumps to the game loop after restoring
            IsRestoring = false;
        }

        await ShowRoom();

        if (IsRestarting || ShouldQuit)
        {
            return;
        }

        CanSaveNow = CanRestoreNow = true;
        (verb, noun) = await GetInput();
        CanSaveNow = CanRestoreNow = false;

        if (IsRestoring)
        {
            // restored from the menu: move the cursor to the next line and start the loop again
            Display.PrintAsciiString("\r");
            IsRestoring = false;
            return;
        }

        if (IsRestarting || ShouldQuit)
        {
            return;
        }

        LinesPrinted = 0;

        await CheckInput((byte)verb, (byte)noun);

        if (IsRestarting || ShouldQuit)
        {
            return;
        }

        await DoAllCommands(GlobalCommands, (byte)verb, (byte)noun);

        if (IsRestarting || ShouldQuit)
        {
            return;
        }

        _state.Moves++;
    }

    protected override void LoadState(SeekableReadStream stream)
    {
        _state.Room = stream.ReadByte();
        _state.Region = stream.ReadByte();
        _state.PrevRegion = stream.ReadByte();

        uint size = stream.ReadUInt32BE();
        if (size != _state.Regions.Count)
        {
            throw new InvalidDataException($"Region count mismatch (expected {_state.Regions.Count}; found {size})");
        }

        foreach (var region in _state.Regions)
        {
            size = stream.ReadUInt32BE();
            if (size != region.Rooms.Count)
            {
                throw new InvalidDataException($"Room count mismatch (expected {region.Rooms.Count}; found {size})");
            }

            foreach (var room in region.Rooms)
            {
                room.Picture = stream.ReadByte();
                room.IsFirstTime = stream.ReadByte();
            }

            size = stream.ReadUInt32BE();
            if (size != region.Vars.Count)
            {
                throw new InvalidDataException($"Variable count mismatch (expected {region.Vars.Count}; found {size})");
            }

            for (int i = 0; i < region.Vars.Count; ++i)
            {
                region.Vars[i] = stream.ReadByte();
            }
        }

        size = stream.ReadUInt32BE();
        if (size != _state.Items.Count)
        {
            throw new InvalidDataException($"Item count mismatch (expected {_state.Items.Count}; found {size})");
        }

        foreach (var item in _state.Items)
        {
            item.Room = stream.ReadByte();
            item.Region = stream.ReadByte();
            item.State = stream.ReadByte();
        }

        size = stream.ReadUInt32BE();
        int expectedSize = _state.Vars.Count - GetRegion(1).Vars.Count;
        if (size != expectedSize)
        {
            throw new InvalidDataException($"Variable count mismatch (expected {expectedSize}; found {size})");
        }

        for (int i = GetRegion(1).Vars.Count; i < _state.Vars.Count; ++i)
        {
            _state.Vars[i] = stream.ReadByte();
        }

        if (stream.Eos)
        {
            return;
        }

        LoadRegion(_state.Region);
        RestoreRoomState(_state.Room);
        RoomOnScreen = PicOnScreen = 0;
    }

    protected override void SaveState(BinaryWriter writer)
    {
        GetCurRoom().IsFirstTime = false;
        BackupVars();
        BackupRoomState(_state.Room);

        writer.Write(_state.Room);
        writer.Write(_state.Region);
        writer.Write(_state.PrevRegion);

        WriteUInt32BE(writer, (uint)_state.Regions.Count);
        foreach (var region in _state.Regions)
        {
            WriteUInt32BE(writer, (uint)region.Rooms.Count);
            foreach (var room in region.Rooms)
            {
                writer.Write(room.Picture);
                writer.Write(room.IsFirstTime);
            }

            WriteUInt32BE(writer, (uint)region.Vars.Count);
            foreach (byte v in region.Vars)
            {
                writer.Write(v);
            }
        }

        WriteUInt32BE(writer, (uint)_state.Items.Count);
        foreach (var item in _state.Items)
        {
            writer.Write(item.Room);
            writer.Write(item.Region);
            writer.Write((byte)item.State);
        }

        WriteUInt32BE(writer, (uint)(_state.Vars.Count - GetRegion(1).Vars.Count));
        for (int i = GetRegion(1).Vars.Count; i < _state.Vars.Count; ++i)
        {
            writer.Write(_state.Vars[i]);
        }
    }

    protected override string LoadMessage(int idx)
    {
        if (idx < Messages.Count && Messages[idx] is { } block)
        {
            return ReadString(block.CreateReadStream(), 0xff, "AVISDURGAN");
        }

        return string.Empty;
    }

    protected override string GetItemDescription(Item item) => ItemDesc[item.Id - 1];

    protected void LoadRegionLocations(SeekableReadStream stream, int regions)
    {
        for (int r = 0; r < regions; ++r)
        {
            byte track = stream.ReadByte();
            byte sector = stream.ReadByte();

            if (stream.Eos)
            {
                throw new InvalidDataException("Failed to read region locations");
            }

            RegionLocations.Add((track, sector));
        }
    }

    protected void LoadRegionInitDataOffsets(SeekableReadStream stream, int regions)
    {
        for (int r = 0; r < regions; ++r)
        {
            byte track = stream.ReadByte();
            byte sector = stream.ReadByte();
            byte offset = stream.ReadByte();
            byte volume = stream.ReadByte();

            if (stream.Eos)
            {
                throw new InvalidDataException("Failed to read region init data offsets");
            }

            RegionInitDataOffsets.Add((track, sector, offset, volume));
        }
    }

    protected virtual void InitRoomState(RoomState roomState)
    {
        roomState.Picture = 1;
        roomState.IsFirstTime = 1;
    }

    protected void InitRegions(ReadOnlySpan<byte> roomsPerRegion)
    {
        _state.Regions.Clear();
        foreach (byte rooms in roomsPerRegion)
        {
            var region = new Region();

            // each region has 24 variables
            ResizeVars(region.Vars, 24);

            for (int rm = 0; rm < rooms; ++rm)
            {
                var roomState = new RoomState();
                InitRoomState(roomState);
                region.Rooms.Add(roomState);
            }

            _state.Regions.Add(region);
        }
    }

    protected void FixupDiskOffset(ref byte track, ref byte sector)
    {
        if (_state.Region == 0)
        {
            return;
        }

        sector += RegionLocations[_state.Region - 1].Sector;
        if (sector >= 16)
        {
            sector -= 16;
            ++track;
        }

        track += RegionLocations[_state.Region - 1].Track;
    }

    protected override void AdjustDataBlockPtr(ref byte track, ref byte sector, ref byte offset, ref byte size)
        => FixupDiskOffset(ref track, ref sector);

    protected virtual RegionChunkType GetRegionChunkType(ushort addr) => addr switch
    {
        0x9000 => RegionChunkType.Messages,
        0x4a80 => RegionChunkType.GlobalPics,
        0x4000 => RegionChunkType.Verbs,
        0x1800 => RegionChunkType.Nouns,
        0x0e00 => RegionChunkType.Rooms,
        0x7b00 => RegionChunkType.RoomCmds,
        0x9500 => RegionChunkType.GlobalCmds,
        _ => RegionChunkType.Unknown,
    };

    protected void LoadRegion(byte region)
    {
        if (CurrentVolume != RegionInitDataOffsets[region - 1].Volume)
        {
            InsertDisk(RegionInitDataOffsets[region - 1].Volume);

            // FIXME in ScummVM too: needed because data block pointers are bound to their disk image
            _state.Region = 0; // to avoid the region offset being applied
            ItemPics.Clear();
            ItemPicIndex!.Seek(0);
            LoadItemPictures(ItemPicIndex, ItemPicIndex.Size / 5);
        }

        _state.Region = region;

        byte track = RegionInitDataOffsets[region - 1].Track;
        byte sector = RegionInitDataOffsets[region - 1].Sector;
        int offset = RegionInitDataOffsets[region - 1].Offset;

        FixupDiskOffset(ref track, ref sector);

        for (int block = 0; block < 7; ++block)
        {
            var stream = Disk!.CreateReadStream(track, sector, offset, 1) ?? throw new InvalidDataException("Error reading region data");

            ushort addr = stream.ReadUInt16LE();
            ushort size = stream.ReadUInt16LE();

            stream = Disk.CreateReadStream(track, sector, offset, size / 256 + 1) ?? throw new InvalidDataException("Error reading region data");
            stream.Skip(4);

            switch (GetRegionChunkType(addr))
            {
                case RegionChunkType.Messages:
                    Messages.Clear();
                    LoadMessages(stream, size / 4);
                    break;
                case RegionChunkType.GlobalPics:
                    Pictures.Clear();
                    LoadPictures(stream);
                    break;
                case RegionChunkType.Verbs:
                    LoadWords(stream, Verbs, PriVerbs);
                    break;
                case RegionChunkType.Nouns:
                    LoadWords(stream, Nouns, PriNouns);
                    break;
                case RegionChunkType.Rooms:
                    stream.Skip(14); // skip invalid room 0
                    _state.Rooms.Clear();
                    LoadRooms(stream, size / 14 - 1);
                    break;
                case RegionChunkType.RoomCmds:
                    ReadCommands(stream, RoomCommands);
                    break;
                case RegionChunkType.GlobalCmds:
                    ReadCommands(stream, GlobalCommands);
                    break;
                default:
                    throw new InvalidDataException($"Unknown data block found (addr {addr:x4}; size {size:x4})");
            }

            offset += 4 + size;
            while (offset >= 256)
            {
                offset -= 256;
                ++sector;
                if (sector >= 16)
                {
                    sector = 0;
                    ++track;
                }
            }
        }

        ApplyRegionWorkarounds();
        RestoreVars();
    }

    protected void LoadItemPicIndex(SeekableReadStream stream, int items)
    {
        ItemPicIndex = stream.ReadStream(items * 5);

        if (stream.Eos)
        {
            throw new InvalidDataException("Error reading item index");
        }
    }

    protected void BackupRoomState(byte room)
    {
        var backup = GetCurRegion().Rooms[room - 1];
        backup.IsFirstTime = GetRoom(room).IsFirstTime ? (byte)1 : (byte)0;
        backup.Picture = GetRoom(room).Picture;
    }

    protected virtual byte RestoreRoomState(byte room)
    {
        var backup = GetCurRegion().Rooms[room - 1];

        if (backup.IsFirstTime != 1)
        {
            GetRoom(room).CurPicture = GetRoom(room).Picture = backup.Picture;
            GetRoom(room).IsFirstTime = false;
            return 0;
        }

        // WORKAROUND from ScummVM (bug #15379, Time Zone unsolvable after loading a save): the
        // original ignores a picture set for a room not visited yet when the game is restored
        if (backup.Picture != 1)
        {
            Host.Log.Warning("adl", $"Detected picture change for unvisited room {room} in region {_state.Region}");
            GetRoom(room).CurPicture = GetRoom(room).Picture = backup.Picture;
        }

        return 1;
    }

    protected void BackupVars()
    {
        var region = GetCurRegion();
        for (int i = 0; i < region.Vars.Count; ++i)
        {
            region.Vars[i] = GetVar(i);
        }
    }

    protected void RestoreVars()
    {
        var region = GetCurRegion();
        for (int i = 0; i < region.Vars.Count; ++i)
        {
            SetVar(i, region.Vars[i]);
        }
    }

    protected override void SwitchRegion(byte region)
    {
        BackupVars();
        BackupRoomState(_state.Room);
        _state.PrevRegion = _state.Region;
        _state.Region = region;
        LoadRegion(region);
        _state.Room = 1;
        PicOnScreen = RoomOnScreen = 0;
    }

    protected override void SwitchRoom(byte roomNr)
    {
        GetCurRoom().CurPicture = GetCurRoom().Picture;
        GetCurRoom().IsFirstTime = false;
        BackupRoomState(_state.Room);
        _state.Room = roomNr;
        RestoreRoomState(_state.Room);
    }

    protected override void SetupOpcodeTables()
    {
        base.SetupOpcodeTables();

        CondOpcodes[0x08] = e => OIsVarGT(e);
        CondOpcodes[0x0a] = null;

        ActOpcodes[0x0a] = e => OSetRegionToPrev(e);
        ActOpcodes[0x0b] = null;
        ActOpcodes[0x0e] = e => OSetRegion(e);
        ActOpcodes[0x12] = e => OSetRegionRoom(e);
        ActOpcodes[0x13] = null;
        ActOpcodes[0x1e] = null;
        ActOpcodes[0x1f] = null;
    }

    protected override int OIsItemInRoom(ScriptEnv e)
    {
        Trace(() => $"\t&& GET_ITEM_ROOM({ItemStr(e.Arg(1))}) == {ItemRoomStr(e.Arg(2))}");

        var item = GetItem(e.Arg(1));

        if (e.Arg(2) != IdiAny && item.Region != _state.Region)
        {
            return -1;
        }

        return item.Room == RoomArg(e.Arg(2)) ? 2 : -1;
    }

    protected virtual int OIsVarGT(ScriptEnv e)
    {
        Trace(() => $"\t&& VARS[{e.Arg(1)}] > {e.Arg(2)}");
        return GetVar(e.Arg(1)) > e.Arg(2) ? 2 : -1;
    }

    protected override async ValueTask<int> OMoveItem(ScriptEnv e)
    {
        await base.OMoveItem(e);
        GetItem(e.Arg(1)).Region = _state.Region;
        return 2;
    }

    protected virtual ValueTask<int> OSetRegionToPrev(ScriptEnv e)
    {
        Trace(() => "\tREGION = PREV_REGION");
        SwitchRegion(_state.PrevRegion);

        // long jump
        IsRestarting = true;
        return Done(-1);
    }

    protected override async ValueTask<int> OMoveAllItems(ScriptEnv e)
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
            if (room1 != item.Room)
            {
                continue;
            }

            if (room1 != IdiAny)
            {
                if (_state.Region != item.Region)
                {
                    continue;
                }

                if (room2 == IdiAny)
                {
                    if (await IsInventoryFull())
                    {
                        break;
                    }

                    if (item.State == ItemDoesntMove)
                    {
                        continue;
                    }
                }
            }

            item.Room = room2;
            item.Region = _state.Region;

            if (room1 == IdiAny)
            {
                item.State = ItemDropped;
            }
        }

        return 2;
    }

    protected virtual ValueTask<int> OSetRegion(ScriptEnv e)
    {
        Trace(() => $"\tREGION = {e.Arg(1)}");
        SwitchRegion(e.Arg(1));

        // long jump
        IsRestarting = true;
        return Done(-1);
    }

    protected override async ValueTask<int> OSave(ScriptEnv e)
    {
        Trace(() => "\tSAVE_GAME()");

        Display.PrintString(StringsV2.SaveReplace);
        byte key = await InputKey();

        if (ShouldQuit)
        {
            return -1;
        }

        if (key != Display.AsciiToNative('Y'))
        {
            return 0;
        }

        int slot = await AskForSlot(StringsV2.SaveInsert);

        if (slot < 0)
        {
            return -1;
        }

        SaveGameState(slot, string.Empty);
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

        PicOnScreen = 0;
        RoomOnScreen = 0;

        // long jump
        IsRestarting = true;
        return -1;
    }

    protected override async ValueTask<int> ORestart(ScriptEnv e)
    {
        Trace(() => "\tRESTART_GAME()");

        while (true)
        {
            Display.PrintString(Strings.PlayAgain);
            string input = await InputString();

            if (ShouldQuit)
            {
                return -1;
            }

            if (input.Length > 0 && input[0] == Display.AsciiToNative('N'))
            {
                return await OQuit(e);
            }

            if (input.Length > 0 && input[0] == Display.AsciiToNative('Y'))
            {
                // the original loads a special saved game from volume 3
                InitState();

                // long jump
                IsRestarting = true;
                return -1;
            }
        }
    }

    protected virtual ValueTask<int> OSetRegionRoom(ScriptEnv e)
    {
        Trace(() => $"\tSET_REGION_ROOM({e.Arg(1)}, {e.Arg(2)})");

        SwitchRegion(e.Arg(1));
        _state.Room = e.Arg(2);

        // long jump
        IsRestarting = true;
        return Done(-1);
    }

    protected override async ValueTask<int> OSetRoomPic(ScriptEnv e)
    {
        await base.OSetRoomPic(e);
        BackupRoomState(e.Arg(1));
        return 2;
    }
}
