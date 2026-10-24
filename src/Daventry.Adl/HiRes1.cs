using Daventry.Adl.Detection;
using Daventry.Core.Formats;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;

namespace Daventry.Adl;

/// <summary>
/// Hi-Res Adventure #1: Mystery House (1980), the first ADL game. Its data are DOS 3.3 files on
/// the disk (or the same files copied out), its pictures line drawings, and it pauses after each
/// message instead of waiting for Return. Port of ScummVM's HiRes1Engine.
/// </summary>
public class HiRes1Engine : AdlEngine
{
    protected const string Exe0 = "AUTO LOAD OBJ";
    protected const string Exe1 = "ADVENTURE";
    private const string MessagesFile = "MESSAGES";

    private const int NumRooms = 41;
    private const int NumPics = 97;
    private const int NumVars = 20;
    private const int NumItemOffsets = 21;
    private const int NumMessages = 168;

    // messages used outside of scripts
    private const int MsgCantGoThere = 137;
    private const int MsgDontUnderstand = 37;
    private const int MsgItemDoesntMove = 151;
    private const int MsgItemNotHere = 152;
    private const int MsgThanksForPlaying = 140;
    private const int MsgDontHaveIt = 127;
    private const int MsgGettingDark = 7;

    private const int OfsPdText0 = 0x005d;
    private const int OfsPdText1 = 0x012b;
    private const int OfsPdText2 = 0x016d;
    private const int OfsPdText3 = 0x0259;

    private const int OfsItems = 0x0100;
    private const int OfsRooms = 0x050a;
    private const int OfsPics = 0x4b03;
    private const int OfsCmds0 = 0x3c00;
    private const int OfsCmds1 = 0x3d00;
    private const int OfsMsgs = 0x4d00;
    private const int OfsItemOffsets = 0x68ff;
    private const int OfsShapes = 0x4f00;

    private readonly List<DataBlock> _corners = [];
    private readonly List<byte> _roomDesc = [];
    private readonly GameStrings _gameStrings = new();
    private bool _messageDelay = true;

    public HiRes1Engine(IHost host, IGameContainer game, AdlDetectedGame detected, DisplayOptions options)
        : base(host, game, detected, options)
    {
    }

    protected GameFiles Files { get; private set; } = null!;

    protected async Task ShowInstructions(SeekableReadStream stream)
    {
        Display.SetMode(Adl.Display.Mode.Text);

        while (true)
        {
            byte opc = stream.ReadByte();

            if (opc != 0x20)
            {
                throw new InvalidDataException("Error reading instructions");
            }

            ushort addr = stream.ReadUInt16BE();

            if (addr == 0x58fc)
            {
                // HOME
                Display.Home();
            }
            else if (addr == 0x6ffd)
            {
                // GETLN1
                await InputString();

                if (ShouldQuit)
                {
                    return;
                }
            }
            else
            {
                // a print string call (the address varies per game)
                string str = ReadString(stream);

                // Ctrl-D signifies a system command (the main binary would be loaded here)
                int posChr4 = str.IndexOf(Display.AsciiToNative((char)4));

                if (posChr4 >= 0)
                {
                    Display.PrintString(str[..posChr4]);
                    return;
                }

                Display.PrintString(str);
            }
        }
    }

