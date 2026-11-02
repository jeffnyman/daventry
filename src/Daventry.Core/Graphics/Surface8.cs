namespace Daventry.Core.Graphics;

/// <summary>An 8-bit indexed-colour surface with one byte per pixel and no row padding.</summary>
public sealed class Surface8
{
    /// <summary>A surface of the given size, every pixel 0.</summary>
    public Surface8(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        Pixels = new byte[width * height];
    }

    /// <summary>The width in pixels.</summary>
    public int Width { get; }
    /// <summary>The height in pixels.</summary>
    public int Height { get; }
    /// <summary>The distance between rows in <see cref="Pixels"/>, which is the width.</summary>
    public int Pitch => Width;
    /// <summary>The pixels, row by row, one palette index each.</summary>
    public byte[] Pixels { get; }
    /// <summary>The whole surface as a rectangle.</summary>
    public Rect Bounds => new(0, 0, Width, Height);

    /// <summary>The palette index at a point.</summary>
    public byte this[int x, int y]
    {
        get => Pixels[y * Width + x];
        set => Pixels[y * Width + x] = value;
    }

    /// <summary>One row of pixels.</summary>
    public Span<byte> Row(int y) => Pixels.AsSpan(y * Width, Width);

    /// <summary>Sets every pixel to a colour.</summary>
    public void Fill(byte color) => Pixels.AsSpan().Fill(color);

    /// <summary>Sets the pixels of a rectangle, clipped to the surface, to a colour.</summary>
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

    /// <summary>Copies another surface of the same size.</summary>
    public void CopyFrom(Surface8 other)
    {
        if (other.Width != Width || other.Height != Height)
        {
            throw new ArgumentException("Surface sizes differ.", nameof(other));
        }

        other.Pixels.CopyTo(Pixels, 0);
    }

    /// <summary>
    /// Copies <paramref name="source"/> shifted right by <paramref name="dx"/> and down by
    /// <paramref name="dy"/> (negative values shift left and up), filling the uncovered edge
    /// with <paramref name="fill"/>. This is what a shaking screen shows.
    /// </summary>
    public void CopyFromShifted(Surface8 source, int dx, int dy, byte fill = 0)
    {
        if (source.Width != Width || source.Height != Height)
        {
            throw new ArgumentException("Surface sizes differ.", nameof(source));
        }

        Fill(fill);
        Blit(source, source.Bounds, dx, dy);
    }
}
