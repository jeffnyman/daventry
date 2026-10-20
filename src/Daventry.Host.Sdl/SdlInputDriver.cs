using Daventry.Core.Input;
using SDL;
using static SDL.SDL3;

namespace Daventry.Host.Sdl;

/// <summary>
/// Drains the SDL event queue into engine input events. Key codes are translated to ScummVM style
/// values and a printable character is synthesised for key presses using a US layout, which is
/// what the original interpreters assumed. Text input events for other layouts can be added later.
/// </summary>
internal sealed unsafe class SdlInputDriver : IInputDriver
{
    private static readonly Dictionary<SDL_Keycode, Key> SpecialKeys = new()
    {
        [SDL_Keycode.SDLK_UP] = Key.Up,
        [SDL_Keycode.SDLK_DOWN] = Key.Down,
        [SDL_Keycode.SDLK_LEFT] = Key.Left,
        [SDL_Keycode.SDLK_RIGHT] = Key.Right,
        [SDL_Keycode.SDLK_INSERT] = Key.Insert,
        [SDL_Keycode.SDLK_HOME] = Key.Home,
        [SDL_Keycode.SDLK_END] = Key.End,
        [SDL_Keycode.SDLK_PAGEUP] = Key.PageUp,
        [SDL_Keycode.SDLK_PAGEDOWN] = Key.PageDown,
        [SDL_Keycode.SDLK_F1] = Key.F1,
        [SDL_Keycode.SDLK_F2] = Key.F2,
        [SDL_Keycode.SDLK_F3] = Key.F3,
        [SDL_Keycode.SDLK_F4] = Key.F4,
        [SDL_Keycode.SDLK_F5] = Key.F5,
        [SDL_Keycode.SDLK_F6] = Key.F6,
        [SDL_Keycode.SDLK_F7] = Key.F7,
        [SDL_Keycode.SDLK_F8] = Key.F8,
        [SDL_Keycode.SDLK_F9] = Key.F9,
        [SDL_Keycode.SDLK_F10] = Key.F10,
        [SDL_Keycode.SDLK_F11] = Key.F11,
        [SDL_Keycode.SDLK_F12] = Key.F12,
        [SDL_Keycode.SDLK_KP_0] = Key.Keypad0,
        [SDL_Keycode.SDLK_KP_1] = Key.Keypad1,
        [SDL_Keycode.SDLK_KP_2] = Key.Keypad2,
        [SDL_Keycode.SDLK_KP_3] = Key.Keypad3,
        [SDL_Keycode.SDLK_KP_4] = Key.Keypad4,
        [SDL_Keycode.SDLK_KP_5] = Key.Keypad5,
        [SDL_Keycode.SDLK_KP_6] = Key.Keypad6,
        [SDL_Keycode.SDLK_KP_7] = Key.Keypad7,
        [SDL_Keycode.SDLK_KP_8] = Key.Keypad8,
        [SDL_Keycode.SDLK_KP_9] = Key.Keypad9,
        [SDL_Keycode.SDLK_KP_PERIOD] = Key.KeypadPeriod,
        [SDL_Keycode.SDLK_KP_DIVIDE] = Key.KeypadDivide,
        [SDL_Keycode.SDLK_KP_MULTIPLY] = Key.KeypadMultiply,
        [SDL_Keycode.SDLK_KP_MINUS] = Key.KeypadMinus,
        [SDL_Keycode.SDLK_KP_PLUS] = Key.KeypadPlus,
        [SDL_Keycode.SDLK_KP_ENTER] = Key.KeypadEnter,
        [SDL_Keycode.SDLK_KP_EQUALS] = Key.KeypadEquals,
        [SDL_Keycode.SDLK_NUMLOCKCLEAR] = Key.NumLock,
        [SDL_Keycode.SDLK_CAPSLOCK] = Key.CapsLock,
        [SDL_Keycode.SDLK_SCROLLLOCK] = Key.ScrollLock,
        [SDL_Keycode.SDLK_RSHIFT] = Key.RightShift,
        [SDL_Keycode.SDLK_LSHIFT] = Key.LeftShift,
        [SDL_Keycode.SDLK_RCTRL] = Key.RightCtrl,
        [SDL_Keycode.SDLK_LCTRL] = Key.LeftCtrl,
        [SDL_Keycode.SDLK_RALT] = Key.RightAlt,
        [SDL_Keycode.SDLK_LALT] = Key.LeftAlt,
        [SDL_Keycode.SDLK_RGUI] = Key.RightMeta,
        [SDL_Keycode.SDLK_LGUI] = Key.LeftMeta,
        [SDL_Keycode.SDLK_PAUSE] = Key.Pause,
    };

    private readonly Queue<InputEvent> _queue = new();
    private readonly SdlGraphicsDriver _graphics;

    public SdlInputDriver(SdlGraphicsDriver graphics)
    {
        _graphics = graphics;
    }

    /// <summary>Set once the window was closed or SDL asked to quit, whether or not the engine reacts.</summary>
    public bool QuitRequested { get; private set; }

    public bool TryRead(out InputEvent inputEvent) => _queue.TryDequeue(out inputEvent);

