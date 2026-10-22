using System.Buffers.Binary;
using Daventry.Core.Graphics;
using Daventry.Core.Hosting;
using Daventry.Sci.Engine;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Graphics;

/// <summary>
/// A palette resource as stored on disk: a header, then one palette entry (SCI32 games never
/// store more) with a start colour, a colour count and per-colour or shared "used" flags.
/// Port of ScummVM's HunkPalette.
/// </summary>
public sealed class HunkPalette
{
    private const int NumPaletteEntriesOffset = 10;
    private const int HunkPaletteHeaderSize = 13;
    private const int EntryHeaderSize = 22;
    private const int EntryStartColorOffset = 10;
    private const int EntryNumColorsOffset = 14;
    private const int EntryUsedOffset = 16;
    private const int EntrySharedUsedOffset = 17;
    private const int EntryVersionOffset = 18;

    private readonly byte[] _data;
    private readonly int _offset;
    private readonly byte _numPalettes;

    public HunkPalette(byte[] data, int offset = 0)
    {
        // the header size field is garbage (0 in KQ7 2.00b's and Phantasmagoria's 999.pal, 14 in
        // most others) while the real header is 13 bytes long
        _data = data;
        _offset = offset;
        _numPalettes = data[offset + NumPaletteEntriesOffset];
        if (_numPalettes is not (0 or 1))
        {
            throw new SciVmException($"HunkPalette with {_numPalettes} palettes");
        }

        if (_numPalettes != 0)
        {
            Version = GetEntryHeader().Version;
        }
    }

    /// <summary>The size of a hunk palette holding one palette of <paramref name="numIndexes"/> colours.</summary>
    public static int CalculateHunkPaletteSize(int numIndexes = 256, bool sharedUsed = true)
    {
        const int numPalettes = 1;
        return HunkPaletteHeaderSize + 2 + 2 * numPalettes + (EntryHeaderSize + numIndexes * (3 + (sharedUsed ? 0 : 1))) * numPalettes;
    }

    /// <summary>Writes a palette as a hunk palette, the format kBitmap stores embedded palettes in.</summary>
    public static void Write(Span<byte> output, SciPalette palette)
    {
        const byte numPalettes = 1;
        const int paletteOffset = HunkPaletteHeaderSize + 2 * numPalettes;
        output[NumPaletteEntriesOffset] = numPalettes;
        output[HunkPaletteHeaderSize + 2] = paletteOffset;

        var entry = output[paletteOffset..];
        entry[EntryStartColorOffset] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(entry[EntryNumColorsOffset..], 256);
        entry[EntryUsedOffset] = 1;
        entry[EntrySharedUsedOffset] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(entry[EntryVersionOffset..], 1);

        var paletteData = entry[EntryHeaderSize..];
        for (int i = 0; i < 256; i++)
        {
            paletteData[i * 4] = palette.Colors[i].Used;
            paletteData[i * 4 + 1] = palette.Colors[i].R;
            paletteData[i * 4 + 2] = palette.Colors[i].G;
            paletteData[i * 4 + 3] = palette.Colors[i].B;
        }
    }

    /// <summary>The version from the last time this palette was submitted; it keeps a cel's palette from being resubmitted every frame.</summary>
    public uint Version { get; private set; }

    private int PalPointer => _offset + HunkPaletteHeaderSize + 2 * _numPalettes;

    private (byte StartColor, ushort NumColors, bool Used, bool SharedUsed, uint Version) GetEntryHeader()
    {
        int data = PalPointer;
        return (_data[data + EntryStartColorOffset],
            BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(data + EntryNumColorsOffset)),
            _data[data + EntryUsedOffset] != 0,
            _data[data + EntrySharedUsedOffset] != 0,
            BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(data + EntryVersionOffset)));
    }

    /// <summary>Records the version in the raw data, as Sierra's interpreter did.</summary>
    public void SetVersion(uint version)
    {
        if (_numPalettes != _data[_offset + NumPaletteEntriesOffset])
        {
            throw new SciVmException("Invalid HunkPalette");
        }

        if (_numPalettes != 0)
        {
            if (GetEntryHeader().Version != Version)
            {
                throw new SciVmException("Invalid HunkPalette");
            }

            BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(PalPointer + EntryVersionOffset), version);
            Version = version;
        }
    }

    public SciPalette ToPalette()
    {
        var outPalette = new SciPalette();
        Array.Clear(outPalette.Mapping);
        Array.Clear(outPalette.Intensity);
        if (_numPalettes == 0)
        {
            return outPalette;
        }

        var header = GetEntryHeader();
        int data = PalPointer + EntryHeaderSize;
        int end = header.StartColor + header.NumColors;
        if (end > 256)
        {
            throw new SciVmException("HunkPalette: more than 256 colours");
        }

        if (header.SharedUsed)
        {
            for (int i = header.StartColor; i < end; i++)
            {
                outPalette.Colors[i] = new SciColor { Used = header.Used ? (byte)1 : (byte)0, R = _data[data], G = _data[data + 1], B = _data[data + 2] };
                data += 3;
            }
        }
        else
        {
            for (int i = header.StartColor; i < end; i++)
            {
                outPalette.Colors[i] = new SciColor { Used = _data[data], R = _data[data + 1], G = _data[data + 2], B = _data[data + 3] };
                data += 4;
            }
        }

        return outPalette;
    }
}

