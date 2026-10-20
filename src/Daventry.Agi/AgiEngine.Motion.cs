using Daventry.Core.Hosting;

namespace Daventry.Agi;

/// <summary>Object motion. Port of ScummVM's motion.cpp.</summary>
public sealed partial class AgiEngine
{
    private static readonly int[] DirTable = [8, 1, 2, 7, 0, 3, 6, 5, 4];

    public static int CheckStep(int delta, int step) => -step >= delta ? 0 : step <= delta ? 2 : 1;

    public bool CheckBlock(int x, int y)
    {
        if (x <= State.Block.X1 || x >= State.Block.X2)
        {
            return false;
        }

        if (y <= State.Block.Y1 || y >= State.Block.Y2)
        {
            return false;
        }

        return true;
    }

    public void ChangePos(ScreenObjEntry screenObj)
    {
        int x = screenObj.XPos;
        int y = screenObj.YPos;
        bool insideBlock = CheckBlock(x, y);

        x += screenObj.StepSize * DirectionDx[screenObj.Direction];
        y += screenObj.StepSize * DirectionDy[screenObj.Direction];

        if (CheckBlock(x, y) == insideBlock)
        {
            screenObj.Flags &= ~ViewFlags.Motion;
        }
        else
        {
            screenObj.Flags |= ViewFlags.Motion;
            screenObj.Direction = 0;
            if (IsEgoView(screenObj))
            {
                SetVar(AgiVar.EgoDirection, 0);
            }
        }
    }

    /// <summary>
    /// The original interpreter stored motion and cycler data in the same four bytes of the screen
    /// object. Activating a motion while a cycler ran overwrote the cycler's flag, so the cycler
    /// would set an unintended flag on completion. We keep the fields separate and instead mark
    /// the cycler's flag as ignored. Jumping at the eagle in KQ1 room 22 depends on this (bug #7046).
    /// </summary>
    public void MotionActivated(ScreenObjEntry screenObj)
    {
        if ((screenObj.Flags & ViewFlags.Cycling) != 0)
        {
            switch (screenObj.Cycle)
            {
                case CycleType.EndOfLoop:
                case CycleType.RevLoop:
                    screenObj.IgnoreLoopFlag = true;
                    Host.Log.Warning("agi", $"Motion activated for screen object {screenObj.ObjectNr} while cycler is active; original AGI would overwrite flag {screenObj.LoopFlag}, we skip setting it");
                    break;
            }
        }
    }

    /// <summary>
    /// The counterpart of <see cref="MotionActivated"/>: activating a cycler while a motion ran
    /// overwrote motion state with the cycler's flag. Games rely on it, for example the witches
    /// disappearing at the end of Black Cauldron (room 12), so it is reproduced.
    /// </summary>
    public void CyclerActivated(ScreenObjEntry screenObj)
    {
        string fieldName;
        int previousValue;
        switch (screenObj.MotionType)
        {
            case MotionType.Wander:
                fieldName = "wander_count";
                previousValue = screenObj.WanderCount;
                screenObj.WanderCount = screenObj.LoopFlag;
                break;
            case MotionType.FollowEgo:
                fieldName = "follow_stepSize";
                previousValue = screenObj.FollowStepSize;
                screenObj.FollowStepSize = screenObj.LoopFlag;
                break;
            case MotionType.MoveObj:
                fieldName = "move_x";
                previousValue = screenObj.MoveX;
                screenObj.MoveX = screenObj.LoopFlag;
                break;
            default:
                return;
        }

        Host.Log.Warning("agi", $"Cycler activated for screen object {screenObj.ObjectNr} while motion is active; overwriting {fieldName}: {previousValue} with flag number {screenObj.LoopFlag}, as original AGI did");
    }

    public void MotionWander(ScreenObjEntry screenObj)
    {
        byte originalWanderCount = screenObj.WanderCount;
        screenObj.WanderCount--;

        if (originalWanderCount == 0 || (screenObj.Flags & ViewFlags.DidntMove) != 0)
        {
            screenObj.Direction = (byte)Random.Next(9);
            if (IsEgoView(screenObj))
            {
                SetVar(AgiVar.EgoDirection, screenObj.Direction);
            }

            while (screenObj.WanderCount < 6)
            {
                screenObj.WanderCount = (byte)Random.Next(51);
            }
        }
    }

