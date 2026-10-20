namespace Daventry.Core.Graphics;

/// <summary>
/// A 256-entry colour palette. <see cref="Version"/> increments on every change so a host can
/// cheaply detect when it needs to re-upload colours.
/// </summary>
public sealed class Palette
{
    public const int Size = 256;

    private readonly Rgb[] _entries = new Rgb[Size];

    public int Version { get; private set; }

    public ReadOnlySpan<Rgb> Entries => _entries;

    public Rgb this[int index]
    {
        get => _entries[index];
        set
        {
            _entries[index] = value;
            Version++;
        }
    }

    public void Set(int start, ReadOnlySpan<Rgb> colors)
    {
        colors.CopyTo(_entries.AsSpan(start, colors.Length));
        Version++;
    }

    public void CopyFrom(Palette other)
    {
        other._entries.CopyTo(_entries, 0);
        Version++;
    }
}
