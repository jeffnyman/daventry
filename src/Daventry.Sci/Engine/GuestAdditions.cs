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

    /// <summary>The patch for SRDialog::doit: push0, &amp;rest 1, callk ScummVMSaveLoad 0, ret.</summary>
    private static readonly byte[] SrDialogPatch = [0x76, 0x59, 0x01, 0x43, Kernel.ScummVMSaveLoadId, 0x00, 0x00, 0x48];

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

        if (script.ScriptNumber != SrDialogScript)
        {
            return;
        }

        // 64990 holds SRDialog, used by the main Game object; it is not loaded until the game
        // first needs it, so it is patched as it comes in (preloading it would reorder the
        // segment table against saved games)
        PatchSrDialogDoit(s, script, "SRDialog", SrDialogPatch);
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
    /// returns its slot; otherwise the host has no dialog of its own, so the call cancels.
    /// </summary>
    public static RegT KScummVMSaveLoad(EngineState s, KernelArgs argv)
    {
        bool isSave = argv.Count > 0;
        if (!isSave && s.DelayedRestoreGameId != -1)
        {
            return RegT.Make(0, KernelFunctions.ShiftScummVMToSciSaveId(s.DelayedRestoreGameId));
        }

        s.Log.Warning("sci.save", "kScummVMSaveLoad: the save dialog of the host is not available");
        return RegT.Make(0, -1);
    }
}
