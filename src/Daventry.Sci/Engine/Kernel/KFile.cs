using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace Daventry.Sci.Engine;

public static partial class KernelFunctions
{
    // ---- kfile.cpp: kFileIO and the device functions ----
    // Files a game writes go to the host's save store as "<game id>-<name>"; see SciFile.

    /// <summary>The marker of a RAMA save file opened through kFileIO, whose words are whole registers.</summary>
    private const string ScummVMSaveHandleName = "-scummvm-save-";

    private const int DeviceInfoGetDevice = 0;
    private const int DeviceInfoGetCurrentDevice = 1;
    private const int DeviceInfoPathsEqual = 2;
    private const int DeviceInfoIsFloppy = 3;
    private const int DeviceInfoGetConfigPath = 5;
    private const int DeviceInfoGetSaveCatName = 7;
    private const int DeviceInfoGetSaveFileName = 8;

    public static RegT DeviceInfo(EngineState s, KernelArgs argv)
    {
        if (s.GameId == SciGameId.Fanmade && argv.Count == 1)
        {
            // WORKAROUND: the fan game script library calls kDeviceInfo without the sub-op; it
            // wants CurDevice
            s.SegMan.Strcpy(argv[0], "/");
            return s.Acc;
        }

        int mode = argv.UInt16(0);
        switch (mode)
        {
            case DeviceInfoGetDevice:
                s.SegMan.Strcpy(argv[2], "/");
                break;

            case DeviceInfoGetCurrentDevice:
                s.SegMan.Strcpy(argv[1], "/");
                break;

            case DeviceInfoPathsEqual:
            {
                string path1 = s.SegMan.GetString(argv[1]);
                string path2 = s.SegMan.GetString(argv[2]);
                return RegT.Make(0, SavePattern.Match(path2, path1, false, "/") ? 1 : 0);
            }

            case DeviceInfoIsFloppy:
                return RegT.Null;

            case DeviceInfoGetConfigPath:
                // early versions return a drive letter, later ones a path; nothing uses it yet
                return RegT.Null;

            // SCI uses these to delete saved games in a less than portable way: the scripts get a
            // name to throw away, and the save is deleted here
            case DeviceInfoGetSaveCatName:
                s.SegMan.Strcpy(argv[1], "__throwaway");
                break;

            case DeviceInfoGetSaveFileName:
            {
                int virtualId = argv.UInt16(3);
                s.SegMan.Strcpy(argv[1], "__throwaway");
                if (virtualId < Savegame.OfficialRangeStart || virtualId > Savegame.OfficialRangeEnd)
                {
                    throw new SciVmException("kDeviceInfo(deleteSave): invalid savegame ID specified");
                }

                int savegameId = virtualId - Savegame.OfficialRangeStart;
                var saves = Savegame.ListSavegames(s);
                if (Savegame.FindSavegame(saves, savegameId) != -1)
                {
                    // the save still exists
                    SciFile.RemoveSaveFile(s, Savegame.SaveName(s, savegameId));
                }

                break;
            }

            default:
                throw new SciVmException($"Unknown DeviceInfo() sub-command: {mode}");
        }

        return s.Acc;
    }

    public static RegT CheckFreeSpace(EngineState s, KernelArgs argv)
    {
        // the path to test is not checked
        const int SaveGameSize = 0;
        const int FreeDiskSpace = 1;
        const int EnoughSpaceToSave = 2;

        // in SCI2.1 Middle the call moved into kFileIO, with the arguments the other way round
        int subop;
        if (s.Version >= SciVersion.V2_1Middle)
        {
            subop = argv.Count > 0 ? argv.Int16(0) : EnoughSpaceToSave;
        }
        else
        {
            subop = argv.Count > 1 ? argv.Int16(1) : EnoughSpaceToSave;
        }

        return subop switch
        {
            SaveGameSize => RegT.Make(0, 0),
            FreeDiskSpace => RegT.Make(0, 0x7FFF), // in KiB, at most 32 MiB
            EnoughSpaceToSave => RegT.Make(0, 1),
            _ => throw new SciVmException($"kCheckFreeSpace: called with unknown sub-op {subop}"),
        };
    }

