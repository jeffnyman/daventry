using Daventry.Core.Graphics;

namespace Daventry.Adl;

/// <summary>
/// ScummVM's NTSC composite emulation of the Apple II screen (PixelWriterColorNTSC and
/// PixelWriterMonoNTSC, based on AppleWin's): the bit stream of a line runs through filters that
/// split it into luma and chroma, and a twelve-bit window of it, with the colour phase, picks one
/// of 4096 colours. The line in between two rendered lines is their average, and text drawn with
/// the sharp monochrome writer (ScummVM's default) repeats its line as the other renderers do.
/// </summary>
public sealed partial class DisplayA2
{
    private const int NtscColors = 4096;
    private const int RenderBufHeight = ScreenHeight + 1; // one extra line to simplify the blending

    /// <summary>What the monitor shows with NTSC on, in true colour.</summary>
    public Surface32? TrueColorScreen { get; private set; }

    private uint[]? _renderBuf;
    private NtscWriter? _ntscGfxWriter;
    private NtscWriter? _ntscTextWriter;

    /// <summary>A pixel writer: the colour of each pixel from the window of bits and the phase.</summary>
    private sealed class NtscWriter(NtscWriterKind kind, uint[][] table)
    {
        public NtscWriterKind Kind { get; } = kind;

        public uint GetColor(uint window, int phase) => Kind switch
        {
            NtscWriterKind.Color => table[phase][(window >> 1) & (NtscColors - 1)],
            NtscWriterKind.Mono => table[0][(window >> 1) & (NtscColors - 1)],
            _ => ((window >> 3) & 1) != 0 ? 0xFFFFFFu : 0u, // PixelWriterMono, white
        };

        /// <summary>The NTSC writers average the lines around the one in between; the sharp writer repeats its line.</summary>
        public bool Blends => Kind != NtscWriterKind.White;
    }

    private enum NtscWriterKind
    {
        Color,
        Mono,
        White,
    }

    private void InitNtsc()
    {
        TrueColorScreen = new Surface32(ScreenWidth, ScreenHeight);
        _renderBuf = new uint[RenderBufHeight * RenderBufWidth];

        // ScummVM builds the graphics writer's table first, then the text writer's, and the
        // filters keep their state from one table to the next (static variables there)
        var filters = new NtscFilters();
        _ntscGfxWriter = Options.Color
            ? new NtscWriter(NtscWriterKind.Color, BuildColorNtscTable(filters))
            : new NtscWriter(NtscWriterKind.Mono, [BuildMonoNtscTable(filters)]);
        _ntscTextWriter = Options.MonoText
            ? new NtscWriter(NtscWriterKind.White, [])
            : new NtscWriter(NtscWriterKind.Mono, [BuildMonoNtscTable(filters)]);
    }

    /// <summary>The NTSC tables as a display with these options builds them, for tests: the graphics writer's, then the text writer's.</summary>
    internal static (uint[][] Graphics, uint[]? Text) BuildNtscTables(bool color, bool monoText)
    {
        var filters = new NtscFilters();
        var graphics = color ? BuildColorNtscTable(filters) : [BuildMonoNtscTable(filters)];
        return (graphics, monoText ? null : BuildMonoNtscTable(filters));
    }

    private void RenderNtsc(int startY, int endY, Func<int, int, byte> getBits, NtscWriter writer)
    {
        var buf = _renderBuf!;
        for (int y = startY; y < endY; ++y)
        {
            int lastBit = 0;
            int ptr = y * 2 * RenderBufWidth;
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

                // because of the pre-render, the extra chunk at the end feeds in more bits
                for (int b = 0; b < 14; ++b)
                {
                    window = (window << 1) | (uint)(bits & 1);
                    bits >>= 1;
                    buf[ptr++] = writer.GetColor(window, phase);
                    phase = (phase + 1) & 3;
                }
            }
        }

        BlendScanlines(startY, endY, writer.Blends);

        // the line in between the graphics and the first line drawn now is redone, with the
        // graphics writer's blending
        if (startY > 0)
        {
            --startY;
            BlendScanlines(startY, startY + 1, _ntscGfxWriter!.Blends);
        }

        var screen = TrueColorScreen!;
        for (int line = startY * 2; line < endY * 2; line++)
        {
            buf.AsSpan(line * RenderBufWidth + PreRender, ScreenWidth).CopyTo(screen.Row(line));
        }

