using System.Runtime.InteropServices;
using Daventry.Core.Graphics;

namespace Daventry.Host.Browser;

/// <summary>Expands an indexed frame through its palette into the RGBA bytes a canvas takes.</summary>
public sealed class FrameConverter
{
    private readonly uint[] _lookup = new uint[256];

    /// <summary>Rebuilds the colour lookup; call after the palette changed.</summary>
    public void SetPalette(ReadOnlySpan<Rgb> colors, int start = 0)
    {
        for (int i = 0; i < colors.Length; i++)
        {
            var c = colors[i];
            // little-endian RGBA, the byte order ImageData expects
            _lookup[start + i] = c.R | (uint)c.G << 8 | (uint)c.B << 16 | 0xFF000000;
        }
    }

    /// <summary>Writes the frame as RGBA; the destination holds four bytes per pixel.</summary>
    public void Convert(ReadOnlySpan<byte> pixels, Span<byte> rgba)
    {
        var dst = MemoryMarshal.Cast<byte, uint>(rgba);
        for (int i = 0; i < pixels.Length; i++)
        {
            dst[i] = _lookup[pixels[i]];
        }
    }
}
