using Daventry.Agi.Resources;
using Daventry.Core.Hosting;

namespace Daventry.Agi;

/// <summary>Command opcodes: variables, flags, objects, views, motion, pictures. Port of ScummVM's op_cmd.cpp, first half.</summary>
public sealed partial class AgiEngine
{
    private ScreenObjEntry Obj(byte objectNr) => State.ScreenObjTable[objectNr];

    // ---- variables and flags ----

    public ValueTask CmdIncrement(byte[] p)
    {
        byte value = GetVar(p[0]);
        byte maxValue = Description.Version < 0x2000 ? (byte)0xF0 : (byte)0xFF;
        if (value < maxValue)
        {
            SetVar(p[0], (byte)(value + 1));
        }

        return default;
    }

    public ValueTask CmdDecrement(byte[] p)
    {
        byte value = GetVar(p[0]);
        if (value != 0)
        {
            SetVar(p[0], (byte)(value - 1));
        }

        return default;
    }

    public ValueTask CmdAssignN(byte[] p)
    {
        SetVar(p[0], p[1]);

        // WORKAROUND: the fan game "Get outta SQ" assigns the total points (variable 7) to 0,
        // so "Points 0 of 0" shows after a restart. Bug #3696.
        if (Description.Gid == AgiGameId.Getouttasq && p[0] == 7)
        {
            SetVar(p[0], 8);
        }

        return default;
    }

    public ValueTask CmdAddN(byte[] p)
    {
        SetVar(p[0], (byte)(GetVar(p[0]) + p[1]));
        return default;
    }

    public ValueTask CmdSubN(byte[] p)
    {
        SetVar(p[0], (byte)(GetVar(p[0]) - p[1]));
        return default;
    }

    public ValueTask CmdAssignV(byte[] p)
    {
        SetVar(p[0], GetVar(p[1]));
        return default;
    }

    public ValueTask CmdAddV(byte[] p)
    {
        SetVar(p[0], (byte)(GetVar(p[0]) + GetVar(p[1])));
        return default;
    }

    public ValueTask CmdSubV(byte[] p)
    {
        SetVar(p[0], (byte)(GetVar(p[0]) - GetVar(p[1])));
        return default;
    }

    public ValueTask CmdMulN(byte[] p)
    {
        SetVar(p[0], (byte)(GetVar(p[0]) * p[1]));
        return default;
    }

    public ValueTask CmdMulV(byte[] p)
    {
        SetVar(p[0], (byte)(GetVar(p[0]) * GetVar(p[1])));
        return default;
    }

    public ValueTask CmdDivN(byte[] p)
    {
        SetVar(p[0], p[1] == 0 ? (byte)0 : (byte)(GetVar(p[0]) / p[1]));
        return default;
    }

    public ValueTask CmdDivV(byte[] p)
    {
        byte divisor = GetVar(p[1]);
        SetVar(p[0], divisor == 0 ? (byte)0 : (byte)(GetVar(p[0]) / divisor));
        return default;
    }

    public ValueTask CmdRandomV1(byte[] p)
    {
        SetVar(p[0], (byte)Random.Next(251));
        return default;
    }

    public ValueTask CmdRandom(byte[] p)
    {
        int min = p[0];
        int max = p[1];
        SetVar(p[2], (byte)(Random.Next(Math.Max(0, max - min) + 1) + min));
        return default;
    }

    public ValueTask CmdLindirectN(byte[] p)
    {
        SetVar(GetVar(p[0]), p[1]);
        return default;
    }

    public ValueTask CmdLindirectV(byte[] p)
    {
        SetVar(GetVar(p[0]), GetVar(p[1]));
        return default;
    }

    public ValueTask CmdRindirect(byte[] p)
    {
        SetVar(p[0], GetVar(GetVar(p[1])));
        return default;
    }

    public ValueTask CmdSet(byte[] p)
    {
        SetFlag(p[0], true);
        return default;
    }

    public ValueTask CmdReset(byte[] p)
    {
        SetFlag(p[0], false);
        return default;
    }

    public ValueTask CmdToggle(byte[] p)
    {
        SetFlag(p[0], !GetFlag(p[0]));
        return default;
    }

    public ValueTask CmdSetV(byte[] p)
    {
        if (Description.Version < 0x2000)
        {
            SetVar(p[0], 1);
        }
        else
        {
            SetFlag(GetVar(p[0]), true);
        }

        return default;
    }

