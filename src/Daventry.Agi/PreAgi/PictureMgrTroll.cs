using Daventry.Agi.Graphics;
using Daventry.Core.Hosting;

namespace Daventry.Agi.PreAgi;

/// <summary>
/// Decodes and draws Troll's Tale pictures. Port of ScummVM's PictureMgr_Troll.
/// <para>
/// Troll's Tale supports lines and flood fills. There is no priority screen. There are two
/// unique picture features:
/// </para>
/// <para>
/// 1. The F3 opcode can dynamically act as a no-op or terminator (FF). This allows pictures to
/// have an optional set of instructions for drawing or hiding a room's object or the king's
/// crown.
/// </para>
/// <para>
/// 2. A custom flood fill technique is used for drawing the Troll over room pictures. Normally,
/// flood fill requires an empty (white) area.
/// </para>
/// <para>
/// One quirk is that the xCorner and yCorner instructions contain a redundant coordinate, even
/// though it is ignored because it is derived from the others. Each room picture depends on the
/// game first drawing a frame within the entire picture area. This is not decorative; the flood
/// fill routines rely on this border because they do not do boundary tests, and pictures are
/// drawn for it.
/// </para>
/// </summary>
public sealed class PictureMgrTroll : PictureMgr
{
    private readonly ILog _log;

    public PictureMgrTroll(GfxMgr gfx, ILog log)
        : base(new AgiGameState(), gfx, null, log)
    {
        _log = log;
    }

    /// <summary>Whether F3 ends the picture; otherwise it is a no-op.</summary>
    public bool StopOnF3 { get; set; }

    /// <summary>Whether the fills follow the Troll's rules (see <see cref="DrawFillCheck"/>).</summary>
    public bool TrollMode { get; set; }

    protected override void DrawPicture()
    {
        DataOffset = 0;
        DataOffsetNibble = false;
        PatternCode = 0;
        PatternNumber = 0;
        PriorityOn = false;
        ScreenOn = false;
        PriorityColor = 4;
        ScreenColor = 15;

        while (DataOffset < DataSize)
        {
            byte curByte = GetNextByte();
            switch (curByte)
            {
                case 0xF0: // F0 is in all Troll's Tale pictures, but it is a no-op.
                    break; // Confirmed in disassembly of opcode table.
                case 0xF1:
                    DrawSetColor();
                    ScreenOn = true;
                    break;
                case 0xF3: // F3 would impersonate opcode F0 (no-op) or FF (terminator)
                    if (StopOnF3)
                    {
                        return;
                    }

                    break;
                case 0xF8:
                    YCorner(true); // skip extra (redundant) coordinates when parsing
                    break;
                case 0xF9:
                    XCorner(true); // skip extra (redundant) coordinates when parsing
                    break;
                case 0xFA: // FA and FB are both used, but they are the same.
                case 0xFB: // Confirmed in disassembly of opcode table.
                    DrawLineAbsolute();
                    break;
                case 0xFE:
                    DrawFill();
                    break;
                case 0xFF: // end of data
                    return;
                default:
                    _log.Warning("agi.picture", $"Unknown picture opcode {curByte:X2} at {DataOffset - 1:X4}");
                    break;
            }
        }
    }

    /// <summary>
    /// Flood fills from a series of start positions.
    /// <para>
    /// Troll's Tale contains two separate flood fill implementations to handle the special case of
    /// drawing the Troll. The game sets a global before drawing to activate Troll mode. We
    /// implement this by overriding this method and the check method.
    /// </para>
    /// </summary>
    protected override void DrawFill()
    {
        DrawSetColor();
        ScreenOn = true;

        if (ScreenColor == 15)
        {
            // White flood fills are only allowed when drawing the Troll, otherwise they are
            // completely ignored. Several room pictures contain white flood fills.
            while (GetNextCoordinates(out byte x, out byte y))
            {
                if (TrollMode)
                {
                    DrawFill(x, y);
                }
            }
        }
        else
        {
            // When not drawing the Troll, do a regular flood fill. When drawing the Troll, first
            // fill with white, and then fill normally.
            byte fillColor = ScreenColor;
            while (GetNextCoordinates(out byte x, out byte y))
            {
                if (TrollMode)
                {
                    ScreenColor = 15; // white
                    DrawFill(x, y);
                    ScreenColor = fillColor;
                }

                DrawFill(x, y);
            }
        }
    }

    /// <summary>
    /// Checks if flood fill is allowed at a position.
    /// <para>
    /// The Troll is a large picture with flood fills that is drawn over many busy room pictures,
    /// and always in the same location. This is a problem because the picture format is only meant
    /// to fill white areas. Sierra handled this by reserving a colour for the Troll's lines (11,
    /// light blue) and implementing a second set of routines that fill white and treat the Troll's
    /// colour as a boundary, and sometimes white as well. When drawing the Troll, a non-white fill
    /// is preceded by a special white fill to clear the area. This does not work well if there are
    /// existing white pixels, and rooms do have these. The Troll has incomplete fills in these
    /// rooms, but this is original behaviour. In some rooms, such as those with white checkered
    /// floors, the results are so bad that Sierra added them to the list of rooms the Troll never
    /// visits.
    /// </para>
    /// <para>
    /// We implement Troll mode without a second flood fill algorithm; instead we override the
    /// check method, and the AGI algorithm in the base class provides the context so we know which
    /// of the two colour checks to use.
    /// </para>
    /// </summary>
    protected override bool DrawFillCheck(int x, int y, bool horizontalCheck)
    {
        if (TrollMode && ScreenColor == 15)
        {
            if (!GetGraphicsCoordinates(ref x, ref y))
            {
                return false;
            }

            byte screenColor = Gfx.GetColor(x, y);

            // when filling white during troll mode...
            if (horizontalCheck)
            {
                // horizontal checks only stop on troll line colour
                return screenColor != 11;
            }

            // all other checks stop on troll line colour or white
            return screenColor != 11 && screenColor != 15;
        }

        return base.DrawFillCheck(x, y, horizontalCheck);
    }
}
