using Daventry.Agi.Graphics;
using Daventry.Agi.Text;

namespace Daventry.Agi;

/// <summary>
/// The interpreter's own dialogs: pause, restart, quit, inventory texts and the saved game slot
/// chooser. Port of ScummVM's systemui.cpp. The Amiga, Apple IIgs and Atari ST button variants of
/// the verification dialog and right-to-left layout are not ported.
/// </summary>
public sealed class SystemUI
{
    public const int SavedGameSlotsOnScreen = 12;
    public const int SavedGameDescriptionLength = 30;
    public const int SavedGameDisplayTextLength = 31;
    public const int SavedGameDisplayTextPrefixLength = 3;

    public sealed class SavedGameEntry
    {
        public int SlotId { get; init; }

        public bool Exists { get; init; }

        public bool IsValid { get; init; }

        /// <summary>The description as stored in the file.</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>"NN:" plus the description, padded for the list.</summary>
        public string DisplayText { get; init; } = string.Empty;
    }

    private readonly AgiEngine _vm;
    private readonly GfxMgr _gfx;
    private readonly TextMgr _text;
    private readonly List<SavedGameEntry> _savedGameArray = [];
    private int _savedGameUpmostSlotNr;
    private int _savedGameSelectedSlotNr;
    private bool _askForVerificationContinueOnMessageBoxClick;
    private bool _askForVerificationCancelled;

    public SystemUI(AgiEngine vm, GfxMgr gfx, TextMgr text)
    {
        _vm = vm;
        _gfx = gfx;
        _text = text;

        switch (gfx.RenderMode)
        {
            case AgiRenderMode.Amiga:
                TextPause = "Game paused.";
                TextRestart = "Restart the game?";
                TextQuit = "Quit the game, or continue?";
                TextSaveGameVerify = "About to save the game\ndescribed as:\n\n%s\n\nin file:\n%s";
                TextRestoreGameVerify = "About to restore the game\ndescribed as:\n\n%s\n\nfrom file:\n%s";
                break;
            case AgiRenderMode.Apple2Gs:
                TextPause = "Game paused.";
                TextRestart = "Restart the game?     ";
                TextSaveGameVerify = "About to save the game\ndescribed as:\n\n%s\n\nin file:\n%s";
                TextRestoreGameVerify = "About to restore the game\ndescribed as:\n\n%s\n\nfrom file:\n%s";
                break;
            case AgiRenderMode.AtariSt:
                TextPause = "Game paused.  Press the left\nmouse button to continue.";
                TextRestart = "About to restart the game.";
                break;
        }
    }

    public string StatusTextScore { get; } = "Score:%v3 of %v7";

    public string StatusTextSoundOn { get; } = "Sound:on";

    public string StatusTextSoundOff { get; } = "Sound:off";

    public string TextEnterCommand { get; } = "Enter input\n\n";

    public string TextPause { get; } = "      Game paused.\nPress Enter to continue.";

    public string TextRestart { get; } = "Press ENTER to restart\nthe game.\n\nPress ESC to continue\nthis game.";

    public string TextQuit { get; } = "Press ENTER to quit.\nPress ESC to keep playing.";

    public string InventoryTextYouAreCarrying { get; } = "You are carrying:";

    public string InventoryTextNothing { get; } = "nothing";

    public string InventoryTextSelectItems { get; } = "Press ENTER to select, ESC to cancel";

    public string InventoryTextReturnToGame { get; } = "Press a key to return to the game";

    public string TextSaveGameSelectSlot { get; } = "Use the arrow keys to select the slot in which you wish to save the game. Press ENTER to save in the slot, ESC to not save a game.";

    public string TextSaveGameEnterDescription { get; } = "How would you like to describe this saved game?\n\n";

    public string TextSaveGameVerify { get; } = "About to save the game\ndescribed as:\n\n%s\n\nin file:\n%s\n\nPress ENTER to continue.\nPress ESC to cancel.";

    public string TextRestoreGameNoSlots { get; } = "There are no games to\nrestore in\n\n the saved game directory\n\nPress ENTER to continue.";

    public string TextRestoreGameSelectSlot { get; } = "Use the arrow keys to select the game which you wish to restore. Press ENTER to restore the game, ESC to not restore a game.";

