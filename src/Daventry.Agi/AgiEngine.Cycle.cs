using System.Buffers.Binary;
using Daventry.Agi.Graphics;
using Daventry.Agi.Resources;
using Daventry.Core.Hosting;

namespace Daventry.Agi;

/// <summary>
/// The interpreter main loop: initialisation, rooms, the cycle and logic execution. Port of
/// ScummVM's cycle.cpp, the resource and init parts of agi.cpp, and runLogic from op_cmd.cpp.
/// Blocking waits are awaited on the <see cref="Scheduler"/> so the host can tick in between.
/// </summary>
public sealed partial class AgiEngine
{
    private const int CommandBufferSize = 12; // CMD_BSIZE

    private AgiOpCodeEntry[] _opCodes = [];
    private AgiOpCodeEntry[] _opCodesCond = [];
    private bool _restartGame;
    private bool _quitRequested;
    private bool _veryFirstInitialCycle = true;
    private uint _instructionCounter;
    private int _artificialDelayCurrentRoom = -1;
    private int _artificialDelayCurrentPicture = -1;

    /// <summary>Scheduler the interpreter waits on; the engine advances it from <see cref="Tick"/>.</summary>
    public FrameScheduler Scheduler { get; } = new();

    public bool ShouldQuit => _quitRequested;

    public bool RestartRequested => _restartGame;

    /// <summary>Set by the hold.key opcode: cursor keys keep ego moving until released.</summary>
    public bool KeyHoldMode { get; set; }

    public void QuitGame() => _quitRequested = true;

    public void CycleInnerLoopActivate(CycleInnerLoopType loopType)
    {
        State.CycleInnerLoopActive = true;
        State.CycleInnerLoopType = loopType;
    }

    public void CycleInnerLoopInactive() => State.CycleInnerLoopActive = false;

    public bool CycleInnerLoopIsActive => State.CycleInnerLoopActive;

    /// <summary>Sets a flag in AGIv2+, a variable in AGIv1.</summary>
    public void SetFlagOrVar(int flagNr, bool newState)
    {
        if (Description.Version < 0x2000)
        {
            State.Vars[flagNr] = (byte)(newState ? 1 : 0);
        }
        else
        {
            SetFlag(flagNr, newState);
        }
    }

    // ---- inventory objects ----

    public int ObjectGetLocation(int objectNr)
    {
        if (objectNr >= Resources.Objects.Count)
        {
            Host.Log.Warning("agi", $"ObjectGetLocation: Can't access object {objectNr}");
            return 0;
        }

        return Resources.Objects[objectNr].Location;
    }

    public void ObjectSetLocation(int objectNr, int location)
    {
        if (objectNr >= Resources.Objects.Count)
        {
            Host.Log.Warning("agi", $"ObjectSetLocation: Can't access object {objectNr}");
            return;
        }

        Resources.Objects[objectNr].Location = location;
    }

    public string ObjectName(int objectNr)
    {
        if (objectNr < 0 || objectNr >= Resources.Objects.Count)
        {
            Host.Log.Warning("agi", $"ObjectName: Can't access object {objectNr}");
            return string.Empty;
        }

        return Resources.Objects[objectNr].Name;
    }

    // ---- waiting ----

    /// <summary>Lets engine time pass. ScummVM's wait(): events are processed before and after.</summary>
    public async ValueTask WaitAsync(int milliseconds, bool busy = false)
    {
        ProcessHostEvents();
        await Scheduler.Delay(milliseconds);
        ProcessHostEvents();
    }

    // ---- resources ----

