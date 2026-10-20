using Daventry.Agi.Text;
using Daventry.Core.Input;

namespace Daventry.Agi;

/// <summary>Input queue, key translation and controller handling. Port of ScummVM's keyboard.cpp.</summary>
public sealed partial class AgiEngine
{
    private const int KeyQueueSize = 16;

    private readonly Queue<ushort> _keyQueue = new();
    private readonly List<InputEvent> _pendingHostEvents = [];
    private Key _keyHoldModeLastKey = Key.None;

    /// <summary>Set temporarily to accept repeated cursor keys as direction events.</summary>
    public bool AllowSynthetic { get; set; }

    /// <summary>Called by the engine each tick with the host's input since the previous tick.</summary>
    internal void QueueHostEvent(InputEvent inputEvent) => _pendingHostEvents.Add(inputEvent);

    private void KeyEnqueue(ushort key)
    {
        if (_keyQueue.Count < KeyQueueSize)
        {
            _keyQueue.Enqueue(key);
        }
    }

    /// <summary>Translates host input into AGI key codes and mouse state. Port of processScummVMEvents.</summary>
    public void ProcessHostEvents()
    {
        if (_pendingHostEvents.Count == 0)
        {
            return;
        }

        foreach (var ev in _pendingHostEvents)
        {
            switch (ev.Type)
            {
                case InputEventType.MouseDown:
                    if (State.MouseEnabled)
                    {
                        if (ev.Button == MouseButton.Left)
                        {
                            Mouse.Button = AgiMouseButton.Left;
                            KeyEnqueue(AgiKey.MouseButtonLeft);
                        }
                        else if (ev.Button == MouseButton.Right)
                        {
                            Mouse.Button = AgiMouseButton.Right;
                            KeyEnqueue(AgiKey.MouseButtonRight);
                        }

                        Mouse.X = ev.X;
                        Mouse.Y = ev.Y;
                    }

                    break;

                case InputEventType.MouseMove:
                    if (State.MouseEnabled)
                    {
                        Mouse.X = ev.X;
                        Mouse.Y = ev.Y;
                        if (!State.MouseFence.IsEmpty)
                        {
                            Mouse.X = Math.Clamp(Mouse.X, State.MouseFence.Left, State.MouseFence.Right);
                            Mouse.Y = Math.Clamp(Mouse.Y, State.MouseFence.Top, State.MouseFence.Bottom);
                        }
                    }

                    break;

                case InputEventType.MouseUp:
                    if (State.MouseEnabled)
                    {
                        Mouse.Button = AgiMouseButton.Up;
                        Mouse.X = ev.X;
                        Mouse.Y = ev.Y;
                    }

                    break;

                case InputEventType.KeyDown:
                {
                    ushort key = TranslateKeyDown(ev);
                    if (key != 0)
                    {
                        KeyEnqueue(key);
                    }

                    break;
                }

                case InputEventType.KeyUp:
                    if (KeyHoldMode && IsDirectionKey(ev.Key) && _keyHoldModeLastKey == ev.Key)
                    {
                        // The original generated direction events on release of the last cursor key.
                        // A stationary event gives the same result through HandleController.
                        KeyEnqueue(AgiKey.Stationary);
                    }

                    break;
            }
        }

        _pendingHostEvents.Clear();
    }

