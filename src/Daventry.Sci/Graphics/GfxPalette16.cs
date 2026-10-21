using Daventry.Core.Storage;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Graphics;

/// <summary>
/// The system palette of SCI0 to SCI1.1 games: loading palette resources, merging or copying view
/// and picture palettes into it, colour matching, intensity, animation and pal-vary fades. Port of
/// ScummVM's palette16.cpp. The pal-vary timer is driven by <see cref="PalVaryTick"/>, which the
/// engine calls sixty times a second. Macintosh icon-bar CLUTs are not ported.
/// </summary>
public sealed partial class GfxPalette16
{
    public const ushort MatchPerfect = 0x8000;
    public const ushort MatchColorMask = 0xFF;

    private const int PalFormatConstant = 1;
    private const int PalFormatVariable = 0;

    private readonly ResourceManager _resMan;
    private readonly GfxScreen _screen;
    private readonly IGameContainer _game;
    private readonly int _totalScreenColors;
    private readonly List<(byte From, uint Schedule)> _schedules = [];
    private readonly byte[] _bpal = new byte[3 * 256];

    private bool _sysPaletteChanged;
    private readonly bool _useMerging;
    private readonly bool _use16bitColorMatch;

    private int _palVaryResourceId = -1;
    private readonly SciPalette _palVaryOriginPalette = new();
    private readonly SciPalette _palVaryTargetPalette = new();
    private int _palVaryStep;
    private int _palVaryStepStop;
    private int _palVaryDirection;
    private int _palVaryTicks;
    private int _palVaryPaused;
    private int _palVarySignal;
    private bool _palVaryZeroTick;
    private bool _palVaryTimerInstalled;
    private int _palVaryTimerCountdown;

    public GfxPalette16(ResourceManager resMan, GfxScreen screen, IGameContainer game, SciVersion version)
    {
        _resMan = resMan;
        _screen = screen;
        _game = game;

        SysPalette.Timestamp = 0;
        for (int color = 0; color < 256; color++)
        {
            SysPalette.Colors[color] = default;
            SysPalette.Intensity[color] = 100;
            SysPalette.Mapping[color] = (byte)color;
        }

        // black and white are hardcoded
        SysPalette.Colors[0].Used = 1;
        SysPalette.Colors[255].Used = 1;
        SysPalette.Colors[255].R = 255;
        SysPalette.Colors[255].G = 255;
        SysPalette.Colors[255].B = 255;
        _sysPaletteChanged = false;

        // The QFG3, EcoQuest 1 and Laura Bow 2 demos, PQ1 VGA and the Crazy Nick's Picks use the
        // older palette format and real merging rather than SCI1.1's copying of used colours.
        if (version < SciVersion.V1_1)
        {
            _useMerging = true;
            _use16bitColorMatch = true;
        }
        else if (version == SciVersion.V1_1)
        {
            // in-between SCI1.1 interpreters need detecting
            _useMerging = resMan.DetectPaletteMergingSci11();
            _use16bitColorMatch = _useMerging;
        }
        else
        {
            _useMerging = false;
            _use16bitColorMatch = false;
        }

        PalVaryInit();
        _totalScreenColors = resMan.ViewType switch
        {
            ViewType.Ega => 16,
            ViewType.Amiga => 32,
            ViewType.Amiga64 => 64,
            _ => 256,
        };
    }

    public SciPalette SysPalette { get; } = new();

    public bool IsMerging => _useMerging;

    public bool IsUsing16BitColorMatch => _use16bitColorMatch;

    public bool PalVaryActive => _palVaryResourceId != -1;

    /// <summary>Loads the game's default palette: EGA, the Amiga "spal" file, or palette 999.</summary>
    public void SetDefault()
    {
        if (_resMan.ViewType == ViewType.Ega)
        {
            SetEga();
        }
        else if (_resMan.ViewType is ViewType.Amiga or ViewType.Amiga64)
        {
            SetAmiga();
        }
        else
        {
            KernelSetFromResource(999, true);
        }
    }

