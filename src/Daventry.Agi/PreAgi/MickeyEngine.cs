using System.Buffers.Binary;
using Daventry.Agi.Graphics;
using Daventry.Core.Hosting;
using Daventry.Core.Input;
using Daventry.Core.Storage;
using static Daventry.Agi.PreAgi.MickeyData;

namespace Daventry.Agi.PreAgi;

/// <summary>
/// Mickey's Space Adventure (1986). Port of ScummVM's MickeyEngine.
/// <para>
/// The game's text, menus and sound data live in mickey.exe and in one .dat file per planet;
/// pictures are one file per room and object. Mickey collects nine crystal pieces across the
/// solar system, flying the ship by pressing orange and blue buttons in the order the computer
/// XL30 hints at. Sound is the PC speaker, one note at a time.
/// </para>
/// </summary>
public sealed class MickeyEngine : PreAgiEngine
{
    private const string ExeName = "mickey.exe";
    private const int ActionAnimateMs = AnimDelay;

    private readonly PictureMgrMickeyWinnie _picture;
    private readonly GameState _state = new();
    private readonly Dictionary<int, DatFile> _datFiles = [];
    private byte[] _exe = [];
    private bool _clickToMove;
    private bool _isGameOver;

    public MickeyEngine(IHost host, IGameContainer game, AgiDetectedGame detected, AgiRenderMode renderMode = AgiRenderMode.Ega)
        : base(host, game, detected, renderMode)
    {
        _picture = new PictureMgrMickeyWinnie(Gfx, detected.Platform, host.Log);
    }

    /// <summary>The room Mickey is in.</summary>
    public int Room => _state.Room;

    public int Planet => _state.Planet;

    public int Crystals => _state.Xtals;

    public bool IsGameOver => _isGameOver;

    /// <summary>Port of MSA_GAME.</summary>
    private sealed class GameState
    {
        public byte Room;
        public byte Planet;
        public byte Disk;
        public byte Air;
        public byte Buttons;
        public byte Rocks;
        public byte Xtals;
        public readonly byte[] PlanetXtal = new byte[MaxDat];
        public readonly ushort[] Clue = new ushort[MaxPlanet];
        public readonly char[] Addr = new char[MaxButton + 1];

        // flags
        public bool HasXtal;
        public bool Intro;
        public bool Suit;
        public bool ShipDoorOpen;
        public bool Flying;
        public bool StoryShown;
        public bool PlanetsInitialized;
        public bool TempleDoorOpen;
        public bool AnimXl30;
        public readonly bool[] Item = new bool[MaxItem];
        public readonly bool[] ItemUsed = new bool[MaxItem];
        public readonly sbyte[] Items = new sbyte[MaxItem];
        public byte ItemCount;

        public readonly sbyte[] RmObj = new sbyte[MaxRoom];
        public readonly byte[] RmPic = new byte[MaxRoom];
        public readonly ushort[] RmTxt = new ushort[MaxRoom];
        public readonly byte[] RmMenu = new byte[MaxRoom];
        public readonly byte[] RmMenuCount = new byte[MaxRoom];
        public sbyte Frame;

        public string AddrString
        {
            get
            {
                int len = Array.IndexOf(Addr, '\0');
                return new string(Addr, 0, len < 0 ? Addr.Length : len);
            }
        }

        public void ClearAddr() => Array.Clear(Addr);
    }

    /// <summary>A planet's .dat file: its header of room, description and string offsets, plus the bytes.</summary>
    private sealed class DatFile
    {
        public DatFile(byte[] data)
        {
            Data = data;
            int p = 2;
            for (int i = 0; i < MaxRoom; i++)
            {
                OfsRoom[i] = Read16(data, p);
                p += 2;
            }

            for (int i = 0; i < MaxRoom; i++)
            {
                OfsDesc[i] = Read16(data, p);
                p += 2;
            }

            for (int i = 0; i < MaxRoom; i++)
            {
                OfsStr[i] = Read16(data, p);
                p += 2;
            }
        }

        public byte[] Data { get; }

        public ushort[] OfsRoom { get; } = new ushort[MaxRoom];

        public ushort[] OfsDesc { get; } = new ushort[MaxRoom];

        public ushort[] OfsStr { get; } = new ushort[MaxRoom];

