using Daventry.Core.Hosting;
using Daventry.Sci.Engine;

namespace Daventry.Sci.Graphics;

public enum TextAlign
{
    Default = -1,
    Left = 0,
    Center = 1,
    Right = 2,
}

public enum ScrollDirection
{
    Up,
    Down,
}

/// <summary>
/// Measures and renders text for SCI32 into bitmaps. The wrapping works as in SCI16, but the
/// drawing parameters persist between calls instead of being passed every time. Port of
/// ScummVM's GfxText32.
/// </summary>
public sealed class GfxText32
{
    public const int SystemFont = -1;

    private readonly Sci32Graphics _g;
    private int _width;
    private int _height;
    private byte _foreColor;
    private byte _backColor;
    private byte _skipColor;
    private SciRect _textRect;
    private byte[] _text = [];
    private int _fontId;
    private int _borderColor;
    private bool _dimmed;
    private TextAlign _alignment;
    private SciPoint _drawPosition;
    private GfxFontFromResource? _font;

    public GfxText32(Sci32Graphics g)
    {
        _g = g;
        _fontId = SystemFont;
        _font = g.GetFont(SystemFont);
        XResolution = g.Frameout.ScriptWidth;
        YResolution = g.Frameout.ScriptHeight;
    }

    /// <summary>The bitmap being drawn into.</summary>
    public RegT Bitmap { get; private set; }

    /// <summary>The coordinate system of the text renderer; the scripts change it with kSetFontRes.</summary>
    public int XResolution { get; set; }

    public int YResolution { get; set; }

    public GfxFontFromResource Font => _font ?? throw new SciVmException($"Font {_fontId} not found");

    private static byte[] Latin1(string text) => System.Text.Encoding.Latin1.GetBytes(text);

    private SciRect ScaleRect(SciRect rect)
    {
        var scaled = rect;
        Rect32Math.MulInc(ref scaled, new Ratio(XResolution, _g.Frameout.ScriptWidth), new Ratio(YResolution, _g.Frameout.ScriptHeight));
        return scaled;
    }

    /// <summary>A text bitmap over a flat colour.</summary>
    public RegT CreateFontBitmap(int width, int height, SciRect rect, string text, byte foreColor, byte backColor, byte skipColor, int fontId, TextAlign alignment, int borderColor, bool dimmed, bool doScaling, bool gc)
    {
        _borderColor = borderColor;
        _text = Latin1(text);
        _textRect = rect;
        _width = width;
        _height = height;
        _foreColor = foreColor;
        _backColor = backColor;
        _skipColor = skipColor;
        _alignment = alignment;
        _dimmed = dimmed;
        SetFont(fontId);

        if (doScaling)
        {
            var scaleX = new Ratio(XResolution, _g.Frameout.ScriptWidth);
            var scaleY = new Ratio(YResolution, _g.Frameout.ScriptHeight);
            _width = (_width * scaleX).ToInt();
            _height = (_height * scaleY).ToInt();
            Rect32Math.MulInc(ref _textRect, scaleX, scaleY);
        }

        // the text rectangle is where the text goes inside the bitmap
        var bitmapRect = new SciRect(_width, _height);
        if (_textRect.Intersects(bitmapRect))
        {
            _textRect.Clip(bitmapRect);
        }
        else
        {
            _textRect = default;
        }

        _g.SegMan.AllocateBitmap(out var bitmap, _width, _height, _skipColor, 0, 0, XResolution, YResolution, 0, false, gc);
        Bitmap = bitmap;
        Erase(bitmapRect, false);
        if (_borderColor > -1)
        {
            DrawFrame(bitmapRect, 1, (byte)_borderColor, false);
        }

        DrawTextBox();
        return Bitmap;
    }

