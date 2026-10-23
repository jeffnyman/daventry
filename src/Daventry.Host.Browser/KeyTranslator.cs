using Daventry.Core.Input;

namespace Daventry.Host.Browser;

/// <summary>
/// Turns a browser keyboard event into the engines' key code and the character it typed. The
/// browser already applied the keyboard layout to <c>key</c>, so the character comes from there;
/// the key code is the unshifted key the way the SDL host derives it from a US layout, which is
/// what the original interpreters assumed.
/// </summary>
public static class KeyTranslator
{
    /// <summary>Modifier flags as the JavaScript side packs them.</summary>
    [Flags]
    public enum Flags
    {
        None = 0,
        Ctrl = 1,
        Alt = 2,
        Shift = 4,
        Meta = 8,
        Repeat = 16,
        CapsLock = 32,
        NumLock = 64,
    }

    private static readonly Dictionary<string, Key> NamedKeys = new(StringComparer.Ordinal)
    {
        ["Enter"] = Key.Return,
        ["Escape"] = Key.Escape,
        ["Backspace"] = Key.Backspace,
        ["Tab"] = Key.Tab,
        ["Delete"] = Key.Delete,
        ["Insert"] = Key.Insert,
        ["Home"] = Key.Home,
        ["End"] = Key.End,
        ["PageUp"] = Key.PageUp,
        ["PageDown"] = Key.PageDown,
        ["ArrowUp"] = Key.Up,
        ["ArrowDown"] = Key.Down,
        ["ArrowLeft"] = Key.Left,
        ["ArrowRight"] = Key.Right,
        ["Clear"] = Key.Clear,
        ["Pause"] = Key.Pause,
        ["NumLock"] = Key.NumLock,
        ["CapsLock"] = Key.CapsLock,
        ["ScrollLock"] = Key.ScrollLock,
    };

    private static readonly Dictionary<string, Key> CodeKeys = new(StringComparer.Ordinal)
    {
        ["ShiftLeft"] = Key.LeftShift,
        ["ShiftRight"] = Key.RightShift,
        ["ControlLeft"] = Key.LeftCtrl,
        ["ControlRight"] = Key.RightCtrl,
        ["AltLeft"] = Key.LeftAlt,
        ["AltRight"] = Key.RightAlt,
        ["MetaLeft"] = Key.LeftMeta,
        ["MetaRight"] = Key.RightMeta,
        ["NumpadEnter"] = Key.KeypadEnter,
        ["NumpadDecimal"] = Key.KeypadPeriod,
        ["NumpadDivide"] = Key.KeypadDivide,
        ["NumpadMultiply"] = Key.KeypadMultiply,
        ["NumpadSubtract"] = Key.KeypadMinus,
        ["NumpadAdd"] = Key.KeypadPlus,
        ["NumpadEqual"] = Key.KeypadEquals,
        ["Space"] = Key.Space,
    };

    public static KeyModifiers ToModifiers(Flags flags)
    {
        var result = KeyModifiers.None;
        if ((flags & Flags.Ctrl) != 0)
        {
            result |= KeyModifiers.Ctrl;
        }

        if ((flags & Flags.Alt) != 0)
        {
            result |= KeyModifiers.Alt;
        }

        if ((flags & Flags.Shift) != 0)
        {
            result |= KeyModifiers.Shift;
        }

        if ((flags & Flags.Meta) != 0)
        {
            result |= KeyModifiers.Meta;
        }

        if ((flags & Flags.CapsLock) != 0)
        {
            result |= KeyModifiers.CapsLock;
        }

        if ((flags & Flags.NumLock) != 0)
        {
            result |= KeyModifiers.NumLock;
        }

        return result;
    }

    /// <summary>
    /// The key code and typed character for a browser event's <c>key</c> and <c>code</c>.
    /// <see cref="Key.None"/> and a null character mean the key is not one the engines know.
    /// </summary>
    public static (Key Key, char Ascii) Translate(string key, string code)
    {
        if (key.Length == 1)
        {
            char ch = key[0];
            char ascii = ch < 128 ? ch : '\0';
            return (KeyFromCode(code) ?? UnshiftedKey(ch), ascii);
        }

        if (NamedKeys.TryGetValue(key, out var named))
        {
            char ascii = named switch
            {
                Key.Return => '\r',
                Key.Escape => '\u001b',
                Key.Backspace => '\b',
                Key.Tab => '\t',
                _ => '\0',
            };
            return (named, ascii);
        }

        if (key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out int fn) && fn is >= 1 and <= 15)
        {
            return (Key.F1 + (fn - 1), '\0');
        }

        if (CodeKeys.TryGetValue(code, out var fromCode))
        {
            return (fromCode, fromCode == Key.KeypadEnter ? '\r' : '\0');
        }

        // Shift, Control, Alt and Meta without a side in the code, or anything else
        return (Key.None, '\0');
    }

    /// <summary>The physical key for a printable character, so that Shift+1 is still the 1 key.</summary>
    private static Key? KeyFromCode(string code)
    {
        if (code.Length == 4 && code.StartsWith("Key", StringComparison.Ordinal))
        {
            return Key.A + (char.ToLowerInvariant(code[3]) - 'a');
        }

        if (code.Length == 6 && code.StartsWith("Digit", StringComparison.Ordinal))
        {
            return Key.D0 + (code[5] - '0');
        }

        if (code.Length == 7 && code.StartsWith("Numpad", StringComparison.Ordinal) && char.IsAsciiDigit(code[6]))
        {
            return Key.Keypad0 + (code[6] - '0');
        }

        return CodeKeys.TryGetValue(code, out var key) ? key : null;
    }

    /// <summary>The unshifted US key for a typed character, for layouts whose codes do not map.</summary>
    private static Key UnshiftedKey(char ch)
    {
        char lower = ch switch
        {
            '!' => '1',
            '@' => '2',
            '#' => '3',
            '$' => '4',
            '%' => '5',
            '^' => '6',
            '&' => '7',
            '*' => '8',
            '(' => '9',
            ')' => '0',
            '_' => '-',
            '+' => '=',
            '{' => '[',
            '}' => ']',
            '|' => '\\',
            ':' => ';',
            '"' => '\'',
            '<' => ',',
            '>' => '.',
            '?' => '/',
            '~' => '`',
            _ => char.ToLowerInvariant(ch),
        };

        return lower < 128 ? (Key)lower : Key.None;
    }
}