public enum PalCyclerDirection
{
    Backward = 0,
    Forward = 1,
}

/// <summary>A range of palette entries rotated on a timer.</summary>
public sealed class PalCycler
{
    public byte FromColor;
    public ushort NumColorsToCycle;
    public byte CurrentCycle;
    public PalCyclerDirection Direction;
    public uint LastUpdateTick;

    /// <summary>Ticks per step; 0 means the cycler only moves when the scripts call doCycle.</summary>
    public short Delay;
    public ushort NumTimesPaused;
}

/// <summary>
/// The SCI32 palette: the source palette the scripts and cels submit, the next palette with
/// varies, cycles and fades applied, and the palette on the hardware. Port of palette32.cpp.
/// </summary>
public sealed partial class GfxPalette32
{
    public const int NumGammaTables = 6;
    private const int NumCyclers = 10;

    private readonly Sci32Graphics _g;
    private uint _version = 1;
    private bool _needsUpdate;
    private readonly Rgb[] _hardwarePalette = new Rgb[256];
    private readonly SciPalette _currentPalette = new();
    private readonly SciPalette _sourcePalette = new();
    private readonly SciPalette _nextPalette = new();

    // varying
    private SciPalette? _varyStartPalette;
    private SciPalette? _varyTargetPalette;
    private byte _varyFromColor;
    private byte _varyToColor = 255;
    private uint _varyLastTick;
    private int _varyTime;
    private short _varyDirection;
    private short _varyPercent;
    private short _varyTargetPercent;
    private ushort _varyNumTimesPaused;

    // cycling
    private readonly PalCycler?[] _cyclers = new PalCycler?[NumCyclers];
    private readonly bool[] _cycleMap = new bool[256];

    // fading
    private readonly ushort[] _fadeTable = new ushort[256];

    // gamma
    private int _gammaLevel;
    private bool _gammaChanged;

    public GfxPalette32(Sci32Graphics g)
    {
        _g = g;
        _gammaLevel = g.Features.UseMacGammaLevel ? 2 : -1;
        Array.Fill(_fadeTable, (ushort)100);
        Array.Clear(_currentPalette.Mapping);
        Array.Clear(_sourcePalette.Mapping);
        Array.Clear(_nextPalette.Mapping);
        LoadPalette(999);
    }

    public SciPalette NextPalette => _nextPalette;

    public SciPalette CurrentPalette => _currentPalette;

    /// <summary>The palette the host shows; <see cref="HardwarePaletteVersion"/> changes when it does.</summary>
    public ReadOnlySpan<Rgb> HardwarePalette => _hardwarePalette;

    public int HardwarePaletteVersion { get; private set; }

    public bool[] CycleMap => _cycleMap;

    public bool LoadPalette(int resourceId)
    {
        var palResource = _g.ResMan.FindResource(ResourceType.Palette, resourceId);
        if (palResource is null)
        {
            return false;
        }

        Submit(new HunkPalette(palResource.Data!));
        return true;
    }

    /// <summary>The nearest colour of the current palette below the remap range.</summary>
    public short MatchColor(byte r, byte g, byte b)
    {
        short bestIndex = 0;
        int bestDifference = 0xFFFFF;
        for (int i = 0; i < _g.Remap.StartColor; i++)
        {
            int difference = _currentPalette.Colors[i].R - r;
            difference *= difference;
            if (bestDifference <= difference)
            {
                continue;
            }

            int channelDifference = _currentPalette.Colors[i].G - g;
            difference += channelDifference * channelDifference;
            if (bestDifference <= difference)
            {
                continue;
            }

            channelDifference = _currentPalette.Colors[i].B - b;
            difference += channelDifference * channelDifference;
            if (bestDifference <= difference)
            {
                continue;
            }

            bestDifference = difference;
            bestIndex = (short)i;
        }

        return bestIndex;
    }

    public byte PlatformBlack => _g.IsMacintosh ? (byte)255 : (byte)0;

    public byte PlatformWhite => _g.IsMacintosh ? (byte)0 : (byte)255;

