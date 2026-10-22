using Daventry.Core.Hosting;

namespace Daventry.Sci.Graphics;

public enum RemapType
{
    None = 0,
    ByRange = 1,
    ByPercent = 2,
    ToGray = 3,
    ToPercentGray = 4,
}

/// <summary>One remapping operation: one of the palette's remap colours and how it recolours what it covers.</summary>
public sealed class SingleRemap
{
    private readonly GfxRemap32 _owner;

    public SingleRemap(GfxRemap32 owner)
    {
        _owner = owner;
    }

    public RemapType Type = RemapType.None;

    /// <summary>The first and last colours shifted by a range remap.</summary>
    public byte From;
    public byte To;

    /// <summary>The shift of a range remap.</summary>
    public short Delta;

    /// <summary>The brightness change of a percent remap; above 100 oversaturates.</summary>
    public short Percent;

    /// <summary>The desaturation of a gray remap, 0 to 100.</summary>
    public byte Gray;

    /// <summary>
    /// The table the cel renderers read: target = RemapColors[target pixel]. The source pixel
    /// picks the SingleRemap, the pixel already in the buffer picks the entry.
    /// </summary>
    public readonly byte[] RemapColors = new byte[237];

    private short _lastPercent;
    private byte _lastGray;
    private readonly SciColor[] _originalColors = new SciColor[237];
    private readonly bool[] _originalColorsChanged = new bool[237];
    private readonly SciColor[] _idealColors = new SciColor[237];
    private readonly bool[] _idealColorsChanged = new bool[237];
    private readonly int[] _matchDistances = new int[237];

    public void Reset()
    {
        _lastPercent = 100;
        _lastGray = 0;
        int remapStartColor = _owner.StartColor;
        var currentPalette = _owner.Palette.CurrentPalette;
        for (int i = 0; i < remapStartColor; i++)
        {
            var color = currentPalette.Colors[i];
            RemapColors[i] = (byte)i;
            _originalColors[i] = color;
            _originalColorsChanged[i] = true;
            _idealColors[i] = color;
            _idealColorsChanged[i] = false;
            _matchDistances[i] = 0;
        }
    }

    public bool Update() => Type switch
    {
        RemapType.None => false,
        RemapType.ByRange => UpdateRange(),
        RemapType.ByPercent => UpdateBrightness(),
        RemapType.ToGray => UpdateSaturation(),
        RemapType.ToPercentGray => UpdateSaturationAndBrightness(),
        _ => throw new Engine.SciVmException($"Illegal remap type {Type}"),
    };

    private bool UpdateRange()
    {
        int remapStartColor = _owner.StartColor;
        bool updated = false;
        for (int i = 0; i < remapStartColor; i++)
        {
            byte targetColor = From <= i && i <= To ? (byte)(i + Delta) : (byte)i;
            if (RemapColors[i] != targetColor)
            {
                updated = true;
                RemapColors[i] = targetColor;
            }

            _originalColorsChanged[i] = true;
        }

        return updated;
    }

    private static byte ClampByte(int v) => (byte)Math.Min(255, v);

    private bool UpdateBrightness()
    {
        int remapStartColor = _owner.StartColor;
        var nextPalette = _owner.Palette.NextPalette;
        for (int i = 1; i < remapStartColor; i++)
        {
            var color = nextPalette.Colors[i];
            if (!_originalColors[i].Equals(color))
            {
                _originalColorsChanged[i] = true;
                _originalColors[i] = color;
            }

            if (Percent != _lastPercent || _originalColorsChanged[i])
            {
                // the result is always clipped, which Sierra only did above 100 percent
                color.R = ClampByte(color.R * Percent / 100);
                color.G = ClampByte(color.G * Percent / 100);
                color.B = ClampByte(color.B * Percent / 100);
                if (!_idealColors[i].Equals(color))
                {
                    _idealColorsChanged[i] = true;
                    _idealColors[i] = color;
                }
            }
        }

        bool updated = Apply();
        Array.Fill(_originalColorsChanged, false, 0, remapStartColor);
        Array.Fill(_idealColorsChanged, false, 0, remapStartColor);
        _lastPercent = Percent;
        return updated;
    }

    private bool UpdateSaturation()
    {
        int remapStartColor = _owner.StartColor;
        var currentPalette = _owner.Palette.CurrentPalette;
        for (int i = 1; i < remapStartColor; i++)
        {
            var color = currentPalette.Colors[i];
            if (!_originalColors[i].Equals(color))
            {
                _originalColorsChanged[i] = true;
                _originalColors[i] = color;
            }

            if (Gray != _lastGray || _originalColorsChanged[i])
            {
                int luminosity = (((color.R * 77) + (color.G * 151) + (color.B * 28)) >> 8) * Percent / 100;
                color.R = ClampByte(color.R - ((color.R - luminosity) * Gray / 100));
                color.G = ClampByte(color.G - ((color.G - luminosity) * Gray / 100));
                color.B = ClampByte(color.B - ((color.B - luminosity) * Gray / 100));
                if (!_idealColors[i].Equals(color))
                {
                    _idealColorsChanged[i] = true;
                    _idealColors[i] = color;
                }
            }
        }

        bool updated = Apply();
        Array.Fill(_originalColorsChanged, false, 0, remapStartColor);
        Array.Fill(_idealColorsChanged, false, 0, remapStartColor);
        _lastGray = Gray;
        return updated;
    }

