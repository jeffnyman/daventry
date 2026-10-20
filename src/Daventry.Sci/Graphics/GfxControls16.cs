using Daventry.Sci.Engine;

namespace Daventry.Sci.Graphics;

public static class ControlStyle
{
    public const int Enabled = 0x0001;
    public const int Disabled = 0x0004;
    public const int Selected = 0x0008;
}

public static class ControlType
{
    public const int Button = 1;
    public const int Text = 2;
    public const int TextEdit = 3;
    public const int Icon = 4;
    public const int List = 6;
    public const int ListAlias = 7;
    public const int Dummy = 10;
}

/// <summary>
/// Buttons, text, edit fields, icons and lists of the SCI0 to SCI1.1 dialogs. Port of ScummVM's
/// controls16.cpp without the Mac font and right-to-left paths.
/// </summary>
public sealed class GfxControls16
{
    private const string ListUpArrow = "\u0018";
    private const string ListDownArrow = "\u0019";

    private readonly EngineState _s;
    private readonly GfxPorts _ports;
    private readonly GfxPaint16 _paint16;
    private readonly GfxText16 _text16;
    private readonly GfxScreen _screen;
    private SciRect _texteditCursorRect;
    private bool _texteditCursorVisible;
    private uint _texteditBlinkTime;

    public GfxControls16(EngineState s)
    {
        _s = s;
        _ports = s.Ports;
        _paint16 = s.Paint;
        _text16 = s.Text;
        _screen = s.Screen;
    }

    private int GetPicNotValid() => _s.Version >= SciVersion.V1_1 ? _screen.PicNotValidSci11 : _screen.PicNotValid;

    private void DrawListControl(SciRect rect, int maxChars, string[] entries, int fontId, int upperPos, int cursorPos, bool isAlias)
    {
        var workerRect = rect;
        int oldFontId = _text16.GetFontId();
        int oldPenColor = _ports.CurPort.PenClr;

        // the basic window
        _paint16.EraseRect(workerRect);
        workerRect.Grow(1);
        _paint16.FrameRect(workerRect);

        // the up and down arrows; the up arrow sits a pixel lower than in Sierra's version
        // because it looks nicer, and SQ4 "fixed" that by moving the character itself
        if (_s.GameId != SciGameId.Sq4)
        {
            workerRect.Top++;
        }

        _text16.Box(ListUpArrow, false, workerRect, TextAlignment.Center, 0);
        workerRect.Top = workerRect.Bottom - 10;
        _text16.Box(ListDownArrow, false, workerRect, TextAlignment.Center, 0);

        // the inner lines
        workerRect.Top = rect.Top + 9;
        workerRect.Bottom -= 10;
        _paint16.FrameRect(workerRect);
        workerRect.Grow(-1);

        _text16.SetFont(fontId);
        int fontSize = _ports.CurPort.FontHeight;
        _ports.PenColor(_ports.CurPort.PenClr);
        _ports.BackColor(_ports.CurPort.BackClr);
        workerRect.Bottom = workerRect.Top + fontSize;
        int lastYpos = rect.Bottom - fontSize;

        // the entries
        for (int i = upperPos; i < entries.Length; i++)
        {
            _paint16.EraseRect(workerRect);
            string listEntry = entries[i];
            if (listEntry.Length > 0)
            {
                _ports.MoveTo(workerRect.Left, workerRect.Top);
                _text16.Draw(listEntry, 0, Math.Min(maxChars, listEntry.Length), oldFontId, oldPenColor);
                if (!isAlias && i == cursorPos)
                {
                    _paint16.InvertRect(workerRect);
                }
            }

            workerRect.Translate(0, fontSize);
            if (workerRect.Bottom > lastYpos)
            {
                break;
            }
        }

        _text16.SetFont(oldFontId);
    }

    private void TexteditCursorDraw(SciRect rect, string text, int curPos)
    {
        if (_texteditCursorVisible)
        {
            return;
        }

        var font = _text16.GetFont();
        if (font is null)
        {
            return;
        }

        int textWidth = 0;
        for (int i = 0; i < curPos && i < text.Length; i++)
        {
            textWidth += font.GetCharWidth(text[i]);
        }

        _texteditCursorRect.Left = rect.Left + textWidth;
        _texteditCursorRect.Top = rect.Top;
        _texteditCursorRect.Bottom = _texteditCursorRect.Top + font.Height;
        _texteditCursorRect.Right = _texteditCursorRect.Left + (curPos >= text.Length ? 1 : font.GetCharWidth(text[curPos]));
        _paint16.InvertRect(_texteditCursorRect);
        _paint16.BitsShow(_texteditCursorRect);
        _texteditCursorVisible = true;
        TexteditSetBlinkTime();
    }

