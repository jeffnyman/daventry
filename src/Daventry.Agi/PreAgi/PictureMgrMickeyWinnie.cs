using Daventry.Agi.Graphics;
using Daventry.Core.Hosting;

namespace Daventry.Agi.PreAgi;

/// <summary>
/// Decodes and draws the pictures of Mickey's Space Adventure (DOS) and Winnie the Pooh
/// (DOS/Amiga, plus the Apple II/C64/CoCo format). Port of ScummVM's PictureMgr_Mickey_Winnie.
/// <para>
/// Both formats support lines, flood fills and solid-circle patterns, and have no priority
/// screen. Pictures can be drawn on top of others at an offset, which is how objects appear in
/// rooms and how the 140 pixel wide pictures sit in the middle of the 160 pixel screen. Mickey's
/// crystal animates by drawing one more instruction per frame, which <see cref="MaxStep"/>
/// controls.
/// </para>
/// </summary>
public sealed class PictureMgrMickeyWinnie : PictureMgr
{
    /// <summary>PreAGI patterns are 13 solid circles: the half heights of their vertical lines, by size.</summary>
    private static readonly byte[] CircleHeights =
    [
        0x00,
        0x01, 0x01,
        0x01, 0x02, 0x02,
        0x01, 0x02, 0x03, 0x03,
        0x02, 0x03, 0x04, 0x04, 0x04,
        0x02, 0x03, 0x04, 0x05, 0x05, 0x05,
        0x02, 0x04, 0x05, 0x05, 0x06, 0x06, 0x06,
        0x02, 0x04, 0x05, 0x06, 0x06, 0x07, 0x07, 0x07,
        0x02, 0x04, 0x06, 0x06, 0x07, 0x07, 0x08, 0x08, 0x08,
        0x03, 0x05, 0x06, 0x07, 0x08, 0x08, 0x08, 0x09, 0x09, 0x09,
        0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x09, 0x0a, 0x0a, 0x0a, 0x0a,
        0x03, 0x05, 0x07, 0x08, 0x09, 0x09, 0x0a, 0x0a, 0x0b, 0x0b, 0x0b, 0x0b,
        0x03, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0a, 0x0b, 0x0b, 0x0c, 0x0c, 0x0c, 0x0c,
    ];

    private readonly ILog _log;
    private readonly bool _isDosOrAmiga;
    private int _xOffset;
    private int _yOffset;

    public PictureMgrMickeyWinnie(GfxMgr gfx, AgiPlatform platform, ILog log)
        : base(new AgiGameState(), gfx, null, log)
    {
        _log = log;
        _isDosOrAmiga = platform is AgiPlatform.Dos or AgiPlatform.Amiga;
        if (!_isDosOrAmiga)
        {
            MinCommand = 0xE0;
        }
    }

    /// <summary>Maximum number of instructions to draw, zero for all. Mickey's crystal animation.</summary>
    public int MaxStep { get; set; }

    /// <summary>Where the picture's origin lands on the 160x168 screen.</summary>
    public void SetOffset(int xOffset, int yOffset)
    {
        _xOffset = xOffset;
        _yOffset = yOffset;
    }

    protected override void DrawPicture()
    {
        DataOffset = 0;
        DataOffsetNibble = false;
        PatternCode = 0;
        PatternNumber = 0;
        PriorityOn = false;
        ScreenOn = false;
        PriorityColor = 4;

        if (_isDosOrAmiga)
        {
            ScreenColor = 15;
            DrawPictureDosAmiga();
        }
        else
        {
            ScreenColor = 0;
            DrawPictureA2C64CoCo();
        }
    }

    private void DrawPictureDosAmiga()
    {
        int step = 0;
        while (DataOffset < DataSize)
        {
            byte curByte = GetNextByte();
            switch (curByte)
            {
                case 0xF0:
                    DrawSetColor();
                    ScreenOn = true;
                    break;
                case 0xF1:
                    ScreenOn = false;
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
                {
                    // The screen-on flag does not prevent preAGI flood fills. Winnie picture 7
                    // (Roo) contains F1 before several fills.
                    bool prevScrOn = ScreenOn;
                    ScreenOn = true;
                    base.DrawFill();
                    ScreenOn = prevScrOn;
                    break;
                }

                case 0xF9:
                    PlotBrush();
                    break;
                case 0xFF:
                    return; // end of data
                default:
                    _log.Warning("agi.picture", $"Unknown picture opcode {curByte:X2} at {DataOffset - 1:X4}");
                    break;
            }

            // Limit drawing to the optional maximum number of opcodes. Used by Mickey for the
            // crystal animation.
            step++;
            if (step == MaxStep)
            {
                return;
            }
        }
    }