        private static ushort Read16(byte[] data, int offset)
            => offset + 2 <= data.Length ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset)) : (ushort)0;
    }

    // ---- data files ----

    private static int GetDat(int room)
    {
        if ((room > 0 && room < 24) || room == 154 || room == 155)
        {
            return PlanetEarth;
        }

        return room switch
        {
            >= 30 and <= 39 => 1, // venus
            >= 40 and <= 69 => 2, // neptune
            >= 70 and <= 82 => 3, // mercury
            >= 83 and <= 92 => 4, // saturn
            >= 93 and <= 103 => 5, // pluto
            >= 106 and <= 120 => 6, // jupiter
            >= 121 and <= 132 => 7, // mars
            >= 133 and <= 145 => 8, // uranus
            _ => PlanetSpaceship,
        };
    }

    /// <summary>Bytes of mickey.exe from an offset, zero-padded past the end.</summary>
    private byte[] ReadExe(int offset, int length)
    {
        var buffer = new byte[length];
        if (offset >= 0 && offset < _exe.Length)
        {
            int count = Math.Min(length, _exe.Length - offset);
            _exe.AsSpan(offset, count).CopyTo(buffer);
        }

        return buffer;
    }

    private DatFile? GetDatFile(int room)
    {
        int dat = GetDat(room);
        if (_datFiles.TryGetValue(dat, out var file))
        {
            return file;
        }

        var data = TryReadFile(string.Format(PathDat, DatNames[dat]));
        if (data is null)
        {
            return null;
        }

        file = new DatFile(data);
        _datFiles[dat] = file;
        return file;
    }

    /// <summary>Follows an offset table in the executable: the entry for an item is itself an offset into the executable.</summary>
    private byte[] ReadOfsData(int offset, int item, int length)
    {
        var table = ReadExe(offset, length);
        int ofs = BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(item * 2));
        return ReadExe(ofs + OfsExe, length);
    }

    // ---- user interface ----

    private async Task<bool> ChooseYesNoAsync(int ofsPrompt, bool errorMsg)
    {
        PrintExeStr(ofsPrompt);

        while (!ShouldQuit)
        {
            switch (await GetSelectionAsync(SelectionType.YesNo))
            {
                case 0:
                    return false;
                case 1:
                    return true;
                default:
                    if (errorMsg)
                    {
                        PrintExeStr(MsgPressYesOrNo);
                        await WaitAnyKeyAsync();
                        PrintExeStr(ofsPrompt);
                    }

                    break;
            }
        }

        return false;
    }

    private async Task<int> Choose1To9Async(int ofsPrompt)
    {
        PrintExeStr(ofsPrompt);

        while (!ShouldQuit)
        {
            int answer = await GetSelectionAsync(SelectionType.Number);
            if (answer != 10)
            {
                return answer;
            }

            PrintExeStr(MsgPress1To9);
            if (await GetSelectionAsync(SelectionType.AnyKey) == 0)
            {
                return 0;
            }

            PrintExeStr(ofsPrompt);
        }

        return 0;
    }

    /// <summary>Prints a game message: a row count, then a column and a string per row.</summary>
    private void PrintStr(byte[] buffer)
    {
        ClearTextArea();

        int pc = 1;
        int rows = buffer.Length > 0 ? buffer[0] + RowMenu0 : RowMenu0;
        for (int row = RowMenu0; row < rows && pc < buffer.Length; row++)
        {
            int col = buffer[pc++];
            string text = ReadCString(buffer, pc);
            DrawStr(row, col, DefaultAttribute, text);
            pc += text.Length + 1;
        }
    }

    private async Task PrintLineAsync(string text)
    {
        ClearTextArea();
        DrawStr(22, 18 - text.Length / 2, DefaultAttribute, text);
        await WaitAnyKeyAsync(true);
    }

    private void PrintExeStr(int ofs)
    {
        if (ofs == 0)
        {
            return;
        }

        PrintStr(ReadExe(ofs, 256));
    }

    private async Task PrintExeMsgAsync(int ofs)
    {
        if (ofs == 0)
        {
            return;
        }

        PrintExeStr(ofs);
        await WaitAnyKeyAsync(true);
    }

    private void PrintDatString(int str)
    {
        var dat = GetDatFile(_state.Room);
        if (dat is null)
        {
            return;
        }

        PrintStr(Slice(dat.Data, dat.OfsStr[str] + OfsDat, 256));
    }

    private void PrintDesc(int room)
    {
        var dat = GetDatFile(room);
        if (dat is null)
        {
            return;
        }

        PrintStr(Slice(dat.Data, dat.OfsDesc[room - 1] + OfsDat, 256));
    }

    private async Task PrintDatMessageAsync(int str)
    {
        PrintDatString(str);
        await WaitAnyKeyAsync(true);
    }

    private static byte[] Slice(byte[] data, int offset, int length)
    {
        var buffer = new byte[length];
        if (offset >= 0 && offset < data.Length)
        {
            data.AsSpan(offset, Math.Min(length, data.Length - offset)).CopyTo(buffer);
        }

        return buffer;
    }

    // ---- the menu ----

    private async Task<bool> CheckMenuAsync()
    {
        var dat = GetDatFile(_state.Room);
        if (dat is null)
        {
            return false;
        }

        var menu = new MickeyMenu(Slice(dat.Data, dat.OfsRoom[_state.Room - 1] + OfsDat, MickeyMenu.Size));
        PatchMenu(menu);

        var (sel0, sel1) = await GetMenuSelAsync(menu);
        if (ShouldQuit)
        {
            return false;
        }

        return await ParseAsync(menu.Cmd(sel0, sel1), menu.Arg(sel0, sel1));
    }

    private void DrawMenu(MickeyMenu menu, int sel0, int sel1)
    {
        ClearTextArea();

        for (int row = 0; row < 2; row++)
        {
            for (int word = 0; word < menu.Count(row); word++)
            {
                int sel = row == 0 ? sel0 : sel1;
                int attr = word == sel ? DefaultAttributeReversed : DefaultAttribute;
                DrawStr(RowMenu0 + row, menu.X0(row, word), attr, menu.Text(row, word));
            }
        }
    }

    private static void GetMouseMenuSelRow(MickeyMenu menu, ref int sel, int row, int x, int y)
    {
        if (y != RowMenu0 + row)
        {
            return;
        }

        for (int word = 0; word < menu.Count(row); word++)
        {
            int x0 = menu.X0(row, word);
            if (x >= x0 && x < x0 + menu.Text(row, word).Length)
            {
                sel = word;
                break;
            }
        }
    }

    private static int FindWord(MickeyMenu menu, int row, string prefix)
    {
        for (int word = 0; word < menu.Count(row); word++)
        {
            if (menu.Text(row, word).StartsWith(prefix, StringComparison.Ordinal))
            {
                return word;
            }
        }

        return -1;
    }

    private static bool InNorthHotspot(int x, int y) => x >= 20 && x <= (PicWidth + 10) * 2 && y >= 0 && y <= 10;

    private static bool InSouthHotspot(int x, int y) => x >= 20 && x <= (PicWidth + 10) * 2 && y >= PicHeight - 10 && y <= PicHeight;

    private static bool InWestHotspot(int x, int y) => y >= 0 && y <= PicHeight && x >= 20 && x <= 30;

    private static bool InEastHotspot(int x, int y) => y >= 0 && y <= PicHeight && x >= PicWidth * 2 && x <= (PicWidth + 10) * 2;

    /// <summary>Lets the player pick a word in a menu row. Returns true on a confirmation, false to start over.</summary>
    private async Task<bool> GetMenuSelRowAsync(MickeyMenu menu, Selection selection, int row)
    {
        int goIndex = FindWord(menu, 0, "GO");
        int northIndex = -1;
        int southIndex = -1;
        int eastIndex = -1;
        int westIndex = -1;
        if (goIndex >= 0)
        {
            northIndex = FindWord(menu, 1, "NORTH");
            southIndex = FindWord(menu, 1, "SOUTH");
            eastIndex = FindWord(menu, 1, "EAST");
            westIndex = FindWord(menu, 1, "WEST");
        }

        int words = row < 2 ? menu.Count(row) : 0;
        _clickToMove = false;

        DrawMenu(menu, selection.Sel0, selection.Sel1);

        while (!ShouldQuit)
        {
            while (TryDequeueEvent(out var ev))
            {
                switch (ev.Type)
                {
                    case InputEventType.Quit:
                        return false;
                    case InputEventType.MouseMove:
                        if (row < 2)
                        {
                            int x = ev.X / 8;
                            int y = ev.Y / 8;

                            // if the mouse hovers over the menu, refresh the menu
                            if (y == RowMenu0 + row)
                            {
                                int sel = selection.Get(row);
                                GetMouseMenuSelRow(menu, ref sel, row, x, y);
                                selection.Set(row, sel);
                                DrawMenu(menu, selection.Sel0, selection.Sel1);
                            }
                        }

                        break;
                    case InputEventType.MouseUp when ev.Button == MouseButton.Left:
                    {
                        // click to move
                        int direction = -1;
                        if (northIndex >= 0 && InNorthHotspot(ev.X, ev.Y))
                        {
                            direction = northIndex;
                        }
                        else if (southIndex >= 0 && InSouthHotspot(ev.X, ev.Y))
                        {
                            direction = southIndex;
                        }
                        else if (westIndex >= 0 && InWestHotspot(ev.X, ev.Y))
                        {
                            direction = westIndex;
                        }
                        else if (eastIndex >= 0 && InEastHotspot(ev.X, ev.Y))
                        {
                            direction = eastIndex;
                        }

                        if (direction >= 0)
                        {
                            selection.Sel0 = goIndex;
                            selection.Sel1 = direction;
                            DrawMenu(menu, selection.Sel0, selection.Sel1);
                            _clickToMove = true;
                        }

                        return true;
                    }

                    case InputEventType.MouseUp when ev.Button == MouseButton.Right:
                        selection.Sel0 = 0;
                        selection.Sel1 = -1;
                        return false;
                    case InputEventType.KeyDown:
                        switch (ev.Key)
                        {
                            case Key.D2:
                                // hidden message
                                if (_state.Room == PicMercuryCave0)
                                {
                                    for (int i = 0; i < 5; i++)
                                    {
                                        await PrintExeMsgAsync(HiddenMsg[i]);
                                    }

                                    ClearTextArea();
                                    await WaitAnyKeyAsync();
                                }

                                break;
                            case Key.D8:
                                if ((ev.Modifiers & KeyModifiers.Ctrl) != 0)
                                {
                                    selection.Sel0 = 0;
                                    selection.Sel1 = -1;
                                    return false;
                                }

                                break;
                            case Key.Escape:
                                selection.Sel0 = 0;
                                selection.Sel1 = -1;
                                return false;
                            case Key.S:
                                SoundOn = !SoundOn;
                                break;
                            case Key.C:
                                await InventoryAsync();
                                await DrawRoomAsync();
                                selection.Sel0 = 0;
                                selection.Sel1 = -1;
                                return false;
                            case Key.B:
                                await PrintRoomDescAsync();
                                DrawMenu(menu, selection.Sel0, selection.Sel1);
                                selection.Sel0 = 0;
                                selection.Sel1 = -1;
                                return false;
                            case Key.Left:
                            case Key.Keypad4:
                            case Key.D4:
                                if (row < 2)
                                {
                                    int sel = selection.Get(row) - 1;
                                    if (sel < 0)
                                    {
                                        sel = words - 1;
                                    }

                                    selection.Set(row, sel);
                                    DrawMenu(menu, selection.Sel0, selection.Sel1);
                                }

                                break;
                            case Key.Right:
                            case Key.Space:
                            case Key.Keypad6:
                            case Key.D6:
                                if (row < 2)
                                {
                                    int sel = selection.Get(row) + 1;
                                    if (sel >= words)
                                    {
                                        sel = 0;
                                    }

                                    selection.Set(row, sel);
                                    DrawMenu(menu, selection.Sel0, selection.Sel1);
                                }

                                break;
                            case Key.Return:
                            case Key.KeypadEnter:
                                return true;
                        }

                        break;
                }

                await AnimateAsync();
                DrawMenu(menu, selection.Sel0, selection.Sel1);
            }

            await AnimateAsync();
            DrawMenu(menu, selection.Sel0, selection.Sel1);
        }

        return false;
    }

    /// <summary>The two menu selections, shared between the row pickers.</summary>
    private sealed class Selection
    {
        public int Sel0;
        public int Sel1 = -1;

        public int Get(int row) => row == 0 ? Sel0 : Sel1;

        public void Set(int row, int value)
        {
            if (row == 0)
            {
                Sel0 = value;
            }
            else
            {
                Sel1 = value;
            }
        }
    }

    private async Task<(int Sel0, int Sel1)> GetMenuSelAsync(MickeyMenu menu)
    {
        var selection = new Selection();
        AwaitingMenuSelection = true;
        try
        {
            while (!ShouldQuit)
            {
                while (!ShouldQuit)
                {
                    if (await GetMenuSelRowAsync(menu, selection, 0))
                    {
                        if (_clickToMove)
                        {
                            break;
                        }

                        selection.Sel1 = 0;
                        if (await GetMenuSelRowAsync(menu, selection, 1))
                        {
                            break;
                        }
                    }
                }

                if (_clickToMove || await GetMenuSelRowAsync(menu, selection, 2))
                {
                    break;
                }
            }
        }
        finally
        {
            AwaitingMenuSelection = false;
        }

        return (selection.Sel0, Math.Max(selection.Sel1, 0));
    }

    private static void CenterMenu(MickeyMenu menu)
    {
        for (int row = 0; row < 2; row++)
        {
            int w = 0;
            for (int word = 0; word < menu.Count(row); word++)
            {
                w += menu.Text(row, word).Length;
            }

            w += menu.Count(row) - 1;
            int x = (40 - w) / 2;

            for (int word = 0; word < menu.Count(row); word++)
            {
                menu.SetX0(row, word, x);
                x += menu.Text(row, word).Length + 1;
            }
        }
    }

    private void PatchMenu(MickeyMenu menu)
    {
        // change planet name in ship airlock menu
        if (_state.Room == PicShipAirlock)
        {
            menu.SetText(1, 2, PlanetNames[_state.Planet]);
        }

        // exit if fix unnecessary
        if (_state.RmMenu[_state.Room] == 0)
        {
            CenterMenu(menu);
            return;
        }

        // read patches
        var buffer = ReadOfsData(OfsMenuPatches, _state.RmMenuCount[_state.Room] + _state.RmMenu[_state.Room] - 1, 512);

        // patch the menu bytes
        int pBuf = 0;
        int patches = buffer[pBuf++];
        for (int patch = 0; patch < patches && pBuf + 1 < buffer.Length; patch++)
        {
            int address = buffer[pBuf];
            if (address < MickeyMenu.Size)
            {
                menu.Data[address] = buffer[pBuf + 1];
            }

            pBuf += 2;
        }

        CenterMenu(menu);
    }

    // ---- sound ----

    private async Task<bool> PlaySoundAsync(int sound, WaitOptions options = WaitOptions.ProcessEvents)
    {
        if (!SoundOn)
        {
            return true;
        }

        if (sound == SndXl30)
        {
            for (int note = 0; note < 6; note++)
            {
                int counter = Rnd(59600) + 59;
                if (!await PlayTimerNoteAsync(counter, 4, options))
                {
                    return false;
                }
            }

            return true;
        }

        var buffer = ReadOfsData(OfsSoundData, sound, 1024);
        int pBuf = 1;
        while (pBuf + 2 < buffer.Length)
        {
            int counter = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(pBuf));
            int length = buffer[pBuf + 2];
            if (counter == 0 && length == 0)
            {
                break;
            }

            if (!await PlayTimerNoteAsync(counter, length, options))
            {
                return false;
            }

            pBuf += 3;
        }

        return true;
    }

    // ---- graphics ----

    private void DrawObj(int obj, int x0, int y0)
    {
        var buffer = TryReadFile(string.Format(PathObj, ObjectNames[obj]));
        if (buffer is null)
        {
            return;
        }

        int maxStep = 0; // default: draw all opcodes
        if (obj == ObjectCrystal)
        {
            // Handle crystal animation. Each "frame" is the picture drawn with an additional
            // opcode until it wraps around. The crystal has 14 opcodes followed by the terminator.
            maxStep = _picture.MaxStep + 1;
            if (maxStep == 15)
            {
                maxStep = 1;
            }
        }

        _picture.MaxStep = maxStep;
        _picture.SetOffset(PicX0 + x0, PicY0 + y0);
        _picture.DecodePictureFromBuffer(buffer, buffer.Length, false, PicWidth, PicHeight);
        _picture.ShowPicture(PicX0, PicY0, PicWidth, PicHeight);
    }

    private void DrawPic(int pic)
    {
        var buffer = TryReadFile(string.Format(PathPic, pic));
        if (buffer is null)
        {
            return;
        }

        // note that decoding clears the screen
        _picture.MaxStep = 0;
        _picture.SetOffset(PicX0, PicY0);
        _picture.DecodePictureFromBuffer(buffer, buffer.Length, true, PicWidth, PicHeight);
        _picture.ShowPicture(PicX0, PicY0, PicWidth, PicHeight);
    }

    private async Task DrawRoomAnimationAsync()
    {
        switch (_state.Room)
        {
            case PicEarthShip:
            case PicVenusShip:
            case PicNeptuneShip:
            case PicMercuryShip:
            case PicSaturnShip:
            case PicPlutoShip:
            case PicJupiterShip:
            case PicMarsShip:
            case PicUranusShip:
            case PicShipVenus:
            case PicShipNeptune:
            case PicShipMercury:
            case PicShipSaturn:
            case PicShipPluto:
            case PicShipJupiter:
            case PicShipMars:
            case PicShipUranus:
            {
                // draw blinking ship lights
                byte[] lightPicture =
                [
                    0xF0, 1, // set color: 1
                    0xF9, 2, 44, 45, // set pattern: 2, plot at 44,45
                    0xFF, // end
                ];

                for (int i = 0; i < 12; i++)
                {
                    int color = _state.Frame + i;
                    if (color > 15)
                    {
                        color -= 15;
                    }

                    // FIXME (ScummVM): this is not the correct animation pattern. The lights do
                    // not simply advance in a sequence from left to right in the original.
                    lightPicture[1] = (byte)color; // change light color
                    lightPicture[4] += 7; // increase x coordinate

                    _picture.MaxStep = 0;
                    _picture.SetOffset(PicX0, PicY0);
                    _picture.DecodePictureFromBuffer(lightPicture, lightPicture.Length, false, PicWidth, PicHeight);
                }

                _picture.ShowPicture(PicX0, PicY0, PicWidth, PicHeight);

                _state.Frame--;
                if (_state.Frame < 0)
                {
                    _state.Frame = 15;
                }

                // play the spaceship beep but don't process events during playback. This sound
                // plays during menu usage, so events must not be consumed while waiting or else
                // inputs will be dropped. Playing this sound does create an input lag, but that
                // is what happened in the original.
                await PlaySoundAsync(SndPressBlue, WaitOptions.Block);
                break;
            }

            case PicShipControls:
                // draw XL30 screen
                if (_state.AnimXl30)
                {
                    if (_state.Frame > 5)
                    {
                        _state.Frame = 0;
                    }

                    DrawObj(ObjectXl31 + _state.Frame, 0, 4);
                    _state.Frame++;
                }

                break;

            default:
                // draw crystal
                if (_state.Room == XtalRoomXY[_state.Planet, 0] && IsCrystalOnCurrentPlanet())
                {
                    if (_state.Planet == PlanetVenus && _state.RmMenu[_state.Room] != 2)
                    {
                        break;
                    }

                    DrawObj(ObjectCrystal, XtalRoomXY[_state.Planet, 1], XtalRoomXY[_state.Planet, 2]);
                }

                break;
        }
    }

    private async Task DrawRoomAsync()
    {
        // draw room picture
        if (_state.Room == PicTitle)
        {
            DrawPic(PicTitle);
        }
        else
        {
            DrawPic(_state.RmPic[_state.Room]);

            if (_state.Room == PicShipControls)
            {
                // draw ship control room window
                if (_state.Flying)
                {
                    DrawObj(ObjectWSpace, 0, 0);
                }
                else
                {
                    DrawObj(ObjectWEarth + _state.Planet, 0, 1);
                }
            }
        }

        // draw room objects
        if (_state.Room < MaxRoom && _state.RmObj[_state.Room] != ObjectNone)
        {
            var buffer = ReadOfsData(MsgRoomObjectXYOffsets, _state.RmObj[_state.Room], 512);
            int pBuf = 0;
            int objs = buffer[pBuf++];
            for (int obj = 0; obj < objs && pBuf + 2 < buffer.Length; obj++)
            {
                int index = buffer[pBuf];
                if (index < ObjectNames.Length)
                {
                    DrawObj(index, buffer[pBuf + 1], buffer[pBuf + 2]);
                }

                pBuf += 3;
            }
        }

        // draw room animation
        await DrawRoomAnimationAsync();
    }

    /// <summary>Shows the Sierra logo, uncompressed CGA 4-color data (4 pixels per byte). The original does not show it; we do if available.</summary>
    private bool DrawLogo()
    {
        const int width = 80;
        const int height = 85 * 2;

        // straight mapping in CGA mode, CGA colors to EGA otherwise
        byte[] colorMapping = Gfx.RenderMode == AgiRenderMode.Cga ? [0, 1, 2, 3] : [0, 11, 13, 15];

        if (!Game.Exists(PathLogo))
        {
            Host.Log.Warning("agi.preagi", $"{PathLogo}: file not found");
            return false;
        }

        var fileBuffer = Game.ReadAllBytes(PathLogo);
        if (fileBuffer.Length < width * height / 4)
        {
            Host.Log.Warning("agi.preagi", $"{PathLogo}: truncated file: {fileBuffer.Length}");
            return false;
        }

        int p = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte curByte = fileBuffer[p++];
                Gfx.PutPixelOnDisplay(x * 4 + 0, y, colorMapping[(curByte >> 6) & 0x03]);
                Gfx.PutPixelOnDisplay(x * 4 + 1, y, colorMapping[(curByte >> 4) & 0x03]);
                Gfx.PutPixelOnDisplay(x * 4 + 2, y, colorMapping[(curByte >> 2) & 0x03]);
                Gfx.PutPixelOnDisplay(x * 4 + 3, y, colorMapping[curByte & 0x03]);
            }
        }

        Gfx.CopyDisplayToScreen();
        return true;
    }

    private async Task AnimateAsync()
    {
        await WaitAsync(ActionAnimateMs, WaitOptions.Block);
        await DrawRoomAnimationAsync();
    }

    private async Task PrintRoomDescAsync()
    {
        // print room description
        PrintDesc(_state.Room);
        await WaitAnyKeyAsync(true);

        // print extended room description
        if (_state.RmTxt[_state.Room] != 0)
        {
            await PrintExeMsgAsync(_state.RmTxt[_state.Room] + OfsExe);
        }
    }

    // ---- saved games ----

    private static string SaveFileName(int slot) => $"mickey.s{slot:00}";

    private async Task<bool> LoadGameAsync()
    {
        while (!ShouldQuit)
        {
            int sel = await Choose1To9Async(LoadGameMsg[1]);
            if (sel == 0)
            {
                return false;
            }

            byte[]? data = null;
            try
            {
                using var stream = Host.Saves.OpenRead(SaveFileName(sel));
                if (stream is not null)
                {
                    using var memory = new MemoryStream();
                    stream.CopyTo(memory);
                    data = memory.ToArray();
                }
            }
            catch (IOException)
            {
                data = null;
            }

            if (data is null)
            {
                await PrintLineAsync("PLEASE CHECK THE DISK DRIVE");
                if (await GetSelectionAsync(SelectionType.AnyKey) == 0)
                {
                    return false;
                }

                continue;
            }

            if (!ReadSaveGame(data))
            {
                return false;
            }

            break;
        }

        await PrintExeMsgAsync(LoadGameMsg[2]);
        return true;
    }

    internal bool ReadSaveGame(byte[] data)
    {
        if (data.Length < 5 || data[0] != 'M' || data[1] != 'I' || data[2] != 'C' || data[3] != 'K')
        {
            Host.Log.Warning("agi.preagi", "loadGame: wrong save game format");
            return false;
        }

        if (data[4] != SaveGameVersion)
        {
            Host.Log.Warning("agi.preagi", $"loadGame: unknown save version: {data[4]}");
            return false;
        }

        using var reader = new BinaryReader(new MemoryStream(data, 5, data.Length - 5));
        try
        {
            _state.Room = reader.ReadByte();
            _state.Planet = reader.ReadByte();
            _state.Disk = reader.ReadByte();
            _state.Air = reader.ReadByte();
            _state.Buttons = reader.ReadByte();
            _state.Rocks = reader.ReadByte();
            _state.Xtals = reader.ReadByte();
            for (int i = 0; i < MaxDat; i++)
            {
                _state.PlanetXtal[i] = reader.ReadByte();
            }

            for (int i = 0; i < MaxPlanet; i++)
            {
                _state.Clue[i] = reader.ReadUInt16();
            }

            for (int i = 0; i < MaxButton + 1; i++)
            {
                _state.Addr[i] = (char)reader.ReadByte();
            }

            _state.HasXtal = reader.ReadByte() == 1;
            _state.Intro = reader.ReadByte() == 1;
            _state.Suit = reader.ReadByte() == 1;
            _state.ShipDoorOpen = reader.ReadByte() == 1;
            _state.Flying = reader.ReadByte() == 1;
            _state.StoryShown = reader.ReadByte() == 1;
            _state.PlanetsInitialized = reader.ReadByte() == 1;
            _state.TempleDoorOpen = reader.ReadByte() == 1;
            _state.AnimXl30 = reader.ReadByte() == 1;

            for (int i = 0; i < MaxItem; i++)
            {
                _state.Item[i] = reader.ReadByte() == 1;
            }

            for (int i = 0; i < MaxItem; i++)
            {
                _state.ItemUsed[i] = reader.ReadByte() == 1;
            }

            for (int i = 0; i < MaxItem; i++)
            {
                _state.Items[i] = reader.ReadSByte();
            }

            _state.ItemCount = reader.ReadByte();

            for (int i = 0; i < MaxRoom; i++)
            {
                _state.RmObj[i] = reader.ReadSByte();
            }

            for (int i = 0; i < MaxRoom; i++)
            {
                _state.RmPic[i] = reader.ReadByte();
            }

            for (int i = 0; i < MaxRoom; i++)
            {
                _state.RmTxt[i] = reader.ReadUInt16();
            }

            for (int i = 0; i < MaxRoom; i++)
            {
                _state.RmMenu[i] = reader.ReadByte();
            }

            for (int i = 0; i < MaxRoom; i++)
            {
                _state.RmMenuCount[i] = reader.ReadByte();
            }

            _state.Frame = reader.ReadSByte();
        }
        catch (EndOfStreamException)
        {
            Host.Log.Warning("agi.preagi", "loadGame: save game is truncated");
            return false;
        }

        return true;
    }

    private async Task SaveGameAsync()
    {
        bool oldDisk = await ChooseYesNoAsync(SaveGameMsg[0], false);

        PrintExeStr(oldDisk ? SaveGameMsg[1] : SaveGameMsg[2]);
        if (await GetSelectionAsync(SelectionType.AnyKey) == 0)
        {
            return;
        }

        while (!ShouldQuit)
        {
            int sel = await Choose1To9Async(SaveGameMsg[3]);
            if (sel == 0)
            {
                return;
            }

            PrintExeStr(oldDisk ? SaveGameMsg[5] : SaveGameMsg[4]);
            if (await GetSelectionAsync(SelectionType.AnyKey) == 0)
            {
                return;
            }

            try
            {
                using var stream = Host.Saves.OpenWrite(SaveFileName(sel));
                WriteSaveGame(stream);
                break;
            }
            catch (IOException ex)
            {
                Host.Log.Warning("agi.preagi", $"Can't write file '{SaveFileName(sel)}': {ex.Message}");
                await PrintLineAsync("PLEASE CHECK THE DISK DRIVE");
                if (await GetSelectionAsync(SelectionType.AnyKey) == 0)
                {
                    return;
                }
            }
        }

        await PrintExeMsgAsync(SaveGameMsg[6]);
    }

    internal void WriteSaveGame(Stream stream)
    {
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)'M');
        writer.Write((byte)'I');
        writer.Write((byte)'C');
        writer.Write((byte)'K');
        writer.Write((byte)SaveGameVersion);

        writer.Write(_state.Room);
        writer.Write(_state.Planet);
        writer.Write(_state.Disk);
        writer.Write(_state.Air);
        writer.Write(_state.Buttons);
        writer.Write(_state.Rocks);
        writer.Write(_state.Xtals);
        writer.Write(_state.PlanetXtal);
        foreach (ushort clue in _state.Clue)
        {
            writer.Write(clue);
        }

        foreach (char c in _state.Addr)
        {
            writer.Write((byte)c);
        }

        writer.Write((byte)(_state.HasXtal ? 1 : 0));
        writer.Write((byte)(_state.Intro ? 1 : 0));
        writer.Write((byte)(_state.Suit ? 1 : 0));
        writer.Write((byte)(_state.ShipDoorOpen ? 1 : 0));
        writer.Write((byte)(_state.Flying ? 1 : 0));
        writer.Write((byte)(_state.StoryShown ? 1 : 0));
        writer.Write((byte)(_state.PlanetsInitialized ? 1 : 0));
        writer.Write((byte)(_state.TempleDoorOpen ? 1 : 0));
        writer.Write((byte)(_state.AnimXl30 ? 1 : 0));

        foreach (bool item in _state.Item)
        {
            writer.Write((byte)(item ? 1 : 0));
        }

        foreach (bool used in _state.ItemUsed)
        {
            writer.Write((byte)(used ? 1 : 0));
        }

        foreach (sbyte item in _state.Items)
        {
            writer.Write(item);
        }

        writer.Write(_state.ItemCount);

        foreach (sbyte obj in _state.RmObj)
        {
            writer.Write(obj);
        }

        writer.Write(_state.RmPic);

        foreach (ushort txt in _state.RmTxt)
        {
            writer.Write(txt);
        }

        writer.Write(_state.RmMenu);
        writer.Write(_state.RmMenuCount);
        writer.Write(_state.Frame);
    }

    // ---- game logic ----

    private async Task ShowPlanetInfoAsync()
    {
        for (int i = 0; i < 4; i++)
        {
            await PrintExeMsgAsync(PlanetInfo[_state.Planet, i]);
        }
    }

    private async Task PrintStoryAsync()
    {
        var buffer = ReadExe(MsgGameStory, LenStory);
        int pBuf = 0;

        ClearScreen(DefaultAttribute);
        for (int row = 0; row < 25; row++)
        {
            string line = ReadCString(buffer, pBuf);
            DrawStr(row, 0, DefaultAttribute, line.Length > 40 ? line[..40] : line);
            pBuf += line.Length + 1;
        }

        await WaitAnyKeyAsync();

        ClearScreen(DefaultAttribute);
        for (int row = 0; row < 21; row++)
        {
            string line = ReadCString(buffer, pBuf);
            DrawStr(row, 0, DefaultAttribute, line.Length > 40 ? line[..40] : line);
            pBuf += line.Length + 1;
        }

        await WaitAnyKeyAsync();

        // set back to black
        Gfx.ClearDisplay(0);

        await DrawRoomAsync();

        _state.StoryShown = true;
    }

    private int GetPlanet()
    {
        if (_state.Buttons == 0)
        {
            return -1;
        }

        string addr = _state.AddrString;
        for (int planet = 0; planet < MaxDat - 1; planet++)
        {
            if (PlanetAddresses[planet] == addr)
            {
                return planet;
            }
        }

        return -1;
    }

    private async Task PressOBAsync(char button)
    {
        // check if too many buttons pressed
        if (_state.Buttons == MaxButton)
        {
            _state.Buttons = 0;
            _state.ClearAddr();
            await PrintExeMsgAsync(MsgTooManyButtonsPressed);
            return;
        }

        // add button press to address
        _state.Buttons++;
        _state.Addr[_state.Buttons - 1] = button;

        // format buttons string
        var buttons = new char[12];
        for (int i = 0; i < MaxButton; i++)
        {
            buttons[i * 2] = _state.Addr[i];
            if (_state.Addr[i + 1] != '\0')
            {
                buttons[i * 2 + 1] = ',';
            }
        }

        // print pressed buttons
        PrintExeStr(MsgMickeyHasPressed);
        DrawStr(20, 22, DefaultAttribute, new string(buttons).TrimEnd('\0'));
        await WaitAnyKeyAsync();
    }

    private async Task InsertDiskAsync(int disk)
    {
        ClearTextArea();
        DrawStr(RowInsertDisk, ColInsertDisk, DefaultAttribute, InsertDisk[disk]);
        await WaitAnyKeyAsync();
    }

    private async Task GameOverAsync()
    {
        DrawPic(PicEarthShipLeaving);
        await PrintExeMsgAsync(GameOverMsg[3]);
        await PlaySoundAsync(SndGameOver);

        if (_state.ItemUsed[ItemLetter])
        {
            DrawPic(PicEarthMinnie);
            await PrintExeMsgAsync(GameOverMsg[4]);
            await PrintExeMsgAsync(GameOverMsg[5]);
        }
        else
        {
            await PrintExeMsgAsync(GameOverMsg[6]);
            await PrintExeMsgAsync(GameOverMsg[7]);
        }

        _isGameOver = true;
    }

    private bool PlanetIsAlreadyAssigned(int planet)
    {
        for (int j = 0; j < MaxPlanet; j++)
        {
            if (_state.PlanetXtal[j] == planet)
            {
                return true;
            }
        }

        return false;
    }

    private async Task FlipSwitchAsync()
    {
        if (_state.Xtals == 0)
        {
            await PrintStoryAsync();
            return;
        }

        if (!_state.StoryShown)
        {
            await PrintStoryAsync();
        }

        // initialize planet data
        if (!_state.PlanetsInitialized)
        {
            Array.Clear(_state.PlanetXtal);
            Array.Clear(_state.Clue);

            _state.PlanetXtal[0] = PlanetEarth;
            _state.PlanetXtal[8] = PlanetUranus;

            for (int i = 1; i < MaxPlanet; i++)
            {
                int planet;
                if (i < 8)
                {
                    do
                    {
                        // Earth (planet 0) and Uranus (planet 8) are excluded
                        planet = Rnd(MaxPlanet - 2);
                    }
                    while (PlanetIsAlreadyAssigned(planet));
                }
                else
                {
                    planet = PlanetUranus; // Uranus is always last
                }

                _state.PlanetXtal[i] = (byte)planet;
                int hint = Rnd(5) - 1; // clues are 0-4
                _state.Clue[i] = (ushort)NextPiece[planet, hint];
            }

            _state.PlanetsInitialized = true;
        }

        // activate screen animation
        _state.AnimXl30 = true;

        ClearTextArea();
        await PlaySoundAsync(SndXl30);
        await PrintExeMsgAsync(MsgXl30Speaking);

        if (_state.HasXtal)
        {
            _state.HasXtal = false;
            await PrintExeMsgAsync(MsgCrystalPieceFound);
        }

        if (_state.Xtals == MaxPlanet)
        {
            await PrintExeMsgAsync(GameOverMsg[0]);
            await PrintExeMsgAsync(GameOverMsg[1]);
            await PrintExeMsgAsync(GameOverMsg[2]);
        }
        else
        {
            PrintExeStr(_state.Clue[_state.Xtals]);
            await WaitAnyKeyAsync(true);
        }
    }

    private async Task InventoryAsync()
    {
        int row = RowInvItems;
        string crystals = string.Format(CrystalsFormat, CrystalNo[Math.Min((int)_state.Xtals, 9)]);

        ClearScreen(DefaultAttribute);
        DrawStr(RowInvTitle, ColInvTitle, DefaultAttribute, Inventory);
        DrawStr(RowInvCrystals, ColInvItems, DefaultAttribute, crystals);

        for (int item = 0; item < MaxItem; item++)
        {
            if (_state.Items[item] != ObjectNone && _state.Item[_state.Items[item]])
            {
                DrawStr(row++, ColInvItems, DefaultAttribute, ItemNames[_state.Items[item]]);
            }
        }

        await WaitAnyKeyAsync();

        ClearScreen(DefaultAttribute);
    }

    private async Task IntroAsync()
    {
        // draw Sierra logo
        if (DrawLogo())
        {
            // not in the original, but needed so that the logo is visible
            await WaitAnyKeyAsync();
            Gfx.ClearDisplay(0); // the logo is larger than the picture area, clear the entire screen
        }

        // draw title picture
        _state.Room = PicTitle;
        await DrawRoomAsync();

        // show copyright
        await PrintExeMsgAsync(MsgCopyright);

        if (ShouldQuit)
        {
            return;
        }

        // play theme
        await PlaySoundAsync(SndTheme, WaitOptions.AllowInterrupt);

        // load game
        _state.Intro = true;
        if (await ChooseYesNoAsync(LoadGameMsg[0], true))
        {
            if (await LoadGameAsync())
            {
                _state.Intro = false;
                return;
            }
        }

        if (ShouldQuit)
        {
            return;
        }

        // play spaceship landing scene
        _state.Planet = PlanetEarth;
        _state.Room = PicEarthRoad4;

        await DrawRoomAsync();
        await PrintRoomDescAsync();

        if (ShouldQuit)
        {
            return;
        }

        await PlaySoundAsync(SndShipLand);

        // flash screen 3 times
        for (int i = 0; i < 3; i++)
        {
            await PlaySoundAsync(SndPressBlue);

            // set screen to white
            Gfx.ClearDisplay(GetWhite());
            await WaitAsync(AnimDelay, WaitOptions.Block);

            // set back to black
            Gfx.ClearDisplay(0);

            await DrawRoomAsync();
            PrintDesc(_state.Room);
        }

        await PrintExeMsgAsync(MsgIntro);
    }

    private async Task GetItemAsync(int item)
    {
        _state.Item[item] = true;
        _state.Items[_state.ItemCount++] = (sbyte)item;
        _state.RmTxt[_state.Room] = 0;
        await PlaySoundAsync(SndTake);
        await DrawRoomAsync();
    }

    private async Task GetXtalAsync(int str)
    {
        _state.RmTxt[_state.Room] = 0;
        _state.HasXtal = true;
        _state.Xtals++;
        await PlaySoundAsync(SndCrystal);
        await DrawRoomAsync();
        await PrintDatMessageAsync(str);
    }

    private async Task<bool> MickeyHasItemAsync(int item)
    {
        if (_state.Item[item])
        {
            await PrintDatMessageAsync(90); // Mickey already has item
            return true;
        }

        return false;
    }

    private bool IsCrystalOnCurrentPlanet()
    {
        // Earth is a special case, because the planet list may not have been initialized yet.
        // Earth is always the first planet, so if no crystals have been gotten yet, then earth's
        // crystal must still be there.
        if (_state.Planet == PlanetEarth)
        {
            return _state.Xtals == 0;
        }

        if (_state.PlanetsInitialized)
        {
            for (int i = 1; i < MaxDat; i++)
            {
                if (_state.PlanetXtal[i] == _state.Planet)
                {
                    return _state.Xtals <= i;
                }
            }
        }

        return false;
    }

    /// <summary>Carries out a menu command. Returns true when the room changes.</summary>
    private async Task<bool> ParseAsync(int cmd, int arg)
    {
        int room = _state.Room;
        switch (cmd)
        {
            // BASIC

            case ActionGotoRoom:
                _state.Room = (byte)arg;
                return true;
            case ActionShowIntStr:
                await PrintLineAsync(Errors[Math.Min(arg, Errors.Length - 1)]);
                break;
            case ActionShowDatStr:
                await PrintDatMessageAsync(arg);
                break;

            // GENERAL

            case ActionPlanetInfo:
                await ShowPlanetInfoAsync();
                break;
            case ActionSaveGame:
                await SaveGameAsync();
                break;
            case ActionLookMickey:
                await PrintLineAsync("YOU CAN SEE MICKEY ALREADY");
                break;

            // EARTH

            case ActionGetRope:
                if (_state.RmMenu[room] == 2)
                {
                    _state.RmObj[room] = ObjectNone;
                    _state.RmMenu[room] = 3;
                    await GetItemAsync(ItemRope);
                    await PrintLineAsync("MICKEY TAKES THE ROPE");
                }
                else
                {
                    _state.RmMenu[room] = 1;
                    await PrintDatMessageAsync(11);
                }

                break;
            case ActionUntieRope:
                _state.RmPic[room] = PicEarthTireSwing1;
                _state.RmObj[room] = 0;
                _state.RmMenu[room] = 2;
                await DrawRoomAsync();
                await PrintDatMessageAsync(12);
                break;
            case ActionGetBone:
                _state.RmObj[room] = ObjectNone;
                _state.RmMenu[room] = 1;
                await GetItemAsync(ItemBone);
                await PrintDatMessageAsync(arg);
                break;
            case ActionGetXtalEarth:
                _state.RmMenu[room] = 1;
                await GetXtalAsync(arg);
                break;
            case ActionLookDesk:
                _state.RmMenu[room] = 1;
                _state.RmObj[room] = 2;
                await DrawRoomAsync();
                await PrintDatMessageAsync(arg);
                break;
            case ActionWriteLetter:
                _state.RmMenu[room] = 3;
                _state.RmMenu[PicEarthMailbox] = 1;
                _state.RmObj[room] = ObjectNone;
                await GetItemAsync(ItemLetter);
                await PrintDatMessageAsync(arg);
                break;
            case ActionMailLetter:
                _state.ItemUsed[ItemLetter] = true;
                _state.Item[ItemLetter] = false;
                _state.RmMenu[room] = 0;
                await PrintDatMessageAsync(arg);
                break;
            case ActionOpenMailbox:
                await PrintDatMessageAsync(_state.ItemUsed[ItemLetter] ? 110 : arg);
                break;
            case ActionOpenCupboard:
                if (_state.RmMenu[room] != 0)
                {
                    await PrintDatMessageAsync(_state.RmObj[room] == ObjectNone ? 78 : arg);
                }
                else
                {
                    _state.RmMenu[room] = 1;
                    _state.RmPic[room] = PicEarthKitchen1;
                    _state.RmObj[room] = 3;
                    await DrawRoomAsync();
                    await PrintDatMessageAsync(arg);
                }

                break;
            case ActionGetFlashlight:
                if (!await MickeyHasItemAsync(ItemFlashlight))
                {
                    _state.RmObj[room] = ObjectNone;
                    await GetItemAsync(ItemFlashlight);
                    await DrawRoomAsync();
                    await PrintDatMessageAsync(arg);
                }

                break;
            case ActionOpenCabinet:
                if (_state.RmMenu[room] != 0)
                {
                    await PrintDatMessageAsync(109);
                }
                else
                {
                    _state.RmMenu[room] = 1;
                    _state.RmPic[room] = PicEarthGarage1;
                    _state.RmObj[room] = 15;
                    await DrawRoomAsync();
                    await PrintDatMessageAsync(arg);
                }

                break;
            case ActionGetCrowbar:
                if (!await MickeyHasItemAsync(ItemCrowbar))
                {
                    _state.RmObj[room]--;
                    await GetItemAsync(ItemCrowbar);
                    await PrintDatMessageAsync(arg);
                }

                break;
            case ActionGetWrench:
                if (!await MickeyHasItemAsync(ItemWrench))
                {
                    _state.RmObj[room] -= 2;
                    await GetItemAsync(ItemWrench);
                    await PrintDatMessageAsync(arg);
                }

                break;
            case ActionOpenCloset:
                if (_state.RmMenu[room] != 0)
                {
                    await PrintDatMessageAsync(99);
                }
                else
                {
                    _state.RmMenu[room] = 1;
                    _state.RmPic[room] = PicEarthBedroom1;
                    _state.RmObj[room] = 7;
                    await DrawRoomAsync();
                    await PrintDatMessageAsync(arg);
                }

                break;
            case ActionGetMattress:
                if (!await MickeyHasItemAsync(ItemMattress))
                {
                    _state.RmObj[room]--;
                    await GetItemAsync(ItemMattress);
                    await PrintDatMessageAsync(arg);
                }

                break;
            case ActionGetScarf:
                if (!await MickeyHasItemAsync(ItemScarf))
                {
                    _state.RmObj[room] -= 2;
                    await GetItemAsync(ItemScarf);
                    await PrintDatMessageAsync(arg);
                }

                break;
            case ActionGetSunglasses:
                if (!await MickeyHasItemAsync(ItemSunglasses))
                {
                    _state.RmObj[room]--;
                    await GetItemAsync(ItemSunglasses);
                    await PrintDatMessageAsync(arg);
                }

                break;
            case ActionGetScale:
                if (!await MickeyHasItemAsync(ItemScale))
                {
                    _state.RmMenu[PicVenusWeigh] = 1;
                    _state.RmMenu[PicNeptuneWeigh] = 1;
                    _state.RmMenu[PicMercuryWeigh] = 1;
                    _state.RmMenu[PicSaturnWeigh] = 1;
                    _state.RmMenu[PicPlutoWeigh] = 1;
                    _state.RmMenu[PicJupiterWeigh] = 1;
                    _state.RmMenu[PicMarsWeigh] = 1;
                    _state.RmMenu[PicUranusWeigh] = 1;
                    _state.RmObj[room] -= 2;
                    await GetItemAsync(ItemScale);
                    await PrintDatMessageAsync(arg);
                }

                break;
            case ActionGotoSpaceship:
                _state.Room = PicShipAirlock;
                if (_state.Planet != PlanetEarth)
                {
                    await InsertDiskAsync(0);
                }

                return true;

            // VENUS

            case ActionDownChasm:
                if (_state.Item[ItemRope])
                {
                    _state.RmMenu[room] = 1;
                }

                await PrintDatMessageAsync(arg);
                break;
            case ActionDownRope:
                if (_state.ItemUsed[ItemRope])
                {
                    _state.Room = PicVenusProbe;
                    return true;
                }

                await PrintDatMessageAsync(arg);
                break;
            case ActionUseRope:
                if (_state.ItemUsed[ItemRope])
                {
                    await PrintDatMessageAsync(22);
                }
                else
                {
                    _state.ItemUsed[ItemRope] = true;
                    _state.Item[ItemRope] = false;
                    _state.RmPic[room] = PicVenusChasm1;
                    await DrawRoomAsync();
                    await PrintDatMessageAsync(arg);
                }

                break;
            case ActionOpenHatch:
                if (_state.ItemUsed[ItemWrench])
                {
                    if (_state.RmMenu[room] == 3 || _state.RmPic[room] == PicVenusProbe1)
                    {
                        await PrintDatMessageAsync(39);
                    }
                    else
                    {
                        _state.RmMenu[room] = 2;
                        _state.RmPic[room] = PicVenusProbe1;
                        await DrawRoomAsync();
                        await PrintDatMessageAsync(24);
                    }
                }
                else
                {
                    if (_state.Item[ItemWrench])
                    {
                        _state.RmMenu[room] = 1;
                    }

                    await PrintDatMessageAsync(arg);
                }

                break;
            case ActionUseWrench:
                _state.ItemUsed[ItemWrench] = true;
                PrintDatString(arg);
                if (_state.RmPic[room] == PicVenusProbe1)
                {
                    ClearRow(22);
                }

                await WaitAnyKeyAsync();
                break;
            case ActionGetXtalVenus:
                _state.RmMenu[room] = 3;
                await GetXtalAsync(arg);
                break;

            // TRITON (NEPTUNE)

            case ActionLookCastle:
                if (_state.RmMenu[room] == 0)
                {
                    _state.RmMenu[room] = 1;
                }

                await PrintDatMessageAsync(arg);
                break;
            case ActionEnterOpening:
                if (_state.ItemUsed[ItemCrowbar])
                {
                    _state.Room = PicNeptuneCastle4;
                    return true;
                }

                if (_state.Item[ItemCrowbar])
                {
                    _state.RmMenu[room] = 2;
                }

                await PrintDatMessageAsync(arg);
                break;
            case ActionUseCrowbar:
                _state.ItemUsed[ItemCrowbar] = true;
                _state.RmMenu[room] = 1;
                _state.RmPic[room] = PicNeptuneEntrance1;
                await DrawRoomAsync();
                await PrintDatMessageAsync(arg);
                break;
            case ActionGetXtalNeptune:
                if (_state.HasXtal)
                {
                    await PrintDatMessageAsync(71);
                }
                else
                {
                    if (_state.Item[ItemScarf])
                    {
                        _state.RmMenu[room] = 1;
                    }

                    await PrintDatMessageAsync(arg);
                }

                break;
            case ActionTalkLeader:
                _state.Room = PicNeptuneEntryway;
                await PrintDatMessageAsync(arg);
                return true;
            case ActionGiveScarf:
                _state.RmObj[room] = 18;
                await GetXtalAsync(arg);
                _state.Item[ItemScarf] = false;
                _state.RmMenu[room] = 0;
                _state.RmMenu[PicEarthBedroom] = 2;
                _state.Room = PicNeptuneEntryway;
                return true;

            // MERCURY

            case ActionGetXtalMercury:
                if (IsCrystalOnCurrentPlanet())
                {
                    if (_state.Item[ItemSunglasses])
                    {
                        _state.RmMenu[room] = 1;
                    }

                    await PrintDatMessageAsync(arg);
                }
                else
                {
                    _state.RmMenu[room] = 2;
                    await PrintDatMessageAsync(32);
                }

                break;
            case ActionGiveSunglasses:
                _state.RmObj[room] = 17;
                _state.RmMenu[room] = 2;
                _state.Item[ItemSunglasses] = false;
                await GetXtalAsync(arg);
                break;

            // TITAN (SATURN)

            case ActionCrossLake:
                if (_state.Item[ItemMattress])
                {
                    _state.RmMenu[PicSaturnLake0] = 1;
                    _state.RmMenu[PicSaturnLake1] = 1;
                    _state.RmMenu[PicSaturnLake2] = 1;
                }

                await PrintDatMessageAsync(arg);
                break;
            case ActionUseMattress:
                await PrintDatMessageAsync(arg);

                // must set room after the message, or else the crystal from the next room will
                // appear and animate while still displaying the picture for the current room
                _state.Room = PicSaturnIsland;
                return true;
            case ActionGetXtalSaturn:
                if (IsCrystalOnCurrentPlanet())
                {
                    await GetXtalAsync(arg);
                }
                else
                {
                    await PrintDatMessageAsync(29);
                }

                break;
            case ActionLeaveIsland:
                _state.Room = PicSaturnLake1;
                await PrintDatMessageAsync(arg);
                return true;

            // PLUTO

            case ActionGetXtalPluto:
                if (IsCrystalOnCurrentPlanet())
                {
                    if (_state.Item[ItemBone])
                    {
                        _state.RmMenu[room] = 1;
                    }

                    await PrintDatMessageAsync(arg);
                }
                else
                {
                    await PrintDatMessageAsync(19);
                }

                break;
            case ActionGiveBone:
                _state.Item[ItemBone] = false;
                _state.RmMenu[room] = 0;
                _state.RmObj[room] = 16;
                await GetXtalAsync(arg);
                break;

            // IO (JUPITER)

            case ActionGetRock0:
            case ActionGetRock1:
                if (_state.Item[ItemRock])
                {
                    await PrintDatMessageAsync(38);
                }
                else
                {
                    _state.RmMenu[room] = 1;
                    _state.RmObj[room] = ObjectNone;
                    await GetItemAsync(ItemRock);
                    await PrintDatMessageAsync(arg);
                }

                break;
            case ActionGetXtalJupiter:
                if (IsCrystalOnCurrentPlanet())
                {
                    switch (_state.Rocks)
                    {
                        case 0:
                            if (_state.Item[ItemRock])
                            {
                                _state.RmMenu[room] = 1;
                            }

                            await PrintDatMessageAsync(arg);
                            break;
                        case 1:
                            if (_state.Item[ItemRock])
                            {
                                _state.RmMenu[room] = 1;
                            }

                            await PrintDatMessageAsync(34);
                            break;
                        case 2:
                            await GetXtalAsync(35);
                            break;
                    }
                }
                else
                {
                    await PrintDatMessageAsync(15);
                }

                break;
            case ActionThrowRock:
                _state.Item[ItemRock] = false;
                _state.ItemCount--;
                _state.RmObj[room]++;
                _state.RmMenu[room] = 0;
                await DrawRoomAsync();
                await PrintDatMessageAsync(_state.Rocks != 0 ? 37 : arg);
                _state.Rocks++;
                break;

            // MARS

            case ActionGoTube:
                if (_state.Item[ItemFlashlight])
                {
                    _state.RmMenu[room] = 1;
                }

                await PrintDatMessageAsync(arg);
                break;
            case ActionUseFlashlight:
                _state.Room = PicMarsTube1;
                await PrintDatMessageAsync(15);
                return true;
            case ActionPlutoDig:
                if (IsCrystalOnCurrentPlanet())
                {
                    await GetXtalAsync(arg);
                }
                else
                {
                    await PrintDatMessageAsync(21);
                }

                break;
            case ActionGetXtalMars:
                await PrintDatMessageAsync(IsCrystalOnCurrentPlanet() ? arg : 23);
                break;

            // OBERON (URANUS)

            case ActionEnterTemple:
                _state.Room = PicUranusTemple;
                return true;
            case ActionUseCrystal:
                if (_state.RmMenu[room] != 0)
                {
                    await PrintDatMessageAsync(25);
                }
                else
                {
                    _state.RmMenu[room] = 1;
                    _state.RmPic[room] = PicUranusTemple1;
                    await DrawRoomAsync();
                    _state.RmPic[room] = PicUranusTemple;
                    await PrintDatMessageAsync(arg);
                }

                break;
            case ActionOpenDoor:
                if (_state.TempleDoorOpen)
                {
                    await PrintDatMessageAsync(36);
                }
                else
                {
                    _state.TempleDoorOpen = true;
                    _state.RmPic[room] = PicUranusTemple2;
                    await DrawRoomAsync();
                    await PrintDatMessageAsync(arg);
                }

                break;
            case ActionEnterDoor:
                if (_state.TempleDoorOpen)
                {
                    _state.Room = PicUranusSteps;
                    return true;
                }

                await PrintDatMessageAsync(arg);
                break;
            case ActionGetXtalUranus:
                if (IsCrystalOnCurrentPlanet())
                {
                    if (_state.Item[ItemCrowbar])
                    {
                        _state.RmMenu[room] = 1;
                    }

                    await PrintDatMessageAsync(arg);
                }
                else
                {
                    await PrintDatMessageAsync(34);
                }

                break;
            case ActionUseCrowbar1:
                _state.RmMenu[room] = 0;
                await GetXtalAsync(arg);
                break;

            // SPACESHIP

            case ActionGoNorth:
                if (_state.ShipDoorOpen)
                {
                    if (_state.Suit)
                    {
                        await PrintDatMessageAsync(45);
                    }
                    else
                    {
                        _state.Room = PicShipCorridor;
                        return true;
                    }
                }
                else
                {
                    await PrintDatMessageAsync(arg);
                }

                break;
            case ActionGoPlanet:
                if (!_state.ShipDoorOpen)
                {
                    if (_state.Xtals == MaxPlanet && _state.Planet == PlanetEarth)
                    {
                        await GameOverAsync();
                        return true;
                    }

                    if (_state.Planet == _state.PlanetXtal[_state.Xtals] || _state.Planet == PlanetEarth)
                    {
                        _state.Room = (byte)HomePlanet[_state.Planet];
                        if (_state.Planet != PlanetEarth)
                        {
                            await InsertDiskAsync(1);
                        }

                        return true;
                    }

                    _state.Room = (byte)ShipPlanet[_state.Planet];
                    return true;
                }

                await PrintDatMessageAsync(arg);
                break;
            case ActionPressButton:
                if (_state.ShipDoorOpen)
                {
                    // inner door open
                    if (_state.Planet != 0 && !_state.Suit)
                    {
                        await PrintDatMessageAsync(arg);
                    }
                    else
                    {
                        _state.ShipDoorOpen = false;
                        _state.RmPic[room]--;
                        await DrawRoomAsync();
                        await PrintDatMessageAsync(2);
                    }
                }
                else
                {
                    _state.ShipDoorOpen = true;
                    _state.RmPic[room]++;
                    await DrawRoomAsync();
                    await PrintDatMessageAsync(14);
                }

                break;
            case ActionWearSpacesuit:
                if (_state.Suit)
                {
                    if (_state.ShipDoorOpen)
                    {
                        _state.Suit = false;
                        _state.RmMenu[room] = 0;
                        _state.RmPic[room] -= 2;
                        await DrawRoomAsync();
                        await PrintDatMessageAsync(13);
                    }
                    else
                    {
                        await PrintDatMessageAsync(3);
                    }
                }
                else
                {
                    if (_state.Planet != 0)
                    {
                        _state.Suit = true;
                        _state.RmMenu[room] = 1;
                        _state.RmPic[room] += 2;
                        await DrawRoomAsync();
                        await PrintDatMessageAsync(arg);
                    }
                    else
                    {
                        await PrintDatMessageAsync(12);
                    }
                }

                break;
            case ActionReadGauge:
                PrintDatString(arg);
                DrawStr(21, 15, DefaultAttribute, TempC[_state.Planet]);
                DrawStr(21, 23, DefaultAttribute, TempF[_state.Planet]);
                await WaitAnyKeyAsync();
                break;
            case ActionPressOrange:
                if (_state.Flying)
                {
                    await PrintDatMessageAsync(4);
                }
                else
                {
                    await PlaySoundAsync(SndPressOrange);
                    await PrintDatMessageAsync(arg);
                    await PressOBAsync(ButtonOrange);
                }

                break;
            case ActionPressBlue:
                if (_state.Flying)
                {
                    await PrintDatMessageAsync(4);
                }
                else
                {
                    await PlaySoundAsync(SndPressBlue);
                    await PrintDatMessageAsync(arg);
                    await PressOBAsync(ButtonBlue);
                }

                break;
            case ActionFlipSwitch:
                await FlipSwitchAsync();
                break;
            case ActionPushThrottle:
                if (_state.Flying)
                {
                    _state.Flying = false;
                    _state.Buttons = 0;
                    _state.ClearAddr();
                    await DrawRoomAsync();
                    PrintDatString(22);
                    DrawStr(RowPlanet, ColPlanet, DefaultAttribute, Planets[_state.Planet]);
                    await WaitAnyKeyAsync(true);
                    await ShowPlanetInfoAsync();
                }
                else
                {
                    await PrintDatMessageAsync(arg);
                }

                break;
            case ActionPullThrottle:
                if (_state.Flying)
                {
                    await PrintDatMessageAsync(18);
                }
                else if (GetPlanet() != -1)
                {
                    _state.Flying = true;
                    _state.Planet = (byte)GetPlanet();
                    await DrawRoomAsync();
                    await PrintDatMessageAsync(16);
                }
                else
                {
                    _state.Buttons = 0;
                    _state.ClearAddr();
                    await PrintDatMessageAsync(17);
                }

                break;
            case ActionLeaveRoom:
                if (_state.Flying)
                {
                    await PrintDatMessageAsync(24);
                }
                else
                {
                    _state.Room = (byte)arg;
                    return true;
                }

                break;
            case ActionOpenCabinet1:
                if (_state.RmMenu[room] != 0)
                {
                    await PrintLineAsync("THE CABINET IS ALREADY OPEN");
                }
                else
                {
                    _state.RmMenu[room] = 1;
                    _state.RmPic[room] = PicShipKitchen1;
                    await DrawRoomAsync();
                    await PrintDatMessageAsync(arg);
                }

                break;
            case ActionReadMap:
                _state.RmPic[room] = PicStarMap;
                await DrawRoomAsync();
                await PrintDatMessageAsync(46);
                await PrintDatMessageAsync(47);
                await PrintDatMessageAsync(48);
                _state.RmPic[room] = PicShipBedroom;
                await DrawRoomAsync();
                break;
            case ActionGoWest:
                _state.Buttons = 0;
                _state.ClearAddr();
                _state.Room = (byte)arg;
                return true;
        }

        return false;
    }

    // ---- keyboard ----

    /// <summary>Waits for a key or mouse button, animating the room meanwhile when asked.</summary>
    private async Task WaitAnyKeyAsync(bool anim = false)
    {
        AwaitingKey = true;
        try
        {
            await WaitAnyKeyCoreAsync(anim);
        }
        finally
        {
            AwaitingKey = false;
        }
    }

    private async Task WaitAnyKeyCoreAsync(bool anim)
    {
        while (!ShouldQuit)
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

                        return;
                    case InputEventType.Quit:
                    case InputEventType.MouseUp:
                        return;
                }
            }

            if (anim)
            {
                await AnimateAsync();
            }

            await Poll();
        }
    }

    // ---- the game ----

    private void Init()
    {
        _exe = TryReadFile(ExeName) ?? [];
        if (_exe.Length == 0)
        {
            throw new FileNotFoundException("mickey.exe holds the game's text and sounds and is required.", ExeName);
        }

        Array.Fill(_state.Items, (sbyte)ObjectNone);

        // read room extended desc offsets
        var buffer = ReadExe(MsgRoomTextOffsets, 512);
        for (int i = 0; i < MaxRoom; i++)
        {
            _state.RmTxt[i] = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(i * 2));
        }

        // read room menu patch indices
        buffer = ReadExe(MsgRoomMenuFix, 512);
        buffer.AsSpan(0, MaxRoom).CopyTo(_state.RmMenuCount);

        // set room picture and room object indices
        for (int i = 0; i < MaxRoom; i++)
        {
            _state.RmPic[i] = (byte)i;
            _state.RmObj[i] = -1;
        }

        _state.RmPic[PicShipAirlock] = PicShipAirlock0;
        _state.RmObj[PicEarthBathroom] = 11;
        _state.RmObj[PicJupiterLava] = 21;
        _state.RmObj[PicJupiterRock0] = 20;
        _state.RmObj[PicJupiterRock1] = 19;
        _state.RmObj[PicEarthInDoghouse] = 1;

        SoundOn = true;
    }

    protected override async Task RunAsync()
    {
        Init();

        // game intro
        await IntroAsync();

        // game loop
        while (!ShouldQuit && !_isGameOver)
        {
            await DrawRoomAsync();

            if (_state.Intro)
            {
                _state.Intro = false;
            }
            else
            {
                await PrintRoomDescAsync();
            }

            bool done;
            if (_state.Room == PicNeptuneGuard)
            {
                _state.Room = PicNeptuneLeader;
                done = true;
            }
            else
            {
                done = false;
            }

            while (!done && !ShouldQuit)
            {
                // check air supply
                if (_state.Suit)
                {
                    _state.Air -= 1;
                    for (int i = 0; i < 4; i++)
                    {
                        if (_state.Air == AirSupply[i])
                        {
                            await PlaySoundAsync(SndXl30);
                            await PrintExeMsgAsync(MsgXl30Speaking);
                            await PrintExeMsgAsync(AirSupplyMsg[i]);
                            if (i == 3)
                            {
                                return;
                            }
                        }
                    }
                }
                else
                {
                    _state.Air = 50; // max air supply
                }

                done = await CheckMenuAsync();
            }

            _state.Frame = 0;
        }
    }
}