    public void MotionFollowEgo(ScreenObjEntry screenObj)
    {
        var ego = State.ScreenObjTable[AgiGameState.ScreenObjectsEgoEntry];
        int egoX = ego.XPos + ego.XSize / 2;
        int egoY = ego.YPos;
        int objX = screenObj.XPos + screenObj.XSize / 2;
        int objY = screenObj.YPos;

        int dir = GetDirection(objX, objY, egoX, egoY, screenObj.FollowStepSize);

        if (dir == 0)
        {
            // already at ego's coordinates
            screenObj.Direction = 0;
            screenObj.MotionType = MotionType.Normal;
            SetFlag(screenObj.FollowFlag, true);
            return;
        }

        if (screenObj.FollowCount == 0xFF)
        {
            screenObj.FollowCount = 0;
        }
        else if ((screenObj.Flags & ViewFlags.DidntMove) != 0)
        {
            while ((screenObj.Direction = (byte)Random.Next(9)) == 0)
            {
            }

            int d = (Math.Abs(egoY - objY) + Math.Abs(egoX - objX)) / 2;
            if (d < screenObj.StepSize)
            {
                screenObj.FollowCount = screenObj.StepSize;
                return;
            }

            while ((screenObj.FollowCount = (byte)Random.Next(d + 1)) < screenObj.StepSize)
            {
            }

            return;
        }

        if (screenObj.FollowCount != 0)
        {
            int k = screenObj.FollowCount - screenObj.StepSize;
            screenObj.FollowCount = (byte)k;
            if ((sbyte)screenObj.FollowCount < 0)
            {
                screenObj.FollowCount = 0;
            }
        }
        else
        {
            screenObj.Direction = (byte)dir;
        }
    }

    public void MotionMoveObj(ScreenObjEntry screenObj)
    {
        screenObj.Direction = (byte)GetDirection(screenObj.XPos, screenObj.YPos, screenObj.MoveX, screenObj.MoveY, screenObj.StepSize);

        if (IsEgoView(screenObj))
        {
            SetVar(AgiVar.EgoDirection, screenObj.Direction);
        }

        if (screenObj.Direction == 0)
        {
            MotionMoveObjStop(screenObj);
        }
    }

    public void CheckMotion(ScreenObjEntry screenObj)
    {
        switch (screenObj.MotionType)
        {
            case MotionType.Wander:
                MotionWander(screenObj);
                break;
            case MotionType.FollowEgo:
                MotionFollowEgo(screenObj);
                break;
            case MotionType.Ego:
            case MotionType.MoveObj:
                MotionMoveObj(screenObj);
                break;
        }

        if (State.Block.Active && (screenObj.Flags & ViewFlags.IgnoreBlocks) == 0 && screenObj.Direction != 0)
        {
            ChangePos(screenObj);
        }
    }

    public void CheckAllMotions()
    {
        foreach (var screenObj in State.ScreenObjTable)
        {
            if (screenObj.HasFlags(ViewFlags.Animated | ViewFlags.Update | ViewFlags.Drawn) && screenObj.StepTimeCount == 1)
            {
                CheckMotion(screenObj);
            }
        }
    }

    /// <summary>Marks a move.obj object as having reached its destination.</summary>
    public void InDestination(ScreenObjEntry screenObj)
    {
        if (screenObj.MotionType == MotionType.MoveObj)
        {
            screenObj.StepSize = screenObj.MoveStepSize;
            SetFlag(screenObj.MoveFlag, true);
        }

        screenObj.MotionType = MotionType.Normal;
        if (IsEgoView(screenObj))
        {
            State.PlayerControl = true;
        }
    }

    public void MotionMoveObjStop(ScreenObjEntry screenObj)
    {
        screenObj.StepSize = screenObj.MoveStepSize;

        // AGI3 only checked the motion type here, but the Ego motion type is used for mouse
        // movement on every version, so it must be checked everywhere.
        if (screenObj.MotionType != MotionType.Ego)
        {
            SetFlag(screenObj.MoveFlag, true);
        }

        screenObj.MotionType = MotionType.Normal;
        if (IsEgoView(screenObj))
        {
            State.PlayerControl = true;
            SetVar(AgiVar.EgoDirection, 0);
        }
    }

    /// <summary>Used by move.obj in the first motion cycle after setting the motion type.</summary>
    public void MoveObj(ScreenObjEntry screenObj) => MotionMoveObj(screenObj);

    /// <summary>Direction (1 to 8, or 0 when there) from a position towards a destination given a step size.</summary>
    public static int GetDirection(int objX, int objY, int destX, int destY, int stepSize)
        => DirTable[CheckStep(destX - objX, stepSize) + 3 * CheckStep(destY - objY, stepSize)];
}