    /// <summary>Decodes a palette resource or embedded palette. Port of createFromData.</summary>
    public void CreateFromData(ReadOnlySpan<byte> data, SciPalette paletteOut)
    {
        paletteOut.Clear();

        if (data.Length < 37)
        {
            // picture 0 of SQ5 has a broken palette without a full header
            return;
        }

        int palFormat;
        int palOffset;
        int palColorStart;
        int palColorCount;

        // the formats are not version exclusive, they just first appeared in these versions
        if ((data[0] == 0 && data[1] == 1) || (data[0] == 0 && data[1] == 0 && ReadUInt16(data, 29) == 0))
        {
            // SCI0/SCI1 palette
            palFormat = PalFormatVariable;
            palOffset = 260;
            palColorStart = 0;
            palColorCount = 256;
        }
        else
        {
            // SCI1.1 palette
            palFormat = data[32];
            palOffset = 37;
            palColorStart = data[25];
            palColorCount = ReadUInt16(data, 29);
        }

        switch (palFormat)
        {
            case PalFormatConstant:
                if (data.Length < palOffset + 3 * palColorCount)
                {
                    return;
                }

                for (int colorNo = palColorStart; colorNo < palColorStart + palColorCount && colorNo < 256; colorNo++)
                {
                    paletteOut.Colors[colorNo].Used = 1;
                    paletteOut.Colors[colorNo].R = data[palOffset++];
                    paletteOut.Colors[colorNo].G = data[palOffset++];
                    paletteOut.Colors[colorNo].B = data[palOffset++];
                }

                break;
            case PalFormatVariable:
                if (data.Length < palOffset + 4 * palColorCount)
                {
                    return;
                }

                for (int colorNo = palColorStart; colorNo < palColorStart + palColorCount && colorNo < 256; colorNo++)
                {
                    paletteOut.Colors[colorNo].Used = data[palOffset++];
                    paletteOut.Colors[colorNo].R = data[palOffset++];
                    paletteOut.Colors[colorNo].G = data[palOffset++];
                    paletteOut.Colors[colorNo].B = data[palOffset++];
                }

                break;
        }
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) => (ushort)(data[offset] | (data[offset + 1] << 8));

    /// <summary>Loads the Amiga palette from the "spal" file. Returns false when there is none.</summary>
    public bool SetAmiga()
    {
        if (!_game.Exists("spal"))
        {
            return false;
        }

        var file = _game.ReadAllBytes("spal");
        if (file.Length < 64)
        {
            throw new InvalidDataException("Amiga palette file ends prematurely");
        }

        for (int curColor = 0; curColor < 32; curColor++)
        {
            byte byte1 = file[curColor * 2];
            byte byte2 = file[curColor * 2 + 1];
            SysPalette.Colors[curColor].Used = 1;
            SysPalette.Colors[curColor].R = (byte)((byte1 & 0x0F) * 0x11);
            SysPalette.Colors[curColor].G = (byte)(((byte2 & 0xF0) >> 4) * 0x11);
            SysPalette.Colors[curColor].B = (byte)((byte2 & 0x0F) * 0x11);
            if (_totalScreenColors == 64)
            {
                // the halfbrite twin
                SysPalette.Colors[curColor + 32].Used = 1;
                SysPalette.Colors[curColor + 32].R = (byte)(SysPalette.Colors[curColor].R >> 1);
                SysPalette.Colors[curColor + 32].G = (byte)(SysPalette.Colors[curColor].G >> 1);
                SysPalette.Colors[curColor + 32].B = (byte)(SysPalette.Colors[curColor].B >> 1);
            }
        }

        CopySysPaletteToScreen(true);
        return true;
    }

