using Daventry.Adl.Detection;
using Daventry.Core.Formats;
using Daventry.Core.Graphics;
using Daventry.Core.Hosting;
using Daventry.Core.Input;
using Daventry.Core.Storage;

namespace Daventry.Adl;

/// <summary>
/// Hi-Res Adventure #4: Ulysses and the Golden Fleece (1981), a two-sided game: side A boots and
/// holds the engine's data, side B the rooms. Port of ScummVM's HiRes4BaseEngine and its versions:
/// the original 1.0, version 1.1 (sectors skewed, some data XORed), Green Valley's re-releases,
/// and the Atari 8-bit re-release.
/// </summary>
public abstract class HiRes4BaseEngine : AdlEngineV3
{
    protected const int NumRooms = HiRes4BaseEngineConstants.NumRooms;
    protected const int NumMessages = HiRes4BaseEngineConstants.NumMessages;
    protected const int NumVars = HiRes4BaseEngineConstants.NumVars;
    protected const int NumItemDescs = HiRes4BaseEngineConstants.NumItemDescs;
    protected const int NumItemPics = HiRes4BaseEngineConstants.NumItemPics;
    protected const int NumItemOffsets = HiRes4BaseEngineConstants.NumItemOffsets;

    protected HiRes4BaseEngine(IHost host, IGameContainer game, AdlDetectedGame detected, DisplayOptions options)
        : base(host, game, detected, options)
    {
        BrokenRooms.Add(121);
        MessageIdTable.CantGoThere = 110;
        MessageIdTable.DontUnderstand = 112;
        MessageIdTable.ItemDoesntMove = 114;
        MessageIdTable.ItemNotHere = 115;
        MessageIdTable.ThanksForPlaying = 113;
    }

    protected DiskImage Boot { get; private set; } = null!;

    protected override void Init()
    {
        Graphics = new GraphicsManV2(Display);
        Boot = OpenDisk(0);
        InsertDisk(1);
    }

    protected override void InitGameState() => ResizeVars(_state.Vars, NumVars);

    protected SeekableReadStream ReadBoot(int track, int sector, int offset = 0, int size = 0, int sectorLimit = 0)
        => Boot.CreateReadStream(track, sector, offset, size, sectorLimit)
            ?? throw new InvalidDataException($"Error reading disk image at track {track}, sector {sector}");

    /// <summary>The boot side's DOS 3.3 files.</summary>
    protected AppleDosFiles BootFiles() => AppleDosFiles.Open(OpenDisk(0));
}

/// <summary>The original release, 1.0.</summary>
public sealed class HiRes4EngineV10(IHost host, IGameContainer game, AdlDetectedGame detected, DisplayOptions options)
    : HiRes4BaseEngine(host, game, detected, options)
{
    protected override async Task RunIntro()
    {
        var stream = ReadBoot(0x06, 0x3, 0xb9, 1);

        Display.SetMode(Adl.Display.Mode.Text);
        Display.PrintString(ReadString(stream));

        await WaitKey(0, Key.Return);
    }

    protected override void Init()
    {
        base.Init();

        var stream = ReadBoot(0x9, 0x1, 0x00, 13);
        var exeStrings = new List<string>();
        ExtractExeStrings(stream, 0x1566, exeStrings);
        MapExeStrings(exeStrings);

        LoadMessages(ReadBoot(0x0e, 0x5, 0x00, 3, 13), NumMessages);
        LoadPictures(ReadBoot(0x09, 0x0, 0x80, 0, 13));
        LoadItemPictures(ReadBoot(0x0d, 0xc, 0x05, 0, 13), NumItemPics);
        LoadItemDescriptions(ReadBoot(0x07, 0x0, 0x15, 2, 13), NumItemDescs);
        ReadCommands(ReadBoot(0x0c, 0x9, 0xa5, 5, 13), RoomCommands);
        ReadCommands(ReadBoot(0x07, 0xc, 0x00, 3, 13), GlobalCommands);
        LoadDroppedItemOffsets(ReadBoot(0x0a, 0x7, 0x15, 0, 13), NumItemOffsets);
        LoadWords(ReadBoot(0x08, 0x3, 0x00, 3, 13), Verbs, PriVerbs, 80); // missing terminator
        LoadWords(ReadBoot(0x05, 0x7, 0x00, 6, 13), Nouns, PriNouns, 109); // missing terminator
    }

    protected override void InitGameState()
    {
        base.InitGameState();

        LoadRooms(ReadBoot(0x04, 0xa, 0x0e, 9, 13), NumRooms);
        LoadItems(ReadBoot(0x04, 0x5, 0x00, 12, 13));
    }
}

