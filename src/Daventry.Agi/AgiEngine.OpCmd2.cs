using Daventry.Agi.Resources;
using Daventry.Agi.Text;
using Daventry.Core.Hosting;

namespace Daventry.Agi;

/// <summary>Command opcodes: text, sound, menus, input, system. Port of ScummVM's op_cmd.cpp, second half.</summary>
public sealed partial class AgiEngine
{
    private bool VersionLacks(ushort minimum, string name, bool excluded2440 = true)
    {
        if (Description.Version < minimum || (excluded2440 && Description.Version == 0x2440))
        {
            Host.Log.Warning("agi", $"{name} called, although not available for current AGI version");
            return true;
        }

        return false;
    }

    // ---- dialogue and windows ----

    public ValueTask CmdOpenDialogue(byte[] p)
    {
        Text.DialogueOpen();
        return default;
    }

    public ValueTask CmdCloseDialogue(byte[] p)
    {
        Text.DialogueClose();
        return default;
    }

    public ValueTask CmdCloseWindow(byte[] p)
    {
        Text.CloseWindow();
        return default;
    }

    public ValueTask CmdStatusLineOn(byte[] p)
    {
        Text.StatusEnable();
        Text.StatusDraw();
        return default;
    }

    public ValueTask CmdStatusLineOff(byte[] p)
    {
        Text.StatusDisable();
        Text.StatusClear();
        return default;
    }

    public async ValueTask CmdShowObj(byte[] p) => await Sprites.ShowObjectAsync(p[0]);

    public async ValueTask CmdShowObjV(byte[] p) => await Sprites.ShowObjectAsync(GetVar(p[0]));

    // ---- sound ----

    public async ValueTask CmdSound(byte[] p)
    {
        int resourceNr = p[0];
        int flagNr = p[1];
        if (Description.Platform is AgiPlatform.Apple2 or AgiPlatform.CoCo3)
        {
            // Sound playback blocked the interpreter on these platforms until the sound finished or a key was pressed.
            if (GetFlag(AgiFlag.SoundOn))
            {
                Sound.StartSound(resourceNr, flagNr);
                await WaitAnyKeyOrFinishedSoundAsync();
                Sound.StopSound();
            }

            SetFlagOrVar(flagNr, true);
        }
        else
        {
            Sound.StartSound(resourceNr, flagNr);
        }
    }

    public ValueTask CmdStopSound(byte[] p)
    {
        Sound.StopSound();
        return default;
    }

    public ValueTask CmdDiscardSound(byte[] p) => default; // Apple IIgs only; sound resources were large and scripts unloaded them

    // ---- menus ----

    public ValueTask CmdMenuInput(byte[] p)
    {
        if (GetFlag(AgiFlag.MenusAccessible))
        {
            Menu.DelayedExecuteViaKeyboard();
        }

        return default;
    }

    public ValueTask CmdEnableItem(byte[] p)
    {
        Menu.ItemEnable(p[0]);
        return default;
    }

    public ValueTask CmdDisableItem(byte[] p)
    {
        Menu.ItemDisable(p[0]);
        return default;
    }

    public ValueTask CmdSubmitMenu(byte[] p)
    {
        Menu.Submit();
        return default;
    }

    public ValueTask CmdSetMenu(byte[] p)
    {
        var text = State.CurLogic?.GetText(p[0]);
        if (text is not null)
        {
            Menu.AddMenu(text);
        }

        return default;
    }

    public ValueTask CmdSetMenuItem(byte[] p)
    {
        var text = State.CurLogic?.GetText(p[0]);
        if (text is not null)
        {
            Menu.AddMenuItem(text, p[1]);
        }

        return default;
    }

    public ValueTask CmdAllowMenu(byte[] p)
    {
        if (Description.Version < 0x3098)
        {
            Host.Log.Warning("agi", "allow.menu called, although not available for current AGI version");
            return default;
        }

        if (p[0] != 0)
        {
            Menu.AccessAllow();
        }
        else
        {
            Menu.AccessDeny();
        }

        return default;
    }

    // ---- scan start, saving, restarting, quitting ----

    public ValueTask CmdSetScanStart(byte[] p)
    {
        State.CurLogic!.SIP = State.CurLogic.CIP;
        return default;
    }