    /// <summary>Some Amiga SCI1 pictures set half of the palette.</summary>
    public void ModifyAmigaPalette(ReadOnlySpan<byte> data)
    {
        int curPos = 0;
        for (int curColor = 0; curColor < 16; curColor++)
        {
            byte byte1 = data[curPos++];
            byte byte2 = data[curPos++];
            SysPalette.Colors[curColor].R = (byte)((byte1 & 0x0F) * 0x11);
            SysPalette.Colors[curColor].G = (byte)(((byte2 & 0xF0) >> 4) * 0x11);
            SysPalette.Colors[curColor].B = (byte)((byte2 & 0x0F) * 0x11);
            if (_totalScreenColors == 64)
            {
                SysPalette.Colors[curColor + 32].R = (byte)(SysPalette.Colors[curColor].R >> 1);
                SysPalette.Colors[curColor + 32].G = (byte)(SysPalette.Colors[curColor].G >> 1);
                SysPalette.Colors[curColor + 32].B = (byte)(SysPalette.Colors[curColor].B >> 1);
            }
        }

        CopySysPaletteToScreen(true);
    }

    private static byte BlendColors(byte c1, byte c2)
    {
        // gamma 2.2
        double t = Math.Pow(c1 / 255.0, 2.2) * 255.0 + Math.Pow(c2 / 255.0, 2.2) * 255.0;
        return (byte)(0.5 + Math.Pow(0.5 * t / 255.0, 1.0 / 2.2) * 255.0);
    }

    public void SetEga()
    {
        ReadOnlySpan<(byte R, byte G, byte B)> ega =
        [
            (0x00, 0x00, 0x00), (0x00, 0x00, 0xAA), (0x00, 0xAA, 0x00), (0x00, 0xAA, 0xAA),
            (0xAA, 0x00, 0x00), (0xAA, 0x00, 0xAA), (0xAA, 0x55, 0x00), (0xAA, 0xAA, 0xAA),
            (0x55, 0x55, 0x55), (0x55, 0x55, 0xFF), (0x55, 0xFF, 0x55), (0x55, 0xFF, 0xFF),
            (0xFF, 0x55, 0x55), (0xFF, 0x55, 0xFF), (0xFF, 0xFF, 0x55), (0xFF, 0xFF, 0xFF),
        ];

        for (int curColor = 0; curColor <= 15; curColor++)
        {
            SysPalette.Colors[curColor].Used = 1;
            if (curColor != 0)
            {
                SysPalette.Colors[curColor].R = ega[curColor].R;
                SysPalette.Colors[curColor].G = ega[curColor].G;
                SysPalette.Colors[curColor].B = ega[curColor].B;
            }
        }

        // colours 16 to 254 are the mixes seen when a finished picture is not dithered
        for (int curColor = 0x10; curColor <= 0xFE; curColor++)
        {
            SysPalette.Colors[curColor].Used = 1;
            int color1 = curColor & 0x0F;
            int color2 = curColor >> 4;
            SysPalette.Colors[curColor].R = BlendColors(SysPalette.Colors[color1].R, SysPalette.Colors[color2].R);
            SysPalette.Colors[curColor].G = BlendColors(SysPalette.Colors[color1].G, SysPalette.Colors[color2].G);
            SysPalette.Colors[curColor].B = BlendColors(SysPalette.Colors[color1].B, SysPalette.Colors[color2].B);
        }

        SysPalette.Timestamp = 1;
        SetOnScreen();
    }