    public static RegT ValidPath(EngineState s, KernelArgs argv) => RegT.Make(0, 1);

    // ---- kFileIO ----

    /// <summary>Sub-function parent; the VM dispatches on the first argument.</summary>
    public static ValueTask<RegT> FileIOAsync(EngineState s, KernelArgs argv) => new(s.Acc);

    public static RegT FileIOOpen(EngineState s, KernelArgs argv)
    {
        string name = s.SegMan.GetString(argv[0]);
        var mode = (FileOpenMode)argv.UInt16(1);
        bool unwrapFilename = true;

        // SQ4 floppy prefixes the names with /\, and so does the QFG4 import; removed before
        // the empty test for QFG4
        if (name.StartsWith("/\\", StringComparison.Ordinal))
        {
            name = name[2..];
        }

        if (name.Length == 0)
        {
            // happens many times in KQ1, when typing for example
            s.Log.Debug("sci.file", "Attempted to open a file with an empty filename");
            return RegT.Signal;
        }

        // SQ4 floppy updates its save index sq4sg.dir when deleting saves; no index is used
        if (name == "sq4sg.dir")
        {
            s.Log.Debug("sci.file", "Not opening unused file sq4sg.dir");
            return RegT.Signal;
        }

        // ECO2's Ecorder prints to PRN, the DOS printer
        if (s.GameId == SciGameId.Ecoquest2 && name == "prn")
        {
            return RegT.Signal;
        }

        if (s.Version >= SciVersion.V2)
        {
            var special = OpenSci32File(s, ref name, mode, ref unwrapFilename);
            if (special is { } handle)
            {
                return handle;
            }
        }

        s.Log.Debug("sci.file", $"kFileIO(open): {name}, 0x{(int)mode:x}");

        if (name.StartsWith("sciAudio\\", StringComparison.Ordinal))
        {
            // the fan-made sciAudio extension: no files, a virtual handle instead
            return RegT.Make(0, SciFile.VirtualFileHandleSciAudio);
        }

        // the QFG import rooms list their files virtually. The list of saved heroes is already
        // gone, so the choice was remembered by kDrawControl
        if (s.InQfgImportRoom)
        {
            name = s.DirSeeker.GetVirtualFilename(s.ChosenQfGImportItem);
            unwrapFilename = false;
        }

        return SciFile.FileOpen(s, name, mode, unwrapFilename);
    }

