namespace Daventry.Sci.Graphics;

/// <summary>One palette entry. Mirrors ScummVM's Color: <see cref="Used"/> doubles as a flag byte.</summary>
public struct SciColor
{
    public byte Used;
    public byte R;
    public byte G;
    public byte B;

    public readonly bool SameRgb(in SciColor other) => R == other.R && G == other.G && B == other.B;
}

/// <summary>A 256 colour palette with the per-colour mapping, timestamp and intensity the SCI16 palette code uses.</summary>
public sealed class SciPalette
{
    public byte[] Mapping { get; } = new byte[256];

    public uint Timestamp { get; set; }

    public SciColor[] Colors { get; } = new SciColor[256];

    public byte[] Intensity { get; } = new byte[256];

    public SciPalette()
    {
        for (int i = 0; i < 256; i++)
        {
            Mapping[i] = (byte)i;
            Intensity[i] = 100;
        }
    }

    public void CopyFrom(SciPalette other)
    {
        Array.Copy(other.Mapping, Mapping, 256);
        Array.Copy(other.Colors, Colors, 256);
        Array.Copy(other.Intensity, Intensity, 256);
        Timestamp = other.Timestamp;
    }

    public SciPalette Clone()
    {
        var copy = new SciPalette();
        copy.CopyFrom(this);
        return copy;
    }

    public void Clear()
    {
        Array.Clear(Colors);
        Array.Clear(Intensity);
        Timestamp = 0;
        for (int i = 0; i < 256; i++)
        {
            Mapping[i] = (byte)i;
        }
    }
}