    protected override async Task RunIntro()
    {
        var stream = Files.CreateReadStream(Exe0);

        // early versions have no bitmap in 'AUTO LOAD OBJ'
        if (GameVersion >= GameVersion.Hr1Coarse)
        {
            // later binaries have a MIXEDON prepended; skipping it gives both variants the same offsets
            if (stream.ReadUInt16BE() == 0xad53)
            {
                stream = Files.CreateReadStream(Exe0, 3);
            }

            stream.Seek(0x1000);
            Display.SetMode(Adl.Display.Mode.Graphics);
            Display.LoadFrameBuffer(stream);
            Display.RenderGraphics();

            if (GameVersion == GameVersion.Hr1Pd)
            {
                // only the PD version shows a title screen during the load
                await Delay(4000);

                if (ShouldQuit)
                {
                    return;
                }
            }
        }

        string str;

        // the public domain release opens with its disclaimer
        if (GameVersion == GameVersion.Hr1Pd)
        {
            // the Roberta Williams Anthology renamed the HELLO file
            string fileName = Files.Exists("MYSTERY.HELLO") ? "MYSTERY.HELLO" : "HELLO";
            var basic = Files.CreateReadStream(fileName);

            Display.SetMode(Adl.Display.Mode.Text);
            Display.Home();

            str = ReadStringAt(basic, OfsPdText0, (byte)'"');
            Display.PrintAsciiString(str + '\r');

            str = ReadStringAt(basic, OfsPdText1, (byte)'"');
            Display.PrintAsciiString(str + "\r\r");

            str = ReadStringAt(basic, OfsPdText2, (byte)'"');
            Display.PrintAsciiString(str + "\r\r");

            str = ReadStringAt(basic, OfsPdText3, (byte)'"');
            Display.PrintAsciiString(str + '\r');

            await InputKey();
            if (ShouldQuit)
            {
                return;
            }
        }

        Display.SetMode(Adl.Display.Mode.Mixed);

        str = ReadStringAt(stream, 0xc);

        if (GameVersion >= GameVersion.Hr1Coarse)
        {
            bool instructions = false;
            char keyInstr = 'I';
            char keyGame = 'G';

            if (Language == "fr")
            {
                keyInstr = 'M';
                keyGame = 'J';
            }

            while (true)
            {
                Display.PrintString(str);
                string s = await InputString();

                if (ShouldQuit)
                {
                    break;
                }

                if (s.Length == 0)
                {
                    continue;
                }

                if (s[0] == Display.AsciiToNative(keyInstr))
                {
                    instructions = true;
                    break;
                }

                if (s[0] == Display.AsciiToNative(keyGame))
                {
                    break;
                }
            }

            if (instructions)
            {
                stream.Seek(0x5d);
                await ShowInstructions(stream);
                Display.PrintAsciiString("\r");
            }
        }
        else
        {
            // this version shows the last page while loading the game; a key stands in for that
            stream.Seek(3);
            await ShowInstructions(stream);
            await InputString();
        }

        stream = Files.CreateReadStream(Exe1);
        stream.Seek(0x1800);
        Display.LoadFrameBuffer(stream);
        Display.RenderGraphics();

        // the title screen would normally be visible during loading; a short delay simulates that
        await Delay(2000);

        Display.SetMode(Adl.Display.Mode.Mixed);

        if (GameVersion == GameVersion.Hr1Simi)
        {
            // the original waits for a key after initialising the state, which also makes it wait
            // on a blank screen after a restart; ScummVM only waits here, in the intro, after
            // pushing out some line feeds to clear the screen
            Display.PrintString(Strings.LineFeeds);
            await InputKey();
        }
    }

    protected override void Init()
    {
        if (Game.Exists(Exe1))
        {
            Files = new PlainFiles(Game);
        }
        else
        {
            string name = Description.Volumes[0];
            var disk = DiskImage.Open(Game, name, Host.Log) ?? throw new InvalidDataException($"Failed to open '{name}'");
            Files = AppleDosFiles.Open(disk);
        }

        Graphics = new GraphicsManV1(Display);
        Display.MoveCursorTo(0, 23); // DOS 3.3

        var stream = Files.CreateReadStream(Exe1);

        var exeStrings = new List<string>();
        ExtractExeStrings(stream, 0x1576, exeStrings);

        if (exeStrings.Count != 18)
        {
            throw new InvalidDataException("Failed to load strings from executable");
        }

        // some messages have overrides inside the executable
        _gameStrings.CantGoThere = exeStrings[12];
        _gameStrings.DontHaveIt = exeStrings[13];
        _gameStrings.DontUnderstand = exeStrings[14];
        _gameStrings.GettingDark = exeStrings[15];

        // other strings from the executable
        Strings.EnterCommand = exeStrings[5];
        Strings.VerbError = exeStrings[3];
        Strings.NounError = exeStrings[4];
        Strings.PlayAgain = exeStrings[8];
        Strings.PressReturn = exeStrings[10];
        Strings.LineFeeds = exeStrings[1];

        MessageIdTable.CantGoThere = MsgCantGoThere;
        MessageIdTable.DontUnderstand = MsgDontUnderstand;
        MessageIdTable.ItemDoesntMove = MsgItemDoesntMove;
        MessageIdTable.ItemNotHere = MsgItemNotHere;
        MessageIdTable.ThanksForPlaying = MsgThanksForPlaying;

        if (Language == "fr")
        {
            VerbErrorPos = 15;
            NounErrorPos = 31;
        }

        // message offsets
        stream.Seek(OfsMsgs);
        for (int i = 0; i < NumMessages; ++i)
        {
            Messages.Add(Files.GetDataBlock(MessagesFile, stream.ReadUInt16LE()));
        }

        // the French version has 5 more
        if (Language == "fr")
        {
            for (int i = 0; i < 5; ++i)
            {
                Messages.Add(Files.GetDataBlock(MessagesFile, stream.ReadUInt16LE()));
            }
        }

        // picture data from the executable
        stream.Seek(OfsPics);
        for (int i = 1; i <= NumPics; ++i)
        {
            byte block = stream.ReadByte();
            ushort offset = stream.ReadUInt16LE();
            Pictures[(byte)i] = Files.GetDataBlock($"BLOCK{block}", offset);
        }

        // commands from the executable
        stream.Seek(OfsCmds1);
        ReadCommands(stream, RoomCommands);

        stream.Seek(OfsCmds0);
        ReadCommands(stream, GlobalCommands);

        // dropped item offsets
        stream.Seek(OfsItemOffsets);
        LoadDroppedItemOffsets(stream, NumItemOffsets);

        // shapes
        stream.Seek(OfsShapes);
        int cornersCount = stream.ReadUInt16LE();
        for (int i = 0; i < cornersCount; ++i)
        {
            _corners.Add(Files.GetDataBlock(Exe1, OfsShapes + stream.ReadUInt16LE()));
        }

        if (stream.Eos)
        {
            throw new InvalidDataException($"Failed to read game data from '{Exe1}'");
        }

        stream.Seek(Language == "fr" ? 0x900 : 0x3800);
        LoadWords(stream, Verbs, PriVerbs);

        stream.Seek(0xf00);
        LoadWords(stream, Nouns, PriNouns);
    }

