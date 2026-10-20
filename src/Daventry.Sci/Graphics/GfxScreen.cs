using Daventry.Core.Graphics;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Graphics;

[Flags]
public enum GfxScreenMask : byte
{
    None = 0,
    Visual = 1,
    Priority = 2,
    Control = 4,
    All = Visual | Priority | Control,
}

/// <summary>
/// The four screens of a SCI16 game: visual, priority and control at script resolution, and the
/// display the user sees. Port of ScummVM's screen.cpp for the DOS layout (320x200, no upscaling);
/// the Macintosh 480x300 and 640x400 modes are not ported. The host palette and the display
/// surface are exposed for the engine to present.
/// </summary>
public sealed class GfxScreen
{
    public const int DitheredBgColorsSize = 256;

    private readonly byte[] _visualScreen;
    private readonly byte[] _priorityScreen;
    private readonly byte[] _controlScreen;
    private readonly byte[] _displayScreen;
    private byte[] _activeScreen;
    private readonly short[] _ditheredPicColors = new short[DitheredBgColorsSize];
    private readonly Rgb[] _hostPalette = new Rgb[256];

    public GfxScreen(ResourceManager resMan, SciVersion version)
    {
        ScriptWidth = 320;
        ScriptHeight = 200;
        Width = ScriptWidth;
        Height = ScriptHeight;
        DisplayWidth = Width;
        DisplayHeight = Height;

        int pixels = Width * Height;
        _visualScreen = new byte[pixels];
        _priorityScreen = new byte[pixels];
        _controlScreen = new byte[pixels];
        Display = new Surface8(DisplayWidth, DisplayHeight);
        _displayScreen = Display.Pixels;
        _activeScreen = _displayScreen;

        if (resMan.ViewType != ViewType.Ega)
        {
            // 255 is defined as white at all times, which is not exact for Amiga 32 colours but harmless
            ColorWhite = 255;
            ColorDefaultVectorData = version >= SciVersion.V1_1 ? (byte)255 : (byte)0;
        }
        else
        {
            ColorWhite = 15;
            ColorDefaultVectorData = 0;
        }

        UnditheringEnabled = true;
    }

    public int Width { get; }

    public int Height { get; }

    public int ScriptWidth { get; }

    public int ScriptHeight { get; }

    public int DisplayWidth { get; }

    public int DisplayHeight { get; }

    public byte ColorWhite { get; }

    public byte ColorDefaultVectorData { get; }

    /// <summary>The frame the host presents. Indexed colour; see <see cref="HostPalette"/>.</summary>
    public Surface8 Display { get; }

    /// <summary>True when the display changed since the host last presented it.</summary>
    public bool DisplayDirty { get; set; }

    /// <summary>The 256 colours the host should show the display with.</summary>
    public ReadOnlySpan<Rgb> HostPalette => _hostPalette;

    /// <summary>Incremented whenever <see cref="HostPalette"/> changes.</summary>
    public int PaletteVersion { get; private set; }

    public bool UnditheringEnabled { get; private set; }

    /// <summary>Possible values 0, 1 and 2. Mirrors _picNotValid.</summary>
    public int PicNotValid { get; set; }

    /// <summary>The variant used by kPicNotValid in SCI1.1.</summary>
    public int PicNotValidSci11 { get; set; }

    public bool FontIsUpscaled { get; set; }

    /// <summary>Horizontal and vertical offset of the shaken screen, applied by the presenter.</summary>
    public (int X, int Y) ShakeOffset { get; set; }

    // ---- presenting ----

    public void CopyToScreen() => DisplayDirty = true;

    public void CopyRectToScreen(SciRect rect) => DisplayDirty = true;

    public void CopyRectToScreen(SciRect rect, int x, int y)
    {
        // Display rect from the active screen to screen location x, y; clipping is already done
        if (x == rect.Left && y == rect.Top)
        {
            DisplayDirty = true;
            return;
        }

        var temp = new byte[rect.Width * rect.Height];
        for (int row = 0; row < rect.Height; row++)
        {
            Array.Copy(_activeScreen, (rect.Top + row) * DisplayWidth + rect.Left, temp, row * rect.Width, rect.Width);
        }

        for (int row = 0; row < rect.Height; row++)
        {
            Array.Copy(temp, row * rect.Width, _displayScreen, (y + row) * DisplayWidth + x, rect.Width);
        }

        DisplayDirty = true;
    }

    /// <summary>Resets every screen; only used when restoring a game.</summary>
    public void ClearForRestoreGame()
    {
        Array.Clear(_visualScreen);
        Array.Clear(_priorityScreen);
        Array.Clear(_controlScreen);
        Array.Clear(_displayScreen);
        Array.Clear(_ditheredPicColors);
        FontIsUpscaled = false;
        CopyToScreen();
    }

