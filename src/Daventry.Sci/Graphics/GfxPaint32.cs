using Daventry.Sci.Engine;

namespace Daventry.Sci.Graphics;

public enum LineStyle
{
    Solid = 0,
    Dashed = 1,
    Pattern = 2,
}

/// <summary>
/// Draws the lines of kAddLine as screen items: each line gets a bitmap of its bounding box in
/// a cel of its own. Port of ScummVM's GfxPaint32.
/// </summary>
public sealed class GfxPaint32
{
    private readonly Sci32Graphics _g;

    public GfxPaint32(Sci32Graphics g)
    {
        _g = g;
    }

    private sealed class LineProperties
    {
        public required SciBitmap Bitmap;
        public readonly bool[] Pattern = new bool[16];
        public int PatternIndex;
        public bool Solid;
        public bool Horizontal;
        public int LastAddress;
    }

    public RegT KernelAddLine(RegT planeObject, SciPoint startPoint, SciPoint endPoint, int priority, byte color, LineStyle style, ushort pattern, byte thickness)
    {
        var plane = _g.Frameout.Planes.FindByObject(planeObject)
            ?? throw new SciVmException($"kAddLine: Plane {planeObject} not found");

        var bitmapId = MakeLineBitmap(startPoint, endPoint, priority, color, style, pattern, thickness, out var gameRect);

        // the line colour rides on the cel info, as Sierra did, so kUpdateLine can keep it
        var celInfo = new CelInfo32 { Type = CelType.Mem, Bitmap = bitmapId, Color = color };
        var screenItem = new ScreenItem(_g, planeObject, celInfo, gameRect)
        {
            Priority = (short)priority,
            FixedPriority = true,
        };
        plane.ScreenItemList.Add(screenItem);
        return screenItem.Object;
    }

    public void KernelUpdateLine(ScreenItem screenItem, Plane32 plane, SciPoint startPoint, SciPoint endPoint, int priority, byte color, LineStyle style, ushort pattern, byte thickness)
    {
        var bitmapId = MakeLineBitmap(startPoint, endPoint, priority, color, style, pattern, thickness, out _);
        _g.SegMan.FreeBitmap(screenItem.CelInfo.Bitmap);
        screenItem.CelInfo.Bitmap = bitmapId;
        screenItem.CelInfo.Color = color;
        screenItem.Position = startPoint;
        screenItem.Priority = (short)priority;
        screenItem.Update();
    }

    public void KernelDeleteLine(RegT screenItemObject, RegT planeObject)
    {
        var plane = _g.Frameout.Planes.FindByObject(planeObject);
        if (plane is null)
        {
            return;
        }

        var screenItem = plane.ScreenItemList.FindByObject(screenItemObject);
        if (screenItem is null)
        {
            return;
        }

        _g.SegMan.FreeBitmap(screenItem.CelInfo.Bitmap);
        _g.Frameout.DeleteScreenItem(screenItem, plane);
    }

    private static void DrawPoint(int x, int y, byte color, LineProperties properties)
    {
        int bitmapWidth = properties.Bitmap.Width;
        int bitmapHeight = properties.Bitmap.Height;

        // scripts draw lines that end outside the visible area; only the points inside count
        if (x < 0 || x >= bitmapWidth || y < 0 || y >= bitmapHeight)
        {
            return;
        }

        var pixels = properties.Bitmap.Pixels;
        int index = bitmapWidth * y + x;
        if (properties.Solid)
        {
            pixels[index] = color;
            return;
        }

        if (properties.Horizontal && x != properties.LastAddress)
        {
            properties.LastAddress = x;
            properties.PatternIndex++;
        }
        else if (!properties.Horizontal && y != properties.LastAddress)
        {
            properties.LastAddress = y;
            properties.PatternIndex++;
        }

        if (properties.PatternIndex < properties.Pattern.Length && properties.Pattern[properties.PatternIndex])
        {
            pixels[index] = color;
        }

        if (properties.PatternIndex == properties.Pattern.Length)
        {
            properties.PatternIndex = 0;
        }
    }

    private RegT MakeLineBitmap(SciPoint startPoint, SciPoint endPoint, int priority, byte color, LineStyle style, ushort pattern, byte thickness, out SciRect outRect)
    {
        byte skipColor = color != SciBitmap.DefaultSkipColor ? (byte)SciBitmap.DefaultSkipColor : (byte)0;

        // the thickness is expected to be 2n + 1
        thickness = (byte)((Math.Max((byte)1, thickness) - 1) | 1);
        int halfThickness = thickness >> 1;

        int scriptWidth = _g.Frameout.ScriptWidth;
        int scriptHeight = _g.Frameout.ScriptHeight;

        outRect = new SciRect(
            Math.Min(startPoint.X, endPoint.X),
            Math.Min(startPoint.Y, endPoint.Y),
            Math.Max(startPoint.X, endPoint.X) + 1,
            Math.Max(startPoint.Y, endPoint.Y) + 1);
        outRect.Grow(halfThickness);
        outRect.Clip(new SciRect(scriptWidth, scriptHeight));

        var bitmap = _g.SegMan.AllocateBitmap(out var bitmapId, Math.Max(outRect.Width, 1), Math.Max(outRect.Height, 1), skipColor, 0, 0, scriptWidth, scriptHeight, 0, false, true);
        bitmap.Pixels.Fill(skipColor);

        var properties = new LineProperties { Bitmap = bitmap, Solid = true };
        switch (style)
        {
            case LineStyle.Solid:
                pattern = 0xFFFF;
                break;
            case LineStyle.Dashed:
                pattern = 0xFF00;
                properties.Solid = false;
                break;
            case LineStyle.Pattern:
                properties.Solid = pattern == 0xFFFF;
                break;
        }

        // coordinates relative to the bitmap
        int x1 = startPoint.X - outRect.Left;
        int y1 = startPoint.Y - outRect.Top;
        int x2 = endPoint.X - outRect.Left;
        int y2 = endPoint.Y - outRect.Top;

        if (!properties.Solid)
        {
            for (int i = 0; i < properties.Pattern.Length; i++)
            {
                properties.Pattern[i] = (pattern & 0x8000) != 0;
                pattern <<= 1;
            }

            properties.PatternIndex = 0;
            properties.Horizontal = Math.Abs(x2 - x1) > Math.Abs(y2 - y1);
            properties.LastAddress = properties.Horizontal ? x1 : y1;
        }

        if (thickness <= 1)
        {
            DrawLine(x1, y1, x2, y2, color, properties);
        }
        else
        {
            DrawThickLine(x1, y1, x2, y2, thickness, color, properties);
        }

        return bitmapId;
    }

