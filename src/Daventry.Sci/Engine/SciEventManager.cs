using Daventry.Core.Input;
using Daventry.Sci.Graphics;

namespace Daventry.Sci.Engine;

/// <summary>An input event in the form the scripts expect. Mirrors SciEvent.</summary>
public struct SciEvent
{
    public int Type;
    public int Modifiers;
    public ushort Character;
    public SciPoint MousePos;
}

/// <summary>
/// Translates host input into SCI events and queues them for kGetEvent. Port of event.cpp.
/// The host pushes events with <see cref="Push"/> from the engine's tick.
/// </summary>
public sealed class SciEventManager
{
    private static readonly (int Offset, string Keys)[] ScancodeAltifyRows =
    [
        (0x10, "QWERTYUIOP[]"),
        (0x1E, "ASDFGHJKL;'\\"),
        (0x2C, "ZXCVBNM,./"),
    ];

    private static readonly byte[] CodePageMap88591ToDos =
    [
        (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?',
        (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?',
        (byte)'?', 0xad, 0x9b, 0x9c, (byte)'?', 0x9d, (byte)'?', 0x9e, (byte)'?', (byte)'?', 0xa6, 0xae, 0xaa, (byte)'?', (byte)'?', (byte)'?',
        (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?', 0xa7, 0xaf, 0xac, 0xab, (byte)'?', 0xa8,
        (byte)'?', (byte)'?', (byte)'?', (byte)'?', 0x8e, 0x8f, 0x92, 0x80, (byte)'?', 0x90, (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?',
        (byte)'?', 0xa5, (byte)'?', (byte)'?', (byte)'?', (byte)'?', 0x99, (byte)'?', (byte)'?', (byte)'?', (byte)'?', (byte)'?', 0x9a, (byte)'?', (byte)'?', 0xe1,
        0x85, 0xa0, 0x83, (byte)'?', 0x84, 0x86, 0x91, 0x87, 0x8a, 0x82, 0x88, 0x89, 0x8d, 0xa1, 0x8c, 0x8b,
        (byte)'?', 0xa4, 0x95, 0xa2, 0x93, (byte)'?', 0x94, (byte)'?', (byte)'?', 0x97, 0xa3, 0x96, 0x81, (byte)'?', (byte)'?', 0x98,
    ];

    private readonly List<SciEvent> _events = [];
    private readonly bool _fontIsExtended;
    private readonly SciVersion _version;
    private SciPoint _mousePos;
    private int _modifierState;

    public SciEventManager(bool fontIsExtended, SciVersion version)
    {
        _fontIsExtended = fontIsExtended;
        _version = version;
    }

    public SciPoint MousePos
    {
        get => _mousePos;
        set => _mousePos = value;
    }

    /// <summary>The current modifier keys, in SCI bits.</summary>
    public int ModifierState => _modifierState;

    private static int ToSciModifiers(KeyModifiers mods)
    {
        int result = 0;
        if ((mods & KeyModifiers.Alt) != 0)
        {
            result |= KernelFunctions.SciKeyModAlt;
        }

        if ((mods & KeyModifiers.Ctrl) != 0)
        {
            result |= KernelFunctions.SciKeyModCtrl;
        }

        if ((mods & KeyModifiers.Shift) != 0)
        {
            result |= KernelFunctions.SciKeyModShift;
        }

        return result;
    }

    /// <summary>The IBM keyboard alt-key scancode of a printable character.</summary>
    private static int Altify(char ch)
    {
        char c = char.ToUpperInvariant(ch);
        foreach (var (offset, keys) in ScancodeAltifyRows)
        {
            int pos = keys.IndexOf(c);
            if (pos >= 0)
            {
                return (offset + pos) << 8;
            }
        }

        return ch;
    }

    /// <summary>Feeds a host event. Mouse moves only update the position; everything else is queued.</summary>
    public void Push(InputEvent ev)
    {
        if (ev.Type == InputEventType.MouseMove)
        {
            _mousePos = new SciPoint(ev.X, ev.Y);
            return;
        }

        if (ev.Type is InputEventType.MouseDown or InputEventType.MouseUp)
        {
            _mousePos = new SciPoint(ev.X, ev.Y);
        }

        if (ev.Type == InputEventType.Quit)
        {
            _events.Add(new SciEvent { Type = KernelFunctions.SciEventQuit, MousePos = _mousePos });
            return;
        }

        var input = new SciEvent { Modifiers = ToSciModifiers(ev.Modifiers), MousePos = _mousePos };
        if (ev.Type is InputEventType.KeyDown or InputEventType.KeyUp)
        {
            _modifierState = input.Modifiers;
        }

        switch (ev.Type)
        {
            case InputEventType.MouseDown:
            case InputEventType.MouseUp:
                input.Type = ev.Type == InputEventType.MouseDown ? KernelFunctions.SciEventMousePress : KernelFunctions.SciEventMouseRelease;
                // Sierra set shift for the right button and control for the middle one in the mouse handler
                if (ev.Button == MouseButton.Right)
                {
                    input.Modifiers |= KernelFunctions.SciKeyModShift;
                }
                else if (ev.Button == MouseButton.Middle)
                {
                    input.Modifiers |= KernelFunctions.SciKeyModCtrl;
                }

                _events.Add(input);
                return;
            case InputEventType.KeyDown:
            case InputEventType.KeyUp:
                break;
            default:
                return;
        }

        // the IBM keyboard driver before SCI1.1 only sent key down events
        if (ev.Type != InputEventType.KeyDown && _version < SciVersion.V1_1)
        {
            return;
        }

        int character = ev.Ascii;
        if (character != 0 && character <= 0xFF)
        {
            // extended characters go to the DOS code page for multilingual games; others drop them
            if (character >= 0x80)
            {
                if (!_fontIsExtended)
                {
                    return;
                }

                character = CodePageMap88591ToDos[character & 0x7F];
            }

            if (ev.Key == Key.Tab)
            {
                character = (ev.Modifiers & KeyModifiers.Shift) != 0 ? 15 << 8 : 9;
            }

            if (ev.Key == Key.Delete)
            {
                character = 83 << 8;
            }
        }
        else if (ev.Key >= Key.F1 && ev.Key <= Key.F10)
        {
            int index = ev.Key - Key.F1;
            character = ((ev.Modifiers & KeyModifiers.Shift) != 0 ? 84 + index : 59 + index) << 8;
        }
        else
        {
            character = ev.Key switch
            {
                Key.Up => KernelFunctions.SciKeyUp,
                Key.Down => KernelFunctions.SciKeyDown,
                Key.Right => KernelFunctions.SciKeyRight,
                Key.Left => KernelFunctions.SciKeyLeft,
                Key.Insert => 82 << 8,
                Key.Home => KernelFunctions.SciKeyHome,
                Key.End => KernelFunctions.SciKeyEnd,
                Key.PageUp => KernelFunctions.SciKeyPageUp,
                Key.PageDown => KernelFunctions.SciKeyPageDown,
                Key.Delete => 83 << 8,
                Key.Keypad5 => KernelFunctions.SciKeyCenter,
                Key.Return => 13,
                Key.Escape => 27,
                Key.Backspace => 8,
                _ => 0,
            };
        }

        if ((ev.Modifiers & KeyModifiers.Alt) != 0 && character is > 0 and < 27)
        {
            character += 96; // a control character from the backend becomes the printable key
        }

        if ((ev.Modifiers & KeyModifiers.Alt) != 0)
        {
            character = Altify((char)(character & 0xFF));
        }
        else if ((ev.Modifiers & (KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Shift)) == KeyModifiers.Ctrl && character is >= 'a' and <= 'z')
        {
            character -= 96; // SSCI gave ASCII control characters for Ctrl+key
        }

        // in SCI1.1 a lone modifier key produces a key-up event
        if (_version != SciVersion.V1_1 && character == 0)
        {
            return;
        }

        if (character == 0 || ev.Type == InputEventType.KeyUp)
        {
            input.Type = KernelFunctions.SciEventKeyUp;
            if (_version == SciVersion.V1_1)
            {
                character = 0x8000; // Island of Dr Brain's word search needs this value for key-up
            }
        }
        else
        {
            input.Type = KernelFunctions.SciEventKeyDown;
        }

        input.Character = (ushort)character;
        _events.Add(input);
    }

    /// <summary>The first queued event matching the mask, removed unless peeking.</summary>
    public SciEvent GetSciEvent(int mask)
    {
        for (int i = 0; i < _events.Count; i++)
        {
            if ((_events[i].Type & mask) != 0)
            {
                var ev = _events[i];
                if ((mask & KernelFunctions.SciEventPeek) == 0)
                {
                    _events.RemoveAt(i);
                }

                return ev;
            }
        }

        return new SciEvent { Type = KernelFunctions.SciEventNone, Modifiers = _modifierState, MousePos = _mousePos };
    }

    public void FlushEvents() => _events.Clear();
}