    public ValueTask CmdResetScanStart(byte[] p)
    {
        State.CurLogic!.SIP = 2;
        return default;
    }

    public async ValueTask CmdSaveGame(byte[] p)
    {
        if (Description.Version >= 0x2272)
        {
            Sound.StopSound(); // only done since 2.272
        }

        if (State.AutomaticSave && await SaveGameAutomaticAsync())
        {
            return;
        }

        await SaveGameDialogAsync();
    }

    public async ValueTask CmdLoadGame(byte[] p)
    {
        if (Description.Version >= 0x2272)
        {
            Sound.StopSound();
        }

        if (State.AutomaticSave && await LoadGameAutomaticAsync())
        {
            return;
        }

        await LoadGameDialogAsync();
    }

    public ValueTask CmdInitDisk(byte[] p) => default;

    public ValueTask CmdLog(byte[] p)
    {
        var message = State.CurLogic?.GetText(p[0]);
        if (message is not null)
        {
            Host.Log.Debug("agi.script", $"Room {GetVar(AgiVar.CurrentRoom)}\nInput line: {Text.PromptPrevious}\n{Text.StringPrintf(message)}");
        }

        return default;
    }

    public ValueTask CmdTraceOn(byte[] p) => default;

    public ValueTask CmdTraceInfo(byte[] p) => default;

    public async ValueTask CmdShowMem(byte[] p) => await Text.MessageBoxAsync("Enough memory");

    public ValueTask CmdInitJoy(byte[] p) => default;

    public ValueTask CmdScriptSize(byte[] p)
    {
        Host.Log.Debug("agi", $"script.size({p[0]})");
        return default;
    }

    /// <summary>Debug status of a screen object, as shown by Amiga Gold Rush's "show position" command.</summary>
    public async ValueTask CmdObjStatusF(byte[] p)
    {
        var obj = Obj(GetVar(p[0]));
        string cycleDesc = obj.Cycle switch
        {
            CycleType.Normal => "normal cycle",
            CycleType.EndOfLoop => "end of loop",
            CycleType.RevLoop => "reverse loop",
            CycleType.Reverse => "reverse cycle",
            _ => "unknown cycle type",
        };
        string motionDesc = obj.MotionType switch
        {
            MotionType.Normal => "normal motion",
            MotionType.Wander => "wandering",
            MotionType.FollowEgo => "following ego",
            MotionType.MoveObj => "moving to a point",
            _ => "unknown motion type",
        };

        string msg = $"Object {GetVar(p[0])}:\nx: {obj.XPos}  xsize: {obj.XSize}\ny: {obj.YPos}  ysize: {obj.YSize}\npri: {obj.Priority}\nstepsize: {obj.StepSize}\n{cycleDesc}\n{motionDesc}";
        await Text.MessageBoxAsync(msg);
    }

    /// <summary>Only known to be used by Mixed Up Mother Goose to set the player's name as the save description.</summary>
    public ValueTask CmdSetSimple(byte[] p)
    {
        if (VersionLacks(0x2425, "set.simple"))
        {
            return default;
        }

        string text = State.GetString(p[0]);
        State.AutomaticSaveDescription = text.Length > AgiGameState.SavedGameDescriptionLength ? text[..AgiGameState.SavedGameDescriptionLength] : text;
        State.AutomaticSave = State.AutomaticSaveDescription.Length > 0;
        return default;
    }

    public ValueTask CmdPopScript(byte[] p)
    {
        VersionLacks(0x2425, "pop.script");
        return default;
    }

    public ValueTask CmdPushScript(byte[] p)
    {
        VersionLacks(0x2425, "push.script");
        return default;
    }

    public ValueTask CmdShowMouse(byte[] p)
    {
        if (State.MouseEnabled)
        {
            State.MouseHidden = false;
        }

        return default;
    }

    public ValueTask CmdHideMouse(byte[] p)
    {
        if (Description.GameId == "mh1" && Description.Platform == AgiPlatform.Apple2Gs)
        {
            // called right after the day 4 hospital arcade sequence with parameter 1; probably a different function
            Host.Log.Warning("agi", "hide.mouse called, disabled for MH1 Apple IIgs");
            return default;
        }

        if (State.MouseEnabled)
        {
            State.MouseHidden = true;
        }

        return default;
    }