    /// <summary>Loads a resource into memory. Port of AgiEngine::loadResource. Returns false on failure.</summary>
    public bool LoadResource(AgiResourceType type, int resourceNr)
    {
        if (resourceNr >= AgiDirectory.MaxEntries)
        {
            return false;
        }

        switch (type)
        {
            case AgiResourceType.Logic:
            {
                var entry = Resources.Directory.Logic[resourceNr];
                if (!entry.IsLoaded)
                {
                    UnloadResource(AgiResourceType.Logic, resourceNr);
                    if (Resources.LoadLogic(resourceNr) is null)
                    {
                        return false;
                    }
                }

                // reset the code pointer in case the logic was cached
                var logic = Resources.Logics[resourceNr]!;
                logic.CIP = logic.SIP;
                return true;
            }

            case AgiResourceType.Picture:
            {
                var entry = Resources.Directory.Picture[resourceNr];
                if (!entry.IsLoaded)
                {
                    UnloadResource(AgiResourceType.Picture, resourceNr);
                    return Resources.Load(AgiResourceType.Picture, resourceNr) is not null;
                }

                return true;
            }

            case AgiResourceType.Sound:
            {
                var entry = Resources.Directory.Sound[resourceNr];
                if (!entry.IsLoaded)
                {
                    var data = Resources.Load(AgiResourceType.Sound, resourceNr);
                    if (data is null)
                    {
                        return false;
                    }

                    if (!Sound.LoadSound(resourceNr, data))
                    {
                        Resources.Unload(AgiResourceType.Sound, resourceNr);
                        return false;
                    }
                }

                return true;
            }

            case AgiResourceType.View:
            {
                var entry = Resources.Directory.View[resourceNr];
                if (!entry.IsLoaded)
                {
                    UnloadResource(AgiResourceType.View, resourceNr);
                    return Resources.LoadView(resourceNr) is not null;
                }

                return true;
            }

            default:
                return false;
        }
    }

    public void UnloadResource(AgiResourceType type, int resourceNr)
    {
        switch (type)
        {
            case AgiResourceType.Logic:
                Resources.Unload(AgiResourceType.Logic, resourceNr);
                break;
            case AgiResourceType.Picture:
                Resources.Unload(AgiResourceType.Picture, resourceNr);
                break;
            case AgiResourceType.View:
                UnloadView(resourceNr);
                break;
            case AgiResourceType.Sound:
                Sound.UnloadSound(resourceNr);
                Resources.Unload(AgiResourceType.Sound, resourceNr);
                break;
        }
    }

    /// <summary>Unloads everything except logic 0.</summary>
    public void UnloadResources()
    {
        for (int i = 1; i < AgiDirectory.MaxEntries; i++)
        {
            UnloadResource(AgiResourceType.Logic, i);
        }

        for (int i = 0; i < AgiDirectory.MaxEntries; i++)
        {
            UnloadResource(AgiResourceType.View, i);
            UnloadResource(AgiResourceType.Picture, i);
            UnloadResource(AgiResourceType.Sound, i);
        }
    }

    /// <summary>Resets the interpreter and loads the game's static data and logic 0. Port of agiInit.</summary>
    public void AgiInit()
    {
        State.AdjMouseX = 0;
        State.AdjMouseY = 0;
        Array.Clear(State.Flags);
        Array.Clear(State.Vars);

        Resources.UnloadAll();
        foreach (var type in Enum.GetValues<AgiResourceType>())
        {
            foreach (var entry in Resources.Directory[type])
            {
                entry.Reset();
            }
        }

        foreach (var obj in State.ScreenObjTable)
        {
            int objectNr = obj.ObjectNr;
            obj.Reset();
            obj.ObjectNr = objectNr;
        }

        State.AddToPicView.Reset();
        State.AddToPicView.ObjectNr = -1;

        Words.ClearEgoWords();
        Gfx.InitPriorityTable();

        // Strings are cleared on startup but not on restart: SQ2 script 98 asks ego's name only
        // once and expects it to survive a restart in string 1 (bug #5673).
        if (!_restartGame)
        {
            State.ClearStrings();
        }

        Host.Log.Info("agi", Description.Version >= 0x3000
            ? $"Emulating Sierra AGI v{(Description.Version >> 12) & 0xF:X}.002.{Description.Version & 0xFFF:X3}"
            : $"Emulating Sierra AGI v{(Description.Version >> 12) & 0xF:X}.{Description.Version & 0xFFF:X3}");

        Resources.Initialize();
        if (!LoadResource(AgiResourceType.Logic, 0))
        {
            throw new AgiResourceException("Logic 0 could not be loaded");
        }

        KeyHoldMode = false;
        _keyHoldModeLastKey = Core.Input.Key.None;
        State.MouseFence = default;

        InGameTimerReset();
    }