    /// <summary>The SCI32 files kFileIOOpen handles itself, or null for an ordinary file.</summary>
    private static RegT? OpenSci32File(EngineState s, ref string name, FileOpenMode mode, ref bool unwrapFilename)
    {
        // GK1, GK2, KQ7, LSL6 hires, Phantasmagoria, PQ4, PQ:SWAT and SQ6 read their version
        // from the VERSION file
        if (name.Equals("version", StringComparison.OrdinalIgnoreCase))
        {
            unwrapFilename = false;

            // LSL6 hires has it in a file with an empty extension
            if (SciFile.GameFileExists(s, name + "."))
            {
                name += ".";
            }
        }

        switch (s.GameId)
        {
            case SciGameId.Shivers when name.EndsWith(".SG", StringComparison.Ordinal):
            {
                // Shivers keeps the name and score of each save in a %d.SG file, which its
                // save and load screen reads
                if (mode is FileOpenMode.OpenOrCreate or FileOpenMode.Create)
                {
                    s.Log.Debug("sci.file", $"Not creating unused file {name}");
                    return RegT.Signal;
                }

                // a file with the description and score, as the scripts expect
                if (!TryParseLeadingNumber(name, 0, out int saveNo) || !Savegame.FillSavegameDesc(s, saveNo + SaveIdShift, out var save))
                {
                    return RegT.Signal;
                }

                string score = save.HighScore == 0 ? save.LowScore.ToString() : $"{save.HighScore}{save.LowScore:000}";
                return VirtualReadHandle(s, $"{SaveDescriptionName(save)}\r\n{score}", false);
            }

            case SciGameId.Mothergoosehires when name.EndsWith(".DTA", StringComparison.Ordinal):
            {
                // Mother Goose Deluxe keeps the name and avatar of each save in a %d.DTA file
                if (mode is FileOpenMode.OpenOrCreate or FileOpenMode.Create)
                {
                    s.Log.Debug("sci.file", $"Not creating unused file {name}");
                    return RegT.Signal;
                }

                // the four-language release puts the language's letter before the number
                int skip = name.Length > 0 && !char.IsAsciiDigit(name[0]) ? 1 : 0;
                if (!TryParseLeadingNumber(name, skip, out int saveNo))
                {
                    s.Log.Warning("sci.file", $"Could not parse game filename {name}");
                }

                if (!Savegame.FillSavegameDesc(s, saveNo + SaveIdShift, out var save))
                {
                    return RegT.Signal;
                }

                return VirtualReadHandle(s, $"{SaveDescriptionName(save)}\r\n{save.AvatarId:00}", true);
            }

            case SciGameId.Kq7 when name == "temp.tmp":
                // KQ7 rewrites its catalogue through temp.tmp; no catalogue is kept
                return RegT.Make(0, SciFile.VirtualFileHandleSci32Save);

            case SciGameId.Pqswat:
                // PQ:SWAT makes a subdirectory for each player profile
                name = name.Replace('\\', '_');
                break;

            case SciGameId.Phantasmagoria2 when name == "RESDUK.PAT":
                // the censorship password file; the game option stands in for it
                return RegT.Signal;

            case SciGameId.Rama:
            {
                if (name == "PREF.DAT")
                {
                    return RegT.Signal;
                }

                int saveNo = -1;
                if (name is "911.sg" or "autorama.sg")
                {
                    saveNo = Savegame.AutoSaveId;
                }
                else if (TryParseSuffixNumber(name, "ramasg.", out int number))
                {
                    saveNo = number + SaveIdShift;
                }

                if (saveNo != -1 && OpenRamaSave(s, saveNo, mode) is { } handle)
                {
                    return handle;
                }

                break;
            }
        }

        // see kMakeSaveCatName
        if (name == "fake.cat")
        {
            return RegT.Make(0, SciFile.VirtualFileHandleSci32Save);
        }

        if (SciFile.IsSaveCatalogue(name))
        {
            if (!SciFile.SaveCatalogueExists(s, name))
            {
                return RegT.Signal;
            }

            // KQ7 and RAMA read the saves from their catalogues directly, not with kGetSaveFiles
            if (name is "kq7cdsg.cat" or "ramasg.cat")
            {
                if (mode is FileOpenMode.OpenOrCreate or FileOpenMode.Create)
                {
                    s.Log.Debug("sci.file", $"Not creating unused file {name}");
                    return RegT.Signal;
                }

                var catalogue = name == "kq7cdsg.cat"
                    ? SciFile.MakeCatalogue(s, 10, Savegame.MaxSaveNameLength, string.Empty, false)
                    : SciFile.MakeCatalogue(s, 100, 20, "ramasg.%d", true);
                return SciFile.AddHandle(s, catalogue, null, string.Empty);
            }

            // a dummy handle stands for the catalogue, so the scripts' writes are ignored
            return RegT.Make(0, SciFile.VirtualFileHandleSci32Save);
        }

        return null;
    }

