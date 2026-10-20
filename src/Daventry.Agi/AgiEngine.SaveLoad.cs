using System.Buffers.Binary;
using System.Text;
using Daventry.Agi.Resources;
using Daventry.Core.Hosting;

namespace Daventry.Agi;

/// <summary>
/// Saving and restoring. Port of ScummVM's saveload.cpp, writing the same file layout (version 11,
/// including the thumbnail block) so saves can be exchanged with ScummVM. Files live in the host's
/// <see cref="Core.Storage.ISaveStore"/> under "&lt;gameid&gt;.NNN", slot 0 being the autosave.
/// </summary>
public sealed partial class AgiEngine
{
    public const int SaveGameCurrentVersion = 11;
    public const int SaveGameMaximumSlots = 100;

    private const uint AgiSaveTag = 0x4147493A; // "AGI:"
    private const uint ThumbnailTag = 0x54484D42; // "THMB"
    private const uint ThumbnailTagBad = 0x424D4854; // "BMHT", written by old ScummVM builds
    private const int ThumbnailWidth = 160;
    private const int ThumbnailHeight = 100;
    private const int ThumbnailHeaderSize = 4 + 4 + 1 + 2 + 2 + (1 + 4 + 4);
    private const int DescriptionFieldLength = AgiGameState.SavedGameDescriptionLength + 1;

    public string GetSaveStateName(int slot) => $"{Description.GameId}.{slot:000}";

    /// <summary>Slot numbers that have a file, in ascending order.</summary>
    public List<int> GetSavegameSlotIds()
    {
        string prefix = Description.GameId + ".";
        var slots = new List<int>();
        foreach (var name in Host.Saves.List(prefix))
        {
            string suffix = name[prefix.Length..];
            if (suffix.Length == 3 && int.TryParse(suffix, out int slot))
            {
                slots.Add(slot);
            }
        }

        slots.Sort();
        return slots;
    }

    /// <summary>
    /// Reads the header of a saved game. Returns false when the slot has no file. Date is
    /// (year &lt;&lt; 16) | (month &lt;&lt; 8) | day and time is (hour &lt;&lt; 16) | (minute &lt;&lt; 8) | second.
    /// </summary>
    public bool GetSavegameInformation(int slot, out string description, out uint saveDate, out uint saveTime, out bool saveIsValid)
    {
        description = string.Empty;
        saveDate = 0;
        saveTime = 0;
        saveIsValid = false;

        using var stream = Host.Saves.OpenRead(GetSaveStateName(slot));
        if (stream is null)
        {
            return false;
        }

        var reader = new SaveReader(stream);
        try
        {
            if (reader.ReadUInt32() != AgiSaveTag)
            {
                Host.Log.Warning("agi.save", "This doesn't appear to be an AGI savegame");
                description = "[not an AGI save]";
                return true;
            }

            string? text = reader.ReadFixedString(DescriptionFieldLength);
            if (text is null)
            {
                Host.Log.Warning("agi.save", "corrupted description");
                description = "[invalid save]";
                return true;
            }

            byte saveVersion = reader.ReadByte();
            if (saveVersion > SaveGameCurrentVersion)
            {
                Host.Log.Warning("agi.save", "save from a newer version, not supported");
                description = "[not supported]";
                return true;
            }

            if (saveVersion >= 4)
            {
                reader.SkipThumbnail();
                uint rawDate = reader.ReadUInt32();
                saveTime = (uint)reader.ReadUInt16() << 8;
                if (saveVersion >= 9)
                {
                    saveTime |= reader.ReadByte();
                }

                // the file stores DDMMYYYY; callers want a sortable value
                uint day = rawDate >> 24;
                uint month = (rawDate >> 16) & 0xFF;
                uint year = rawDate & 0xFFFF;
                saveDate = (year << 16) | (month << 8) | day;
            }

            description = text;
            saveIsValid = true;
            return true;
        }
        catch (EndOfStreamException)
        {
            Host.Log.Warning("agi.save", "unexpected EOF");
            description = "[invalid save]";
            return true;
        }
    }