    public void AgiDeinit()
    {
        Words.ClearEgoWords();
        UnloadResources();
        UnloadResource(AgiResourceType.Logic, 0);
        State.ClearImageStack();
    }

    // ---- rooms ----

    public async ValueTask NewRoomAsync(int newRoomNr)
    {
        // Gold Rush's copy protection quiz (room 125) is disabled, as the original authors did in
        // the Software Farm re-release; logic 129 is the quiz trigger.
        if (Description.Gid == AgiGameId.Goldrush && State.CurLogicNr == 129)
        {
            newRoomNr = 73;
        }

        var ego = State.ScreenObjTable[AgiGameState.ScreenObjectsEgoEntry];

        await ArtificialDelayTriggerNewRoomAsync(newRoomNr);

        Sound.StopSound();

        for (int i = 0; i < State.ScreenObjTable.Length; i++)
        {
            var obj = State.ScreenObjTable[i];
            obj.ObjectNr = i;
            obj.Flags &= ~(ViewFlags.Animated | ViewFlags.Drawn);
            obj.Flags |= ViewFlags.Update;
            obj.StepTime = 1;
            obj.StepTimeCount = 1;
            obj.CycleTime = 1;
            obj.CycleTimeCount = 1;
            obj.StepSize = 1;
        }

        UnloadResources();

        State.PlayerControl = true;
        State.Block.Active = false;
        State.Horizon = 36;
        SetVar(AgiVar.PreviousRoom, GetVar(AgiVar.CurrentRoom));
        SetVar(AgiVar.CurrentRoom, (byte)newRoomNr);
        SetVar(AgiVar.BorderTouchObject, 0);
        SetVar(AgiVar.BorderCode, 0);
        SetVar(AgiVar.EgoViewResource, ego.CurrentViewNr);

        LoadResource(AgiResourceType.Logic, newRoomNr);

        // reposition ego in the new room
        switch (GetVar(AgiVar.BorderTouchEgo))
        {
            case 1:
                ego.YPos = GfxMgr.ScriptHeight - 1;
                break;
            case 2:
                ego.XPos = 0;
                break;
            case 3:
                ego.YPos = State.Horizon + 1;
                break;
            case 4:
                ego.XPos = GfxMgr.ScriptWidth - ego.XSize;
                break;
        }

        if (Description.Version < 0x2000)
        {
            ego.Flags &= ~ViewFlags.DidntMove;
            LoadResource(AgiResourceType.View, ego.CurrentViewNr);
            SetView(ego, ego.CurrentViewNr);
        }
        else
        {
            if (Description.Version >= 0x3000 && ego.MotionType == MotionType.Ego)
            {
                // only done in AGI3
                ego.MotionType = MotionType.Normal;
                SetVar(AgiVar.EgoDirection, 0);
            }

            SetVar(AgiVar.BorderTouchEgo, 0);
            SetFlag(AgiFlag.NewRoomExec, true);
            State.ExitAllLogics = true;
            Text.StatusDraw();
            Text.PromptRedraw();

            // WORKAROUND: LSL1 leaves a flag set after exiting room 17 by the staircase that ignores
            // priority triggers everywhere, letting the player leave the store without paying (bug #13137).
            if (Description.Gid == AgiGameId.Lsl1)
            {
                SetFlag(36, false);
            }

            // WORKAROUND: KQ3 fish talk in room 31 overlaps flags with the mice conversation on the
            // ship in room 86; clear the mice flags when the voyage starts (bug #15130).
            if (Description.Gid == AgiGameId.Kq3 && newRoomNr == 77)
            {
                for (int flag = 193; flag <= 197; flag++)
                {
                    SetFlag(flag, false);
                }
            }
        }
    }