    /// <summary>Merges or copies a palette into the system palette and shows it. Port of GfxPalette::set.</summary>
    public void Set(SciPalette newPalette, bool force, bool forceRealMerge = false, bool includeFirstColor = true)
    {
        uint systime = SysPalette.Timestamp;
        if (force || newPalette.Timestamp != systime)
        {
            // SCI1.1 copies the used colours over; in-between interpreters still merge
            if (forceRealMerge || _useMerging)
            {
                _sysPaletteChanged |= Merge(newPalette, force, forceRealMerge);
            }
            else
            {
                // the first colour is always excluded while pal-vary is active
                includeFirstColor &= _palVaryResourceId == -1;
                _sysPaletteChanged |= Insert(newPalette, SysPalette, includeFirstColor);
            }

            // keep the new palette from being merged again without need
            newPalette.Timestamp = SysPalette.Timestamp;

            bool updatePalette = _sysPaletteChanged && _screen.PicNotValid == 0;
            if (_palVaryResourceId != -1)
            {
                // pal-vary active: never set directly, but also insert into the origin palette
                Insert(newPalette, _palVaryOriginPalette);
                PalVaryProcess(0, updatePalette);
                return;
            }

            if (updatePalette)
            {
                SetOnScreen();
                _sysPaletteChanged = false;
            }
        }
    }

    public bool Insert(SciPalette newPalette, SciPalette destPalette, bool includeFirstColor = false)
    {
        bool paletteChanged = false;
        for (int i = includeFirstColor ? 0 : 1; i < 255; i++)
        {
            if (newPalette.Colors[i].Used != 0)
            {
                if (!newPalette.Colors[i].SameRgb(destPalette.Colors[i]))
                {
                    destPalette.Colors[i].R = newPalette.Colors[i].R;
                    destPalette.Colors[i].G = newPalette.Colors[i].G;
                    destPalette.Colors[i].B = newPalette.Colors[i].B;
                    paletteChanged = true;
                }

                destPalette.Colors[i].Used = newPalette.Colors[i].Used;
                newPalette.Mapping[i] = (byte)i;
            }
        }

        // the timestamp is not updated for SCI1.1; only kDrawPic does that
        return paletteChanged;
    }

    public bool Merge(SciPalette newPalette, bool force, bool forceRealMerge)
    {
        bool paletteChanged = false;
        for (int i = 1; i < 255; i++)
        {
            if (newPalette.Colors[i].Used == 0)
            {
                continue;
            }

            // forced, or the destination colour is still unused
            if (force || SysPalette.Colors[i].Used == 0)
            {
                SysPalette.Colors[i].Used = newPalette.Colors[i].Used;
                if (!newPalette.Colors[i].SameRgb(SysPalette.Colors[i]))
                {
                    SysPalette.Colors[i].R = newPalette.Colors[i].R;
                    SysPalette.Colors[i].G = newPalette.Colors[i].G;
                    SysPalette.Colors[i].B = newPalette.Colors[i].B;
                    paletteChanged = true;
                }

                newPalette.Mapping[i] = (byte)i;
                continue;
            }

            // the same colour at the same position matches directly; LSL1 demo and SQ5 have
            // duplicate colours that a lookup would match to the wrong slot
            if (SysPalette.Colors[i].SameRgb(newPalette.Colors[i]))
            {
                newPalette.Mapping[i] = (byte)i;
                continue;
            }

            ushort res = MatchColor(newPalette.Colors[i].R, newPalette.Colors[i].G, newPalette.Colors[i].B);
            if ((res & MatchPerfect) != 0)
            {
                newPalette.Mapping[i] = (byte)(res & MatchColorMask);
                continue;
            }

            // no exact match: take a free colour
            int j = 1;
            for (; j < 256; j++)
            {
                if (SysPalette.Colors[j].Used == 0)
                {
                    SysPalette.Colors[j].Used = newPalette.Colors[i].Used;
                    SysPalette.Colors[j].R = newPalette.Colors[i].R;
                    SysPalette.Colors[j].G = newPalette.Colors[i].G;
                    SysPalette.Colors[j].B = newPalette.Colors[i].B;
                    newPalette.Mapping[i] = (byte)j;
                    paletteChanged = true;
                    break;
                }
            }

            // still nothing: settle for the approximate colour
            if (j == 256)
            {
                newPalette.Mapping[i] = (byte)(res & MatchColorMask);
                SysPalette.Colors[res & MatchColorMask].Used |= 0x10;
            }
        }

        if (!forceRealMerge)
        {
            SysPalette.Timestamp = MillisecondsNow() * 60 / 1000;
        }

        return paletteChanged;
    }