    public string TextRestoreGameError { get; } = "Error in restoring game.\nPress ENTER to quit.";

    public string TextRestoreGameVerify { get; } = "About to restore the game\ndescribed as:\n\n%s\n\nfrom file:\n%s\n\nPress ENTER to continue.\nPress ESC to cancel.";

    public IReadOnlyList<SavedGameEntry> SavedGames => _savedGameArray;

    /// <summary>Index into <see cref="SavedGames"/> of the highlighted entry while the chooser is open.</summary>
    public int SavedGameSelectedSlotNr => _savedGameSelectedSlotNr;

    public async ValueTask PauseDialogAsync() => await AskForVerificationAsync(TextPause, true);

    public ValueTask<bool> RestartDialogAsync() => AskForVerificationAsync(TextRestart, false);

    public ValueTask<bool> QuitDialogAsync() => AskForVerificationAsync(TextQuit, false);

    /// <summary>Shows a message box and waits for Enter (true) or Escape (false). A click inside the box continues when allowed.</summary>
    public async ValueTask<bool> AskForVerificationAsync(string verifyText, bool continueOnMessageBoxClick)
    {
        _text.DrawMessageBox(verifyText, 0, 35);
        _askForVerificationContinueOnMessageBoxClick = continueOnMessageBoxClick;

        _vm.CycleInnerLoopActivate(CycleInnerLoopType.SystemUiVerification);
        _askForVerificationCancelled = false;
        do
        {
            await _vm.ProcessAgiEventsAsync();
        }
        while (_vm.CycleInnerLoopIsActive && !(_vm.ShouldQuit || _vm.RestartRequested));

        _text.CloseWindow();
        return !(_askForVerificationCancelled || _vm.ShouldQuit);
    }

    public void AskForVerificationKeyPress(ushort newKey)
    {
        switch (newKey)
        {
            case AgiKey.Enter:
                _vm.CycleInnerLoopInactive();
                break;
            case AgiKey.Escape:
                _askForVerificationCancelled = true;
                _vm.CycleInnerLoopInactive();
                break;
            case AgiKey.MouseButtonLeft:
                if (_askForVerificationContinueOnMessageBoxClick && _text.IsMouseWithinMessageBox())
                {
                    _vm.CycleInnerLoopInactive();
                }

                break;
        }
    }

    // ---- saved games ----

    /// <summary>Slot that already holds the automatic description, else the first free slot, else -1.</summary>
    public int FigureOutAutomaticSaveGameSlot(string automaticSaveDescription)
    {
        ReadSavedGameSlots(filterNonexistent: false, withAutoSaveSlot: false);
        FigureOutAutomaticSavedGameSlot(automaticSaveDescription, out int matched, out int fresh);
        return matched >= 0 ? matched : fresh;
    }

    /// <summary>Slot that holds the automatic description, or -1.</summary>
    public int FigureOutAutomaticRestoreGameSlot(string automaticSaveDescription)
    {
        ReadSavedGameSlots(filterNonexistent: true, withAutoSaveSlot: false);
        FigureOutAutomaticSavedGameSlot(automaticSaveDescription, out int matched, out _);
        return matched;
    }

    /// <summary>Lets the player pick a slot to save into. Returns the slot id, or -1 when cancelled.</summary>
    public async ValueTask<int> AskForSaveGameSlotAsync()
    {
        ReadSavedGameSlots(filterNonexistent: false, withAutoSaveSlot: false);

        int slotNr = await AskForSavedGameSlotAsync(TextSaveGameSelectSlot);
        return slotNr < 0 ? -1 : _savedGameArray[slotNr].SlotId;
    }