    public void ResetControllers() => Array.Clear(State.ControllerOccurred);

    // ---- the cycle ----

    public async ValueTask InterpretCycleAsync()
    {
        var ego = State.ScreenObjTable[AgiGameState.ScreenObjectsEgoEntry];

        if (!State.PlayerControl)
        {
            SetVar(AgiVar.EgoDirection, ego.Direction);
        }
        else
        {
            ego.Direction = GetVar(AgiVar.EgoDirection);
        }

        CheckAllMotions();

        byte oldScore = GetVar(AgiVar.Score);
        bool oldSound = GetFlag(AgiFlag.SoundOn);

        ResetGetVarSecondsHeuristic();

        State.ExitAllLogics = false;
        while (await RunLogicAsync(0) == 0 && !(ShouldQuit || _restartGame))
        {
            SetVar(AgiVar.WordNotFound, 0);
            SetVar(AgiVar.BorderTouchObject, 0);
            SetVar(AgiVar.BorderCode, 0);
            oldScore = GetVar(AgiVar.Score);
            SetFlag(AgiFlag.EnteredCli, false);
            State.ExitAllLogics = false;
            _veryFirstInitialCycle = false;
            ArtificialDelayCycleDone();
            ResetControllers();

            // Reset the mouse button after new.room; AGIMOUSE games that call new.room on a click
            // would otherwise loop forever on variable 27 (bug #10737).
            Mouse.Button = AgiMouseButton.Up;
        }

        _veryFirstInitialCycle = false;
        ArtificialDelayCycleDone();
        ResetControllers();

        ego.Direction = GetVar(AgiVar.EgoDirection);

        if (GetVar(AgiVar.Score) != oldScore || GetFlag(AgiFlag.SoundOn) != oldSound)
        {
            Text.StatusDraw();
        }

        SetVar(AgiVar.BorderTouchObject, 0);
        SetVar(AgiVar.BorderCode, 0);
        SetFlag(AgiFlag.NewRoomExec, false);
        SetFlag(AgiFlag.RestartGame, false);
        SetFlag(AgiFlag.RestoreJustRan, false);

        if (State.GfxMode)
        {
            UpdateScreenObjTable();
        }

        Gfx.UpdateScreen();
    }