    protected override void InitGameState()
    {
        ResizeVars(_state.Vars, NumVars);

        var stream = Files.CreateReadStream(Exe1);

        // rooms
        _roomDesc.Clear();
        stream.Seek(OfsRooms);
        for (int i = 0; i < NumRooms; ++i)
        {
            var room = new Room();
            stream.ReadByte();
            _roomDesc.Add(stream.ReadByte());
            for (int j = 0; j < 6; ++j)
            {
                room.Connections[j] = stream.ReadByte();
            }

            room.Picture = stream.ReadByte();
            room.CurPicture = stream.ReadByte();
            _state.Rooms.Add(room);
        }

        // items
        stream.Seek(OfsItems);
        byte id;
        while ((id = stream.ReadByte()) != 0xff)
        {
            var item = new Item
            {
                Id = id,
                Noun = stream.ReadByte(),
                Room = stream.ReadByte(),
                Picture = stream.ReadByte(),
                IsShape = stream.ReadByte() != 0,
                PositionX = stream.ReadByte(),
                PositionY = stream.ReadByte(),
                State = stream.ReadByte(),
                Description = stream.ReadByte(),
            };

            stream.ReadByte();

            int size = stream.ReadByte();
            for (int i = 0; i < size; ++i)
            {
                item.RoomPictures.Add(stream.ReadByte());
            }

            _state.Items.Add(item);
        }
    }

    protected override async Task PrintString(string str)
    {
        Display.PrintString(WordWrap(str));

        if (_messageDelay)
        {
            await Delay(Language == "fr" ? 2900 : 2250);
        }
    }

    protected override string LoadMessage(int idx)
    {
        char returnChar = Display.AsciiToNative('\r');
        return ReadString(Messages[idx]!.CreateReadStream(), (byte)returnChar) + returnChar;
    }

    protected override async Task PrintMessage(int idx)
    {
        // in the English version, messages with overrides in the executable don't delay after
        // printing; the slow drawing of the room gave the player time to read them, which ScummVM
        // doesn't, so it adds a delay (the French version's length-based delays are made fixed too)
        switch (idx)
        {
            case MsgCantGoThere:
                Display.PrintString(_gameStrings.CantGoThere);
                break;
            case MsgDontHaveIt:
                Display.PrintString(_gameStrings.DontHaveIt);
                break;
            case MsgDontUnderstand:
                Display.PrintString(_gameStrings.DontUnderstand);
                break;
            case MsgGettingDark:
                Display.PrintString(_gameStrings.GettingDark);
                break;
            default:
                await PrintString(LoadMessage(idx));
                return;
        }

        await Delay(1500);
    }

    protected override void DrawItems()
    {
        int dropped = 0;

        foreach (var item in _state.Items)
        {
            // skip items not in this room
            if (item.Room != _state.Room)
            {
                continue;
            }

            if (item.State == ItemDropped)
            {
                // draw a dropped item if in normal view
                if (GetCurRoom().Picture == GetCurRoom().CurPicture)
                {
                    var (x, y) = ItemOffsets[dropped++];
                    DrawItem(item, x, y);
                }
            }
            else
            {
                // draw a fixed item if the current view is in its picture list
                foreach (byte pic in item.RoomPictures)
                {
                    if (pic == GetCurRoom().CurPicture)
                    {
                        DrawItem(item, item.PositionX, item.PositionY);
                        break;
                    }
                }
            }
        }
    }