/// <summary>Version 1.1: the data sit in skewed sectors and parts of it are XORed.</summary>
public class HiRes4EngineV11(IHost host, IGameContainer game, AdlDetectedGame detected, DisplayOptions options)
    : HiRes4BaseEngine(host, game, detected, options)
{
    protected override async Task RunIntro()
    {
        var files = BootFiles();
        var menu = files.CreateReadStream("\b\b\b\b\b\b\bULYSSES\r(C) 1982");
        menu.Seek(0x2eb);

        int[] rows = [0, 2, 4, 16];
        foreach (int row in rows)
        {
            string s = menu.ReadString(0, 39);
            Display.MoveCursorTo(0, row);
            Display.PrintString(s);
        }

        await WaitKey(3000);
    }

    protected override void Init()
    {
        base.Init();

        var stream = ReadSkewedSectors(Boot, 0x05, 0x6, 1);
        Strings.VerbError = ReadStringAt(stream, 0x4f);
        Strings.NounError = ReadStringAt(stream, 0x8e);
        Strings.EnterCommand = ReadStringAt(stream, 0xbc);

        stream = ReadSkewedSectors(Boot, 0x05, 0x3, 1);
        stream.Skip(0xd7);
        StringsV2.Time = ReadString(stream, 0xff);

        stream = ReadSkewedSectors(Boot, 0x05, 0x7, 2);
        Strings.LineFeeds = ReadStringAt(stream, 0xf8);

        stream = ReadSkewedSectors(Boot, 0x06, 0xf, 3);
        StringsV2.SaveInsert = ReadStringAt(stream, 0x5f);
        StringsV2.SaveReplace = ReadStringAt(stream, 0xe5);
        StringsV2.RestoreInsert = ReadStringAt(stream, 0x132);
        StringsV2.RestoreReplace = ReadStringAt(stream, 0x1c2);
        Strings.PlayAgain = ReadStringAt(stream, 0x225);

        LoadMessages(ReadSkewedSectors(Boot, 0x0a, 0x0, 5), NumMessages);

        stream = ReadSkewedSectors(Boot, 0x05, 0x2, 1);
        stream.Skip(0x80);
        LoadPictures(stream);

        stream = ReadSkewedSectors(Boot, 0x09, 0x2, 1);
        stream.Skip(0x05);
        LoadItemPictures(stream, NumItemPics);

        stream = ReadSkewedSectors(Boot, 0x04, 0x0, 3);
        stream.Skip(0x15);
        LoadItemDescriptions(stream, NumItemDescs);

        stream = ReadSkewedSectors(Boot, 0x08, 0x2, 6);
        stream.Skip(0xa5);
        ReadCommands(stream, RoomCommands);

        stream = DecodeData(ReadSkewedSectors(Boot, 0x04, 0xc, 4), 0x218, 0x318, 0xee);
        ReadCommands(stream, GlobalCommands);

        stream = ReadSkewedSectors(Boot, 0x06, 0x6, 1);
        stream.Skip(0x15);
        LoadDroppedItemOffsets(stream, NumItemOffsets);

        LoadWords(ReadSkewedSectors(Boot, 0x05, 0x0, 4), Verbs, PriVerbs);
        LoadWords(ReadSkewedSectors(Boot, 0x0b, 0xb, 7), Nouns, PriNouns);
    }

    protected override void InitGameState()
    {
        base.InitGameState();

        var stream = ReadSkewedSectors(Boot, 0x0b, 0x9, 10);
        stream.Skip(0x0e);
        LoadRooms(stream, NumRooms);

        stream = DecodeData(ReadSkewedSectors(Boot, 0x0b, 0x0, 13), 0x43, 0x143, 0x91);
        LoadItems(stream);
    }

    /// <summary>Reads sectors five apart, the skew this version wrote them with.</summary>
    private static SeekableReadStream ReadSkewedSectors(DiskImage disk, int track, int sector, int count)
    {
        int bytesPerSector = disk.BytesPerSector;
        int sectorsPerTrack = disk.SectorsPerTrack;
        var buf = new byte[count * bytesPerSector];
        int p = 0;

        while (count-- != 0)
        {
            var stream = disk.CreateReadStream(track, sector) ?? throw new InvalidDataException("Error loading from disk image");
            if (stream.Read(buf.AsSpan(p, bytesPerSector)) < bytesPerSector)
            {
                throw new InvalidDataException("Error loading from disk image");
            }

            p += bytesPerSector;
            sector += 5;
            sector %= sectorsPerTrack;
            if (sector == 0)
            {
                ++track;
            }
        }

        return new SeekableReadStream(buf);
    }

    private static SeekableReadStream DecodeData(SeekableReadStream stream, int startOffset, int endOffset, byte xorVal)
    {
        var buf = stream.ToArray();
        endOffset = Math.Min(endOffset, buf.Length);

        for (int i = startOffset; i < endOffset; ++i)
        {
            buf[i] ^= xorVal;
        }

        return new SeekableReadStream(buf);
    }
}