    /// <summary>
    /// RAMA writes its saves itself, through kFileIO: the file gets ScummVM's save header in front,
    /// and its words are whole registers.
    /// </summary>
    private static RegT? OpenRamaSave(EngineState s, int saveNo, FileOpenMode mode)
    {
        string fileName = Savegame.SaveName(s, saveNo);
        Stream? input = null;
        Stream? output = null;
        bool valid = false;

        if (mode == FileOpenMode.OpenOrFail)
        {
            input = SciFile.OpenForLoading(s, fileName);
            if (input is not null && Savegame.GetMetadata(input, out _))
            {
                Savegame.SkipThumbnail(input);
                valid = true;

                // ScummVM also sets the engine's play time from the header here; the tick
                // count is the host's clock in this port
            }
        }
        else
        {
            output = SciFile.OpenForSaving(s, fileName);
            if (output is not null)
            {
                string saveName = saveNo == Savegame.AutoSaveId ? "(Autosave)" : GetRamaSaveName(s, saveNo - SaveIdShift);
                Savegame.WriteMetadata(s, output, saveName, ReadVersionFile(s));
                valid = true;
            }
        }

        if (!valid)
        {
            input?.Dispose();
            output?.Dispose();
            return null;
        }

        return SciFile.AddHandle(s, input, output, ScummVMSaveHandleName);
    }

    /// <summary>The description RAMA gave a save, from its catalogue object (global 130).</summary>
    private static string GetRamaSaveName(EngineState s, int saveNo)
    {
        var sel = s.Kernel.Selectors;
        var catalogId = s.GetGlobal(GlobalVar.RamaCatalogFile);
        if (catalogId.IsNull)
        {
            throw new SciVmException("Could not find CatalogFile object to retrieve save game name");
        }

        var list = s.SegMan.LookupList(Selectors.ReadSelector(s, catalogId, sel.Elements));
        var node = s.SegMan.LookupNode(list.First);
        while (node is not null)
        {
            var entryId = node.Value;
            if (Selectors.ReadSelectorValue(s, entryId, sel.FileNumber) == saveNo)
            {
                var description = Selectors.ReadSelector(s, entryId, sel.Description);
                if (s.SegMan.IsObject(description))
                {
                    description = Selectors.ReadSelector(s, description, sel.Data);
                }

                return s.SegMan.GetString(description);
            }

            node = s.SegMan.LookupNode(node.Succ);
        }

        throw new SciVmException($"Could not find a save name for save {saveNo}");
    }

    /// <summary>The name of a save as the original wrote it into its own files, at most 36 characters.</summary>
    private static string SaveDescriptionName(SavegameDesc save)
        => save.Name.Length > Savegame.MaxSaveNameLength ? save.Name[..Savegame.MaxSaveNameLength] : save.Name;

    /// <summary>A read-only handle on made-up contents, optionally terminated.</summary>
    private static RegT VirtualReadHandle(EngineState s, string contents, bool terminate)
    {
        var bytes = Encoding.Latin1.GetBytes(terminate ? contents + "\0" : contents);
        return SciFile.AddHandle(s, new MemoryStream(bytes, writable: false), null, string.Empty);
    }

    /// <summary>The number at the start of the name from the given position, as sscanf's %d reads it.</summary>
    private static bool TryParseLeadingNumber(string name, int start, out int number)
    {
        var match = Regex.Match(name[Math.Min(start, name.Length)..], @"^\s*[+-]?\d+");
        number = 0;
        return match.Success && int.TryParse(match.Value, out number);
    }

    /// <summary>The number after a fixed prefix ("ramasg.%d").</summary>
    private static bool TryParseSuffixNumber(string name, string prefix, out int number)
    {
        number = 0;
        return name.StartsWith(prefix, StringComparison.Ordinal) && TryParseLeadingNumber(name, prefix.Length, out number);
    }

    public static RegT FileIOClose(EngineState s, KernelArgs argv)
    {
        if (argv[0] == RegT.Signal)
        {
            return s.Acc;
        }

        int handle = argv.UInt16(0);
        if (handle >= SciFile.VirtualFileHandleStart)
        {
            // a virtual handle
            return RegT.True;
        }

        var f = SciFile.GetFileFromHandle(s, handle);
        if (f is not null)
        {
            f.Close();
        }

        // SCI0 returns nothing
        if (s.Version <= SciVersion.V0Late)
        {
            return s.Acc;
        }

        return f is not null ? RegT.True : RegT.Null;
    }

