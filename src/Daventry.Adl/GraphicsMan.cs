using Daventry.Core.Formats;
using Daventry.Core.Graphics;

namespace Daventry.Adl;

/// <summary>
/// Draws ADL pictures into the Apple II frame buffer. Port of ScummVM's GraphicsMan_v1 (Mystery
/// House: four-connected lines and Applesoft shape tables), GraphicsMan_v2 (Mission Asteroid and
/// Wizard and the Princess to Ulysses: lines, corners and pattern fills) and GraphicsMan_v3 (Time
/// Zone and The Dark Crystal, whose fill differs).
/// </summary>
public class GraphicsManV1
{
    private static readonly byte[] Stepping =
    [
        0xff, 0xfe, 0xfa, 0xf4, 0xec, 0xe1, 0xd4, 0xc5,
        0xb4, 0xa1, 0x8d, 0x78, 0x61, 0x49, 0x31, 0x18,
        0xff,
    ];

    public GraphicsManV1(DisplayA2 display)
    {
        Display = display;
        Bounds = new Rect(0, 0, 280, 160);
    }

    /// <summary>The area pictures may draw in: the top 160 lines, above the text window.</summary>
    public Rect Bounds { get; set; }

    protected DisplayA2 Display { get; }

    protected virtual byte ClearColor => 0x00;

    public void ClearScreen()
    {
        Display.SetMode(Adl.Display.Mode.Mixed);
        Display.Clear(ClearColor);
    }

    /// <summary>Draws a four-connected line, Applesoft BASIC's HPLOT TO.</summary>
    public void DrawLine(int x1, int y1, int x2, int y2, byte color)
    {
        int deltaX = x2 - x1;
        int xStep = 1;

        if (deltaX < 0)
        {
            deltaX = -deltaX;
            xStep = -1;
        }

        int deltaY = y2 - y1;
        int yStep = -1;

        if (deltaY > 0)
        {
            deltaY = -deltaY;
            yStep = 1;
        }

        int x = x1, y = y1;
        short steps = (short)(deltaX - deltaY + 1);
        int err = deltaX + deltaY;

        while (true)
        {
            PutPixel(x, y, color);

            if (--steps == 0)
            {
                return;
            }

            if (err < 0)
            {
                y += yStep;
                err += deltaX;
            }
            else
            {
                x += xStep;
                err += deltaY;
            }
        }
    }

    /// <summary>Draws an Applesoft shape table entry, BASIC's DRAW; <paramref name="x"/> and <paramref name="y"/> end where the pen stopped.</summary>
    public void DrawShape(SeekableReadStream corners, ref int x, ref int y, byte rotation = 0, byte scaling = 1, byte color = 0x7f)
    {
        int quadrant = rotation >> 4;
        rotation &= 0xf;
        byte xStep = Stepping[rotation];
        byte yStep = (byte)(Stepping[(rotation ^ 0xf) + 1] + 1);

        while (true)
        {
            byte b = corners.ReadByte();

            if (corners.Eos)
            {
                throw new InvalidDataException("Error reading corners");
            }

            if (b == 0)
            {
                return;
            }

            do
            {
                byte xFrac = 0x80;
                byte yFrac = 0x80;
                for (int j = 0; j < scaling; ++j)
                {
                    if (xFrac + xStep + 1 > 255)
                    {
                        DrawShapePixel(ref x, ref y, color, b, quadrant);
                    }

                    xFrac = (byte)(xFrac + xStep + 1);
                    if (yFrac + yStep > 255)
                    {
                        DrawShapePixel(ref x, ref y, color, b, quadrant + 1);
                    }

                    yFrac = (byte)(yFrac + yStep);
                }

                b >>= 3;
            }
            while (b != 0);
        }
    }