    public ValueTask CmdResetV(byte[] p)
    {
        if (Description.Version < 0x2000)
        {
            SetVar(p[0], 0);
        }
        else
        {
            SetFlag(GetVar(p[0]), false);
        }

        return default;
    }

    public ValueTask CmdToggleV(byte[] p)
    {
        if (Description.Version < 0x2000)
        {
            SetVar(p[0], (byte)(GetVar(p[0]) ^ 1));
        }
        else
        {
            int flagNr = GetVar(p[0]);
            SetFlag(flagNr, !GetFlag(flagNr));
        }

        return default;
    }

    public ValueTask CmdSetBit(byte[] p)
    {
        SetVar(p[1], (byte)(GetVar(p[1]) | (1 << p[0])));
        return default;
    }

    public ValueTask CmdClearBit(byte[] p)
    {
        SetVar(p[1], (byte)(GetVar(p[1]) & ~(1 << p[0])));
        return default;
    }

    // ---- rooms, logics, resources ----

    public async ValueTask CmdNewRoom(byte[] p) => await NewRoomAsync(p[0]);

    public async ValueTask CmdNewRoomF(byte[] p) => await NewRoomAsync(GetVar(p[0]));

    public ValueTask CmdLoadView(byte[] p)
    {
        LoadResource(AgiResourceType.View, p[0]);
        return default;
    }

    public ValueTask CmdLoadLogic(byte[] p)
    {
        LoadResource(AgiResourceType.Logic, p[0]);
        return default;
    }

    public ValueTask CmdLoadSound(byte[] p)
    {
        LoadResource(AgiResourceType.Sound, p[0]);
        return default;
    }

    public ValueTask CmdLoadViewF(byte[] p)
    {
        LoadResource(AgiResourceType.View, GetVar(p[0]));
        return default;
    }

    public ValueTask CmdLoadLogicF(byte[] p)
    {
        LoadResource(AgiResourceType.Logic, GetVar(p[0]));
        return default;
    }

    public ValueTask CmdDiscardView(byte[] p)
    {
        UnloadResource(AgiResourceType.View, p[0]);
        return default;
    }

    public async ValueTask CmdCall(byte[] p)
    {
        // sIP is not saved because set.scan.start can be used in a called script (fixes the xmas demo)
        int oldCIP = State.CurLogic!.CIP;
        int oldLogicNr = State.CurLogicNr;

        await RunLogicAsync(p[0]);

        State.CurLogicNr = oldLogicNr;
        State.CurLogic = Resources.Logics[State.CurLogicNr] ?? State.CurLogic;
        State.CurLogic!.CIP = oldCIP;
    }

    public async ValueTask CmdCallF(byte[] p) => await CmdCall([GetVar(p[0])]);

    public ValueTask CmdCallV1(byte[] p)
    {
        LoadResource(AgiResourceType.Logic, p[0]);
        ++State.MaxLogics;
        SetVar(13, 1);
        return default;
    }

    // ---- screen object flags ----

    public ValueTask CmdObjectOnAnything(byte[] p)
    {
        Obj(p[0]).Flags &= ~(ViewFlags.OnWater | ViewFlags.OnLand);
        return default;
    }

    public ValueTask CmdObjectOnLand(byte[] p)
    {
        Obj(p[0]).Flags |= ViewFlags.OnLand;
        return default;
    }

    public ValueTask CmdObjectOnWater(byte[] p)
    {
        Obj(p[0]).Flags |= ViewFlags.OnWater;
        return default;
    }

    public ValueTask CmdObserveHorizon(byte[] p)
    {
        Obj(p[0]).Flags &= ~ViewFlags.IgnoreHorizon;
        return default;
    }

    public ValueTask CmdIgnoreHorizon(byte[] p)
    {
        Obj(p[0]).Flags |= ViewFlags.IgnoreHorizon;
        return default;
    }

    public ValueTask CmdObserveObjs(byte[] p)
    {
        Obj(p[0]).Flags &= ~ViewFlags.IgnoreObjects;
        return default;
    }

    public ValueTask CmdIgnoreObjs(byte[] p)
    {
        Obj(p[0]).Flags |= ViewFlags.IgnoreObjects;
        return default;
    }

