using Daventry.Agi.Resources;
using Daventry.Core.Hosting;

namespace Daventry.Agi.Graphics;

/// <summary>
/// Decodes and draws AGI picture resources. Port of ScummVM's PictureMgr.
/// <para>
/// AGI pictures are vector based and contain the visual and priority screens. Drawing instructions
/// begin with an opcode byte in the range F0 to FF. Parameters are one byte each, except in the
/// AGI v3 nibble compression scheme where the F0 and F2 parameters are one nibble; that is flagged
/// in the picture's directory entry. An instruction ends at the next byte in the opcode range.
/// Extra bytes and unknown opcodes are ignored. Pictures end with FF.
/// </para>
/// PreAGI picture variants are implemented as subclasses, so the AGI behaviour here is the baseline.
/// </summary>
public class PictureMgr
{
    public const int DefaultWidth = 160;
    public const int DefaultHeight = 168;

    private static readonly ushort[] BinaryList =
    [
        0x8000, 0x4000, 0x2000, 0x1000, 0x800, 0x400, 0x200, 0x100,
        0x80, 0x40, 0x20, 0x10, 0x8, 0x4, 0x2, 0x1,
    ];

    private static readonly byte[] CircleList = [0, 1, 4, 9, 16, 25, 37, 50];

    private static readonly ushort[] CircleData =
    [
        0x8000,
        0x0000, 0xE000, 0x0000,
        0x7000, 0xF800, 0x0F800, 0x0F800, 0x7000,
        0x3800, 0x7C00, 0x0FE00, 0x0FE00, 0x0FE00, 0x7C00, 0x3800,
        0x1C00, 0x7F00, 0x0FF80, 0x0FF80, 0x0FF80, 0x0FF80, 0x0FF80, 0x7F00, 0x1C00,
        0x0E00, 0x3F80, 0x7FC0, 0x7FC0, 0x0FFE0, 0x0FFE0, 0x0FFE0, 0x7FC0, 0x7FC0, 0x3F80, 0x1F00, 0x0E00,
        0x0F80, 0x3FE0, 0x7FF0, 0x7FF0, 0x0FFF8, 0x0FFF8, 0x0FFF8, 0x0FFF8, 0x0FFF8, 0x7FF0, 0x7FF0, 0x3FE0, 0x0F80,
        0x07C0, 0x1FF0, 0x3FF8, 0x7FFC, 0x7FFC, 0x0FFFE, 0x0FFFE, 0x0FFFE, 0x0FFFE, 0x0FFFE, 0x7FFC, 0x7FFC, 0x3FF8, 0x1FF0, 0x07C0,
    ];

    private readonly ILog _log;
    private byte[] _data = [];
    private int _dataSize;
    private int _dataOffset;
    private bool _dataOffsetNibble;
    private bool _nibbleMode;
    private byte _patCode;
    private byte _patNum;
    private bool _priOn;
    private bool _scrOn;
    private byte _scrColor;
    private byte _priColor;

    public PictureMgr(AgiGameState state, GfxMgr gfx, AgiResourceManager resources, ILog log)
    {
        State = state;
        Gfx = gfx;
        Resources = resources;
        _log = log;
    }

    protected AgiGameState State { get; }

    protected GfxMgr Gfx { get; }

    protected AgiResourceManager Resources { get; }

    public int ResourceNr { get; private set; }

    protected int Width { get; private set; }

    protected int Height { get; private set; }

    /// <summary>Lowest byte value that is an opcode. PreAGI variants lower this.</summary>
    protected byte MinCommand { get; set; } = 0xF0;

    protected virtual byte InitialPriorityColor => 4;

    /// <summary>
    /// Draws a picture by resource number to the visual and priority screens. The resource is
    /// loaded if needed. Optionally clears the screens first.
    /// </summary>
    public void DecodePicture(int resourceNr, bool clearScreen, bool agi256 = false, int width = DefaultWidth, int height = DefaultHeight)
    {
        var entry = Resources.Directory.Picture[resourceNr];
        var data = Resources.Load(AgiResourceType.Picture, resourceNr)
            ?? throw new AgiResourceException($"Picture {resourceNr} could not be loaded");

        ResourceNr = resourceNr;
        _data = data;
        // ScummVM uses the directory length, which for nibble pictures exceeds the stored data and
        // relies on padding. Bounding by the buffer is equivalent because pictures end with FF.
        _dataSize = Math.Min((int)entry.Length, data.Length);
        _nibbleMode = (entry.Flags & AgiResourceFlags.PictureV3NibbleParm) != 0;
        Width = width;
        Height = height;

        if (clearScreen)
        {
            Gfx.Clear(15, InitialPriorityColor); // white, priority 4 (or 1 for PreAGI)
        }

        if (!agi256)
        {
            DrawPicture();
        }
        else
        {
            DrawPictureAgi256();
        }

        if (clearScreen)
        {
            State.ClearImageStack();
        }

        State.RecordImageStackCall(ImageStackCall.AddPicture, resourceNr, clearScreen ? 1 : 0, agi256 ? 1 : 0, 0, 0, 0, 0);
    }

