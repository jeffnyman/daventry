using System.Buffers.Binary;
using Daventry.Core.Hosting;
using Daventry.Core.Input;
using Daventry.Core.Storage;

namespace Daventry.Agi.PreAgi;

/// <summary>
/// Troll's Tale (1984). Port of ScummVM's TrollEngine.
/// <para>
/// The game is a menu of up to three choices per room: go somewhere, take a treasure or do
/// something. The evil Troll appears at random in most rooms and has to be avoided by changing
/// the picture; sixteen treasures are found and taken back to King Mark's guard. Everything comes
/// from the PC booter disk image, which ScummVM names troll.img; the DOS release's game.dat holds
/// the same image (see <see cref="AgiDetectedGame.DataFile"/>).
/// </para>
/// </summary>
public sealed class TrollEngine : PreAgiEngine
{
    // strings

    private const string PressAnyKeyText = "PRESS ANY KEY TO CONTINUE:";

    private const string Intro0 = "SIERRA ON-LINE INC.";
    private const string Intro1 = "Presents :";
    private const string Intro2 = "Copyright 1984 Sierra On-Line Inc.";
    private const string Intro3 = "    Press any key to continue.    ";
    private const string Intro4 = "HAVE YOU PLAYED THIS GAME BEFORE ?";
    private const string Intro5 = "PRESS <Y> OR <N>";

    private const string Tutorial0 = " First press the <space bar>.\n  1. Turn the screen GREEN.\n  2. Turn the screen BLACK.\n *3. SEE a SURPRISE, and then more.";
    private const string Tutorial1 = " Press <return> to make your choice.\n  1. Turn the screen GREEN.\n  2. Turn the screen BLACK.\n  3. SEE a SURPRISE, and then more.";
    private const string Tutorial5 = "Would you like more practice ?";
    private const string Tutorial6 = "Press <Y> for yes, <N> for no.";
    private const string Tutorial7 = "The evil TROLL has hidden all the";
    private const string Tutorial8 = "Treasures of MARK, the Dwarf King.";
    private const string Tutorial9 = "Help KING MARK find his Treasures.";
    private const string Tutorial10 = "You can't take a Treasure if the TROLL";
    private const string Tutorial11 = "is in the same picture as the Treasure.";
    private const string Tutorial12 = "To make the TROLL go away you have to";
    private const string Tutorial13 = "make the picture change.";
    private const string Tutorial14 = "During the game see the Treasures you";
    private const string Tutorial15 = "have already found by pressing <F>.";
    private const string Tutorial16 = "During the game you can turn the sound";
    private const string Tutorial17 = "on or off by pressing the <S> key ";
    private const string Tutorial18 = "while holding down the <Ctrl> key.";
    private const string Tutorial19 = "The TROLL has hidden these Treasures:";

    private const string Credits0 = "Prepare to enter the world of . . .";
    private const string Credits1 = "TROLL'S TALE (tm)";
    private const string Credits2 = "------------";
    private const string Credits3 = "Written by MIKE MACCHESNEY";
    private const string Credits4 = "Conversion by PETER OLIPHANT";
    private const string Credits5 = "Graphic Art by DOUG MACNEILL";
    private const string Credits6 = "Original Version by AL LOWE";

    private const string Treasure0 = "TREASURES FOUND";
    private const string Treasure1 = "---------------";
    private const string Treasure2 = "NONE";
    private const string Treasure3 = "THERE ARE STILL {0} TREASURES TO FIND";
    private const string Treasure4 = "{0} TREASURES TO FIND";
    private const string Treasure5 = "{0} TREASURE TO FIND";
    private const string Treasure6 = "YOU HAVE FOUND ALL OF THE TREASURES!!";
    private const string Treasure7 = "THERE'S ONLY ONE MORE TREASURE TO FIND.";
    private const string Treasure8 = "GREAT!! YOU HAVE FOUND EVERY TREASURE.";
    private const string Treasure9 = "TAKE THE TREASURES TO THE GUARD.";

    private const string GameOver0 = "You took {0} moves to complete TROLL'S";
    private const string GameOver1 = "TALE. Do you think you can do better?";

    // picture

    private const int PicNum = 47;
    private const int PicWidth = 160;
    private const int PicHeight = 168;