    public ValueTask CmdObserveBlocks(byte[] p)
    {
        Obj(p[0]).Flags &= ~ViewFlags.IgnoreBlocks;
        return default;
    }

    public ValueTask CmdIgnoreBlocks(byte[] p)
    {
        Obj(p[0]).Flags |= ViewFlags.IgnoreBlocks;
        return default;
    }

    public ValueTask CmdSetHorizon(byte[] p)
    {
        State.Horizon = p[0];
        return default;
    }

    public ValueTask CmdGetPriority(byte[] p)
    {
        SetVar(p[1], Obj(p[0]).Priority);
        return default;
    }

    public ValueTask CmdSetPriority(byte[] p)
    {
        var obj = Obj(p[0]);
        obj.Flags |= ViewFlags.FixedPriority;
        obj.Priority = p[1];
        return default;
    }

    public ValueTask CmdSetPriorityF(byte[] p)
    {
        var obj = Obj(p[0]);
        obj.Flags |= ViewFlags.FixedPriority;
        obj.Priority = GetVar(p[1]);
        return default;
    }

    public ValueTask CmdReleasePriority(byte[] p)
    {
        Obj(p[0]).Flags &= ~ViewFlags.FixedPriority;
        return default;
    }

    public ValueTask CmdSetUpperLeft(byte[] p) => default; // does nothing in 2.917

    public ValueTask CmdStartUpdate(byte[] p)
    {
        StartUpdate(Obj(p[0]));
        return default;
    }

    public ValueTask CmdStopUpdate(byte[] p)
    {
        StopUpdate(Obj(p[0]));
        return default;
    }

    public ValueTask CmdCurrentView(byte[] p)
    {
        SetVar(p[1], Obj(p[0]).CurrentViewNr);
        return default;
    }

    public ValueTask CmdCurrentCel(byte[] p)
    {
        SetVar(p[1], Obj(p[0]).CurrentCelNr);
        return default;
    }

    public ValueTask CmdCurrentLoop(byte[] p)
    {
        SetVar(p[1], Obj(p[0]).CurrentLoopNr);
        return default;
    }

    public ValueTask CmdLastCel(byte[] p)
    {
        var obj = Obj(p[0]);
        SetVar(p[1], (byte)((obj.LoopData?.CelCount ?? 1) - 1));
        return default;
    }

    public ValueTask CmdSetCel(byte[] p)
    {
        var obj = Obj(p[0]);
        SetCel(obj, p[1]);
        if (Description.Version >= 0x2000)
        {
            obj.Flags &= ~ViewFlags.DontUpdate;
        }

        return default;
    }

    public ValueTask CmdSetCelF(byte[] p)
    {
        var obj = Obj(p[0]);
        SetCel(obj, GetVar(p[1]));
        obj.Flags &= ~ViewFlags.DontUpdate;
        return default;
    }

    public ValueTask CmdSetView(byte[] p)
    {
        SetView(Obj(p[0]), p[1]);
        return default;
    }

    public ValueTask CmdSetViewF(byte[] p)
    {
        SetView(Obj(p[0]), GetVar(p[1]));
        return default;
    }

    public ValueTask CmdSetLoop(byte[] p)
    {
        SetLoop(Obj(p[0]), p[1]);
        return default;
    }

    public ValueTask CmdSetLoopF(byte[] p)
    {
        SetLoop(Obj(p[0]), GetVar(p[1]));
        return default;
    }

    public ValueTask CmdNumberOfLoops(byte[] p)
    {
        SetVar(p[1], Obj(p[0]).LoopCount);
        return default;
    }

    public ValueTask CmdFixLoop(byte[] p)
    {
        Obj(p[0]).Flags |= ViewFlags.FixLoop;
        return default;
    }

    public ValueTask CmdReleaseLoop(byte[] p)
    {
        Obj(p[0]).Flags &= ~ViewFlags.FixLoop;
        return default;
    }

    public ValueTask CmdStepSize(byte[] p)
    {
        Obj(p[0]).StepSize = GetVar(p[1]);
        return default;
    }

    public ValueTask CmdStepTime(byte[] p)
    {
        var obj = Obj(p[0]);
        obj.StepTime = obj.StepTimeCount = GetVar(p[1]);
        return default;
    }