    /// <summary>Draws a picture at an offset.</summary>
    public virtual void DrawPic(SeekableReadStream pic, int posX, int posY)
    {
        bool newLine = false;
        byte oldX = 0, oldY = 0;
        while (true)
        {
            byte x = pic.ReadByte();
            byte y = pic.ReadByte();

            if (pic.Eos)
            {
                throw new InvalidDataException("Error reading picture");
            }

            if (x == 0xff && y == 0xff)
            {
                return;
            }

            if (x == 0 && y == 0)
            {
                newLine = true;
                continue;
            }

            x = (byte)(x + posX);
            y = (byte)(y + posY);

            if (y > 160)
            {
                y = 160;
            }

            if (newLine)
            {
                PutPixel(x, y, 0x7f);
                newLine = false;
            }
            else
            {
                DrawLine(oldX, oldY, x, y, 0x7f);
            }

            oldX = x;
            oldY = y;
        }
    }

    protected void PutPixel(int x, int y, byte color)
    {
        if (Bounds.Contains(x, y))
        {
            Display.PutPixel(x, y, color);
        }
    }

    private void DrawShapePixel(ref int x, ref int y, byte color, byte bits, int quadrant)
    {
        if ((bits & 4) != 0)
        {
            PutPixel(x, y, color);
        }

        int b = bits + quadrant;

        if ((b & 1) != 0)
        {
            x += (b & 2) != 0 ? -1 : 1;
        }
        else
        {
            y += (b & 2) != 0 ? 1 : -1;
        }
    }
}

/// <summary>Pictures of the ADL v2 games: opcodes for corners, relative and absolute lines, fills and colours.</summary>
public class GraphicsManV2 : GraphicsManV1
{
    private static readonly byte[,] FillPatterns =
    {
        { 0x00, 0x00, 0x00, 0x00 },
        { 0x80, 0x80, 0x80, 0x80 },
        { 0xff, 0xff, 0xff, 0xff },
        { 0x7f, 0x7f, 0x7f, 0x7f },
        { 0x2a, 0x55, 0x2a, 0x55 },
        { 0xaa, 0xd5, 0xaa, 0xd5 },
        { 0x55, 0x2a, 0x55, 0x2a },
        { 0xd5, 0xaa, 0xd5, 0xaa },
        { 0x33, 0x66, 0x4c, 0x19 },
        { 0xb3, 0xe6, 0xcc, 0x99 },
        { 0x22, 0x44, 0x08, 0x11 },
        { 0xa2, 0xc4, 0x88, 0x91 },
        { 0x11, 0x22, 0x44, 0x08 },
        { 0x91, 0xa2, 0xc4, 0x88 },
        { 0x6e, 0x5d, 0x3b, 0x77 },
        { 0xee, 0xdd, 0xbb, 0xf7 },
        { 0x5d, 0x3b, 0x77, 0x6e },
        { 0xdd, 0xbb, 0xf7, 0xee },
        { 0x66, 0x4c, 0x19, 0x33 },
        { 0xe6, 0xcc, 0x99, 0xb3 },
        { 0x33, 0x66, 0x4c, 0x19 },
        { 0xb3, 0xe6, 0xcc, 0x99 },
    };

    private byte _color;
    private int _offsetX, _offsetY;

    public GraphicsManV2(DisplayA2 display)
        : base(display)
    {
    }

    protected override byte ClearColor => 0xff;

