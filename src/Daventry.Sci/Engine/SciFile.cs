using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using System.Buffers.Binary;
using System.Text;

namespace Daventry.Sci.Engine;

/// <summary>The modes of kFileIO's open. Mirrors kFileOpenMode.</summary>
public enum FileOpenMode
{
    OpenOrCreate = 0,
    OpenOrFail = 1,
    Create = 2,
}

/// <summary>A file the scripts opened with kFileIO. Mirrors FileHandle.</summary>
public sealed class FileHandle
{
    public string Name = string.Empty;
    public Stream? In;
    public Stream? Out;

    public bool IsOpen => In is not null || Out is not null;

    public void Close()
    {
        // both can be set, and then they are the same stream
        if (In is not null)
        {
            In.Dispose();
        }
        else
        {
            Out?.Dispose();
        }

        In = null;
        Out = null;
        Name = string.Empty;
    }
}

/// <summary>
/// A file read into memory to be read and written in place, and written back to the save store
/// when it is closed, if it was changed. Port of SaveFileRewriteStream.
/// </summary>
public sealed class SaveFileRewriteStream : MemoryStream
{
    private readonly ISaveStore? _store;
    private readonly string _fileName;
    private bool _changed;

    public SaveFileRewriteStream(ISaveStore? store, string fileName, Stream? inFile, FileOpenMode mode)
    {
        _store = store;
        _fileName = fileName;
        bool truncate = mode == FileOpenMode.Create;
        bool seekToEnd = mode == FileOpenMode.OpenOrCreate;
        if (!truncate && inFile is not null)
        {
            inFile.CopyTo(this);
            Position = seekToEnd ? Length : 0;
            _changed = false;
        }
        else
        {
            _changed = true;
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _changed = true;
        base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _changed = true;
        base.Write(buffer);
    }

    public override void WriteByte(byte value)
    {
        _changed = true;
        base.WriteByte(value);
    }

    /// <summary>Writes the file back to the store.</summary>
    public void Commit()
    {
        if (!_changed || _store is null)
        {
            return;
        }

        _store.WriteAllBytes(_fileName, GetBuffer().AsSpan(0, (int)Length));
        _changed = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Commit();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// kFileIO's directory listing of the files a game wrote, or the virtual listing of the
/// characters exported from earlier Quest for Glory games. Port of DirSeeker.
/// </summary>
public sealed class DirSeeker
{
    private readonly List<string> _files = [];
    private readonly List<string> _virtualFiles = [];
    private RegT _outBuffer = RegT.Null;
    private int _iter;

    public RegT FirstFile(EngineState s, string mask, RegT buffer)
    {
        if (buffer.Segment == 0)
        {
            throw new SciVmException($"DirSeeker::firstFile('{mask}') invoked with invalid buffer");
        }

        _outBuffer = buffer;
        _files.Clear();
        _virtualFiles.Clear();

        int qfgImport = SciFile.QfgImportRoomGame(s);
        if (qfgImport != 0)
        {
            AddAsVirtualFiles(s, "-QfG1-", "qfg1-*");
            AddAsVirtualFiles(s, "-QfG1VGA-", "qfg1vga-*");
            if (qfgImport > 2)
            {
                AddAsVirtualFiles(s, "-QfG2-", "qfg2-*");
                AddAsVirtualFiles(s, "-QfG2 AGDI-", "qfg2agdi-*");
            }

            if (qfgImport > 3)
            {
                AddAsVirtualFiles(s, "-QfG3-", "qfg3-*");
            }

            if (qfgImport == 3)
            {
                // QFG3 sorts the listing itself, which would put the virtual list out of step
                var savedHeros = s.SegMan.FindObjectByName("savedHeros");
                if (!savedHeros.IsNull)
                {
                    Selectors.WriteSelectorValue(s, savedHeros, s.Kernel.Selectors.Sort, 0);
                }
            }
        }
        else if (s.Saves is not null)
        {
            _files.AddRange(s.Saves.ListMatching(s.WrapFilename(mask)));
        }

        _iter = 0;
        return NextFile(s);
    }

    public RegT NextFile(EngineState s)
    {
        if (_iter >= _files.Count)
        {
            return RegT.Null;
        }

        // the prefix is stripped, unless this is a virtual listing
        string name = _virtualFiles.Count == 0 ? s.UnwrapFilename(_files[_iter]) : _files[_iter];
        if (name.Length > 12)
        {
            name = name[..12];
        }

        s.SegMan.Strcpy(_outBuffer, name);
        _iter++;
        return _outBuffer;
    }

    public string GetVirtualFilename(int fileNumber)
    {
        if (fileNumber < 0 || fileNumber >= _virtualFiles.Count)
        {
            throw new SciVmException("invalid virtual filename access");
        }

        return _virtualFiles[fileNumber];
    }

    private void AddAsVirtualFiles(EngineState s, string title, string fileMask)
    {
        if (s.Saves is null)
        {
            return;
        }

        var foundFiles = s.Saves.ListMatching(fileMask).Order(StringComparer.Ordinal).ToList();
        bool titleAdded = false;
        foreach (string regularFilename in foundFiles)
        {
            string wrappedFilename = regularFilename[(fileMask.Length - 1)..];
            using var testFile = s.Saves.OpenRead(regularFilename);
            if (testFile is null)
            {
                continue;
            }

            // anything larger than 1 KB is a saved game, which does not belong in the list
            if (testFile.Length > 1024)
            {
                continue;
            }

            if (!titleAdded)
            {
                _files.Add(title);
                _virtualFiles.Add(string.Empty);
                titleAdded = true;
            }

            // the prefix is removed for display, but the real name is kept
            _files.Add(wrappedFilename);
            _virtualFiles.Add(regularFilename);
        }
    }
}

/// <summary>
/// The file layer behind kFileIO. A file a game writes goes to the host's save store under the
/// name "&lt;game id&gt;-&lt;file name&gt;" (ScummVM's wrapped name); a file it reads is looked
/// for there first and then among the game's own files. Port of engine/file.cpp.
/// </summary>
public static class SciFile
{
    public const int VirtualFileHandleStart = 32000;
    public const int VirtualFileHandleSci32Save = 32100;
    public const int VirtualFileHandleSciAudio = 32300;
    public const int VirtualFileHandleEnd = 32300;

    private const int InitialFileHandles = 5;

    /// <summary>Closes every file and leaves a new table of handles; handle 0 is never used.</summary>
    public static void ResetFileHandles(EngineState s)
    {
        foreach (var handle in s.FileHandles)
        {
            handle.Close();
        }

        s.FileHandles.Clear();
        for (int i = 0; i < InitialFileHandles; i++)
        {
            s.FileHandles.Add(new FileHandle());
        }
    }

    public static int FindFreeFileHandle(EngineState s)
    {
        int handle = 1;
        while (handle < s.FileHandles.Count && s.FileHandles[handle].IsOpen)
        {
            handle++;
        }

        if (handle == s.FileHandles.Count)
        {
            s.FileHandles.Add(new FileHandle());
        }

        return handle;
    }

    /// <summary>A handle with the given streams, as file_open leaves it.</summary>
    public static RegT AddHandle(EngineState s, Stream? inFile, Stream? outFile, string name)
    {
        int handle = FindFreeFileHandle(s);
        var f = s.FileHandles[handle];
        f.In = inFile;
        f.Out = outFile;
        f.Name = name;
        return RegT.Make(0, handle);
    }

    /// <summary>Port of file_open.</summary>
    public static RegT FileOpen(EngineState s, string filename, FileOpenMode mode, bool unwrapFilename)
    {
        string englishName = SciLanguage.GetSciLanguageString(filename, SciLanguage.English, out _, out _).ToLowerInvariant();
        string wrappedName = unwrapFilename ? s.WrapFilename(englishName) : englishName;
        Stream? inFile = null;
        Stream? outFile = null;

        // ScummVM compresses what it writes except for a few games' files (the QFG characters,
        // Hoyle 5's options, Phantasmagoria's saves); nothing is compressed here

        bool isRewritableFile = s.GameId switch
        {
            SciGameId.Phantasmagoria => filename is "phantsg.dir" or "chase.dat" or "tmp.dat",
            SciGameId.Pqswat => filename == "swat.dat",
            _ => false,
        };

        if (isRewritableFile)
        {
            s.Log.Debug("sci.file", $"  -> file_open opening {wrappedName} for rewriting");

            // without a file of that name in the store, the game's own file is read
            var original = OpenForLoading(s, wrappedName) ?? OpenGameFile(s, englishName);
            if (mode == FileOpenMode.OpenOrFail && original is null)
            {
                s.Log.Debug("sci.file", $"  -> file_open(kFileOpenModeOpenOrFail): failed to open file '{englishName}'");
                return RegT.Signal;
            }

            var stream = new SaveFileRewriteStream(s.Saves, wrappedName, original, mode);
            original?.Dispose();
            inFile = stream;
            outFile = stream;
        }
        else if (mode == FileOpenMode.OpenOrFail)
        {
            inFile = OpenForLoading(s, wrappedName) ?? OpenGameFile(s, englishName);
            if (inFile is null)
            {
                s.Log.Debug("sci.file", $"  -> file_open(kFileOpenModeOpenOrFail): failed to open file '{englishName}'");
            }
        }
        else if (mode is FileOpenMode.Create or FileOpenMode.OpenOrCreate)
        {
            // both create the file afresh, destroying anything it had. QFG1 opens its character
            // export with Create, closes it at once and opens it again with OpenOrCreate
            outFile = OpenForSaving(s, wrappedName);
            if (outFile is null)
            {
                s.Log.Debug("sci.file", $"  -> file_open({mode}): failed to create file '{englishName}'");
            }
        }
        else
        {
            throw new SciVmException($"file_open: unsupported mode {(int)mode} (filename '{englishName}')");
        }

        if (inFile is null && outFile is null)
        {
            s.Log.Debug("sci.file", "  -> file_open() failed");
            return RegT.Signal;
        }

        var handle = AddHandle(s, inFile, outFile, englishName);
        s.Log.Debug("sci.file", $"  -> opened file '{englishName}' with handle {handle.Offset}");
        return handle;
    }

    /// <summary>A file of the save store, or null.</summary>
    public static Stream? OpenForLoading(EngineState s, string name)
    {
        try
        {
            return s.Saves?.OpenRead(name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            s.Log.Warning("sci.file", $"Could not read {name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>A new file in the save store, or null when there is no store or it refuses.</summary>
    public static Stream? OpenForSaving(EngineState s, string name)
    {
        if (s.Saves is null)
        {
            return null;
        }

        try
        {
            return s.Saves.OpenWrite(name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            s.Log.Warning("sci.file", $"Could not create {name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Whether the save store has a file matching the name, which may hold wildcards.</summary>
    public static bool SaveFileExists(EngineState s, string name)
    {
        try
        {
            return s.Saves is not null && s.Saves.ListMatching(name).Count > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Deletes a file of the save store. Port of removeSavefile.</summary>
    public static bool RemoveSaveFile(EngineState s, string name)
    {
        try
        {
            return s.Saves is not null && s.Saves.Delete(name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            s.Log.Warning("sci.file", $"Could not delete {name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>One of the game's own files, read only, or null. Stands in for SearchMan.</summary>
    public static Stream? OpenGameFile(EngineState s, string name) => GameFileExists(s, name) ? s.ResMan.Game.Open(name) : null;

    public static bool GameFileExists(EngineState s, string name) => name.Length > 0 && s.ResMan.Game.Exists(name);

    /// <summary>Port of getFileFromHandle.</summary>
    public static FileHandle? GetFileFromHandle(EngineState s, int handle)
    {
        if (handle == 0 || (handle >= VirtualFileHandleStart && handle <= VirtualFileHandleEnd))
        {
            throw new SciVmException($"Attempt to use invalid file handle ({handle})");
        }

        if (handle >= s.FileHandles.Count || !s.FileHandles[handle].IsOpen)
        {
            s.Log.Warning("sci.file", $"Attempt to use invalid/unused file handle {handle}");
            return null;
        }

        return s.FileHandles[handle];
    }

    /// <summary>
    /// Reads a line of at most maxSize - 1 bytes into a zeroed buffer of maxSize bytes and
    /// returns its length, which counts a final line feed although the feed itself is removed.
    /// Port of fgets_wrapper.
    /// </summary>
    public static int FgetsWrapper(EngineState s, byte[] dest, int maxSize, int handle)
    {
        // always cleared, since some scripts use the result without checking for errors (bug #12060)
        Array.Clear(dest, 0, maxSize);

        var f = GetFileFromHandle(s, handle);
        if (f is null)
        {
            return 0;
        }

        if (f.In is null)
        {
            throw new SciVmException($"fgets_wrapper: Trying to read from file '{f.Name}' opened for writing");
        }

        int readBytes = 0;
        if (maxSize > 1)
        {
            ReadLine(f.In, dest, maxSize);

            // Sierra's interpreter returned the byte count and did not stop at a NUL
            int nul = Array.IndexOf(dest, (byte)0, 0, maxSize);
            readBytes = nul >= 0 ? nul : maxSize;

            // the line is returned without its line feed
            if (readBytes > 0 && dest[readBytes - 1] == 0x0A)
            {
                dest[readBytes - 1] = 0;
            }
        }
        else if (maxSize > 0)
        {
            dest[0] = 0;
        }

        s.Log.Debug("sci.file", $"  -> FGets'ed \"{Encoding.Latin1.GetString(dest, 0, readBytes).TrimEnd('\0', '\n')}\"");
        return readBytes;
    }

    /// <summary>
    /// Reads up to bufSize - 1 bytes, through the first line feed, with CR and CR/LF read as LF,
    /// and terminates them. False at the end of the file, leaving the buffer as it was. Port of
    /// SeekableReadStream::readLine.
    /// </summary>
    public static bool ReadLine(Stream stream, byte[] buf, int bufSize)
    {
        const int Lf = 0x0A;
        const int Cr = 0x0D;
        int len = 0;
        int c = 0;
        while (len + 1 < bufSize && c != Lf)
        {
            c = stream.ReadByte();
            if (c < 0)
            {
                if (len == 0)
                {
                    return false;
                }

                break;
            }

            if (c == Cr)
            {
                // CR and CR/LF are read as a plain LF
                int next = stream.ReadByte();
                if (next >= 0 && next != Lf)
                {
                    stream.Seek(-1, SeekOrigin.Current);
                }

                c = Lf;
            }

            buf[len++] = (byte)c;
        }

        buf[len] = 0;
        return true;
    }

    /// <summary>
    /// Which Quest for Glory game's import screen is up: 2, 3 or 4, or 0 outside them. Mirrors
    /// SciEngine::inQfGImportRoom.
    /// </summary>
    public static int QfgImportRoomGame(EngineState s) => !s.InQfgImportRoom ? 0 : s.GameId switch
    {
        SciGameId.Qfg2 => 2,
        SciGameId.Qfg3 => 3,
        _ => 4,
    };

    // ---- SCI32 save catalogues ----

    public static bool IsSaveCatalogue(string name) => name == "autosave.cat" || name.EndsWith("sg.cat", StringComparison.Ordinal);

    /// <summary>
    /// Whether the catalogue a script looks for would exist: the saved games are checked
    /// instead, as no catalogue is written. Port of saveCatalogueExists.
    /// </summary>
    public static bool SaveCatalogueExists(EngineState s, string name)
    {
        // some games always have one save, the "new game" one, which does not count
        int numPermanentSaves = s.GameId is SciGameId.Torin or SciGameId.Lsl7 or SciGameId.Lighthouse ? 1 : 0;

        // Torin uses autosave.cat, LSL7 autosvsg.cat
        if (name is "autosave.cat" or "autosvsg.cat")
        {
            return SaveFileExists(s, Savegame.SaveName(s, Savegame.AutoSaveId));
        }

        return s.Saves is not null && s.Saves.ListMatching(Savegame.SavegamePattern(s)).Count > numPermanentSaves;
    }

    /// <summary>A catalogue as KQ7 and RAMA read it, made from the saved games. Port of makeCatalogue.</summary>
    public static MemoryStream MakeCatalogue(EngineState s, int maxNumSaves, int gameNameSize, string fileNamePattern, bool ramaFormat)
    {
        const int GameIdSize = 2;
        const int NumSavesSize = 2;
        const int FreeSlotSize = 2;
        const int TerminatorSize = GameIdSize;

        var games = Savegame.ListSavegames(s);
        int numSaves = Math.Min(games.Count, maxNumSaves);
        int fileNameSize = fileNamePattern.Length == 0 ? 0 : 12;
        int entrySize = GameIdSize + fileNameSize + gameNameSize;
        int dataSize = numSaves * entrySize + TerminatorSize;
        if (ramaFormat)
        {
            dataSize += NumSavesSize + FreeSlotSize * maxNumSaves;
        }

        var data = new byte[dataSize];
        var output = data.AsSpan();
        bool[] usedSlots = ramaFormat ? new bool[maxNumSaves] : [];
        if (ramaFormat)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(output, (ushort)numSaves);
            output = output[NumSavesSize..];
        }

        for (int i = 0; i < numSaves; i++)
        {
            var save = games[i];
            ushort id = (ushort)(save.Id - KernelFunctions.SaveIdShift);
            if (!ramaFormat)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(output, id);
                output = output[GameIdSize..];
            }

            if (fileNameSize != 0)
            {
                WriteFixed(output, fileNamePattern.Replace("%d", id.ToString()), fileNameSize);
                output = output[fileNameSize..];
            }

            // the names can fill the field exactly
            WriteFixed(output, save.Name, gameNameSize);
            output = output[gameNameSize..];
            if (ramaFormat)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(output, id);
                output = output[GameIdSize..];
                usedSlots[id] = true;
            }
        }

        if (ramaFormat)
        {
            // which slots are free
            foreach (bool used in usedSlots)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(output, (ushort)(used ? 0 : 1));
                output = output[FreeSlotSize..];
            }
        }

        BinaryPrimitives.WriteUInt16LittleEndian(output, 0xFFFF);
        return new MemoryStream(data, writable: false);
    }

    /// <summary>A string in a fixed field, zero-padded and unterminated when it fills it (strncpy).</summary>
    private static void WriteFixed(Span<byte> output, string text, int size)
    {
        var bytes = Encoding.Latin1.GetBytes(text);
        int length = Math.Min(bytes.Length, size);
        bytes.AsSpan(0, length).CopyTo(output);
        output[length..size].Clear();
    }
}