    public ValueTask CmdCycleTime(byte[] p)
    {
        var obj = Obj(p[0]);
        obj.CycleTime = obj.CycleTimeCount = GetVar(p[1]);
        return default;
    }

    public ValueTask CmdStopCycling(byte[] p)
    {
        Obj(p[0]).Flags &= ~ViewFlags.Cycling;
        return default;
    }

    public ValueTask CmdStartCycling(byte[] p)
    {
        Obj(p[0]).Flags |= ViewFlags.Cycling;
        return default;
    }

    public ValueTask CmdNormalCycle(byte[] p)
    {
        var obj = Obj(p[0]);
        obj.Cycle = CycleType.Normal;
        if (Description.Version >= 0x2000)
        {
            obj.Flags |= ViewFlags.Cycling;
        }

        return default;
    }

    public ValueTask CmdReverseCycle(byte[] p)
    {
        var obj = Obj(p[0]);
        obj.Cycle = CycleType.Reverse;
        if (Description.Version >= 0x2000)
        {
            obj.Flags |= ViewFlags.Cycling;
        }

        return default;
    }

    public ValueTask CmdSetDir(byte[] p)
    {
        Obj(p[0]).Direction = GetVar(p[1]);
        return default;
    }

    public ValueTask CmdGetDir(byte[] p)
    {
        SetVar(p[1], Obj(p[0]).Direction);
        return default;
    }

    // ---- inventory objects ----

    public ValueTask CmdGetRoomF(byte[] p)
    {
        SetVar(p[1], (byte)ObjectGetLocation(GetVar(p[0])));
        return default;
    }

    public ValueTask CmdPut(byte[] p)
    {
        ObjectSetLocation(p[0], GetVar(p[1]));
        return default;
    }

    public ValueTask CmdPutF(byte[] p)
    {
        ObjectSetLocation(GetVar(p[0]), GetVar(p[1]));
        return default;
    }

    public ValueTask CmdDrop(byte[] p)
    {
        ObjectSetLocation(p[0], 0);
        return default;
    }

    public ValueTask CmdGet(byte[] p)
    {
        ObjectSetLocation(p[0], AgiObject.EgoOwned);
        return default;
    }

    public ValueTask CmdGetV1(byte[] p)
    {
        ObjectSetLocation(p[0], AgiObject.EgoOwnedV1);
        return default;
    }

    public ValueTask CmdGetF(byte[] p)
    {
        ObjectSetLocation(GetVar(p[0]), AgiObject.EgoOwned);
        return default;
    }

    public ValueTask CmdWordToString(byte[] p)
    {
        State.SetString(p[0], Words.GetEgoWord(p[1]));
        return default;
    }

    // ---- pictures ----

    public ValueTask CmdDrawPicV1(byte[] p)
    {
        int resourceNr = GetVar(p[0]);
        Picture.DecodePicture(resourceNr, true);
        Text.PromptClear();
        ArtificialDelayTriggerDrawPicture(resourceNr);
        return default;
    }

    public ValueTask CmdDrawPic(byte[] p)
    {
        int resourceNr = GetVar(p[0]);
        Sprites.EraseSprites();
        Picture.DecodePicture(resourceNr, true);
        Sprites.BuildAllSpriteLists();
        Sprites.DrawAllSpriteLists();
        State.PictureShown = false;

        // WORKAROUND: SQ1 logics 20 and 110 do not reset flag 103, which makes the spider droid
        // (view 46) kill the player at ground level. Reset it when returning to picture 20. Bug #3056.
        if (Description.Gid == AgiGameId.Sq1 && resourceNr == 20)
        {
            SetFlag(103, false);
        }

        ArtificialDelayTriggerDrawPicture(resourceNr);
        return default;
    }

    public ValueTask CmdShowPic(byte[] p)
    {
        SetFlag(AgiFlag.OutputMode, false);
        Text.CloseWindow();
        Picture.ShowPictureWithTransition();
        State.PictureShown = true;
        return default;
    }

    public ValueTask CmdLoadPic(byte[] p)
    {
        int resourceNr = GetVar(p[0]);
        Sprites.EraseSprites();
        LoadResource(AgiResourceType.Picture, resourceNr);
        Sprites.BuildAllSpriteLists();
        Sprites.DrawAllSpriteLists();
        return default;
    }