    /// <summary>Draws a picture from a raw buffer. Used by PreAGI games.</summary>
    public void DecodePictureFromBuffer(byte[] data, int length, bool clearScreen, int width = DefaultWidth, int height = DefaultHeight)
    {
        _data = data;
        _dataSize = Math.Min(length, data.Length);
        _nibbleMode = false;
        Width = width;
        Height = height;

        if (clearScreen)
        {
            Gfx.Clear(15, InitialPriorityColor);
        }

        DrawPicture();
    }

    /// <summary>Renders the drawn picture from the active screen to the display screen.</summary>
    public void ShowPicture(int x = 0, int y = 0, int width = DefaultWidth, int height = DefaultHeight)
    {
        Gfx.RenderBlock(x, y, width, height);
    }

    /// <summary>Renders the drawn picture to the display with the platform's transition effect.</summary>
    public void ShowPictureWithTransition()
    {
        Width = GfxMgr.ScriptWidth;
        Height = GfxMgr.ScriptHeight;

        if (!State.AutomaticRestoreGame)
        {
            // only do transitions when we are not restoring a saved game
            if (!State.GfxMode)
            {
                // not yet in graphics mode, so set the graphics mode palette now
                Gfx.SetPalette(true);
            }

            switch (Gfx.RenderMode)
            {
                case AgiRenderMode.Amiga:
                case AgiRenderMode.Apple2Gs:
                case AgiRenderMode.AtariSt:
                    // TODO: Amiga and Atari ST transition effects; render directly for now
                    Gfx.RenderBlock(0, 0, GfxMgr.ScriptWidth, GfxMgr.ScriptHeight, false);
                    Gfx.CopyDisplayToScreen();
                    return;
            }
        }

        Gfx.RenderBlock(0, 0, GfxMgr.ScriptWidth, GfxMgr.ScriptHeight);
    }

    // ---- data access ----

    protected byte GetNextByte()
    {
        if (!_dataOffsetNibble)
        {
            return At(_dataOffset++);
        }

        byte curByte = (byte)(At(_dataOffset++) << 4);
        return (byte)((At(_dataOffset) >> 4) | curByte);
    }

    /// <summary>Gets the next parameter byte, or returns false without advancing if the next byte is an opcode.</summary>
    protected bool GetNextParamByte(out byte b)
    {
        byte value = GetNextByte();
        if (value >= MinCommand)
        {
            _dataOffset--;
            b = 0;
            return false;
        }

        b = value;
        return true;
    }

    protected byte GetNextNibble()
    {
        if (!_dataOffsetNibble)
        {
            _dataOffsetNibble = true;
            return (byte)(At(_dataOffset) >> 4);
        }

        _dataOffsetNibble = false;
        return (byte)(At(_dataOffset++) & 0x0F);
    }

    protected virtual bool GetNextXCoordinate(out byte x) => GetNextParamByte(out x);

    protected virtual bool GetNextYCoordinate(out byte y) => GetNextParamByte(out y);

    /// <summary>Both coordinates, or false. If only x is present the offset advances by one and x is ignored.</summary>
    protected bool GetNextCoordinates(out byte x, out byte y)
    {
        y = 0;
        return GetNextXCoordinate(out x) && GetNextYCoordinate(out y);
    }

    /// <summary>Validates picture coordinates and translates them to GfxMgr coordinates. PreAGI overrides this for offset drawing.</summary>
    protected virtual bool GetGraphicsCoordinates(ref int x, ref int y)
        => 0 <= x && x < Width && 0 <= y && y < Height;

    private byte At(int offset) => offset < _data.Length ? _data[offset] : (byte)0;

    // ---- drawing ----

