using System.Text;
using Daventry.Core.Hosting;

namespace Daventry.Sci.Engine;

public static partial class KernelFunctions
{
    // ---- kfile.cpp: the save and restore kernel calls ----
    // Slot 0 is reserved for an autosave; the scripts see slots through "virtual ids" that
    // start at 100, so they never mistake an id for a count.

    private const int SaveGameSlotFirst = 1;
    private const int SaveGameSlotLast = 99;

    public static RegT SaveGame(EngineState s, KernelArgs argv)
    {
        string gameId = !argv[0].IsNull ? s.SegMan.GetString(argv[0]) : string.Empty;
        short virtualId = argv.Int16(1);
        int savegameId = -1;
        string version = argv.Count > 3 && !argv[3].IsNull ? s.SegMan.GetString(argv[3]) : string.Empty;

        // no deleting a user's save from within a kernel function
        if (s.ExecutionStackBase != 0)
        {
            s.Log.Warning("sci.save", "kSaveGame - won't save from within kernel function");
            return RegT.Null;
        }

        if (argv[0].IsNull)
        {
            // a direct call from a patched Game::save, which asks for ScummVM's own dialog; the
            // scripts are not patched here
            s.Log.Warning("sci.save", "kSaveGame: the save dialog of the host is not available");
            return RegT.Null;
        }

        if (argv[2].IsNull)
        {
            throw new SciVmException("kSaveGame: called with description being NULL");
        }

        string description = s.SegMan.GetString(argv[2]);
        s.Log.Debug("sci.save", $"kSaveGame({gameId},{virtualId},{description},{version})");

        var saves = Savegame.ListSavegames(s);
        if (virtualId is >= Savegame.OfficialRangeStart and <= Savegame.OfficialRangeEnd)
        {
            // an actual id: it must exist
            savegameId = virtualId - Savegame.OfficialRangeStart;
            if (Savegame.FindSavegame(saves, savegameId) == -1)
            {
                return RegT.Null;
            }
        }
        else if (virtualId < Savegame.OfficialRangeStart)
        {
            // a low id: the scripts expect a new slot
            switch (s.GameId)
            {
                case SciGameId.Jones:
                    savegameId = 0; // one save slot only
                    break;
                case SciGameId.Qfg3:
                {
                    // the auto-save of QFG3 is recognised by its name, message (0, 0, 16, 1)
                    s.SegMan.AllocDynmem(Savegame.MaxSaveNameLength, "kSaveGame", out var autoSaveNameId);
                    s.Message.GetMessage(0, new MessageTuple(0, 0, 16, 1), autoSaveNameId);
                    string autoSaveName = s.SegMan.GetString(autoSaveNameId);
                    if (description == autoSaveName)
                    {
                        savegameId = Savegame.AutoSaveId;
                    }

                    s.SegMan.FreeDynmem(autoSaveNameId);
                    break;
                }

                case SciGameId.Fanmade:
                    // Cascade Quest auto-saves to slot 99, which goes into the autosave slot (bug #7007)
                    if (s.SegMan.GetObjectName(s.GameObjectAddress) == "CascadeQuest" && virtualId == Savegame.OfficialRangeStart - 1)
                    {
                        savegameId = 0;
                    }

                    break;
            }

            if (savegameId < 0)
            {
                if (virtualId == s.LastSaveVirtualId)
                {
                    // the same virtual id as last time: the caller overwrites the last save
                    savegameId = s.LastSaveNewId;
                }
                else
                {
                    // the first free slot
                    for (savegameId = SaveGameSlotFirst; savegameId <= SaveGameSlotLast; savegameId++)
                    {
                        if (Savegame.FindSavegame(saves, savegameId) == -1)
                        {
                            break;
                        }
                    }

                    if (savegameId > SaveGameSlotLast)
                    {
                        throw new SciVmException("kSavegame: no more savegame slots available");
                    }
                }
            }

            // WORKAROUND: Mother Goose 256 keeps the current save id in a global that it also
            // uses to auto-save and auto-delete at the end of the game (bug #5294)
            if (s.GameId == SciGameId.Mothergoose256)
            {
                s.SetGlobal(0xB3, RegT.Make(0, Savegame.OfficialRangeStart + savegameId));
            }
        }
        else
        {
            throw new SciVmException("kSaveGame: invalid savegameId used");
        }

        // remembered in case the caller overwrites the newly created save
        s.LastSaveVirtualId = virtualId;
        s.LastSaveNewId = savegameId;

        return Savegame.Save(s, savegameId, description, version) ? RegT.True : RegT.Null;
    }

