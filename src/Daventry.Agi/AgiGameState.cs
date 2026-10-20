using System.Text;
using Daventry.Agi.Resources;

namespace Daventry.Agi;

public enum ImageStackCall
{
    AddPicture = 1,
    AddView = 2,
}

/// <summary>One recorded drawing call, replayed to rebuild the screen after restoring a game. Mirrors ScummVM's ImageStackElement.</summary>
public readonly record struct ImageStackElement(ImageStackCall Type, int Parameter1, int Parameter2, int Parameter3, int Parameter4, int Parameter5, int Parameter6, int Parameter7);

/// <summary>Reserved VM variable numbers. Mirrors ScummVM's VM_VAR_* enum.</summary>
public static class AgiVar
{
    public const int CurrentRoom = 0;
    public const int PreviousRoom = 1;
    public const int BorderTouchEgo = 2;
    public const int Score = 3;
    public const int BorderCode = 4;
    public const int BorderTouchObject = 5;
    public const int EgoDirection = 6;
    public const int MaxScore = 7;
    public const int FreePages = 8;
    public const int WordNotFound = 9;
    public const int TimeDelay = 10;
    public const int Seconds = 11;
    public const int Minutes = 12;
    public const int Hours = 13;
    public const int Days = 14;
    public const int JoystickSensitivity = 15;
    public const int EgoViewResource = 16;
    public const int AgiErrorCode = 17;
    public const int AgiErrorInfo = 18;
    public const int Key = 19;
    public const int Computer = 20;
    public const int WindowAutoCloseTimer = 21;
    public const int SoundGenerator = 22;
    public const int Volume = 23;
    public const int MaxInputCharacters = 24;
    public const int SelectedInventoryItem = 25;
    public const int Monitor = 26;
    public const int MouseButtonState = 27;
    public const int MouseX = 28;
    public const int MouseY = 29;
}

/// <summary>Reserved VM flag numbers. Mirrors ScummVM's VM_FLAG_* enum.</summary>
public static class AgiFlag
{
    public const int EgoWater = 0;
    public const int EgoInvisible = 1;
    public const int EnteredCli = 2;
    public const int EgoTouchedP2 = 3;
    public const int SaidAcceptedInput = 4;
    public const int NewRoomExec = 5;
    public const int RestartGame = 6;
    public const int ScriptBlocked = 7;
    public const int JoySensitivity = 8;
    public const int SoundOn = 9;
    public const int DebuggerOn = 10;
    public const int LogicZeroFirstTime = 11;
    public const int RestoreJustRan = 12;
    public const int StatusSelectsItems = 13;
    public const int MenusAccessible = 14;
    public const int OutputMode = 15;
    public const int AutoRestart = 16;
}

/// <summary>The rectangle set by the block command. Mirrors ScummVM's AgiBlock.</summary>
public sealed class AgiBlock
{
    public bool Active;
    public int X1;
    public int Y1;
    public int X2;
    public int Y2;
}

/// <summary>A key mapped to a controller slot by set.key. Mirrors AgiControllerKeyMapping.</summary>
public struct AgiControllerKeyMapping
{
    public ushort KeyCode;
    public byte ControllerSlot;
}

public readonly record struct ScriptPos(int Script, int CurIP);

/// <summary>
/// Interpreter state shared between subsystems. Mirrors ScummVM's AgiGame struct.
/// </summary>
public sealed class AgiGameState
{
    public const int MaxVars = 256;
    public const int MaxFlags = 256;
    public const int MaxControllers = 256;
    public const int MaxControllerKeyMappings = 39;
    public const int MaxStrings = 24;
    public const int MaxStringLength = 40;
    public const int SavedGameDescriptionLength = 30;

    /// <summary>Mirrors SCREENOBJECTS_MAX. KQ3 uses object 255, hence 255 entries plus the add.to.pic view.</summary>
    public const int ScreenObjectsMax = 255;