    /// <summary>Writes the current state to a slot. Port of AgiEngine::saveGame.</summary>
    public bool SaveGame(int slot, string descriptionString)
    {
        string name = GetSaveStateName(slot);
        Host.Log.Debug("agi.save", $"saveGame({name}, {descriptionString})");

        // Make sure all graphics was blitted to screen (bug #4790, ego partly erased in thumbnails)
        Gfx.UpdateScreen();

        try
        {
            using var stream = Host.Saves.OpenWrite(name);
            var w = new SaveWriter(stream);

            w.WriteUInt32(AgiSaveTag);
            w.WriteFixedString(descriptionString, DescriptionFieldLength);
            w.WriteByte(SaveGameCurrentVersion);

            WriteThumbnail(w);

            var now = DateTime.Now;
            uint saveDate = ((uint)now.Day << 24) | ((uint)now.Month << 16) | (uint)now.Year;
            ushort saveTime = (ushort)((now.Hour << 8) | now.Minute);
            w.WriteUInt32(saveDate);
            w.WriteUInt16(saveTime);
            w.WriteByte((byte)now.Second);
            w.WriteUInt32(InGameTimerGet() / 1000);

            w.WriteByte(2); // was _game.state, 2 = STATE_RUNNING
            w.WriteFixedString(State.Id, 8);

            // MD5 of the game, all zero for fallback matched games as in ScummVM
            string md5 = Description.Md5 ?? new string('\0', 32);
            for (int i = 0; i < 32; i++)
            {
                w.WriteByte(i < md5.Length ? (byte)md5[i] : (byte)0);
            }

            w.WriteByte(State.AutomaticSave ? (byte)1 : (byte)0);
            w.WriteFixedString(State.AutomaticSaveDescription, DescriptionFieldLength);

            // touch the seconds variable so that it gets updated
            GetVar(AgiVar.Seconds);

            for (int i = 0; i < AgiGameState.MaxFlags; i++)
            {
                w.WriteByte(State.Flags[i] ? (byte)1 : (byte)0);
            }

            for (int i = 0; i < AgiGameState.MaxVars; i++)
            {
                w.WriteByte(State.Vars[i]);
            }

            w.WriteInt16((short)State.Horizon);
            w.WriteInt16((short)Text.StatusRow);
            w.WriteInt16((short)Text.PromptRow_Get());
            w.WriteInt16((short)Text.WindowRowMin);

            w.WriteInt16(1); // was _game.inputMode, INPUTMODE_NORMAL
            w.WriteInt16((short)State.CurLogicNr);

            w.WriteInt16((short)(State.PlayerControl ? 1 : 0));
            w.WriteInt16((short)(ShouldQuit ? 1 : 0));
            w.WriteInt16((short)(Text.StatusEnabled ? 0x7FFF : 0));
            w.WriteInt16(1); // was clock enabled
            w.WriteInt16((short)(State.ExitAllLogics ? 1 : 0));
            w.WriteInt16((short)(State.PictureShown ? 1 : 0));
            w.WriteInt16((short)(Text.PromptIsEnabled() ? 1 : 0)); // was _game.hasPrompt
            w.WriteInt16(0); // was _game.gameFlags
            w.WriteInt16((short)(Text.PromptIsEnabled() ? 0x7FFF : 0));

            for (int i = 0; i < Graphics.GfxMgr.ScriptHeight; i++)
            {
                w.WriteByte((byte)Gfx.SaveLoadGetPriority(i));
            }

            w.WriteInt16((short)(Gfx.SaveLoadWasPriorityTableModified ? 1 : 0));

            w.WriteInt16((short)(State.GfxMode ? 1 : 0));
            w.WriteByte(Text.InputGetCursorChar());
            w.WriteInt16(Text.TextAttrib.Foreground);
            w.WriteInt16(Text.TextAttrib.Background);

            int objectCount = Resources.Objects.Count;
            w.WriteInt16((short)objectCount);
            for (int i = 0; i < objectCount; i++)
            {
                w.WriteInt16((short)ObjectGetLocation(i));
            }

            foreach (var mapping in State.ControllerKeyMapping)
            {
                w.WriteUInt16(mapping.KeyCode);
                w.WriteByte((byte)mapping.ControllerSlot);
            }

            w.WriteByte(KeyHoldMode ? (byte)1 : (byte)0);

            for (int i = 0; i < AgiGameState.MaxStrings; i++)
            {
                w.WriteFixedString(State.GetString(i), AgiGameState.MaxStringLength);
            }

            var directory = Resources.Directory;
            for (int i = 0; i < AgiDirectory.MaxEntries; i++)
            {
                w.WriteByte((byte)directory.Logic[i].Flags);
                var logic = Resources.Logics[i];
                w.WriteInt16((short)(logic?.SIP ?? 0));
                w.WriteInt16((short)(logic?.CIP ?? 0));
            }

            for (int i = 0; i < AgiDirectory.MaxEntries; i++)
            {
                w.WriteByte((byte)directory.Picture[i].Flags);
            }

            for (int i = 0; i < AgiDirectory.MaxEntries; i++)
            {
                w.WriteByte((byte)directory.View[i].Flags);
            }

            for (int i = 0; i < AgiDirectory.MaxEntries; i++)
            {
                w.WriteByte((byte)directory.Sound[i].Flags);
            }

            foreach (var screenObj in State.ScreenObjTable)
            {
                w.WriteByte(screenObj.StepTime);
                w.WriteByte(screenObj.StepTimeCount);
                w.WriteByte((byte)screenObj.ObjectNr);
                w.WriteInt16((short)screenObj.XPos);
                w.WriteInt16((short)screenObj.YPos);
                w.WriteByte(screenObj.CurrentViewNr);
                w.WriteByte(screenObj.CurrentLoopNr);
                w.WriteByte(screenObj.LoopCount);
                w.WriteByte(screenObj.CurrentCelNr);
                w.WriteByte(screenObj.CelCount);
                w.WriteInt16((short)screenObj.XPosPrev);
                w.WriteInt16((short)screenObj.YPosPrev);
                w.WriteInt16((short)screenObj.XSize);
                w.WriteInt16((short)screenObj.YSize);
                w.WriteByte(screenObj.StepSize);
                w.WriteByte(screenObj.CycleTime);
                w.WriteByte(screenObj.CycleTimeCount);
                w.WriteByte(screenObj.Direction);
                w.WriteByte((byte)screenObj.MotionType);
                w.WriteByte((byte)screenObj.Cycle);
                w.WriteByte(screenObj.LoopFlag); // version 11+
                w.WriteByte(screenObj.Priority);
                w.WriteUInt16((ushort)screenObj.Flags);

                // four motion bytes, laid out per motion type for compatibility
                switch (screenObj.MotionType)
                {
                    case MotionType.Normal:
                        w.WriteBytes(0, 0, 0, 0);
                        break;
                    case MotionType.Wander:
                        w.WriteBytes(screenObj.WanderCount, 0, 0, 0);
                        break;
                    case MotionType.FollowEgo:
                        w.WriteBytes(screenObj.FollowStepSize, screenObj.FollowFlag, screenObj.FollowCount, 0);
                        break;
                    case MotionType.Ego:
                    case MotionType.MoveObj:
                        w.WriteBytes((byte)screenObj.MoveX, (byte)screenObj.MoveY, screenObj.MoveStepSize, screenObj.MoveFlag);
                        break;
                    default:
                        throw new AgiResourceException($"unknown motion type {screenObj.MotionType}");
                }
            }

            foreach (var element in State.ImageStack)
            {
                w.WriteByte((byte)element.Type);
                w.WriteInt16((short)element.Parameter1);
                w.WriteInt16((short)element.Parameter2);
                w.WriteInt16((short)element.Parameter3);
                w.WriteInt16((short)element.Parameter4);
                w.WriteInt16((short)element.Parameter5);
                w.WriteInt16((short)element.Parameter6);
                w.WriteInt16((short)element.Parameter7);
            }

            w.WriteByte(0);

            w.WriteInt16((short)Gfx.AgiPalFileNr);
            Host.Log.Info("agi.save", $"Saved game '{descriptionString}' in {name}");
            return true;
        }
        catch (IOException ex)
        {
            Host.Log.Warning("agi.save", $"Can't write '{name}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Restores a slot. Port of AgiEngine::loadGame. Returns false when the file is missing or unusable.</summary>
    public bool LoadGame(int slot, bool checkId = true)
    {
        string name = GetSaveStateName(slot);
        Host.Log.Debug("agi.save", $"loadGame({name})");

        using var stream = Host.Saves.OpenRead(name);
        if (stream is null)
        {
            Host.Log.Warning("agi.save", $"Can't open '{name}', game not loaded");
            return false;
        }

        var r = new SaveReader(stream);
        if (r.ReadUInt32() != AgiSaveTag)
        {
            Host.Log.Warning("agi.save", "This doesn't appear to be an AGI savegame, game not restored");
            return false;
        }

        string description = r.ReadFixedString(DescriptionFieldLength) ?? throw new AgiResourceException("saved game description is corrupt");
        Host.Log.Debug("agi.save", $"Description is: {description}");

        byte saveVersion = r.ReadByte();
        if (saveVersion < 2)
        {
            Host.Log.Warning("agi.save", $"Old save game version ({saveVersion}, current version is {SaveGameCurrentVersion}). Will try and read anyway");
        }

        if (saveVersion < 3)
        {
            Host.Log.Warning("agi.save", "This save game contains no AGIPAL data, if the game is using the AGIPAL hack, it won't work correctly");
        }

        if (saveVersion > SaveGameCurrentVersion)
        {
            throw new AgiResourceException("Saved game was created with a newer version. Unable to load.");
        }

        bool totalPlayTimeWasSet = false;
        if (saveVersion >= 4)
        {
            r.SkipThumbnail();
            r.ReadUInt32(); // save date
            r.ReadUInt16(); // save time
            if (saveVersion >= 9)
            {
                r.ReadByte(); // seconds
            }

            if (saveVersion >= 6)
            {
                uint playTime = r.ReadUInt32();
                InGameTimerReset(playTime * 1000);
                totalPlayTimeWasSet = true;
            }
        }

        r.ReadByte(); // was _game.state

        string loadId = r.ReadFixedString(8) ?? string.Empty;
        if (checkId && loadId != State.Id)
        {
            Host.Log.Warning("agi.save", $"This save seems to be from a different AGI game (save from {loadId}, running {State.Id}), not loaded");
            return false;
        }

        State.Id = loadId;

        if (saveVersion >= 5)
        {
            var md5Bytes = r.ReadBytes(32);
            string md5 = md5Bytes[0] == 0 ? "fallback matched" : Encoding.ASCII.GetString(md5Bytes);
            Host.Log.Debug("agi.save", $"Saved game MD5: \"{md5}\"");
            if (Description.Md5 is null)
            {
                Host.Log.Warning("agi.save", "Since your game was only detected via the fallback detector, there is no possibility to assure the save is compatible with your game version");
            }
            else if (!string.Equals(md5, Description.Md5, StringComparison.OrdinalIgnoreCase))
            {
                Host.Log.Warning("agi.save", $"Game was saved with different gamedata - you may encounter problems (yours {Description.Md5}, save {md5})");
            }
        }

        if (saveVersion >= 7)
        {
            State.AutomaticSave = r.ReadByte() != 0;
            State.AutomaticSaveDescription = r.ReadFixedString(DescriptionFieldLength) ?? string.Empty;
        }
        else
        {
            State.AutomaticSave = false;
            State.AutomaticSaveDescription = string.Empty;
        }

        for (int i = 0; i < AgiGameState.MaxFlags; i++)
        {
            State.Flags[i] = r.ReadByte() != 0;
        }

        for (int i = 0; i < AgiGameState.MaxVars; i++)
        {
            State.Vars[i] = r.ReadByte();
        }

        if (!totalPlayTimeWasSet)
        {
            // Saves before version 6: rebuild the play time from the clock variables
            uint playTime = (uint)(State.Vars[AgiVar.Seconds] + State.Vars[AgiVar.Minutes] * 60 + State.Vars[AgiVar.Hours] * 3600 + State.Vars[AgiVar.Days] * 86400) * 1000;
            InGameTimerReset(playTime);
        }

        SetVar(AgiVar.FreePages, 180); // realistic value, overwriting the loaded one

        State.Horizon = r.ReadInt16();
        Text.StatusRow_Set(r.ReadInt16());
        Text.PromptRow_Set(r.ReadInt16());
        Text.ConfigureScreen(r.ReadInt16());

        // never saved
        Text.PromptReset();

        r.ReadInt16(); // was _game.inputMode

        State.CurLogicNr = r.ReadInt16();

        State.PlayerControl = r.ReadInt16() != 0;
        if (r.ReadInt16() != 0)
        {
            QuitGame();
        }

        if (r.ReadInt16() != 0)
        {
            Text.StatusEnable();
        }
        else
        {
            Text.StatusDisable();
        }

        r.ReadInt16(); // was clock enabled
        State.ExitAllLogics = r.ReadInt16() != 0;
        r.ReadInt16(); // was _game.pictureShown
        r.ReadInt16(); // was _game.hasPrompt
        r.ReadInt16(); // was _game.gameFlags
        if (r.ReadInt16() != 0)
        {
            Text.PromptEnable();
        }
        else
        {
            Text.PromptDisable();
        }

        for (int i = 0; i < Graphics.GfxMgr.ScriptHeight; i++)
        {
            Gfx.SaveLoadSetPriority(i, r.ReadByte());
        }

        if (saveVersion >= 10)
        {
            Gfx.SaveLoadSetPriorityTableModified(r.ReadInt16() != 0);
        }
        else
        {
            Gfx.SaveLoadFigureOutPriorityTableModified();
        }

        Text.CloseWindow();
        State.Block.Active = false;

        State.GfxMode = r.ReadInt16() != 0;
        Text.InputSetCursorChar((char)r.ReadByte());

        short textForeground = r.ReadInt16();
        short textBackground = r.ReadInt16();
        Text.CharAttrib_Set((byte)textForeground, (byte)textBackground);

        int objectCount = r.ReadInt16();
        for (int i = 0; i < objectCount; i++)
        {
            ObjectSetLocation(i, r.ReadInt16());
        }

        Array.Clear(State.ControllerOccurred);

        if (saveVersion >= 7)
        {
            for (int i = 0; i < State.ControllerKeyMapping.Length; i++)
            {
                State.ControllerKeyMapping[i].KeyCode = r.ReadUInt16();
                State.ControllerKeyMapping[i].ControllerSlot = r.ReadByte();
            }
        }

        if (saveVersion >= 8)
        {
            KeyHoldMode = r.ReadByte() != 0;
        }

        for (int i = 0; i < AgiGameState.MaxStrings; i++)
        {
            State.SetString(i, r.ReadFixedString(AgiGameState.MaxStringLength) ?? string.Empty);
        }

        for (int i = 0; i < AgiDirectory.MaxEntries; i++)
        {
            if ((r.ReadByte() & (byte)AgiResourceFlags.Loaded) != 0)
            {
                LoadResource(AgiResourceType.Logic, i);
            }
            else
            {
                UnloadResource(AgiResourceType.Logic, i);
            }

            short sip = r.ReadInt16();
            short cip = r.ReadInt16();
            if (Resources.Logics[i] is { } logic)
            {
                logic.SIP = sip;
                logic.CIP = cip;
            }
        }

        LoadOrUnloadFromFlags(r, AgiResourceType.Picture);
        LoadOrUnloadFromFlags(r, AgiResourceType.View);
        LoadOrUnloadFromFlags(r, AgiResourceType.Sound);

        foreach (var screenObj in State.ScreenObjTable)
        {
            screenObj.StepTime = r.ReadByte();
            screenObj.StepTimeCount = r.ReadByte();
            screenObj.ObjectNr = r.ReadByte();
            screenObj.XPos = r.ReadInt16();
            screenObj.YPos = r.ReadInt16();
            screenObj.CurrentViewNr = r.ReadByte();
            screenObj.CurrentLoopNr = r.ReadByte();
            screenObj.LoopCount = r.ReadByte();
            screenObj.CurrentCelNr = r.ReadByte();
            screenObj.CelCount = r.ReadByte();
            screenObj.XPosPrev = r.ReadInt16();
            screenObj.YPosPrev = r.ReadInt16();
            screenObj.XSize = r.ReadInt16();
            screenObj.YSize = r.ReadInt16();
            screenObj.StepSize = r.ReadByte();
            screenObj.CycleTime = r.ReadByte();
            screenObj.CycleTimeCount = r.ReadByte();
            screenObj.Direction = r.ReadByte();
            screenObj.MotionType = (MotionType)r.ReadByte();
            screenObj.Cycle = (CycleType)r.ReadByte();
            if (saveVersion >= 11)
            {
                screenObj.SetLoopFlag(r.ReadByte());
            }

            screenObj.Priority = r.ReadByte();
            screenObj.Flags = (ViewFlags)r.ReadUInt16();

            byte oldLoopFlag;
            switch (screenObj.MotionType)
            {
                case MotionType.Normal:
                    oldLoopFlag = r.ReadByte();
                    r.ReadBytes(3);
                    break;
                case MotionType.Wander:
                    screenObj.WanderCount = r.ReadByte();
                    r.ReadBytes(3);
                    oldLoopFlag = screenObj.WanderCount;
                    break;
                case MotionType.FollowEgo:
                    screenObj.FollowStepSize = r.ReadByte();
                    screenObj.FollowFlag = r.ReadByte();
                    screenObj.FollowCount = r.ReadByte();
                    r.ReadByte();
                    oldLoopFlag = screenObj.FollowStepSize;
                    break;
                case MotionType.Ego:
                case MotionType.MoveObj:
                    screenObj.MoveX = r.ReadByte();
                    screenObj.MoveY = r.ReadByte();
                    screenObj.MoveStepSize = r.ReadByte();
                    screenObj.MoveFlag = r.ReadByte();
                    oldLoopFlag = (byte)screenObj.MoveX;
                    break;
                default:
                    throw new AgiResourceException($"unknown motion type {screenObj.MotionType} in saved game");
            }

            if (saveVersion < 11)
            {
                // before version 7 the loop flag shared vt.parm1; versions 7 to 10 did not save it
                screenObj.SetLoopFlag(saveVersion < 7 ? oldLoopFlag : (byte)0);
            }
        }

        // Fix the view, loop and cel references of the screen objects
        foreach (var screenObj in State.ScreenObjTable)
        {
            if (Resources.Directory.View[screenObj.CurrentViewNr].IsEmpty)
            {
                continue;
            }

            if (!Resources.Directory.View[screenObj.CurrentViewNr].IsLoaded)
            {
                LoadResource(AgiResourceType.View, screenObj.CurrentViewNr);
            }

            SetView(screenObj, screenObj.CurrentViewNr);
            SetLoop(screenObj, screenObj.CurrentLoopNr);
            SetCel(screenObj, screenObj.CurrentCelNr);
        }

        Sprites.EraseSprites();
        State.PictureShown = false;
        Gfx.ClearDisplay(0, false); // clear the display, but do not copy it yet because of the transition

        // Recreate the background from the saved image stack
        State.ClearImageStack();
        byte type;
        while ((type = r.ReadByte()) != 0)
        {
            var parm = new int[7];
            for (int i = 0; i < 7; i++)
            {
                parm[i] = r.ReadInt16();
            }

            ReplayImageStackCall((ImageStackCall)type, parm[0], parm[1], parm[2], parm[3], parm[4], parm[5], parm[6]);
        }

        if (saveVersion >= 3)
        {
            Gfx.SetAgiPal(r.ReadInt16(), Game);
        }

        SetFlag(AgiFlag.RestoreJustRan, true);
        Words.ClearEgoWords();

        // don't delay anything right after restoring a game
        ArtificialDelayReset();

        Sprites.EraseSprites();
        Sprites.BuildAllSpriteLists();
        Sprites.DrawAllSpriteLists();
        Picture.ShowPictureWithTransition();
        State.PictureShown = true;
        Text.StatusDraw();
        Text.PromptRedraw();

        Gfx.CopyDisplayToScreen();
        Sound.ApplyVolume(State.Vars[AgiVar.Volume], false);
        return true;
    }

    private void LoadOrUnloadFromFlags(SaveReader r, AgiResourceType type)
    {
        for (int i = 0; i < AgiDirectory.MaxEntries; i++)
        {
            if ((r.ReadByte() & (byte)AgiResourceFlags.Loaded) != 0)
            {
                LoadResource(type, i);
            }
            else
            {
                UnloadResource(type, i);
            }
        }
    }

    /// <summary>Redraws one recorded picture or add.to.pic call while restoring.</summary>
    public void ReplayImageStackCall(ImageStackCall type, int p1, int p2, int p3, int p4, int p5, int p6, int p7)
    {
        switch (type)
        {
            case ImageStackCall.AddPicture:
                LoadResource(AgiResourceType.Picture, p1);
                Picture.DecodePicture(p1, p2 != 0, p3 != 0);
                break;
            case ImageStackCall.AddView:
                LoadResource(AgiResourceType.View, p1);
                Sprites.AddToPic(p1, p2, p3, p4, p5, p6, p7);
                break;
        }
    }

    /// <summary>Saves to a slot and reports the outcome. Port of doSave.</summary>
    public bool DoSave(int slot, string description) => SaveGame(slot, description);

    /// <summary>Restores a slot and leaves the current logic. Port of doLoad.</summary>
    public async ValueTask<bool> DoLoadAsync(int slot, bool showMessages)
    {
        Sprites.EraseSprites();
        Sound.StopSound();
        Text.CloseWindow();

        bool ok;
        try
        {
            ok = LoadGame(slot);
        }
        catch (Exception ex) when (ex is EndOfStreamException or AgiResourceException)
        {
            Host.Log.Warning("agi.save", $"Error restoring slot {slot}: {ex.Message}");
            ok = false;
        }

        if (ok)
        {
            State.ExitAllLogics = true;
            Menu.ItemEnableAll();
        }
        else if (showMessages)
        {
            await Text.MessageBoxAsync("Error restoring game.");
        }

        return ok;
    }

    /// <summary>Saves under the description given by set.simple, reusing its slot or taking a fresh one.</summary>
    public async ValueTask<bool> SaveGameAutomaticAsync()
    {
        int slot = SystemUI.FigureOutAutomaticSaveGameSlot(State.AutomaticSaveDescription);
        if (slot < 0)
        {
            return false;
        }

        // Remove a window in case one is currently shown, otherwise it would end up in the thumbnail
        // (Mixed Up Mother Goose; the scripts close the window after saving).
        Text.CloseWindow();
        await ValueTask.CompletedTask;
        return DoSave(slot, State.AutomaticSaveDescription);
    }

    /// <summary>Restores the slot whose description matches set.simple.</summary>
    public async ValueTask<bool> LoadGameAutomaticAsync()
    {
        int slot = SystemUI.FigureOutAutomaticRestoreGameSlot(State.AutomaticSaveDescription);
        return slot >= 0 && await DoLoadAsync(slot, true);
    }

    public async ValueTask<bool> SaveGameDialogAsync()
    {
        int slot = await SystemUI.AskForSaveGameSlotAsync();
        if (slot < 0)
        {
            return false;
        }

        string? description = await SystemUI.AskForSaveGameDescriptionAsync(slot);
        return description is not null && DoSave(slot, description);
    }

    public async ValueTask<bool> LoadGameDialogAsync()
    {
        int slot = await SystemUI.AskForRestoreGameSlotAsync();
        return slot >= 0 && await DoLoadAsync(slot, true);
    }

    /// <summary>Writes a 160x100 RGB565 thumbnail of the display in ScummVM's THMB block layout.</summary>
    private void WriteThumbnail(SaveWriter w)
    {
        w.WriteUInt32(ThumbnailTag);
        w.WriteUInt32((uint)(ThumbnailHeaderSize + ThumbnailWidth * ThumbnailHeight * 2));
        w.WriteByte(2); // THMB_VERSION
        w.WriteUInt16(ThumbnailWidth);
        w.WriteUInt16(ThumbnailHeight);

        // PixelFormat(2, 5, 6, 5, 0, 11, 5, 0, 0): bytesPerPixel, losses, shifts
        w.WriteBytes(2, 3, 2, 3, 8, 11, 5, 0, 0);

        var display = Gfx.Display;
        var palette = Gfx.CurrentPalette.ToArray();
        int cellWidth = Math.Max(1, display.Width / ThumbnailWidth);
        int cellHeight = Math.Max(1, display.Height / ThumbnailHeight);
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

                if (n > 0)
                {
                    r /= n;
                    g /= n;
                    b /= n;
                }

                w.WriteUInt16((ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3)));
            }
        }
    }

