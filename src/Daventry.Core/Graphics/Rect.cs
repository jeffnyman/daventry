namespace Daventry.Core.Graphics;

/// <summary>An integer rectangle. Right and Bottom are exclusive.</summary>
public readonly record struct Rect(int X, int Y, int Width, int Height)
{
    public int Left => X;
    public int Top => Y;
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static Rect FromBounds(int left, int top, int right, int bottom)
        => new(left, top, right - left, bottom - top);

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    public Rect Intersect(Rect other)
    {
        int l = Math.Max(X, other.X);
        int t = Math.Max(Y, other.Y);
        int r = Math.Min(Right, other.Right);
        int b = Math.Min(Bottom, other.Bottom);
        return r <= l || b <= t ? default : FromBounds(l, t, r, b);
    }

    public Rect Offset(int dx, int dy) => new(X + dx, Y + dy, Width, Height);
}