    // max values

    private const int MaxTreasure = 16;
    private const int MaxOption = 3;

    private const int SelOption1 = 0;
    private const int SelOption2 = 1;
    private const int SelOption3 = 2;

    private const int NumRoomDescs = 65;
    private const int NumOptions = 129;
    private const int NumNumRooms = 43;
    private const int NumUserMsgs = 34;
    private const int NumLocDescs = 59;
    private const int NumNonTroll = 9;

    // offsets

    /// <summary>The name ScummVM opens.</summary>
    public const string BinName = "troll.img";

    private const int OfsPicStart = 0x3EF5;
    private const int OfsLocMessages = 0x1F7C;
    private const int OfsUserMessages = 0x34A4;
    private const int OfsRoomDescs = 0x0082;
    private const int OfsOptions = 0x0364;
    private const int OfsPicStartIdx = 0x02CD;
    private const int OfsRoomPicDeltas = 0x030C;
    private const int OfsAllTreasures = 0x3B24;
    private const int OfsItems = 0x34E8;
    private const int OfsFramePic = 0x3EC2;
    private const int OfsRoomConnects = 0x02FA;
    private const int OfsNonTrollRooms = 0x3CF9;

    private const int MenuSize = 160 + 5;

    private enum OptionType
    {
        Go,
        Get,
        Do,
        Flashlight,
    }

    private sealed class RoomDesc
    {
        public int[] Options { get; } = new int[3];

        public OptionType[] OptionTypes { get; } = new OptionType[3];

        public int[] RoomDescIndex { get; } = new int[3];
    }

    private sealed class UserMsg
    {
        public int Num { get; set; }

        public string[] Msg { get; } = new string[3];
    }

    private sealed record Item(byte Bg, byte Fg, string Name);

    private readonly PictureMgrTroll _picture;
    private readonly string _dataFile;

    private int _roomPicture;
    private int _treasuresLeft;
    private int _currentRoom;
    private int _moves;
    private bool _isTrollAway;
    private readonly int[] _inventory = new int[MaxTreasure];
    private byte[] _gameData = [];

    // These come from game data
    private readonly int[] _pictureOffsets = new int[PicNum];
    private readonly int[] _roomPicStartIdx = new int[NumNumRooms];
    private readonly int[] _roomPicDeltas = new int[NumNumRooms];
    private readonly int[] _roomStates = new int[NumNumRooms];
    private readonly UserMsg[] _userMessages = new UserMsg[NumUserMsgs];
    private readonly int[] _locMessagesIdx = new int[NumLocDescs];
    private readonly RoomDesc[] _roomDescs = new RoomDesc[NumRoomDescs];
    private readonly int[] _options = new int[NumOptions];
    private readonly Item[] _items = new Item[MaxTreasure];
    private readonly int[] _roomConnects = new int[NumOptions];
    private readonly int[] _nonTrollRooms = new int[NumNonTroll];
    private readonly int[] _tunes = new int[6];

    public TrollEngine(IHost host, IGameContainer game, AgiDetectedGame detected)
        : base(host, game, detected)
    {
        _picture = new PictureMgrTroll(Gfx, host.Log);
        _dataFile = detected.DataFile ?? BinName;

        // ScummVM leaves this unset until the game starts; the tutorial's menu can open the
        // treasure list before then, which should say NONE
        _treasuresLeft = MaxTreasure;
    }

    /// <summary>The room the player is in; for tests.</summary>
    public int CurrentRoom => _currentRoom;

    /// <summary>Moves made so far; for tests.</summary>
    public int Moves => _moves;

    /// <summary>Treasures still hidden; for tests.</summary>
    public int TreasuresLeft => _treasuresLeft;

    /// <summary>Whether the Troll is absent from the current room; for tests.</summary>
    public bool IsTrollAway => _isTrollAway;

    /// <summary>Troll's pictures are 168 lines, a row taller than Mickey's and Winnie's, so its text starts a row lower.</summary>
    protected override int TextAreaStart => 21;

    protected override async Task RunAsync()
    {
        if (!Init())
        {
            QuitGame();
            return;
        }

        while (!ShouldQuit)
        {
            await IntroAsync();
            await GameLoopAsync();
            await GameOverAsync();
        }
    }