    /// <summary>Bresenham's line.</summary>
    private static void DrawLine(int x0, int y0, int x1, int y1, byte color, LineProperties data)
    {
        bool steep = Math.Abs(y1 - y0) > Math.Abs(x1 - x0);
        if (steep)
        {
            (x0, y0) = (y0, x0);
            (x1, y1) = (y1, x1);
        }

        int deltaX = Math.Abs(x1 - x0);
        int deltaY = Math.Abs(y1 - y0);
        int deltaErr = deltaY;
        int x = x0;
        int y = y0;
        int err = 0;
        int xStep = x0 < x1 ? 1 : -1;
        int yStep = y0 < y1 ? 1 : -1;

        if (steep)
        {
            DrawPoint(y, x, color, data);
        }
        else
        {
            DrawPoint(x, y, color, data);
        }

        while (x != x1)
        {
            x += xStep;
            err += deltaErr;
            if (2 * err > deltaX)
            {
                y += yStep;
                err -= deltaX;
            }

            if (steep)
            {
                DrawPoint(y, x, color, data);
            }
            else
            {
                DrawPoint(x, y, color, data);
            }
        }
    }

    private static void DrawFilledRect(int left, int top, int right, int bottom, byte color, LineProperties data)
    {
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                DrawPoint(x, y, color, data);
            }
        }
    }

    /// <summary>A line of the given thickness, drawn as strokes across the line's minor axis (ScummVM's drawThickLine2).</summary>
    private static void DrawThickLine(int x1, int y1, int x2, int y2, int thick, byte color, LineProperties data)
    {
        int dx = Math.Abs(x2 - x1);
        int dy = Math.Abs(y2 - y1);

        if (dx == 0)
        {
            int xn = x1 - thick / 2;
            DrawFilledRect(xn, Math.Min(y1, y2), xn + thick - 1, Math.Max(y1, y2), color, data);
            return;
        }

        if (dy == 0)
        {
            int yn = y1 - thick / 2;
            DrawFilledRect(Math.Min(x1, x2), yn, Math.Max(x1, x2), yn + thick - 1, color, data);
            return;
        }

        int wid;
        int d;
        int incr1;
        int incr2;
        int x;
        int y;
        int wstart;
        if (dy <= dx)
        {
            // more or less horizontal: the stroke is vertical
            wid = thick != 0 ? (int)(thick * (1.0 / dx * Math.Sqrt((double)dx * dx + (double)dy * dy))) : 1;
            d = 2 * dy - dx;
            incr1 = 2 * dy;
            incr2 = 2 * (dy - dx);
            int xend;
            int ydirflag;
            if (x1 > x2)
            {
                x = x2;
                y = y2;
                ydirflag = -1;
                xend = x1;
            }
            else
            {
                x = x1;
                y = y1;
                ydirflag = 1;
                xend = x2;
            }

            wstart = y - wid / 2;
            for (int w = wstart; w < wstart + wid; w++)
            {
                DrawPoint(x, w, color, data);
            }

            bool down = (y2 - y1) * ydirflag > 0;
            while (x < xend)
            {
                x++;
                if (d < 0)
                {
                    d += incr1;
                }
                else
                {
                    y += down ? 1 : -1;
                    d += incr2;
                }

                wstart = y - wid / 2;
                for (int w = wstart; w < wstart + wid; w++)
                {
                    DrawPoint(x, w, color, data);
                }
            }
        }
        else
        {
            // more or less vertical: the stroke is horizontal
            wid = thick != 0 ? (int)(thick * (1.0 / dy * Math.Sqrt((double)dx * dx + (double)dy * dy))) : 1;
            d = 2 * dx - dy;
            incr1 = 2 * dx;
            incr2 = 2 * (dx - dy);
            int yend;
            int xdirflag;
            if (y1 > y2)
            {
                y = y2;
                x = x2;
                yend = y1;
                xdirflag = -1;
            }
            else
            {
                y = y1;
                x = x1;
                yend = y2;
                xdirflag = 1;
            }

            wstart = x - wid / 2;
            for (int w = wstart; w < wstart + wid; w++)
            {
                DrawPoint(w, y, color, data);
            }

            bool right = (x2 - x1) * xdirflag > 0;
            while (y < yend)
            {
                y++;
                if (d < 0)
                {
                    d += incr1;
                }
                else
                {
                    x += right ? 1 : -1;
                    d += incr2;
                }

                wstart = x - wid / 2;
                for (int w = wstart; w < wstart + wid; w++)
                {
                    DrawPoint(w, y, color, data);
                }
            }
        }
    }
}