    public static RegT FileIOReadRaw(EngineState s, KernelArgs argv)
    {
        int handle = argv.UInt16(0);
        var dest = argv[1];
        int size = argv.UInt16(2);
        int bytesRead = 0;
        var buf = new byte[size];

        var f = SciFile.GetFileFromHandle(s, handle);
        if (f is not null)
        {
            if (s.Version >= SciVersion.V2)
            {
                var destReference = s.SegMan.Dereference(dest);
                if (s.SegMan.GetSegmentObj(dest.Segment) is null)
                {
                    throw new SciVmException($"kFileIO(readRaw): invalid destination {dest}");
                }

                if (destReference.MaxSize == size - 4 && s.SegMan.GetSegmentType(dest.Segment) == SegmentType.Array)
                {
                    // an array structure, which starts with its element count and size; found in
                    // the ARC files of the Behind the Developer's Shield and Inside the Chest demos
                    f.In?.Seek(4, SeekOrigin.Current);
                    size -= 4;
                }
            }

            if (f.In is not null)
            {
                bytesRead = f.In.ReadAtLeast(buf.AsSpan(0, size), size, throwOnEndOfStream: false);
            }
        }

        if (bytesRead > 0)
        {
            s.SegMan.Memcpy(dest, buf, bytesRead);
        }

        return RegT.Make(0, bytesRead);
    }

    public static RegT FileIOWriteRaw(EngineState s, KernelArgs argv)
    {
        int handle = argv.UInt16(0);
        int size = argv.UInt16(2);

        if (s.Version >= SciVersion.V2 && handle == SciFile.VirtualFileHandleSci32Save)
        {
            return RegT.Make(0, size);
        }

        var buf = new byte[size];
        s.SegMan.Memcpy(buf, argv[1], size);

        var f = SciFile.GetFileFromHandle(s, handle);
        if (f?.Out is { } output && TryWrite(s, output, buf))
        {
            return RegT.Make(0, size);
        }

        return s.Version >= SciVersion.V2 ? RegT.Signal : RegT.Null;
    }

    private static bool TryWrite(EngineState s, Stream output, ReadOnlySpan<byte> data)
    {
        try
        {
            output.Write(data);
            return true;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ObjectDisposedException)
        {
            s.Log.Warning("sci.file", $"Writing a file failed: {ex.Message}");
            return false;
        }
    }

    public static RegT FileIOUnlink(EngineState s, KernelArgs argv)
    {
        string name = s.SegMan.GetString(argv[0]);
        bool result;

        // SQ4 floppy prefixes the names with /\
        if (name.StartsWith("/\\", StringComparison.Ordinal))
        {
            name = name[2..];
        }

        if (s.GameId == SciGameId.Sq4 && name.StartsWith("sq4sg.", StringComparison.Ordinal))
        {
            // SQ4 floppy names its saves sq4sg.xxx after the virtual id
            int virtualId = int.TryParse(name[^3..], out int number) ? number : 0;
            name = Savegame.SaveName(s, virtualId - Savegame.OfficialRangeStart);
            result = SciFile.RemoveSaveFile(s, name);
        }
        else if (s.Version >= SciVersion.V2)
        {
            // KQ7 and RAMA name their saves themselves too
            if (TryParseSuffixNumber(name, "kq7cdsg.", out int saveNo) || TryParseSuffixNumber(name, "ramasg.", out saveNo))
            {
                name = Savegame.SaveName(s, saveNo + SaveIdShift);
            }
            else if (s.GameId == SciGameId.Rama && name is "911.sg" or "autorama.sg")
            {
                name = Savegame.SaveName(s, Savegame.AutoSaveId);
            }

            // the name may already be wrapped, so both are tried
            result = SciFile.RemoveSaveFile(s, name) || SciFile.RemoveSaveFile(s, s.WrapFilename(name));
        }
        else if (s.GameId == SciGameId.Kq5 && s.Platform == SciPlatform.FmTowns && name.StartsWith("a:\\KQ5sg.", StringComparison.Ordinal))
        {
            // KQ5 FM-Towns deletes its saves by a fixed path from its own restore screen (script 764)
            result = false;
            if (TryParseLeadingNumber(name, "a:\\KQ5sg.".Length, out int saveNo) && saveNo is >= 1 and <= 10)
            {
                result = SciFile.RemoveSaveFile(s, Savegame.SaveName(s, saveNo));
            }
        }
        else
        {
            result = SciFile.RemoveSaveFile(s, s.WrapFilename(name));
        }

        s.Log.Debug("sci.file", $"kFileIO(unlink): {name}");
        return RegT.Make(0, result ? 1 : 0);
    }