/// <summary>
/// The Green Valley Publishing re-releases (version 0.0 with its animated logo and shape-drawn
/// title, and a later 1.1): a menu offering the instructions or the game. Port of ScummVM's
/// HiRes4Engine_LNG.
/// </summary>
public sealed class HiRes4EngineLng(IHost host, IGameContainer game, AdlDetectedGame detected, DisplayOptions options)
    : HiRes4EngineV11(host, game, detected, options)
{
    private const int Clock = 1022727; // Apple II CPU clock rate

    protected override async Task RunIntro()
    {
        var files = BootFiles();

        while (!ShouldQuit)
        {
            var menu = files.CreateReadStream("MENU");
            bool oldVersion = files.Exists("MS2");

            if (oldVersion)
            {
                // version 0.0
                var ms2 = files.CreateReadStream("MS2");
                await RunIntroLogo(ms2);

                if (ShouldQuit)
                {
                    return;
                }

                Graphics.Bounds = new Rect(0, 0, 280, 192);
                await RunIntroTitle(menu, ms2);
                Graphics.Bounds = new Rect(0, 0, 280, 160);
            }
            else
            {
                // version 1.1; its publisher logo uses BASIC graphics routines ScummVM doesn't
                // implement, so it's skipped. File offset, x and y of each line of the title
                // screen, leaving out the "create data disk" option
                int[][] text = [[0x13, 4, 1], [0x42, 7, 9], [0x66, 7, 11], [0xaa, 7, 17]];

                for (int i = 0; i < text.Length; ++i)
                {
                    var str = ReadStringAt(menu, text[i][0], (byte)'"').ToCharArray();
                    for (int c = 0; c < str.Length; c++)
                    {
                        str[c] = Display.AsciiToNative(str[c]);
                        if (i == 0)
                        {
                            str[c] = (char)(str[c] & ~0xc0); // invert the first line
                        }
                    }

                    Display.MoveCursorTo(text[i][1], text[i][2]);
                    Display.PrintString(new string(str));
                }
            }

            while (true)
            {
                byte key = await InputKey(false);

                if (ShouldQuit)
                {
                    return;
                }

                if (key == Display.AsciiToNative('1'))
                {
                    await RunIntroInstructions(files.CreateReadStream("INSTRUCTIONS"));
                    break;
                }

                if (key == Display.AsciiToNative('2'))
                {
                    await RunIntroLoading(files.CreateReadStream("THE ADVENTURE"));
                    return;
                }
            }
        }
    }

    private async Task DrawText(string str, SeekableReadStream shapeTable, float ht, float vt)
    {
        if (ShouldQuit)
        {
            return;
        }

        int x = (int)(ht * 7);
        int y = (int)(vt * 7.7f);

        DrawChar(99, shapeTable, ref x, ref y);

        foreach (char ch in str)
        {
            DrawChar((byte)(ch - 32), shapeTable, ref x, ref y);
            DrawChar(98, shapeTable, ref x, ref y);

            Display.RenderGraphics();
            await Delay(15);
        }
    }

    private void DrawChar(byte c, SeekableReadStream shapeTable, ref int x, ref int y)
    {
        shapeTable.Seek(0);
        byte entries = shapeTable.ReadByte();

        if (c >= entries)
        {
            throw new InvalidDataException($"Character {c} is not in the shape table");
        }

        shapeTable.Seek(c * 2 + 2);
        int offset = shapeTable.ReadUInt16LE();
        shapeTable.Seek(offset);

        Graphics.DrawShape(shapeTable, ref x, ref y);
    }

    private async Task RunIntroLogo(SeekableReadStream ms2)
    {
        int width = DisplayA2.GfxWidth;
        int height = DisplayA2.GfxHeight;
        int pitch = DisplayA2.GfxPitch;

        Display.Clear(0x00);
        Display.SetMode(Adl.Display.Mode.Graphics);
        var logo = new byte[pitch * height];
        DisplayA2.LoadFrameBuffer(ms2, logo);

        for (int x = 0; x < width; ++x)
        {
            for (int y = 0; y < height; ++y)
            {
                byte p = logo[y * pitch + x / 7];
                Display.SetPixelBit(x, y, p);
                if (x % 7 == 6)
                {
                    Display.SetPixelPalette(x, y, p);
                }
            }

            Display.RenderGraphics();

            if (ShouldQuit)
            {
                return;
            }

            await Delay(7);
        }

        for (int i = 38; i != 0; --i)
        {
            for (int py = 1; py < height; ++py)
            {
                for (int px = 0; px < width; px += 7)
                {
                    Display.SetPixelByte(px, py - 1, Display.GetPixelByte(px, py));
                }
            }

            Display.RenderGraphics();

            await PlayTones([new Tone(Clock / 2.0 / ((i * 4 + 1) * 10.0 + 10.0), 12.5)], false, false);

            if (ShouldQuit)
            {
                return;
            }
        }
    }

    private async Task RunIntroTitle(SeekableReadStream menu, SeekableReadStream ms2)
    {
        ms2.Seek(0x2290);
        var shapeTable = ms2.ReadStream(0x450);
        if (ms2.Eos)
        {
            throw new InvalidDataException("Failed to read shape table");
        }

        string titleString = ReadStringAt(menu, 0x1f5, (byte)'"');
        await DrawText(titleString, shapeTable, 4.0f, 22.5f);

        titleString = ReadStringAt(menu, 0x22b, (byte)'"');
        await DrawText(titleString, shapeTable, 5.0f, 24.0f);

        // "TM" drawn with lines
        Graphics.DrawLine(200, 170, 200, 174, 0x7f);
        Graphics.DrawLine(198, 170, 202, 170, 0x7f);
        Display.RenderGraphics();
        await Delay(7);
        Graphics.DrawLine(204, 170, 204, 174, 0x7f);
        Graphics.DrawLine(204, 170, 207, 173, 0x7f);
        Graphics.DrawLine(207, 173, 209, 170, 0x7f);
        Graphics.DrawLine(209, 170, 209, 174, 0x7f);
        Display.RenderGraphics();
        await Delay(7);

        titleString = ReadStringAt(menu, 0x46c);
        await DrawText(titleString, shapeTable, 20.0f - titleString.Length / 2.0f, 10.6f);

        titleString = ReadStringAt(menu, 0x490);
        await DrawText(titleString, shapeTable, 20.0f - titleString.Length / 2.0f, 11.8f);

        string[] menuStrings = [ReadStringAt(menu, 0x515), ReadStringAt(menu, 0x52b)];

        for (int i = 0; i < menuStrings.Length; ++i)
        {
            await DrawText($"{i + 1}) {menuStrings[i]}", shapeTable, 12.5f, 14.0f + i * 1.2f);
        }

        titleString = ReadStringAt(menu, 0x355, (byte)'"');
        await DrawText(titleString, shapeTable, 12.5f, 14.0f + menuStrings.Length * 1.2f + 2.0f);
    }

    private async Task RunIntroInstructions(SeekableReadStream instructions)
    {
        var line = new System.Text.StringBuilder();
        string pressKey = ReadStringAt(instructions, 0xad6, (byte)'"');
        instructions.Seek(0);

        Display.Home();
        Display.SetMode(Adl.Display.Mode.Text);

        // search for PRINT commands in the tokenized BASIC
        while (true)
        {
            byte c;

            do
            {
                c = instructions.ReadByte();

                if (instructions.Eos)
                {
                    throw new InvalidDataException("Error reading instructions file");
                }

                // GOSUB (calls the "press any key" routine)
                if (c == 0xb0)
                {
                    Display.MoveCursorTo(6, 23);
                    Display.PrintAsciiString(pressKey);
                    await InputKey();

                    if (ShouldQuit)
                    {
                        return;
                    }

                    Display.Home();
                }
            }
            while (c != 0xba); // PRINT

            int quotes = 0;
            while (true)
            {
                c = instructions.ReadByte();

                if (instructions.Eos)
                {
                    throw new InvalidDataException("Error reading instructions file");
                }

                if (c == '"')
                {
                    ++quotes;
                    continue;
                }

                if (c == 0)
                {
                    break;
                }

                if (quotes == 1)
                {
                    line.Append((char)c);
                }
                else if (c == ':')
                {
                    // separator
                    break;
                }
                else if (c == '4')
                {
                    // Ctrl-D before "RUN MENU"
                    return;
                }
            }

            line.Append('\r');
            Display.PrintAsciiString(line.ToString());
            line.Clear();
        }
    }

    private async Task RunIntroLoading(SeekableReadStream adventure)
    {
        Display.Home();
        Display.SetMode(Adl.Display.Mode.Text);

        const int stringCount = 4;
        const int stringLength = 39;
        adventure.Seek(0x2eb);

        var text = new byte[stringCount * stringLength];
        if (adventure.Read(text) < text.Length)
        {
            throw new InvalidDataException("Failed to read loading screen text");
        }

        int[] rows = [2, 19, 8, 22];
        for (int i = 0; i < stringCount; ++i)
        {
            Display.MoveCursorTo(0, rows[i]);
            Display.PrintString(Latin1(text.AsSpan(i * stringLength, stringLength)));
        }

        await WaitKey(3000);
    }
}