    // User Interface

    private async Task PressAnyKeyAsync(int col = 4)
    {
        DrawStr(24, col, ColorDefault, PressAnyKeyText);
        await GetSelectionAsync(SelectionType.AnyKey);
    }

    private void DrawMenu(string menu, int iSel)
    {
        ClearTextArea();
        DrawStr(21, 0, ColorDefault, menu);
        DrawStr(22 + iSel, 0, ColorDefault, " *");
    }

    private async Task<(bool Chosen, int Sel)> GetMenuSelAsync(string menu, int iSel, int nSel)
    {
        AwaitingMenuSelection = true;
        try
        {
            return await GetMenuSelCoreAsync(menu, iSel, nSel);
        }
        finally
        {
            AwaitingMenuSelection = false;
        }
    }

    private async Task<(bool Chosen, int Sel)> GetMenuSelCoreAsync(string menu, int iSel, int nSel)
    {
        DrawMenu(menu, iSel);

        while (!ShouldQuit)
        {
            while (TryDequeueEvent(out var ev))
            {
                switch (ev.Type)
                {
                    case InputEventType.Quit:
                        return (false, iSel);
                    case InputEventType.MouseMove:
                    {
                        int y = ev.Y / 8;
                        if (y >= 22 && nSel > y - 22)
                        {
                            iSel = y - 22;
                        }

                        DrawMenu(menu, iSel);
                        break;
                    }

                    case InputEventType.MouseUp when ev.Button == MouseButton.Left:
                        return (true, iSel);
                    case InputEventType.KeyDown:
                        switch (ev.Key)
                        {
                            case Key.T:
                            case Key.F:
                                await InventoryAsync();
                                return (false, iSel);
                            case Key.Down:
                            case Key.Space:
                                iSel += 1;
                                if (iSel == nSel)
                                {
                                    iSel = SelOption1;
                                }

                                DrawMenu(menu, iSel);
                                break;
                            case Key.Up:
                                iSel -= 1;
                                if (iSel == SelOption1 - 1)
                                {
                                    iSel = nSel - 1;
                                }

                                DrawMenu(menu, iSel);
                                break;
                            case Key.Return:
                            case Key.KeypadEnter:
                                return (true, iSel);
                            case Key.S:
                                if ((ev.Modifiers & KeyModifiers.Ctrl) != 0)
                                {
                                    if (SoundOn)
                                    {
                                        await PlayTuneAsync(2, 1);
                                        SoundOn = !SoundOn;
                                    }
                                    else
                                    {
                                        SoundOn = !SoundOn;
                                        await PlayTuneAsync(3, 1);
                                    }
                                }

                                break;
                        }

                        break;
                }
            }

            await Poll();
        }

        return (true, iSel);
    }

    // Graphics

    private void DrawPic(int iPic, bool f3IsCont, bool clear, bool troll = false)
    {
        if (clear)
        {
            ClearScreen(0x0F, false);
        }

        // draw the frame picture
        _picture.StopOnF3 = false;
        _picture.TrollMode = false;
        _picture.DecodePictureFromBuffer(_gameData[OfsFramePic..], 4096, clear, PicWidth, PicHeight);

        // draw the picture
        _picture.StopOnF3 = !f3IsCont;
        _picture.TrollMode = troll;
        _picture.DecodePictureFromBuffer(_gameData[_pictureOffsets[iPic]..], 4096, false, PicWidth, PicHeight);

        _picture.ShowPicture(0, 0, PicWidth, PicHeight);
    }

    // Game Logic

    private async Task InventoryAsync()
    {
        ClearScreen(0x07);
        DrawStr(1, 12, ColorDefault, Treasure0);
        DrawStr(2, 12, ColorDefault, Treasure1);

        for (int i = 0; i < MaxTreasure - _treasuresLeft; i++)
        {
            int n = _inventory[i] - 1;
            DrawStr(2 + i, 10, (_items[n].Bg << 4) | 0x0F, $" {i + 1,2} ");
            DrawStr(2 + i, 14, (_items[n].Bg << 4) | _items[n].Fg, _items[n].Name);
        }

        switch (_treasuresLeft)
        {
            case 1:
                DrawStr(20, 10, ColorDefault, string.Format(Treasure5, _treasuresLeft));
                break;
            case 0:
                DrawStr(20, 1, ColorDefault, Treasure6);
                break;
            case MaxTreasure:
                DrawStr(3, 17, ColorDefault, Treasure2);
                break;
            default:
                DrawStr(20, 10, ColorDefault, string.Format(Treasure4, _treasuresLeft));
                break;
        }

        await PressAnyKeyAsync(6);
    }