    public static RegT FileIOReadString(EngineState s, KernelArgs argv)
    {
        int maxSize = argv.UInt16(1);
        var buf = new byte[maxSize];
        int handle = argv.UInt16(2);

        int bytesRead = SciFile.FgetsWrapper(s, buf, maxSize, handle);

        // a size too large for the destination is reduced
        var destR = s.SegMan.Dereference(argv[0]);
        if (!destR.IsValid)
        {
            throw new SciVmException($"kFileIO(readString): invalid destination {argv[0]}");
        }

        if (bytesRead > destR.MaxSize)
        {
            throw new SciVmException($"kFileIO(readString) attempting to read {bytesRead} bytes into buffer of size {destR.MaxSize}");
        }

        if (maxSize > destR.MaxSize)
        {
            maxSize = destR.MaxSize;
        }

        s.SegMan.Memcpy(argv[0], buf, maxSize);
        return bytesRead != 0 ? argv[0] : RegT.Null;
    }

    public static RegT FileIOWriteString(EngineState s, KernelArgs argv)
    {
        int handle = argv.UInt16(0);
        string str = s.SegMan.GetString(argv[1]);

        // the commands of the fan-made sciAudio extension (an MP3 player that read them from
        // text files) are not supported
        if (handle == SciFile.VirtualFileHandleSciAudio)
        {
            s.Log.Warning("sci.file", "sciAudio is not supported");
            return RegT.Null;
        }

        var f = SciFile.GetFileFromHandle(s, handle);
        var bytes = Encoding.Latin1.GetBytes(str);
        if (f?.Out is { } output && TryWrite(s, output, bytes))
        {
            return RegT.Make(0, bytes.Length);
        }

        return s.Version >= SciVersion.V2 ? RegT.Signal : RegT.Null;
    }

    public static RegT FileIOSeek(EngineState s, KernelArgs argv)
    {
        int handle = argv.UInt16(0);
        short offset = argv.Int16(1);
        int whence = argv.UInt16(2);

        var f = SciFile.GetFileFromHandle(s, handle);
        if (f?.In is { } input)
        {
            offset = (short)Math.Min(offset, input.Length);
            long target = whence switch
            {
                1 => input.Position + offset,
                2 => input.Length + offset,
                _ => offset,
            };

            bool success = target >= 0 && target <= input.Length;
            if (success)
            {
                input.Position = target;
            }

            if (s.Version >= SciVersion.V2)
            {
                return success ? RegT.Make(0, (int)input.Position) : RegT.Signal;
            }

            return RegT.Make(0, success ? 1 : 0);
        }

        if (f?.Out is not null)
        {
            throw new SciVmException($"kFileIOSeek: Unsupported seek operation on a writeable stream (offset: {offset}, whence: {whence})");
        }

        return RegT.Signal;
    }

    public static RegT FileIOFindFirst(EngineState s, KernelArgs argv)
    {
        string mask = s.SegMan.GetString(argv[0]);

        // the mask gets the game's prefix, so "*" returns every file of the game; the attribute
        // argument is not used
        if (mask == "*.*")
        {
            mask = "*";
        }

        return s.DirSeeker.FirstFile(s, mask, argv[1]);
    }

    public static RegT FileIOFindNext(EngineState s, KernelArgs argv) => s.DirSeeker.NextFile(s);