    private bool UpdateSaturationAndBrightness()
    {
        int remapStartColor = _owner.StartColor;
        var currentPalette = _owner.Palette.CurrentPalette;
        for (int i = 1; i < remapStartColor; i++)
        {
            var color = currentPalette.Colors[i];
            if (!_originalColors[i].Equals(color))
            {
                _originalColorsChanged[i] = true;
                _originalColors[i] = color;
            }

            if (Percent != _lastPercent || Gray != _lastGray || _originalColorsChanged[i])
            {
                int luminosity = (((color.R * 77) + (color.G * 151) + (color.B * 28)) >> 8) * Percent / 100;
                color.R = ClampByte(color.R - ((color.R - luminosity) * Gray) / 100);
                color.G = ClampByte(color.G - ((color.G - luminosity) * Gray) / 100);
                color.B = ClampByte(color.B - ((color.B - luminosity) * Gray) / 100);
                if (!_idealColors[i].Equals(color))
                {
                    _idealColorsChanged[i] = true;
                    _idealColors[i] = color;
                }
            }
        }

        bool updated = Apply();
        Array.Fill(_originalColorsChanged, false, 0, remapStartColor);
        Array.Fill(_idealColorsChanged, false, 0, remapStartColor);
        _lastPercent = Percent;
        _lastGray = Gray;
        return updated;
    }

    private bool Apply()
    {
        int remapStartColor = _owner.StartColor;

        // blocked colours may not be remap targets, nor may colours that are cycling
        var blockedColors = new bool[237];
        int blockedRangeCount = _owner.BlockedRangeCount;
        if (blockedRangeCount != 0)
        {
            int blockedRangeStart = _owner.BlockedRangeStart;
            for (int i = blockedRangeStart; i < Math.Min(237, blockedRangeStart + blockedRangeCount); i++)
            {
                blockedColors[i] = true;
            }
        }

        var cycleMap = _owner.Palette.CycleMap;
        for (int i = 0; i < remapStartColor; i++)
        {
            if (cycleMap[i])
            {
                blockedColors[i] = true;
            }
        }

        bool updated = false;
        for (int i = 1; i < remapStartColor; i++)
        {
            if (!_idealColorsChanged[i] && !_originalColorsChanged[RemapColors[i]])
            {
                continue;
            }

            if (_idealColorsChanged[i] && _originalColorsChanged[RemapColors[i]] && _matchDistances[i] < 100
                && ColorDistance(_idealColors[i], _originalColors[RemapColors[i]]) <= _matchDistances[i])
            {
                continue;
            }

            int bestColor = MatchColor(_idealColors[i], _matchDistances[i], out int distance, blockedColors);
            if (bestColor != -1 && RemapColors[i] != bestColor)
            {
                updated = true;
                RemapColors[i] = (byte)bestColor;
                _matchDistances[i] = distance;
            }
        }

        return updated;
    }

    private static int ColorDistance(in SciColor a, in SciColor b)
    {
        int channelDistance = a.R - b.R;
        int distance = channelDistance * channelDistance;
        channelDistance = a.G - b.G;
        distance += channelDistance * channelDistance;
        channelDistance = a.B - b.B;
        distance += channelDistance * channelDistance;
        return distance;
    }

    /// <summary>The closest unblocked colour of the next palette, or -1. outDistance is only valid for the best index.</summary>
    private int MatchColor(in SciColor color, int minimumDistance, out int outDistance, bool[] blockedIndexes)
    {
        int bestIndex = -1;
        int bestDistance = 0xFFFFF;
        int distance = minimumDistance;
        var nextPalette = _owner.Palette.NextPalette;
        for (int i = 0; i < _owner.StartColor; i++)
        {
            if (blockedIndexes[i])
            {
                continue;
            }

            distance = nextPalette.Colors[i].R - color.R;
            distance *= distance;
            if (bestDistance <= distance)
            {
                continue;
            }

            int channelDistance = nextPalette.Colors[i].G - color.G;
            distance += channelDistance * channelDistance;
            if (bestDistance <= distance)
            {
                continue;
            }

            channelDistance = nextPalette.Colors[i].B - color.B;
            distance += channelDistance * channelDistance;
            if (bestDistance <= distance)
            {
                continue;
            }

            bestDistance = distance;
            bestIndex = i;
        }

        outDistance = distance;
        return bestIndex;
    }
}

/// <summary>Colour remapping for SCI32: the remap colours above 235 recolour what they are drawn over. Port of remap32.cpp.</summary>
public sealed partial class GfxRemap32
{
    private readonly Sci32Graphics _g;
    private readonly SingleRemap[] _remaps;
    private bool _needsUpdate;