    /// <summary>Source of the merge timestamps. Defaults to the wall clock; engines set their own time.</summary>
    public Func<uint> MillisecondsNow { get; set; } = () => (uint)Environment.TickCount64;

    /// <summary>Called when kDrawPic is done in SCI1.1: bumps the timestamp and loads the pal-vary target.</summary>
    public void DrewPicture(int pictureId, bool gameIsRestarting)
    {
        if (!_useMerging)
        {
            SysPalette.Timestamp++;
        }

        if (_palVaryResourceId != -1 && !gameIsRestarting)
        {
            PalVaryLoadTargetPalette(pictureId);
        }
    }

    public ushort MatchColor(byte matchRed, byte matchGreen, byte matchBlue, bool force16BitColorMatch = false)
    {
        int differenceTotal = 0;
        int bestDifference = 0x7FFF;
        ushort bestColorNr = 255;

        if (_use16bitColorMatch || force16BitColorMatch)
        {
            // SCI0 to SCI1 and the first SCI1.1 games
            for (int colorNr = 0; colorNr < 256; colorNr++)
            {
                if (SysPalette.Colors[colorNr].Used == 0)
                {
                    continue;
                }

                differenceTotal = Math.Abs(SysPalette.Colors[colorNr].R - matchRed)
                    + Math.Abs(SysPalette.Colors[colorNr].G - matchGreen)
                    + Math.Abs(SysPalette.Colors[colorNr].B - matchBlue);
                if (differenceTotal <= bestDifference)
                {
                    bestDifference = differenceTotal;
                    bestColorNr = (ushort)colorNr;
                }
            }
        }
        else
        {
            // SCI1.1 from QfG3 on has a matching bug that must be kept (SQ5 bug #6455)
            for (int colorNr = 0; colorNr < 256; colorNr++)
            {
                if (SysPalette.Colors[colorNr].Used == 0)
                {
                    continue;
                }

                // the int8 difference is widened before ABS, so -128 stays 128 (C promotion rules)
                differenceTotal = (byte)Math.Abs((int)(sbyte)(SysPalette.Colors[colorNr].R - matchRed))
                    + (byte)Math.Abs((int)(sbyte)(SysPalette.Colors[colorNr].G - matchGreen))
                    + (byte)Math.Abs((int)(sbyte)(SysPalette.Colors[colorNr].B - matchBlue));
                if (differenceTotal <= bestDifference)
                {
                    bestDifference = differenceTotal;
                    bestColorNr = (ushort)colorNr;
                }
            }
        }

        // the original did two merge calls in the worst case; the flag lets the merge skip that
        if (differenceTotal == 0)
        {
            return (ushort)(bestColorNr | MatchPerfect);
        }

        return bestColorNr;
    }

    public void SetOnScreen(bool update = true) => CopySysPaletteToScreen(update);

    public void CopySysPaletteToScreen(bool update)
    {
        _screen.GrabPalette(_bpal, 0, 256);
        for (int i = 0; i < 256; i++)
        {
            if (SysPalette.Colors[i].Used != 0)
            {
                _bpal[i * 3] = (byte)Math.Clamp(SysPalette.Colors[i].R * SysPalette.Intensity[i] / 100, 0, 255);
                _bpal[i * 3 + 1] = (byte)Math.Clamp(SysPalette.Colors[i].G * SysPalette.Intensity[i] / 100, 0, 255);
                _bpal[i * 3 + 2] = (byte)Math.Clamp(SysPalette.Colors[i].B * SysPalette.Intensity[i] / 100, 0, 255);
            }
        }

        _screen.SetPalette(_bpal, 0, 256);
    }

    // ---- kernel functions ----

