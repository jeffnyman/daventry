using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using System.Text;

namespace Daventry.Sci.Engine;

public static partial class KernelFunctions
{
    // ---- kfile.cpp: the SCI32 file and save game functions ----
    // SCI32 scripts number their saves from 0; slot 0 is the autosave here, so the ids shift by
    // one, and the highest id (99) stands in for the autosave when a game lists it.

    private const int SaveIdShift = 1;
    private const int MaxShiftedSaveId = 99;

    /// <summary>A SCI save number as a slot of the save store.</summary>
    public static int ShiftSciToScummVMSaveId(int saveId) => saveId == MaxShiftedSaveId ? 0 : saveId >= 0 ? saveId + SaveIdShift : saveId;

    /// <summary>A slot of the save store as the number the scripts see.</summary>
    public static int ShiftScummVMToSciSaveId(int saveId) => saveId == 0 ? MaxShiftedSaveId : saveId > 0 ? saveId - SaveIdShift : saveId;

    /// <summary>Sub-function parent; the VM dispatches on the first argument.</summary>
    public static ValueTask<RegT> SaveAsync(EngineState s, KernelArgs argv) => new(s.Acc);

    public static ValueTask<RegT> CdAsync(EngineState s, KernelArgs argv) => new(s.Acc);

    // no file ever exists, as in the SCI16 stubs: the extra SCI32 operations fail the same way
    public static RegT FileIOReadByte(EngineState s, KernelArgs argv) => RegT.Null;

    public static RegT FileIOWriteByte(EngineState s, KernelArgs argv) => RegT.Signal;

    public static RegT FileIOReadWord(EngineState s, KernelArgs argv) => s.Acc;

    public static RegT FileIOWriteWord(EngineState s, KernelArgs argv) => RegT.Signal;

    public static RegT FileIOGetCWD(EngineState s, KernelArgs argv)
    {
        s.SegMan.LookupArray(argv[0]).FromString("C:\\SIERRA\\");
        return argv[0];
    }

    /// <summary>Torin, LSL7 and RAMA check that the save directory is valid; the directory is never used here.</summary>
    public static RegT FileIOIsValidDirectory(EngineState s, KernelArgs argv) => RegT.True;

    public static RegT SaveGame32(EngineState s, KernelArgs argv)
    {
        // the control panel (QFG4), keyboard (Shivers) or whatever else is up is hidden before the
        // save is taken, so the thumbnail shows the game (bug #9752); needs the frameout
        s.RequireGfx32().Frameout.FrameOut(true);
        string gameName = s.SegMan.GetString(argv[0]);
        int saveNo = argv.Int16(1);
        string saveDescription = argv[2].IsNull ? string.Empty : s.SegMan.GetString(argv[2]);
        string gameVersion = argv.Count <= 3 || argv[3].IsNull ? string.Empty : s.SegMan.GetString(argv[3]);
        s.Log.Debug("sci.save", $"kSaveGame32({gameName}, {saveNo}, {saveDescription}, {gameVersion})");

        if (gameName is "Autosave" or "Autosv")
        {
            // Torin and LSL7: slot 0 is the autosave, slot 1 the "new game" save
            if (saveNo != 0)
            {
                saveNo = Savegame.NewGameId;
            }
        }
        else
        {
            saveNo = ShiftSciToScummVMSaveId(saveNo);
        }

        if (s.GameId == SciGameId.Qfg4)
        {
            // QFG4's auto-save is recognised by its name, message (0, 0, 16, 1)
            var autoSaveName = s.SegMan.AllocateArray(SciArrayType.String, 0, out var autoSaveNameId);
            s.Message.GetMessage(0, new MessageTuple(0, 0, 16, 1), autoSaveNameId);
            if (saveDescription == autoSaveName.ToString())
            {
                saveNo = Savegame.AutoSaveId;
            }

            s.SegMan.FreeArray(autoSaveNameId);
        }

        return Savegame.Save(s, saveNo, saveDescription, gameVersion) ? RegT.True : RegT.Null;
    }

    public static RegT RestoreGame32(EngineState s, KernelArgs argv)
    {
        string gameName = s.SegMan.GetString(argv[0]);
        int saveNo = argv.Int16(1);
        if (gameName is "Autosave" or "Autosv")
        {
            if (saveNo != 0)
            {
                saveNo = Savegame.NewGameId;
            }
        }
        else
        {
            saveNo = ShiftSciToScummVMSaveId(saveNo);
        }

        return Savegame.Restore(s, saveNo) ? RegT.True : RegT.Null;
    }