/// <summary>
/// The Atari 8-bit re-release on three sides, its sectors half the size of the Apple's: the Apple
/// II track and sector numbers in the data are converted. Port of ScummVM's HiRes4Engine_Atari;
/// untested, as the library has no copy.
/// </summary>
public sealed class HiRes4EngineAtari : AdlEngineV3
{
    private static readonly string[] AtariDisks = ["ulys1a", "ulys1b", "ulys2c"];

    private DiskImage _boot = null!;
    private int _curDisk;

    public HiRes4EngineAtari(IHost host, IGameContainer game, AdlDetectedGame detected, DisplayOptions options)
        : base(host, game, detected, options)
    {
        BrokenRooms.Add(121);
    }

    protected override void Init()
    {
        Graphics = new GraphicsManV2(Display);

        _boot = OpenAtariDisk(0);

        InsertAtariDisk(1);
        LoadCommonData();

        var stream = Read(_boot, 0x06, 0x2);
        Strings.VerbError = ReadStringAt(stream, 0x4f);
        Strings.NounError = ReadStringAt(stream, 0x83);
        Strings.EnterCommand = ReadStringAt(stream, 0xa6);

        stream = Read(_boot, 0x05, 0xb, 0xd7);
        StringsV2.Time = ReadString(stream, 0xff);

        stream = Read(_boot, 0x06, 0x7, 0x00, 2);
        StringsV2.SaveInsert = ReadStringAt(stream, 0x62);
        StringsV2.SaveReplace = ReadStringAt(stream, 0xdd);
        StringsV2.RestoreInsert = ReadStringAt(stream, 0x12a);
        StringsV2.RestoreReplace = ReadStringAt(stream, 0x1b8);
        Strings.PlayAgain = ReadStringAt(stream, 0x21b);

        // TODO in ScummVM too: the restart sequence has "insert side a/b" strings

        MessageIdTable.CantGoThere = 110;
        MessageIdTable.DontUnderstand = 112;
        MessageIdTable.ItemDoesntMove = 114;
        MessageIdTable.ItemNotHere = 115;
        MessageIdTable.ThanksForPlaying = 113;

        LoadItemDescriptions(Read(_boot, 0x06, 0xd, 0x12, 2), HiRes4BaseEngineConstants.NumItemDescs);
        LoadDroppedItemOffsets(Read(_boot, 0x07, 0x1, 0xf4), HiRes4BaseEngineConstants.NumItemOffsets);
        ReadCommands(Read(_boot, 0x08, 0xe, 0xa5, 5), RoomCommands);
        ReadCommands(Read(_boot, 0x0a, 0x9, 0x00, 3), GlobalCommands);
        LoadWords(Read(_boot, 0x05, 0x4, 0x00, 3), Verbs, PriVerbs);
        LoadWords(Read(_boot, 0x03, 0xb, 0x00, 6), Nouns, PriNouns);
    }