    public override void DrawPic(SeekableReadStream pic, int posX, int posY)
    {
        // NOTE: the original only resets the colour for overlays, so a room picture that draws
        // without setting a colour uses the last one of the previous picture. ScummVM assumes
        // this is unintentional and does not copy it, and neither does Daventry.
        _color = 0;
        _offsetX = posX;
        _offsetY = posY;

        while (true)
        {
            byte opcode = pic.ReadByte();

            if (pic.Eos)
            {
                throw new InvalidDataException("Error reading picture");
            }

            switch (opcode)
            {
                case 0xe0:
                    DrawCorners(pic, false);
                    break;
                case 0xe1:
                    DrawCorners(pic, true);
                    break;
                case 0xe2:
                    DrawRelativeLines(pic);
                    break;
                case 0xe3:
                    DrawAbsoluteLines(pic);
                    break;
                case 0xe4:
                    Fill(pic);
                    break;
                case 0xe5:
                    ClearScreen();
                    _color = 0x00;
                    break;
                case 0xf0:
                    _color = 0x00;
                    break;
                case 0xf1:
                    _color = 0x2a;
                    break;
                case 0xf2:
                    _color = 0x55;
                    break;
                case 0xf3:
                    _color = 0x7f;
                    break;
                case 0xf4:
                    _color = 0x80;
                    break;
                case 0xf5:
                    _color = 0xaa;
                    break;
                case 0xf6:
                    _color = 0xd5;
                    break;
                case 0xf7:
                    _color = 0xff;
                    break;
                case 0xff:
                    return;
                default:
                    if (opcode >= 0xe0)
                    {
                        throw new InvalidDataException($"Invalid pic opcode {opcode:x2}");
                    }

                    // ScummVM warns "Expected pic opcode, but found data byte" and carries on
                    break;
            }
        }
    }

    protected bool CanFillAt(int x, int y, bool stopBit = false)
        => Display.GetPixelBit(x, y) != stopBit && Display.GetPixelBit(x + 1, y) != stopBit;

    protected void FillRow(int x, int y, byte pattern, bool stopBit = false)
    {
        // set pixel at p and palette
        byte color = GetPatternColor(x, y, pattern);
        Display.SetPixelPalette(x, y, color);
        Display.SetPixelBit(x, y, color);

        // fill left of p
        FillRowLeft(x, y, pattern, stopBit);

        // fill right of p
        while (++x < Bounds.Right)
        {
            if (x % 7 == 0)
            {
                color = GetPatternColor(x, y, pattern);

                // palette is set before the first bit is tested
                Display.SetPixelPalette(x, y, color);
            }

            if (Display.GetPixelBit(x, y) == stopBit)
            {
                break;
            }

            Display.SetPixelBit(x, y, color);
        }
    }

    protected static byte GetPatternColor(int x, int y, byte pattern)
    {
        if (pattern >= FillPatterns.GetLength(0))
        {
            throw new InvalidDataException($"Invalid fill pattern {pattern} encountered in picture");
        }

        int offset = (y & 1) << 1;
        offset += (x / 7) & 3;
        return FillPatterns[pattern, offset % 4];
    }

    protected virtual void FillRowLeft(int x, int y, byte pattern, bool stopBit)
    {
        byte color = GetPatternColor(x, y, pattern);

        while (--x >= Bounds.Left)
        {
            if (x % 7 == 6)
            {
                color = GetPatternColor(x, y, pattern);
                Display.SetPixelPalette(x, y, color);
            }

            if (Display.GetPixelBit(x, y) == stopBit)
            {
                break;
            }

            Display.SetPixelBit(x, y, color);
        }
    }

    protected virtual void FillAt(int x, int y, byte pattern)
    {
        bool stopBit = !Display.GetPixelBit(x, y);

        // move up into the open space above p
        while (--y >= Bounds.Top && CanFillAt(x, y, stopBit))
        {
        }

        // then fill by moving down
        while (++y < Bounds.Bottom && CanFillAt(x, y, stopBit))
        {
            FillRow(x, y, pattern, stopBit);
        }
    }

    private static bool ReadByte(SeekableReadStream pic, out byte b)
    {
        b = pic.ReadByte();

        if (pic.Eos)
        {
            throw new InvalidDataException("Error reading picture");
        }

        if (b >= 0xe0)
        {
            pic.Seek(-1, SeekOrigin.Current);
            return false;
        }

        return true;
    }

