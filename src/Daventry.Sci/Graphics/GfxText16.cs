using Daventry.Sci.Engine;

namespace Daventry.Sci.Graphics;

public enum TextAlignment
{
    Right = -1,
    Left = 0,
    Center = 1,
}

/// <summary>
/// Text measuring and drawing for SCI0 to SCI1.1: word wrapping, alignment and the SCI1.1 text
/// codes that switch font and colour inside a string. Port of ScummVM's text16.cpp without the
/// Mac, Japanese and Korean paths.
/// </summary>
public sealed class GfxText16
{
    private readonly GfxCache _cache;
    private readonly GfxPorts _ports;
    private readonly GfxPaint16 _paint16;
    private readonly GfxScreen _screen;
    private readonly SciVersion _version;
    private readonly bool _useEarlyGetLongestTextCalculations;
    private readonly List<SciRect> _codeRefRects = [];
    private int[] _codeFonts = [];
    private int[] _codeColors = [];
    private SciRect _codeRefTempRect;
    private GfxFontFromResource? _font;

    public GfxText16(GfxCache cache, GfxPorts ports, GfxPaint16 paint16, GfxScreen screen, SciVersion version, bool useEarlyGetLongestTextCalculations)
    {
        _cache = cache;
        _ports = ports;
        _paint16 = paint16;
        _screen = screen;
        _version = version;
        _useEarlyGetLongestTextCalculations = useEarlyGetLongestTextCalculations;
    }

    /// <summary>The segment manager, for the reference rectangles of Pepper's text codes.</summary>
    public SegManager? SegMan { get; set; }

    public int GetFontId() => _ports.CurPort.FontId;

    public GfxFontFromResource? GetFont()
    {
        if (_font is null || _font.ResourceId != _ports.CurPort.FontId)
        {
            _font = _cache.GetFont(_ports.CurPort.FontId);
        }

        return _font;
    }

    public void SetFont(int fontId)
    {
        if (_font is null || _font.ResourceId != fontId)
        {
            _font = _cache.GetFont(fontId);
        }

        if (_font is null)
        {
            return;
        }

        _ports.CurPort.FontId = _font.ResourceId;
        _ports.CurPort.FontHeight = _font.Height;
    }

    /// <summary>
    /// Handles a text code after a '|': c sets the colour, f the font, r marks a reference
    /// (Pepper). Returns the number of characters the code took.
    /// </summary>
    private int CodeProcessing(string text, ref int pos, int orgFontId, int orgPenColor, bool doingDrawing)
    {
        int textCode = pos;
        int textCodeSize = 1;
        while (pos < text.Length && text[pos++] != '|')
        {
            textCodeSize++;
        }

        if (textCode >= text.Length)
        {
            return textCodeSize;
        }

        char curCode = text[textCode];
        int curCodeParm = -1;
        if (textCode + 1 < text.Length && char.IsAsciiDigit(text[textCode + 1]))
        {
            int end = textCode + 1;
            while (end < text.Length && char.IsAsciiDigit(text[end]))
            {
                end++;
            }

            curCodeParm = (sbyte)int.Parse(text.AsSpan(textCode + 1, end - textCode - 1));
        }

        switch (curCode)
        {
            case 'c': // set text colour
                if (curCodeParm == -1)
                {
                    _ports.CurPort.PenClr = orgPenColor;
                }
                else if (curCodeParm < _codeColors.Length)
                {
                    _ports.CurPort.PenClr = _codeColors[curCodeParm];
                }

                break;
            case 'f': // set text font
                if (curCodeParm == -1)
                {
                    SetFont(orgFontId);
                }
                else if (curCodeParm < _codeFonts.Length)
                {
                    SetFont(_codeFonts[curCodeParm]);
                }

                break;
            case 'r': // reference (used in Pepper)
                if (doingDrawing)
                {
                    if (_codeRefTempRect.Top == -1)
                    {
                        _codeRefTempRect.Top = _ports.CurPort.CurTop;
                        _codeRefTempRect.Left = _ports.CurPort.CurLeft;
                    }
                    else
                    {
                        _codeRefTempRect.Bottom = _ports.CurPort.CurTop + _ports.CurPort.FontHeight;
                        _codeRefTempRect.Right = _ports.CurPort.CurLeft;
                        _codeRefRects.Add(_codeRefTempRect);
                        _codeRefTempRect.Left = _codeRefTempRect.Top = -1;
                    }
                }

                break;
        }

        return textCodeSize;
    }