    protected override void DrawItem(Item item, int x, int y)
    {
        if (item.IsShape)
        {
            var stream = _corners[item.Picture - 1].CreateReadStream();
            Graphics.DrawShape(stream, ref x, ref y);
        }
        else
        {
            DrawPic(item.Picture, x, y);
        }
    }

    protected override void LoadRoom(byte roomNr) => CurrentRoomData.Description = LoadMessage(_roomDesc[_state.Room - 1]);

    protected override async Task ShowRoom()
    {
        _state.CurPicture = GetCurRoom().CurPicture;
        Graphics.ClearScreen();
        LoadRoom(_state.Room);

        if (!_state.IsDark)
        {
            DrawPic(GetCurRoom().CurPicture);
            DrawItems();
        }

        Display.RenderGraphics();
        _messageDelay = false;
        await PrintString(CurrentRoomData.Description);
        _messageDelay = true;
    }

    private string WordWrap(string str)
    {
        int end = 39;
        char spaceChar = Display.AsciiToNative(' ');
        char returnChar = Display.AsciiToNative('\r');
        var chars = str.ToCharArray();

        while (chars.Length > end)
        {
            while (chars[end] != spaceChar)
            {
                --end;
            }

            chars[end] = returnChar;
            end += 40;
        }

        return new string(chars);
    }

    private sealed class GameStrings
    {
        public string CantGoThere { get; set; } = string.Empty;

        public string DontHaveIt { get; set; } = string.Empty;

        public string DontUnderstand { get; set; } = string.Empty;

        public string GettingDark { get; set; } = string.Empty;
    }
}

/// <summary>
/// The French Mystery House by Malibu Microcomputing, second release: a parser that turns an
/// imperative into the infinitive it knows, and skips words until it finds a noun. Port of
/// ScummVM's HiRes1Engine_VF.
/// </summary>
public sealed class HiRes1EngineVf(IHost host, IGameContainer game, AdlDetectedGame detected, DisplayOptions options)
    : HiRes1Engine(host, game, detected, options)
{
    protected override async Task<(int Verb, int Noun)> GetInput()
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

            if (!Verbs.ContainsKey(verbString))
            {
                // if the verb looks like an imperative, try the infinitives
                string ez = "ÅÚ"; // "EZ"
                int ezPos = verbString.IndexOf(ez, StringComparison.Ordinal);
                bool found = false;

                if (ezPos >= 0)
                {
                    foreach (string suffix in new[] { "ÅÒ", "ÉÒ", "ÒÅ" }) // "ER", "IR", "RE"
                    {
                        verbString = Replace(verbString, ezPos, suffix);
                        if (Verbs.ContainsKey(verbString))
                        {
                            found = true;
                            break;
                        }
                    }
                }

                if (!found)
                {
                    Display.PrintString(FormatVerbError(verbString));
                    continue;
                }
            }

            int verb = Verbs[verbString];

            while (true)
            {
                // go over all words to find a known noun; at the end of the line it always
                // matches the empty word, which is in the noun list
                string nounString = GetWord(line, ref index);

                if (Nouns.TryGetValue(nounString, out int noun))
                {
                    return (verb, noun);
                }
            }
        }
    }

    protected override async Task RunIntro()
    {
        var stream = Files.CreateReadStream(Exe0);
        stream.Seek(0x1000);

        // the title image is one padding byte short
        var frameBuffer = new byte[0x2000];
        if (stream.Read(frameBuffer.AsSpan(0, frameBuffer.Length - 1)) < frameBuffer.Length - 1)
        {
            throw new InvalidDataException("Failed to read title image");
        }

        Display.SetMode(Adl.Display.Mode.Graphics);
        Display.LoadFrameBuffer(new SeekableReadStream(frameBuffer));
        Display.RenderGraphics();

        Display.SetMode(Adl.Display.Mode.Mixed);

        string str = ReadStringAt(stream, 0xf);

        while (true)
        {
            Display.PrintString(str);
            byte key = await InputKey();

            if (ShouldQuit)
            {
                return;
            }

            if (key == Display.AsciiToNative('M'))
            {
                stream.Seek(0x75);
                await ShowInstructions(stream);
                return;
            }

            if (key == Display.AsciiToNative('J'))
            {
                return;
            }
        }
    }
}