    protected override void LoadRoom(byte roomNr)
    {
        if (roomNr is >= 59 and < 113)
        {
            if (_curDisk != 2)
            {
                InsertAtariDisk(2);
                RebindDisk();
            }
        }
        else if (_curDisk != 1)
        {
            InsertAtariDisk(1);
            RebindDisk();
        }

        base.LoadRoom(roomNr);
    }

    protected override string FormatVerbError(string verb)
    {
        var err = Strings.VerbError.ToCharArray();
        for (int i = 0; i < verb.Length; ++i)
        {
            err[i + 8] = verb[i];
        }

        return new string(err);
    }

    protected override string FormatNounError(string verb, string noun)
    {
        var err = Strings.NounError.ToCharArray();
        for (int i = 0; i < verb.Length; ++i)
        {
            err[i + 8] = verb[i];
        }

        for (int i = 0; i < noun.Length; ++i)
        {
            err[i + 19] = noun[i];
        }

        return new string(err);
    }

    protected override void InitGameState()
    {
        ResizeVars(_state.Vars, HiRes4BaseEngineConstants.NumVars);

        LoadRooms(Read(_boot, 0x03, 0x1, 0x0e, 9), HiRes4BaseEngineConstants.NumRooms);
        LoadItems(Read(_boot, 0x02, 0xc, 0x00, 12));

        // FIXME in ScummVM too
        Display.MoveCursorTo(0, 23);
    }

