namespace Daventry.Sci.Engine;

/// <summary>
/// A VM register: a segment and an offset. Numbers have segment 0; pointers name a segment of
/// the segment manager. Mirrors ScummVM's reg_t for SCI0 to SCI2.1 (SCI3's extra offset bits
/// are not ported). Arithmetic that needs the segment manager lives in <see cref="Vm"/>.
/// </summary>
public readonly record struct RegT(ushort Segment, ushort Offset)
{
    public const ushort UninitializedSegment = 0x1FFF;
    public const ushort SignalOffset = 0xFFFF;

    public static readonly RegT Null = default;
    public static readonly RegT Signal = new(0, SignalOffset);
    public static readonly RegT True = new(0, 1);

    public static RegT Make(int segment, int offset) => new((ushort)segment, (ushort)offset);

    public bool IsNull => (Offset | Segment) == 0;

    public bool IsNumber => Segment == 0;

    public bool IsPointer => Segment != 0 && Segment != UninitializedSegment;

    public bool IsInitialized => Segment != UninitializedSegment;

    public ushort ToUInt16() => Offset;

    public short ToInt16() => (short)Offset;

    public RegT WithOffset(int offset) => new(Segment, (ushort)offset);

    public RegT WithSegment(int segment) => new((ushort)segment, Offset);

    public RegT IncOffset(int delta) => new(Segment, (ushort)(Offset + delta));

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
}