    private bool ReadPoint(SeekableReadStream pic, out int x, out int y)
    {
        x = y = 0;

        if (!ReadByte(pic, out byte b))
        {
            return false;
        }

        x = (short)((b + _offsetX) << 1);

        if (!ReadByte(pic, out b))
        {
            return false;
        }

        y = (short)(b + _offsetY);
        return true;
    }

    private void DrawCorners(SeekableReadStream pic, bool yFirst)
    {
        if (!ReadPoint(pic, out int x, out int y))
        {
            return;
        }

        bool skipX = yFirst;
        while (true)
        {
            byte b;
            int n;

            if (!skipX)
            {
                if (!ReadByte(pic, out b))
                {
                    return;
                }

                n = b + _offsetX;
                PutPixel(x, y, _color);
                n <<= 1;
                DrawLine(x, y, n, y, _color);
                x = n;
            }

            skipX = false;

            if (!ReadByte(pic, out b))
            {
                return;
            }

            n = b + _offsetY;
            PutPixel(x, y, _color);
            DrawLine(x, y, x, n, _color);
            PutPixel(x + 1, y, _color);
            DrawLine(x + 1, y, x + 1, n, _color);
            y = n;
        }
    }

    private void DrawRelativeLines(SeekableReadStream pic)
    {
        if (!ReadPoint(pic, out int x1, out int y1))
        {
            return;
        }

        PutPixel(x1, y1, _color);

        while (true)
        {
            int x2 = x1, y2 = y1;

            if (!ReadByte(pic, out byte n))
            {
                return;
            }

            int h = (n & 0x70) >> 4;
            int l = n & 7;

            x2 += (n & 0x80) != 0 ? -(h << 1) : h << 1;
            y2 += (n & 8) != 0 ? -l : l;

            DrawLine(x1, y1, x2, y2, _color);
            x1 = x2;
            y1 = y2;
        }
    }

    private void DrawAbsoluteLines(SeekableReadStream pic)
    {
        if (!ReadPoint(pic, out int x1, out int y1))
        {
            return;
        }

        PutPixel(x1, y1, _color);

        while (true)
        {
            if (!ReadPoint(pic, out int x2, out int y2))
            {
                return;
            }

            DrawLine(x1, y1, x2, y2, _color);
            x1 = x2;
            y1 = y2;
        }
    }

    private void Fill(SeekableReadStream pic)
    {
        if (!ReadByte(pic, out byte pattern))
        {
            return;
        }

        while (true)
        {
            if (!ReadPoint(pic, out int x, out int y))
            {
                return;
            }

            if (Bounds.Contains(x, y))
            {
                FillAt(x, y, pattern);
            }
        }
    }
}

/// <summary>Pictures of Time Zone and The Dark Crystal, whose fill works outward from the seed row.</summary>
public sealed class GraphicsManV3(DisplayA2 display) : GraphicsManV2(display)
{
    protected override void FillRowLeft(int x, int y, byte pattern, bool stopBit)
    {
        byte color = GetPatternColor(x, y, pattern);

        while (--x >= Bounds.Left)
        {
            // in this version, when moving left, it no longer sets the palette first
            if (!Display.GetPixelBit(x, y))
            {
                return;
            }

            if (x % 7 == 6)
            {
                color = GetPatternColor(x, y, pattern);
                Display.SetPixelPalette(x, y, color);
            }

            Display.SetPixelBit(x, y, color);
        }
    }

    protected override void FillAt(int x, int y, byte pattern)
    {
        // if the row at p cannot be filled, nothing is done
        if (!CanFillAt(x, y))
        {
            return;
        }

        FillRow(x, y, pattern);

        // fill up from p
        int qy = y;
        while (--qy >= Bounds.Top && CanFillAt(x, qy))
        {
            FillRow(x, qy, pattern);
        }

        // fill down from p
        while (++y < Bounds.Bottom && CanFillAt(x, y))
        {
            FillRow(x, y, pattern);
        }
    }
}