    // ---- pixels ----

    public static GfxScreenMask GetDrawingMask(byte color, byte priority, byte control)
    {
        var flag = GfxScreenMask.None;
        if (color != 255)
        {
            flag |= GfxScreenMask.Visual;
        }

        if (priority != 255)
        {
            flag |= GfxScreenMask.Priority;
        }

        if (control != 255)
        {
            flag |= GfxScreenMask.Control;
        }

        return flag;
    }

    public void PutPixel(int x, int y, GfxScreenMask drawMask, byte color, byte priority, byte control)
    {
        int offset = y * Width + x;
        if ((drawMask & GfxScreenMask.Visual) != 0)
        {
            _visualScreen[offset] = color;
            _displayScreen[offset] = color;
        }

        if ((drawMask & GfxScreenMask.Priority) != 0)
        {
            _priorityScreen[offset] = priority;
        }

        if ((drawMask & GfxScreenMask.Control) != 0)
        {
            _controlScreen[offset] = control;
        }
    }

    /// <summary>Called from vector drawing; the same as <see cref="PutPixel"/> without upscaling.</summary>
    public void VectorPutPixel(int x, int y, GfxScreenMask drawMask, byte color, byte priority, byte control)
        => PutPixel(x, y, drawMask, color, priority, control);

    /// <summary>Writes a pixel on the display only, for hires content drawn onto an upscaled display.</summary>
    public void PutPixelOnDisplay(int x, int y, byte color) => _displayScreen[y * DisplayWidth + x] = color;

    /// <summary>Font pixels are never line-tripled on upscaled displays; without upscaling this is a plain visual pixel.</summary>
    public void PutFontPixel(int startingY, int x, int y, byte color)
    {
        int actualY = startingY + y;
        if (FontIsUpscaled)
        {
            PutPixelOnDisplay(x, actualY, color);
            return;
        }

        int offset = actualY * Width + x;
        _visualScreen[offset] = color;
        _displayScreen[offset] = color;
    }

    public byte GetVisual(int x, int y) => _visualScreen[y * Width + x];

    public byte GetPriority(int x, int y) => _priorityScreen[y * Width + x];

    public byte GetControl(int x, int y) => _controlScreen[y * Width + x];

    public byte VectorGetVisual(int x, int y) => _visualScreen[y * Width + x];

    public byte VectorGetPriority(int x, int y) => _priorityScreen[y * Width + x];

    public byte VectorGetControl(int x, int y) => _controlScreen[y * Width + x];

    public GfxScreenMask VectorIsFillMatch(int x, int y, GfxScreenMask screenMask, byte checkForColor, byte checkForPriority, byte checkForControl, bool isEga)
    {
        int offset = y * Width + x;
        var match = GfxScreenMask.None;
        if ((screenMask & GfxScreenMask.Visual) != 0)
        {
            if (!isEga)
            {
                if (_visualScreen[offset] == checkForColor)
                {
                    match |= GfxScreenMask.Visual;
                }
            }
            else
            {
                // EGA pixels are 4 bits but stored as a full byte for undithering; only the
                // visible colour counts when filling
                byte egaColor = _visualScreen[offset];
                egaColor = ((x ^ y) & 1) != 0 ? (byte)((egaColor ^ (egaColor >> 4)) & 0x0F) : (byte)(egaColor & 0x0F);
                if (egaColor == checkForColor)
                {
                    match |= GfxScreenMask.Visual;
                }
            }
        }

        if ((screenMask & GfxScreenMask.Priority) != 0 && _priorityScreen[offset] == checkForPriority)
        {
            match |= GfxScreenMask.Priority;
        }

        if ((screenMask & GfxScreenMask.Control) != 0 && _controlScreen[offset] == checkForControl)
        {
            match |= GfxScreenMask.Control;
        }

        return match;
    }