    /// <summary>Waits a tick, polls input and routes it to the game or the active inner loop. Returns the key, or 0.</summary>
    public async ValueTask<ushort> ProcessAgiEventsAsync()
    {
        var ego = State.ScreenObjTable[AgiGameState.ScreenObjectsEgoEntry];

        await WaitAsync(10);
        ushort key = DoPollKeyboard();

        if (!CycleInnerLoopIsActive)
        {
            // click-to-walk mouse interface
            if (State.PlayerControl && (ego.Flags & ViewFlags.AdjEgoXY) != 0)
            {
                int toX = ego.MoveX;
                int toY = ego.MoveY;

                // AGI Mouse games target ego's bottom left corner; Amiga games the bottom centre.
                if (Description.Platform == AgiPlatform.Amiga)
                {
                    toX -= ego.XSize / 2;
                }

                toX += State.AdjMouseX;
                toY += State.AdjMouseY;

                ego.Direction = (byte)GetDirection(ego.XPos, ego.YPos, toX, toY, ego.StepSize);
                if (ego.Direction == 0)
                {
                    InDestination(ego);
                }
            }
        }

        HandleMouseClicks(ref key);

        if (!CycleInnerLoopIsActive)
        {
            if (key != 0)
            {
                if (!HandleController(key))
                {
                    // only set the key variable when no controller or direction consumed it
                    SetVar(AgiVar.Key, (byte)(key & 0xFF));
                    if (Text.PromptIsEnabled())
                    {
                        Text.PromptKeyPress(key);
                    }
                }
            }

            if (Menu.DelayedExecuteActive())
            {
                await Menu.ExecuteAsync();
            }
        }
        else
        {
            switch (State.CycleInnerLoopType)
            {
                case CycleInnerLoopType.GetString:
                case CycleInnerLoopType.GetNumber:
                    if (key != 0)
                    {
                        Text.StringKeyPress(key);
                    }

                    break;
                case CycleInnerLoopType.Inventory:
                    if (key != 0)
                    {
                        Inventory.KeyPress(key);
                    }

                    break;
                case CycleInnerLoopType.MenuViaKeyboard:
                    if (key != 0)
                    {
                        Menu.KeyPress(key);
                    }

                    break;
                case CycleInnerLoopType.MenuViaMouse:
                    Menu.MouseEvent(key);
                    break;
                case CycleInnerLoopType.SystemUiSelectSavedGameSlot:
                    if (key != 0)
                    {
                        SystemUI.SavedGameSlotKeyPress(key);
                    }

                    break;
                case CycleInnerLoopType.SystemUiVerification:
                    SystemUI.AskForVerificationKeyPress(key);
                    break;
                case CycleInnerLoopType.MessageBox:
                    if (key != 0)
                    {
                        Text.MessageBoxKeyPress(key);
                    }

                    break;
            }
        }

        Gfx.UpdateScreen();
        return key;
    }

    public async ValueTask PlayGameAsync()
    {
        Sound.StopSound();

        // Reset the priority screen to 4, otherwise the fan game Nick's Quest loops forever because
        // it draws views before the first background picture (bug #5916).
        Gfx.Clear(0, 4);

        State.Horizon = 36;
        State.PlayerControl = false;

        SetFlag(AgiFlag.LogicZeroFirstTime, true); // not in 2.917
        SetFlag(AgiFlag.NewRoomExec, true); // needed for MUMG and SQ2
        SetFlag(AgiFlag.SoundOn, true);
        // do not set the time delay variable, the original did not (data segment value 0)

        State.GfxMode = true;
        Text.PromptRow_Set(22);

        SetFlag(AgiFlag.EnteredCli, false);
        SetFlag(AgiFlag.SaidAcceptedInput, false);
        SetVar(AgiVar.WordNotFound, 0);
        SetVar(AgiVar.Key, 0);

        ArtificialDelayReset();

        do
        {
            await ProcessAgiEventsAsync();
            InGameTimerUpdate();

            byte timeDelay;
            if (Description.Platform == AgiPlatform.Apple2 || Description.Version < 0x2000)
            {
                // Apple II had no speed control and AGIv1 used set.speed; both use the internal speed level
                timeDelay = State.SpeedLevel;
            }
            else
            {
                timeDelay = GetVar(AgiVar.TimeDelay);
            }

            // An original cycle was 50 ms (20 per second): delay 1 was 20 fps, 2 was 10 fps, and 0 was
            // unlimited. Our counter runs at 25 ms, so double the delay and treat 0 as 40 fps.
            timeDelay = (byte)(timeDelay * 2);
            if (timeDelay == 0)
            {
                timeDelay = 1;
            }

            if (_passedPlayTimeCycles >= timeDelay)
            {
                InGameTimerResetPassedCycles();
                await InterpretCycleAsync();

                SetFlag(AgiFlag.EnteredCli, false);
                SetFlag(AgiFlag.SaidAcceptedInput, false);
                SetVar(AgiVar.WordNotFound, 0);
                SetVar(AgiVar.Key, 0);
            }
        }
        while (!(ShouldQuit || _restartGame));

        Sound.StopSound();
    }