    private void TexteditCursorErase()
    {
        if (_texteditCursorVisible)
        {
            _paint16.InvertRect(_texteditCursorRect);
            _paint16.BitsShow(_texteditCursorRect);
            _texteditCursorVisible = false;
        }

        TexteditSetBlinkTime();
    }

    private void TexteditSetBlinkTime() => _texteditBlinkTime = _s.Millis() + 30 * 1000 / 60;

    /// <summary>Applies a key event to an edit control and redraws it. Port of kernelTexteditChange.</summary>
    public void KernelTexteditChange(RegT controlObject, RegT eventObject)
    {
        var sel = _s.Kernel.Selectors;
        var segMan = _s.SegMan;
        int cursorPos = Selectors.ReadSelectorValue(_s, controlObject, sel.Cursor);
        int maxChars = Selectors.ReadSelectorValue(_s, controlObject, sel.Max);
        var textReference = Selectors.ReadSelector(_s, controlObject, sel.Text);
        int eventKey = 0;
        int modifiers = 0;
        bool textChanged = false;
        bool textAddChar = false;

        if (textReference.IsNull)
        {
            throw new SciVmException("kEditControl called on object that doesn't have a text reference");
        }

        string text = segMan.GetString(textReference);

        if (!eventObject.IsNull)
        {
            int textSize = text.Length;
            int eventType = Selectors.ReadSelectorValue(_s, eventObject, sel.Type);

            if (eventType == KernelFunctions.SciEventKeyDown)
            {
                eventKey = Selectors.ReadSelectorValue(_s, eventObject, sel.Message);
                modifiers = Selectors.ReadSelectorValue(_s, eventObject, sel.Modifiers);
                switch (eventKey)
                {
                    case 8: // backspace
                        if (cursorPos > 0)
                        {
                            cursorPos--;
                            text = text.Remove(cursorPos, 1);
                            textChanged = true;
                        }

                        break;
                    case 83 << 8: // delete
                        if (cursorPos < textSize)
                        {
                            text = text.Remove(cursorPos, 1);
                            textChanged = true;
                        }

                        break;
                    case KernelFunctions.SciKeyHome:
                        cursorPos = 0;
                        textChanged = true;
                        break;
                    case KernelFunctions.SciKeyEnd:
                        cursorPos = textSize;
                        textChanged = true;
                        break;
                    case KernelFunctions.SciKeyLeft:
                        if (cursorPos > 0)
                        {
                            cursorPos--;
                            textChanged = true;
                        }

                        break;
                    case KernelFunctions.SciKeyRight:
                        if (cursorPos + 1 <= textSize)
                        {
                            cursorPos++;
                            textChanged = true;
                        }

                        break;
                    case 3: // Control-C erases the whole line
                        if ((modifiers & KernelFunctions.SciKeyModCtrl) != 0)
                        {
                            cursorPos = 0;
                            text = string.Empty;
                            textChanged = true;
                        }

                        break;
                    default:
                        if ((modifiers & KernelFunctions.SciKeyModCtrl) != 0 && eventKey == 99)
                        {
                            // Control-C in earlier games (SCI0 to SCI1 middle) erases the whole line
                            cursorPos = 0;
                            text = string.Empty;
                            textChanged = true;
                        }
                        else if (eventKey is > 31 and < 256 && textSize < maxChars)
                        {
                            // insert the pressed character
                            textAddChar = true;
                            textChanged = true;
                        }

                        break;
                }
            }
        }

        if (textChanged)
        {
            int oldFontId = _text16.GetFontId();
            int fontId = Selectors.ReadSelectorValue(_s, controlObject, sel.Font);
            var rect = _s.Compare.GetNSRect(controlObject);

            _text16.SetFont(fontId);
            if (textAddChar)
            {
                var font = _text16.GetFont();
                // does the new character fit?
                int textWidth = 0;
                foreach (char ch in text)
                {
                    textWidth += font?.GetCharWidth(ch) ?? 0;
                }

                textWidth += font?.GetCharWidth(eventKey) ?? 0;
                if (textWidth >= rect.Width)
                {
                    _text16.SetFont(oldFontId);
                    return;
                }

                text = text.Insert(cursorPos++, ((char)eventKey).ToString());
            }

            TexteditCursorErase();
            _paint16.EraseRect(rect);
            _text16.Box(text, false, rect, TextAlignment.Left, -1);
            _paint16.BitsShow(rect);
            TexteditCursorDraw(rect, text, cursorPos);
            _text16.SetFont(oldFontId);
            segMan.Strcpy(textReference, text);
        }
        else if (_s.Millis() >= _texteditBlinkTime)
        {
            _paint16.InvertRect(_texteditCursorRect);
            _paint16.BitsShow(_texteditCursorRect);
            _texteditCursorVisible = !_texteditCursorVisible;
            TexteditSetBlinkTime();
        }

        Selectors.WriteSelectorValue(_s, controlObject, sel.Cursor, cursorPos);
    }