    public bool KernelSetFromResource(int resourceId, bool force)
    {
        var palResource = _resMan.FindResource(new ResourceId(ResourceType.Palette, (ushort)resourceId));
        if (palResource is null)
        {
            return false;
        }

        var palette = new SciPalette();
        CreateFromData(palResource.Data, palette);
        Set(palette, force);
        return true;
    }

    public void KernelSetFlag(int fromColor, int toColor, byte flag)
    {
        for (int colorNr = fromColor; colorNr < toColor; colorNr++)
        {
            SysPalette.Colors[colorNr].Used |= flag;
        }
    }

    public void KernelUnsetFlag(int fromColor, int toColor, byte flag)
    {
        for (int colorNr = fromColor; colorNr < toColor; colorNr++)
        {
            SysPalette.Colors[colorNr].Used &= (byte)~flag;
        }
    }

    public void KernelSetIntensity(int fromColor, int toColor, int intensity, bool setPalette)
    {
        if (_resMan.ViewType != ViewType.Ega)
        {
            Array.Fill(SysPalette.Intensity, (byte)intensity, fromColor, toColor - fromColor);
        }

        if (setPalette)
        {
            SetOnScreen();
        }
    }

    public int KernelFindColor(int r, int g, int b, bool force16BitColorMatch = false)
        => MatchColor((byte)r, (byte)g, (byte)b, force16BitColorMatch) & MatchColorMask;

    /// <summary>Rotates a colour range on schedule. Returns true when the palette changed. Port of kernelAnimate.</summary>
    public bool KernelAnimate(byte fromColor, byte toColor, int speed, uint now)
    {
        int scheduleNr = _schedules.FindIndex(s => s.From == fromColor);
        if (scheduleNr < 0)
        {
            _schedules.Add((fromColor, now + (uint)Math.Abs(speed)));
            scheduleNr = _schedules.Count - 1;
        }

        var schedule = _schedules[scheduleNr];
        if (schedule.Schedule > now)
        {
            return false;
        }

        if (speed > 0)
        {
            var col = SysPalette.Colors[fromColor];
            if (fromColor < toColor)
            {
                Array.Copy(SysPalette.Colors, fromColor + 1, SysPalette.Colors, fromColor, toColor - fromColor - 1);
            }

            SysPalette.Colors[toColor - 1] = col;
        }
        else
        {
            var col = SysPalette.Colors[toColor - 1];
            if (fromColor < toColor)
            {
                Array.Copy(SysPalette.Colors, fromColor, SysPalette.Colors, fromColor + 1, toColor - fromColor - 1);
            }

            SysPalette.Colors[fromColor] = col;
        }

        _schedules[scheduleNr] = (fromColor, now + (uint)Math.Abs(speed));
        return true;
    }

    public void KernelAnimateSet() => SetOnScreen();

    /// <summary>The 1024 byte copy kPalette(save) hands to the script.</summary>
    public byte[] KernelSave()
    {
        var memory = new byte[1024];
        int pos = 0;
        for (int colorNr = 0; colorNr < 256; colorNr++)
        {
            memory[pos++] = SysPalette.Colors[colorNr].Used;
            memory[pos++] = SysPalette.Colors[colorNr].R;
            memory[pos++] = SysPalette.Colors[colorNr].G;
            memory[pos++] = SysPalette.Colors[colorNr].B;
        }

        return memory;
    }

    public void KernelRestore(byte[] memory)
    {
        var restored = new SciPalette { Timestamp = 0 };
        int pos = 0;
        for (int colorNr = 0; colorNr < 256; colorNr++)
        {
            restored.Colors[colorNr].Used = memory[pos++];
            restored.Colors[colorNr].R = memory[pos++];
            restored.Colors[colorNr].G = memory[pos++];
            restored.Colors[colorNr].B = memory[pos++];
        }

        // restoring excludes the first colour, unlike most system palette operations
        Set(restored, true, false, false);
    }

