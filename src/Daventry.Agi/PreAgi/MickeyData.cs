namespace Daventry.Agi.PreAgi;

/// <summary>The constants of Mickey's Space Adventure: file names, offsets into mickey.exe, rooms, objects. Port of mickey.h.</summary>
internal static class MickeyData
{
    public const int SaveGameVersion = 2;

    // strings
    public const string PathDat = "dat/{0}";
    public const string PathObj = "obj/{0}.ooo";
    public const string PathPic = "{0}.pic";
    public const string PathLogo = "logos.bcg";
    public const string Inventory = "MICKEY IS CARRYING THE FOLLOWING:";
    public const string CrystalsFormat = "{0} CRYSTALS";

    public static readonly string[] CrystalNo = ["NO", " 1", " 2", " 3", " 4", " 5", " 6", " 7", " 8", " 9"];
    public static readonly string[] TempC = [" 20 ", " 480", "-200", " 430", "-185", "-230", "-130", "-150", "-215"];
    public static readonly string[] TempF = [" 68 ", " 897", "-328", " 807", "-301", "-382", "-202", "-238", "-355"];
    public static readonly string[] Planets = ["EARTH.  ", "VENUS.  ", "TRITON. ", "MERCURY.", "TITAN.  ", "PLUTO.  ", "IO.     ", "MARS.   ", "OBERON. "];

    public static readonly string[] Errors =
    [
        "THAT CANNOT BE UNDERSTOOD",
        "TRY GOING THERE INSTEAD",
        "THAT CAN'T BE DONE",
        "MICKEY WOULDN'T WANT TO DO THAT!",
        "WHICH DIRECTION?",
        "THAT DOESN'T MAKE SENSE!",
        "MICKEY WOULDN'T WANT TO DO THAT!",
    ];

    public static readonly string[] InsertDisk = ["Please insert disk 1 and press any key", "Please insert disk 2 and press any key"];

    // max values
    public const int MaxPlanet = 9;
    public const int MaxDat = 10;
    public const int MaxRoom = 160;
    public const int MaxButton = 6;
    public const int MaxItem = 11;
    public const int AnimDelay = 25;
    public const int LenStory = 1372;

    // rows and columns
    public const int RowMenu0 = 20;
    public const int RowMenu1 = 21;
    public const int RowInvTitle = 2;
    public const int RowInvCrystals = 4;
    public const int RowInvItems = 5;
    public const int RowPlanet = 22;
    public const int RowInsertDisk = 23;
    public const int ColInvTitle = 4;
    public const int ColInvItems = 15;
    public const int ColPlanet = 28;
    public const int ColInsertDisk = 1;

    // screen
    public const int PicWidth = 140;
    public const int PicHeight = 159;
    public const int PicX0 = 10;
    public const int PicY0 = 0;

    // pictures
    public const int PicEarthTireSwing1 = 200;
    public const int PicEarthInDoghouse = 154;
    public const int PicEarthRoad4 = 13;
    public const int PicEarthUpInTree = 155;
    public const int PicEarthShip = 17;
    public const int PicEarthKitchen1 = 159;
    public const int PicEarthGarage1 = 160;
    public const int PicEarthBedroom = 22;
    public const int PicEarthBedroom1 = 161;
    public const int PicEarthBathroom = 23;
    public const int PicEarthShipLeaving = 24;
    public const int PicEarthMinnie = 25;
    public const int PicEarthMailbox = 8;
    public const int PicShipAirlock = 25;
    public const int PicShipAirlock0 = 201;
    public const int PicShipBedroom = 29;
    public const int PicShipControls = 26;
    public const int PicShipCorridor = 27;
    public const int PicShipKitchen1 = 172;
    public const int PicShipVenus = 146;
    public const int PicShipNeptune = 147;
    public const int PicShipMercury = 148;
    public const int PicShipSaturn = 149;
    public const int PicShipPluto = 150;
    public const int PicShipJupiter = 151;
    public const int PicShipMars = 152;
    public const int PicShipUranus = 153;
    public const int PicVenusChasm1 = 183;
    public const int PicVenusProbe = 39;
    public const int PicVenusProbe1 = 184;
    public const int PicVenusShip = 33;
    public const int PicVenusWeigh = 37;
    public const int PicNeptuneCastle4 = 59;
    public const int PicNeptuneEntrance1 = 185;
    public const int PicNeptuneEntryway = 63;
    public const int PicNeptuneGuard = 69;
    public const int PicNeptuneLeader = 64;
    public const int PicNeptuneShip = 49;
    public const int PicNeptuneWeigh = 41;
    public const int PicMercuryAlien0 = 72;
    public const int PicMercuryCave0 = 70;
    public const int PicMercuryShip = 76;
    public const int PicMercuryWeigh = 82;
    public const int PicSaturnIsland = 89;
    public const int PicSaturnLake0 = 85;
    public const int PicSaturnLake1 = 88;
    public const int PicSaturnLake2 = 92;
    public const int PicSaturnShip = 87;
    public const int PicSaturnWeigh = 83;
    public const int PicPlutoAliens = 100;
    public const int PicPlutoShip = 95;
    public const int PicPlutoWeigh = 94;
    public const int PicJupiterLava = 110;
    public const int PicJupiterRock0 = 112;
    public const int PicJupiterRock1 = 119;
    public const int PicJupiterShip = 115;
    public const int PicJupiterWeigh = 118;
    public const int PicMarsShip = 123;
    public const int PicMarsTube1 = 131;
    public const int PicMarsVolcano = 132;
    public const int PicMarsWeigh = 122;
    public const int PicUranusChamber = 145;
    public const int PicUranusShip = 137;
    public const int PicUranusSteps = 144;
    public const int PicUranusTemple = 143;
    public const int PicUranusTemple1 = 206;
    public const int PicUranusTemple2 = 207;
    public const int PicUranusWeigh = 136;
    public const int PicStarMap = 165;
    public const int PicTitle = 240;