    /// <summary>Big-endian writer over a stream, matching ScummVM's WriteStream.</summary>
    private sealed class SaveWriter(Stream stream)
    {
        private readonly byte[] _buffer = new byte[4];

        public void WriteByte(byte value) => stream.WriteByte(value);

        public void WriteBytes(params byte[] values) => stream.Write(values);

        public void WriteUInt16(ushort value)
        {
            BinaryPrimitives.WriteUInt16BigEndian(_buffer, value);
            stream.Write(_buffer, 0, 2);
        }

        public void WriteInt16(short value) => WriteUInt16((ushort)value);

        public void WriteUInt32(uint value)
        {
            BinaryPrimitives.WriteUInt32BigEndian(_buffer, value);
            stream.Write(_buffer, 0, 4);
        }

        /// <summary>Writes a NUL padded Latin-1 string in a fixed field; the last byte is always NUL.</summary>
        public void WriteFixedString(string value, int fieldLength)
        {
            var field = new byte[fieldLength];
            int count = Math.Min(value.Length, fieldLength - 1);
            for (int i = 0; i < count; i++)
            {
                field[i] = (byte)value[i];
            }

            stream.Write(field);
        }
    }

    /// <summary>Big-endian reader that throws <see cref="EndOfStreamException"/> on short reads.</summary>
    private sealed class SaveReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[4];

