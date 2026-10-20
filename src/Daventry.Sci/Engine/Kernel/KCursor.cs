using Daventry.Core.Hosting;
using Daventry.Sci.Graphics;

namespace Daventry.Sci.Engine;

public static partial class KernelFunctions
{
    // ---- kgraphics.cpp: the cursor ----

    private static RegT SetCursorSci0(EngineState s, KernelArgs argv)
    {
        int cursorId = argv.Int16(0);

        // set the pointer position, if requested
        if (argv.Count >= 4)
        {
            s.Cursor.KernelSetPos(new SciPoint(argv.Int16(2), argv.Int16(3)));
        }

        if (argv.Count >= 2 && argv.Int16(1) == 0)
        {
            cursorId = -1;
        }

        s.Cursor.KernelSetShape(cursorId);
        return s.Acc;
    }

    private static RegT SetCursorSci11(EngineState s, KernelArgs argv)
    {
        SciPoint? hotspot = null;
        switch (argv.Count)
        {
            case 1:
                switch (argv.Int16(0))
                {
                    case 0:
                        s.Cursor.KernelHide();
                        break;
                    case -1:
                        s.Cursor.KernelClearZoomZone();
                        break;
                    case -2:
                        s.Cursor.KernelResetMoveZone();
                        break;
                    default:
                        s.Cursor.KernelShow();
                        break;
                }

                break;
            case 2:
                s.Cursor.KernelSetPos(new SciPoint(argv.Int16(0), argv.Int16(1)));
                break;
            case 4:
            {
                int top = argv.Int16(0);
                int left = argv.Int16(1);
                int bottom = argv.Int16(2) + 1; // the bottom and right edges are inside the zone
                int right = argv.Int16(3) + 1;
                if (right >= left && bottom >= top)
                {
                    s.Cursor.KernelSetMoveZone(new SciRect(left, top, right, bottom));
                }
                else
                {
                    s.Log.Warning("sci.gfx", $"kSetCursor: Ignoring invalid mouse zone ({left}, {top})-({right}, {bottom})");
                }

                break;
            }

            case 9: // KQ5 CD passes four additional "900d" parameters
            case 5:
                hotspot = new SciPoint(argv.Int16(3), argv.Int16(4));
                goto case 3;
            case 3:
                s.Cursor.KernelSetView(argv.UInt16(0), argv.UInt16(1), argv.UInt16(2), hotspot);
                break;
            case 10:
                // Freddy Pharkas reading the prescription through the whiskey glass (bug #4969)
                s.Cursor.KernelSetZoomZone(argv.UInt16(0),
                    new SciRect(argv.UInt16(1), argv.UInt16(2), argv.UInt16(3), argv.UInt16(4)),
                    argv.UInt16(5), argv.UInt16(6), argv.UInt16(7), argv.UInt16(8), (byte)argv.UInt16(9));
                break;
            default:
                throw new SciVmException($"kSetCursor: Unhandled case: {argv.Count} arguments given");
        }

        return s.Acc;
    }

    public static RegT SetCursor(EngineState s, KernelArgs argv)
        => s.Features.DetectSetCursorType() == SciVersion.V1_1 ? SetCursorSci11(s, argv) : SetCursorSci0(s, argv);

    public static RegT MoveCursor(EngineState s, KernelArgs argv)
    {
        s.Cursor.KernelSetPos(new SciPoint(argv.Int16(0), argv.Int16(1)));
        return s.Acc;
    }
}