    protected override void AdjustDataBlockPtr(ref byte track, ref byte sector, ref byte offset, ref byte size)
    {
        // convert the game's Apple II disk offsets to Atari ones
        int sectorIndex = (track * 16 + sector + 1) << 1;

        // the Atari has 128 bytes per sector against 256 on the Apple II; size counts the
        // additional sectors after the first one
        size = (byte)(size * 2);

        if (offset >= 128)
        {
            // an offset in the second half of an Apple II sector: skip one sector
            ++sectorIndex;
            offset -= 128;
        }
        else
        {
            // in the first half, one more sector has to be read
            ++size;
        }

        // the Atari's 18 sectors per track, sectorIndex being 1-based
        track = (byte)((sectorIndex - 1) / 18);
        sector = (byte)((sectorIndex - 1) % 18);
    }

    private DiskImage OpenAtariDisk(int index)
    {
        string name = Description.Files.TryGetValue(AtariDisks[index], out var file) ? file : AtariDisks[index] + ".xfd";
        return DiskImage.Open(Game, name, Host.Log) ?? throw new InvalidDataException($"Failed to open disk image '{name}'");
    }

    private SeekableReadStream Read(DiskImage disk, byte track, byte sector, byte offset = 0, byte size = 0)
    {
        AdjustDataBlockPtr(ref track, ref sector, ref offset, ref size);
        return disk.CreateReadStream(track, sector, offset, size) ?? throw new InvalidDataException("Error reading disk image");
    }

    private void InsertAtariDisk(int diskNr)
    {
        if (_curDisk == diskNr)
        {
            return;
        }

        _curDisk = diskNr;
        Disk = OpenAtariDisk(diskNr);
    }

    private void RebindDisk()
    {
        // the rooms' data are bound to the disk image, so they are bound again here; reloading
        // the rooms would reset their state
        var stream = Read(_boot, 0x03, 0x1, 0x0e, 9);
        for (int i = 0; i < HiRes4BaseEngineConstants.NumRooms; ++i)
        {
            stream.Skip(7);
            _state.Rooms[i].Data = ReadDataBlockPtr(stream);
            stream.Skip(3);
        }

        // data that is on both side B and C
        LoadCommonData();
    }

    private void LoadCommonData()
    {
        Messages.Clear();
        LoadMessages(Read(_boot, 0x0a, 0x4, 0x00, 3), HiRes4BaseEngineConstants.NumMessages);

        Pictures.Clear();
        LoadPictures(Read(_boot, 0x05, 0xe, 0x80));

        ItemPics.Clear();
        LoadItemPictures(Read(_boot, 0x09, 0xe, 0x05), HiRes4BaseEngineConstants.NumItemPics);
    }
}

/// <summary>Ulysses' table sizes, shared by the Apple II and Atari engines.</summary>
internal static class HiRes4BaseEngineConstants
{
    public const int NumRooms = 164;
    public const int NumMessages = 255;
    public const int NumVars = 40;
    public const int NumItemDescs = 44;
    public const int NumItemPics = 41;
    public const int NumItemOffsets = 16;
}