    /// <summary>Runs the game until it quits, restarting as requested. Port of runGame.</summary>
    public async Task RunGameAsync()
    {
        InGameTimerReset();
        do
        {
            AgiInit();

            if (_restartGame)
            {
                SetFlag(AgiFlag.RestartGame, true);
                InGameTimerReset();
                _restartGame = false;
            }

            switch (Description.Platform)
            {
                case AgiPlatform.AtariSt:
                    SetVar(AgiVar.Computer, AgiComputerType.AtariSt);
                    SetVar(AgiVar.SoundGenerator, AgiSoundType.Pc);
                    break;
                case AgiPlatform.Amiga:
                    SetVar(AgiVar.Computer, AgiComputerType.Amiga);
                    SetVar(AgiVar.SoundGenerator, AgiSoundType.Tandy);
                    break;
                case AgiPlatform.Apple2:
                    SetVar(AgiVar.Computer, AgiComputerType.Apple2);
                    SetVar(AgiVar.SoundGenerator, AgiSoundType.Pc);
                    break;
                case AgiPlatform.Apple2Gs:
                    SetVar(AgiVar.Computer, AgiComputerType.Apple2Gs);
                    SetVar(AgiVar.SoundGenerator, (Description.Features & AgiGameFeatures.Apple2GsOldSound) != 0 ? AgiSoundType.Apple2GsOld : AgiSoundType.Tandy);
                    break;
                default:
                    SetVar(AgiVar.Computer, AgiComputerType.Pc);
                    SetVar(AgiVar.SoundGenerator, AgiSoundType.Pc);
                    break;
            }

            // Hercules reports EGA: some games place text differently for Hercules and we support the regular prompt.
            SetVar(AgiVar.Monitor, Gfx.RenderMode == AgiRenderMode.Cga ? AgiMonitorType.Cga : AgiMonitorType.Ega);
            SetVar(AgiVar.FreePages, 180);
            SetVar(AgiVar.MaxInputCharacters, 38);
            Text.PromptDisable();

            await PlayGameAsync();
            AgiDeinit();
        }
        while (_restartGame);
    }

    /// <summary>Executes a logic script. Returns 1 on return, 0 after new.room. Port of runLogic.</summary>
    public async ValueTask<int> RunLogicAsync(int logicNr)
    {
        State.LogicList[0] = 0;
        State.MaxLogics = 0;
        State.ExecStack.Add(new ScriptPos(logicNr, 0));

        if (!Resources.Directory.Logic[logicNr].IsLoaded)
        {
            LoadResource(AgiResourceType.Logic, logicNr);
        }

        var logic = Resources.Logics[logicNr]
            ?? throw new AgiResourceException($"Logic {logicNr} could not be loaded");

        State.CurLogicNr = logicNr;
        State.CurLogic = logic;
        logic.CIP = logic.SIP;

        var parameters = new byte[CommandBufferSize];
        var code = logic.Data;

        while (State.CurLogic!.CIP < logic.Size && !(ShouldQuit || _restartGame))
        {
            ProcessHostEvents();
            _instructionCounter++;

            var current = State.CurLogic;
            State.ExecStack[^1] = new ScriptPos(logicNr, current.CIP);

            byte op = code[current.CIP++];
            switch (op)
            {
                case 0xFF: // if
                    await TestIfCodeAsync(logicNr);
                    break;

                case 0xFE: // goto, the offset is relative to the end of the 2-byte operand
                    current.CIP += 2 + (short)BinaryPrimitives.ReadInt16LittleEndian(code.AsSpan(current.CIP));
                    break;

                case 0x00: // return
                    State.ExecStack.RemoveAt(State.ExecStack.Count - 1);
                    return 1;

                default:
                {
                    var entry = _opCodes[op];
                    if (entry.Function is null)
                    {
                        throw new AgiResourceException($"Illegal opcode {op:X2} ({op}) in logic {State.CurLogicNr}, ip {current.CIP}");
                    }

                    int size = entry.ParameterSize;
                    Array.Clear(parameters);
                    int available = Math.Min(size, code.Length - current.CIP);
                    if (available > 0)
                    {
                        Array.Copy(code, current.CIP, parameters, 0, available);
                    }

                    await entry.Function(this, parameters);
                    current.CIP += size;
                    break;
                }
            }

            if (SecondsBusyLoopDetected)
            {
                // A script is spinning on the seconds variable (Manhunter 1 title screen). ScummVM
                // waits 10 ms inside getVar; here the wait happens after the instruction instead,
                // which is the only place this cooperative interpreter can suspend.
                SecondsBusyLoopDetected = false;
                await WaitAsync(10, true);
            }

            if (State.ExitAllLogics)
            {
                break;
            }
        }

        State.ExecStack.RemoveAt(State.ExecStack.Count - 1);
        return 0; // after new.room
    }