    /// <summary>Asks for a description and confirmation. Returns null when cancelled.</summary>
    public async ValueTask<string?> AskForSaveGameDescriptionAsync(int slotId)
    {
        bool previousEditState = _text.InputGetEditStatus();
        byte previousEditCursor = _text.InputGetCursorChar();

        _text.DrawMessageBox(TextSaveGameEnterDescription, 0, 31, true);
        _text.InputEditOn();

        _text.CharPos_Push();
        _text.CharAttrib_Push();

        _text.CharPos_SetInsideWindow(3, 0);
        _text.CharAttrib_Set(15, 0);
        _text.ClearBlockInsideWindow(3, 0, 31, 0); // the input line is black
        _text.InputSetCursorChar('_');

        // preset the current description of the slot
        _text.StringSet(string.Empty);
        foreach (var entry in _savedGameArray)
        {
            if (entry.SlotId == slotId && entry.IsValid)
            {
                _text.StringSet(entry.Description);
            }
        }

        _vm.CycleInnerLoopActivate(CycleInnerLoopType.GetString);
        await _text.StringEditAsync(SavedGameDescriptionLength);

        _text.CharAttrib_Pop();
        _text.CharPos_Pop();
        _text.InputSetCursorChar((char)previousEditCursor);
        if (!previousEditState)
        {
            _text.InputEditOff();
        }

        _text.CloseWindow();

        if (!_text.StringWasEntered())
        {
            return null;
        }

        string description = _text.InputString;
        if (!await AskForSavedGameVerificationAsync(TextSaveGameVerify, description, slotId))
        {
            return null;
        }

        return description;
    }

    /// <summary>Lets the player pick a saved game to restore. Returns the slot id, or -1.</summary>
    public async ValueTask<int> AskForRestoreGameSlotAsync()
    {
        ReadSavedGameSlots(filterNonexistent: true, withAutoSaveSlot: true);

        if (_savedGameArray.Count == 0)
        {
            await _text.MessageBoxAsync(TextRestoreGameNoSlots);
            return -1;
        }

        int slotNr = await AskForSavedGameSlotAsync(TextRestoreGameSelectSlot);
        if (slotNr < 0)
        {
            return -1;
        }

        var selected = _savedGameArray[slotNr];
        if (!selected.IsValid)
        {
            await _text.MessageBoxAsync(TextRestoreGameError);
            return -1;
        }

        if (!await AskForSavedGameVerificationAsync(TextRestoreGameVerify, selected.Description, selected.SlotId))
        {
            return -1;
        }

        return selected.SlotId;
    }

    private async ValueTask<int> AskForSavedGameSlotAsync(string slotListText)
    {
        int messageBoxHeight = 5 + Math.Min(_savedGameArray.Count, SavedGameSlotsOnScreen);
        _text.DrawMessageBox(slotListText, messageBoxHeight, 34, true);

        DrawSavedGameSlots();
        DrawSavedGameSlotSelector(true);

        _vm.CycleInnerLoopActivate(CycleInnerLoopType.SystemUiSelectSavedGameSlot);
        do
        {
            await _vm.ProcessAgiEventsAsync();
        }
        while (_vm.CycleInnerLoopIsActive && !(_vm.ShouldQuit || _vm.RestartRequested));

        _text.CloseWindow();
        return _savedGameSelectedSlotNr;
    }