    public static RegT FileIOExists(EngineState s, KernelArgs argv)
    {
        string name = s.SegMan.GetString(argv[0]);

        // HACK: Pepper's Adventure in Time looks for CDAUDIO over and over in its game menu
        if (s.GameId == SciGameId.Pepper && name == "CDAUDIO")
        {
            return RegT.Null;
        }

        if (s.Version >= SciVersion.V2)
        {
            if (SciFile.IsSaveCatalogue(name))
            {
                return SciFile.SaveCatalogueExists(s, name) ? RegT.True : RegT.Null;
            }

            int findSaveNo = -1;
            if (s.GameId == SciGameId.Lsl7 && name == "autosvsg.000")
            {
                // LSL7 goes to the main menu at startup only when the autosave exists
                findSaveNo = Savegame.AutoSaveId;
            }
            else if (s.GameId == SciGameId.Rama)
            {
                // RAMA checks for the save files before listing them in its save and load screen
                if (name == "autorama.sg")
                {
                    findSaveNo = Savegame.AutoSaveId;
                }
                else if (TryParseSuffixNumber(name, "ramasg.", out int number))
                {
                    findSaveNo = number + SaveIdShift;
                }
            }

            if (findSaveNo != -1)
            {
                return SciFile.SaveFileExists(s, Savegame.SaveName(s, findSaveNo)) ? RegT.True : RegT.Null;
            }
        }

        if (s.GameId == SciGameId.Kq5 && s.Platform == SciPlatform.FmTowns
            && name.StartsWith("a:\\KQ5sg.", StringComparison.Ordinal)
            && TryParseLeadingNumber(name, "a:\\KQ5sg.".Length, out int kq5SaveNo) && kq5SaveNo is >= 1 and <= 10)
        {
            // KQ5 FM-Towns looks for its saves by a fixed path from its own restore screen (script 764)
            return Savegame.FindSavegame(Savegame.ListSavegames(s), kq5SaveNo) != -1 ? RegT.True : RegT.Null;
        }

        // a game file, a file of the store with that name, or one with the game's prefix
        bool exists = SciFile.GameFileExists(s, name)
            || SciFile.SaveFileExists(s, name)
            || SciFile.SaveFileExists(s, s.WrapFilename(name));

        // GK1's easter egg at the Voodoo Hounfour (script 805) wants a file named "buster"
        if (!exists && name == "buster")
        {
            exists = true;
        }

        // The English LSL5 creates memory.drv (its password) when it cannot open it, but the
        // other versions check for it with this call and stop without it, so one with no
        // password is made
        if (!exists && name == "memory.drv")
        {
            byte[] defaultContent = [0xE9, 0xE9, 0xEB, 0xE1, 0x0D, 0x0A, 0x31, 0x30, 0x30, 0x30];
            using var outFile = SciFile.OpenForSaving(s, s.WrapFilename(name));
            exists = outFile is not null && TryWrite(s, outFile, defaultContent);
        }

        // KQ6 Mac checks for two videos before playing them, which may be MacBinary files
        if (!exists && s.GameId == SciGameId.Kq6 && s.Platform == SciPlatform.Macintosh && name is "HalfDome" or "Kq6Movie")
        {
            exists = SciFile.GameFileExists(s, name + ".bin") || SciFile.GameFileExists(s, name + ".rsrc");
        }

        s.Log.Debug("sci.file", $"kFileIO(fileExists) {name} -> {exists}");
        return RegT.Make(0, exists ? 1 : 0);
    }

    public static RegT FileIORename(EngineState s, KernelArgs argv)
    {
        // only the game's own files are renamed: Phantasmagoria and KQ7 rewrite their
        // catalogues to a temporary file and rename it when deleting saves
        string oldName = s.WrapFilename(s.SegMan.GetString(argv[0]));
        string newName = s.WrapFilename(s.SegMan.GetString(argv[1]));

        // SCI1.1 returns 0 or a DOS error code, SCI32 -1 on failure; -1 is returned for all
        return StoreOperation(s, store => store.Rename(oldName, newName)) ? RegT.Null : RegT.Signal;
    }

