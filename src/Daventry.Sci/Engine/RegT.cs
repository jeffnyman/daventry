namespace Daventry.Sci.Engine;

/// <summary>
/// A VM register: a segment and an offset. Numbers have segment 0; pointers name a segment of
/// the segment manager. Mirrors ScummVM's reg_t. SCI3 scripts reach 256 KB, so offsets carry 18
/// bits (ScummVM keeps the top two in the segment word, as the saved games do); <see cref="Make"/>
/// truncates to 16 bits like make_reg and <see cref="Make32"/> keeps 18 like make_reg32. ScummVM
/// wraps <see cref="WithOffset"/> and <see cref="IncOffset"/> at 16 bits before SCI3, which only
/// differs for an offset already outside any script. Arithmetic that needs the segment manager
/// lives in <see cref="Vm"/>.
/// </summary>
public readonly record struct RegT(ushort Segment, int Offset)
{
    public const ushort UninitializedSegment = 0x1FFF;
    public const ushort SignalOffset = 0xFFFF;
    public const int OffsetMask = 0x3FFFF;

    public static readonly RegT Null = default;
    public static readonly RegT Signal = new(0, SignalOffset);
    public static readonly RegT True = new(0, 1);

    public static RegT Make(int segment, int offset) => new((ushort)segment, (ushort)offset);

    /// <summary>A pointer with an 18-bit offset (make_reg32), for positions in SCI3 scripts.</summary>
    public static RegT Make32(int segment, long offset) => new((ushort)segment, (int)(offset & OffsetMask));

    /// <summary>ScummVM's packed form, as saved games store it: offset bits 16 and 17 in bits 14 and 15 of the segment.</summary>
    public static RegT FromPacked(ushort segment, ushort offset) => new((ushort)(segment & 0x3FFF), ((segment & 0xC000) << 2) | offset);

    public ushort PackedSegment => (ushort)(((Offset & 0x30000) >> 2) | (Segment & 0x3FFF));

    public bool IsNull => (Offset | Segment) == 0;

    public bool IsNumber => Segment == 0;

    public bool IsPointer => Segment != 0 && Segment != UninitializedSegment;

    public bool IsInitialized => Segment != UninitializedSegment;

    public ushort ToUInt16() => (ushort)Offset;

    public short ToInt16() => (short)Offset;

    public RegT WithOffset(long offset) => new(Segment, (int)(offset & OffsetMask));

    public RegT WithSegment(int segment) => new((ushort)segment, Offset);

    public RegT IncOffset(long delta) => new(Segment, (int)((Offset + delta) & OffsetMask));

    public override string ToString() => $"{Segment:x4}:{Offset:x4}";
}

/// <summary>Raised for script and VM errors that ScummVM reports with error().</summary>
public sealed class SciVmException : Exception
{
    public SciVmException(string message)
        : base(message)
    {
    }

    public SciVmException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public enum SegmentType
{
    Invalid = 0,
    Script = 1,
    Clones = 2,
    Locals = 3,
    Stack = 4,
    Lists = 6,
    Nodes = 7,
    Hunk = 8,
    DynMem = 9,

    /// <summary>SCI32 arrays and strings (kArray, kString).</summary>
    Array = 11,

    /// <summary>SCI32 in-memory bitmaps (kBitmap, text bitmaps).</summary>
    Bitmap = 13,
    Max,
}

public static class SelectorIds
{
    /// <summary>Special selector used when a call has no selector.</summary>
    public const int NullSelector = -1;
}

/// <summary>Well known global variable indexes.</summary>
public static class GlobalVar
{
    public const int Ego = 0;
    public const int Game = 1;
    public const int CurrentRoom = 2;
    public const int Speed = 3;
    public const int Quit = 4;
    public const int Cast = 5;
    public const int Sounds = 8;
    public const int CurrentRoomNo = 11;
    public const int PreviousRoomNo = 12;
    public const int NewRoomNo = 13;
    public const int Score = 15;
    public const int VersionNew = 27;
    public const int VersionOld = 28;
    public const int User = 80;
    public const int FastCast = 84;
    public const int MessageType = 90;
    public const int RamaCatalogFile = 130;
    public const int Shivers1Score = 349;
}