    protected void PutVirtPixel(int x, int y)
    {
        if (!GetGraphicsCoordinates(ref x, ref y))
        {
            return;
        }

        var drawMask = GfxScreenMask.Visual & 0;
        if (_priOn)
        {
            drawMask |= GfxScreenMask.Priority;
        }

        if (_scrOn)
        {
            drawMask |= GfxScreenMask.Visual;
        }

        Gfx.PutPixel(x, y, drawMask, _scrColor, _priColor);
    }

    protected virtual void DrawPicture()
    {
        _dataOffset = 0;
        _dataOffsetNibble = false;
        _patCode = 0;
        _patNum = 0;
        _priOn = false;
        _scrOn = false;
        _scrColor = 15;
        _priColor = 4;

        while (_dataOffset < _dataSize)
        {
            byte curByte = GetNextByte();
            switch (curByte)
            {
                case 0xF0:
                    if (!_nibbleMode)
                    {
                        DrawSetColor();
                    }
                    else
                    {
                        DrawSetNibbleColor();
                    }

                    _scrOn = true;
                    break;
                case 0xF1:
                    _scrOn = false;
                    break;
                case 0xF2:
                    if (!_nibbleMode)
                    {
                        DrawSetPriority();
                    }
                    else
                    {
                        DrawSetNibblePriority();
                    }

                    _priOn = true;
                    break;
                case 0xF3:
                    _priOn = false;
                    break;
                case 0xF4:
                    YCorner();
                    break;
                case 0xF5:
                    XCorner();
                    break;
                case 0xF6:
                    DrawLineAbsolute();
                    break;
                case 0xF7:
                    DrawLineShort();
                    break;
                case 0xF8:
                    DrawFill();
                    break;
                case 0xF9:
                    _patCode = GetNextByte();
                    break;
                case 0xFA:
                    PlotBrush();
                    break;
                case 0xFF:
                    return; // end of data
                default:
                    _log.Warning("agi", $"Unknown picture opcode {curByte:X2} at {_dataOffset - 1:X4} in picture {ResourceNr}");
                    break;
            }
        }
    }

    /// <summary>AGI256 pictures are raw 8-bit bitmaps for the visual screen only.</summary>
    private void DrawPictureAgi256()
    {
        int maxLength = Width * Height;
        int x = 0;
        int y = 0;
        for (int i = 0; i < _dataSize; i++)
        {
            Gfx.PutPixel(x, y, GfxScreenMask.Visual, _data[i], 0);
            x++;
            if (x >= Width)
            {
                x = 0;
                y++;
                if (y >= Height)
                {
                    break;
                }
            }
        }

        if (_dataSize < maxLength)
        {
            _log.Warning("agi", $"Undersized AGI256 picture resource {ResourceNr}, using it anyway. Filling rest with white");
            for (int i = _dataSize; i < maxLength; i++)
            {
                x++;
                if (x >= Width)
                {
                    x = 0;
                    y++;
                    if (y >= Height)
                    {
                        break;
                    }
                }

                Gfx.PutPixel(x, y, GfxScreenMask.Visual, 15, 0);
            }
        }
        else if (_dataSize > maxLength)
        {
            _log.Warning("agi", $"Oversized AGI256 picture resource {ResourceNr}, decoding only {Width}x{Height} part of it");
        }
    }

    private void DrawSetColor()
    {
        _scrColor = GetNextByte();
        if (Gfx.RenderMode == AgiRenderMode.Cga)
        {
            _scrColor = Gfx.GetCgaMixtureColor(_scrColor);
        }
    }

    private void DrawSetPriority() => _priColor = GetNextByte();

    private void DrawSetNibbleColor()
    {
        _scrColor = GetNextNibble();
        if (Gfx.RenderMode == AgiRenderMode.Cga)
        {
            _scrColor = Gfx.GetCgaMixtureColor(_scrColor);
        }
    }

    private void DrawSetNibblePriority() => _priColor = GetNextNibble();

    /// <summary>Series of lines at right angles. Start point, then alternating x and y coordinates.</summary>
    protected void XCorner(bool skipOtherCoords = false)
    {
        if (!GetNextCoordinates(out byte x1, out byte y1))
        {
            return;
        }

        PutVirtPixel(x1, y1);
        while (true)
        {
            if (!GetNextXCoordinate(out byte x2))
            {
                break;
            }

            if (skipOtherCoords && !GetNextParamByte(out _))
            {
                break;
            }

            DrawLine(x1, y1, x2, y1);
            x1 = x2;

            if (skipOtherCoords && !GetNextParamByte(out _))
            {
                break;
            }

            if (!GetNextYCoordinate(out byte y2))
            {
                break;
            }

            DrawLine(x1, y1, x1, y2);
            y1 = y2;
        }
    }

