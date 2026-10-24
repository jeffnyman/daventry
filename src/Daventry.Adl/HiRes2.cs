using Daventry.Adl.Detection;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;

namespace Daventry.Adl;

/// <summary>
/// The 13-sector ADL v2 games, whose data sit at fixed tracks and sectors: Hi-Res Adventure #0:
/// Mission Asteroid, #2: Wizard and the Princess and #3: Cranston Manor. Port of ScummVM's
/// HiResBaseEngine (which runs Mission Asteroid on its own), HiRes2Engine and HiRes3Engine.
/// </summary>
public class HiResBaseEngine : AdlEngineV2
{
    private readonly int _numRooms, _numMsgs, _numItemPics;

    public HiResBaseEngine(IHost host, IGameContainer game, AdlDetectedGame detected, DisplayOptions options, int numRooms = 43, int numMsgs = 142, int numItemPics = 2)
        : base(host, game, detected, options)
    {
        _numRooms = numRooms;
        _numMsgs = numMsgs;
        _numItemPics = numItemPics;

        MessageIdTable.CantGoThere = 110;
        MessageIdTable.DontUnderstand = 112;
        MessageIdTable.ItemDoesntMove = 114;
        MessageIdTable.ItemNotHere = 115;
        MessageIdTable.ThanksForPlaying = 113;
    }

    protected override void Init()
    {
        Graphics = new GraphicsManV2(Display);

        Disk = OpenDisk(0);
        Disk.SectorLimit = 13;

        var stream = Read(0x1f, 0x2, 0x00, 4);
        LoadMessages(stream, _numMsgs);

        stream = Read(0x19, 0x0, 0x00, 25, 13);
        var exeStrings = new List<string>();
        ExtractExeStrings(stream, 0x1566, exeStrings);
        MapExeStrings(exeStrings);

        // early versions differ slightly; later ones have two more strings for "INIT DISK"
        bool oldEngine = exeStrings.Count < 13;

        if (!oldEngine)
        {
            stream = Read(0x19, 0x7, 0xd7);
            StringsV2.Time = ReadString(stream, 0xff);
        }

        // global picture data
        stream = Read(0x19, 0xa, 0x80, 0);
        LoadPictures(stream);

        // item picture data
        stream = Read(0x1e, 0x9, 0x05);
        LoadItemPictures(stream, _numItemPics);

        // commands from the executable
        stream = Read(0x1d, 0x7, 0x00, 4);
        ReadCommands(stream, RoomCommands);

        stream = Read(oldEngine ? 0x19 : 0x1f, 0x7, 0x00, 3);
        ReadCommands(stream, GlobalCommands);

        // dropped item offsets
        stream = Read(0x1b, 0x4, 0x15);
        LoadDroppedItemOffsets(stream, 16);

        // verbs
        stream = Read(0x19, 0x0, 0x00, 3);
        LoadWords(stream, Verbs, PriVerbs);

        // nouns
        stream = Read(0x22, 0x2, 0x00, 7);
        LoadWords(stream, Nouns, PriNouns);
    }

    protected override void InitGameState()
    {
        ResizeVars(_state.Vars, 40);

        var stream = Read(0x21, 0x5, 0x0e, 7);
        LoadRooms(stream, _numRooms);

        stream = Read(0x21, 0x0, 0x00, 2);
        LoadItems(stream);
    }

    protected Core.Formats.SeekableReadStream Read(int track, int sector, int offset = 0, int size = 0, int sectorLimit = 0)
        => Disk!.CreateReadStream(track, sector, offset, size, sectorLimit)
            ?? throw new InvalidDataException($"Error reading disk image at track {track}, sector {sector}");
}

/// <summary>Hi-Res Adventure #2: Wizard and the Princess (1980).</summary>
public sealed class HiRes2Engine : HiResBaseEngine
{
    public HiRes2Engine(IHost host, IGameContainer game, AdlDetectedGame detected, DisplayOptions options)
        : base(host, game, detected, options, 135, 255, 38)
    {
        MessageIdTable.CantGoThere = 123;
        MessageIdTable.DontUnderstand = 19;
        MessageIdTable.ItemDoesntMove = 242;
        MessageIdTable.ItemNotHere = 4;
        MessageIdTable.ThanksForPlaying = 239;
    }

    protected override async Task RunIntro()
    {
        // only the Green Valley version has a title screen
        if (Disk!.SectorsPerTrack != 16)
        {
            return;
        }

        var stream = Read(0x00, 0xd, 0x17, 1);

        Display.SetMode(Adl.Display.Mode.Text);

        string str = ReadString(stream);
        Display.PrintString(str);
        await Delay(2000);
    }
}

/// <summary>Hi-Res Adventure #3: Cranston Manor (1981), with a few rooms whose data the game never had.</summary>
public sealed class HiRes3Engine : HiResBaseEngine
{
    public HiRes3Engine(IHost host, IGameContainer game, AdlDetectedGame detected, DisplayOptions options)
        : base(host, game, detected, options, 138, 255, 36)
    {
        BrokenRooms.AddRange([18, 24, 54, 98, 102, 108]);
    }
}
