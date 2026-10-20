using Daventry.Agi.Graphics;

namespace Daventry.Agi;

/// <summary>Position, collision and priority checks. Port of ScummVM's checks.cpp.</summary>
public sealed partial class AgiEngine
{
    private static readonly int[] DirectionDx = [0, 0, 1, 1, 1, 0, -1, -1, -1];
    private static readonly int[] DirectionDy = [0, -1, -1, 0, 1, 1, 1, 0, -1];

    /// <summary>Whether a screen object's position is on screen and below the horizon.</summary>
    public bool CheckPosition(ScreenObjEntry screenObj)
    {
        if (screenObj.XPos < 0)
        {
            return false;
        }

        if (screenObj.XPos + screenObj.XSize > GfxMgr.ScriptWidth)
        {
            return false;
        }

        if (screenObj.YPos - screenObj.YSize < -1)
        {
            return false;
        }

        if (screenObj.YPos >= GfxMgr.ScriptHeight)
        {
            return false;
        }

        if ((screenObj.Flags & ViewFlags.IgnoreHorizon) == 0 && screenObj.YPos <= State.Horizon)
        {
            return false;
        }

        return true;
    }

    /// <summary>Whether another animated object is in the way.</summary>
    public bool CheckCollision(ScreenObjEntry screenObj)
    {
        if ((screenObj.Flags & ViewFlags.IgnoreObjects) != 0)
        {
            return false;
        }

        foreach (var checkObj in State.ScreenObjTable)
        {
            if (!checkObj.HasFlags(ViewFlags.Animated | ViewFlags.Drawn))
            {
                continue;
            }

            if ((checkObj.Flags & ViewFlags.IgnoreObjects) != 0)
            {
                continue;
            }

            if (screenObj.ObjectNr == checkObj.ObjectNr)
            {
                continue;
            }

            // no horizontal overlap
            if (screenObj.XPos + screenObj.XSize < checkObj.XPos || screenObj.XPos > checkObj.XPos + checkObj.XSize)
            {
                continue;
            }

            // same y: collision
            if (screenObj.YPos == checkObj.YPos)
            {
                return true;
            }

            // crossed the baseline: collision
            if ((screenObj.YPos > checkObj.YPos && screenObj.YPosPrev < checkObj.YPosPrev) ||
                (screenObj.YPos < checkObj.YPos && screenObj.YPosPrev > checkObj.YPosPrev))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Updates the object's priority from the priority bands unless fixed, and checks the control
    /// line pixels under its baseline. Returns false if the object may not stand there.
    /// </summary>
    public bool CheckPriority(ScreenObjEntry screenObj)
    {
        bool touchedWater = false;
        bool touchedTrigger = false;
        bool touchedControl = true;

        if ((screenObj.Flags & ViewFlags.FixedPriority) == 0)
        {
            screenObj.Priority = (byte)Gfx.PriorityFromY(screenObj.YPos);
        }

        if (screenObj.Priority != 0x0F)
        {
            touchedWater = true;
            int curX = screenObj.XPos;
            int curY = screenObj.YPos;
            for (int celX = 0; celX < screenObj.XSize; celX++, curX++)
            {
                byte screenPriority = Gfx.GetPriority(curX, curY);
                if (screenPriority == 0)
                {
                    // unconditional black: no go at all
                    touchedControl = false;
                    break;
                }

                if (screenPriority != 3)
                {
                    // not water surface
                    touchedWater = false;
                    if (screenPriority == 1)
                    {
                        // conditional blue
                        if ((screenObj.Flags & ViewFlags.IgnoreBlocks) == 0)
                        {
                            touchedControl = false;
                            break;
                        }
                    }
                    else if (screenPriority == 2)
                    {
                        touchedTrigger = true;
                    }
                }
            }

            if (touchedControl)
            {
                if (!touchedWater)
                {
                    if ((screenObj.Flags & ViewFlags.OnWater) != 0)
                    {
                        touchedControl = false;
                    }
                }
                else if ((screenObj.Flags & ViewFlags.OnLand) != 0)
                {
                    touchedControl = false;
                }
            }
        }

        if (screenObj.ObjectNr == 0)
        {
            SetFlag(AgiFlag.EgoTouchedP2, touchedTrigger);
            SetFlag(AgiFlag.EgoWater, touchedWater);

            // WORKAROUND: KQ3 infinite falling (bug #13379). Falling off the ladder in room 22 or the
            // stairs in room 64 can leave ego falling forever because an unrelated black priority
            // line overlaps the fall path. Ignore those lines when falling.
            if (!touchedControl && Description.GameId == "kq3" && screenObj.CurrentViewNr == 11)
            {
                touchedControl = true;
            }
        }

        return touchedControl;
    }

    /// <summary>
    /// Moves every animated, updating object by its direction and step size. A new position must
    /// pass the positioning rules, otherwise the previous position is kept.
    /// </summary>
    public void UpdatePosition()
    {
        SetVar(AgiVar.BorderCode, 0);
        SetVar(AgiVar.BorderTouchEgo, 0);
        SetVar(AgiVar.BorderTouchObject, 0);

        foreach (var screenObj in State.ScreenObjTable)
        {
            if (!screenObj.HasFlags(ViewFlags.Animated | ViewFlags.Update | ViewFlags.Drawn))
            {
                continue;
            }

            if (screenObj.StepTimeCount > 1)
            {
                screenObj.StepTimeCount--;
                continue;
            }

            screenObj.StepTimeCount = screenObj.StepTime;

            int x = screenObj.XPos;
            int oldX = x;
            int y = screenObj.YPos;
            int oldY = y;

            if ((screenObj.Flags & ViewFlags.UpdatePos) == 0)
            {
                x += screenObj.StepSize * DirectionDx[screenObj.Direction];
                y += screenObj.StepSize * DirectionDy[screenObj.Direction];
            }

            int border = 0;

            // left/right borders
            if (Description.Version == 0x3086)
            {
                // The KQ4 interpreter compares x differently from 2.917 before it and 3.098 after
                // it. Required for Sarien bug #192: KQ4 room 135, the hen moving to the left border.
                if (x <= 0)
                {
                    x = 0;
                    border = 4;
                }
            }
            else if (x < 0)
            {
                x = 0;
                border = 4;
            }

            if (border == 0 && x + screenObj.XSize > GfxMgr.ScriptWidth)
            {
                x = GfxMgr.ScriptWidth - screenObj.XSize;
                border = 2;
            }

            // top/bottom borders
            if (y - screenObj.YSize < -1)
            {
                y = screenObj.YSize - 1;
                border = 1;
            }
            else if (y > GfxMgr.ScriptHeight - 1)
            {
                y = GfxMgr.ScriptHeight - 1;
                border = 3;
            }
            else if ((screenObj.Flags & ViewFlags.IgnoreHorizon) == 0 && y <= State.Horizon)
            {
                y = State.Horizon + 1;
                border = 1;
            }

            // test the new position, roll back if it fails
            screenObj.XPos = x;
            screenObj.YPos = y;
            if (CheckCollision(screenObj) || !CheckPriority(screenObj))
            {
                screenObj.XPos = oldX;
                screenObj.YPos = oldY;
                border = 0;
                FixPosition(screenObj);
            }

            if (border != 0)
            {
                if (IsEgoView(screenObj))
                {
                    SetVar(AgiVar.BorderTouchEgo, (byte)border);
                }
                else
                {
                    SetVar(AgiVar.BorderCode, (byte)screenObj.ObjectNr);
                    SetVar(AgiVar.BorderTouchObject, (byte)border);
                }

                if (screenObj.MotionType == MotionType.MoveObj)
                {
                    MotionMoveObjStop(screenObj);
                }
            }

            screenObj.Flags &= ~ViewFlags.UpdatePos;
        }
    }

    public void FixPosition(int screenObjNr) => FixPosition(State.ScreenObjTable[screenObjNr]);

    /// <summary>
    /// Moves a sprite in a widening spiral until it is at a valid position according to the
    /// priority and control line data. Also known as the "Budin-Sonneveld effect".
    /// </summary>
    public void FixPosition(ScreenObjEntry screenObj)
    {
        if ((screenObj.Flags & ViewFlags.IgnoreHorizon) == 0 && screenObj.YPos <= State.Horizon)
        {
            screenObj.YPos = State.Horizon + 1;
        }

        int dir = 0;
        int count = 1;
        int size = 1;

        while (!CheckPosition(screenObj) || CheckCollision(screenObj) || !CheckPriority(screenObj))
        {
            switch (dir)
            {
                case 0: // west
                    screenObj.XPos--;
                    if (--count != 0)
                    {
                        continue;
                    }

                    dir = 1;
                    break;
                case 1: // south
                    screenObj.YPos++;
                    if (--count != 0)
                    {
                        continue;
                    }

                    dir = 2;
                    size++;
                    break;
                case 2: // east
                    screenObj.XPos++;
                    if (--count != 0)
                    {
                        continue;
                    }

                    dir = 3;
                    break;
                case 3: // north
                    screenObj.YPos--;
                    if (--count != 0)
                    {
                        continue;
                    }

                    dir = 0;
                    size++;
                    break;
            }

            count = size;
        }
    }

    /// <summary>
    /// Tests whether ego faces nearby water without obstacles. Used by opcode 5F in Black Cauldron
    /// AGIv1 to test if the water flask can be filled. Returns the distance, or 250 if none.
    /// </summary>
    public byte EgoNearWater(byte limit)
    {
        var ego = State.ScreenObjTable[AgiGameState.ScreenObjectsEgoEntry];
        int x1 = ego.XPos;
        int x2 = 0;
        byte direction;
        switch (ego.CurrentLoopNr)
        {
            case 0: // right
                direction = 3;
                x1 += ego.XSize;
                break;
            case 1: // left
                direction = 7;
                break;
            case 2: // down
                direction = 5;
                x1 += ego.XSize / 2;
                break;
            case 3: // up
                direction = 1;
                x2 = x1 + ego.XSize;
                x1--;
                break;
            default:
                return 250;
        }

        int distance = -1;
        while (x1 != 0)
        {
            distance = NearWater(ego, direction, x1, ego.YPos, limit);
            if (distance != -1)
            {
                break;
            }

            x1 = x2;
            x2 = 0;
        }

        if (distance == -1)
        {
            return 250;
        }

        // adjust ego's position for the collision check
        int prevPrevX = ego.XPosPrev;
        int prevPrevY = ego.YPosPrev;
        ego.XPosPrev = ego.XPos;
        ego.YPosPrev = ego.YPos;
        switch (direction)
        {
            case 1:
                ego.YPos -= distance;
                break;
            case 3:
                ego.XPos += x1 - ego.XSize;
                break;
            case 5:
                ego.YPos += distance;
                break;
            case 7:
                ego.XPos -= distance;
                break;
        }

        if (!CheckCollision(ego))
        {
            if (State.Block.Active && (ego.Flags & ViewFlags.IgnoreBlocks) == 0 && CheckBlock(ego.XPos, ego.YPos))
            {
                distance = 250;
            }
        }
        else
        {
            distance = 250;
        }

        ego.XPos = ego.XPosPrev;
        ego.YPos = ego.YPosPrev;
        ego.XPosPrev = prevPrevX;
        ego.YPosPrev = prevPrevY;
        return (byte)distance;
    }

    /// <summary>
    /// Distance to water in a direction, or -1 if none or blocked. The original scans left when
    /// facing right; that bug is not reproduced.
    /// </summary>
    public int NearWater(ScreenObjEntry screenObj, byte direction, int x, int y, byte limit)
    {
        int dx = 0;
        int dy = 0;
        switch (direction)
        {
            case 1:
                dy = -1;
                break;
            case 3:
                dx = 1;
                break;
            case 5:
                dy = 1;
                break;
            case 7:
                dx = -1;
                break;
        }

        for (int i = 0; i <= limit; i++)
        {
            if (!(0 <= x && x < GfxMgr.ScriptWidth && 0 <= y && y < GfxMgr.ScriptHeight))
            {
                break;
            }

            byte priority = Gfx.GetPriority(x, y);
            x += dx;
            y += dy;

            if (priority == 3)
            {
                return i;
            }

            if (screenObj.Priority != 15)
            {
                if (priority == 0)
                {
                    break;
                }

                if (priority == 1 && (screenObj.Flags & ViewFlags.IgnoreBlocks) == 0)
                {
                    break;
                }
            }
        }

        return -1;
    }
}