    /// <summary>A text bitmap over a view cel.</summary>
    public RegT CreateFontBitmap(in CelInfo32 celInfo, SciRect rect, string text, int foreColor, int backColor, int fontId, int skipColor, int borderColor, bool dimmed, bool gc)
    {
        _borderColor = borderColor;
        _text = Latin1(text);
        _textRect = rect;
        _foreColor = (byte)foreColor;
        _dimmed = dimmed;
        SetFont(fontId);

        Rect32Math.MulInc(ref _textRect, new Ratio(XResolution, _g.Frameout.ScriptWidth), new Ratio(YResolution, _g.Frameout.ScriptHeight));

        var view = new CelObjView(_g, celInfo.ResourceId, celInfo.LoopNo, celInfo.CelNo);
        _skipColor = view.SkipColor;
        _width = view.Width * XResolution / view.XResolution;
        _height = view.Height * YResolution / view.YResolution;

        var bitmapRect = new SciRect(_width, _height);
        if (_textRect.Intersects(bitmapRect))
        {
            _textRect.Clip(bitmapRect);
        }
        else
        {
            _textRect = default;
        }

        var bitmap = _g.SegMan.AllocateBitmap(out var bitmapId, _width, _height, _skipColor, 0, 0, XResolution, YResolution, 0, false, gc);
        Bitmap = bitmapId;

        // Sierra filled the pixels with 11 and then erased them with the skip colour
        _backColor = _skipColor;
        Erase(bitmapRect, false);
        _backColor = (byte)backColor;

        view.Draw(new PixelBuffer(bitmap.RawData, bitmap.PixelsOffset, bitmap.Width, bitmap.Height), bitmapRect, new SciPoint(0, 0), false, new Ratio(XResolution, view.XResolution), new Ratio(YResolution, view.YResolution));

        if (_backColor != skipColor && _foreColor != skipColor)
        {
            Erase(_textRect, false);
        }

        if (text.Length > 0)
        {
            if (_foreColor == skipColor)
            {
                throw new SciVmException("TODO: Implement transparent text");
            }

            if (borderColor != -1)
            {
                DrawFrame(bitmapRect, 1, (byte)_borderColor, false);
            }

            DrawTextBox();
        }

        return Bitmap;
    }

    /// <summary>A text bitmap with a title bar over a flat colour.</summary>
    public RegT CreateTitledFontBitmap(int width, int height, SciRect textRect, string text, byte foreColor, byte backColor, byte skipColor, int fontId, TextAlign alignment, int borderColor, string title, byte titleForeColor, byte titleBackColor, int titleFontId, bool doScaling, bool gc)
    {
        _borderColor = borderColor;
        _width = width;
        _height = height;
        _skipColor = skipColor;
        SetFont(titleFontId);

        var scaleX = new Ratio(XResolution, _g.Frameout.ScriptWidth);
        var scaleY = new Ratio(YResolution, _g.Frameout.ScriptHeight);
        if (doScaling)
        {
            _width = (_width * scaleX).ToInt();
            _height = (_height * scaleY).ToInt();
        }

        _text = Latin1(title);
        GetTextDimensions(0, 10000, out int titleWidth, out int titleHeight);
        if (_g.Version < SciVersion.V3)
        {
            titleHeight = _g.GetFont(titleFontId)?.Height ?? titleHeight;
        }

        titleWidth += 2;
        titleHeight += 1;
        if (borderColor != -1)
        {
            titleWidth += 2;
            titleHeight += 2;
        }

        _g.SegMan.AllocateBitmap(out var bitmapId, _width, _height, _skipColor, 0, 0, XResolution, YResolution, 0, false, gc);
        Bitmap = bitmapId;

        // the background
        _backColor = backColor;
        _textRect = new SciRect(0, 0, width, height);
        Erase(_textRect, false);

        // the title background
        _foreColor = titleForeColor;
        _backColor = titleBackColor;
        _alignment = TextAlign.Center;
        _dimmed = false;
        _textRect.SetHeight(titleHeight);
        Erase(_textRect, false);

        // the title border
        if (borderColor != -1)
        {
            DrawFrame(_textRect, 1, (byte)borderColor, false);
            _textRect.Grow(-2);
        }

        DrawTextBox();

        SetFont(fontId);
        _text = Latin1(text);
        _foreColor = foreColor;
        _backColor = backColor;
        _alignment = alignment;
        _textRect = textRect;
        if (doScaling)
        {
            Rect32Math.MulInc(ref _textRect, scaleX, scaleY);
        }

        // the text border
        var textBorderRect = new SciRect(0, titleHeight - 1, _width, _height);
        _textRect.Clip(textBorderRect);
        if (borderColor != -1)
        {
            DrawFrame(textBorderRect, 1, (byte)borderColor, false);
        }

        if (_textRect.Height >= Font.Height)
        {
            DrawTextBox();
        }

        return Bitmap;
    }