    public void KernelAssertPalette(GfxView view)
    {
        var viewPalette = view.Palette;
        if (viewPalette is not null)
        {
            Set(viewPalette, true);
        }
    }

    public void KernelSyncScreenPalette()
    {
        _screen.GrabPalette(_bpal, 0, 256);
        for (int i = 1; i < 255; i++)
        {
            SysPalette.Colors[i].R = _bpal[i * 3];
            SysPalette.Colors[i].G = _bpal[i * 3 + 1];
            SysPalette.Colors[i].B = _bpal[i * 3 + 2];
        }
    }

    // ---- pal-vary ----

    private void PalVaryInit()
    {
        _palVaryResourceId = -1;
        _palVaryPaused = 0;
        _palVarySignal = 0;
        _palVaryStep = 0;
        _palVaryStepStop = 0;
        _palVaryDirection = 0;
        _palVaryTicks = 0;
        _palVaryZeroTick = false;
    }

    private bool PalVaryLoadTargetPalette(int resourceId)
    {
        _palVaryResourceId = resourceId != 65535 ? resourceId : -1;
        var palResource = _resMan.FindResource(new ResourceId(ResourceType.Palette, (ushort)resourceId));
        if (palResource is null)
        {
            return false;
        }

        CreateFromData(palResource.Data, _palVaryTargetPalette);
        return true;
    }

    private void PalVaryInstallTimer()
    {
        // Leftover timers are removed first (QFG1 VGA, sleeping at Erana's place, bug #5900)
        PalVaryRemoveTimer();
        _palVaryTimerInstalled = true;
        _palVaryTimerCountdown = Math.Max(1, _palVaryTicks);
    }

    private void PalVaryRemoveTimer() => _palVaryTimerInstalled = false;

    /// <summary>Called sixty times a second by the engine; stands in for ScummVM's timer callback.</summary>
    public void PalVaryTick()
    {
        if (!_palVaryTimerInstalled)
        {
            return;
        }

        if (--_palVaryTimerCountdown <= 0)
        {
            _palVaryTimerCountdown = Math.Max(1, _palVaryTicks);
            if (_palVaryPaused == 0)
            {
                _palVarySignal++;
            }

            _palVaryZeroTick = false;
        }
    }

    public bool KernelPalVaryInit(int resourceId, int ticks, int stepStop, int direction)
    {
        if (_palVaryResourceId != -1)
        {
            return false; // another pal-vary is taking place
        }

        if (!PalVaryLoadTargetPalette(resourceId))
        {
            return false;
        }

        _palVaryOriginPalette.CopyFrom(SysPalette);
        _palVarySignal = 0;
        _palVaryTicks = ticks;
        _palVaryStep = 1;
        _palVaryStepStop = stepStop;
        _palVaryDirection = direction;

        // without ticks, jump straight to the destination
        if (_palVaryTicks == 0)
        {
            _palVaryDirection = stepStop;
        }

        _palVaryZeroTick = _palVaryTicks == 0;
        PalVaryInstallTimer();
        return true;
    }

    public int KernelPalVaryReverse(int ticks, int stepStop, int direction)
    {
        if (_palVaryResourceId == -1)
        {
            return 0;
        }

        if (_palVaryStep > 64)
        {
            _palVaryStep = 64;
        }

        if (ticks != -1)
        {
            _palVaryTicks = ticks;
        }

        _palVaryStepStop = stepStop;
        _palVaryDirection = direction != -1 ? -direction : -_palVaryDirection;

        if (_palVaryTicks == 0)
        {
            _palVaryDirection = _palVaryStepStop - _palVaryStep;
        }

        _palVaryZeroTick = _palVaryTicks == 0;
        PalVaryInstallTimer();
        return KernelPalVaryGetCurrentStep();
    }

    public int KernelPalVaryGetCurrentStep() => _palVaryDirection >= 0 ? _palVaryStep : -_palVaryStep;