    private async Task WaitAnyKeyIntroAsync()
    {
        int iMsg = 0;
        AwaitingKey = true;
        try
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
                        case InputEventType.MouseUp when ev.Button is MouseButton.Left or MouseButton.Right:
                            return;
                    }
                }

                switch (iMsg)
                {
                    case 200:
                        iMsg = 0;
                        DrawStr(22, 3, ColorDefault, Intro2);
                        break;
                    case 0:
                        DrawStr(22, 3, ColorDefault, Intro2);
                        break;
                    case 100:
                        DrawStr(22, 3, ColorDefault, Intro3);
                        break;
                }

                iMsg++;
                await Poll();
            }
        }
        finally
        {
            AwaitingKey = false;
        }
    }

    private async Task CreditsAsync()
    {
        ClearScreen(0x07);

        DrawStr(1, 2, ColorDefault, Credits0);

        int color = 10;
        for (int i = 0; i < Credits1.Length; i++)
        {
            DrawStr(7, 19 + i, color++, Credits1[i].ToString());
            if (color > 15)
            {
                color = 9;
            }
        }

        DrawStr(8, 19, ColorDefault, Credits2);

        DrawStr(13, 11, 9, Credits3);
        DrawStr(15, 8, 10, Credits4);
        DrawStr(17, 7, 12, Credits5);
        DrawStr(19, 2, 14, Credits6);

        await PressAnyKeyAsync();
    }

    private async Task TutorialAsync()
    {
        int iSel = 0;

        while (!ShouldQuit)
        {
            ClearScreen(0xFF);

            PrintStr(Tutorial0);
            await GetSelectionAsync(SelectionType.Space);

            ClearScreen(0x55);
            DefaultColor = 0x0F;

            bool done = false;
            while (!done && !ShouldQuit)
            {
                (_, iSel) = await GetMenuSelAsync(Tutorial1, iSel, MaxOption);

                switch (iSel)
                {
                    case SelOption1:
                        ClearScreen(0x22, false);
                        break;
                    case SelOption2:
                        ClearScreen(0x00, false);
                        break;
                    case SelOption3:
                        done = true;
                        break;
                }
            }

            // do you need more practice ?
            ClearScreen(0x4F);
            DrawStr(7, 4, ColorDefault, Tutorial5);
            DrawStr(9, 4, ColorDefault, Tutorial6);

            if (await GetSelectionAsync(SelectionType.YesNo) == 0)
            {
                break;
            }
        }

        // show info texts
        ClearScreen(0x5F);
        DrawStr(4, 1, ColorDefault, Tutorial7);
        DrawStr(5, 1, ColorDefault, Tutorial8);
        await PressAnyKeyAsync();

        ClearScreen(0x2F);
        DrawStr(6, 1, ColorDefault, Tutorial9);
        await PressAnyKeyAsync();

        ClearScreen(0x19);
        DrawStr(7, 1, ColorDefault, Tutorial10);
        DrawStr(8, 1, ColorDefault, Tutorial11);
        await PressAnyKeyAsync();

        ClearScreen(0x6E);
        DrawStr(9, 1, ColorDefault, Tutorial12);
        DrawStr(10, 1, ColorDefault, Tutorial13);
        await PressAnyKeyAsync();

        ClearScreen(0x4C);
        DrawStr(11, 1, ColorDefault, Tutorial14);
        DrawStr(12, 1, ColorDefault, Tutorial15);
        await PressAnyKeyAsync();

        ClearScreen(0x5D);
        DrawStr(13, 1, ColorDefault, Tutorial16);
        DrawStr(14, 1, ColorDefault, Tutorial17);
        DrawStr(15, 1, ColorDefault, Tutorial18);
        await PressAnyKeyAsync();

        // show treasures
        ClearScreen(0x2A);
        DrawStr(2, 1, ColorDefault, Tutorial19);
        for (int i = 0; i < MaxTreasure; i++)
        {
            DrawStr(19 - i, 11, ColorDefault, _items[i].Name);
        }

        await PressAnyKeyAsync();
    }

    private async Task IntroAsync()
    {
        // sierra on-line presents
        ClearScreen(0x2F);
        DrawStr(9, 10, ColorDefault, Intro0);
        DrawStr(14, 15, ColorDefault, Intro1);
        await WaitAsync(3200);

        // Draw logo
        DefaultColor = 0x0F;
        DrawPic(45, false, true);

        // wait for keypress and alternate message
        await WaitAnyKeyIntroAsync();

        // have you played this game before?
        DrawStr(22, 3, ColorDefault, Intro4);
        DrawStr(23, 6, ColorDefault, Intro5);

        if (await GetSelectionAsync(SelectionType.YesNo) == 0)
        {
            await TutorialAsync();
        }

        await CreditsAsync();
    }

    private async Task GameOverAsync()
    {
        // Without this check the game shows the picture, plays the music, and then quits. So if
        // the game is quitting, we shouldn't run the "game over" part.
        if (ShouldQuit)
        {
            return;
        }

        ClearTextArea();
        DrawPic(42, true, true);

        await PlayTuneAsync(4, 25);

        await PrintUserMessageAsync(16);

        await PrintUserMessageAsync(33);

        ClearTextArea();

        DrawPic(46, true, true);

        DrawStr(21, 1, ColorDefault, string.Format(GameOver0, _moves));
        DrawStr(22, 1, ColorDefault, GameOver1);

        await PressAnyKeyAsync();
    }

    private void DrawTroll()
    {
        for (int i = 0; i < NumNonTroll; i++)
        {
            if (_currentRoom == _nonTrollRooms[i])
            {
                _isTrollAway = true;
                return;
            }
        }

        DrawPic(43, false, false, true);
    }

    /// <summary>Draws the room and builds its menu, four 40-column rows. Returns the number of options.</summary>
    private int DrawRoom(char[] menu)
    {
        bool contFlag = false;

        if (_currentRoom == 1)
        {
            ClearScreen(0x00, false);
        }
        else
        {
            if (_currentRoom != 42 && _roomPicDeltas[_currentRoom] != 0)
            {
                contFlag = true;
            }

            DrawPic(_currentRoom, contFlag, true);

            if (_currentRoom == 42)
            {
                DrawPic(44, false, false); // don't clear
            }
            else if (!_isTrollAway)
            {
                DrawTroll();
            }
        }

        // The menu is built in a C string as the original does: the room description, then each
        // option padded to the end of its row with a space written over the terminator.
        int n = 0;
        Array.Clear(menu);
        menu[0] = ' '; // leading space
        StrNCat(menu, ReadCString(_gameData, _locMessagesIdx[_currentRoom]), 39);

        for (int i = 0; i < 3; i++)
        {
            if (_roomDescs[_roomPicture - 1].Options[i] != 0)
            {
                StrNCat(menu, $"  {i + 1}.", 4);

                StrNCat(menu, ReadCString(_gameData, _options[_roomDescs[_roomPicture - 1].Options[i] - 1]), 35);
                menu[(i + 2) * 40 - 1] = ' ';
                menu[(i + 2) * 40] = '\0';

                n = i + 1;
            }
        }

        return n;
    }

    private static void StrNCat(char[] dest, string src, int count)
    {
        int start = Array.IndexOf(dest, '\0');
        int length = Math.Min(src.Length, count);
        src.CopyTo(0, dest, start, length);
        dest[start + length] = '\0';
    }

    private static string CString(char[] buffer)
    {
        int end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? buffer.Length : end);
    }

    private async Task PlayTuneAsync(int tune, int len)
    {
        if (!SoundOn)
        {
            return;
        }

        int ptr = _tunes[tune - 1];

        for (int i = 0; i < len; i++)
        {
            int freq = BinaryPrimitives.ReadUInt16LittleEndian(_gameData.AsSpan(ptr));
            ptr += 2;
            int duration = BinaryPrimitives.ReadUInt16LittleEndian(_gameData.AsSpan(ptr));
            ptr += 2;

            // Play note without processing events. The sounds are so short in this game that we
            // don't need to process events while playing notes.
            await PlaySpeakerNoteAsync(freq, duration, WaitOptions.Block);
        }
    }

    private async Task PickupTreasureAsync(int treasureId)
    {
        _inventory[MaxTreasure - _treasuresLeft] = treasureId;

        if (_currentRoom != 24)
        {
            ClearTextArea();
            DrawPic(_currentRoom, false, true);
        }

        await PrintUserMessageAsync(treasureId + 16);

        ClearTextArea();

        _treasuresLeft--;

        switch (_treasuresLeft)
        {
            case 1:
                DrawStr(22, 1, ColorDefault, Treasure7);
                break;
            case 0:
                DrawStr(22, 1, ColorDefault, Treasure8);
                DrawStr(23, 4, ColorDefault, Treasure9);

                _roomStates[6] = 1;

                _locMessagesIdx[6] = OfsAllTreasures;
                break;
            default:
                DrawStr(22, 1, ColorDefault, string.Format(Treasure3, _treasuresLeft));
                break;
        }

        await PressAnyKeyAsync();
    }

    private async Task PrintUserMessageAsync(int msgId)
    {
        ClearTextArea();

        var message = _userMessages[msgId - 1];
        for (int i = 0; i < message.Num; i++)
        {
            DrawStr(21 + i, 1, ColorDefault, message.Msg[i]);
        }

        if (msgId == 34)
        {
            for (int i = 0; i < 2; i++)
            {
                await PlayTuneAsync(5, 11);
            }
        }

        await PressAnyKeyAsync();
    }

    private async Task GameLoopAsync()
    {
        bool done = false;
        var menu = new char[MenuSize];

        _moves = 0;
        _roomPicture = 1;
        _treasuresLeft = MaxTreasure;
        bool haveFlashlight = false;
        _currentRoom = 0;
        _isTrollAway = true;
        SoundOn = true;

        Array.Clear(_roomStates);
        Array.Clear(_inventory);

        while (!done && !ShouldQuit)
        {
            int numberOfOptions = DrawRoom(menu);

            var (chosen, currentOption) = await GetMenuSelAsync(CString(menu), 0, numberOfOptions);
            if (chosen)
            {
                _moves++;
            }
            else
            {
                continue;
            }

            int roomParam = _roomDescs[_roomPicture - 1].RoomDescIndex[currentOption];

            switch (_roomDescs[_roomPicture - 1].OptionTypes[currentOption])
            {
                case OptionType.Flashlight when !haveFlashlight:
                    await PrintUserMessageAsync(13);
                    break;
                case OptionType.Flashlight:
                case OptionType.Go:
                    _currentRoom = roomParam;
                    _roomPicture = _roomPicStartIdx[_currentRoom];
                    _roomPicture += _roomStates[_currentRoom];

                    if (_currentRoom < 6 || _treasuresLeft == 0)
                    {
                        _isTrollAway = true;
                    }
                    else
                    {
                        // make odd 1:3
                        _isTrollAway = Rnd(3) != 2;
                    }

                    break;
                case OptionType.Get:
                    if (!_isTrollAway)
                    {
                        await PrintUserMessageAsync(34);
                    }
                    else
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            await PlayTuneAsync(1, 3);
                        }

                        _roomStates[_currentRoom] = 1;
                        _roomPicDeltas[_currentRoom] = 0;

                        _roomPicture++;

                        if (_roomConnects[roomParam - 1] != 0xFF)
                        {
                            _roomStates[_roomConnects[roomParam - 1]] = 1;
                        }

                        if (roomParam == 1)
                        {
                            haveFlashlight = true;
                        }

                        _locMessagesIdx[_currentRoom] = OfsLocMessages + (roomParam + 42) * 39;

                        await PickupTreasureAsync(roomParam);
                    }

                    break;
                case OptionType.Do:
                    if (roomParam != 16)
                    {
                        await PrintUserMessageAsync(roomParam);
                        break;
                    }

                    done = true;
                    break;
            }
        }
    }

    private void FillOffsets()
    {
        ReadOnlySpan<byte> data = _gameData;
        int U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(_gameData.AsSpan(offset));

        for (int i = 0; i < PicNum; i++)
        {
            _pictureOffsets[i] = U16(OfsPicStart + i * 2);
        }

        for (int i = 0; i < NumOptions; i++)
        {
            _options[i] = U16(OfsOptions + i * 2);
        }

        for (int i = 0; i < NumNumRooms; i++)
        {
            _roomPicStartIdx[i] = data[OfsPicStartIdx + i];
            _roomPicDeltas[i] = data[OfsRoomPicDeltas + i];
            _roomConnects[i] = data[OfsRoomConnects + i];
        }

        for (int i = 0; i < NumLocDescs; i++)
        {
            _locMessagesIdx[i] = OfsLocMessages + i * 39;
        }

        int start = U16(OfsRoomDescs);
        for (int i = 0; i < NumRoomDescs; i++, start += 2)
        {
            int ptr = U16(start);
            var desc = new RoomDesc();

            for (int j = 0; j < 3; j++)
            {
                desc.Options[j] = data[ptr++];
            }

            for (int j = 0; j < 3; j++)
            {
                desc.OptionTypes[j] = data[ptr++] switch
                {
                    0 => OptionType.Go,
                    1 => OptionType.Get,
                    2 => OptionType.Do,
                    3 => OptionType.Flashlight,
                    _ => throw new InvalidDataException($"Bad data @ ({ptr - 1:x}) {i}"),
                };
            }

            for (int j = 0; j < 3; j++)
            {
                desc.RoomDescIndex[j] = data[ptr++];
            }

            _roomDescs[i] = desc;
        }

        start = OfsUserMessages;
        for (int i = 0; i < NumUserMsgs; i++, start += 2)
        {
            int ptr = U16(start);
            var message = new UserMsg { Num = data[ptr++] };

            for (int j = 0; j < message.Num; j++, ptr += 39)
            {
                message.Msg[j] = ReadCString(data, ptr, 39);
            }

            _userMessages[i] = message;
        }

        start = OfsItems;
        for (int i = 0; i < MaxTreasure; i++, start += 2)
        {
            int ptr = U16(start);
            _items[i] = new Item(data[ptr], data[ptr + 1], ReadCString(data, ptr + 2, 15));
        }

        for (int i = 0; i < NumNonTroll; i++)
        {
            _nonTrollRooms[i] = data[OfsNonTrollRooms + i];
        }

        _tunes[0] = 0x3BFD;
        _tunes[1] = 0x3C09;
        _tunes[2] = 0x3C0D;
        _tunes[3] = 0x3C11;
        _tunes[4] = 0x3C79;
        _tunes[5] = 0x3CA5;
    }

    // Init

    /// <summary>Reads the game's data out of the disk image: every other run between the gaps, plus one sector that is elsewhere.</summary>
    private bool Init()
    {
        int[] gaps =
        [
            0x3A40, 0x4600, 0x4800, 0x5800, 0x5a00, 0x6a00,
            0x6c00, 0x7400, 0x7600, 0x7c00, 0x7e00, 0x8e00,
            0x9000, 0xa000, 0xa200, 0xb200, 0xb400, 0xc400,
            0xc600, 0xd600, 0xd800, 0xe800, 0xea00, 0xfa00,
            0xfc00, 0x10c00, 0x10e00, 0x11e00, 0x12000, 0x13000,
        ];

        var file = TryReadFile(_dataFile);
        if (file is null)
        {
            return false;
        }

        if (file.Length < 0x18470 + 592)
        {
            Host.Log.Error("agi.preagi", $"{_dataFile} is {file.Length} bytes, too short for Troll's Tale");
            return false;
        }

        _gameData = new byte[0xD9C0];

        bool flip = true;
        int ptr = 0;
        for (int i = 0; i < gaps.Length - 1; i++)
        {
            int diff = gaps[i + 1] - gaps[i];
            if (flip)
            {
                file.AsSpan(gaps[i], diff).CopyTo(_gameData.AsSpan(ptr));
                ptr += diff;
            }

            flip = !flip;
        }

        // One sector is off
        file.AsSpan(0x18470, 592).CopyTo(_gameData.AsSpan(15632));

        FillOffsets();
        return true;
    }
}