    /// <summary>Selects the font used for drawing and measuring.</summary>
    public void SetFont(int fontId)
    {
        if (fontId != _fontId)
        {
            _fontId = fontId;
            _font = _g.GetFont(fontId);
        }
    }

    private void DrawFrame(SciRect rect, int size, byte color, bool doScaling)
    {
        var targetRect = doScaling ? ScaleRect(rect) : rect;
        var bitmap = _g.SegMan.LookupBitmap(Bitmap);
        var pixels = bitmap.RawData;
        int pos = bitmap.PixelsOffset + rect.Top * _width + rect.Left;

        // not fully disassembled, but right in every case seen
        int rectWidth = targetRect.Width;
        int heightRemaining = targetRect.Height;
        int sidesHeight = heightRemaining - size * 2;
        int centerWidth = rectWidth - size * 2;
        int stride = _width - rectWidth;

        for (int y = 0; y < size && y < heightRemaining; y++)
        {
            pixels.AsSpan(pos, rectWidth).Fill(color);
            pos += _width;
            heightRemaining--;
        }

        for (int y = 0; y < sidesHeight; y++)
        {
            for (int x = 0; x < size; x++)
            {
                pixels[pos++] = color;
            }

            pos += centerWidth;
            for (int x = 0; x < size; x++)
            {
                pixels[pos++] = color;
            }

            pos += stride;
        }

        for (int y = 0; y < size && y < heightRemaining; y++)
        {
            pixels.AsSpan(pos, rectWidth).Fill(color);
            pos += _width;
            heightRemaining--;
        }
    }

    private void DrawChar(int charIndex)
    {
        var bitmap = _g.SegMan.LookupBitmap(Bitmap);
        Font.DrawToBuffer(charIndex, _drawPosition.Y, _drawPosition.X, _foreColor, _dimmed, bitmap.RawData, bitmap.PixelsOffset, _width, _height);
        _drawPosition.X += Font.GetCharWidth(charIndex);
    }

    /// <summary>The height of the current font in script pixels.</summary>
    public int GetScaledFontHeight() => (Font.Height * _g.Frameout.ScriptHeight + YResolution - 1) / YResolution;

    public int ScaleUpWidth(int value) => (value * _g.Frameout.ScriptWidth + XResolution - 1) / XResolution;

    public int ScaleUpHeight(int value) => (value * _g.Frameout.ScriptHeight + YResolution - 1) / YResolution;

    public int GetCharWidth(int charIndex, bool doScaling)
    {
        int width = Font.GetCharWidth(charIndex);
        return doScaling ? ScaleUpWidth(width) : width;
    }

    /// <summary>Draws the current text into the current bitmap, wrapped and aligned.</summary>
    public void DrawTextBox()
    {
        if (_text.Length == 0)
        {
            return;
        }

        int textRectWidth = _textRect.Width;
        _drawPosition.Y = _textRect.Top;
        int charIndex = 0;
        if (_g.GameId is SciGameId.Sq6 or SciGameId.Mothergoosehires)
        {
            if (GetLongest(ref charIndex, textRectWidth) == 0)
            {
                throw new SciVmException("DrawTextBox GetLongest=0");
            }
        }

        charIndex = 0;
        int nextCharIndex = 0;
        while (charIndex < _text.Length && _text[charIndex] != 0)
        {
            _drawPosition.X = _textRect.Left;
            int length = GetLongest(ref nextCharIndex, textRectWidth);
            int textWidth = GetTextWidth(charIndex, length);
            if (_alignment == TextAlign.Center)
            {
                _drawPosition.X += (textRectWidth - textWidth) / 2;
            }
            else if (_alignment == TextAlign.Right)
            {
                _drawPosition.X += textRectWidth - textWidth;
            }

            DrawText(charIndex, length);
            charIndex = nextCharIndex;
            _drawPosition.Y += Font.Height;
        }
    }

    public void DrawTextBox(string text)
    {
        _text = Latin1(text);
        DrawTextBox();
    }