    private static bool IsDirectionKey(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down
        or Key.Home or Key.End or Key.PageUp or Key.PageDown
        or Key.Keypad4 or Key.Keypad6 or Key.Keypad8 or Key.Keypad2 or Key.Keypad9 or Key.Keypad3 or Key.Keypad7 or Key.Keypad1;

    private ushort TranslateKeyDown(InputEvent ev)
    {
        int key = ev.Ascii;

        if (key != 0 && key <= 0xFF)
        {
            // a plain character; map Ctrl+A to 1 and so on, and Alt+letter to its scancode
            if (char.IsAsciiLetter((char)key))
            {
                if ((ev.Modifiers & KeyModifiers.Ctrl) != 0)
                {
                    key = char.ToUpperInvariant((char)key) - 'A' + 1;
                }
                else if ((ev.Modifiers & KeyModifiers.Alt) != 0)
                {
                    key = AgiKey.ScancodeTable[char.ToUpperInvariant((char)key) - 'A'] << 8;
                }
            }

            return (ushort)key;
        }

        bool acceptDirection = AllowSynthetic || !ev.Repeat;
        key = ev.Key switch
        {
            Key.Left or Key.Keypad4 => acceptDirection ? AgiKey.Left : 0,
            Key.Right or Key.Keypad6 => acceptDirection ? AgiKey.Right : 0,
            Key.Up or Key.Keypad8 => acceptDirection ? AgiKey.Up : 0,
            Key.Down or Key.Keypad2 => acceptDirection ? AgiKey.Down : 0,
            Key.PageUp or Key.Keypad9 => acceptDirection ? AgiKey.UpRight : 0,
            Key.PageDown or Key.Keypad3 => acceptDirection ? AgiKey.DownRight : 0,
            Key.Home or Key.Keypad7 => acceptDirection ? AgiKey.UpLeft : 0,
            Key.End or Key.Keypad1 => acceptDirection ? AgiKey.DownLeft : 0,
            Key.Keypad5 => AgiKey.Stationary,
            Key.F1 => AgiKey.F1,
            Key.F2 => AgiKey.F2,
            Key.F3 => AgiKey.F3,
            Key.F4 => AgiKey.F4,
            Key.F5 => AgiKey.F5,
            Key.F6 => AgiKey.F6,
            Key.F7 => AgiKey.F7,
            Key.F8 => AgiKey.F8,
            Key.F9 => AgiKey.F9,
            Key.F10 => AgiKey.F10,
            Key.F11 => AgiKey.F11,
            Key.F12 => AgiKey.F12,
            Key.KeypadEnter => AgiKey.Enter,
            _ => 0,
        };

        if (IsDirectionKey(ev.Key))
        {
            _keyHoldModeLastKey = ev.Key;
        }

        return (ushort)key;
    }

    /// <summary>Returns the next queued key, or 0.</summary>
    public ushort DoPollKeyboard() => IsKeypress() ? GetKeypress() : (ushort)0;

    public bool IsKeypress()
    {
        ProcessHostEvents();
        return _keyQueue.Count > 0;
    }

    public ushort GetKeypress() => _keyQueue.TryDequeue(out var key) ? key : (ushort)0;

    public void ClearKeyQueue()
    {
        ProcessHostEvents();
        _keyQueue.Clear();
    }

    /// <summary>Routes a left click to the menu bar or the active inner loop. Returns true if the click was consumed.</summary>
    public bool HandleMouseClicks(ref ushort key)
    {
        if (key != AgiKey.MouseButtonLeft)
        {
            return false;
        }

        if (!CycleInnerLoopIsActive)
        {
            var displayLineRect = Gfx.GetFontRectForDisplayScreen(0, 0, TextMgr.FontColumnCharacters, 1);
            if (displayLineRect.Contains(Mouse.X, Mouse.Y))
            {
                // the mouse is inside the first line of the screen
                if (GetFlag(AgiFlag.MenusAccessible) && Menu.IsAvailable())
                {
                    Menu.DelayedExecuteViaMouse();
                    key = 0;
                    return true;
                }
            }

            if (Text.PromptIsEnabled())
            {
                int promptRow = Text.PromptRow_Get();
                var promptRect = new Core.Graphics.Rect(0, promptRow * Gfx.DisplayFontHeight, displayLineRect.Width, displayLineRect.Height);
                if (promptRect.Contains(Mouse.X, Mouse.Y))
                {
                    key = 0; // the predictive input dialog would open here
                    return true;
                }
            }
        }
        else
        {
            switch (State.CycleInnerLoopType)
            {
                case CycleInnerLoopType.MenuViaKeyboard:
                    Menu.MouseEvent(key);
                    key = 0;
                    break;
            }
        }

        return false;
    }

    /// <summary>Maps a key to a controller or an ego direction. Returns true if the key was consumed.</summary>
    public bool HandleController(ushort key)
    {
        var ego = State.ScreenObjTable[AgiGameState.ScreenObjectsEgoEntry];

        if (key == 0)
        {
            return false;
        }

        if (key == AgiKey.Escape)
        {
            // On the Amiga, Apple IIgs and Atari ST, ESC paused and the menu was opened by clicking the
            // status line. If the menu is accessible and available, open it; otherwise fall through to
            // the controllers as on PC.
            switch (Description.Platform)
            {
                case AgiPlatform.Amiga:
                case AgiPlatform.Apple2Gs:
                case AgiPlatform.AtariSt:
                    if (GetFlag(AgiFlag.MenusAccessible) && Menu.IsAvailable())
                    {
                        Menu.DelayedExecuteViaKeyboard();
                        return true;
                    }

                    break;
            }
        }

        if (Description.Gid is AgiGameId.Mh1 or AgiGameId.Mh2 && key == AgiKey.Enter && !Text.PromptIsEnabled())
        {
            key = 0x20; // Enter acts as Space in Manhunter when the prompt is disabled
        }

        foreach (var mapping in State.ControllerKeyMapping)
        {
            if (mapping.KeyCode == key)
            {
                State.ControllerOccurred[mapping.ControllerSlot] = true;
                return true;
            }
        }

        int newDirection = key switch
        {
            AgiKey.Up => 1,
            AgiKey.Down => 5,
            AgiKey.Left => 7,
            AgiKey.Right => 3,
            AgiKey.UpRight => 2,
            AgiKey.DownRight => 4,
            AgiKey.UpLeft => 8,
            AgiKey.DownLeft => 6,
            _ => 0,
        };

        if (State.PlayerControl && (Description.Features & AgiGameFeatures.AgiMouse) == 0 && !State.MouseHidden && key == AgiKey.MouseButtonLeft)
        {
            if (Description.Gid == AgiGameId.Pq1 && GetVar(AgiVar.CurrentRoom) == 116)
            {
                // WORKAROUND: clicks in PQ1's newspaper screen act as the right arrow (bug #4908)
                newDirection = 3;
            }
            else
            {
                // click-to-walk; AdjEgoXY is deliberately not set, it would break climbing the log in SQ2
                int egoDestinationX = Mouse.X;
                int egoDestinationY = Mouse.Y;
                Gfx.TranslateDisplayPosToGameScreen(ref egoDestinationX, ref egoDestinationY);
                ego.MotionType = MotionType.Ego;
                ego.MoveX = egoDestinationX < ego.XSize / 2 ? -1 : egoDestinationX - ego.XSize / 2;
                ego.MoveY = egoDestinationY;
                ego.MoveStepSize = ego.StepSize;
                return true;
            }
        }

        if (newDirection != 0 || key == AgiKey.Stationary)
        {
            ego.Flags &= ~ViewFlags.AdjEgoXY;
            SetVar(AgiVar.EgoDirection, (byte)(ego.Direction == newDirection ? 0 : newDirection));
            if (State.PlayerControl)
            {
                ego.MotionType = MotionType.Normal;
            }

            return true;
        }

        return false;
    }

    /// <summary>Waits for Enter, Escape or a left click.</summary>
    public async ValueTask<ushort> WaitKeyAsync()
    {
        ushort key = 0;
        ClearKeyQueue();
        while (!(ShouldQuit || _restartGame || GetFlag(AgiFlag.RestoreJustRan)))
        {
            await WaitAsync(10);
            key = DoPollKeyboard();
            if (key is AgiKey.Enter or AgiKey.Escape or AgiKey.MouseButtonLeft)
            {
                break;
            }
        }

        return key;
    }

    public async ValueTask<ushort> WaitAnyKeyAsync()
    {
        ushort key = 0;
        ClearKeyQueue();
        while (!(ShouldQuit || _restartGame))
        {
            await WaitAsync(10);
            key = DoPollKeyboard();
            if (key != 0)
            {
                break;
            }
        }

        return key;
    }

    /// <summary>Waits for a key or for the sound to finish, for platforms where sound playback blocked the interpreter.</summary>
    public async ValueTask WaitAnyKeyOrFinishedSoundAsync()
    {
        ClearKeyQueue();
        while (!(ShouldQuit || _restartGame || !Sound.IsPlaying))
        {
            await WaitAsync(10);
            if (DoPollKeyboard() != 0)
            {
                break;
            }
        }
    }
}
