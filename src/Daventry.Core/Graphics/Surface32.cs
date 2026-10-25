namespace Daventry.Core.Graphics;

/// <summary>
/// A true-colour surface: one 32-bit pixel per element, 0x00RRGGBB (XRGB8888), no row padding.
/// Used for video that does not fit a 256-colour palette, such as Phantasmagoria 2's Duck videos.
/// </summary>
public sealed class Surface32
{
    public Surface32(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        Pixels = new uint[width * height];
    }

    public int Width { get; }
    public int Height { get; }
    public uint[] Pixels { get; }
    public Rect Bounds => new(0, 0, Width, Height);

    public uint this[int x, int y]
    {
        get => Pixels[y * Width + x];
        set => Pixels[y * Width + x] = value;
    }

    public Span<uint> Row(int y) => Pixels.AsSpan(y * Width, Width);

    public static uint Pack(byte r, byte g, byte b) => ((uint)r << 16) | ((uint)g << 8) | b;

    public static uint Pack(Rgb color) => Pack(color.R, color.G, color.B);

    public static Rgb Unpack(uint pixel) => new((byte)(pixel >> 16), (byte)(pixel >> 8), (byte)pixel);

    /// <summary>Fills this surface with an indexed surface of the same size, looked up in <paramref name="palette"/>.</summary>
    public void FromIndexed(Surface8 source, ReadOnlySpan<Rgb> palette)
    {
        if (source.Width != Width || source.Height != Height)
        {
            throw new ArgumentException($"Source is {source.Width}x{source.Height} but this surface is {Width}x{Height}.", nameof(source));
        }

        Span<uint> lookup = stackalloc uint[256];
        for (int i = 0; i < 256; i++)
        {
            lookup[i] = i < palette.Length ? Pack(palette[i]) : 0;
        }

        var pixels = source.Pixels;
        for (int i = 0; i < pixels.Length; i++)
        {
            Pixels[i] = lookup[pixels[i]];
        }
    }

    public void CopyFrom(Surface32 source)
    {
        if (source.Width != Width || source.Height != Height)
        {
            throw new ArgumentException($"Source is {source.Width}x{source.Height} but this surface is {Width}x{Height}.", nameof(source));
        }

        source.Pixels.CopyTo(Pixels, 0);
    }
}
