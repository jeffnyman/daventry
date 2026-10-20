using Daventry.Agi.Graphics;
using Daventry.Agi.Resources;
using Daventry.Core.Hosting;

namespace Daventry.Agi;

/// <summary>Screen object view handling. Port of ScummVM's view.cpp.</summary>
public sealed partial class AgiEngine
{
    // loops to use according to direction and number of loops in the view resource
    private static readonly byte[] LoopTable2 = [0x04, 0x04, 0x00, 0x00, 0x00, 0x04, 0x01, 0x01, 0x01];
    private static readonly byte[] LoopTable4 = [0x04, 0x03, 0x00, 0x00, 0x00, 0x02, 0x01, 0x01, 0x01];

    /// <summary>Advances a screen object's cel according to its cycle type.</summary>
    public void UpdateView(ScreenObjEntry screenObj)
    {
        if ((screenObj.Flags & ViewFlags.DontUpdate) != 0)
        {
            screenObj.Flags &= ~ViewFlags.DontUpdate;
            return;
        }

        int celNr = screenObj.CurrentCelNr;
        int lastCelNr = screenObj.CelCount - 1;

        switch (screenObj.Cycle)
        {
            case CycleType.Normal:
                celNr++;
                if (celNr > lastCelNr)
                {
                    celNr = 0;
                }

                break;

            case CycleType.EndOfLoop:
                if (celNr < lastCelNr)
                {
                    if (++celNr != lastCelNr)
                    {
                        break;
                    }
                }

                if (!screenObj.IgnoreLoopFlag)
                {
                    SetFlag(screenObj.LoopFlag, true);
                }
                else
                {
                    Host.Log.Warning("agi", $"EndOfLoop: skip setting flag {screenObj.LoopFlag}");
                }

                screenObj.Flags &= ~ViewFlags.Cycling;
                screenObj.Direction = 0;
                screenObj.Cycle = CycleType.Normal;
                break;

            case CycleType.RevLoop:
                if (celNr != 0)
                {
                    celNr--;
                    if (celNr != 0)
                    {
                        break;
                    }
                }

                if (!screenObj.IgnoreLoopFlag)
                {
                    SetFlag(screenObj.LoopFlag, true);
                }
                else
                {
                    Host.Log.Warning("agi", $"RevLoop: skip setting flag {screenObj.LoopFlag}");
                }

                screenObj.Flags &= ~ViewFlags.Cycling;
                screenObj.Direction = 0;
                screenObj.Cycle = CycleType.Normal;
                break;

            case CycleType.Reverse:
                celNr = celNr == 0 ? lastCelNr : celNr - 1;
                break;
        }

        SetCel(screenObj, celNr);
    }

    /// <summary>Unloads a view and rebuilds the sprite lists, since sprites may reference it.</summary>
    public void UnloadView(int viewNr)
    {
        if (!Resources.Directory.View[viewNr].IsLoaded)
        {
            return;
        }

        // Rebuild sprite list, see Sarien bug #779302
        Sprites.EraseSprites();
        Resources.Unload(AgiResourceType.View, viewNr);
        Sprites.BuildAllSpriteLists();
        Sprites.DrawAllSpriteLists();
    }

    /// <summary>Sets a screen object to use the given view resource.</summary>
    public void SetView(ScreenObjEntry screenObj, int viewNr)
    {
        var view = Resources.Views[viewNr];
        if (!Resources.Directory.View[viewNr].IsLoaded || view is null)
        {
            // View not loaded, probably a game script bug. The original interpreter bombed out with
            // "view not loaded, Press ESC to quit"; this happens in Larry 1 on the Apple IIgs right
            // after getting beaten up by the taxi driver. Load it now instead.
            Host.Log.Warning("agi", $"SetView() called on screen object {screenObj.ObjectNr} to use view {viewNr}, but view not loaded; loading it now");
            view = Resources.LoadView(viewNr)
                ?? throw new AgiResourceException($"SetView() called to set view {viewNr} for screen object {screenObj.ObjectNr}, which is not loaded and loading failed");
        }

        screenObj.ViewResource = view;
        screenObj.CurrentViewNr = (byte)viewNr;
        screenObj.LoopCount = (byte)view.LoopCount;
        screenObj.ViewReplaced = true;

        if (Description.Version < 0x2000)
        {
            screenObj.StepSize = view.HeaderStepSize;
            screenObj.CycleTime = view.HeaderCycleTime;
            screenObj.CycleTimeCount = 0;
        }

        SetLoop(screenObj, screenObj.CurrentLoopNr >= screenObj.LoopCount ? 0 : screenObj.CurrentLoopNr);
    }