    /// <summary>
    /// The number of characters that fit into <paramref name="maxWidth"/> with whole words, not
    /// counting the breaking space. Moves <paramref name="pos"/> to the start of the next line.
    /// </summary>
    public int GetLongest(string text, ref int pos, int maxWidth, int orgFontId)
    {
        int lastSpacePos = -1;
        int lastSpaceCharCount = 0;
        int curCharCount = 0;
        int resultCharCount;
        int curWidth = 0;
        int tempWidth = 0;
        int previousFontId = GetFontId();
        int previousPenColor = _ports.CurPort.PenClr;

        var font = GetFont();
        if (font is null)
        {
            return 0;
        }

        int curChar;
        while (true)
        {
            curChar = pos < text.Length ? text[pos] : 0;
            switch (curChar)
            {
                case 0x7C:
                    if (_version >= SciVersion.V1_1)
                    {
                        curCharCount++;
                        pos++;
                        curCharCount += CodeProcessing(text, ref pos, orgFontId, previousPenColor, false);
                        continue;
                    }

                    break;

                // 0xD, 0xA and 0xD 0xA count towards the line and end it, as Sierra did
                case 0xD:
                    if (pos + 1 < text.Length && text[pos + 1] == 0xA)
                    {
                        curCharCount++;
                        pos++;
                    }

                    goto case 0xA;
                case 0xA:
                    curCharCount++;
                    pos++;
                    goto case 0;
                case 0:
                    SetFont(previousFontId);
                    _ports.PenColor(previousPenColor);
                    return curCharCount;

                case ' ':
                    lastSpaceCharCount = curCharCount; // count up to (but not including) the breaking space
                    lastSpacePos = pos + 1; // right after the current space
                    break;
            }

            tempWidth += font.GetCharWidth(curChar);

            // too wide: break out
            if (tempWidth > maxWidth)
            {
                break;
            }

            // early interpreters used greater or equal while no space had been reached yet
            if (_useEarlyGetLongestTextCalculations && lastSpaceCharCount == 0 && tempWidth == maxWidth)
            {
                break;
            }

            curWidth = tempWidth;
            curCharCount++;
            pos++;
        }

        _ = curWidth;
        if (lastSpaceCharCount != 0)
        {
            // break at the last space, skipping the spaces that follow it
            resultCharCount = lastSpaceCharCount;
            pos = lastSpacePos;
            while (pos < text.Length && text[pos] == ' ')
            {
                pos++;
            }
        }
        else
        {
            // no space found: the very first word is split. Early interpreters counted the
            // character that exceeded the width (LB1 room 786, bug #10000)
            if (_useEarlyGetLongestTextCalculations)
            {
                curCharCount++;
                pos++;
            }

            resultCharCount = curCharCount;
        }

        SetFont(previousFontId);
        _ports.PenColor(previousPenColor);
        return resultCharCount;
    }

    public void Width(string text, int from, int len, int orgFontId, out int textWidth, out int textHeight, bool restoreFont)
    {
        int previousFontId = GetFontId();
        int previousPenColor = _ports.CurPort.PenClr;

        textWidth = 0;
        textHeight = 0;

        var font = GetFont();
        if (font is not null)
        {
            int pos = from;
            while (len-- > 0)
            {
                int curChar = pos < text.Length ? text[pos] : 0;
                pos++;
                switch (curChar)
                {
                    case 0x0A:
                    case 0x0D:
                        textHeight = Math.Max(textHeight, _ports.CurPort.FontHeight);
                        break;
                    case 0x7C:
                        // the pipe is a control character in SCI1.1, a normal one before
                        if (_version >= SciVersion.V1_1)
                        {
                            len -= CodeProcessing(text, ref pos, orgFontId, 0, false);
                            break;
                        }

                        goto default;
                    default:
                        textHeight = Math.Max(textHeight, _ports.CurPort.FontHeight);
                        textWidth += font.GetCharWidth(curChar);
                        break;
                }
            }
        }

        // the font is only restored when asked: when measuring, the code-modified font must
        // stay active for the following draw
        if (restoreFont)
        {
            SetFont(previousFontId);
            _ports.PenColor(previousPenColor);
        }
    }