    /// <summary>Series of lines at right angles. Start point, then alternating y and x coordinates.</summary>
    protected void YCorner(bool skipOtherCoords = false)
    {
        if (!GetNextCoordinates(out byte x1, out byte y1))
        {
            return;
        }

        PutVirtPixel(x1, y1);
        while (true)
        {
            if (skipOtherCoords && !GetNextParamByte(out _))
            {
                break;
            }

            if (!GetNextYCoordinate(out byte y2))
            {
                break;
            }

            DrawLine(x1, y1, x1, y2);
            y1 = y2;

            if (!GetNextXCoordinate(out byte x2))
            {
                break;
            }

            if (skipOtherCoords && !GetNextParamByte(out _))
            {
                break;
            }

            DrawLine(x1, y1, x2, y1);
            x1 = x2;
        }
    }

    /// <summary>Draws a circle or square, optionally with a splatter pattern, sized by the current pattern code. Originally from NAGI.</summary>
    protected virtual void PlotPattern(byte x, byte y)
    {
        int penX = x;
        int penY = y;
        int penSize = _patCode & 0x07;
        int circleIndex = CircleList[penSize];

        // x position = pen_x - pen.size / 2
        penX = penX * 2 - penSize;
        if (penX < 0)
        {
            penX = 0;
        }

        int temp16 = Width * 2 - 2 * penSize;
        if (penX >= temp16)
        {
            penX = temp16;
        }

        penX /= 2;
        int penFinalX = penX;

        // y position = pen_y - pen.size
        penY -= penSize;
        if (penY < 0)
        {
            penY = 0;
        }

        temp16 = 167 - 2 * penSize;
        if (penY >= temp16)
        {
            penY = temp16;
        }

        int penFinalY = penY;

        byte t = (byte)(_patNum | 0x01);
        temp16 = (penSize << 1) + 1; // pen size
        penFinalY += temp16; // the last row of this shape
        temp16 <<= 1;
        int penWidth = temp16;

        bool circleCond = (_patCode & 0x10) != 0;
        const int counterStep = 4;
        const int ditherCond = 0x02;

        for (; penY < penFinalY; penY++)
        {
            ushort circleWord = CircleData[circleIndex++];
            for (int counter = 0; counter <= penWidth; counter += counterStep)
            {
                if (circleCond || (BinaryList[counter >> 1] & circleWord) != 0)
                {
                    if ((_patCode & 0x20) != 0)
                    {
                        byte temp8 = (byte)(t % 2);
                        t >>= 1;
                        if (temp8 != 0)
                        {
                            t ^= 0xB8;
                        }
                    }

                    // box plot, or circle plot that passes the dither test
                    if ((_patCode & 0x20) == 0 || (t & 0x03) == ditherCond)
                    {
                        PutVirtPixel(penX, penY);
                    }
                }

                penX++;
            }

            penX = penFinalX;
        }
    }

    protected virtual void PlotBrush()
    {
        while (true)
        {
            if ((_patCode & 0x20) != 0)
            {
                if (!GetNextParamByte(out _patNum))
                {
                    break;
                }
            }

            if (!GetNextCoordinates(out byte x1, out byte y1))
            {
                break;
            }

            PlotPattern(x1, y1);
        }
    }

    /// <summary>
    /// Draws a horizontal, vertical or diagonal line. Originally from Sarien: a routine sent by
    /// Joshua Neal, modified by Stuart George.
    /// </summary>
    protected virtual void DrawLine(int x1, int y1, int x2, int y2)
    {
        x1 = Math.Clamp(x1, 0, Width - 1);
        x2 = Math.Clamp(x2, 0, Width - 1);
        y1 = Math.Clamp(y1, 0, Height - 1);
        y2 = Math.Clamp(y2, 0, Height - 1);

        if (x1 == x2)
        {
            if (y1 > y2)
            {
                (y1, y2) = (y2, y1);
            }

            for (; y1 <= y2; y1++)
            {
                PutVirtPixel(x1, y1);
            }

            return;
        }

        if (y1 == y2)
        {
            if (x1 > x2)
            {
                (x1, x2) = (x2, x1);
            }

            for (; x1 <= x2; x1++)
            {
                PutVirtPixel(x1, y1);
            }

            return;
        }

        int stepX = 1;
        int deltaX = x2 - x1;
        if (deltaX < 0)
        {
            stepX = -1;
            deltaX = -deltaX;
        }

        int stepY = 1;
        int deltaY = y2 - y1;
        if (deltaY < 0)
        {
            stepY = -1;
            deltaY = -deltaY;
        }

        int i;
        int detdelta;
        int errorX;
        int errorY;
        if (deltaY > deltaX)
        {
            i = deltaY;
            detdelta = deltaY;
            errorX = deltaY / 2;
            errorY = 0;
        }
        else
        {
            i = deltaX;
            detdelta = deltaX;
            errorX = 0;
            errorY = deltaX / 2;
        }

        int x = x1;
        int y = y1;
        PutVirtPixel(x, y);
        do
        {
            errorY += deltaY;
            if (errorY >= detdelta)
            {
                errorY -= detdelta;
                y += stepY;
            }

            errorX += deltaX;
            if (errorX >= detdelta)
            {
                errorX -= detdelta;
                x += stepX;
            }

            PutVirtPixel(x, y);
            i--;
        }
        while (i > 0);
    }