    /// <summary>
    /// Reads a control code after a '|': the code letter, its number for a, c and f codes, and
    /// skips to the closing '|'. Returns false when the code runs off the end of the text.
    /// </summary>
    private bool ReadStyleControl(ref int pos, ref int length, out char controlChar, out int value)
    {
        controlChar = (char)_text[pos++];
        length--;
        value = 0;
        if (length == 0)
        {
            return false;
        }

        if (controlChar is 'a' or 'c' or 'f')
        {
            while (length > 0)
            {
                byte valueChar = _text[pos];
                if (valueChar < '0' || valueChar > '9')
                {
                    break;
                }

                pos++;
                length--;
                value = 10 * value + (valueChar - '0');
            }

            if (length == 0)
            {
                return false;
            }
        }

        while (length > 0 && _text[pos] != '|')
        {
            pos++;
            length--;
        }

        if (length > 0)
        {
            pos++;
            length--;
        }

        return true;
    }

    private void DrawText(int index, int length)
    {
        if (index + length > _text.Length)
        {
            throw new SciVmException("GfxText32::drawText past the end of the text");
        }

        int pos = index;
        while (length-- > 0)
        {
            int currentChar = _text[pos++];
            if (currentChar == '|')
            {
                if (!ReadStyleControl(ref pos, ref length, out char controlChar, out int value))
                {
                    return;
                }

                if (controlChar == 'a')
                {
                    _alignment = (TextAlign)value;
                }
                else if (controlChar == 'c')
                {
                    _foreColor = (byte)value;
                }
                else if (controlChar == 'f')
                {
                    SetFont(value);
                }
            }
            else
            {
                DrawChar(currentChar);
            }
        }
    }

    /// <summary>Swaps the fore and back colours within a rectangle of a bitmap (the caret of edit boxes).</summary>
    public void InvertRect(RegT bitmapId, int bitmapStride, SciRect rect, byte foreColor, byte backColor, bool doScaling)
    {
        var targetRect = rect;
        if (doScaling)
        {
            bitmapStride = bitmapStride * XResolution / _g.Frameout.ScriptWidth;
            targetRect = ScaleRect(rect);
        }

        var bitmap = _g.SegMan.LookupBitmap(bitmapId);
        uint invertSize = (uint)(targetRect.Height * bitmapStride + targetRect.Width);
        if (invertSize >= bitmap.DataSize)
        {
            throw new SciVmException($"InvertRect too big: {invertSize} >= {bitmap.DataSize}");
        }

        var pixels = bitmap.RawData;
        int pixel = bitmap.PixelsOffset + bitmapStride * targetRect.Top + targetRect.Left;
        int stride = bitmapStride - targetRect.Width;
        for (int y = 0; y < targetRect.Height; y++)
        {
            for (int x = 0; x < targetRect.Width; x++)
            {
                if (pixels[pixel] == foreColor)
                {
                    pixels[pixel] = backColor;
                }
                else if (pixels[pixel] == backColor)
                {
                    pixels[pixel] = foreColor;
                }

                pixel++;
            }

            pixel += stride;
        }
    }

    /// <summary>
    /// The number of characters from charIndex that fit in width pixels, stopping at word breaks
    /// and line breaks; charIndex moves to the first character of the next line.
    /// </summary>
    private int GetLongest(ref int charIndex, int width)
    {
        if (width <= 0)
        {
            throw new SciVmException("GfxText32::getLongest with no width");
        }

        int testLength = 0;
        int length = 0;
        int initialCharIndex = charIndex;

        // the index of the word after the last word break
        int lastWordBreakIndex = charIndex;
        int pos = charIndex;

        while (pos < _text.Length && _text[pos] != 0)
        {
            int currentChar = _text[pos++];

            if (currentChar == '\r' || currentChar == '\n')
            {
                // a Windows style \r\n (or \n\r) line break is skipped whole; Sierra's pointer had
                // not advanced yet here, so its indices were one higher
                if ((currentChar == '\r' && At(pos) == '\n') || (currentChar == '\n' && At(pos) == '\r' && At(pos + 1) != '\n'))
                {
                    charIndex++;
                }

                // the last word of the line made it too wide: give back up to the previous word
                if (length != 0 && GetTextWidth(initialCharIndex, testLength) > width)
                {
                    charIndex = lastWordBreakIndex;
                    return length;
                }

                // skip the line break and return the text seen so far
                charIndex++;
                return testLength;
            }

            if (currentChar == ' ')
            {
                // the last word made the line too wide: give back up to the previous word and fold
                // the spaces after it into the line break
                if (GetTextWidth(initialCharIndex, testLength) > width)
                {
                    charIndex = lastWordBreakIndex;
                    int nextChar = lastWordBreakIndex;
                    while (At(nextChar++) == ' ')
                    {
                        charIndex++;
                    }

                    return length;
                }

                // a word break within the width; the next word starts after it
                length = testLength;
                lastWordBreakIndex = charIndex + 1;
            }

            charIndex++;
            testLength++;

            // no word break yet and already too wide: split the word where it overflows
            if (length == 0 && GetTextWidth(initialCharIndex, testLength) > width)
            {
                charIndex = --testLength + lastWordBreakIndex;
                return testLength;
            }
        }

        // the whole text was one word, or narrower than the area: the whole line
        if (length == 0 || GetTextWidth(initialCharIndex, testLength) <= width)
        {
            return testLength;
        }

        // the last word made the line too wide: up to the penultimate word
        charIndex = lastWordBreakIndex;
        return length;
    }