    public int KernelPalVaryChangeTarget(int resourceId)
    {
        if (_palVaryResourceId != -1)
        {
            var palResource = _resMan.FindResource(new ResourceId(ResourceType.Palette, (ushort)resourceId));
            if (palResource is not null)
            {
                var insertPalette = new SciPalette();
                CreateFromData(palResource.Data, insertPalette);
                Insert(insertPalette, _palVaryTargetPalette);
                PalVaryProcess(0, true);
            }
        }

        return KernelPalVaryGetCurrentStep();
    }

    public void KernelPalVaryChangeTicks(int ticks)
    {
        _palVaryTicks = ticks;
        if (_palVaryStep - _palVaryStepStop != 0)
        {
            PalVaryRemoveTimer();
            PalVaryInstallTimer();
        }
    }

    /// <summary>Counts pauses, so three pauses need three resumes.</summary>
    public void KernelPalVaryPause(bool pause)
    {
        if (_palVaryResourceId == -1)
        {
            return;
        }

        if (pause)
        {
            _palVaryPaused++;
        }
        else if (_palVaryPaused > 0)
        {
            _palVaryPaused--;
        }
    }

    public void KernelPalVaryDeinit()
    {
        PalVaryRemoveTimer();
        _palVaryResourceId = -1;
    }

    public void PalVaryUpdate()
    {
        if (_palVarySignal != 0)
        {
            PalVaryProcess(_palVarySignal, true);
            _palVarySignal = 0;
        }
    }

    /// <summary>True when kAnimate should wait briefly for a zero-tick pal-vary to trigger (Freddy Pharkas at night, bug #5298).</summary>
    public bool NeedsPalVaryDelay => _palVaryResourceId != -1 && _palVaryPaused == 0 && _palVaryZeroTick;

    public void PalVaryPrepareForTransition()
    {
        if (_palVaryResourceId != -1)
        {
            PalVaryProcess(0, false);
        }
    }

    public void PalVaryProcess(int signal, bool setPalette)
    {
        int stepChange = signal * _palVaryDirection;
        _palVaryStep += stepChange;
        if (stepChange > 0)
        {
            if (_palVaryStep > _palVaryStepStop)
            {
                _palVaryStep = _palVaryStepStop;
            }
        }
        else if (_palVaryStep < _palVaryStepStop && signal != 0)
        {
            _palVaryStep = _palVaryStepStop;
        }

        if (_palVaryStep == _palVaryStepStop)
        {
            PalVaryRemoveTimer();
        }

        if (_palVaryStep == 0)
        {
            _palVaryResourceId = -1;
        }

        for (int colorNr = 0; colorNr < 256; colorNr++)
        {
            SciColor inbetween;
            inbetween.Used = SysPalette.Colors[colorNr].Used;
            int color = _palVaryTargetPalette.Colors[colorNr].R - _palVaryOriginPalette.Colors[colorNr].R;
            inbetween.R = (byte)(color * _palVaryStep / 64 + _palVaryOriginPalette.Colors[colorNr].R);
            color = _palVaryTargetPalette.Colors[colorNr].G - _palVaryOriginPalette.Colors[colorNr].G;
            inbetween.G = (byte)(color * _palVaryStep / 64 + _palVaryOriginPalette.Colors[colorNr].G);
            color = _palVaryTargetPalette.Colors[colorNr].B - _palVaryOriginPalette.Colors[colorNr].B;
            inbetween.B = (byte)(color * _palVaryStep / 64 + _palVaryOriginPalette.Colors[colorNr].B);

            ref var current = ref SysPalette.Colors[colorNr];
            if (inbetween.Used != current.Used || !inbetween.SameRgb(current))
            {
                current = inbetween;
                _sysPaletteChanged = true;
            }
        }

        if (_sysPaletteChanged && setPalette && _screen.PicNotValid == 0)
        {
            SetOnScreen();
            _sysPaletteChanged = false;
        }
    }
}