    public ValueTask CmdLoadPicV1(byte[] p)
    {
        LoadResource(AgiResourceType.Picture, GetVar(p[0]));
        return default;
    }

    public ValueTask CmdDiscardPic(byte[] p) => default;

    public ValueTask CmdOverlayPic(byte[] p)
    {
        int resourceNr = GetVar(p[0]);
        Sprites.EraseSprites();
        Picture.DecodePicture(resourceNr, false);
        Sprites.BuildAllSpriteLists();
        Sprites.DrawAllSpriteLists();
        Sprites.ShowAllSpriteLists();
        State.PictureShown = false;
        ArtificialDelayTriggerDrawPicture(resourceNr);
        return default;
    }

    public async ValueTask CmdShowPriScreen(byte[] p)
    {
        Gfx.DebugShowMap(1);
        await WaitKeyAsync();
        Gfx.DebugShowMap(0);
    }

    /// <summary>AGI256 modified opcode 170 to load and draw a 256 colour picture without clearing the priority screen.</summary>
    public ValueTask CmdAgi256LoadPic(byte[] p)
    {
        int resourceNr = GetVar(p[0]);
        Sprites.EraseSprites();
        LoadResource(AgiResourceType.Picture, resourceNr);
        Picture.DecodePicture(resourceNr, false, true);
        Sprites.DrawAllSpriteLists();
        State.PictureShown = false;
        ArtificialDelayTriggerDrawPicture(resourceNr);
        return default;
    }

    /// <summary>AGIMOUSE modified opcode 171 to report the mouse state in variables 27 to 29.</summary>
    public ValueTask CmdAgiMouseGetMouseState(byte[] p)
    {
        SetVar(AgiVar.MouseButtonState, (byte)Mouse.Button);
        SetVar(AgiVar.MouseX, (byte)(Mouse.X / 2));
        SetVar(AgiVar.MouseY, (byte)Mouse.Y);
        return default;
    }

    // ---- animation ----

    public ValueTask CmdAnimateObj(byte[] p)
    {
        var obj = Obj(p[0]);
        if (Description.Version < 0x2000)
        {
            if ((obj.Flags & ViewFlags.DidntMove) != 0)
            {
                return default;
            }
        }
        else if ((obj.Flags & ViewFlags.Animated) != 0)
        {
            return default;
        }

        obj.Flags = ViewFlags.Animated | ViewFlags.Update | ViewFlags.Cycling;
        if (Description.Version < 0x2000)
        {
            obj.Flags |= ViewFlags.DidntMove;
        }

        obj.MotionType = MotionType.Normal;
        obj.Cycle = CycleType.Normal;
        obj.Direction = 0;
        return default;
    }

    public ValueTask CmdUnanimateAll(byte[] p)
    {
        Sprites.EraseSprites();
        foreach (var obj in State.ScreenObjTable)
        {
            obj.Flags &= ~(ViewFlags.Animated | ViewFlags.Drawn);
        }

        return default;
    }

    public ValueTask CmdDraw(byte[] p)
    {
        var obj = Obj(p[0]);
        if ((obj.Flags & ViewFlags.Drawn) != 0)
        {
            return default;
        }

        obj.Flags |= ViewFlags.Update;
        if (Description.Version >= 0x3000)
        {
            SetLoop(obj, obj.CurrentLoopNr);
            SetCel(obj, obj.CurrentCelNr);
        }

        FixPosition(obj);
        obj.XPosPrev = obj.XPos;
        obj.YPosPrev = obj.YPos;
        obj.XSizePrev = obj.XSize;
        obj.YSizePrev = obj.YSize;

        Sprites.EraseRegularSprites();
        obj.Flags |= ViewFlags.Drawn;
        Sprites.BuildRegularSpriteList();
        Sprites.DrawRegularSpriteList();
        Sprites.ShowSprite(obj);
        obj.Flags &= ~ViewFlags.DontUpdate;
        return default;
    }

    public ValueTask CmdErase(byte[] p)
    {
        var obj = Obj(p[0]);
        if ((obj.Flags & ViewFlags.Drawn) == 0)
        {
            return default;
        }

        Sprites.EraseRegularSprites();
        bool noUpdateFlag = (obj.Flags & ViewFlags.Update) == 0;
        if (noUpdateFlag)
        {
            Sprites.EraseStaticSprites();
        }

        obj.Flags &= ~ViewFlags.Drawn;

        if (noUpdateFlag)
        {
            Sprites.BuildStaticSpriteList();
            Sprites.DrawStaticSpriteList();
        }

        Sprites.BuildRegularSpriteList();
        Sprites.DrawRegularSpriteList();
        Sprites.ShowSprite(obj);
        return default;
    }

