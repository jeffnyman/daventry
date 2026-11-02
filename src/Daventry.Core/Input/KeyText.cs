namespace Daventry.Core.Input;

/// <summary>
/// Typing for hosts that make their own key events (scripted runs, tests, on-screen keyboards).
/// Games read typed text from <see cref="InputEvent.Ascii"/>, not from <see cref="InputEvent.Key"/>:
/// a key event without its character types nothing.
/// </summary>
public static class KeyText
{
    /// <summary>
    /// The key, modifiers and character a character is typed with on a US keyboard: 'a' is
    /// <see cref="Key.A"/>, 'A' is <see cref="Key.A"/> with Shift, '!' is <see cref="Key.D1"/> with
    /// Shift, and '\r' or '\n' is <see cref="Key.Return"/>. A character outside ASCII is typed with
    /// no key and no character, which games ignore.
    /// </summary>
    public static (Key Key, KeyModifiers Modifiers, char Ascii) FromChar(char c)
    {
        switch (c)
        {
            case '\r' or '\n':
                return (Key.Return, KeyModifiers.None, '\r');
            case '\b':
                return (Key.Backspace, KeyModifiers.None, '\b');
            case '\t':
                return (Key.Tab, KeyModifiers.None, '\t');
            case '\u001b':
                return (Key.Escape, KeyModifiers.None, '\u001b');
        }

        if (c is < ' ' or > '~')
        {
            return (Key.None, KeyModifiers.None, '\0');
        }

        if (c is >= 'A' and <= 'Z')
        {
            return (Key.A + (c - 'A'), KeyModifiers.Shift, c);
        }

        // the shifted symbols of a US keyboard, by the key they are on; ScummVM's key codes are
        // ASCII for the unshifted keys, so everything else is its own key
        char unshifted = c switch
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
            _ => c,
        };

        return ((Key)unshifted, unshifted == c ? KeyModifiers.None : KeyModifiers.Shift, c);
    }
}