    /// <summary>
    /// Sierra's Bresenham line. Not interchangeable with a generic line routine: the flood fill
    /// depends on exactly these pixels.
    /// </summary>
    public void DrawLine(SciPoint startPoint, SciPoint endPoint, byte color, byte priority, byte control)
    {
        int maxWidth = Width - 1;
        int maxHeight = Height - 1;

        // LSL3 room 620 draws a line from 0,199 to 320,199, so clip
        int left = Math.Clamp(startPoint.X, 0, maxWidth);
        int top = Math.Clamp(startPoint.Y, 0, maxHeight);
        int right = Math.Clamp(endPoint.X, 0, maxWidth);
        int bottom = Math.Clamp(endPoint.Y, 0, maxHeight);

        var drawMask = GetDrawingMask(color, priority, control);

        if (top == bottom)
        {
            if (right < left)
            {
                (right, left) = (left, right);
            }

            for (int i = left; i <= right; i++)
            {
                VectorPutPixel(i, top, drawMask, color, priority, control);
            }

            return;
        }

        if (left == right)
        {
            if (top > bottom)
            {
                (top, bottom) = (bottom, top);
            }

            for (int i = top; i <= bottom; i++)
            {
                VectorPutPixel(left, i, drawMask, color, priority, control);
            }

            return;
        }

        int dy = bottom - top;
        int dx = right - left;
        int stepy = dy < 0 ? -1 : 1;
        int stepx = dx < 0 ? -1 : 1;
        dy = Math.Abs(dy) << 1;
        dx = Math.Abs(dx) << 1;

        VectorPutPixel(left, top, drawMask, color, priority, control);
        VectorPutPixel(right, bottom, drawMask, color, priority, control);

        if (dx > dy)
        {
            int fraction = dy - (dx >> 1);
            while (left != right)
            {
                if (fraction >= 0)
                {
                    top += stepy;
                    fraction -= dx;
                }

                left += stepx;
                fraction += dy;
                VectorPutPixel(left, top, drawMask, color, priority, control);
            }
        }
        else
        {
            int fraction = dx - (dy >> 1);
            while (top != bottom)
            {
                if (fraction >= 0)
                {
                    left += stepx;
                    fraction -= dy;
                }

                top += stepy;
                fraction += dx;
                VectorPutPixel(left, top, drawMask, color, priority, control);
            }
        }
    }

    public void DrawLine(int left, int top, int right, int bottom, byte color, byte priority, byte control)
        => DrawLine(new SciPoint(left, top), new SciPoint(right, bottom), color, priority, control);

    // ---- saving and restoring screen areas (bits handles) ----

    public int BitsGetDataSize(SciRect rect, GfxScreenMask mask)
    {
        int byteCount = 8 + 1; // rect and mask
        int pixels = rect.Width * rect.Height;
        if ((mask & GfxScreenMask.Visual) != 0)
        {
            byteCount += pixels * 2; // visual and display
        }

        if ((mask & GfxScreenMask.Priority) != 0)
        {
            byteCount += pixels;
        }

        if ((mask & GfxScreenMask.Control) != 0)
        {
            byteCount += pixels;
        }

        return byteCount;
    }

    public byte[] BitsSave(SciRect rect, GfxScreenMask mask)
    {
        var memory = new byte[BitsGetDataSize(rect, mask)];
        int pos = 0;
        WriteInt16(memory, ref pos, rect.Top);
        WriteInt16(memory, ref pos, rect.Left);
        WriteInt16(memory, ref pos, rect.Bottom);
        WriteInt16(memory, ref pos, rect.Right);
        memory[pos++] = (byte)mask;

        if ((mask & GfxScreenMask.Visual) != 0)
        {
            BitsSaveScreen(rect, _visualScreen, Width, memory, ref pos);
            BitsSaveScreen(rect, _displayScreen, DisplayWidth, memory, ref pos);
        }

        if ((mask & GfxScreenMask.Priority) != 0)
        {
            BitsSaveScreen(rect, _priorityScreen, Width, memory, ref pos);
        }

        if ((mask & GfxScreenMask.Control) != 0)
        {
            BitsSaveScreen(rect, _controlScreen, Width, memory, ref pos);
        }

        return memory;
    }

    private static void BitsSaveScreen(SciRect rect, byte[] screen, int screenWidth, byte[] memory, ref int pos)
    {
        int width = rect.Width;
        for (int y = rect.Top; y < rect.Bottom; y++)
        {
            Array.Copy(screen, y * screenWidth + rect.Left, memory, pos, width);
            pos += width;
        }
    }

    public static SciRect BitsGetRect(byte[] memory)
    {
        int pos = 0;
        int top = ReadInt16(memory, ref pos);
        int left = ReadInt16(memory, ref pos);
        int bottom = ReadInt16(memory, ref pos);
        int right = ReadInt16(memory, ref pos);
        return new SciRect(left, top, right, bottom);
    }

    public void BitsRestore(byte[] memory)
    {
        var rect = BitsGetRect(memory);
        int pos = 8;
        var mask = (GfxScreenMask)memory[pos++];

        if ((mask & GfxScreenMask.Visual) != 0)
        {
            BitsRestoreScreen(rect, memory, ref pos, _visualScreen, Width);
            BitsRestoreScreen(rect, memory, ref pos, _displayScreen, DisplayWidth);
        }

        if ((mask & GfxScreenMask.Priority) != 0)
        {
            BitsRestoreScreen(rect, memory, ref pos, _priorityScreen, Width);
        }

        if ((mask & GfxScreenMask.Control) != 0)
        {
            BitsRestoreScreen(rect, memory, ref pos, _controlScreen, Width);
        }
    }