        Dirty = true;
    }

    /// <summary>
    /// Fills the line in between each rendered line and the next (reading the line after the
    /// last): their average for the NTSC writers (BlendBright, or BlendDim with scanlines), a copy
    /// of the line above for the sharp one (LineDoubleBright, or LineDoubleDim).
    /// </summary>
    private void BlendScanlines(int yStart, int yEnd, bool average)
    {
        var buf = _renderBuf!;
        bool scanlines = Options.Scanlines;
        for (int y = yStart; y < yEnd; ++y)
        {
            int line = y * 2 * RenderBufWidth;
            for (int x = 0; x < RenderBufWidth; ++x)
            {
                uint color1 = buf[line + x];
                uint color2 = buf[line + 2 * RenderBufWidth + x];
                buf[line + RenderBufWidth + x] =
                    (uint)Blend((byte)(color1 >> 16), (byte)(color2 >> 16), average, scanlines) << 16
                    | (uint)Blend((byte)(color1 >> 8), (byte)(color2 >> 8), average, scanlines) << 8
                    | Blend((byte)color1, (byte)color2, average, scanlines);
            }
        }
    }

    private static byte Blend(byte c1, byte c2, bool average, bool scanlines) => (average, scanlines) switch
    {
        (true, false) => (byte)((c1 + c2) >> 1), // BlendBright
        (true, true) => (byte)(((c1 + c2) >> 2) + ((c1 + c2) >> 3)), // BlendDim
        (false, false) => c1, // LineDoubleBright
        (false, true) => (byte)((c1 >> 1) + (c1 >> 2)), // LineDoubleDim
    };

    /// <summary>
    /// The colour of every twelve-bit window at each of the four colour phases: the bits as a
    /// composite signal through the filters, the chroma demodulated at the phase into I and Q, then
    /// YIQ to RGB. Black windows lose their ghosting, as ScummVM's NTSC_REMOVE_BLACK_GHOSTING does.
    /// </summary>
    private static uint[][] BuildColorNtscTable(NtscFilters filters)
    {
        var table = new uint[ColorPhases][];
        for (int phase = 0; phase < ColorPhases; ++phase)
        {
            table[phase] = new uint[NtscColors];
            double phi = DegToRad(phase * 90.0 + 45.0);
            for (uint s = 0; s < NtscColors; ++s)
            {
                uint t = s;
                double y = 0;
                double i = 0.0;
                double q = 0.0;

                for (int n = 0; n < 12; ++n)
                {
                    double z = (t & 0x800) != 0 ? 1.0 : 0.0;
                    t <<= 1;

                    for (int k = 0; k < 2; k++)
                    {
                        double zz = filters.Signal(z);
                        double c = filters.Chroma(zz);
                        y = filters.Luma(zz - c);

                        c = c * 2.0;
                        i = i + (c * Math.Cos(phi) - i) / 8.0;
                        q = q + (c * Math.Sin(phi) - q) / 8.0;

                        phi += DegToRad(45.0);
                    }
                }

                // YIQ to RGB
                double r64 = y + (0.956 * i) + (0.621 * q);
                double g64 = y + (-0.272 * i) + (-0.647 * q);
                double b64 = y + (-1.105 * i) + (1.702 * q);

                byte r = (byte)(Math.Clamp(r64, 0.0, 1.0) * 255);
                byte g = (byte)(Math.Clamp(g64, 0.0, 1.0) * 255);
                byte b = (byte)(Math.Clamp(b64, 0.0, 1.0) * 255);

                if ((s & 0xf) == 0)
                {
                    r = g = b = 0;
                }

                table[phase][s] = Surface32.Pack(r, g, b);
            }
        }

        return table;
    }

    /// <summary>The brightness of every twelve-bit window: the luma of the same filters.</summary>
    private static uint[] BuildMonoNtscTable(NtscFilters filters)
    {
        var table = new uint[NtscColors];
        for (uint s = 0; s < NtscColors; ++s)
        {
            uint t = s;
            double y = 0;

            for (int n = 0; n < 12; ++n)
            {
                double z = (t & 0x800) != 0 ? 1.0 : 0.0;
                t <<= 1;

                for (int k = 0; k < 2; k++)
                {
                    double zz = filters.Signal(z);
                    double c = filters.Chroma(zz);
                    y = filters.Luma(zz - c);
                }
            }

            byte brightness = (byte)(Math.Clamp(y, 0.0, 1.0) * 255);
            table[s] = Surface32.Pack(brightness, brightness, brightness);
        }

        return table;
    }

    /// <summary>Math::deg2rad for doubles.</summary>
    private static double DegToRad(double degrees) => degrees * 0.0174532925199433;

    /// <summary>
    /// ScummVM's filterSignal, filterChroma and filterLuma: second-order IIR filters whose state
    /// carries over from one call to the next, and from one table to the next.
    /// </summary>
    private sealed class NtscFilters
    {
        private readonly double[] _chromaX = new double[3];
        private readonly double[] _chromaY = new double[3];
        private readonly double[] _lumaX = new double[3];
        private readonly double[] _lumaY = new double[3];
        private readonly double[] _signalX = new double[3];
        private readonly double[] _signalY = new double[3];

        public double Chroma(double z)
        {
            var x = _chromaX;
            var y = _chromaY;
            x[0] = x[1];
            x[1] = x[2];
            x[2] = z / 7.438011255;

            y[0] = y[1];
            y[1] = y[2];
            y[2] = -x[0] + x[2] + (-0.7318893645 * y[0]) + (1.2336442711 * y[1]);

            return y[2];
        }

        public double Luma(double z)
        {
            var x = _lumaX;
            var y = _lumaY;
            x[0] = x[1];
            x[1] = x[2];
            x[2] = z / 13.71331570;

            y[0] = y[1];
            y[1] = y[2];
            y[2] = x[0] + x[2] + (2.0 * x[1]) + (-0.3961075449 * y[0]) + (1.1044202472 * y[1]);

            return y[2];
        }

        public double Signal(double z)
        {
            var x = _signalX;
            var y = _signalY;
            x[0] = x[1];
            x[1] = x[2];
            x[2] = z / 7.614490548;

            y[0] = y[1];
            y[1] = y[2];
            y[2] = x[0] + x[2] + (2.0 * x[1]) + (-0.2718798058 * y[0]) + (0.7465656072 * y[1]);

            return y[2];
        }
    }
}