    public ValueTask CmdPosition(byte[] p)
    {
        var obj = Obj(p[0]);
        obj.XPos = obj.XPosPrev = p[1];
        obj.YPos = obj.YPosPrev = p[2];
        return default;
    }

    public ValueTask CmdPositionV1(byte[] p)
    {
        var obj = Obj(p[0]);
        obj.XPos = p[1];
        obj.YPos = p[2];
        return default;
    }

    public ValueTask CmdPositionF(byte[] p)
    {
        var obj = Obj(p[0]);
        obj.XPos = obj.XPosPrev = GetVar(p[1]);
        obj.YPos = obj.YPosPrev = GetVar(p[2]);

        // WORKAROUND: KQ3 Rosella staircase bug (fixed by Sierra in Amiga 2.15). When changing
        // rooms while Rosella follows and ego faces left or right, she is placed on ego's position
        // and can trap the player on stairs. Place her one pixel above, as when facing down. Bug #11996.
        if (Description.Gid == AgiGameId.Kq3 && State.CurLogicNr == 119 && p[0] == 15 &&
            obj.YPos == State.ScreenObjTable[AgiGameState.ScreenObjectsEgoEntry].YPos)
        {
            obj.YPos--;
            obj.YPosPrev--;
        }

        return default;
    }

    public ValueTask CmdPositionFV1(byte[] p)
    {
        var obj = Obj(p[0]);
        obj.XPos = GetVar(p[1]);
        obj.YPos = GetVar(p[2]);
        return default;
    }

    public ValueTask CmdGetPosn(byte[] p)
    {
        var obj = Obj(p[0]);
        SetVar(p[1], (byte)obj.XPos);
        SetVar(p[2], (byte)obj.YPos);
        return default;
    }

    public ValueTask CmdReposition(byte[] p)
    {
        var obj = Obj(p[0]);
        int dx = (sbyte)GetVar(p[1]);
        int dy = (sbyte)GetVar(p[2]);
        obj.Flags |= ViewFlags.UpdatePos;

        obj.XPos = dx < 0 && obj.XPos < -dx ? 0 : obj.XPos + dx;
        obj.YPos = dy < 0 && obj.YPos < -dy ? 0 : obj.YPos + dy;

        FixPosition(obj);
        return default;
    }

    public ValueTask CmdRepositionV1(byte[] p)
    {
        var obj = Obj(p[0]);
        obj.XPosPrev = obj.XPos;
        obj.YPosPrev = obj.YPos;
        obj.Flags |= ViewFlags.UpdatePos;
        obj.XPos = (obj.XPos + p[1]) & 0xFF;
        obj.YPos = (obj.YPos + p[2]) & 0xFF;
        return default;
    }

    public ValueTask CmdRepositionTo(byte[] p)
    {
        var obj = Obj(p[0]);
        obj.XPos = p[1];
        obj.YPos = p[2];
        obj.Flags |= ViewFlags.UpdatePos;
        FixPosition(obj);
        return default;
    }

    public ValueTask CmdRepositionToF(byte[] p)
    {
        var obj = Obj(p[0]);
        obj.XPos = GetVar(p[1]);
        obj.YPos = GetVar(p[2]);
        obj.Flags |= ViewFlags.UpdatePos;
        FixPosition(obj);
        return default;
    }

    public ValueTask CmdAddToPic(byte[] p)
    {
        Sprites.AddToPic(p[0], p[1], p[2], p[3], p[4], p[5], p[6]);
        return default;
    }

    public ValueTask CmdAddToPicV1(byte[] p)
    {
        Sprites.AddToPic(p[0], p[1], p[2], p[3], p[4], p[5], -1);
        return default;
    }

    public ValueTask CmdAddToPicF(byte[] p)
    {
        Sprites.AddToPic(GetVar(p[0]), GetVar(p[1]), GetVar(p[2]), GetVar(p[3]), GetVar(p[4]), GetVar(p[5]), GetVar(p[6]));
        return default;
    }

