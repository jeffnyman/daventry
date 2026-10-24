using Daventry.Core.Formats;
using Daventry.Core.Graphics;

namespace Daventry.Adl;

/// <summary>
/// The Apple II hi-res screen and 40-column text, port of ScummVM's Display_A2. The frame buffer
/// is the machine's: 40 bytes a line, seven pixels a byte, the high bit choosing the colour
/// palette by shifting the byte half a pixel. Text and graphics are turned into 560x384 pixels
/// (each Apple pixel two wide, each line two high) when the game asks for a render, as
/// ScummVM's renderer does.
/// <para>
/// Daventry hosts take indexed colour, so of ScummVM's renderers this ports the ones that fit a
/// palette: the RGB-monitor colours (AppleWin's palette, a colour per four-bit window and phase)
/// and the green or white monochrome monitor, each with an optional dimmed scanline. ScummVM's
/// default NTSC composite emulation blends neighbouring pixels into thousands of colours and
/// needs a true-colour host; it is not ported yet.
/// </para>
/// </summary>
public sealed class DisplayA2 : Display
{
    public const int GfxWidth = 280;
    public const int GfxHeight = 192;
    public const int GfxPitch = GfxWidth / 7;
    public const int GfxSize = GfxPitch * GfxHeight;
    public const int TextColumns = 40;
    public const int TextRows = 24;
    public const int SplitHeight = 32;

    /// <summary>Size of the presented frame: every Apple pixel two wide and two high.</summary>
    public const int ScreenWidth = GfxWidth * 2;
    public const int ScreenHeight = GfxHeight * 2;

    // palette layout: 0-15 the RGB-monitor colours, 16 the green monochrome phosphor, and the
    // same 17 colours dimmed to three quarters from DimBase for scanline mode
    private const byte MonoGreen = 16;
    private const byte DimBase = 32;
    private const int ColorPhases = 4;

    // 3 pixels of "pre-render" are cut off when a line is copied to the screen
    private const int PreRender = 3;
    private const int RenderBufWidth = (GfxPitch + 1) * 14;

    // AppleWin's RGB monitor palette, by colour number
    private static readonly Rgb[] RgbPalette =
    [
        new(0x00, 0x00, 0x00), new(0x9d, 0x09, 0x66), new(0x2a, 0x2a, 0xe5), new(0xc7, 0x34, 0xff),
        new(0x00, 0x80, 0x00), new(0x80, 0x80, 0x80), new(0x0d, 0xa1, 0xff), new(0xaa, 0xaa, 0xff),
        new(0x55, 0x55, 0x00), new(0xf2, 0x5e, 0x00), new(0xc0, 0xc0, 0xc0), new(0xff, 0x89, 0xe5),
        new(0x38, 0xcb, 0x00), new(0xd5, 0xd5, 0x1a), new(0x62, 0xf6, 0x99), new(0xff, 0xff, 0xff),
    ];

