using Daventry.Core.Graphics;
using System.Buffers.Binary;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Daventry.Sci.Graphics;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Engine;

/// <summary>The header of a saved game. Mirrors SavegameMetadata.</summary>
public sealed class SavegameMetadata
{
    public string Name = string.Empty;
    public uint Version;
    public string GameVersion = string.Empty;
    public int SaveDate;
    public int SaveTime;
    public uint PlayTime;
    public ushort GameObjectOffset;
    public ushort Script0Size;
    public ushort LowScore;
    public ushort HighScore;
    public byte AvatarId;
}

/// <summary>A saved game of the current game, as the save and restore kernels list them. Mirrors SavegameDesc.</summary>
public sealed class SavegameDesc
{
    public int Id;
    public uint Date; // YYYYMMDD packed, for sorting
    public int Time;
    public uint Version;
    public string Name = string.Empty;
    public string GameVersion = string.Empty;
    public ushort Script0Size;
    public ushort GameObjectOffset;
}

/// <summary>
/// Saves and restores the whole interpreter state in ScummVM's saved game format (version 46),
/// so files can be exchanged with it. Port of engine/savegame.cpp and the listing helpers of
/// engine/file.cpp. Saves are named "&lt;game id&gt;.NNN" in the host's save store.
/// </summary>
public static class Savegame
{
    public const uint CurrentVersion = 46;
    public const uint MinimumVersion = 14;

    /// <summary>The oldest format SCI32 games can load; earlier ones lack the plane and screen item state.</summary>
    public const uint MinimumSci32Version = 41;

    public const int MaxSaveNameLength = 36; // excluding the terminator
    public const int MaxNumSaveGames = 20;
    public const int AutoSaveId = 0;
    public const int NewGameId = 999;
    public const int OfficialRangeStart = 100;
    public const int OfficialRangeEnd = 199;

    private const uint ThumbnailTag = 0x54484D42; // "THMB"
    private const uint ThumbnailTagBad = 0x424D4854; // "BMHT", written by old ScummVM builds
    private const int ThumbnailWidth = 160;
    private const int ThumbnailHeight = 100;
    private const int ThumbnailHeaderSize = 4 + 4 + 1 + 2 + 2 + (1 + 4 + 4);

    public static string SaveName(EngineState s, int id) => $"{s.SaveNamePrefix}{id:000}";

    // ---- metadata ----

    private static void SyncMetadata(SaveSerializer s, SavegameMetadata meta, EngineState? state)
    {
        meta.Name = s.SyncString(meta.Name);
        s.SyncVersion(CurrentVersion);
        meta.Version = s.Version;
        meta.GameVersion = s.SyncString(meta.GameVersion);
        meta.SaveDate = s.SyncInt32(meta.SaveDate);
        meta.SaveTime = s.SyncInt32(meta.SaveTime);
        if (s.Version < 22)
        {
            meta.GameObjectOffset = 0;
            meta.Script0Size = 0;
        }
        else
        {
            meta.GameObjectOffset = s.SyncUInt16(meta.GameObjectOffset);
            meta.Script0Size = s.SyncUInt16(meta.Script0Size);
        }

        // the play time: the engine tick count since version 34
        if (s.IsLoading)
        {
            meta.PlayTime = 0;
            if (s.Version >= 26)
            {
                meta.PlayTime = s.SyncUInt32(meta.PlayTime);
            }
        }
        else
        {
            meta.PlayTime = state?.TickCount ?? 0;
            s.SyncUInt32(meta.PlayTime);
        }

        // extra data for the restore screens of Shivers and Mother Goose Deluxe (SCI32)
        if (s.Version >= 39)
        {
            if (s.IsSaving)
            {
                meta.LowScore = meta.HighScore = 0;
                meta.AvatarId = 0;
            }

            meta.LowScore = s.SyncUInt16(meta.LowScore);
            meta.HighScore = s.SyncUInt16(meta.HighScore);
            meta.AvatarId = s.SyncByte(meta.AvatarId);
        }
    }