    public ValueTask CmdForceUpdate(byte[] p)
    {
        Sprites.EraseSprites();
        Sprites.BuildAllSpriteLists();
        Sprites.DrawAllSpriteLists();
        Sprites.ShowAllSpriteLists();
        return default;
    }

    public ValueTask CmdReverseLoop(byte[] p)
    {
        var obj = Obj(p[0]);
        obj.Cycle = CycleType.RevLoop;
        obj.Flags |= ViewFlags.DontUpdate | ViewFlags.Update | ViewFlags.Cycling;
        obj.SetLoopFlag(p[1]);
        SetFlag(obj.LoopFlag, false);
        CyclerActivated(obj);
        return default;
    }

    public ValueTask CmdReverseLoopV1(byte[] p)
    {
        var obj = Obj(p[0]);
        obj.Cycle = CycleType.RevLoop;
        SetCel(obj, 0);
        obj.Flags |= ViewFlags.DontUpdate | ViewFlags.Update | ViewFlags.Cycling;
        obj.SetLoopFlag(p[1]);
        return default;
    }

    public ValueTask CmdEndOfLoop(byte[] p)
    {
        var obj = Obj(p[0]);
        obj.Cycle = CycleType.EndOfLoop;
        obj.Flags |= ViewFlags.DontUpdate | ViewFlags.Update | ViewFlags.Cycling;
        obj.SetLoopFlag(p[1]);
        SetFlag(obj.LoopFlag, false);
        CyclerActivated(obj);
        return default;
    }

    public ValueTask CmdEndOfLoopV1(byte[] p)
    {
        var obj = Obj(p[0]);
        obj.Cycle = CycleType.EndOfLoop;
        SetCel(obj, 0);
        obj.Flags |= ViewFlags.DontUpdate | ViewFlags.Update | ViewFlags.Cycling;
        obj.SetLoopFlag(p[1]);
        return default;
    }

    public ValueTask CmdBlock(byte[] p)
    {
        State.Block.Active = true;
        State.Block.X1 = p[0];
        State.Block.Y1 = p[1];
        State.Block.X2 = p[2];
        State.Block.Y2 = p[3];
        return default;
    }

    public ValueTask CmdUnblock(byte[] p)
    {
        State.Block.Active = false;
        return default;
    }

    public ValueTask CmdNormalMotion(byte[] p)
    {
        Obj(p[0]).MotionType = MotionType.Normal;
        return default;
    }

    public ValueTask CmdStopMotion(byte[] p)
    {
        var obj = Obj(p[0]);
        obj.Direction = 0;
        obj.MotionType = MotionType.Normal;
        if (p[0] == 0)
        {
            SetVar(AgiVar.EgoDirection, 0);
            State.PlayerControl = false;
        }

        return default;
    }

    public ValueTask CmdStopMotionV1(byte[] p)
    {
        Obj(p[0]).Flags &= ~ViewFlags.Animated;
        return default;
    }

    public ValueTask CmdStartMotion(byte[] p)
    {
        var obj = Obj(p[0]);
        obj.MotionType = MotionType.Normal;
        if (p[0] == 0)
        {
            SetVar(AgiVar.EgoDirection, 0);
            State.PlayerControl = true;
        }

        return default;
    }

    public ValueTask CmdStartMotionV1(byte[] p)
    {
        Obj(p[0]).Flags |= ViewFlags.Animated;
        return default;
    }

    public ValueTask CmdPlayerControl(byte[] p)
    {
        var ego = State.ScreenObjTable[AgiGameState.ScreenObjectsEgoEntry];
        State.PlayerControl = true;
        if (ego.MotionType != MotionType.Ego)
        {
            ego.MotionType = MotionType.Normal;
        }

        return default;
    }

    public ValueTask CmdProgramControl(byte[] p)
    {
        State.PlayerControl = false;
        return default;
    }

    public ValueTask CmdFollowEgo(byte[] p)
    {
        var obj = Obj(p[0]);
        byte followStepSize = p[1];
        obj.MotionType = MotionType.FollowEgo;
        obj.FollowStepSize = followStepSize <= obj.StepSize ? obj.StepSize : followStepSize;
        obj.FollowFlag = p[2];
        obj.FollowCount = 255;
        SetFlagOrVar(obj.FollowFlag, false);
        obj.Flags |= Description.Version < 0x2000 ? ViewFlags.Update | ViewFlags.Animated : ViewFlags.Update;
        MotionActivated(obj);
        return default;
    }

