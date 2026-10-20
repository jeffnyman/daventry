namespace Daventry.Agi;

/// <summary>
/// Key codes as the AGI scripts see them: ASCII for characters, IBM PC scancode in the high byte
/// for special keys. Mirrors ScummVM's AGI_KEY_* constants.
/// </summary>
public static class AgiKey
{
    public const ushort Backspace = 0x08;
    public const ushort Escape = 0x1B;
    public const ushort Enter = 0x0D;
    public const ushort Up = 0x4800;
    public const ushort Down = 0x5000;
    public const ushort Left = 0x4B00;
    public const ushort Stationary = 0x4C00;
    public const ushort Right = 0x4D00;
    public const ushort DownLeft = 0x4F00;
    public const ushort DownRight = 0x5100;
    public const ushort UpLeft = 0x4700;
    public const ushort UpRight = 0x4900;
    public const ushort F1 = 0x3B00;
    public const ushort F2 = 0x3C00;
    public const ushort F3 = 0x3D00;
    public const ushort F4 = 0x3E00;
    public const ushort F5 = 0x3F00;
    public const ushort F6 = 0x4000;
    public const ushort F7 = 0x4100;
    public const ushort F8 = 0x4200;
    public const ushort F9 = 0x4300;
    public const ushort F10 = 0x4400;
    public const ushort F11 = 0xD900;
    public const ushort F12 = 0xDA00;
    public const ushort PageUp = 0x4900;
    public const ushort PageDown = 0x5100;
    public const ushort Home = 0x4700;
    public const ushort End = 0x4F00;
    public const ushort MouseButtonLeft = 0xF101;
    public const ushort MouseButtonRight = 0xF202;
    public const ushort MouseWheelUp = 0xF203;
    public const ushort MouseWheelDown = 0xF204;

    /// <summary>Menu triggers per platform. Mixed Up Mother Goose on the Apple IIgs hooks ESC for the menu itself.</summary>
    public const ushort MenuTriggerPc = 0x001B;
    public const ushort MenuTriggerApple2Gs = 0x0301;
    public const ushort MenuTriggerAtariSt = 0x0101;

    /// <summary>IBM PC keyboard scancodes for A to Z, used for Alt+letter controllers.</summary>
    public static readonly byte[] ScancodeTable =
    [
        30, 48, 46, 32, 18, 33, 34, 35, 23, 36, 37, 38, 50,
        49, 24, 25, 16, 19, 31, 20, 22, 47, 17, 45, 21, 44,
    ];
}

public enum AgiMouseButton
{
    Up = 0,
    Left = 1,
    Right = 2,
}

/// <summary>Current mouse state in display coordinates. Mirrors ScummVM's Mouse struct.</summary>
public sealed class AgiMouse
{
    public AgiMouseButton Button;
    public int X;
    public int Y;
}

/// <summary>Which blocking UI the interpreter is currently inside. Mirrors CYCLE_INNERLOOP_*.</summary>
public enum CycleInnerLoopType
{
    None = -1,
    GetString = 0,
    GetNumber,
    Inventory,
    MenuViaKeyboard,
    MenuViaMouse,
    SystemUiSelectSavedGameSlot,
    SystemUiVerification,
    MessageBox,
    HaveKey,
}

/// <summary>Mirrors the computer and sound generator type values reported to scripts through variables 20 and 22.</summary>
public static class AgiComputerType
{
    public const byte Pc = 0;
    public const byte AtariSt = 4;
    public const byte Amiga = 5;
    public const byte Apple2Gs = 7;
    public const byte Apple2 = 8;
}

public static class AgiSoundType
{
    public const byte Pc = 1;
    public const byte Tandy = 3;
    public const byte Apple2GsOld = 4;
}

public static class AgiMonitorType
{
    public const byte Cga = 0;
    public const byte Ega = 3;
    public const byte Hercules = 4;
}