    private void DrawPictureA2C64CoCo()
    {
        while (DataOffset < DataSize)
        {
            byte curByte = GetNextByte();
            if (curByte is >= 0xF0 and <= 0xFE)
            {
                ScreenColor = (byte)(curByte & 0x0F);
                continue;
            }

            switch (curByte)
            {
                case 0xE0: // x-corner
                    XCorner();
                    break;
                case 0xE1: // y-corner
                    YCorner();
                    break;
                case 0xE2: // dynamic draw lines
                    DrawLineShort();
                    break;
                case 0xE3: // absolute draw lines
                    DrawLineAbsolute();
                    break;
                case 0xE4: // fill
                    DrawSetColor();
                    base.DrawFill();
                    break;
                case 0xE5: // enable screen drawing
                    ScreenOn = true;
                    break;
                case 0xE6: // plot brush
                    PlotBrush();
                    break;
                case 0xFF: // end of data
                    return;
                default:
                    _log.Warning("agi.picture", $"Unknown picture opcode {curByte:X2} at {DataOffset - 1:X4}");
                    break;
            }
        }
    }

    /// <summary>Plots the given brush pattern. All brushes are solid circles.</summary>
    protected override void PlotBrush()
    {
        PatternCode = GetNextByte();
        if (PatternCode > 12)
        {
            PatternCode = 12;
        }

        while (GetNextCoordinates(out byte x, out byte y))
        {
            PlotPattern(x, y);
        }
    }

    /// <summary>Draws a solid circle whose size is the pattern code.</summary>
    protected override void PlotPattern(byte x, byte y)
    {
        int patCode = PatternCode;
        int circleDataIndex = (patCode * (patCode + 1)) / 2;

        // Draw the circle by drawing its vertical lines two at a time, starting at the left
        // and right edges and working inwards. Circles have odd widths, so the final iteration
        // draws the middle line twice.
        for (int i = patCode; i >= 0; i--)
        {
            byte height = CircleHeights[circleDataIndex++];
            int y1 = y - height;
            int y2 = y + height;

            DrawLine(x - i, y1, x - i, y2);
            DrawLine(x + i, y1, x + i, y2);
        }
    }

    /// <summary>Flood fills from a start position, with a clipped height.</summary>
    protected override void DrawFill(int x, int y)
    {
        // Flood fill does extra height clipping, and pictures rely on this. The get-coordinates
        // routine clips to (139, 159) and then the flood fill routine checks if y >= 159 and
        // decrements to 158. The flood fill clip is not in the Apple II/C64/CoCo versions of
        // Winnie, as can be seen by the table edge being a different color than Winnie's shirt
        // in the first room, but the same color as the shirt in DOS/Amiga. (Picture 28)
        if (_isDosOrAmiga && y >= Height)
        {
            y = Height - 1;
        }

        base.DrawFill(x, y);
    }

    /// <summary>
    /// Gets the next x coordinate and clips it to the picture width. Many Winnie pictures contain
    /// out of bounds coordinates and rely on this clipping.
    /// </summary>
    protected override bool GetNextXCoordinate(out byte x)
    {
        if (!GetNextParamByte(out x))
        {
            return false;
        }

        if (_isDosOrAmiga && x >= Width)
        {
            x = (byte)(Width - 1);
        }

        return true;
    }

    /// <summary>Gets the next y coordinate and clips it to the picture height. Note that this is a different clip than for x.</summary>
    protected override bool GetNextYCoordinate(out byte y)
    {
        if (!GetNextParamByte(out y))
        {
            return false;
        }

        if (_isDosOrAmiga && y > Height)
        {
            y = (byte)Height;
        }

        return true;
    }

    /// <summary>
    /// Validates picture coordinates and translates them to screen coordinates by applying the
    /// offset. Winnie places tall objects at the bottom of the screen in several rooms, so the
    /// offset coordinates are checked against the screen as well.
    /// </summary>
    protected override bool GetGraphicsCoordinates(ref int x, ref int y)
    {
        if (!base.GetGraphicsCoordinates(ref x, ref y))
        {
            return false;
        }

        x += _xOffset;
        y += _yOffset;
        return x < GfxMgr.ScriptWidth && y < GfxMgr.ScriptHeight;
    }
}