    /// <summary>Sets a screen object to use the given loop of its current view.</summary>
    public void SetLoop(ScreenObjEntry screenObj, int loopNr)
    {
        if (screenObj.ViewResource is null || !Resources.Directory.View[screenObj.CurrentViewNr].IsLoaded)
        {
            throw new InvalidOperationException($"SetLoop() called on screen object {screenObj.ObjectNr}, which has no loaded view resource assigned to it");
        }

        if (screenObj.LoopCount == 0)
        {
            Host.Log.Warning("agi", $"SetLoop() called on screen object {screenObj.ObjectNr}, which has no loops (view {screenObj.CurrentViewNr})");
            return;
        }

        if (loopNr >= screenObj.LoopCount)
        {
            // Requested loop doesn't exist: clip instead of erroring out (needed by Manhunter 1 when
            // leaving the arcade machine). KQ1 bowing to the king in room 53 sets loop 1 on view 71,
            // which has one loop, before setting view 0; detect that and set the view first (bug #7045).
            if (Description.Gid == AgiGameId.Kq1 && screenObj.CurrentViewNr == 71 && loopNr == 1)
            {
                SetView(screenObj, 0);
            }
            else
            {
                int requestedLoopNr = loopNr;
                loopNr = screenObj.LoopCount - 1;
                Host.Log.Warning("agi", $"Non-existent loop requested for screen object {screenObj.ObjectNr}: view {screenObj.CurrentViewNr}, requested loop {requestedLoopNr} -> clipped to loop {loopNr}");
            }
        }

        var curViewLoop = screenObj.ViewResource.Loops[loopNr];
        screenObj.CurrentLoopNr = (byte)loopNr;
        screenObj.LoopData = curViewLoop;
        screenObj.CelCount = (byte)curViewLoop.CelCount;

        SetCel(screenObj, screenObj.CurrentCelNr >= screenObj.CelCount ? 0 : screenObj.CurrentCelNr);
    }

    /// <summary>Sets a screen object to use the given cel of its current loop.</summary>
    public void SetCel(ScreenObjEntry screenObj, int celNr)
    {
        if (screenObj.ViewResource is null || !Resources.Directory.View[screenObj.CurrentViewNr].IsLoaded)
        {
            throw new InvalidOperationException($"SetCel() called on screen object {screenObj.ObjectNr}, which has no loaded view resource assigned to it");
        }

        if (screenObj.LoopCount == 0)
        {
            Host.Log.Warning("agi", $"SetCel() called on screen object {screenObj.ObjectNr}, which has no loops (view {screenObj.CurrentViewNr})");
            return;
        }

        var curViewLoop = screenObj.ViewResource.Loops[screenObj.CurrentLoopNr];

        // Prevents a crash in KQ1; not in the Sierra interpreter.
        if (curViewLoop.CelCount == 0)
        {
            Host.Log.Warning("agi", $"SetCel() called on screen object {screenObj.ObjectNr}, which has no cels (view {screenObj.CurrentViewNr})");
            return;
        }

        if (celNr >= screenObj.CelCount)
        {
            // Requested cel doesn't exist: clip instead of erroring out. Required for KQ3 on the Apple
            // IIgs, walking the planks death cutscene (bug #5832), a game bug.
            int requestedCelNr = celNr;
            celNr = screenObj.CelCount - 1;
            Host.Log.Warning("agi", $"Non-existent cel requested for screen object {screenObj.ObjectNr}: view {screenObj.CurrentViewNr}, loop {screenObj.CurrentLoopNr}, requested cel {requestedCelNr} -> clipped to cel {celNr}");
        }

        screenObj.CurrentCelNr = (byte)celNr;
        var curViewCel = curViewLoop.Cels[celNr];
        screenObj.CelData = curViewCel;
        screenObj.XSize = curViewCel.Width;
        screenObj.YSize = curViewCel.Height;

        ClipViewCoordinates(screenObj);
    }