    public static RegT FileIOCopy(EngineState s, KernelArgs argv)
    {
        string oldName = s.WrapFilename(s.SegMan.GetString(argv[0]));
        string newName = s.WrapFilename(s.SegMan.GetString(argv[1]));
        return StoreOperation(s, store => store.Copy(oldName, newName)) ? RegT.Null : RegT.Signal;
    }

    private static bool StoreOperation(EngineState s, Func<ISaveStore, bool> operation)
    {
        if (s.Saves is null)
        {
            return false;
        }

        try
        {
            return operation(s.Saves);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            s.Log.Warning("sci.file", $"A file operation failed: {ex.Message}");
            return false;
        }
    }

    // ---- the SCI32 additions ----

    public static RegT FileIOReadByte(EngineState s, KernelArgs argv)
    {
        // the byte goes into the low byte of the accumulator
        var f = SciFile.GetFileFromHandle(s, argv.UInt16(0));
        if (f is null)
        {
            return RegT.Null;
        }

        int value = f.In?.ReadByte() ?? -1;
        return RegT.Make(0, (s.Acc.ToUInt16() & 0xFF00) | (value < 0 ? 0 : value));
    }

    public static RegT FileIOWriteByte(EngineState s, KernelArgs argv)
    {
        var f = SciFile.GetFileFromHandle(s, argv.UInt16(0));
        if (f?.Out is { } output && TryWrite(s, output, [(byte)(argv.UInt16(1) & 0xFF)]))
        {
            return RegT.Make(0, 1); // bytes written
        }

        return RegT.Signal;
    }

    public static RegT FileIOReadWord(EngineState s, KernelArgs argv)
    {
        var f = SciFile.GetFileFromHandle(s, argv.UInt16(0));
        if (f?.In is not { } input)
        {
            return s.Acc;
        }

        Span<byte> word = stackalloc byte[4];
        if (f.Name == ScummVMSaveHandleName)
        {
            // a whole register, its segment as it is packed in saves
            int read = input.ReadAtLeast(word, 4, throwOnEndOfStream: false);
            word[read..].Clear();
            return RegT.FromPacked(BinaryPrimitives.ReadUInt16LittleEndian(word), BinaryPrimitives.ReadUInt16LittleEndian(word[2..]));
        }

        int count = input.ReadAtLeast(word[..2], 2, throwOnEndOfStream: false);
        word[count..2].Clear();
        return RegT.Make(0, BinaryPrimitives.ReadUInt16LittleEndian(word));
    }

    public static RegT FileIOWriteWord(EngineState s, KernelArgs argv)
    {
        int handle = argv.UInt16(0);
        if (handle == SciFile.VirtualFileHandleSci32Save)
        {
            return RegT.Make(0, 2); // bytes written
        }

        var f = SciFile.GetFileFromHandle(s, handle);
        if (f?.Out is not { } output)
        {
            return RegT.Signal;
        }

        var value = argv[1];
        Span<byte> word = stackalloc byte[4];
        if (f.Name == ScummVMSaveHandleName)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(word, value.PackedSegment);
            BinaryPrimitives.WriteUInt16LittleEndian(word[2..], (ushort)value.Offset);
            TryWrite(s, output, word);
        }
        else
        {
            if (value.IsPointer)
            {
                throw new SciVmException($"kFileIO(WriteWord): Attempt to write non-number {value} to non-save file");
            }

            BinaryPrimitives.WriteUInt16LittleEndian(word, value.ToUInt16());
            TryWrite(s, output, word[..2]);
        }

        return RegT.Make(0, 2); // bytes written
    }

    public static RegT FileIOGetCWD(EngineState s, KernelArgs argv)
    {
        s.SegMan.LookupArray(argv[0]).FromString("C:\\SIERRA\\");
        return argv[0];
    }

    /// <summary>Torin, LSL7 and RAMA check that the save directory is valid; the directory is never used here.</summary>
    public static RegT FileIOIsValidDirectory(EngineState s, KernelArgs argv) => RegT.True;
}
