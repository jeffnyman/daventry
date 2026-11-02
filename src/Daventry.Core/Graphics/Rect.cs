namespace Daventry.Core.Graphics;

/// <summary>An integer rectangle. Right and Bottom are exclusive.</summary>
public readonly record struct Rect(int X, int Y, int Width, int Height)
{
    /// <summary>The left edge, <see cref="X"/>.</summary>
    public int Left => X;
    /// <summary>The top edge, <see cref="Y"/>.</summary>
    public int Top => Y;
    /// <summary>The right edge, exclusive.</summary>
    public int Right => X + Width;
    /// <summary>The bottom edge, exclusive.</summary>
    public int Bottom => Y + Height;
    /// <summary>Whether the rectangle has no area.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>The rectangle between two edges, right and bottom exclusive.</summary>
    public static Rect FromBounds(int left, int top, int right, int bottom)
        => new(left, top, right - left, bottom - top);

    /// <summary>Whether the point is inside.</summary>
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    /// <summary>The part both rectangles cover, or an empty rectangle.</summary>
    public Rect Intersect(Rect other)
    {
        int l = Math.Max(X, other.X);
        int t = Math.Max(Y, other.Y);
        int r = Math.Min(Right, other.Right);
        int b = Math.Min(Bottom, other.Bottom);
        return r <= l || b <= t ? default : FromBounds(l, t, r, b);
    }

    /// <summary>The same rectangle moved by the offsets.</summary>
    public Rect Offset(int dx, int dy) => new(X + dx, Y + dy, Width, Height);
}
