namespace Daventry.Agi;

public enum MotionType : byte
{
    Normal = 0,
    Wander = 1,
    FollowEgo = 2,
    MoveObj = 3,

    /// <summary>Used by ScummVM for mouse movement only.</summary>
    Ego = 4,
}

public enum CycleType : byte
{
    Normal = 0,
    EndOfLoop = 1,
    RevLoop = 2,
    Reverse = 3,
}

/// <summary>Screen object flags. Mirrors ScummVM's ViewFlags with the same bit values.</summary>
[Flags]
public enum ViewFlags : ushort
{
    None = 0,
    Drawn = 1 << 0,
    IgnoreBlocks = 1 << 1,
    FixedPriority = 1 << 2,
    IgnoreHorizon = 1 << 3,
    Update = 1 << 4,
    Cycling = 1 << 5,
    Animated = 1 << 6,
    Motion = 1 << 7,
    OnWater = 1 << 8,
    IgnoreObjects = 1 << 9,
    UpdatePos = 1 << 10,
    OnLand = 1 << 11,
    DontUpdate = 1 << 12,
    FixLoop = 1 << 13,
    DidntMove = 1 << 14,
    AdjEgoXY = 1 << 15,
}

/// <summary>
/// A screen object table entry: one animated object and everything about its view, position,
/// motion and cycling. Mirrors ScummVM's ScreenObjEntry (the original vt_entry). Fields rather
/// than properties because the interpreter reads and writes all of them constantly.
/// </summary>
public sealed class ScreenObjEntry
{
    /// <summary>0 to 254 for regular entries; -1 for the add.to.pic view.</summary>
    public int ObjectNr;

    public byte StepTime;
    public byte StepTimeCount;
    public int XPos;
    public int YPos;
    public byte CurrentViewNr;
    public bool ViewReplaced;
    public AgiView? ViewResource;
    public byte CurrentLoopNr;
    public byte LoopCount;
    public AgiViewLoop? LoopData;
    public byte CurrentCelNr;
    public byte CelCount;
    public AgiViewCel? CelData;
    public int XSize;
    public int YSize;
    public int XPosPrev;
    public int YPosPrev;
    public int XSizePrev;
    public int YSizePrev;
    public byte StepSize;
    public byte CycleTime;
    public byte CycleTimeCount;
    public byte Direction;
    public MotionType MotionType;
    public CycleType Cycle;
    public byte Priority;
    public ViewFlags Flags;

    // MotionType.MoveObj
    public int MoveX;
    public int MoveY;
    public byte MoveStepSize;
    public byte MoveFlag;

    // MotionType.FollowEgo
    public byte FollowStepSize;
    public byte FollowFlag;
    public byte FollowCount;

    // MotionType.Wander
    public byte WanderCount;

    public byte LoopFlag;
    public bool IgnoreLoopFlag;

    public ScreenObjEntry()
    {
        Reset();
    }

    public void Reset()
    {
        ObjectNr = 0;
        StepTime = 0;
        StepTimeCount = 0;
        XPos = 0;
        YPos = 0;
        CurrentViewNr = 0;
        ViewReplaced = false;
        ViewResource = null;
        CurrentLoopNr = 0;
        LoopCount = 0;
        LoopData = null;
        CurrentCelNr = 0;
        CelCount = 0;
        CelData = null;
        XSize = 0;
        YSize = 0;
        XPosPrev = 0;
        YPosPrev = 0;
        XSizePrev = 0;
        YSizePrev = 0;
        StepSize = 0;
        CycleTime = 0;
        CycleTimeCount = 0;
        Direction = 0;
        MotionType = MotionType.Normal;
        Cycle = CycleType.Normal;
        Priority = 0;
        Flags = ViewFlags.None;
        MoveX = 0;
        MoveY = 0;
        MoveStepSize = 0;
        MoveFlag = 0;
        FollowStepSize = 0;
        FollowFlag = 0;
        FollowCount = 0;
        WanderCount = 0;
        LoopFlag = 0;
        IgnoreLoopFlag = false;
    }

    public void SetLoopFlag(byte flag)
    {
        LoopFlag = flag;
        IgnoreLoopFlag = false;
    }

    public bool HasFlags(ViewFlags flags) => (Flags & flags) == flags;
}