    public void RedrawScreen()
    {
        State.GfxMode = true;
        Gfx.SetPalette(true);
        Text.CharAttrib_Set(Text.TextAttrib.Foreground, Text.TextAttrib.Background);
        Gfx.ClearDisplay(0);
        Picture.ShowPicture();
        Text.StatusDraw();
        Text.PromptRedraw();
    }

    /// <summary>Shakes the display. The original shifted the screen; here each repeat is a short pause until the display offset port lands.</summary>
    public async ValueTask ShakeScreenAsync(int repeatCount)
    {
        for (int i = 0; i < repeatCount; i++)
        {
            await WaitAsync(66);
        }
    }

    // ---- artificial delays (agi.cpp) ----

    public void NonBlockingTextIsShown()
    {
        State.NonBlockingTextShown = true;
        State.NonBlockingTextCyclesLeft = 2; // one additional script cycle is counted too
    }

    public void NonBlockingTextForget()
    {
        State.NonBlockingTextShown = false;
        State.NonBlockingTextCyclesLeft = 0;
    }

    private void ArtificialDelayReset()
    {
        NonBlockingTextForget();
        _artificialDelayCurrentRoom = -1;
        _artificialDelayCurrentPicture = -1;
    }

    private void ArtificialDelayCycleDone()
    {
        if (State.NonBlockingTextCyclesLeft > 0)
        {
            State.NonBlockingTextCyclesLeft--;
            if (State.NonBlockingTextCyclesLeft == 0)
            {
                State.NonBlockingTextShown = false;
            }
        }
    }

    /// <summary>
    /// Rooms that change right after showing non-blocking text relied on slow loading (Space Quest
    /// 2 intro). Loading is instant here, so a 2 second delay keeps the text readable.
    /// </summary>
    private async ValueTask ArtificialDelayTriggerNewRoomAsync(int newRoomNr)
    {
        if (!State.AutomaticRestoreGame)
        {
            int millisecondsDelay = 0;
            if (State.NonBlockingTextShown && newRoomNr != _artificialDelayCurrentRoom)
            {
                millisecondsDelay = 2000;
            }

            if (millisecondsDelay > 0)
            {
                await WaitAsync(millisecondsDelay, true);
                State.NonBlockingTextShown = false;
            }
        }

        _artificialDelayCurrentRoom = newRoomNr;
    }

    private void ArtificialDelayTriggerDrawPicture(int newPictureNr)
    {
        // The picture variant of the delay above would need to block inside a synchronous opcode;
        // the room delay covers the known cases, so only the bookkeeping is kept here.
        _artificialDelayCurrentPicture = newPictureNr;
    }

    // ---- save and restore (saveload.cpp, not ported yet) ----

    public ValueTask<bool> SaveGameAutomaticAsync() => ValueTask.FromResult(false);

    public ValueTask<bool> LoadGameAutomaticAsync() => ValueTask.FromResult(false);

    public async ValueTask SaveGameDialogAsync() => await Text.MessageBoxAsync("Saving is not available yet.");

    public async ValueTask LoadGameDialogAsync() => await Text.MessageBoxAsync("Restoring is not available yet.");
}