    /// <summary>Reads the header of a saved game. False when it is unreadable or of an unsupported version.</summary>
    public static bool GetMetadata(Stream stream, out SavegameMetadata meta)
    {
        var s = SaveSerializer.ForLoading(stream);
        meta = new SavegameMetadata();
        SyncMetadata(s, meta, null);
        if (s.Eos)
        {
            return false;
        }

        return meta.Version >= MinimumVersion && meta.Version <= CurrentVersion;
    }

    // ---- the thumbnail ----

    private static void WriteThumbnail(Stream stream, Surface8 display, ReadOnlySpan<Rgb> hostPalette)
    {
        Span<byte> header = stackalloc byte[ThumbnailHeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(header, ThumbnailTag);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)(ThumbnailHeaderSize + ThumbnailWidth * ThumbnailHeight * 2));
        header[8] = 2; // THMB_VERSION
        BinaryPrimitives.WriteUInt16BigEndian(header[9..], ThumbnailWidth);
        BinaryPrimitives.WriteUInt16BigEndian(header[11..], ThumbnailHeight);
        // PixelFormat(2, 5, 6, 5, 0, 11, 5, 0, 0): bytes per pixel, losses, shifts
        header[13] = 2;
        header[14] = 3;
        header[15] = 2;
        header[16] = 3;
        header[17] = 8;
        header[18] = 11;
        header[19] = 5;
        header[20] = 0;
        header[21] = 0;
        stream.Write(header);

        var palette = hostPalette.ToArray();
        int cellWidth = Math.Max(1, display.Width / ThumbnailWidth);
        int cellHeight = Math.Max(1, display.Height / ThumbnailHeight);
        var row = new byte[ThumbnailWidth * 2];
        for (int ty = 0; ty < ThumbnailHeight; ty++)
        {
            for (int tx = 0; tx < ThumbnailWidth; tx++)
            {
                int r = 0, g = 0, b = 0, n = 0;
                for (int dy = 0; dy < cellHeight; dy++)
                {
                    int y = ty * cellHeight + dy;
                    if (y >= display.Height)
                    {
                        break;
                    }

                    for (int dx = 0; dx < cellWidth; dx++)
                    {
                        int x = tx * cellWidth + dx;
                        if (x >= display.Width)
                        {
                            break;
                        }

                        var color = palette[display.Pixels[y * display.Width + x] % palette.Length];
                        r += color.R;
                        g += color.G;
                        b += color.B;
                        n++;
                    }
                }

                ushort rgb565 = 0;
                if (n > 0)
                {
                    rgb565 = (ushort)(((r / n) >> 3 << 11) | ((g / n) >> 2 << 5) | ((b / n) >> 3));
                }

                BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(tx * 2), rgb565);
            }

