namespace Daventry.Core.Graphics;

/// <summary>An 8-bit indexed-colour surface with one byte per pixel and no row padding.</summary>
public sealed class Surface8
{
    public Surface8(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        Pixels = new byte[width * height];
    }

    public int Width { get; }
    public int Height { get; }
    public int Pitch => Width;
    public byte[] Pixels { get; }
    public Rect Bounds => new(0, 0, Width, Height);

    public byte this[int x, int y]
    {
        get => Pixels[y * Width + x];
        set => Pixels[y * Width + x] = value;
    }

    public Span<byte> Row(int y) => Pixels.AsSpan(y * Width, Width);

    public void Fill(byte color) => Pixels.AsSpan().Fill(color);

    public void FillRect(Rect rect, byte color)
    {
        var r = rect.Intersect(Bounds);
        if (r.IsEmpty)
        {
            return;
        }

        for (int y = r.Y; y < r.Bottom; y++)
        {
            Pixels.AsSpan(y * Width + r.X, r.Width).Fill(color);
        }
    }

    /// <summary>Copies a clipped rectangle from <paramref name="source"/> to this surface.</summary>
    public void Blit(Surface8 source, Rect sourceRect, int destX, int destY)
    {
        var sr = sourceRect.Intersect(source.Bounds);
        if (sr.IsEmpty)
        {
            return;
        }

        var dr = new Rect(destX, destY, sr.Width, sr.Height).Intersect(Bounds);
        if (dr.IsEmpty)
        {
            return;
        }

        int sx = sr.X + (dr.X - destX);
        int sy = sr.Y + (dr.Y - destY);
        for (int y = 0; y < dr.Height; y++)
        {
            source.Pixels.AsSpan((sy + y) * source.Width + sx, dr.Width)
                .CopyTo(Pixels.AsSpan((dr.Y + y) * Width + dr.X, dr.Width));
        }
    }

    public void CopyFrom(Surface8 other)
    {
        if (other.Width != Width || other.Height != Height)
        {
            throw new ArgumentException("Surface sizes differ.", nameof(other));
        }

        other.Pixels.CopyTo(Pixels, 0);
    }
}