    private int At(int pos) => pos < _text.Length ? _text[pos] : 0;

    private int GetTextWidth(int index, int length)
    {
        GetTextDimensions(index, length, out int width, out _);
        return width;
    }

    /// <summary>The unscaled size of part of the current text. Only font changes affect the width, so the other control codes are skipped.</summary>
    private void GetTextDimensions(int index, int length, out int width, out int height)
    {
        width = 0;
        height = 0;
        var font = _font;
        int pos = index;
        int currentChar = At(pos++);
        while (length > 0 && currentChar != 0)
        {
            if (currentChar == '|')
            {
                currentChar = At(pos++);
                length--;
                if (length > 0 && currentChar == 'f')
                {
                    int fontId = 0;
                    while (length > 0 && At(pos) >= '0' && At(pos) <= '9')
                    {
                        currentChar = At(pos++);
                        length--;
                        fontId = fontId * 10 + currentChar - '0';
                    }

                    if (length > 0)
                    {
                        font = _g.GetFont(fontId);
                    }
                }

                // past any other control data
                while (length > 0 && At(pos) != '|')
                {
                    pos++;
                    length--;
                }

                if (length > 0)
                {
                    pos++;
                    length--;
                }
            }
            else if (font is not null)
            {
                width += font.GetCharWidth(currentChar);
                int charHeight = font.GetCharHeight(currentChar);
                if (height < charHeight)
                {
                    height = charHeight;
                }
            }

            if (length > 0)
            {
                currentChar = At(pos++);
                length--;
            }
        }
    }

    /// <summary>The scaled width of part of a text.</summary>
    public int GetTextWidth(string text, int index, int length)
    {
        _text = Latin1(text);
        return ScaleUpWidth(GetTextWidth(index, length));
    }

    /// <summary>
    /// The size of a block of text wrapped at maxWidth script pixels (0 means three fifths of
    /// the width, negative means no wrapping). Sierra's rectangle started as 1x1 and was fixed
    /// up afterwards; this works in exclusive rectangles with inclusive rounding.
    /// </summary>
    public SciRect GetTextSize(string text, int maxWidth, bool doScaling)
    {
        var result = new SciRect();
        int scriptWidth = _g.Frameout.ScriptWidth;
        int scriptHeight = _g.Frameout.ScriptHeight;
        maxWidth = maxWidth * XResolution / scriptWidth;
        _text = Latin1(text);

        if (maxWidth >= 0)
        {
            if (maxWidth == 0)
            {
                maxWidth = XResolution * 3 / 5;
            }

            result.Right = maxWidth;
            int textWidth = 0;
            if (_text.Length > 0)
            {
                int charIndex = 0;
                int nextCharIndex = 0;
                while (charIndex < _text.Length && _text[charIndex] != 0)
                {
                    int length = GetLongest(ref nextCharIndex, result.Width);
                    textWidth = Math.Max(textWidth, GetTextWidth(charIndex, length));
                    charIndex = nextCharIndex;
                    // the font height may be wrong for a line whose font changed inline
                    result.Bottom += Font.Height;
                }
            }

            if (textWidth < maxWidth)
            {
                result.Right = textWidth;
            }
        }
        else
        {
            result.Right = GetTextWidth(0, 10000);
            // Sierra left the bottom a pixel taller than the font from SCI2.1 middle on
            result.Bottom = _g.Version < SciVersion.V2_1Middle ? 0 : Font.Height + 1;
        }

        if (doScaling)
        {
            // the top and left are always zero, so only the far edges are scaled
            result.Right = ((result.Right - 1) * scriptWidth + XResolution - 1) / XResolution + 1;
            result.Bottom = ((result.Bottom - 1) * scriptHeight + YResolution - 1) / YResolution + 1;
        }

        return result;
    }