            stream.Write(row, 0, row.Length);
        }
    }

    private static void SkipThumbnail(Stream stream)
    {
        long position = stream.Position;
        Span<byte> head = stackalloc byte[8];
        if (stream.ReadAtLeast(head, 8, throwOnEndOfStream: false) < 8)
        {
            stream.Position = position;
            return;
        }

        uint tag = BinaryPrimitives.ReadUInt32BigEndian(head);
        if (tag != ThumbnailTag && tag != ThumbnailTagBad)
        {
            stream.Position = position;
            return;
        }

        uint size = BinaryPrimitives.ReadUInt32BigEndian(head[4..]);
        stream.Position = position + size;
    }

    // ---- the engine state ----

    /// <summary>Port of EngineState::saveLoadWithSerializer for SCI16.</summary>
    private static void SyncEngineState(SaveSerializer s, EngineState state)
    {
        s.SyncString(string.Empty, 14, 23); // OBSOLETE: used to be gameVersion

        if (state.Gfx32 is null)
        {
            // the picture port, which Castle of Dr. Brain changes during its intro
            var picPortRect = state.Ports.KernelGetPicWindow(out int picPortTop, out int picPortLeft);
            picPortRect.Top = s.SyncInt16((short)picPortRect.Top);
            picPortRect.Left = s.SyncInt16((short)picPortRect.Left);
            picPortRect.Bottom = s.SyncInt16((short)picPortRect.Bottom);
            picPortRect.Right = s.SyncInt16((short)picPortRect.Right);
            picPortTop = s.SyncInt16((short)picPortTop);
            picPortLeft = s.SyncInt16((short)picPortLeft);
            if (s.IsLoading)
            {
                state.Ports.KernelSetPicWindow(picPortRect, picPortTop, picPortLeft, false);
            }
        }

        state.Gfx32?.Video?.BeforeSaveLoad(s, state.Version);
        state.SegMan.SaveLoad(s);
        state.Sound.SyncPlayList(s);
        if (state.Gfx32 is { } gfx32)
        {
            gfx32.Palette.SaveLoad(s);
            gfx32.Remap.SaveLoad(s);
            gfx32.Cursor.SaveLoad(s);
            state.Audio32?.SaveLoad(s);
            gfx32.Video?.SaveLoad(s, state.Version);
        }
        else
        {
            state.Palette.SaveLoad(s);
        }

        // loading is not normally possible while audio plays in SCI games, but a restore can
        // come at any moment here
        if (s.IsLoading)
        {
            if (state.Audio32 is { } audio32)
            {
                audio32.Stop(Sound.Audio32.AllChannels);
            }
            else
            {
                state.Audio.StopAudio();
            }
        }
    }

    /// <summary>The game version from global 27 (or 28 in earlier games), or the VERSION file. Port of getGameVersionFromGlobal.</summary>
    public static string GetGameVersion(EngineState s)
    {
        var versionRef = s.GetGlobal(GlobalVar.VersionNew);
        if (versionRef.IsNumber)
        {
            versionRef = s.GetGlobal(GlobalVar.VersionOld);
        }

        // LSL7 and Phantasmagoria 2 keep the version in a Str object
        if (s.SegMan.IsObject(versionRef))
        {
            versionRef = Selectors.ReadSelector(s, versionRef, s.Kernel.Selectors.Data);
        }

        if (versionRef.IsPointer)
        {
            string fromGlobal = s.SegMan.GetString(versionRef);
            if (fromGlobal.Length > 0)
            {
                return fromGlobal;
            }
        }

        if (s.ResMan.Game.Exists("VERSION"))
        {
            using var reader = new StreamReader(s.ResMan.Game.Open("VERSION"));
            return reader.ReadLine() ?? string.Empty;
        }

        return string.Empty;
    }

    // ---- saving ----

    /// <summary>Writes the whole game state. Port of gamestate_save.</summary>
    public static void Save(EngineState s, Stream stream, string saveName, string version)
    {
        if (version.Length == 0)
        {
            version = GetGameVersion(s);
        }

        var ser = SaveSerializer.ForSaving(stream);
        var now = s.ClockStart.AddMilliseconds(s.Millis());
        var script0 = s.ResMan.FindResource(new ResourceId(ResourceType.Script, 0)) ?? throw new SciVmException("script 0 is missing");
        var meta = new SavegameMetadata
        {
            Version = CurrentVersion,
            Name = saveName,
            GameVersion = version,
            SaveDate = ((now.Day & 0xFF) << 24) | ((now.Month & 0xFF) << 16) | (now.Year & 0xFFFF),
            SaveTime = ((now.Hour & 0xFF) << 16) | ((now.Minute & 0xFF) << 8) | (now.Second & 0xFF),
            Script0Size = (ushort)script0.Size,
            GameObjectOffset = (ushort)s.GameObjectAddress.Offset,
        };
        SyncMetadata(ser, meta, s);
        if (s.Gfx32 is { } gfx32)
        {
            WriteThumbnail(stream, gfx32.Frameout.Screen, gfx32.Palette.HardwarePalette);
        }
        else
        {
            WriteThumbnail(stream, s.Screen.Display, s.Screen.HostPalette);
        }

        SyncEngineState(ser, s);
        s.Ports?.SaveLoad(ser);
        s.Vocabulary?.SaveLoad(ser);
        // SSCI (at least Jones CD) also stores the menu state
    }

    /// <summary>Saves to a slot of the host's save store. False when the store refuses or the state cannot be written.</summary>
    public static bool Save(EngineState s, int saveId, string saveName, string version)
    {
        if (s.Saves is null)
        {
            s.Log.Warning("sci.save", "No save store, cannot save");
            return false;
        }

        try
        {
            using var stream = s.Saves.OpenWrite(SaveName(s, saveId));
            Save(s, stream, saveName, version);
            return true;
        }
        catch (Exception ex) when (ex is IOException or SciVmException or UnauthorizedAccessException)
        {
            s.Log.Warning("sci.save", $"Saving the game failed: {ex.Message}");
            return false;
        }
    }

    // ---- restoring ----

    /// <summary>
    /// Restores the whole game state from a stream. On success the VM is left with the load
    /// abort set so the game loop calls the game's replay method. Port of gamestate_restore.
    /// </summary>
    public static bool Restore(EngineState s, Stream stream)
    {
        var ser = SaveSerializer.ForLoading(stream);
        var meta = new SavegameMetadata();
        SyncMetadata(ser, meta, null);
        if (ser.Eos)
        {
            return false;
        }

        if (meta.Version < MinimumVersion || meta.Version > CurrentVersion)
        {
            s.Log.Warning("sci.save", meta.Version < MinimumVersion
                ? "The format of this saved game is obsolete, unable to load it"
                : $"Savegame version is {meta.Version}, maximum supported is {CurrentVersion}");
            return false;
        }

        if (meta.GameObjectOffset > 0 && meta.Script0Size > 0)
        {
            var script0 = s.ResMan.FindResource(new ResourceId(ResourceType.Script, 0));
            if (script0 is null || script0.Size != meta.Script0Size || s.GameObjectAddress.Offset != meta.GameObjectOffset)
            {
                s.Log.Warning("sci.save", "This saved game was created with a different version of the game, unable to load it");
                return false;
            }
        }

        SkipThumbnail(stream);

        // the ports go first, since that frees hunk memory that must not survive the restore
        s.Ports?.Reset();

        // the screen is cleared for SCI16, so leftovers such as KQ6's hires portraits vanish;
        // SCI32 neither cleared nor redrew, it only drew what changed
        if (s.Gfx32 is null)
        {
            s.Screen.ClearForRestoreGame();
        }

        s.Reset(true);
        SyncEngineState(ser, s);

        s.SegMan.ReconstructStack(s);
        s.SegMan.ReconstructClones();
        s.InitGlobals();
        s.GcCountDown = EngineState.GcInterval - 1;

        // time state
        s.LastWaitTime = s.Millis();
        s.ScreenUpdateTime = s.Millis();

        s.Ports?.SaveLoad(ser);
        if (ser.Version >= 30)
        {
            s.Vocabulary?.SaveLoad(ser);
        }

        s.Sound.ReconstructPlayList();

        s.Message.Reset();
        s.SegMan.InitSysStrings();

        s.AbortScriptProcessing = AbortGameState.LoadGame;
        s.GameIsRestarting = GameIsRestartingFlags.Restore; // signals the restored game to the scripts
        return true;
    }

    /// <summary>Restores a slot of the host's save store, with the fix-ups some games need afterwards.</summary>
    public static bool Restore(EngineState s, int saveId)
    {
        if (s.Saves is null)
        {
            return false;
        }

        using var stream = s.Saves.OpenRead(SaveName(s, saveId));
        if (stream is null)
        {
            s.Log.Warning("sci.save", $"Savegame #{saveId} not found");
            return false;
        }

        if (!Restore(s, stream))
        {
            return false;
        }

        AfterRestoreFixUp(s, saveId);
        s.LastRestoredSaveId = saveId;
        return true;
    }

    /// <summary>The per-game adjustments after a restore. Port of gamestate_afterRestoreFixUp for SCI16.</summary>
    private static void AfterRestoreFixUp(EngineState s, int savegameId)
    {
        var sel = s.Kernel.Selectors;
        switch (s.GameId)
        {
            case SciGameId.Camelot:
            {
                // WORKAROUND: Camelot depends on its menu state persisting: the items decide when the
                // player can draw or sheathe the sword and open a purse, so they are recreated from
                // the game state. Open Purse: a purse in the inventory. Draw Sword: flag 3, unless a
                // room disables it (under the aqueduct in room 50, sitting with the scholar in 82).
                var enablePurse = RegT.Null;
                var ego = s.GetGlobal(GlobalVar.Ego);
                foreach (var purse in s.SegMan.FindObjectsByName("purse"))
                {
                    if (Selectors.ReadSelector(s, purse, sel.Owner) == ego)
                    {
                        enablePurse = RegT.True;
                        break;
                    }
                }

                s.Menu.KernelSetAttribute(1281 >> 8, 1281 & 0xFF, GfxMenu.AttributeEnabled, enablePurse);

                bool hasSword = (s.GetGlobal(250).Offset & 0x1000) != 0;
                bool underAqueduct = s.GetGlobal(11).Offset == 50;
                bool sittingWithScholar = Selectors.ReadSelectorValue(s, ego, sel.View) == 84;
                var enableSword = hasSword && !underAqueduct && !sittingWithScholar ? RegT.True : RegT.Null;
                s.Menu.KernelSetAttribute(1283 >> 8, 1283 & 0xFF, GfxMenu.AttributeEnabled, enableSword);
                break;
            }

            case SciGameId.Mothergoose:
                // WORKAROUND: rm200::newRoom sets global C5h to the number of children plus one
                // right after creating a child, so the saved game id goes in directly
                s.SetGlobal(0xC5, RegT.Make(0, OfficialRangeStart + savegameId));
                break;
            case SciGameId.Mothergoose256:
                // WORKAROUND: the SCI1/SCI1.1 versions keep the current saved game id in a global
                s.SetGlobal(0xB3, RegT.Make(0, OfficialRangeStart + savegameId));
                break;
            case SciGameId.Jones:
                // HACK: the menu items enabled by room1::init are not enabled when restoring from
                // the game's main menu (bugs #6537 and #6723)
                s.Menu.KernelSetAttribute(257 >> 8, 257 & 0xFF, GfxMenu.AttributeEnabled, RegT.True); // Sierra -> About Jones
                s.Menu.KernelSetAttribute(258 >> 8, 258 & 0xFF, GfxMenu.AttributeEnabled, RegT.True); // Sierra -> Help
                s.Menu.KernelSetAttribute(769 >> 8, 769 & 0xFF, GfxMenu.AttributeEnabled, RegT.True); // Options -> Delete current player
                s.Menu.KernelSetAttribute(513 >> 8, 513 & 0xFF, GfxMenu.AttributeEnabled, RegT.True); // Game -> Save Game
                s.Menu.KernelSetAttribute(515 >> 8, 515 & 0xFF, GfxMenu.AttributeEnabled, RegT.True); // Game -> Restore Game
                s.Menu.KernelSetAttribute(1025 >> 8, 1025 & 0xFF, GfxMenu.AttributeEnabled, RegT.True); // Status -> Statistics
                s.Menu.KernelSetAttribute(1026 >> 8, 1026 & 0xFF, GfxMenu.AttributeEnabled, RegT.True); // Status -> Goals
                break;
            case SciGameId.Kq5:
                // WORKAROUND: the KQ5 CD Windows cursors are not used, so every KQCursor forgets
                // its number and initializes with the DOS view cursors again
                if (s.IsCd)
                {
                    foreach (var cursor in FindObjectsBySuperClass(s, "KQCursor"))
                    {
                        Selectors.WriteSelector(s, cursor, sel.Number, RegT.Null);
                    }
                }

                break;
            case SciGameId.Kq6:
                if (s.IsCd)
                {
                    // WORKAROUND: the CD version keeps a global for hires mode, which only the
                    // Windows interpreter had; the DOS interpreter is lowres
                    s.SetGlobal(0xA9, RegT.Make(0, s.Platform == SciPlatform.Windows ? 1 : 0));
                }

                break;
            case SciGameId.Pq2:
                // HACK: the save menu option is disabled in the death screen and must come back (bug #6875)
                s.Menu.KernelSetAttribute(2, 1, GfxMenu.AttributeEnabled, RegT.True); // Game -> Save Game
                break;
        }
    }

    /// <summary>Every object whose class chain includes a class of the given name.</summary>
    private static List<RegT> FindObjectsBySuperClass(EngineState s, string superClassName)
    {
        var result = new List<RegT>();
        var segments = s.SegMan.Segments;
        for (int seg = 1; seg < segments.Count; seg++)
        {
            if (segments[seg] is not Script script)
            {
                continue;
            }

            foreach (var obj in script.Objects.Values)
            {
                var superClass = obj.SuperClassSelector;
                int depth = 0;
                while (!superClass.IsNull && depth++ < 64)
                {
                    var superObj = s.SegMan.GetObject(superClass);
                    if (superObj is null)
                    {
                        break;
                    }

                    if (s.SegMan.GetObjectName(superClass) == superClassName)
                    {
                        result.Add(obj.Pos);
                        break;
                    }

                    superClass = superObj.SuperClassSelector;
                }
            }
        }

        return result;
    }

    /// <summary>Performs a restore requested from outside the scripts, at a safe point. Port of gamestate_delayedrestore.</summary>
    public static void DelayedRestore(EngineState s)
    {
        if (s.DelayedRestoreGameId == -1)
        {
            return;
        }

        int saveId = s.DelayedRestoreGameId;
        s.DelayedRestoreGameId = -1;
        if (!Restore(s, saveId))
        {
            s.Log.Warning("sci.save", $"Delayed restore of slot {saveId} failed");
        }
    }

    // ---- listing ----

    /// <summary>The saved games of the current game, newest first. Port of listSavegames.</summary>
    public static List<SavegameDesc> ListSavegames(EngineState s)
    {
        var saves = new List<SavegameDesc>();
        if (s.Saves is null)
        {
            return saves;
        }

        foreach (string name in s.Saves.List(s.SaveNamePrefix))
        {
            string suffix = name[s.SaveNamePrefix.Length..];
            if (suffix.Length != 3 || !int.TryParse(suffix, out int id))
            {
                continue;
            }

            // the new game and autosave slots are left out, except for QFG3 and QFG4 whose
            // autosave is a normal saved game
            if (s.GameId != SciGameId.Qfg3 && s.GameId != SciGameId.Qfg4 && (id == NewGameId || id == AutoSaveId))
            {
                continue;
            }

            using var stream = s.Saves.OpenRead(name);
            if (stream is null || !GetMetadata(stream, out var meta) || meta.Name.Length == 0)
            {
                continue;
            }

            saves.Add(new SavegameDesc
            {
                Id = id,
                // the date is stored as DDMMYYYY, so it is reordered for sorting
                Date = (uint)(((meta.SaveDate & 0xFFFF) << 16) | ((meta.SaveDate & 0xFF0000) >> 8) | ((meta.SaveDate >> 24) & 0xFF)),
                Time = meta.SaveTime,
                Version = meta.Version,
                Name = meta.Name,
                GameVersion = meta.GameVersion,
                Script0Size = meta.Script0Size,
                GameObjectOffset = meta.GameObjectOffset,
            });
        }

        saves.Sort((l, r) => l.Date != r.Date ? r.Date.CompareTo(l.Date) : r.Time.CompareTo(l.Time));
        return saves;
    }

    public static int FindSavegame(List<SavegameDesc> saves, int savegameId) => saves.FindIndex(d => d.Id == savegameId);
}
