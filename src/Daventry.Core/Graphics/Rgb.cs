namespace Daventry.Core.Graphics;

/// <summary>An 8-bit-per-channel colour.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = new(0, 0, 0);
    public static readonly Rgb White = new(255, 255, 255);
}
