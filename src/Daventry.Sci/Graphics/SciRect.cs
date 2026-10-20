namespace Daventry.Sci.Graphics;

/// <summary>A point in script coordinates. Mirrors Common::Point.</summary>
public struct SciPoint(int x, int y)
{
    public int X = x;
    public int Y = y;

    public override readonly string ToString() => $"({X}, {Y})";
}

/// <summary>
/// A rectangle with exclusive right and bottom edges, mirroring ScummVM's Common::Rect so the
/// graphics code can be ported edge for edge. Mutable on purpose; the original adjusts edges in place.
/// </summary>
public struct SciRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public SciRect(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    /// <summary>A rectangle at the origin with the given size, like Common::Rect(w, h).</summary>
    public SciRect(int width, int height)
        : this(0, 0, width, height)
    {
    }

    public readonly int Width => Right - Left;

    public readonly int Height => Bottom - Top;

    public readonly bool IsEmpty => Left >= Right || Top >= Bottom;

    public readonly bool Contains(int x, int y) => Left <= x && x < Right && Top <= y && y < Bottom;

    public readonly bool Contains(SciRect r) => Left <= r.Left && r.Right <= Right && Top <= r.Top && r.Bottom <= Bottom;

    public readonly bool Intersects(SciRect r) => Left < r.Right && r.Left < Right && Top < r.Bottom && r.Top < Bottom;

    public void SetWidth(int width) => Right = Left + width;

    public void SetHeight(int height) => Bottom = Top + height;

    public void MoveTo(int x, int y)
    {
        Bottom += y - Top;
        Right += x - Left;
        Top = y;
        Left = x;
    }

    public void Translate(int dx, int dy)
    {
        Left += dx;
        Right += dx;
        Top += dy;
        Bottom += dy;
    }

    /// <summary>Shrinks this rectangle to its intersection with another, like Common::Rect::clip.</summary>
    public void Clip(SciRect r)
    {
        if (Top < r.Top)
        {
            Top = r.Top;
        }
        else if (Top > r.Bottom)
        {
            Top = r.Bottom;
        }

        if (Left < r.Left)
        {
            Left = r.Left;
        }
        else if (Left > r.Right)
        {
            Left = r.Right;
        }

        if (Bottom > r.Bottom)
        {
            Bottom = r.Bottom;
        }
        else if (Bottom < r.Top)
        {
            Bottom = r.Top;
        }

        if (Right > r.Right)
        {
            Right = r.Right;
        }
        else if (Right < r.Left)
        {
            Right = r.Left;
        }
    }

    public void Clip(int maxWidth, int maxHeight) => Clip(new SciRect(0, 0, maxWidth, maxHeight));

    public readonly SciRect FindIntersectingRect(SciRect r)
    {
        if (!Intersects(r))
        {
            return default;
        }

        return new SciRect(Math.Max(r.Left, Left), Math.Max(r.Top, Top), Math.Min(r.Right, Right), Math.Min(r.Bottom, Bottom));
    }

    public void Extend(SciRect r)
    {
        Left = Math.Min(Left, r.Left);
        Right = Math.Max(Right, r.Right);
        Top = Math.Min(Top, r.Top);
        Bottom = Math.Max(Bottom, r.Bottom);
    }

    public override readonly string ToString() => $"({Left}, {Top}, {Right}, {Bottom})";
}