    private static bool ColorsEqual(SciPalette a, SciPalette b)
    {
        for (int i = 0; i < 256; i++)
        {
            if (!a.Colors[i].Equals(b.Colors[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Merges the used entries of a palette into the source palette.</summary>
    public void Submit(SciPalette palette)
    {
        if (_needsUpdate)
        {
            MergePalette(_sourcePalette, palette);
        }
        else
        {
            var oldSourcePalette = _sourcePalette.Clone();
            MergePalette(_sourcePalette, palette);
            if (!ColorsEqual(_sourcePalette, oldSourcePalette))
            {
                _version++;
                _needsUpdate = true;
            }
        }
    }

    public void Submit(HunkPalette hunkPalette)
    {
        if (hunkPalette.Version == _version)
        {
            return;
        }

        Submit(hunkPalette.ToPalette());
        hunkPalette.SetVersion(_version);
    }

    /// <summary>Applies the varies, cycles and fades of this frame to the next palette. Returns whether a remap table changed.</summary>
    public bool UpdateForFrame()
    {
        ApplyAll();
        _needsUpdate = false;
        return _g.Remap.RemapAllTables(!ColorsEqual(_nextPalette, _currentPalette));
    }

    /// <summary>Copies the source palette to the next palette without fades, cycles or varies, and remaps.</summary>
    public void UpdateFFrame()
    {
        Array.Copy(_sourcePalette.Colors, _nextPalette.Colors, 256);
        _needsUpdate = false;
        _g.Remap.RemapAllTables(!ColorsEqual(_nextPalette, _currentPalette));
    }

    /// <summary>Moves the next palette to the current one and to the hardware.</summary>
    public void UpdateHardware()
    {
        if (ColorsEqual(_currentPalette, _nextPalette) && !_gammaChanged)
        {
            return;
        }

        // a Windows-only demo and game carry bogus entries above 236, and the Macintosh games use
        // 236 for black to stay clear of the system palette; those entries render black
        int maxIndex = 254;
        if (_g.GameId == SciGameId.Hoyle5 || (_g.GameId == SciGameId.Gk2 && _g.State.IsDemo) || _g.IsMacintosh)
        {
            maxIndex = 235;
        }

        for (int i = 0; i <= maxIndex; i++)
        {
            _currentPalette.Colors[i] = _nextPalette.Colors[i];

            // every entry is copied, used or not: Phantasmagoria's title screen references entries
            // outside its palette and expects them black rather than stale
            var c = _currentPalette.Colors[i];
            _hardwarePalette[i] = _gammaLevel == -1
                ? new Rgb(c.R, c.G, c.B)
                : new Rgb(GammaTables[_gammaLevel][c.R], GammaTables[_gammaLevel][c.G], GammaTables[_gammaLevel][c.B]);
        }

        // the last colour is always white
        _hardwarePalette[255] = Rgb.White;
        HardwarePaletteVersion++;
        _gammaChanged = false;
    }

    private SciPalette GetPaletteFromResource(int resourceId)
    {
        var palResource = _g.ResMan.FindResource(ResourceType.Palette, resourceId)
            ?? throw new SciVmException($"Could not load vary palette {resourceId}");
        return new HunkPalette(palResource.Data!).ToPalette();
    }

    /// <summary>
    /// Copies the used colours, index 255 included although the hardware forces it white: not
    /// doing so breaks badly programmed rooms such as Phantasmagoria's 6400.
    /// </summary>
    private static void MergePalette(SciPalette to, SciPalette from)
    {
        for (int i = 0; i < 256; i++)
        {
            if (from.Colors[i].Used != 0)
            {
                to.Colors[i] = from.Colors[i];
            }
        }
    }

    private void ApplyAll()
    {
        ApplyVary();
        ApplyCycles();
        ApplyFade();
    }

    // ---- varying ----

    public void SetVary(SciPalette target, short percent, int ticks, short fromColor, short toColor)
    {
        SetTarget(target);
        SetVaryTime(percent, ticks);
        if (fromColor > -1)
        {
            _varyFromColor = (byte)fromColor;
        }

        if (toColor > -1)
        {
            if (toColor >= 256)
            {
                throw new SciVmException($"PalVary to colour {toColor} out of range");
            }

            _varyToColor = (byte)toColor;
        }
    }

    public short VaryPercent => Math.Abs(_varyPercent);

    public void SetVaryPercent(short percent, int ticks)
    {
        if (_varyTargetPalette is not null)
        {
            SetVaryTime(percent, ticks);
        }
    }

    public void SetVaryTime(int time)
    {
        if (_varyTargetPalette is not null)
        {
            SetVaryTime(_varyTargetPercent, time);
        }
    }

    public void SetVaryTime(short percent, int ticks)
    {
        _varyLastTick = _g.TickCount;
        if (ticks == 0 || _varyPercent == percent)
        {
            _varyDirection = 0;
            _varyTargetPercent = _varyPercent = percent;
        }
        else
        {
            _varyTime = ticks / (percent - _varyPercent);
            _varyTargetPercent = percent;
            if (_varyTime > 0)
            {
                _varyDirection = 1;
            }
            else if (_varyTime < 0)
            {
                _varyDirection = -1;
                _varyTime = -_varyTime;
            }
            else
            {
                _varyDirection = 0;
                _varyTargetPercent = _varyPercent = percent;
            }
        }
    }

    public void VaryOff()
    {
        _varyNumTimesPaused = 0;
        _varyPercent = _varyTargetPercent = 0;
        _varyFromColor = 0;
        _varyToColor = 255;
        _varyDirection = 0;
        _varyTargetPalette = null;
        _varyStartPalette = null;
    }

    public void VaryPause()
    {
        _varyDirection = 0;
        _varyNumTimesPaused++;
    }

    public void VaryOn()
    {
        if (_varyNumTimesPaused > 0)
        {
            _varyNumTimesPaused--;
        }

        if (_varyTargetPalette is not null && _varyNumTimesPaused == 0)
        {
            if (_varyPercent != _varyTargetPercent && _varyTime != 0)
            {
                _varyDirection = (short)(_varyTargetPercent - _varyPercent > 0 ? 1 : -1);
            }
            else
            {
                _varyPercent = _varyTargetPercent;
            }
        }
    }

    public void SetTarget(SciPalette palette) => _varyTargetPalette = palette.Clone();

    public void SetStart(SciPalette palette) => _varyStartPalette = palette.Clone();

    public void MergeStart(SciPalette palette)
    {
        if (_varyStartPalette is not null)
        {
            MergePalette(_varyStartPalette, palette);
        }
        else
        {
            _varyStartPalette = palette.Clone();
        }
    }

    public void MergeTarget(SciPalette palette)
    {
        if (_varyTargetPalette is not null)
        {
            MergePalette(_varyTargetPalette, palette);
        }
        else
        {
            _varyTargetPalette = palette.Clone();
        }
    }

    public void ApplyVary()
    {
        uint now = _g.TickCount;
        while ((int)(now - _varyLastTick) > _varyTime && _varyDirection != 0)
        {
            _varyLastTick += (uint)_varyTime;
            if (_varyPercent == _varyTargetPercent)
            {
                _varyDirection = 0;
            }

            _varyPercent += _varyDirection;
        }

        if (_varyPercent == 0 || _varyTargetPalette is null)
        {
            for (int i = 0; i < 256; i++)
            {
                _nextPalette.Colors[i] = _varyStartPalette is not null && i >= _varyFromColor && i <= _varyToColor
                    ? _varyStartPalette.Colors[i]
                    : _sourcePalette.Colors[i];
            }
        }
        else
        {
            for (int i = 0; i < 256; i++)
            {
                if (i >= _varyFromColor && i <= _varyToColor)
                {
                    var targetColor = _varyTargetPalette.Colors[i];
                    var sourceColor = _varyStartPalette is not null ? _varyStartPalette.Colors[i] : _sourcePalette.Colors[i];
                    _nextPalette.Colors[i] = new SciColor
                    {
                        R = (byte)(((targetColor.R - sourceColor.R) * _varyPercent / 100) + sourceColor.R),
                        G = (byte)(((targetColor.G - sourceColor.G) * _varyPercent / 100) + sourceColor.G),
                        B = (byte)(((targetColor.B - sourceColor.B) * _varyPercent / 100) + sourceColor.B),
                        Used = sourceColor.Used,
                    };
                }
                else
                {
                    _nextPalette.Colors[i] = _sourcePalette.Colors[i];
                }
            }
        }
    }

    public void KernelPalVarySet(int paletteId, short percent, int ticks, short fromColor, short toColor)
    {
        SciPalette palette;
        if (_g.Version == SciVersion.V3 && paletteId == 0xFFFF)
        {
            // SCI3 inverts the current palette; the inversion excludes toColor (RAMA room 6201 needs that)
            palette = _currentPalette.Clone();
            for (int i = fromColor; i < toColor; i++)
            {
                var c = palette.Colors[i];
                palette.Colors[i] = new SciColor { Used = c.Used, R = (byte)~c.R, G = (byte)~c.G, B = (byte)~c.B };
            }
        }
        else
        {
            palette = GetPaletteFromResource(paletteId);
        }

        SetVary(palette, percent, ticks, fromColor, toColor);
    }

    public void KernelPalVaryMergeTarget(int paletteId) => MergeTarget(GetPaletteFromResource(paletteId));

    public void KernelPalVarySetTarget(int paletteId) => SetTarget(GetPaletteFromResource(paletteId));

    public void KernelPalVarySetStart(int paletteId) => SetStart(GetPaletteFromResource(paletteId));

    public void KernelPalVaryMergeStart(int paletteId) => MergeStart(GetPaletteFromResource(paletteId));

    public void KernelPalVaryPause(bool pause)
    {
        if (pause)
        {
            VaryPause();
        }
        else
        {
            VaryOn();
        }
    }

    // ---- cycling ----

    /// <summary>Cycles the entries from fromColor to toColor inclusive; cyclers may not overlap, and fromColor is their key.</summary>
    public void SetCycle(byte fromColor, byte toColor, short direction, short delay)
    {
        if (fromColor >= toColor)
        {
            throw new SciVmException($"PalCycle: from colour {fromColor} is not below to colour {toColor}");
        }

        var cycler = GetCycler(fromColor);
        if (cycler is not null)
        {
            ClearCycleMap(fromColor, cycler.NumColorsToCycle);
        }
        else
        {
            for (int i = 0; i < NumCyclers; i++)
            {
                if (_cyclers[i] is null)
                {
                    cycler = new PalCycler();
                    _cyclers[i] = cycler;
                    break;
                }
            }
        }

        // with every slot taken, the cycler that last moved the longest ago is replaced
        if (cycler is null)
        {
            uint now = _g.TickCount;
            uint minUpdateDelta = uint.MaxValue;
            foreach (var candidate in _cyclers)
            {
                uint updateDelta = now - candidate!.LastUpdateTick;
                if (updateDelta < minUpdateDelta)
                {
                    minUpdateDelta = updateDelta;
                    cycler = candidate;
                }
            }

            ClearCycleMap(cycler!.FromColor, cycler.NumColorsToCycle);
        }

        ushort numColorsToCycle = (ushort)(toColor - fromColor);
        if (_g.Features.HasMidPaletteCode)
        {
            numColorsToCycle++;
        }

        cycler.FromColor = fromColor;
        cycler.NumColorsToCycle = numColorsToCycle;
        cycler.CurrentCycle = fromColor;
        cycler.Direction = direction < 0 ? PalCyclerDirection.Backward : PalCyclerDirection.Forward;
        cycler.Delay = delay;
        cycler.LastUpdateTick = _g.TickCount;
        cycler.NumTimesPaused = 0;
        SetCycleMap(fromColor, numColorsToCycle);
    }

    public void DoCycle(byte fromColor, short speed)
    {
        var cycler = GetCycler(fromColor);
        if (cycler is not null)
        {
            cycler.LastUpdateTick = _g.TickCount;
            UpdateCycler(cycler, speed);
        }
    }

    public void CycleOn(byte fromColor)
    {
        var cycler = GetCycler(fromColor);
        if (cycler is not null && cycler.NumTimesPaused > 0)
        {
            cycler.NumTimesPaused--;
        }
    }

    public void CyclePause(byte fromColor)
    {
        var cycler = GetCycler(fromColor);
        if (cycler is not null)
        {
            cycler.NumTimesPaused++;
        }
    }

    public void CycleAllOn()
    {
        foreach (var cycler in _cyclers)
        {
            if (cycler is not null && cycler.NumTimesPaused > 0)
            {
                cycler.NumTimesPaused--;
            }
        }
    }

    public void CycleAllPause()
    {
        foreach (var cycler in _cyclers)
        {
            if (cycler is not null)
            {
                // currentCycle runs 0 to the cycle length while fromColor is a palette index; applyAllCycles puts it back in range
                cycler.CurrentCycle = cycler.FromColor;
            }
        }

        ApplyAllCycles();
        foreach (var cycler in _cyclers)
        {
            if (cycler is not null)
            {
                cycler.NumTimesPaused++;
            }
        }
    }

    public void CycleOff(byte fromColor)
    {
        for (int i = 0; i < NumCyclers; i++)
        {
            var cycler = _cyclers[i];
            if (cycler is not null && cycler.FromColor == fromColor)
            {
                ClearCycleMap(fromColor, cycler.NumColorsToCycle);
                _cyclers[i] = null;
                break;
            }
        }
    }

    public void CycleAllOff()
    {
        for (int i = 0; i < NumCyclers; i++)
        {
            var cycler = _cyclers[i];
            if (cycler is not null)
            {
                ClearCycleMap(cycler.FromColor, cycler.NumColorsToCycle);
                _cyclers[i] = null;
            }
        }
    }

    private static void UpdateCycler(PalCycler cycler, short speed)
    {
        int currentCycle = cycler.CurrentCycle;
        int numColorsToCycle = cycler.NumColorsToCycle;
        if (cycler.Direction == PalCyclerDirection.Backward)
        {
            currentCycle = (currentCycle - (speed % numColorsToCycle)) + numColorsToCycle;
        }
        else
        {
            currentCycle += speed;
        }

        cycler.CurrentCycle = (byte)(currentCycle % numColorsToCycle);
    }

    // Sierra's loop bounds are reproduced as they are: the end is numColors, not fromColor + numColors
    private void ClearCycleMap(int fromColor, int numColorsToClear)
    {
        for (int i = fromColor; i < numColorsToClear; i++)
        {
            _cycleMap[i] = false;
        }
    }

    private void SetCycleMap(int fromColor, int numColorsToSet)
    {
        for (int i = fromColor; i < numColorsToSet; i++)
        {
            if (_cycleMap[i])
            {
                throw new SciVmException("Cycles intersect");
            }

            _cycleMap[i] = true;
        }
    }

    private PalCycler? GetCycler(int fromColor)
    {
        foreach (var cycler in _cyclers)
        {
            if (cycler is not null && cycler.FromColor == fromColor)
            {
                return cycler;
            }
        }

        return null;
    }

    private readonly SciColor[] _paletteCopy = new SciColor[256];

    private void ApplyAllCycles()
    {
        Array.Copy(_nextPalette.Colors, _paletteCopy, 256);
        foreach (var cycler in _cyclers)
        {
            if (cycler is null)
            {
                continue;
            }

            cycler.CurrentCycle = (byte)((cycler.CurrentCycle + 1) % cycler.NumColorsToCycle);
            for (int j = 0; j < cycler.NumColorsToCycle; j++)
            {
                _nextPalette.Colors[cycler.FromColor + j] = _paletteCopy[cycler.FromColor + (cycler.CurrentCycle + j) % cycler.NumColorsToCycle];
            }
        }
    }

    private void ApplyCycles()
    {
        Array.Copy(_nextPalette.Colors, _paletteCopy, 256);
        uint now = _g.TickCount;
        foreach (var cycler in _cyclers)
        {
            if (cycler is null)
            {
                continue;
            }

            if (cycler.Delay != 0 && cycler.NumTimesPaused == 0)
            {
                while (cycler.Delay + cycler.LastUpdateTick < now)
                {
                    UpdateCycler(cycler, 1);
                    cycler.LastUpdateTick += (uint)cycler.Delay;
                }
            }

            for (int j = 0; j < cycler.NumColorsToCycle; j++)
            {
                _nextPalette.Colors[cycler.FromColor + j] = _paletteCopy[cycler.FromColor + (cycler.CurrentCycle + j) % cycler.NumColorsToCycle];
            }
        }
    }

    // ---- fading ----

    /// <summary>Sets the intensity of a range of entries in percent; 0 is dark, above 100 brightens.</summary>
    public void SetFade(ushort percent, byte fromColor, ushort toColor)
    {
        if (fromColor > toColor)
        {
            return;
        }

        // SQ6's Sierra logo and main menu call this with toColor 256
        if (toColor > 255)
        {
            toColor = 255;
        }

        for (int i = fromColor; i <= toColor; i++)
        {
            _fadeTable[i] = percent;
        }
    }

    public void FadeOff() => SetFade(100, 0, 255);

    public void ApplyFade()
    {
        for (int i = 0; i < 256; i++)
        {
            if (_fadeTable[i] == 100)
            {
                continue;
            }

            var color = _nextPalette.Colors[i];
            color.R = (byte)Math.Min(255, color.R * _fadeTable[i] / 100);
            color.G = (byte)Math.Min(255, color.G * _fadeTable[i] / 100);
            color.B = (byte)Math.Min(255, color.B * _fadeTable[i] / 100);
            _nextPalette.Colors[i] = color;
        }
    }

    // ---- gamma ----

    /// <summary>Sets the gamma correction, from 0 (off) to <see cref="NumGammaTables"/>.</summary>
    public void SetGamma(short level)
    {
        _gammaLevel = Math.Clamp((int)level, 0, NumGammaTables) - 1;
        _gammaChanged = true;
    }

    private static readonly byte[][] GammaTables =
    [
        [
            0, 2, 3, 5, 6, 7, 9, 10, 11, 13, 14, 15, 16, 18, 19, 20, 21, 22, 23, 25, 26, 27, 28, 29, 30, 32, 33, 34, 35, 36, 37, 38,
            39, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72,
            74, 75, 76, 77, 78, 79, 80, 81, 82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 97, 98, 99, 100, 101, 102, 103, 104, 105,
            106, 107, 108, 109, 110, 111, 112, 113, 114, 115, 116, 117, 118, 119, 120, 121, 122, 123, 124, 125, 126, 127, 128, 128, 129, 130, 131, 132, 133, 134, 135, 136,
            137, 138, 139, 140, 141, 142, 143, 144, 145, 146, 147, 148, 149, 150, 151, 152, 153, 153, 154, 155, 156, 157, 158, 159, 160, 161, 162, 163, 164, 165, 166, 167,
            168, 169, 170, 171, 171, 172, 173, 174, 175, 176, 177, 178, 179, 180, 181, 182, 183, 184, 185, 186, 186, 187, 188, 189, 190, 191, 192, 193, 194, 195, 196, 197,
            198, 199, 199, 200, 201, 202, 203, 204, 205, 206, 207, 208, 209, 210, 211, 211, 212, 213, 214, 215, 216, 217, 218, 219, 220, 221, 222, 222, 223, 224, 225, 226,
            227, 228, 229, 230, 231, 232, 232, 233, 234, 235, 236, 237, 238, 239, 240, 241, 242, 242, 243, 244, 245, 246, 247, 248, 249, 250, 251, 251, 252, 253, 254, 255,
        ],
        [
            0, 3, 5, 6, 8, 10, 11, 13, 14, 16, 17, 19, 20, 22, 23, 24, 26, 27, 28, 30, 31, 32, 33, 35, 36, 37, 38, 40, 41, 42, 43, 44,
            46, 47, 48, 49, 50, 51, 53, 54, 55, 56, 57, 58, 59, 60, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71, 73, 74, 75, 76, 77, 78, 79, 80,
            81, 82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 97, 99, 100, 101, 102, 103, 104, 105, 106, 107, 108, 108, 109, 110, 111, 112,
            113, 114, 115, 116, 117, 118, 119, 120, 121, 122, 123, 124, 125, 126, 127, 128, 129, 130, 131, 132, 133, 134, 135, 136, 136, 137, 138, 139, 140, 141, 142, 143,
            144, 145, 146, 147, 148, 149, 150, 151, 151, 152, 153, 154, 155, 156, 157, 158, 159, 160, 161, 162, 162, 163, 164, 165, 166, 167, 168, 169, 170, 171, 172, 172,
            173, 174, 175, 176, 177, 178, 179, 180, 180, 181, 182, 183, 184, 185, 186, 187, 188, 188, 189, 190, 191, 192, 193, 194, 195, 196, 196, 197, 198, 199, 200, 201,
            202, 202, 203, 204, 205, 206, 207, 208, 209, 209, 210, 211, 212, 213, 214, 215, 215, 216, 217, 218, 219, 220, 221, 221, 222, 223, 224, 225, 226, 227, 227, 228,
            229, 230, 231, 232, 233, 233, 234, 235, 236, 237, 238, 238, 239, 240, 241, 242, 243, 243, 244, 245, 246, 247, 248, 249, 249, 250, 251, 252, 253, 254, 254, 255,
        ],
        [
            0, 4, 6, 8, 10, 12, 14, 16, 18, 19, 21, 23, 24, 26, 27, 29, 30, 32, 33, 35, 36, 37, 39, 40, 41, 43, 44, 45, 47, 48, 49, 50,
            52, 53, 54, 55, 57, 58, 59, 60, 61, 62, 64, 65, 66, 67, 68, 69, 71, 72, 73, 74, 75, 76, 77, 78, 79, 81, 82, 83, 84, 85, 86, 87,
            88, 89, 90, 91, 92, 93, 94, 95, 96, 97, 98, 99, 100, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 112, 113, 114, 115, 116, 117, 118, 119,
            120, 121, 122, 123, 124, 125, 126, 127, 128, 129, 130, 131, 132, 133, 134, 135, 135, 136, 137, 138, 139, 140, 141, 142, 143, 144, 145, 146, 146, 147, 148, 149,
            150, 151, 152, 153, 154, 155, 156, 156, 157, 158, 159, 160, 161, 162, 163, 163, 164, 165, 166, 167, 168, 169, 170, 170, 171, 172, 173, 174, 175, 176, 177, 177,
            178, 179, 180, 181, 182, 183, 183, 184, 185, 186, 187, 188, 188, 189, 190, 191, 192, 193, 194, 194, 195, 196, 197, 198, 199, 199, 200, 201, 202, 203, 203, 204,
            205, 206, 207, 208, 208, 209, 210, 211, 212, 212, 213, 214, 215, 216, 217, 217, 218, 219, 220, 221, 221, 222, 223, 224, 225, 225, 226, 227, 228, 229, 229, 230,
            231, 232, 233, 233, 234, 235, 236, 237, 237, 238, 239, 240, 240, 241, 242, 243, 244, 244, 245, 246, 247, 247, 248, 249, 250, 251, 251, 252, 253, 254, 254, 255,
        ],
        [
            0, 5, 9, 11, 14, 16, 19, 21, 23, 25, 26, 28, 30, 32, 33, 35, 37, 38, 40, 41, 43, 44, 46, 47, 49, 50, 52, 53, 54, 56, 57, 58,
            60, 61, 62, 64, 65, 66, 67, 69, 70, 71, 72, 73, 75, 76, 77, 78, 79, 80, 82, 83, 84, 85, 86, 87, 88, 89, 91, 92, 93, 94, 95, 96,
            97, 98, 99, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115, 116, 117, 118, 119, 120, 121, 122, 123, 124, 125, 126, 127, 128,
            129, 130, 131, 132, 133, 134, 134, 135, 136, 137, 138, 139, 140, 141, 142, 143, 144, 144, 145, 146, 147, 148, 149, 150, 151, 152, 152, 153, 154, 155, 156, 157,
            158, 158, 159, 160, 161, 162, 163, 164, 164, 165, 166, 167, 168, 169, 169, 170, 171, 172, 173, 174, 174, 175, 176, 177, 178, 179, 179, 180, 181, 182, 183, 183,
            184, 185, 186, 187, 187, 188, 189, 190, 191, 191, 192, 193, 194, 195, 195, 196, 197, 198, 199, 199, 200, 201, 202, 202, 203, 204, 205, 205, 206, 207, 208, 209,
            209, 210, 211, 212, 212, 213, 214, 215, 215, 216, 217, 218, 218, 219, 220, 221, 221, 222, 223, 224, 224, 225, 226, 227, 227, 228, 229, 230, 230, 231, 232, 232,
            233, 234, 235, 235, 236, 237, 238, 238, 239, 240, 240, 241, 242, 243, 243, 244, 245, 245, 246, 247, 248, 248, 249, 250, 250, 251, 252, 252, 253, 254, 255, 255,
        ],
        [
            0, 9, 14, 18, 21, 24, 27, 29, 32, 34, 37, 39, 41, 43, 45, 47, 48, 50, 52, 54, 55, 57, 59, 60, 62, 63, 65, 66, 68, 69, 71, 72,
            73, 75, 76, 77, 79, 80, 81, 83, 84, 85, 86, 88, 89, 90, 91, 92, 94, 95, 96, 97, 98, 99, 100, 102, 103, 104, 105, 106, 107, 108, 109, 110,
            111, 112, 113, 114, 115, 116, 117, 118, 119, 120, 121, 122, 123, 124, 125, 126, 127, 128, 129, 130, 131, 132, 133, 134, 135, 136, 137, 137, 138, 139, 140, 141,
            142, 143, 144, 145, 145, 146, 147, 148, 149, 150, 151, 151, 152, 153, 154, 155, 156, 156, 157, 158, 159, 160, 161, 161, 162, 163, 164, 165, 165, 166, 167, 168,
            169, 169, 170, 171, 172, 173, 173, 174, 175, 176, 176, 177, 178, 179, 179, 180, 181, 182, 182, 183, 184, 185, 185, 186, 187, 188, 188, 189, 190, 191, 191, 192,
            193, 194, 194, 195, 196, 196, 197, 198, 199, 199, 200, 201, 201, 202, 203, 203, 204, 205, 206, 206, 207, 208, 208, 209, 210, 210, 211, 212, 212, 213, 214, 214,
            215, 216, 216, 217, 218, 218, 219, 220, 220, 221, 222, 222, 223, 224, 224, 225, 226, 226, 227, 228, 228, 229, 230, 230, 231, 231, 232, 233, 233, 234, 235, 235,
            236, 237, 237, 238, 238, 239, 240, 240, 241, 242, 242, 243, 243, 244, 245, 245, 246, 247, 247, 248, 248, 249, 250, 250, 251, 251, 252, 253, 253, 254, 254, 255,
        ],
        [
            0, 16, 23, 28, 32, 36, 39, 42, 45, 48, 50, 53, 55, 58, 60, 62, 64, 66, 68, 70, 71, 73, 75, 77, 78, 80, 81, 83, 84, 86, 87, 89,
            90, 92, 93, 94, 96, 97, 98, 100, 101, 102, 103, 105, 106, 107, 108, 109, 111, 112, 113, 114, 115, 116, 117, 118, 119, 121, 122, 123, 124, 125, 126, 127,
            128, 129, 130, 131, 132, 133, 134, 135, 135, 136, 137, 138, 139, 140, 141, 142, 143, 144, 145, 145, 146, 147, 148, 149, 150, 151, 151, 152, 153, 154, 155, 156,
            156, 157, 158, 159, 160, 160, 161, 162, 163, 164, 164, 165, 166, 167, 167, 168, 169, 170, 170, 171, 172, 173, 173, 174, 175, 176, 176, 177, 178, 179, 179, 180,
            181, 181, 182, 183, 183, 184, 185, 186, 186, 187, 188, 188, 189, 190, 190, 191, 192, 192, 193, 194, 194, 195, 196, 196, 197, 198, 198, 199, 199, 200, 201, 201,
            202, 203, 203, 204, 204, 205, 206, 206, 207, 208, 208, 209, 209, 210, 211, 211, 212, 212, 213, 214, 214, 215, 215, 216, 217, 217, 218, 218, 219, 220, 220, 221,
            221, 222, 222, 223, 224, 224, 225, 225, 226, 226, 227, 228, 228, 229, 229, 230, 230, 231, 231, 232, 233, 233, 234, 234, 235, 235, 236, 236, 237, 237, 238, 238,
            239, 240, 240, 241, 241, 242, 242, 243, 243, 244, 244, 245, 245, 246, 246, 247, 247, 248, 248, 249, 249, 250, 250, 251, 251, 252, 252, 253, 253, 254, 254, 255,
        ],
    ];
}