    public static RegT CheckSaveGame32(EngineState s, KernelArgs argv)
    {
        string gameName = s.SegMan.GetString(argv[0]);
        int saveNo = argv.Int16(1);
        string gameVersion = argv[2].IsNull ? string.Empty : s.SegMan.GetString(argv[2]);

        // without a version from the scripts the VERSION file of the game is the reference
        if (gameVersion.Length == 0)
        {
            gameVersion = ReadVersionFile(s);
        }

        if (gameName is "Autosave" or "Autosv")
        {
            if (saveNo == 1)
            {
                saveNo = Savegame.NewGameId;
            }
        }
        else
        {
            saveNo = ShiftSciToScummVMSaveId(saveNo);
        }

        var saves = Savegame.ListSavegames(s);
        int index = Savegame.FindSavegame(saves, saveNo);
        if (index == -1)
        {
            return RegT.Null;
        }

        var save = saves[index];
        if (save.Version < Savegame.MinimumSci32Version)
        {
            s.Log.Warning("sci.save", $"Save version {save.Version} is below minimum SCI32 savegame version {Savegame.MinimumSci32Version}");
            return RegT.Null;
        }

        if (save.Version > Savegame.CurrentVersion)
        {
            s.Log.Warning("sci.save", $"Save version {save.Version} is above maximum SCI32 savegame version {Savegame.CurrentVersion}");
            return RegT.Null;
        }

        if (save.GameVersion != gameVersion && gameVersion.Length > 0 && save.GameVersion.Length > 0)
        {
            s.Log.Warning("sci.save", $"Save game was created for game version {save.GameVersion}, but the current game version is {gameVersion}");
            return RegT.Null;
        }

        if (save.GameObjectOffset > 0 && save.Script0Size > 0)
        {
            var script0 = s.ResMan.FindResource(Resources.ResourceType.Script, 0)!;
            if (save.Script0Size != script0.Data!.Length)
            {
                s.Log.Warning("sci.save", $"Save game was created for a game with a script 0 size of {save.Script0Size}, but the current game script 0 size is {script0.Data.Length}");
                return RegT.Null;
            }

            if (save.GameObjectOffset != s.GameObjectAddress.Offset)
            {
                s.Log.Warning("sci.save", $"Save game was created for a game with the main game object at offset {save.GameObjectOffset}, but the current main game object offset is {s.GameObjectAddress.Offset}");
                return RegT.Null;
            }
        }

        return RegT.True;
    }

    /// <summary>The first line of the game's VERSION file, or an empty string.</summary>
    private static string ReadVersionFile(EngineState s)
    {
        var game = s.ResMan.Game;
        string? name = game.FirstExisting("VERSION", "version", "VERSION.");
        if (name is null)
        {
            return string.Empty;
        }

        try
        {
            using var reader = new StreamReader(game.Open(name), Encoding.Latin1);
            return reader.ReadLine()?.TrimEnd('\r') ?? string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    public static RegT GetSaveFiles32(EngineState s, KernelArgs argv)
    {
        // argv[0] is the game name, the name of the save catalogue in Sierra's interpreter
        var descriptions = s.SegMan.LookupArray(argv[1]);
        var saveIds = s.SegMan.LookupArray(argv[2]);

        var saves = Savegame.ListSavegames(s);

        // Sierra limited a directory to 20 games; more are allowed with the standard dialog
        descriptions.Resize(Savegame.MaxSaveNameLength * saves.Count + 1, true);
        saveIds.Resize(saves.Count + 1, true);

        for (int i = 0; i < saves.Count; i++)
        {
            var save = saves[i];
            // Phantasmagoria 2 creates names of exactly the maximum length, so no terminator is forced
            var name = Encoding.Latin1.GetBytes(save.Name);
            int length = Math.Min(name.Length, Savegame.MaxSaveNameLength);
            for (int c = 0; c < Savegame.MaxSaveNameLength; c++)
            {
                descriptions.SetByteAt(Savegame.MaxSaveNameLength * i + c, c < length ? name[c] : (byte)0);
            }

            saveIds.SetFromInt16(i, (short)ShiftScummVMToSciSaveId(save.Id));
        }

        descriptions.SetByteAt(Savegame.MaxSaveNameLength * saves.Count, 0);
        saveIds.SetFromInt16(saves.Count, 0);
        return RegT.Make(0, saves.Count);
    }

    /// <summary>
    /// Save catalogues are not used, but the scripts write them out after deleting a save, so a
    /// fixed name lets kFileIO recognise and ignore those writes.
    /// </summary>
    public static RegT MakeSaveCatName(EngineState s, KernelArgs argv)
    {
        s.SegMan.LookupArray(argv[0]).FromString("fake.cat");
        return argv[0];
    }

    public static RegT MakeSaveFileName(EngineState s, KernelArgs argv)
    {
        // argv[1] is the game name, which is not used
        int saveNo = argv.Int16(2);
        s.SegMan.LookupArray(argv[0]).FromString(Savegame.SaveName(s, saveNo + SaveIdShift));
        return argv[0];
    }

    /// <summary>The host's own save and load dialog, reached through script patches; the games' dialogs are used instead.</summary>
    public static RegT ScummVMSaveLoad(EngineState s, KernelArgs argv)
    {
        s.Log.Warning("sci.save", "kScummVMSaveLoad: the save dialog of the host is not available");
        return RegT.Null;
    }

    // ---- CD checks (Phantasmagoria and the other multi-disc games) ----

    public static RegT CheckCD(EngineState s, KernelArgs argv)
    {
        // the Macintosh interpreters showed a dialog asking for the disc; all resources are on
        // one volume here, so the current disc is whatever was asked for
        int cdNo = argv.Count > 0 ? argv.Int16(0) : 0;
        if (cdNo != 0)
        {
            s.CurrentDiscNo = cdNo;
        }

        return RegT.Make(0, s.CurrentDiscNo);
    }

    /// <summary>Would read the disc number from the loaded save; the current disc is returned instead.</summary>
    public static RegT GetSavedCD(EngineState s, KernelArgs argv) => RegT.Make(0, s.CurrentDiscNo);
}