    public static RegT RestoreGame(EngineState s, KernelArgs argv)
    {
        string gameId = !argv[0].IsNull ? s.SegMan.GetString(argv[0]) : string.Empty;
        short savegameId = argv.Int16(1);
        s.Log.Debug("sci.save", $"kRestoreGame({gameId},{savegameId})");

        if (argv[0].IsNull)
        {
            // a direct call from the launcher or a patched Game::restore: the id is already right,
            // but -1 asks for the host's dialog, which does not exist here
            if (savegameId == -1)
            {
                s.Log.Warning("sci.save", "kRestoreGame: the restore dialog of the host is not available");
                return s.Acc;
            }
        }
        else if (s.GameId == SciGameId.Jones)
        {
            savegameId = 0; // one save slot only
        }
        else
        {
            // a real call from a script: the virtual id becomes the slot
            if (savegameId < Savegame.OfficialRangeStart || savegameId > Savegame.OfficialRangeEnd)
            {
                s.Log.Warning("sci.save", $"Savegame ID {savegameId} is not allowed");
                return RegT.True;
            }

            savegameId -= Savegame.OfficialRangeStart;
        }

        s.Acc = RegT.Null; // success
        var saves = Savegame.ListSavegames(s);
        if (Savegame.FindSavegame(saves, savegameId) == -1)
        {
            s.Acc = RegT.True;
            s.Log.Warning("sci.save", $"Savegame ID {savegameId} not found");
        }
        else if (!Savegame.Restore(s, savegameId))
        {
            s.Acc = RegT.True; // failure
        }

        if (s.Acc.IsNull && s.Sound.IsGlobalPauseActive)
        {
            // the music's global pause counter goes back without touching the sounds
            s.Sound.ResetGlobalPauseCounter();
        }

        return s.Acc;
    }

    public static RegT GetSaveDir(EngineState s, KernelArgs argv) => s.SegMan.SaveDirPtr;

    public static RegT GetCWD(EngineState s, KernelArgs argv)
    {
        s.SegMan.Strcpy(argv[0], "/");
        return argv[0];
    }

    public static RegT CheckSaveGame(EngineState s, KernelArgs argv)
    {
        string gameId = s.SegMan.GetString(argv[0]);
        ushort virtualId = argv.UInt16(1);
        s.Log.Debug("sci.save", $"kCheckSaveGame({gameId}, {virtualId})");

        var saves = Savegame.ListSavegames(s);

        // 0 happens in QfG2 when restoring from an empty list
        if (virtualId == 0)
        {
            return RegT.Null;
        }

        int savegameId;
        if (s.GameId == SciGameId.Jones)
        {
            savegameId = 0; // one save slot only
        }
        else
        {
            if (virtualId < Savegame.OfficialRangeStart || virtualId > Savegame.OfficialRangeEnd)
            {
                throw new SciVmException($"kCheckSaveGame: called with invalid savegame ID ({virtualId})");
            }

            savegameId = virtualId - Savegame.OfficialRangeStart;
        }

        int savegameNr = Savegame.FindSavegame(saves, savegameId);
        if (savegameNr == -1)
        {
            return RegT.Null;
        }

        // a compatible version
        uint ver = saves[savegameNr].Version;
        return ver < Savegame.MinimumVersion || ver > Savegame.CurrentVersion ? RegT.Null : RegT.True;
    }

    public static RegT GetSaveFiles(EngineState s, KernelArgs argv)
    {
        // the scripts ask for the current save files; a new slot requested afterwards is really a
        // new slot and not an overwrite of the last one
        s.LastSaveVirtualId = Savegame.OfficialRangeStart;

        var saves = Savegame.ListSavegames(s);
        int totalSaves = Math.Min(saves.Count, Savegame.MaxNumSaveGames);

        string gameId = s.SegMan.GetString(argv[0]);
        s.Log.Debug("sci.save", $"kGetSaveFiles({gameId})");

        var slot = s.SegMan.DerefRegPtr(argv[2], totalSaves);
        if (slot is null)
        {
            s.Log.Warning("sci.save", $"kGetSaveFiles: {argv[2]} invalid or too small to hold slot data");
            totalSaves = 0;
        }

        int bufSize = totalSaves * Savegame.MaxSaveNameLength + 1;
        var saveNames = new byte[bufSize];
        for (int i = 0; i < totalSaves; i++)
        {
            // the virtual id of the save
            slot!.Value.Regs[slot.Value.Index + i] = RegT.Make(0, saves[i].Id + Savegame.OfficialRangeStart);
            var name = Encoding.Latin1.GetBytes(saves[i].Name);
            int length = Math.Min(name.Length, Savegame.MaxSaveNameLength - 1);
            Array.Copy(name, 0, saveNames, i * Savegame.MaxSaveNameLength, length);
        }

        s.SegMan.Memcpy(argv[1], saveNames, bufSize);
        return RegT.Make(0, totalSaves);
    }
}