    public void SavedGameSlotKeyPress(ushort newKey)
    {
        int slotCount = _savedGameArray.Count;
        int newUpmostSlotNr = _savedGameUpmostSlotNr;
        int newSelectedSlotNr = _savedGameSelectedSlotNr;
        bool slotsScrolled = false;

        switch (newKey)
        {
            case AgiKey.Enter:
                _vm.CycleInnerLoopInactive();
                return;
            case AgiKey.Escape:
                _savedGameSelectedSlotNr = -1;
                _vm.CycleInnerLoopInactive();
                return;
            case AgiKey.Up:
                newSelectedSlotNr--;
                break;
            case AgiKey.Down:
                newSelectedSlotNr++;
                break;

            // Scrolling did not exist in the original, which was limited to 12 slots
            case AgiKey.PageUp:
                newUpmostSlotNr -= SavedGameSlotsOnScreen;
                break;
            case AgiKey.PageDown:
                newUpmostSlotNr += SavedGameSlotsOnScreen;
                break;
            case AgiKey.Home:
                newUpmostSlotNr = 0;
                break;
            case AgiKey.End:
                newUpmostSlotNr = slotCount - 1 - (SavedGameSlotsOnScreen - 1);
                break;
        }

        if (newUpmostSlotNr != _savedGameUpmostSlotNr)
        {
            if (newUpmostSlotNr < 0)
            {
                newUpmostSlotNr = 0;
            }

            if (newUpmostSlotNr + (SavedGameSlotsOnScreen - 1) >= slotCount)
            {
                newUpmostSlotNr = Math.Max(0, slotCount - 1 - (SavedGameSlotsOnScreen - 1));
            }

            if (newUpmostSlotNr != _savedGameUpmostSlotNr)
            {
                slotsScrolled = true;
                int slotDifference = _savedGameSelectedSlotNr - _savedGameUpmostSlotNr;
                newSelectedSlotNr = newUpmostSlotNr + slotDifference;
            }
        }

        if (newSelectedSlotNr != _savedGameSelectedSlotNr || slotsScrolled)
        {
            if (newSelectedSlotNr < 0)
            {
                newSelectedSlotNr = slotCount - 1;
                newUpmostSlotNr = Math.Max(0, newSelectedSlotNr - SavedGameSlotsOnScreen);
            }

            if (newSelectedSlotNr >= slotCount)
            {
                newSelectedSlotNr = 0;
                newUpmostSlotNr = 0;
            }

            if (newSelectedSlotNr < newUpmostSlotNr)
            {
                newUpmostSlotNr = newSelectedSlotNr;
            }

            if (newSelectedSlotNr >= newUpmostSlotNr + SavedGameSlotsOnScreen)
            {
                newUpmostSlotNr = newSelectedSlotNr - (SavedGameSlotsOnScreen - 1);
            }

            DrawSavedGameSlotSelector(false);

            bool drawSlots = newUpmostSlotNr != _savedGameUpmostSlotNr;
            _savedGameUpmostSlotNr = newUpmostSlotNr;
            _savedGameSelectedSlotNr = newSelectedSlotNr;
            if (drawSlots)
            {
                DrawSavedGameSlots();
            }

            DrawSavedGameSlotSelector(true);
        }
    }

    private static string CreateSavedGameDisplayText(string actualDescription, int slotId, bool fillUpWithSpaces)
    {
        int maxLength = SavedGameDisplayTextLength - SavedGameDisplayTextPrefixLength;
        string description = actualDescription.Length > maxLength ? actualDescription[..maxLength] : actualDescription;
        string text = $"{slotId:00}:{description}";
        return fillUpWithSpaces ? text.PadRight(SavedGameDisplayTextLength) : text;
    }

    /// <summary>Fills the slot list from the save store. Port of readSavedGameSlots.</summary>
    private void ReadSavedGameSlots(bool filterNonexistent, bool withAutoSaveSlot)
    {
        _savedGameArray.Clear();
        _savedGameUpmostSlotNr = 0;
        _savedGameSelectedSlotNr = 0;

        int mostRecentSlotNr = -1;
        uint mostRecentSlotSaveDate = 0;
        uint mostRecentSlotSaveTime = 0;
        int lastSlotId = -1;

        var slotIds = _vm.GetSavegameSlotIds();
        slotIds.Add(AgiEngine.SaveGameMaximumSlots); // so that the loop processes every slot

        foreach (int rawSlotId in slotIds)
        {
            int curSlotId = Math.Min(rawSlotId, AgiEngine.SaveGameMaximumSlots);
            if (curSlotId == 0 && !withAutoSaveSlot)
            {
                continue;
            }

            if (!filterNonexistent)
            {
                // add the empty slots between the last one and this one
                for (int loopSlotId = lastSlotId + 1; loopSlotId < curSlotId; loopSlotId++)
                {
                    if (loopSlotId == 0 && !withAutoSaveSlot)
                    {
                        continue;
                    }

                    _savedGameArray.Add(new SavedGameEntry
                    {
                        SlotId = loopSlotId,
                        Exists = false,
                        IsValid = false,
                        DisplayText = CreateSavedGameDisplayText(string.Empty, loopSlotId, true),
                    });
                }
            }

            if (curSlotId >= AgiEngine.SaveGameMaximumSlots)
            {
                break;
            }

            bool exists = _vm.GetSavegameInformation(curSlotId, out string description, out uint saveDate, out uint saveTime, out bool saveIsValid);
            if (exists)
            {
                if (saveIsValid)
                {
                    // larger or equal on purpose, so the last of several saves with the same stamp wins
                    if (saveDate > mostRecentSlotSaveDate || (saveDate == mostRecentSlotSaveDate && saveTime >= mostRecentSlotSaveTime))
                    {
                        mostRecentSlotNr = _savedGameArray.Count;
                        mostRecentSlotSaveDate = saveDate;
                        mostRecentSlotSaveTime = saveTime;
                    }
                }
            }
            else if (filterNonexistent)
            {
                continue;
            }

            _savedGameArray.Add(new SavedGameEntry
            {
                SlotId = curSlotId,
                Exists = true,
                IsValid = saveIsValid,
                Description = description.Length > SavedGameDescriptionLength ? description[..SavedGameDescriptionLength] : description,
                DisplayText = CreateSavedGameDisplayText(description, curSlotId, true),
            });

            lastSlotId = curSlotId;
        }

        if (mostRecentSlotNr >= 0)
        {
            // the most recent save is the default, which Sierra seems to have done as well
            _savedGameSelectedSlotNr = mostRecentSlotNr;
            if (mostRecentSlotNr < SavedGameSlotsOnScreen)
            {
                _savedGameUpmostSlotNr = 0;
            }
            else
            {
                // scroll so the slot sits in the middle
                int slotCount = _savedGameArray.Count;
                _savedGameUpmostSlotNr = mostRecentSlotNr - SavedGameSlotsOnScreen / 2;
                if (_savedGameUpmostSlotNr + (SavedGameSlotsOnScreen - 1) >= slotCount)
                {
                    _savedGameUpmostSlotNr = slotCount - 1 - (SavedGameSlotsOnScreen - 1);
                }
            }
        }
    }