    public void KernelDrawButton(SciRect rect, string text, ushort languageSplitter, int fontId, int style, bool hilite)
    {
        if (!hilite)
        {
            int sci0EarlyPen = 0;
            int sci0EarlyBack = 0;
            if (_s.Version == SciVersion.V0Early)
            {
                // SCI0 early used hardcoded green and black buttons instead of the port colours
                sci0EarlyPen = _ports.CurPort.PenClr;
                sci0EarlyBack = _ports.CurPort.BackClr;
                _ports.PenColor(0);
                _ports.BackColor(2);
            }

            rect.Grow(1);
            _paint16.EraseRect(rect);
            _paint16.FrameRect(rect);
            rect.Grow(-2);
            _ports.TextGreyedOutput((style & ControlStyle.Enabled) == 0);
            _text16.Box(text, languageSplitter, false, rect, TextAlignment.Center, fontId);
            _ports.TextGreyedOutput(false);
            rect.Grow(1);
            if ((style & ControlStyle.Selected) != 0)
            {
                _paint16.FrameRect(rect);
            }

            if (GetPicNotValid() == 0)
            {
                rect.Grow(1);
                _paint16.BitsShow(rect);
            }

            if (_s.Version == SciVersion.V0Early)
            {
                _ports.PenColor(sci0EarlyPen);
                _ports.BackColor(sci0EarlyBack);
            }
        }
        else
        {
            // SCI0 early inverted buttons with xor, which gave pink and white buttons
            if (_s.Version == SciVersion.V0Early)
            {
                _paint16.InvertRectViaXor(rect);
            }
            else
            {
                _paint16.InvertRect(rect);
            }

            _paint16.BitsShow(rect);
        }
    }

    public void KernelDrawText(SciRect rect, string text, ushort languageSplitter, int fontId, TextAlignment alignment, int style, bool hilite)
    {
        if (!hilite)
        {
            rect.Grow(1);
            _paint16.EraseRect(rect);
            rect.Grow(-1);
            _text16.Box(text, languageSplitter, false, rect, alignment, fontId);
            if ((style & ControlStyle.Selected) != 0)
            {
                _paint16.FrameRect(rect);
            }

            if (GetPicNotValid() == 0)
            {
                _paint16.BitsShow(rect);
            }
        }
        else
        {
            if (_s.Version == SciVersion.V0Early)
            {
                _paint16.InvertRectViaXor(rect);
            }
            else
            {
                _paint16.InvertRect(rect);
            }

            _paint16.BitsShow(rect);
        }
    }

    public void KernelDrawTextEdit(SciRect rect, string text, ushort languageSplitter, int fontId, int style, int cursorPos)
    {
        var textRect = rect;
        int oldFontId = _text16.GetFontId();

        rect.Grow(1);
        _texteditCursorVisible = false;
        TexteditCursorErase();
        _paint16.EraseRect(rect);
        _text16.Box(text, languageSplitter, false, textRect, TextAlignment.Left, fontId);
        _paint16.FrameRect(rect);
        if ((style & ControlStyle.Selected) != 0)
        {
            _text16.SetFont(fontId);
            rect.Grow(-1);
            TexteditCursorDraw(rect, text, cursorPos);
            _text16.SetFont(oldFontId);
            rect.Grow(1);
        }

        if (GetPicNotValid() == 0)
        {
            _paint16.BitsShow(rect);
        }
    }

    public void KernelDrawIcon(SciRect rect, int viewId, int loopNo, int celNo, int priority, int style, bool hilite)
    {
        if (!hilite)
        {
            _paint16.DrawCelAndShow(viewId, loopNo, celNo, rect.Left, rect.Top, (byte)priority, 0);
            if ((style & 0x20) != 0)
            {
                _paint16.FrameRect(rect);
            }

            if (GetPicNotValid() == 0)
            {
                _paint16.BitsShow(rect);
            }
        }
        else
        {
            if (_s.Version == SciVersion.V0Early)
            {
                _paint16.InvertRectViaXor(rect);
            }
            else
            {
                _paint16.InvertRect(rect);
            }

            _paint16.BitsShow(rect);
        }
    }

    public void KernelDrawList(SciRect rect, int maxChars, string[] entries, int fontId, int style, int upperPos, int cursorPos, bool isAlias, bool hilite)
    {
        if (hilite)
        {
            return;
        }

        DrawListControl(rect, maxChars, entries, fontId, upperPos, cursorPos, isAlias);
        rect.Grow(1);
        if (isAlias && (style & ControlStyle.Selected) != 0)
        {
            _paint16.FrameRect(rect);
        }

        if (GetPicNotValid() == 0)
        {
            _paint16.BitsShow(rect);
        }
    }
}