    public void StringWidth(string str, int orgFontId, out int textWidth, out int textHeight)
        => Width(str, 0, str.Length, orgFontId, out textWidth, out textHeight, true);

    public void DrawString(string str, int orgFontId, int orgPenColor) => Draw(str, 0, str.Length, orgFontId, orgPenColor);

    /// <summary>Measures a text wrapped at <paramref name="maxWidth"/> (0 means 192, negative means one line).</summary>
    public int Size(ref SciRect rect, string text, ushort languageSplitter, int fontId, int maxWidth)
    {
        int previousFontId = GetFontId();
        int previousPenColor = _ports.CurPort.PenClr;
        int maxTextWidth = 0;
        _ = languageSplitter;

        if (fontId != -1)
        {
            SetFont(fontId);
        }
        else
        {
            fontId = previousFontId;
        }

        rect.Top = rect.Left = 0;

        if (maxWidth < 0)
        {
            // force output as a single line
            StringWidth(text, fontId, out int textWidth, out int textHeight);
            rect.Bottom = textHeight;
            rect.Right = textWidth;
        }
        else
        {
            // rect.Right is the widest line, rect.Bottom the number of lines times the font height
            rect.Right = maxWidth != 0 ? maxWidth : 192;
            int curTextPos = 0; // work position for GetLongest
            int curTextLine = 0; // start of the current line
            int totalHeight = 0;
            while (curTextPos < text.Length)
            {
                int charCount = GetLongest(text, ref curTextPos, rect.Right, fontId);
                if (charCount == 0)
                {
                    break;
                }

                Width(text, curTextLine, charCount, fontId, out int textWidth, out int textHeight, false);
                maxTextWidth = Math.Max(textWidth, maxTextWidth);
                totalHeight += textHeight;
                curTextLine = curTextPos;
            }

            rect.Bottom = totalHeight;
            rect.Right = maxWidth != 0 ? maxWidth : Math.Min(rect.Right, maxTextWidth);
        }

        SetFont(previousFontId);
        _ports.PenColor(previousPenColor);
        return rect.Right;
    }

    /// <summary>Draws characters at the current pen position.</summary>
    public void Draw(string text, int from, int len, int orgFontId, int orgPenColor)
    {
        var font = GetFont();
        if (font is null)
        {
            return;
        }

        var port = _ports.CurPort;
        var rect = new SciRect { Top = port.CurTop, Bottom = port.CurTop + port.FontHeight };
        int pos = from;
        while (len-- > 0)
        {
            int curChar = pos < text.Length ? text[pos] : 0;
            pos++;
            switch (curChar)
            {
                case 0x0A:
                case 0x0D:
                case 0:
                    break;
                case 0x7C:
                    if (_version >= SciVersion.V1_1)
                    {
                        len -= CodeProcessing(text, ref pos, orgFontId, orgPenColor, true);
                        break;
                    }

                    goto default;
                default:
                {
                    int charWidth = font.GetCharWidth(curChar);
                    // clear the character cell in pen mode 1
                    if (port.PenMode == 1)
                    {
                        rect.Left = port.CurLeft;
                        rect.Right = rect.Left + charWidth;
                        _paint16.EraseRect(rect);
                    }

                    font.Draw(curChar, port.Top + port.CurTop, port.Left + port.CurLeft, (byte)port.PenClr, port.GreyedOutput);
                    port.CurLeft += charWidth;
                    // the font may have changed through a text code
                    font = GetFont() ?? font;
                    break;
                }
            }
        }
    }

    public void Show(string text, int from, int len, int orgFontId, int orgPenColor)
    {
        var rect = new SciRect
        {
            Top = _ports.CurPort.CurTop,
            Bottom = _ports.CurPort.CurTop + _ports.GetPointSize(),
            Left = _ports.CurPort.CurLeft,
        };
        Draw(text, from, len, orgFontId, orgPenColor);
        rect.Right = _ports.CurPort.CurLeft;
        _paint16.BitsShow(rect);
    }

