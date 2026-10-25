using Daventry.Core.Hosting;

namespace Daventry.Sci.Engine;

/// <summary>
/// The host-side additions to the games, port of the parts of ScummVM's GuestAdditions that a
/// restore requested from outside the scripts needs: SCI16 games are restored directly at the
/// next kGetEvent or kWait, SCI32 games through their own restore method, with the save dialog
/// script patched so that the pending slot is returned instead of asking the player.
/// </summary>
public static class GuestAdditions
{
    private const int SrDialogScript = 64990;

    private const int TorinSaveRestoreScript = 64866;
    private const int Phant2SaveRestoreScript = 64978;
    private const int GlobalVarPhant2ControlPanel = 250;

    /// <summary>The patch for SRDialog::doit: push0, &amp;rest 1, callk ScummVMSaveLoad 0, ret.</summary>
    private static readonly byte[] SrDialogPatch = [0x76, 0x59, 0x01, 0x43, Kernel.ScummVMSaveLoadId, 0x00, 0x00, 0x48];

    /// <summary>
    /// Torin and LSL7's save export: pushi new, push0, class Str, send 4, sal 1 (a description
    /// string in local 1), push0, &amp;rest 1, callk ScummVMSaveLoad 0, ret. Bytes 1 and 2 take
    /// the selector of new.
    /// </summary>
    private static readonly byte[] SrTorinPatch =
        [0x38, 0xFF, 0xFF, 0x76, 0x51, 0x0F, 0x4A, 0x04, 0x00, 0xA3, 0x01, 0x76, 0x59, 0x01, 0x43, Kernel.ScummVMSaveLoadId, 0x00, 0x00, 0x48];

    /// <summary>RAMA's Save::doit and Restore::doit: push1, pushSelf, callk ScummVMSaveLoad 2, ret.</summary>
    private static readonly byte[] RamaSrDialogPatch = [0x78, 0x7C, 0x43, Kernel.ScummVMSaveLoadId, 0x02, 0x00, 0x48];

    /// <summary>Port of kGetEventHook: true when a pending restore was carried out and the call must return.</summary>
    public static async ValueTask<bool> KGetEventHookAsync(EngineState s, KernelArgs argv)
    {
        if (s.DelayedRestoreGameId == -1)
        {
            return false;
        }

        // Lighthouse: a restore while the game object still initializes fails on a saved Robot
        if (s.GameId == SciGameId.Lighthouse && s.CallInStack(s.GameObjectAddress, s.Kernel.Selectors.Init))
        {
            return false;
        }

        return await RestoreFromLauncherAsync(s, argv);
    }

    /// <summary>Port of kWaitHook.</summary>
    public static async ValueTask<bool> KWaitHookAsync(EngineState s, KernelArgs argv)
    {
        if (s.DelayedRestoreGameId == -1)
        {
            return false;
        }

        return await RestoreFromLauncherAsync(s, argv);
    }

    /// <summary>
    /// Port of instantiateScriptHook: while a restore is pending, the SCI32 save dialog script
    /// gets the patch that makes it return the pending slot. Scripts loaded by a restore are
    /// left alone (segManSaveLoadScriptHook).
    /// </summary>
    public static void InstantiateScriptHook(EngineState s, Script script)
    {
        if (s.Version < SciVersion.V2 || s.DelayedRestoreGameId == -1)
        {
            return;
        }

        if (s.GameId is SciGameId.Lsl7 or SciGameId.Torin && script.ScriptNumber == TorinSaveRestoreScript)
        {
            PatchGameSaveRestoreTorin(s, script);
        }
        else if (s.GameId == SciGameId.Phantasmagoria2 && script.ScriptNumber == Phant2SaveRestoreScript)
        {
            PatchGameSaveRestorePhant2(s, script);
        }
        else if (script.ScriptNumber == SrDialogScript)
        {
            // 64990 holds SRDialog, used by the main Game object; it is not loaded until the
            // game first needs it, so it is patched as it comes in (preloading it would reorder
            // the segment table against saved games)
            if (s.GameId == SciGameId.Rama)
            {
                PatchSrDialogDoit(s, script, "Save", RamaSrDialogPatch);
                PatchSrDialogDoit(s, script, "Restore", RamaSrDialogPatch);
            }
            else
            {
                PatchSrDialogDoit(s, script, "SRDialog", SrDialogPatch);
            }
        }
    }

    /// <summary>Port of patchGameSaveRestoreTorin: export 2 of script 64866 becomes the host's save and restore.</summary>
    private static void PatchGameSaveRestoreTorin(EngineState s, Script script)
    {
        uint address = script.ValidateExportFunc(2, exportsAreWide: false, relocSci3: true);
        Array.Copy(SrTorinPatch, 0, script.Buf, (int)address, SrTorinPatch.Length);
        int newSelector = s.Kernel.Selectors.New;
        script.Buf[address + 1] = (byte)(newSelector & 0xFF);
        script.Buf[address + 2] = (byte)((newSelector >> 8) & 0xFF);
        s.Log.Debug("sci.save", $"patched the save and restore export of script {script.ScriptNumber}");
    }