    private static void BitsRestoreScreen(SciRect rect, byte[] memory, ref int pos, byte[] screen, int screenWidth)
    {
        int width = rect.Width;
        for (int y = rect.Top; y < rect.Bottom; y++)
        {
            Array.Copy(memory, pos, screen, y * screenWidth + rect.Left, width);
            pos += width;
        }
    }

    private static void WriteInt16(byte[] memory, ref int pos, int value)
    {
        memory[pos++] = (byte)value;
        memory[pos++] = (byte)(value >> 8);
    }

    private static int ReadInt16(byte[] memory, ref int pos)
    {
        short value = (short)(memory[pos] | (memory[pos + 1] << 8));
        pos += 2;
        return value;
    }

    // ---- dithering ----

    /// <summary>
    /// Finishes an EGA picture: visual pixels hold two 4-bit colours; the display shows them
    /// dithered, or the mixed colour when undithering is on.
    /// </summary>
    public void Dither(bool addToFlag)
    {
        if (!UnditheringEnabled)
        {
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int offset = y * Width + x;
                    byte color = _visualScreen[offset];
                    if ((color & 0xF0) != 0)
                    {
                        color ^= (byte)(color << 4);
                        color = ((x ^ y) & 1) != 0 ? (byte)(color >> 4) : (byte)(color & 0x0F);
                        _displayScreen[offset] = color;
                        _visualScreen[offset] = color;
                    }
                }
            }

            return;
        }

        if (!addToFlag)
        {
            Array.Clear(_ditheredPicColors);
        }

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int offset = y * Width + x;
                byte color = _visualScreen[offset];
                if ((color & 0xF0) != 0)
                {
                    color ^= (byte)(color << 4);

                    // remember the combination for cel undithering
                    _ditheredPicColors[color]++;

                    // a dither with black on the left is turned around, else the plain EGA colour would show
                    byte ditheredColor = (color & 0xF0) != 0 ? color : (byte)(color << 4);
                    _displayScreen[offset] = ditheredColor;

                    color = ((x ^ y) & 1) != 0 ? (byte)(color >> 4) : (byte)(color & 0x0F);
                    _visualScreen[offset] = color;
                }
            }
        }
    }

    public void DitherForceDitheredColor(byte color) => _ditheredPicColors[color] = 256;

    public void EnableUndithering(bool flag) => UnditheringEnabled = flag;

    public short[]? UnditherGetDitheredBgColors() => UnditheringEnabled ? _ditheredPicColors : null;

    // ---- debugging ----

    public void DebugShowMap(int mapNo)
    {
        _activeScreen = mapNo switch
        {
            0 => _visualScreen,
            1 => _priorityScreen,
            2 => _controlScreen,
            3 => _displayScreen,
            _ => _activeScreen,
        };

        if (!ReferenceEquals(_activeScreen, _displayScreen))
        {
            Array.Copy(_activeScreen, _displayScreen, _displayScreen.Length);
        }

        CopyToScreen();
    }

    public int KernelPicNotValid(int newPicNotValid, SciVersion version)
    {
        int old;
        if (version >= SciVersion.V1_1)
        {
            old = PicNotValidSci11;
            if (newPicNotValid != -1)
            {
                PicNotValidSci11 = newPicNotValid;
            }
        }
        else
        {
            old = PicNotValid;
            if (newPicNotValid != -1)
            {
                PicNotValid = newPicNotValid;
            }
        }

        return old;
    }

    // ---- palette ----

    /// <summary>Copies the current host palette into a buffer of RGB triples.</summary>
    public void GrabPalette(byte[] buffer, int start, int num)
    {
        for (int i = 0; i < num; i++)
        {
            var c = _hostPalette[start + i];
            buffer[(start + i) * 3] = c.R;
            buffer[(start + i) * 3 + 1] = c.G;
            buffer[(start + i) * 3 + 2] = c.B;
        }
    }

    /// <summary>Sets host colours from RGB triples.</summary>
    public void SetPalette(byte[] buffer, int start, int num)
    {
        bool changed = false;
        for (int i = start; i < start + num; i++)
        {
            var c = new Rgb(buffer[i * 3], buffer[i * 3 + 1], buffer[i * 3 + 2]);
            if (_hostPalette[i] != c)
            {
                _hostPalette[i] = c;
                changed = true;
            }
        }

        if (changed)
        {
            PaletteVersion++;
        }
    }
}