    public MouseState GetMouseState()
    {
        float x;
        float y;
        var buttons = SDL_GetMouseState(&x, &y);
        var (fx, fy) = _graphics.ToFramebuffer(x, y);
        return new MouseState(
            fx,
            fy,
            (buttons & SDL_MouseButtonFlags.SDL_BUTTON_LMASK) != 0,
            (buttons & SDL_MouseButtonFlags.SDL_BUTTON_RMASK) != 0,
            (buttons & SDL_MouseButtonFlags.SDL_BUTTON_MMASK) != 0);
    }

    /// <summary>Moves everything SDL has queued into the engine queue. Called by the host loop.</summary>
    public void Pump()
    {
        SDL_Event sdlEvent;
        while (SDL_PollEvent(&sdlEvent))
        {
            switch ((SDL_EventType)sdlEvent.type)
            {
                case SDL_EventType.SDL_EVENT_QUIT:
                case SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED:
                    QuitRequested = true;
                    _queue.Enqueue(InputEvent.Quit);
                    break;

                case SDL_EventType.SDL_EVENT_KEY_DOWN:
                case SDL_EventType.SDL_EVENT_KEY_UP:
                {
                    var key = TranslateKey(sdlEvent.key.key);
                    var modifiers = TranslateModifiers(sdlEvent.key.mod);
                    var ascii = ToAscii(sdlEvent.key.key, sdlEvent.key.mod);
                    _queue.Enqueue(sdlEvent.key.down
                        ? InputEvent.KeyDown(key, modifiers, ascii)
                        : InputEvent.KeyUp(key, modifiers, ascii));
                    break;
                }

                case SDL_EventType.SDL_EVENT_MOUSE_MOTION:
                {
                    var (x, y) = _graphics.ToFramebuffer(sdlEvent.motion.x, sdlEvent.motion.y);
                    _queue.Enqueue(InputEvent.MouseMove(x, y));
                    break;
                }

                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN:
                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP:
                {
                    var (x, y) = _graphics.ToFramebuffer(sdlEvent.button.x, sdlEvent.button.y);
                    var button = sdlEvent.button.button switch
                    {
                        1 => MouseButton.Left,
                        2 => MouseButton.Middle,
                        3 => MouseButton.Right,
                        _ => MouseButton.None,
                    };
                    if (button == MouseButton.None)
                    {
                        break;
                    }

                    _queue.Enqueue(sdlEvent.button.down ? InputEvent.MouseDown(button, x, y) : InputEvent.MouseUp(button, x, y));
                    break;
                }
            }
        }
    }

    private static Key TranslateKey(SDL_Keycode keycode)
    {
        if (SpecialKeys.TryGetValue(keycode, out var key))
        {
            return key;
        }

        uint value = (uint)keycode;
        return value < 128 ? (Key)value : Key.None;
    }

    private static KeyModifiers TranslateModifiers(SDL_Keymod mod)
    {
        var result = KeyModifiers.None;
        if ((mod & SDL_Keymod.SDL_KMOD_CTRL) != 0)
        {
            result |= KeyModifiers.Ctrl;
        }

        if ((mod & SDL_Keymod.SDL_KMOD_ALT) != 0)
        {
            result |= KeyModifiers.Alt;
        }

        if ((mod & SDL_Keymod.SDL_KMOD_SHIFT) != 0)
        {
            result |= KeyModifiers.Shift;
        }

        if ((mod & SDL_Keymod.SDL_KMOD_GUI) != 0)
        {
            result |= KeyModifiers.Meta;
        }

        if ((mod & SDL_Keymod.SDL_KMOD_NUM) != 0)
        {
            result |= KeyModifiers.NumLock;
        }

        if ((mod & SDL_Keymod.SDL_KMOD_CAPS) != 0)
        {
            result |= KeyModifiers.CapsLock;
        }

        if ((mod & SDL_Keymod.SDL_KMOD_SCROLL) != 0)
        {
            result |= KeyModifiers.ScrollLock;
        }

        return result;
    }

    /// <summary>US keyboard layout translation of a key press to the character it types.</summary>
    private static char ToAscii(SDL_Keycode keycode, SDL_Keymod mod)
    {
        uint value = (uint)keycode;
        if (keycode == SDL_Keycode.SDLK_KP_ENTER)
        {
            return '\r';
        }

        if (value >= 128)
        {
            return '\0';
        }

        char c = (char)value;
        bool shift = (mod & SDL_Keymod.SDL_KMOD_SHIFT) != 0;
        bool caps = (mod & SDL_Keymod.SDL_KMOD_CAPS) != 0;

        if (c is >= 'a' and <= 'z')
        {
            return shift ^ caps ? char.ToUpperInvariant(c) : c;
        }

        if (!shift)
        {
            return c;
        }

        return c switch
        {
            '1' => '!',
            '2' => '@',
            '3' => '#',
            '4' => '$',
            '5' => '%',
            '6' => '^',
            '7' => '&',
            '8' => '*',
            '9' => '(',
            '0' => ')',
            '-' => '_',
            '=' => '+',
            '[' => '{',
            ']' => '}',
            '\\' => '|',
            ';' => ':',
            '\'' => '"',
            ',' => '<',
            '.' => '>',
            '/' => '?',
            '`' => '~',
            _ => c,
        };
    }
}