        public byte ReadByte()
        {
            int value = stream.ReadByte();
            return value < 0 ? throw new EndOfStreamException() : (byte)value;
        }

        public byte[] ReadBytes(int count)
        {
            var bytes = new byte[count];
            stream.ReadExactly(bytes);
            return bytes;
        }

        public ushort ReadUInt16()
        {
            stream.ReadExactly(_buffer, 0, 2);
            return BinaryPrimitives.ReadUInt16BigEndian(_buffer);
        }

        public short ReadInt16() => (short)ReadUInt16();

        public uint ReadUInt32()
        {
            stream.ReadExactly(_buffer, 0, 4);
            return BinaryPrimitives.ReadUInt32BigEndian(_buffer);
        }

        /// <summary>Reads a fixed field; returns null when it holds no terminating NUL.</summary>
        public string? ReadFixedString(int fieldLength)
        {
            var field = ReadBytes(fieldLength);
            int end = Array.IndexOf(field, (byte)0);
            return end < 0 ? null : Encoding.Latin1.GetString(field, 0, end);
        }

        /// <summary>Skips a ScummVM thumbnail block if one is present.</summary>
        public void SkipThumbnail()
        {
            long position = stream.Position;
            uint tag = ReadUInt32();
            if (tag != ThumbnailTag && tag != ThumbnailTagBad)
            {
                stream.Position = position;
                return;
            }

            uint size = ReadUInt32();
            stream.Position = position + size;
        }
    }
}