    private void FigureOutAutomaticSavedGameSlot(string automaticSaveDescription, out int matchedGameSlotId, out int freshGameSlotId)
    {
        matchedGameSlotId = -1;
        freshGameSlotId = -1;

        foreach (var entry in _savedGameArray)
        {
            if (entry.IsValid && entry.Description == automaticSaveDescription)
            {
                matchedGameSlotId = entry.SlotId;
                return;
            }

            if (freshGameSlotId < 0 && !entry.Exists && entry.SlotId != 0)
            {
                freshGameSlotId = entry.SlotId;
            }
        }
    }

    private void DrawSavedGameSlots()
    {
        int slotsToDrawCount = Math.Min(_savedGameArray.Count - _savedGameUpmostSlotNr, SavedGameSlotsOnScreen);

        _text.CharAttrib_Push();
        _text.CharAttrib_Set(0, 15);
        for (int slotNr = 0; slotNr < slotsToDrawCount; slotNr++)
        {
            _text.DisplayTextInsideWindow("-", 5 + slotNr, 1);
            _text.DisplayTextInsideWindow(_savedGameArray[_savedGameUpmostSlotNr + slotNr].DisplayText, 5 + slotNr, 3);
        }

        _text.CharAttrib_Pop();
    }

    private void DrawSavedGameSlotSelector(bool active)
    {
        int windowRow = 5 + (_savedGameSelectedSlotNr - _savedGameUpmostSlotNr);

        _text.CharAttrib_Push();
        _text.CharAttrib_Set(0, 15);
        _text.DisplayTextInsideWindow(active ? "\x1a" : " ", windowRow, 0);
        _text.CharAttrib_Pop();
    }

    private async ValueTask<bool> AskForSavedGameVerificationAsync(string verifyText, string actualDescription, int slotId)
    {
        string displayDescription = CreateSavedGameDisplayText(actualDescription, slotId, false);
        string fileName = _vm.GetSaveStateName(slotId);

        // the texts hold two %s placeholders: description, then file name
        int first = verifyText.IndexOf("%s", StringComparison.Ordinal);
        string userActionVerify = verifyText;
        if (first >= 0)
        {
            userActionVerify = string.Concat(verifyText.AsSpan(0, first), displayDescription, verifyText.AsSpan(first + 2));
            int second = userActionVerify.IndexOf("%s", StringComparison.Ordinal);
            if (second >= 0)
            {
                userActionVerify = string.Concat(userActionVerify.AsSpan(0, second), fileName, userActionVerify.AsSpan(second + 2));
            }
        }

        return await AskForVerificationAsync(userActionVerify, false);
    }
}