    // Uppercase-only Apple II font (manually created in ScummVM)
    private static readonly byte[,] Font =
    {
        { 0x00, 0x1c, 0x22, 0x2a, 0x3a, 0x1a, 0x02, 0x3c }, { 0x00, 0x08, 0x14, 0x22, 0x22, 0x3e, 0x22, 0x22 }, // @A
        { 0x00, 0x1e, 0x22, 0x22, 0x1e, 0x22, 0x22, 0x1e }, { 0x00, 0x1c, 0x22, 0x02, 0x02, 0x02, 0x22, 0x1c }, // BC
        { 0x00, 0x1e, 0x22, 0x22, 0x22, 0x22, 0x22, 0x1e }, { 0x00, 0x3e, 0x02, 0x02, 0x1e, 0x02, 0x02, 0x3e }, // DE
        { 0x00, 0x3e, 0x02, 0x02, 0x1e, 0x02, 0x02, 0x02 }, { 0x00, 0x3c, 0x02, 0x02, 0x02, 0x32, 0x22, 0x3c }, // FG
        { 0x00, 0x22, 0x22, 0x22, 0x3e, 0x22, 0x22, 0x22 }, { 0x00, 0x1c, 0x08, 0x08, 0x08, 0x08, 0x08, 0x1c }, // HI
        { 0x00, 0x20, 0x20, 0x20, 0x20, 0x20, 0x22, 0x1c }, { 0x00, 0x22, 0x12, 0x0a, 0x06, 0x0a, 0x12, 0x22 }, // JK
        { 0x00, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x3e }, { 0x00, 0x22, 0x36, 0x2a, 0x2a, 0x22, 0x22, 0x22 }, // LM
        { 0x00, 0x22, 0x22, 0x26, 0x2a, 0x32, 0x22, 0x22 }, { 0x00, 0x1c, 0x22, 0x22, 0x22, 0x22, 0x22, 0x1c }, // NO
        { 0x00, 0x1e, 0x22, 0x22, 0x1e, 0x02, 0x02, 0x02 }, { 0x00, 0x1c, 0x22, 0x22, 0x22, 0x2a, 0x12, 0x2c }, // PQ
        { 0x00, 0x1e, 0x22, 0x22, 0x1e, 0x0a, 0x12, 0x22 }, { 0x00, 0x1c, 0x22, 0x02, 0x1c, 0x20, 0x22, 0x1c }, // RS
        { 0x00, 0x3e, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08 }, { 0x00, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x1c }, // TU
        { 0x00, 0x22, 0x22, 0x22, 0x22, 0x22, 0x14, 0x08 }, { 0x00, 0x22, 0x22, 0x22, 0x2a, 0x2a, 0x36, 0x22 }, // VW
        { 0x00, 0x22, 0x22, 0x14, 0x08, 0x14, 0x22, 0x22 }, { 0x00, 0x22, 0x22, 0x14, 0x08, 0x08, 0x08, 0x08 }, // XY
        { 0x00, 0x3e, 0x20, 0x10, 0x08, 0x04, 0x02, 0x3e }, { 0x00, 0x3e, 0x06, 0x06, 0x06, 0x06, 0x06, 0x3e }, // Z[
        { 0x00, 0x00, 0x02, 0x04, 0x08, 0x10, 0x20, 0x00 }, { 0x00, 0x3e, 0x30, 0x30, 0x30, 0x30, 0x30, 0x3e }, // \]
        { 0x00, 0x00, 0x00, 0x08, 0x14, 0x22, 0x00, 0x00 }, { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3e }, // ^_
        { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, { 0x00, 0x08, 0x08, 0x08, 0x08, 0x08, 0x00, 0x08 }, //  !
        { 0x00, 0x14, 0x14, 0x14, 0x00, 0x00, 0x00, 0x00 }, { 0x00, 0x14, 0x14, 0x3e, 0x14, 0x3e, 0x14, 0x14 }, // "#
        { 0x00, 0x08, 0x3c, 0x0a, 0x1c, 0x28, 0x1e, 0x08 }, { 0x00, 0x06, 0x26, 0x10, 0x08, 0x04, 0x32, 0x30 }, // $%
        { 0x00, 0x04, 0x0a, 0x0a, 0x04, 0x2a, 0x12, 0x2c }, { 0x00, 0x08, 0x08, 0x08, 0x00, 0x00, 0x00, 0x00 }, // &'
        { 0x00, 0x08, 0x04, 0x02, 0x02, 0x02, 0x04, 0x08 }, { 0x00, 0x08, 0x10, 0x20, 0x20, 0x20, 0x10, 0x08 }, // ()
        { 0x00, 0x08, 0x2a, 0x1c, 0x08, 0x1c, 0x2a, 0x08 }, { 0x00, 0x00, 0x08, 0x08, 0x3e, 0x08, 0x08, 0x00 }, // *+
        { 0x00, 0x00, 0x00, 0x00, 0x00, 0x08, 0x08, 0x04 }, { 0x00, 0x00, 0x00, 0x00, 0x3e, 0x00, 0x00, 0x00 }, // ,-
        { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08 }, { 0x00, 0x00, 0x20, 0x10, 0x08, 0x04, 0x02, 0x00 }, // ./
        { 0x00, 0x1c, 0x22, 0x32, 0x2a, 0x26, 0x22, 0x1c }, { 0x00, 0x08, 0x0c, 0x08, 0x08, 0x08, 0x08, 0x1c }, // 01
        { 0x00, 0x1c, 0x22, 0x20, 0x18, 0x04, 0x02, 0x3e }, { 0x00, 0x3e, 0x20, 0x10, 0x18, 0x20, 0x22, 0x1c }, // 23
        { 0x00, 0x10, 0x18, 0x14, 0x12, 0x3e, 0x10, 0x10 }, { 0x00, 0x3e, 0x02, 0x1e, 0x20, 0x20, 0x22, 0x1c }, // 45
        { 0x00, 0x38, 0x04, 0x02, 0x1e, 0x22, 0x22, 0x1c }, { 0x00, 0x3e, 0x20, 0x10, 0x08, 0x04, 0x04, 0x04 }, // 67
        { 0x00, 0x1c, 0x22, 0x22, 0x1c, 0x22, 0x22, 0x1c }, { 0x00, 0x1c, 0x22, 0x22, 0x3c, 0x20, 0x10, 0x0e }, // 89
        { 0x00, 0x00, 0x00, 0x08, 0x00, 0x08, 0x00, 0x00 }, { 0x00, 0x00, 0x00, 0x08, 0x00, 0x08, 0x08, 0x04 }, // :;
        { 0x00, 0x10, 0x08, 0x04, 0x02, 0x04, 0x08, 0x10 }, { 0x00, 0x00, 0x00, 0x3e, 0x00, 0x3e, 0x00, 0x00 }, // <=
        { 0x00, 0x04, 0x08, 0x10, 0x20, 0x10, 0x08, 0x04 }, { 0x00, 0x1c, 0x22, 0x10, 0x08, 0x08, 0x00, 0x08 }, // >?
    };

    private static readonly byte[,] ColorTable = BuildColorTable();
    private static readonly ushort[] DoublePixelMasks = BuildDoublePixelMasks();

    private readonly byte[] _frameBuf = new byte[GfxSize];
    private readonly byte[] _row = new byte[RenderBufWidth];
    private readonly Func<TimeSpan> _clock;
    private bool _showCursor;
    private bool _blink;

    /// <param name="clock">Engine time, for the cursor's blink.</param>
    /// <param name="options">Colour, scanlines, text colour and cursor style.</param>
    public DisplayA2(Func<TimeSpan> clock, DisplayOptions options)
    {
        _clock = clock;
        Options = options;
        Screen = new Surface8(ScreenWidth, ScreenHeight);
    }

    public DisplayOptions Options { get; }

    /// <summary>What the monitor shows: rewritten by <see cref="RenderText"/> and <see cref="RenderGraphics"/>.</summary>
    public Surface8 Screen { get; }

    /// <summary>Set by a render; the engine presents the screen and clears it.</summary>
    public bool Dirty { get; set; }

    /// <summary>Called for a bell character (Ctrl-G) in printed text.</summary>
    public Action? Bell { get; set; }

    /// <summary>The colours the screen's pixels index.</summary>
    public static Rgb[] Palette { get; } = BuildPalette();

    /// <summary>The Apple II frame buffer, for tests.</summary>
    public ReadOnlySpan<byte> FrameBuffer => _frameBuf;

    public override void Init() => CreateTextBuffer(TextColumns, TextRows);

    public override char AsciiToNative(char c) => (char)(c | 0x80);

    // ---- frame buffer ----

    /// <summary>Reads a picture in the hi-res page's interleaved order: three lines 64 apart, then 8 bytes of padding.</summary>
    public static void LoadFrameBuffer(SeekableReadStream stream, byte[] dst)
    {
        int d = 0;
        for (int j = 0; j < 8; ++j)
        {
            for (int i = 0; i < 8; ++i)
            {
                stream.Read(dst.AsSpan(d, GfxPitch));
                d += GfxPitch * 64;
                stream.Read(dst.AsSpan(d, GfxPitch));
                d += GfxPitch * 64;
                stream.Read(dst.AsSpan(d, GfxPitch));
                stream.ReadUInt32LE();
                stream.ReadUInt32LE();
                d -= GfxPitch * 120;
            }

            d -= GfxPitch * 63;
        }

        if (stream.Eos)
        {
            throw new InvalidDataException("Failed to read frame buffer");
        }
    }

    public void LoadFrameBuffer(SeekableReadStream stream) => LoadFrameBuffer(stream, _frameBuf);

    public void PutPixel(int x, int y, byte color)
    {
        int offset = x / 7;
        byte mask = (byte)(0x80 | (1 << (x % 7)));

        // white and black are in both palettes, so the palette bit is left alone
        if ((color & 0x7f) == 0x7f || (color & 0x7f) == 0)
        {
            mask &= 0x7f;
        }

        // colours starting with bits '01' or '10' are inverted at odd offsets
        if ((offset & 1) != 0)
        {
            byte c = (byte)(color << 1);
            if (c is >= 0x40 and < 0xc0)
            {
                color ^= 0x7f;
            }
        }

        WriteFrameBuffer(x, y, color, mask);
    }

    public void SetPixelByte(int x, int y, byte color) => _frameBuf[y * GfxPitch + x / 7] = color;

    public void SetPixelBit(int x, int y, byte color) => WriteFrameBuffer(x, y, color, (byte)(1 << (x % 7)));

    public void SetPixelPalette(int x, int y, byte color) => WriteFrameBuffer(x, y, color, 0x80);

    public byte GetPixelByte(int x, int y) => _frameBuf[y * GfxPitch + x / 7];

    /// <summary>
    /// The fill tests the pixel right of the last column; as in the C++, that reads the first byte
    /// of the next line, and past the end of the last line it reads as off.
    /// </summary>
    public bool GetPixelBit(int x, int y)
    {
        int index = y * GfxPitch + x / 7;
        return (uint)index < GfxSize && (_frameBuf[index] & (1 << (x % 7))) != 0;
    }

    public void Clear(byte color)
    {
        byte val = 0;
        byte c = (byte)(color << 1);
        if (c is >= 0x40 and < 0xc0)
        {
            val = 0x7f;
        }

        for (int i = 0; i < GfxSize; ++i)
        {
            _frameBuf[i] = color;
            color ^= val;
        }
    }

    // ---- text ----

    public override void PrintChar(char c)
    {
        if (c == AsciiToNative('\r'))
        {
            int rowStart = CursorPos / TextColumns * TextColumns;
            CompleteLine(rowStart, CursorPos);
            CursorPos = rowStart + TextColumns;
        }
        else if (c == AsciiToNative('\a'))
        {
            RenderText();
            Bell?.Invoke();
        }
        else if (c is < (char)0x80 or >= (char)0xa0)
        {
            SetCharAtCursor((byte)c);
            ++CursorPos;
            if (CursorPos % TextColumns == 0)
            {
                CompleteLine(CursorPos - TextColumns, CursorPos);
            }
        }

        if (CursorPos == TextColumns * TextRows)
        {
            ScrollUp();
        }
    }

    public override void ShowCursor(bool enable) => _showCursor = enable;

    // ---- rendering ----

    public override void RenderText()
    {
        if (CurrentMode == Mode.Graphics)
        {
            return;
        }

        _blink = ((long)_clock().TotalMilliseconds / 270 & 1) != 0;

        int startY = CurrentMode == Mode.Text ? 0 : GfxHeight - SplitHeight;
        bool colorText = CurrentMode == Mode.Mixed && Options.Color && !Options.MonoText;
        Render(startY, GfxHeight, TextBits, colorText ? Writer.Color : TextWriter);
    }

    public override void RenderGraphics()
    {
        if (CurrentMode == Mode.Text)
        {
            return;
        }

        int endY = CurrentMode == Mode.Graphics ? GfxHeight : GfxHeight - SplitHeight;
        Render(0, endY, (y, x) => _frameBuf[y * GfxPitch + x], Options.Color ? Writer.Color : Writer.Green);
    }

    private enum Writer
    {
        Color,
        Green,
        White,
    }

    private Writer TextWriter => Options.Color ? Writer.White : Writer.Green;

    private byte TextBits(int y, int x)
    {
        int charPos = (y >> 3) * TextColumns + x;
        int m = TextBuf[charPos];
        int b = Font[m & 0x3f, y % 8];

        if (_showCursor && charPos == CursorPos)
        {
            if (!Options.Apple2eCursor)
            {
                m = (m & 0x3f) | 0x40;
            }
            else if (_blink)
            {
                ReadOnlySpan<byte> cursor = [0x00, 0x00, 0x2a, 0x14, 0x2a, 0x14, 0x2a, 0x00];
                b = cursor[y % 8];
            }
        }

        // inverse below 0x40, flashing below 0x80
        if ((m & 0x80) == 0 && ((m & 0x40) == 0 || _blink))
        {
            b = ~b;
        }

        return (byte)(b & 0x7f);
    }

    private void Render(int startY, int endY, Func<int, int, byte> getBits, Writer writer)
    {
        var pixels = Screen.Pixels;
        for (int y = startY; y < endY; ++y)
        {
            int lastBit = 0;
            int ptr = 0;
            int phase = 3;
            uint window = 0;

            for (int x = 0; x <= GfxPitch; ++x)
            {
                int bits = 0;
                if (x < GfxPitch)
                {
                    byte m = getBits(y, x);
                    bits = DoublePixelMasks[m & 0x7f];
                    if ((m & 0x80) != 0)
                    {
                        bits = (bits << 1) | lastBit;
                    }

                    lastBit = (bits >> 13) & 1;
                }

                // the extra chunk at the end feeds the pre-render
                for (int b = 0; b < 14; ++b)
                {
                    window = (window << 1) | (uint)(bits & 1);
                    bits >>= 1;
                    _row[ptr++] = writer switch
                    {
                        Writer.Color => ColorTable[phase, (window >> 2) & 15],
                        Writer.Green => ((window >> 3) & 1) != 0 ? MonoGreen : (byte)0,
                        _ => ((window >> 3) & 1) != 0 ? (byte)15 : (byte)0,
                    };
                    phase = (phase + 1) & 3;
                }
            }

            int even = y * 2 * ScreenWidth;
            int odd = even + ScreenWidth;
            for (int i = 0; i < ScreenWidth; i++)
            {
                byte color = _row[PreRender + i];
                pixels[even + i] = color;

                // the line in between repeats the line above, or a dimmer copy of it for scanlines
                pixels[odd + i] = Options.Scanlines ? (byte)(color + DimBase) : color;
            }
        }

        Dirty = true;
    }

    private void WriteFrameBuffer(int x, int y, byte color, byte mask)
    {
        ref byte b = ref _frameBuf[y * GfxPitch + x / 7];
        color ^= b;
        color &= mask;
        b ^= color;
    }

    private static byte[,] BuildColorTable()
    {
        var table = new byte[ColorPhases, 16];
        for (int pattern = 0; pattern < 16; ++pattern)
        {
            int color = ((pattern & 1) << 3) | ((pattern & 2) << 1) | ((pattern & 4) >> 1) | ((pattern & 8) >> 3);
            for (int phase = 0; phase < ColorPhases; ++phase)
            {
                table[phase, pattern] = (byte)color;
                color = ((color & 8) >> 3) | ((color << 1) & 0x0f);
            }
        }

        return table;
    }

    private static ushort[] BuildDoublePixelMasks()
    {
        var masks = new ushort[128];
        for (int val = 0; val < masks.Length; ++val)
        {
            for (int bit = 0; bit < 7; bit++)
            {
                if ((val & (1 << bit)) != 0)
                {
                    masks[val] |= (ushort)(3 << (bit * 2));
                }
            }
        }

        return masks;
    }

    private static Rgb[] BuildPalette()
    {
        var palette = new Rgb[DimBase * 2];
        RgbPalette.CopyTo(palette, 0);
        palette[MonoGreen] = new Rgb(0x00, 0xc0, 0x00);
        for (int i = 0; i <= MonoGreen; i++)
        {
            // ScummVM's LineDoubleDim: (c >> 1) + (c >> 2)
            var c = palette[i];
            palette[DimBase + i] = new Rgb((byte)((c.R >> 1) + (c.R >> 2)), (byte)((c.G >> 1) + (c.G >> 2)), (byte)((c.B >> 1) + (c.B >> 2)));
        }

        return palette;
    }
}

/// <summary>ScummVM's ADL display options that Daventry supports.</summary>
/// <param name="Color">Colour graphics; off shows a green monochrome monitor (Mystery House's default).</param>
/// <param name="Scanlines">Darken every other line, as on a CRT.</param>
/// <param name="MonoText">Draw the text window in white even in colour mode (ScummVM's default).</param>
/// <param name="Apple2eCursor">The checkered blinking cursor of later Apple II models.</param>
public sealed record DisplayOptions(bool Color = true, bool Scanlines = false, bool MonoText = true, bool Apple2eCursor = false);
