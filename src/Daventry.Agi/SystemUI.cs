using Daventry.Agi.Graphics;
using Daventry.Agi.Text;

namespace Daventry.Agi;

/// <summary>
/// The interpreter's own dialogs: pause, restart, quit, inventory and save texts. Port of the
/// text table and verification dialog from ScummVM's systemui.cpp. Save and restore slot
/// selection and the platform button variants are not ported yet.
/// </summary>
public sealed class SystemUI
{
    private readonly AgiEngine _vm;
    private readonly GfxMgr _gfx;
    private readonly TextMgr _text;
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
                break;
            case AgiRenderMode.Apple2Gs:
                TextPause = "Game paused.";
                TextRestart = "Restart the game?     ";
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

    public string TextRestoreGameNoSlots { get; } = "There are no games to\nrestore in\n\n the saved game directory\n\nPress ENTER to continue.";

    public string TextRestoreGameSelectSlot { get; } = "Use the arrow keys to select the game which you wish to restore. Press ENTER to restore the game, ESC to not restore a game.";

    public string TextRestoreGameError { get; } = "Error in restoring game.\nPress ENTER to quit.";

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

    public void SavedGameSlotKeyPress(ushort newKey)
    {
        if (newKey is AgiKey.Enter or AgiKey.Escape)
        {
            _vm.CycleInnerLoopInactive();
        }
    }
}