    // objects
    public const int ObjectNone = -1;
    public const int ObjectWEarth = 15;
    public const int ObjectWSpace = 24;
    public const int ObjectXl31 = 25;
    public const int ObjectCrystal = 31;

    public static readonly string[] ObjectNames =
    [
        "rok1", "wrench", "scale", "cbar", "bone", "glasses", "deskstuf", "raft",
        "scarf", "flashlit", "rope", "rok1", "scarfc64", "rok2", "rock35", "earthw",
        "venw", "trw", "merw", "titw", "plw", "iow", "mrw", "obw", "spw", "xl31",
        "xl31e", "xl32", "xl32e", "xl33", "xl33e", "crys1",
    ];

    /// <summary>The room, x and y of each planet's crystal.</summary>
    public static readonly int[,] XtalRoomXY =
    {
        { PicEarthUpInTree, 14, 76 },
        { PicVenusProbe, 74, 80 },
        { PicNeptuneLeader, 70, 27 },
        { PicMercuryAlien0, 123, 64 },
        { PicSaturnIsland, 110, 115 },
        { PicPlutoAliens, 60, 104 },
        { PicJupiterLava, 56, 54 },
        { PicMarsVolcano, 107, 100 },
        { PicUranusChamber, 90, 4 },
    };

    // planets
    public const int PlanetEarth = 0;
    public const int PlanetVenus = 1;
    public const int PlanetUranus = 8;
    public const int PlanetSpaceship = 9;

    public static readonly string[] DatNames =
    [
        "earth.dat", "venus.dat", "neptune.dat", "mercury.dat", "saturn.dat",
        "pluto.dat", "jupiter.dat", "mars.dat", "uranus.dat", "spacship.dat",
    ];

    public static readonly string[] PlanetNames = ["EARTH", "VENUS", "TRITON", "MERCURY", "TITAN", "PLUTO", "IO", "MARS", "OBERON"];

    public static readonly string[] PlanetAddresses = ["OB", "B", "OOBBB", "O", "OOBB", "OOOBBB", "OBB", "OOB", "OOOBB"];

    public static readonly int[] HomePlanet =
    [
        PicEarthShip, PicVenusShip, PicNeptuneShip, PicMercuryShip,
        PicSaturnShip, PicPlutoShip, PicJupiterShip, PicMarsShip, PicUranusShip,
    ];

    public static readonly int[] ShipPlanet =
    [
        0, PicShipVenus, PicShipNeptune, PicShipMercury, PicShipSaturn,
        PicShipPluto, PicShipJupiter, PicShipMars, PicShipUranus,
    ];

    // items
    public const int ItemFlashlight = 0;
    public const int ItemRope = 1;
    public const int ItemBone = 2;
    public const int ItemLetter = 3;
    public const int ItemCrowbar = 4;
    public const int ItemWrench = 5;
    public const int ItemMattress = 6;
    public const int ItemScarf = 7;
    public const int ItemSunglasses = 8;
    public const int ItemScale = 9;
    public const int ItemRock = 10;

    public static readonly string[] ItemNames =
    [
        "A FLASHLIGHT", "A ROPE ", "A BONE ", "A LETTER", "A CROWBAR", "A WRENCH",
        "A MATTRESS", "A SCARF", "SUNGLASSES", "A SCALE ", "A ROCK ",
    ];

    // buttons
    public const char ButtonOrange = 'O';
    public const char ButtonBlue = 'B';

    // file offset modifiers
    public const int OfsDat = 0x0002;
    public const int OfsExe = 0x35C0;