    /// <summary>Draws a text wrapped and aligned inside a rectangle.</summary>
    public void Box(string text, ushort languageSplitter, bool show, SciRect rect, TextAlignment alignment, int fontId)
    {
        int offset = 0;
        int hline = 0;
        int previousFontId = GetFontId();
        int previousPenColor = _ports.CurPort.PenClr;
        int curTextPos = 0;
        int curTextLine = 0;
        _ = languageSplitter;

        if (fontId != -1)
        {
            SetFont(fontId);
        }
        else
        {
            fontId = previousFontId;
        }

        // reset the reference code rects
        _codeRefRects.Clear();
        _codeRefTempRect.Left = _codeRefTempRect.Top = -1;

        while (curTextPos < text.Length)
        {
            int charCount = GetLongest(text, ref curTextPos, rect.Width, fontId);
            if (charCount == 0)
            {
                break;
            }

            Width(text, curTextLine, charCount, fontId, out int textWidth, out int textHeight, true);
            switch (alignment)
            {
                case TextAlignment.Right:
                    offset = rect.Width - textWidth;
                    break;
                case TextAlignment.Center:
                    offset = (rect.Width - textWidth) / 2;
                    break;
                case TextAlignment.Left:
                    offset = 0;
                    break;
            }

            _ports.MoveTo(rect.Left + offset, rect.Top + hline);

            if (show)
            {
                Show(text, curTextLine, charCount, fontId, previousPenColor);
            }
            else
            {
                Draw(text, curTextLine, charCount, fontId, previousPenColor);
            }

            hline += textHeight;
            curTextLine = curTextPos;
        }

        SetFont(previousFontId);
        _ports.PenColor(previousPenColor);
    }

    public void Box(string text, bool show, SciRect rect, TextAlignment alignment, int fontId) => Box(text, 0, show, rect, alignment, fontId);

    public void DrawString(string text)
    {
        int previousFontId = GetFontId();
        int previousPenColor = _ports.CurPort.PenClr;
        Draw(text, 0, text.Length, previousFontId, previousPenColor);
        SetFont(previousFontId);
        _ports.PenColor(previousPenColor);
    }

    /// <summary>Draws the status line without line break handling: KQ4's IV character is 0xA.</summary>
    public void DrawStatus(string str)
    {
        var font = GetFont();
        if (font is null)
        {
            return;
        }

        var port = _ports.CurPort;
        foreach (char curChar in str)
        {
            if (curChar == 0)
            {
                continue;
            }

            int charWidth = font.GetCharWidth(curChar);
            font.Draw(curChar, port.Top + port.CurTop, port.Left + port.CurLeft, (byte)port.PenClr, port.GreyedOutput);
            port.CurLeft += charWidth;
        }
    }

    /// <summary>The reference rectangles of the last Box as dynamic memory, terminated with 0x7777 words.</summary>
    public RegT AllocAndFillReferenceRectArray()
    {
        int rectCount = _codeRefRects.Count;
        if (rectCount == 0 || SegMan is null)
        {
            return RegT.Null;
        }

        var buffer = SegMan.AllocDynmem(4 * 2 * (rectCount + 1), "text code reference rects", out var rectArray);
        int offset = 0;
        foreach (var codeRect in _codeRefRects)
        {
            var curPort = _ports.GetPort();
            WriteUInt16(buffer, offset, codeRect.Left + curPort.Left);
            WriteUInt16(buffer, offset + 2, codeRect.Top + curPort.Top);
            WriteUInt16(buffer, offset + 4, codeRect.Right + curPort.Left);
            WriteUInt16(buffer, offset + 6, codeRect.Bottom + curPort.Top);
            offset += 8;
        }

        for (int i = 0; i < 4; i++)
        {
            WriteUInt16(buffer, offset + i * 2, 0x7777);
        }

        return rectArray;
    }

    private static void WriteUInt16(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }

    public void KernelTextSize(string text, ushort languageSplitter, int font, int maxWidth, out int textWidth, out int textHeight)
    {
        var rect = new SciRect(0, 0, 0, 0);
        Size(ref rect, text, languageSplitter, font, maxWidth);
        textWidth = rect.Width;
        textHeight = rect.Height;
    }

    /// <summary>The fonts the SCI1+ text codes refer to.</summary>
    public void KernelTextFonts(KernelArgs argv)
    {
        _codeFonts = new int[argv.Count];
        for (int i = 0; i < argv.Count; i++)
        {
            _codeFonts[i] = argv.UInt16(i);
        }
    }

    /// <summary>The colours the SCI1+ text codes refer to.</summary>
    public void KernelTextColors(KernelArgs argv)
    {
        _codeColors = new int[argv.Count];
        for (int i = 0; i < argv.Count; i++)
        {
            _codeColors[i] = argv.UInt16(i);
        }
    }
}