    public ValueTask CmdFenceMouse(byte[] p)
    {
        State.MouseFence = new Core.Graphics.Rect(p[0], p[1], p[2] - p[0], p[3] - p[0]);
        return default;
    }

    /// <summary>hold.key was added in 2.425; there was no way to disable it until 3.098.</summary>
    public ValueTask CmdHoldKey(byte[] p)
    {
        if (!VersionLacks(0x2425, "hold.key"))
        {
            KeyHoldMode = true;
        }

        return default;
    }

    public ValueTask CmdReleaseKey(byte[] p)
    {
        if (Description.Version < 0x3098)
        {
            Host.Log.Warning("agi", "release.key called, although not available for current AGI version");
            return default;
        }

        KeyHoldMode = false;
        return default;
    }

    public ValueTask CmdAdjEgoMoveToXY(byte[] p)
    {
        var ego = State.ScreenObjTable[AgiGameState.ScreenObjectsEgoEntry];
        if (_opCodes[182].ParameterSize == 2)
        {
            // Two signed parameters: Amiga/Atari ST Gold Rush (logics 130, 150), Manhunter 1 and 2 (logic 0).
            // Turn off mouse movement when called with new arguments; fixes looping on ladders in Gold Rush's mines.
            int x = (sbyte)p[0];
            int y = (sbyte)p[1];
            if (x != State.AdjMouseX || y != State.AdjMouseY)
            {
                ego.Flags &= ~ViewFlags.AdjEgoXY;
            }

            State.AdjMouseX = x;
            State.AdjMouseY = y;
        }
        else
        {
            ego.Flags |= ViewFlags.AdjEgoXY;
        }

        return default;
    }

    public ValueTask CmdParse(byte[] p)
    {
        SetVar(AgiVar.WordNotFound, 0);
        SetFlag(AgiFlag.EnteredCli, false);
        SetFlag(AgiFlag.SaidAcceptedInput, false);
        Words.ParseUsingDictionary(Text.StringPrintf(State.GetString(p[0])), this);
        return default;
    }

    public ValueTask CmdSetGameId(byte[] p)
    {
        var text = State.CurLogic?.GetText(p[0]);
        State.Id = text is null ? string.Empty : text.Length > 7 ? text[..7] : text;
        Host.Log.Debug("agi", $"Game ID: \"{State.Id}\"");
        return default;
    }

    public async ValueTask CmdPause(byte[] p) => await SystemUI.PauseDialogAsync();

    public async ValueTask CmdVersion(byte[] p)
    {
        int ver = Description.Version;
        int maj = (ver >> 12) & 0xF;
        int min = ver & 0xFFF;
        string verMsg = maj == 2
            ? $"\n                               \n\n  Daventry Sierra AGI v{maj:X}.{min:X3}"
            : $"\n                             \n\n Daventry Sierra AGI v{maj:X}.002.{min:X3}";
        await Text.MessageBoxAsync("AGI engine" + verMsg);
    }

    public ValueTask CmdConfigureScreen(byte[] p)
    {
        Text.ConfigureScreen(p[0]);
        Text.StatusRow_Set(p[2]);
        Text.PromptRow_Set(p[1]);
        return default;
    }

    public ValueTask CmdTextScreen(byte[] p)
    {
        State.GfxMode = false;
        Gfx.SetPalette(false);
        Text.CharAttrib_Set(Text.TextAttrib.Foreground, Text.TextAttrib.Background);
        Gfx.ClearDisplay(0);
        Text.ClearLines(0, 24, Text.TextAttrib.CombinedBackground);
        return default;
    }

    public ValueTask CmdGraphics(byte[] p)
    {
        RedrawScreen();
        return default;
    }

    public ValueTask CmdSetTextAttribute(byte[] p)
    {
        Text.CharAttrib_Set(p[0], p[1]);
        return default;
    }

    public async ValueTask CmdStatus(byte[] p)
    {
        Text.InputEditOn();
        Text.CharAttrib_Push();
        Text.CharAttrib_Set(0, 15);
        await CmdTextScreen(p);
        await Inventory.ShowAsync();
        Text.CharAttrib_Pop();
        RedrawScreen();
    }

    public async ValueTask CmdQuit(byte[] p)
    {
        Sound.StopSound();
        if (p[0] != 0 || await SystemUI.QuitDialogAsync())
        {
            QuitGame();
        }
    }