    /// <summary>Short lines between positions given as packed relative displacements.</summary>
    private void DrawLineShort()
    {
        if (!GetNextCoordinates(out byte bx, out byte by))
        {
            return;
        }

        int x1 = bx;
        int y1 = by;
        PutVirtPixel(x1, y1);
        while (true)
        {
            if (!GetNextParamByte(out byte disp))
            {
                break;
            }

            int dx = (disp >> 4) & 0x0F;
            int dy = disp & 0x0F;
            if ((dx & 0x08) != 0)
            {
                dx = -(dx & 0x07);
            }

            if ((dy & 0x08) != 0)
            {
                dy = -(dy & 0x07);
            }

            DrawLine(x1, y1, x1 + dx, y1 + dy);
            x1 += dx;
            y1 += dy;
        }
    }

    /// <summary>Lines between absolute positions.</summary>
    private void DrawLineAbsolute()
    {
        if (!GetNextCoordinates(out byte x1, out byte y1))
        {
            return;
        }

        PutVirtPixel(x1, y1);
        while (true)
        {
            if (!GetNextCoordinates(out byte x2, out byte y2))
            {
                break;
            }

            DrawLine(x1, y1, x2, y2);
            x1 = x2;
            y1 = y2;
        }
    }

    /// <summary>Flood fills from a series of start positions.</summary>
    protected virtual void DrawFill()
    {
        while (GetNextCoordinates(out byte x, out byte y))
        {
            DrawFill(x, y);
        }
    }

    /// <summary>Scanline flood fill from one start position.</summary>
    protected virtual void DrawFill(int x, int y)
    {
        if (!_scrOn && !_priOn)
        {
            return;
        }

        var stack = new Stack<(int X, int Y)>();
        stack.Push((x, y));

        while (stack.Count > 0)
        {
            var (px, py) = stack.Pop();
            if (!DrawFillCheck(px, py, false))
            {
                continue;
            }

            // scan for the left border
            int c;
            for (c = px - 1; DrawFillCheck(c, py, true); c--)
            {
            }

            bool newSpanUp = true;
            bool newSpanDown = true;
            for (c++; DrawFillCheck(c, py, true); c++)
            {
                PutVirtPixel(c, py);
                if (DrawFillCheck(c, py - 1, false))
                {
                    if (newSpanUp)
                    {
                        stack.Push((c, py - 1));
                        newSpanUp = false;
                    }
                }
                else
                {
                    newSpanUp = true;
                }

                if (DrawFillCheck(c, py + 1, false))
                {
                    if (newSpanDown)
                    {
                        stack.Push((c, py + 1));
                        newSpanDown = false;
                    }
                }
                else
                {
                    newSpanDown = true;
                }
            }
        }
    }

    /// <summary>
    /// Whether flood fill may paint this position. <paramref name="horizontalCheck"/> is true while
    /// scanning the current line for a boundary; Troll's Tale overrides fill behaviour based on it.
    /// </summary>
    protected virtual bool DrawFillCheck(int x, int y, bool horizontalCheck)
    {
        if (!GetGraphicsCoordinates(ref x, ref y))
        {
            return false;
        }

        byte screenColor = Gfx.GetColor(x, y);
        byte screenPriority = Gfx.GetPriority(x, y);

        if (!_priOn && _scrOn && _scrColor != 15)
        {
            return screenColor == 15;
        }

        if (_priOn && !_scrOn && _priColor != 4)
        {
            return screenPriority == 4;
        }

        return _scrOn && screenColor == 15 && _scrColor != 15;
    }
}