    public GfxRemap32(Sci32Graphics g)
    {
        _g = g;

        // the Macintosh interpreters used entry 236 for black instead of 0, to keep clear of the
        // system palette's white at 0, and have one remap entry less
        StartColor = g.IsMacintosh ? (byte)237 : (byte)236;
        int count = g.Features.HasMidPaletteCode ? 9 : 19;
        _remaps = new SingleRemap[count];
        for (int i = 0; i < count; i++)
        {
            _remaps[i] = new SingleRemap(this);
        }

        EndColor = (byte)(236 + count - 1);
    }

    internal GfxPalette32 Palette => _g.Palette;

    public int RemapCount { get; private set; }

    public byte StartColor { get; }

    public byte EndColor { get; }

    public byte BlockedRangeStart { get; private set; }

    public short BlockedRangeCount { get; private set; }

    /// <summary>Turns off the remap of a colour; colour 0 turns off all of them.</summary>
    public void RemapOff(byte color)
    {
        if (color == 0)
        {
            RemapAllOff();
            return;
        }

        if (color < StartColor || color > EndColor)
        {
            _g.Log.Warning("sci.gfx", $"GfxRemap32::remapOff: {color} out of remap range");
            return;
        }

        var singleRemap = _remaps[EndColor - color];
        singleRemap.Type = RemapType.None;
        RemapCount--;
        _needsUpdate = true;
    }

    public void RemapAllOff()
    {
        foreach (var remap in _remaps)
        {
            remap.Type = RemapType.None;
        }

        RemapCount = 0;
        _needsUpdate = true;
    }

    private SingleRemap? Activate(byte color, string caller)
    {
        if (color < StartColor || color > EndColor)
        {
            _g.Log.Warning("sci.gfx", $"GfxRemap32::{caller}: {color} out of remap range");
            return null;
        }

        var singleRemap = _remaps[EndColor - color];
        if (singleRemap.Type == RemapType.None)
        {
            RemapCount++;
            singleRemap.Reset();
        }

        _needsUpdate = true;
        return singleRemap;
    }

    public void RemapByRange(byte color, short from, short to, short delta)
    {
        if (from < 0)
        {
            _g.Log.Warning("sci.gfx", $"GfxRemap32::remapByRange: attempt to remap negative color {from}");
            return;
        }

        if (to >= StartColor)
        {
            _g.Log.Warning("sci.gfx", $"GfxRemap32::remapByRange: attempt to remap into the remap zone at {to}");
            return;
        }

        if (Activate(color, "remapByRange") is not { } singleRemap)
        {
            return;
        }

        singleRemap.From = (byte)from;
        singleRemap.To = (byte)to;
        singleRemap.Delta = delta;
        singleRemap.Type = RemapType.ByRange;
    }

    public void RemapByPercent(byte color, short percent)
    {
        if (Activate(color, "remapByPercent") is not { } singleRemap)
        {
            return;
        }

        singleRemap.Percent = percent;
        singleRemap.Type = RemapType.ByPercent;
    }

    public void RemapToGray(byte color, sbyte gray)
    {
        if (gray < 0 || gray > 100)
        {
            throw new Engine.SciVmException($"RemapToGray percent out of range; gray = {gray}");
        }

        if (Activate(color, "remapToGray") is not { } singleRemap)
        {
            return;
        }

        singleRemap.Gray = (byte)gray;
        singleRemap.Type = RemapType.ToGray;
    }

    public void RemapToPercentGray(byte color, short gray, short percent)
    {
        if (Activate(color, "remapToPercentGray") is not { } singleRemap)
        {
            return;
        }

        singleRemap.Percent = percent;
        singleRemap.Gray = (byte)gray;
        singleRemap.Type = RemapType.ToPercentGray;
    }

    /// <summary>Keeps a range of palette entries from being used as remap targets.</summary>
    public void BlockRange(byte from, short count)
    {
        BlockedRangeStart = from;
        BlockedRangeCount = count;
    }

    /// <summary>Whether the colour has an active remap; otherwise it draws as a skip colour.</summary>
    public bool RemapEnabled(byte color)
    {
        int index = EndColor - color;
        // KQ7 DOS uses remap colours outside the valid range; they are skip pixels, as in Sierra's interpreter
        if (index < 0 || index >= _remaps.Length)
        {
            return false;
        }

        return _remaps[index].Type != RemapType.None;
    }

    /// <summary>The colour to draw for a remap pixel over a target pixel.</summary>
    public byte RemapColor(byte sourceColor, byte targetColor)
    {
        int index = EndColor - sourceColor;
        var singleRemap = _remaps[index];
        // targets in the remap zone itself read as black, as the uninitialised memory Sierra read did (RAMA's cursors)
        if (targetColor >= StartColor)
        {
            return 0;
        }

        return singleRemap.RemapColors[targetColor];
    }

    /// <summary>Recomputes the active remaps after a palette or remap change. Returns whether any table changed.</summary>
    public bool RemapAllTables(bool paletteUpdated)
    {
        if (!_needsUpdate && !paletteUpdated)
        {
            return false;
        }

        bool updated = false;
        foreach (var remap in _remaps)
        {
            if (remap.Type != RemapType.None)
            {
                updated |= remap.Update();
            }
        }

        _needsUpdate = false;
        return updated;
    }
}