    public ValueTask CmdQuitV1(byte[] p)
    {
        Sound.StopSound();
        QuitGame();
        return default;
    }

    public async ValueTask CmdRestartGame(byte[] p)
    {
        Sound.StopSound();
        bool doRestart = GetFlag(AgiFlag.AutoRestart) || await SystemUI.RestartDialogAsync();
        if (doRestart)
        {
            _restartGame = true;
            SetFlag(AgiFlag.RestartGame, true);
            Menu.ItemEnableAll();
        }
    }

    // ---- input ----

    public ValueTask CmdAcceptInput(byte[] p)
    {
        Text.PromptEnable();
        Text.PromptRedraw();
        return default;
    }

    public ValueTask CmdPreventInput(byte[] p)
    {
        Text.PromptDisable();
        Text.InputEditOn();
        Text.ClearLine(Text.PromptRow_Get(), 0);
        return default;
    }

    public ValueTask CmdCancelLine(byte[] p)
    {
        Text.PromptCancelLine();
        return default;
    }

    public ValueTask CmdEchoLine(byte[] p)
    {
        if (Text.PromptIsEnabled())
        {
            Text.PromptEchoLine();
        }

        return default;
    }

    public async ValueTask CmdGetString(byte[] p)
    {
        int stringDestNr = p[0];
        int leadInTextNr = p[1];
        int stringRow = p[2];
        int stringColumn = p[3];
        int stringMaxLen = Math.Min((int)p[4], TextMgr.TextStringMaxSize);

        bool previousEditState = Text.InputGetEditStatus();
        Text.CharPos_Push();
        Text.InputEditOn();

        if (stringRow < 25)
        {
            Text.CharPos_Set(stringRow, stringColumn);
        }

        var leadInText = State.CurLogic?.GetText(leadInTextNr);
        if (leadInText is not null)
        {
            Text.DisplayText(Text.StringWordWrap(Text.StringPrintf(leadInText), 40));
        }

        CycleInnerLoopActivate(CycleInnerLoopType.GetString);
        Text.StringSet(string.Empty);
        await Text.StringEditAsync(stringMaxLen);

        State.SetString(stringDestNr, Text.InputString);

        Text.CharPos_Pop();
        if (!previousEditState)
        {
            Text.InputEditOff();
        }
    }

    public async ValueTask CmdGetNum(byte[] p)
    {
        int leadInTextNr = p[0];
        int numberDestVarNr = p[1];

        Text.InputEditOn();
        Text.CharPos_Set(Text.PromptRow_Get(), 0);

        var leadInText = State.CurLogic?.GetText(leadInTextNr);
        if (leadInText is not null)
        {
            Text.DisplayText(Text.StringWordWrap(Text.StringPrintf(leadInText), 40));
        }

        Text.InputEditOff();
        CycleInnerLoopActivate(CycleInnerLoopType.GetNumber);
        Text.StringSet(string.Empty);
        await Text.StringEditAsync(3);
        Text.PromptRedraw();

        int.TryParse(Text.InputString, out int number);
        SetVar(numberDestVarNr, (byte)number);
    }

    public ValueTask CmdSetCursorChar(byte[] p)
    {
        var text = State.CurLogic?.GetText(p[0]);
        Text.InputSetCursorChar(text is { Length: > 0 } ? text[0] : '_');
        return default;
    }

    public ValueTask CmdSetKey(byte[] p)
    {
        ushort key = (ushort)(p[0] | (p[1] << 8));
        byte controllerSlot = p[2];
        int keyMappingSlot = -1;
        var mappings = State.ControllerKeyMapping;
        for (int i = 0; i < mappings.Length; i++)
        {
            if (keyMappingSlot == -1 && mappings[i].KeyCode == 0)
            {
                keyMappingSlot = i;
            }

            if (mappings[i].KeyCode == key && mappings[i].ControllerSlot == controllerSlot)
            {
                return default;
            }
        }

        if (keyMappingSlot == -1)
        {
            Host.Log.Warning("agi", $"Number of set.keys exceeded {AgiGameState.MaxControllerKeyMappings}");
            return default;
        }

        mappings[keyMappingSlot].KeyCode = key;
        mappings[keyMappingSlot].ControllerSlot = controllerSlot;
        State.ControllerOccurred[controllerSlot] = false;
        return default;
    }