    /// <summary>Fills a rectangle of the current bitmap with the background colour.</summary>
    public void Erase(SciRect rect, bool doScaling)
    {
        var targetRect = doScaling ? ScaleRect(rect) : rect;
        var bitmap = _g.SegMan.LookupBitmap(Bitmap);
        new PixelBuffer(bitmap.RawData, bitmap.PixelsOffset, bitmap.Width, bitmap.Height).FillRect(targetRect, _backColor);
    }

    public int GetStringWidth(string text) => GetTextWidth(text, 0, 10000);

    /// <summary>How many characters of a text from index fit into a rectangle.</summary>
    public int GetTextCount(string text, int index, SciRect textRect, bool doScaling)
    {
        var scaledRect = textRect;
        if (doScaling)
        {
            Rect32Math.MulInc(ref scaledRect, new Ratio(XResolution, _g.Frameout.ScriptWidth), new Ratio(YResolution, _g.Frameout.ScriptHeight));
        }

        var oldText = _text;
        _text = Latin1(text);
        int charIndex = index;
        int maxWidth = scaledRect.Width;
        int lineCount = (scaledRect.Height - 2) / Font.Height;
        while (lineCount-- > 0)
        {
            GetLongest(ref charIndex, maxWidth);
        }

        _text = oldText;
        return charIndex - index;
    }

    public int GetTextCount(string text, int index, int fontId, SciRect textRect, bool doScaling)
    {
        SetFont(fontId);
        return GetTextCount(text, index, textRect, doScaling);
    }

    /// <summary>Scrolls the text area of the current bitmap by one line and draws a new line at the edge it opened.</summary>
    public void ScrollLine(string lineText, int numLines, byte color, TextAlign align, int fontId, ScrollDirection dir)
    {
        var bitmap = _g.SegMan.LookupBitmap(Bitmap);
        var pixels = bitmap.RawData;
        int baseOffset = bitmap.PixelsOffset;
        int h = Font.Height;

        if (dir == ScrollDirection.Up)
        {
            // the existing text moves down
            for (int i = 0; i < (numLines - 1) * h; i++)
            {
                int y = _textRect.Top + numLines * h - i - 1;
                Array.Copy(pixels, baseOffset + (y - h) * _width + _textRect.Left, pixels, baseOffset + y * _width + _textRect.Left, _textRect.Width);
            }
        }
        else
        {
            for (int i = 0; i < (numLines - 1) * h; i++)
            {
                int y = _textRect.Top + i;
                Array.Copy(pixels, baseOffset + (y + h) * _width + _textRect.Left, pixels, baseOffset + y * _width + _textRect.Left, _textRect.Width);
            }
        }

        var lineRect = _textRect;
        if (dir == ScrollDirection.Up)
        {
            lineRect.Bottom = lineRect.Top + h;
        }
        else
        {
            lineRect.Top += (numLines - 1) * h;
            lineRect.Bottom++;
        }

        Erase(lineRect, false);

        _drawPosition.X = _textRect.Left;
        _drawPosition.Y = _textRect.Top;
        if (dir == ScrollDirection.Down)
        {
            _drawPosition.Y += (numLines - 1) * h;
        }

        _foreColor = color;
        _alignment = align;
        SetFont(fontId);
        _text = Latin1(lineText);
        int textWidth = GetTextWidth(0, _text.Length);
        if (_alignment == TextAlign.Center)
        {
            _drawPosition.X += (_textRect.Width - textWidth) / 2;
        }
        else if (_alignment == TextAlign.Right)
        {
            _drawPosition.X += _textRect.Width - textWidth;
        }

        DrawText(0, _text.Length);
    }
}