    /// <summary>Port of patchGameSaveRestorePhant2: srGetGame::init returns the host's choice.</summary>
    private static void PatchGameSaveRestorePhant2(EngineState s, Script script)
    {
        foreach (var obj in script.Objects.Values)
        {
            if (s.SegMan.DerefString(obj.Name) != "srGetGame")
            {
                continue;
            }

            int methodIndex = obj.FuncSelectorPosition(s.Kernel.Selectors.Init);
            if (methodIndex == -1)
            {
                continue;
            }

            Array.Copy(SrDialogPatch, 0, script.Buf, obj.GetFunction(methodIndex).Offset, SrDialogPatch.Length);
            break;
        }
    }

    /// <summary>Port of patchSRDialogDoit: overwrites the start of the object's doit method.</summary>
    private static void PatchSrDialogDoit(EngineState s, Script script, string objectName, byte[] patch)
    {
        foreach (var obj in script.Objects.Values)
        {
            if (s.SegMan.GetObjectName(obj.Pos) != objectName)
            {
                continue;
            }

            for (int methodNr = 0; methodNr < obj.MethodCount; methodNr++)
            {
                if (s.Kernel.GetSelectorName(obj.GetFuncSelector(methodNr)) != "doit")
                {
                    continue;
                }

                var methodAddress = obj.GetFunction(methodNr);
                Array.Copy(patch, 0, script.Buf, (int)methodAddress.Offset, patch.Length);
                s.Log.Debug("sci.save", $"patched {objectName}::doit of script {script.ScriptNumber} to return the pending restore");
                return;
            }
        }
    }

    /// <summary>
    /// Port of restoreFromLauncher. SCI32 games restore through their own Game::restore, whose
    /// patched dialog returns the pending slot; SCI16 games are restored right here.
    /// </summary>
    private static async ValueTask<bool> RestoreFromLauncherAsync(EngineState s, KernelArgs argv)
    {
        if (s.Version >= SciVersion.V2)
        {
            if (s.RestoringFromLauncher)
            {
                // a failed restore would otherwise be retried forever
                s.DelayedRestoreGameId = -1;
                s.RestoringFromLauncher = false;
                return false;
            }

            // not before the benchmarking room is over: SQ6 would call into the subtitles
            // ScrollWindow that does not exist yet, other games break their benchmark
            if (s.SegMan.GetObjectName(s.GetGlobal(GlobalVar.CurrentRoom)) == "speedRoom")
            {
                return false;
            }

            // LSL6 hires and PQ4 restore methods need the room number to be set; the Mac
            // versions call kGetEvent from their init before the first newRoom
            if ((s.GameId == SciGameId.Lsl6hires || s.GameId == SciGameId.Pq4) && s.GetGlobal(GlobalVar.CurrentRoomNo).IsNull)
            {
                return false;
            }

            s.RestoringFromLauncher = true;

            // queued events would otherwise land in the restored game
            s.Events.FlushEvents();

            var sel = s.Kernel.Selectors;
            if (s.GameId == SciGameId.Phantasmagoria2)
            {
                // Phantasmagoria 2 restores through reallyRestore with the slot in Game::num,
                // and its "Click to continue" prompt needs input enabled first
                await Selectors.InvokeSelector(s, s.GetGlobal(GlobalVar.User), sel.CanInput, argv, RegT.True);
                Selectors.WriteSelectorValue(s, s.GameObjectAddress, sel.Num, KernelFunctions.ShiftScummVMToSciSaveId(s.DelayedRestoreGameId));
                await Selectors.InvokeSelector(s, s.GameObjectAddress, sel.ReallyRestore, argv);
            }
            else if (s.GameId == SciGameId.Shivers)
            {
                // Shivers takes the slot as a parameter of its restore method
                await Selectors.InvokeSelector(s, s.GameObjectAddress, sel.Restore, argv, RegT.Make(0, KernelFunctions.ShiftScummVMToSciSaveId(s.DelayedRestoreGameId)));
            }
            else
            {
                // Game::restore runs Restore::doit, which returns the pending slot through the
                // patched SRDialog instead of asking the player
                await Selectors.InvokeSelector(s, s.GameObjectAddress, sel.Restore, argv);

                // RAMA's own save system never resets the engine, so the slot is cleared here
                if (s.GameId == SciGameId.Rama)
                {
                    s.DelayedRestoreGameId = -1;
                }
            }

            s.RestoringFromLauncher = false;
            return true;
        }

        Savegame.DelayedRestore(s);
        return true;
    }

    /// <summary>
    /// Port of kScummVMSaveLoad for the games whose save dialog is patched: a pending restore
    /// returns its slot; otherwise the host has no dialog of its own, so the call cancels. Each
    /// game wants the answer in its own form.
    /// </summary>
    public static RegT KScummVMSaveLoad(EngineState s, KernelArgs argv)
    {
        return s.GameId switch
        {
            SciGameId.Phantasmagoria2 => PromptSaveRestorePhant2(s, argv),
            SciGameId.Lsl7 or SciGameId.Torin => PromptSaveRestoreTorin(s, argv),
            SciGameId.Rama => PromptSaveRestoreRama(s, argv),
            _ => RegT.Make(0, RunSaveRestore(s, argv.Count > 0, argv.Count > 0 ? argv[0] : RegT.Null)),
        };
    }