    /// <summary>Keeps a screen object wholly on screen and below the horizon unless it ignores it.</summary>
    public void ClipViewCoordinates(ScreenObjEntry screenObj)
    {
        if (screenObj.XPos + screenObj.XSize > GfxMgr.ScriptWidth)
        {
            screenObj.Flags |= ViewFlags.UpdatePos;
            screenObj.XPos = GfxMgr.ScriptWidth - screenObj.XSize;
        }

        if (screenObj.YPos - screenObj.YSize + 1 < 0)
        {
            screenObj.Flags |= ViewFlags.UpdatePos;
            screenObj.YPos = screenObj.YSize - 1;
        }

        if (screenObj.YPos <= State.Horizon && (screenObj.Flags & ViewFlags.IgnoreHorizon) == 0)
        {
            screenObj.Flags |= ViewFlags.UpdatePos;
            screenObj.YPos = State.Horizon + 1;
        }

        if (Description.Version < 0x2000)
        {
            screenObj.Flags |= ViewFlags.DontUpdate;
        }
    }

    public void StartUpdate(ScreenObjEntry screenObj)
    {
        if ((screenObj.Flags & ViewFlags.Update) == 0)
        {
            Sprites.EraseSprites();
            screenObj.Flags |= ViewFlags.Update;
            Sprites.BuildAllSpriteLists();
            Sprites.DrawAllSpriteLists();
        }
    }

    public void StopUpdate(ScreenObjEntry screenObj)
    {
        if ((screenObj.Flags & ViewFlags.Update) != 0)
        {
            Sprites.EraseSprites();
            screenObj.Flags &= ~ViewFlags.Update;
            Sprites.BuildAllSpriteLists();
            Sprites.DrawAllSpriteLists();
        }
    }

    /// <summary>Called at the end of each interpreter cycle to update loops, cycle cels, move objects and redraw sprites.</summary>
    public void UpdateScreenObjTable()
    {
        int changeCount = 0;
        foreach (var screenObj in State.ScreenObjTable)
        {
            if (!screenObj.HasFlags(ViewFlags.Animated | ViewFlags.Update | ViewFlags.Drawn))
            {
                continue;
            }

            changeCount++;

            int loopNr = 4;
            if ((screenObj.Flags & ViewFlags.FixLoop) == 0)
            {
                switch (screenObj.LoopCount)
                {
                    case 2:
                    case 3:
                        loopNr = LoopTable2[screenObj.Direction];
                        break;
                    case 4:
                        loopNr = LoopTable4[screenObj.Direction];
                        break;
                    default:
                        // for KQ4
                        if (Description.Version == 0x3086 || Description.Gid == AgiGameId.Kq4)
                        {
                            loopNr = LoopTable4[screenObj.Direction];
                        }

                        break;
                }
            }

            // AGI 2.272 (ddp, xmas) doesn't test step_time_count!
            if (loopNr != 4 && loopNr != screenObj.CurrentLoopNr)
            {
                if (Description.Version <= 0x2272 || screenObj.StepTimeCount == 1)
                {
                    SetLoop(screenObj, loopNr);
                }
            }

            if ((screenObj.Flags & ViewFlags.Cycling) != 0 && screenObj.CycleTimeCount != 0)
            {
                screenObj.CycleTimeCount--;
                if (screenObj.CycleTimeCount == 0)
                {
                    UpdateView(screenObj);
                    screenObj.CycleTimeCount = screenObj.CycleTime;
                }
            }
        }

        if (changeCount > 0)
        {
            Sprites.EraseRegularSprites();
            UpdatePosition();
            Sprites.BuildRegularSpriteList();
            Sprites.DrawRegularSpriteList();
            Sprites.ShowRegularSpriteList();
            State.ScreenObjTable[AgiGameState.ScreenObjectsEgoEntry].Flags &= ~(ViewFlags.OnWater | ViewFlags.OnLand);
        }
    }

    public bool IsEgoView(ScreenObjEntry screenObj) => ReferenceEquals(screenObj, State.ScreenObjTable[AgiGameState.ScreenObjectsEgoEntry]);
}