    public ValueTask CmdSetString(byte[] p)
    {
        State.SetString(p[0], State.CurLogic?.GetText(p[1]) ?? string.Empty);
        return default;
    }

    // ---- text display ----

    public ValueTask CmdDisplay(byte[] p)
    {
        // V1 has 4 arguments
        int textNr = Description.Version >= 0x2000 ? p[2] : p[3];
        Text.Display(textNr, p[0], p[1]);
        return default;
    }

    public ValueTask CmdDisplayF(byte[] p)
    {
        Text.Display(GetVar(p[2]), GetVar(p[0]), GetVar(p[1]));
        return default;
    }

    public ValueTask CmdClearTextRect(byte[] p)
    {
        byte color = Text.CalculateTextBackground(p[4]);
        Text.ClearBlock(p[0], p[1], p[2], p[3], color);
        return default;
    }

    public ValueTask CmdToggleMonitor(byte[] p) => default;

    public ValueTask CmdClearLines(byte[] p)
    {
        int textRowUpper = p[0];
        int textRowLower = p[1];
        byte color;
        if (!State.GfxMode && Description.Platform == AgiPlatform.Amiga)
        {
            // The Amiga interpreter respected the colour in text mode; others used black (bug #16246).
            color = p[2];
        }
        else
        {
            color = Text.CalculateTextBackground(p[2]);
        }

        // Residence 44 calls clear.lines(24,0,0); Agent06 calls clear.lines(1,150,0)
        if (textRowUpper > textRowLower)
        {
            Host.Log.Warning("agi", "clear.lines: RowUpper higher than RowLower");
            textRowLower = textRowUpper;
        }

        Text.ClearLines(textRowUpper, textRowLower, color);
        return default;
    }

    public async ValueTask CmdPrint(byte[] p)
    {
        await Text.PrintAsync(p[0]);

        // WORKAROUND: LSL1 prints the same message three times when giving whiskey to the man in
        // room 14; a short pause makes it visible that these are separate boxes. Bug #15293.
        if (Description.GameId == "lsl1" && p[0] == 22 && GetVar(AgiVar.CurrentRoom) == 14)
        {
            Gfx.UpdateScreen();
            await WaitAsync(50);
        }
    }

    public async ValueTask CmdPrintF(byte[] p) => await Text.PrintAsync(GetVar(p[0]));

    public async ValueTask CmdPrintAt(byte[] p) => await Text.PrintAtAsync(p[0], p[1], p[2], p[3]);

    public async ValueTask CmdPrintAtV(byte[] p) => await Text.PrintAtAsync(GetVar(p[0]), p[1], p[2], p[3]);

    public ValueTask CmdSetPriBase(byte[] p)
    {
        if (Description.Version != 0x2425 && Description.Version < 0x2936)
        {
            Host.Log.Warning("agi", "set.pri.base called, although not available for current AGI version");
            return default;
        }

        Gfx.SetPriorityTable(p[0]);
        return default;
    }

    public ValueTask CmdGetMousePosn(byte[] p)
    {
        int mouseX = Mouse.X;
        int mouseY = Mouse.Y;
        Gfx.TranslateDisplayPosToGameScreen(ref mouseX, ref mouseY);
        SetVar(p[0], (byte)mouseX);
        SetVar(p[1], (byte)mouseY);
        return default;
    }

    public async ValueTask CmdShakeScreen(byte[] p)
    {
        int shakeCount = p[0];
        // AGIPAL uses shake.screen values 100 to 109 to set the palette
        if (shakeCount is >= 100 and < 110)
        {
            Gfx.SetAgiPal(shakeCount, Game);
            return;
        }

        await ShakeScreenAsync(shakeCount);
    }

    public ValueTask CmdUnknown(byte[] p)
    {
        var logic = State.CurLogic!;
        byte opcode = logic.Data[logic.CIP - 1];
        var parameters = string.Join(",", Enumerable.Range(0, _opCodes[opcode].ParameterSize).Select(i => $" {p[i]:X2} ({p[i]})"));
        Host.Log.Warning("agi", $"Unknown opcode: {opcode:X2} ({opcode}), parameters:{parameters}");
        return default;
    }
}