    public ValueTask CmdMoveObj(byte[] p)
    {
        var obj = Obj(p[0]);
        obj.MotionType = MotionType.MoveObj;
        obj.MoveX = p[1];
        obj.MoveY = p[2];
        obj.MoveStepSize = obj.StepSize;
        obj.MoveFlag = p[4];
        if (p[3] != 0)
        {
            obj.StepSize = p[3];
        }

        SetFlagOrVar(obj.MoveFlag, false);
        obj.Flags |= Description.Version < 0x2000 ? ViewFlags.Update | ViewFlags.Animated : ViewFlags.Update;
        MotionActivated(obj);

        if (p[0] == 0)
        {
            State.PlayerControl = false;
        }

        // AGI 2.272 (ddp, xmas) doesn't call moveObj
        if (Description.Version > 0x2272)
        {
            MoveObj(obj);
        }

        return default;
    }

    public ValueTask CmdMoveObjF(byte[] p)
    {
        var obj = Obj(p[0]);
        byte stepSize = GetVar(p[3]);
        obj.MotionType = MotionType.MoveObj;
        obj.MoveX = GetVar(p[1]);
        obj.MoveY = GetVar(p[2]);
        obj.MoveStepSize = obj.StepSize;
        obj.MoveFlag = p[4];
        if (stepSize != 0)
        {
            obj.StepSize = stepSize;
        }

        SetFlag(obj.MoveFlag, false);
        obj.Flags |= ViewFlags.Update;
        MotionActivated(obj);

        if (p[0] == 0)
        {
            State.PlayerControl = false;
        }

        if (Description.Version > 0x2272)
        {
            MoveObj(obj);
        }

        return default;
    }

    public ValueTask CmdWander(byte[] p)
    {
        var obj = Obj(p[0]);
        if (p[0] == 0)
        {
            State.PlayerControl = false;
        }

        obj.MotionType = MotionType.Wander;
        obj.Flags |= Description.Version < 0x2000 ? ViewFlags.Update | ViewFlags.Animated : ViewFlags.Update;
        MotionActivated(obj);
        return default;
    }

    public ValueTask CmdDistance(byte[] p)
    {
        var obj1 = Obj(p[0]);
        var obj2 = Obj(p[1]);
        int destVarNr = p[2];
        int d;
        if ((obj1.Flags & ViewFlags.Drawn) != 0 && (obj2.Flags & ViewFlags.Drawn) != 0)
        {
            int x1 = obj1.XPos + obj1.XSize / 2;
            int y1 = obj1.YPos;
            int x2 = obj2.XPos + obj2.XSize / 2;
            int y2 = obj2.YPos;
            d = Math.Abs(x1 - x2) + Math.Abs(y1 - y2);
            if (d > 0xFE)
            {
                d = 0xFE;
            }
        }
        else
        {
            d = 0xFF;
        }

        // WORKAROUND: KQ4 zombie bug (#3067). In the graveyards (rooms 16 and 18) at night with the
        // Obsidian Scarab, a zombie rising right next to Rosella would be repelled mid-rise and then
        // never chase her. Report the zombie as far away until it is up and chasing.
        if (Description.Gid == AgiGameId.Kq4 && GetVar(AgiVar.CurrentRoom) is 16 or 18 && destVarNr is >= 221 and <= 223)
        {
            const int zombieChasingEgo = 2;
            byte[] zombieStateVarNumList = [155, 156, (byte)(GetVar(AgiVar.CurrentRoom) == 16 ? 162 : 158)];
            int zombieNum = destVarNr - 221;
            if (GetVar(zombieStateVarNumList[zombieNum]) != zombieChasingEgo)
            {
                d = 0xFF;
            }
        }

        SetVar(destVarNr, (byte)d);
        return default;
    }

    public ValueTask CmdNearWater(byte[] p)
    {
        SetVar(p[1], EgoNearWater(p[0]));
        return default;
    }

    public ValueTask CmdSetItemView(byte[] p) => default; // V1 command

    public ValueTask CmdSetSpeed(byte[] p)
    {
        State.SpeedLevel = GetVar(p[0]);
        return default;
    }
}