    /// <summary>
    /// Port of runSaveRestore: the pending restore when there is one; the host has no save
    /// dialog, so anything else cancels (-1). A description array is cleared, as the empty
    /// description the dialog returns for a restore would do.
    /// </summary>
    private static int RunSaveRestore(EngineState s, bool isSave, RegT outDescription)
    {
        int saveId = -1;
        if (!isSave && s.DelayedRestoreGameId != -1)
        {
            saveId = s.DelayedRestoreGameId;
        }
        else
        {
            s.Log.Warning("sci.save", "kScummVMSaveLoad: the save dialog of the host is not available");
        }

        if (!outDescription.IsNull)
        {
            if (s.SegMan.IsObject(outDescription))
            {
                outDescription = Selectors.ReadSelector(s, outDescription, s.Kernel.Selectors.Data);
            }

            s.SegMan.LookupArray(outDescription).FromString(string.Empty);
        }

        return KernelFunctions.ShiftScummVMToSciSaveId(saveId);
    }

    /// <summary>Port of promptSaveRestoreTorin: the result goes into locals 1 to 3 of the patched export.</summary>
    private static RegT PromptSaveRestoreTorin(EngineState s, KernelArgs argv)
    {
        bool isSave = argv.Count > 0 && argv[0].ToInt16() != 0;
        var descriptionId = RegT.Null;
        if (isSave)
        {
            s.SegMan.AllocateArray(SciArrayType.String, 0, out descriptionId);
        }

        int saveNo = RunSaveRestore(s, isSave, descriptionId);
        if (saveNo != -1)
        {
            var locals = s.VarArray[EngineState.VarLocal]!;
            int baseIndex = s.VarIndex[EngineState.VarLocal];
            if (s.VariablesMax[EngineState.VarLocal] <= 2)
            {
                throw new SciVmException("promptSaveRestoreTorin: the save script has too few locals");
            }

            Selectors.WriteSelector(s, locals[baseIndex + 1], s.Kernel.Selectors.Data, descriptionId);
            locals[baseIndex + 2] = RegT.Make(0, saveNo);
            locals[baseIndex + 3] = RegT.Make(0, isSave ? 1 : 0);
        }
        else if (isSave)
        {
            s.SegMan.FreeArray(descriptionId);
        }

        return RegT.Make(0, saveNo != -1 ? 1 : 0);
    }

    /// <summary>Port of promptSaveRestorePhant2: the control panel is also told to close.</summary>
    private static RegT PromptSaveRestorePhant2(EngineState s, KernelArgs argv)
    {
        if (argv.Count != 2)
        {
            throw new SciVmException($"kScummVMSaveLoad: Phantasmagoria 2 passes 2 arguments, not {argv.Count}");
        }

        bool isSave = argv[1].ToInt16() == 0;
        int saveNo = RunSaveRestore(s, isSave, argv[0]);

        // unhighlight the button, so the panel does not reopen on the save or load page
        var button = s.SegMan.FindObjectByName(isSave ? "saveButton" : "loadButton");
        Selectors.WriteSelectorValue(s, button, s.Kernel.Selectors.Cel, 0);

        // ends the control panel's own event loop and hides it
        Selectors.WriteSelector(s, s.GetGlobal(GlobalVarPhant2ControlPanel), s.Kernel.Selectors.Scratch, RegT.True);
        return RegT.Make(0, saveNo);
    }

    /// <summary>
    /// Port of promptSaveRestoreRama: RAMA's dialogs return the index of the save in the list,
    /// not its id. A save always cancels here (the host has no dialog), so ScummVM's code that
    /// creates the new save file and resets the CatalogFile is not needed.
    /// </summary>
    private static RegT PromptSaveRestoreRama(EngineState s, KernelArgs argv)
    {
        if (argv.Count != 1)
        {
            throw new SciVmException($"kScummVMSaveLoad: RAMA passes 1 argument, not {argv.Count}");
        }

        bool isSave = s.SegMan.GetObjectName(argv[0]) == "Save";
        var editor = s.SegMan.FindObjectByName("editI");
        var outDescription = Selectors.ReadSelector(s, editor, s.Kernel.Selectors.Text);
        if (!s.SegMan.IsValidAddr(outDescription, SegmentType.Array))
        {
            s.SegMan.AllocateArray(SciArrayType.String, 0, out outDescription);
            Selectors.WriteSelector(s, editor, s.Kernel.Selectors.Text, outDescription);
        }

        int saveNo = RunSaveRestore(s, isSave, outDescription);
        int saveIndex = -1;
        if (saveNo != -1)
        {
            saveNo += KernelFunctions.SaveIdShift;
            saveIndex = Savegame.FindSavegame(Savegame.ListSavegames(s), saveNo);
        }

        return RegT.Make(0, saveIndex);
    }
}