    // actions
    public const int ActionGotoRoom = 0x00;
    public const int ActionShowIntStr = 0x01;
    public const int ActionShowDatStr = 0x03;
    public const int ActionGetRope = 0x7F;
    public const int ActionUntieRope = 0x80;
    public const int ActionGetBone = 0x81;
    public const int ActionGetXtalEarth = 0x82;
    public const int ActionLookDesk = 0x83;
    public const int ActionWriteLetter = 0x84;
    public const int ActionMailLetter = 0x85;
    public const int ActionOpenCupboard = 0x86;
    public const int ActionGetFlashlight = 0x87;
    public const int ActionOpenCabinet = 0x88;
    public const int ActionGetCrowbar = 0x89;
    public const int ActionGetWrench = 0x8A;
    public const int ActionOpenCloset = 0x8B;
    public const int ActionGetMattress = 0x8C;
    public const int ActionGetScarf = 0x8D;
    public const int ActionGetSunglasses = 0x8E;
    public const int ActionGetScale = 0x8F;
    public const int ActionGotoSpaceship = 0x90;
    public const int ActionDownChasm = 0x91;
    public const int ActionDownRope = 0x92;
    public const int ActionUseRope = 0x93;
    public const int ActionOpenHatch = 0x94;
    public const int ActionUseWrench = 0x95;
    public const int ActionGetXtalVenus = 0x96;
    public const int ActionLookCastle = 0x97;
    public const int ActionEnterOpening = 0x98;
    public const int ActionUseCrowbar = 0x99;
    public const int ActionGetXtalNeptune = 0x9A;
    public const int ActionTalkLeader = 0x9B;
    public const int ActionGiveScarf = 0x9C;
    public const int ActionGetXtalMercury = 0x9D;
    public const int ActionGiveSunglasses = 0x9E;
    public const int ActionCrossLake = 0x9F;
    public const int ActionUseMattress = 0xA0;
    public const int ActionGetXtalSaturn = 0xA1;
    public const int ActionLeaveIsland = 0xA2;
    public const int ActionGetXtalPluto = 0xA3;
    public const int ActionGiveBone = 0xA4;
    public const int ActionGetRock0 = 0xA5;
    public const int ActionGetRock1 = 0xA6;
    public const int ActionGetXtalJupiter = 0xA7;
    public const int ActionThrowRock = 0xA8;
    public const int ActionGoTube = 0xA9;
    public const int ActionUseFlashlight = 0xAA;
    public const int ActionPlutoDig = 0xAB;
    public const int ActionGetXtalMars = 0xAC;
    public const int ActionUseCrystal = 0xAD;
    public const int ActionOpenDoor = 0xAE;
    public const int ActionEnterDoor = 0xAF;
    public const int ActionGetXtalUranus = 0xB0;
    public const int ActionUseCrowbar1 = 0xB1;
    public const int ActionGoNorth = 0xB2;
    public const int ActionGoPlanet = 0xB3;
    public const int ActionPressButton = 0xB4;
    public const int ActionWearSpacesuit = 0xB5;
    public const int ActionReadGauge = 0xB6;
    public const int ActionPressOrange = 0xB7;
    public const int ActionPressBlue = 0xB8;
    public const int ActionFlipSwitch = 0xB9;
    public const int ActionPushThrottle = 0xBA;
    public const int ActionPullThrottle = 0xBB;
    public const int ActionLeaveRoom = 0xBC;
    public const int ActionOpenCabinet1 = 0xBD;
    public const int ActionReadMap = 0xBE;
    public const int ActionGoWest = 0xBF;
    public const int ActionPlanetInfo = 0xC0;
    public const int ActionEnterTemple = 0xC1;
    public const int ActionOpenMailbox = 0xC2;
    public const int ActionSaveGame = 0xC3;
    public const int ActionLookMickey = 0xC4;

    // sounds
    public const int SndTheme = 0;
    public const int SndCrystal = 1;
    public const int SndTake = 2;
    public const int SndGameOver = 3;
    public const int SndPressBlue = 4;
    public const int SndPressOrange = 5;
    public const int SndShipLand = 6;
    public const int SndXl30 = 7;

    // message offsets within mickey.exe
    public static readonly int[] HiddenMsg = [0x8C44, 0x8C83, 0x8D23, 0x8D97, 0x8E2A];
    public static readonly int[] GameOverMsg = [0x7914, 0x7978, 0x7A17, 0x7A94, 0x7B04, 0x7B8F, 0x7BEB, 0x7C79];

    /// <summary>Do you have a formatted disk, insert disk, insert disk 2, save by number, everything will be lost, previous game will be lost, game saved.</summary>
    public static readonly int[] SaveGameMsg = [0x73FA, 0x7436, 0x746C, 0x74E9, 0x75F6, 0x766A, 0x758B];

    /// <summary>Do you want to load game, insert game save disk, game restored.</summary>
    public static readonly int[] LoadGameMsg = [0x76CE, 0x770B, 0x7777];