    public const int ScreenObjectsEgoEntry = 0;

    private readonly List<ImageStackElement> _imageStack = [];
    private readonly string[] _strings = new string[MaxStrings + 1];

    public AgiGameState()
    {
        ScreenObjTable = new ScreenObjEntry[ScreenObjectsMax];
        for (int i = 0; i < ScreenObjTable.Length; i++)
        {
            ScreenObjTable[i] = new ScreenObjEntry { ObjectNr = i };
        }

        Array.Fill(_strings, string.Empty);
    }

    /// <summary>Last adj.ego.move.to.x.y parameters.</summary>
    public int AdjMouseX;
    public int AdjMouseY;

    /// <summary>Game id set by set.game.id.</summary>
    public string Id = string.Empty;

    public byte[] Vars { get; } = new byte[MaxVars];

    public bool[] Flags { get; } = new bool[MaxFlags];

    /// <summary>Horizon y coordinate.</summary>
    public int Horizon;

    public bool CycleInnerLoopActive;
    public CycleInnerLoopType CycleInnerLoopType = CycleInnerLoopType.None;

    public int CurLogicNr;
    public AgiLogic? CurLogic;
    public List<ScriptPos> ExecStack { get; } = [];

    public bool PlayerControl;

    /// <summary>Break the cycle after new.room.</summary>
    public bool ExitAllLogics;

    /// <summary>True after show.pic, false after a new picture is drawn.</summary>
    public bool PictureShown;

    public AgiBlock Block { get; } = new();

    /// <summary>True once the game has switched from text mode to graphics mode.</summary>
    public bool GfxMode = true;

    public bool[] ControllerOccurred { get; } = new bool[MaxControllers];

    public AgiControllerKeyMapping[] ControllerKeyMapping { get; } = new AgiControllerKeyMapping[MaxControllerKeyMappings];

    public ScreenObjEntry[] ScreenObjTable { get; }

    public ScreenObjEntry AddToPicView { get; } = new() { ObjectNr = -1 };

    public bool AutomaticSave;
    public string AutomaticSaveDescription = string.Empty;

    public Core.Graphics.Rect MouseFence;
    public bool MouseEnabled = true;
    public bool MouseHidden;

    /// <summary>Result of the last test opcode, consumed by if-condition evaluation.</summary>
    public bool TestResult;

    public int MaxLogics;
    public int[] LogicList { get; } = new int[256];

    /// <summary>
    /// Set when non-blocking text was shown. Rooms that change right after showing text (Space
    /// Quest 2 intro) relied on slow loading; a short delay is inserted so the text stays readable.
    /// </summary>
    public bool NonBlockingTextShown;
    public int NonBlockingTextCyclesLeft;

    /// <summary>True while a saved game is being restored automatically, which suppresses transitions.</summary>
    public bool AutomaticRestoreGame;

    /// <summary>Game speed for platforms and versions without a time delay variable. 2 is normal.</summary>
    public byte SpeedLevel = 2;

    public IReadOnlyList<ImageStackElement> ImageStack => _imageStack;

    public void ClearImageStack() => _imageStack.Clear();

    public void RecordImageStackCall(ImageStackCall type, int p1, int p2, int p3, int p4, int p5, int p6, int p7)
        => _imageStack.Add(new ImageStackElement(type, p1, p2, p3, p4, p5, p6, p7));

    public string GetString(int number)
    {
        if (number >= 0 && number <= MaxStrings)
        {
            return _strings[number];
        }

        // string 56 is the interpreter version in some scripts
        return number == 56 ? ".917" : string.Empty;
    }

    public void SetString(int number, string value)
    {
        if (number >= 0 && number <= MaxStrings)
        {
            _strings[number] = value.Length >= MaxStringLength ? value[..(MaxStringLength - 1)] : value;
        }
    }

    public void ClearStrings() => Array.Fill(_strings, string.Empty);
}