    /// <summary>Be aware, low, dangerously low, out of air.</summary>
    public static readonly int[] AirSupplyMsg = [0x7D10, 0x7D31, 0x7D51, 0x7D9B];
    public static readonly int[] AirSupply = [30, 20, 10, 0];

    public static readonly int[,] PlanetInfo =
    {
        { 0x6313, 0x63B2, 0x6449, 0 }, // EARTH
        { 0x61EB, 0x6288, 0, 0 }, // VENUS
        { 0x6B64, 0x6C06, 0x6CA3, 0 }, // NEPTUNE
        { 0x609B, 0x612C, 0x61CA, 0 }, // MERCURY
        { 0x6879, 0x6916, 0x6984, 0 }, // SATURN
        { 0x6CCF, 0x6D72, 0x6E10, 0 }, // PLUTO
        { 0x667C, 0x6714, 0x67B1, 0x684E }, // JUPITER
        { 0x6471, 0x650F, 0x65AD, 0x6651 }, // MARS
        { 0x69C3, 0x6A62, 0x6B00, 0 }, // URANUS
    };

    /// <summary>Next crystal piece hints, five per planet.</summary>
    public static readonly int[,] NextPiece =
    {
        { 0, 0, 0, 0, 0 }, // earth
        { 0x4DCC, 0x4E20, 0x4E64, 0x4E9E, 0x4F0B }, // venus
        { 0x5900, 0x599B, 0x5A07, 0x5A8E, 0x5B07 }, // neptune
        { 0x4F57, 0x4FA3, 0x4FF1, 0x5056, 0x50BD }, // mercury
        { 0x5471, 0x54DF, 0x5548, 0x55C2, 0x562A }, // saturn
        { 0x5B78, 0x5BB6, 0x5C29, 0x5C76, 0x5CE1 }, // pluto
        { 0x526B, 0x52DA, 0x5340, 0x53A1, 0x540C }, // jupiter
        { 0x50F6, 0x512C, 0x5170, 0x51D5, 0x5228 }, // mars
        { 0x56AA, 0x571C, 0x579E, 0x5807, 0x5875 }, // uranus
    };

    public const int MsgCopyright = 0x7801;
    public const int MsgIntro = 0x4679;
    public const int MsgGameStory = 0x6E9C;
    public const int MsgPress1To9 = 0x7530;
    public const int MsgPressYesOrNo = 0x480D;
    public const int MsgMickeyHasPressed = 0x5D90;
    public const int MsgTooManyButtonsPressed = 0x5DF7;
    public const int MsgXl30Speaking = 0x4725;
    public const int MsgCrystalPieceFound = 0x600C;
    public const int MsgRoomTextOffsets = 0x8B01;
    public const int MsgRoomObjectXYOffsets = 0x8EA8;
    public const int MsgRoomMenuFix = 0x4a27;

    // offsets to offset arrays
    public const int OfsMenuPatches = 0x5e7a;
    public const int OfsSoundData = 0x9deb;
}

/// <summary>
/// A room's menu as the .dat files store it: two rows of up to five words with their columns,
/// then a command and an argument for every pair of words. Port of MSA_MENU, kept as the raw
/// 172 bytes because the executable patches it by byte offset.
/// </summary>
internal sealed class MickeyMenu
{
    public const int Size = 172;
    private const int EntrySize = 12;
    private const int RowSize = 1 + 5 * EntrySize;
    private const int CmdOffset = 2 * RowSize;
    private const int ArgOffset = CmdOffset + 25;

    public MickeyMenu(byte[] data)
    {
        Data = new byte[Size];
        data.AsSpan(0, Math.Min(data.Length, Size)).CopyTo(Data);
    }

    public byte[] Data { get; }

    public int Count(int row) => Math.Min((int)Data[row * RowSize], 5);

    public int X0(int row, int word) => Data[row * RowSize + 1 + word * EntrySize];

    public void SetX0(int row, int word, int x) => Data[row * RowSize + 1 + word * EntrySize] = (byte)x;

    public string Text(int row, int word)
    {
        int start = row * RowSize + 2 + word * EntrySize;
        int end = start;
        while (end < start + 11 && Data[end] != 0)
        {
            end++;
        }

        var chars = new char[end - start];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)Data[start + i];
        }

        return new string(chars);
    }

    public void SetText(int row, int word, string text)
    {
        int start = row * RowSize + 2 + word * EntrySize;
        for (int i = 0; i < 11; i++)
        {
            Data[start + i] = i < text.Length ? (byte)text[i] : (byte)0;
        }
    }

    public int Cmd(int sel0, int sel1) => Data[CmdOffset + sel0 * 5 + sel1];

    public int Arg(int sel0, int sel1) => Data[ArgOffset + sel0 * 5 + sel1];
}
