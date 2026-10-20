using Daventry.Core.Graphics;
using Daventry.Core.Storage;

namespace Daventry.Agi.Graphics;

[Flags]
public enum GfxScreenMask : byte
{
    Visual = 1,
    Priority = 2,
    All = Visual | Priority,
}

/// <summary>
/// The AGI screens. Port of ScummVM's GfxMgr. There are three layers: the 160x168 game screen
/// and priority screen that pictures and sprites draw into, and the display screen (320x200, or
/// 640x400 when upscaled) that the game screen is rendered onto and the host presents.
/// Font and text drawing, box drawing and transitions are ported with the text subsystem.
/// </summary>
public sealed class GfxMgr
{
    public const int ScriptWidth = 160;
    public const int ScriptHeight = 168;
    public const int VisualWidth = 160;
    public const int VisualHeight = 200;
    public const int DisplayDefaultWidth = 320;
    public const int DisplayDefaultHeight = 200;

    private static readonly byte[] CgaMixtureColorTable =
    [
        0x00, 0x08, 0x04, 0x0C, 0x01, 0x09, 0x02, 0x05,
        0x0A, 0x0D, 0x06, 0x0E, 0x0B, 0x03, 0x07, 0x0F,
    ];

    private static readonly byte[] HerculesColorMapping =
    [
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x88, 0x00, 0x00, 0x00, 0x22, 0x00, 0x00, 0x00,
        0x80, 0x10, 0x02, 0x20, 0x01, 0x08, 0x40, 0x04,
        0xAA, 0x00, 0xAA, 0x00, 0xAA, 0x00, 0xAA, 0x00,
        0x22, 0x88, 0x22, 0x88, 0x22, 0x88, 0x22, 0x88,
        0x88, 0x00, 0x88, 0x00, 0x88, 0x00, 0x88, 0x00,
        0x11, 0x22, 0x44, 0x88, 0x11, 0x22, 0x44, 0x88,
        0x55, 0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55, 0xAA,
        0x22, 0x00, 0x88, 0x00, 0x22, 0x00, 0x88, 0x00,
        0xD7, 0xFF, 0x7D, 0xFF, 0xD7, 0xFF, 0x7D, 0xFF,
        0xDD, 0x55, 0x77, 0xAA, 0xDD, 0x55, 0x77, 0xAA,
        0x7F, 0xEF, 0xFD, 0xDF, 0xFE, 0xF7, 0xBF, 0xFB,
        0xAA, 0xFF, 0xAA, 0xFF, 0xAA, 0xFF, 0xAA, 0xFF,
        0x77, 0xBB, 0xDD, 0xEE, 0x77, 0xBB, 0xDD, 0xEE,
        0x77, 0xFF, 0xFF, 0xFF, 0xDD, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
    ];

    private readonly byte[] _gameScreen = new byte[ScriptWidth * ScriptHeight];
    private readonly byte[] _priorityScreen = new byte[ScriptWidth * ScriptHeight];
    private readonly Rgb[] _paletteGfxMode = new Rgb[256];
    private readonly Rgb[] _paletteTextMode = new Rgb[256];
    private readonly byte[] _priorityTable = new byte[ScriptHeight];
    private readonly ushort _version;
    private readonly AgiGameId _gameId;
    private readonly bool _altAmigaPalette;
    private byte[] _activeScreen;
    private bool _priorityTableSet;
    private int _renderStartVisualOffsetY;
    private int _renderStartDisplayOffsetY;
    private int _displayWidthMulAdjust;
    private int _displayHeightMulAdjust;

    public GfxMgr(AgiRenderMode renderMode, ushort version, AgiGameId gameId, bool altAmigaPalette = false)
    {
        RenderMode = renderMode;
        _version = version;
        _gameId = gameId;
        _altAmigaPalette = altAmigaPalette;
        _activeScreen = _gameScreen;
        Display = new Surface8(DisplayDefaultWidth, DisplayDefaultHeight);
        InitPriorityTable();
    }

    public AgiRenderMode RenderMode { get; }

    /// <summary>The screen the host presents: 320x200, or 640x400 when <see cref="UpscaledHires"/>.</summary>
    public Surface8 Display { get; private set; }

    public bool UpscaledHires { get; private set; }

    public int DisplayScreenWidth => Display.Width;

    public int DisplayScreenHeight => Display.Height;

    public int DisplayFontWidth { get; private set; } = 8;

    public int DisplayFontHeight { get; private set; } = 8;

    /// <summary>True when the graphics mode palette is active rather than the text mode palette.</summary>
    public bool GfxModePaletteActive { get; private set; } = true;

    /// <summary>Increments whenever the palette the host should show changes.</summary>
    public int PaletteVersion { get; private set; }

    public ReadOnlySpan<Rgb> CurrentPalette => GfxModePaletteActive ? _paletteGfxMode : _paletteTextMode;

    /// <summary>Set whenever something was rendered to the display screen; the engine clears it after presenting.</summary>
    public bool DisplayDirty { get; set; }

    public int RenderStartDisplayOffsetY => _renderStartDisplayOffsetY;

    /// <summary>Sets up palettes and the display screen for the render mode. Mirrors GfxMgr::initVideo without the mouse cursors.</summary>
    public void InitVideo(bool hiresFont = false)
    {
        bool forceHires = false;
        AgiPalettes.Init(_paletteTextMode, AgiPalettes.Ega);

        switch (RenderMode)
        {
            case AgiRenderMode.Ega:
                AgiPalettes.Init(_paletteGfxMode, AgiPalettes.Ega);
                break;
            case AgiRenderMode.Cga:
                AgiPalettes.Init(_paletteGfxMode, AgiPalettes.Cga, 4, 8);
                break;
            case AgiRenderMode.Vga:
                AgiPalettes.Init(_paletteGfxMode, AgiPalettes.Vga, 256, 8);
                break;
            case AgiRenderMode.HerculesGreen:
                AgiPalettes.Init(_paletteGfxMode, AgiPalettes.HerculesGreen, 2, 8);
                forceHires = true;
                break;
            case AgiRenderMode.HerculesAmber:
                AgiPalettes.Init(_paletteGfxMode, AgiPalettes.HerculesAmber, 2, 8);
                forceHires = true;
                break;
            case AgiRenderMode.Amiga:
                if (!_altAmigaPalette)
                {
                    // the Amiga palette depends on the interpreter version
                    if (_version < 0x2936)
                    {
                        AgiPalettes.Init(_paletteGfxMode, AgiPalettes.AmigaV1, 16, 4);
                    }
                    else if (_version == 0x2936)
                    {
                        AgiPalettes.Init(_paletteGfxMode, AgiPalettes.AmigaV2, 16, 4);
                    }
                    else
                    {
                        AgiPalettes.Init(_paletteGfxMode, AgiPalettes.AmigaV3, 16, 4);
                    }
                }
                else
                {
                    AgiPalettes.Init(_paletteGfxMode, AgiPalettes.AmigaAlt);
                }

                break;
            case AgiRenderMode.Apple2Gs:
                // Space Quest 1 on the Apple IIgs used the Amiga v1 palette; everything else the standard IIgs palette
                AgiPalettes.Init(_paletteGfxMode, _gameId == AgiGameId.Sq1 ? AgiPalettes.AmigaV1 : AgiPalettes.Apple2Gs, 16, 4);
                break;
            case AgiRenderMode.AtariSt:
                AgiPalettes.Init(_paletteGfxMode, AgiPalettes.AtariSt, 16, 3);
                break;
            case AgiRenderMode.Macintosh:
                switch (_gameId)
                {
                    case AgiGameId.Kq3:
                    case AgiGameId.Pq1:
                    case AgiGameId.Goldrush: // the Gold Rush palette that came with the game is quite ugly, so the KQ3/PQ1 one is used
                        AgiPalettes.InitClut(_paletteGfxMode, AgiPalettes.MacintoshClut);
                        break;
                    default:
                        AgiPalettes.InitClut(_paletteGfxMode, AgiPalettes.MacintoshClut3);
                        break;
                }

                break;
            default:
                throw new NotSupportedException($"initVideo: unsupported render mode {RenderMode}");
        }

        if (hiresFont || forceHires)
        {
            UpscaledHires = true;
            Display = new Surface8(640, 400);
            DisplayFontWidth = 16;
            DisplayFontHeight = 16;
            _displayWidthMulAdjust = 2;
            _displayHeightMulAdjust = 1;
        }
        else
        {
            UpscaledHires = false;
            Display = new Surface8(DisplayDefaultWidth, DisplayDefaultHeight);
            DisplayFontWidth = 8;
            DisplayFontHeight = 8;
            _displayWidthMulAdjust = 0;
            _displayHeightMulAdjust = 0;
        }

        Array.Clear(_gameScreen);
        Array.Clear(_priorityScreen);
        _activeScreen = _gameScreen;
        SetPalette(true);
    }

    public void SetPalette(bool gfxModePalette)
    {
        GfxModePaletteActive = gfxModePalette;
        PaletteVersion++;
    }

    /// <summary>Replaces the first 16 graphics mode colours, for AGIPAL fan games.</summary>
    public void SetGfxModeColors(ReadOnlySpan<Rgb> colors, int start = 0)
    {
        colors.CopyTo(_paletteGfxMode.AsSpan(start));
        PaletteVersion++;
    }

    public void SetRenderStartOffset(int offsetY)
    {
        if (offsetY >= VisualHeight - ScriptHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(offsetY), "invalid render start offset");
        }

        _renderStartVisualOffsetY = offsetY;
        _renderStartDisplayOffsetY = offsetY * (1 + _displayHeightMulAdjust);
    }

    // Game screen to 320x200: x * 2, y + renderStart. To 640x400: x * 4, y * 2 + renderStart.
    public void TranslateGamePosToDisplayScreen(ref int x, ref int y)
    {
        x *= 2 + _displayWidthMulAdjust;
        y = y * (1 + _displayHeightMulAdjust) + _renderStartDisplayOffsetY;
    }

    public void TranslateVisualPosToDisplayScreen(ref int x, ref int y)
    {
        x *= 2 + _displayWidthMulAdjust;
        y *= 1 + _displayHeightMulAdjust;
    }

    public void TranslateDisplayPosToGameScreen(ref int x, ref int y)
    {
        y -= _renderStartDisplayOffsetY; // remove status bar line
        x /= 2 + _displayWidthMulAdjust;
        y /= 1 + _displayHeightMulAdjust;
        if (y < 0)
        {
            y = 0;
        }

        if (y >= ScriptHeight)
        {
            y = ScriptHeight + 1; // 1 beyond
        }
    }

    public void TranslateVisualDimensionToDisplayScreen(ref int width, ref int height)
    {
        width *= 2 + _displayWidthMulAdjust;
        height *= 1 + _displayHeightMulAdjust;
    }

    public void TranslateDisplayDimensionToVisualScreen(ref int width, ref int height)
    {
        width /= 2 + _displayWidthMulAdjust;
        height /= 1 + _displayHeightMulAdjust;
    }

    public void TranslateGameRectToDisplayScreen(ref int x, ref int y, ref int width, ref int height)
    {
        TranslateGamePosToDisplayScreen(ref x, ref y);
        TranslateVisualDimensionToDisplayScreen(ref width, ref height);
    }

    public void TranslateVisualRectToDisplayScreen(ref int x, ref int y, ref int width, ref int height)
    {
        TranslateVisualPosToDisplayScreen(ref x, ref y);
        TranslateVisualDimensionToDisplayScreen(ref width, ref height);
    }

    public int GetDisplayOffsetToGameScreenPos(int x, int y)
    {
        TranslateGamePosToDisplayScreen(ref x, ref y);
        return y * Display.Width + x;
    }

    public int GetDisplayOffsetToVisualScreenPos(int x, int y)
    {
        TranslateVisualPosToDisplayScreen(ref x, ref y);
        return y * Display.Width + x;
    }

    /// <summary>In ScummVM this copies part of the display screen to the system framebuffer. Here the whole display is presented, so it marks it dirty.</summary>
    public void CopyDisplayRectToScreen(int x, int y, int width, int height) => DisplayDirty = true;

    public void CopyDisplayToScreen() => DisplayDirty = true;

    public void UpdateScreen() => DisplayDirty = true;

    /// <summary>Switches rendering between the visual (0) and priority (1) screens, for debugging.</summary>
    public void DebugShowMap(int mapNr)
    {
        _activeScreen = mapNr == 1 ? _priorityScreen : _gameScreen;
        RenderBlock(0, 0, ScriptWidth, ScriptHeight);
    }

    public bool PriorityScreenShown => ReferenceEquals(_activeScreen, _priorityScreen);

    public void Clear(byte color, byte priority)
    {
        Array.Fill(_gameScreen, color);
        Array.Fill(_priorityScreen, priority);
    }

    public void ClearDisplay(byte color, bool copyToScreen = true)
    {
        Display.Fill(color);
        if (copyToScreen)
        {
            CopyDisplayToScreen();
        }
    }

    /// <summary>Puts a pixel on the game and/or priority screens.</summary>
    public void PutPixel(int x, int y, GfxScreenMask drawMask, byte color, byte priority)
    {
        int offset = y * ScriptWidth + x;
        if ((drawMask & GfxScreenMask.Visual) != 0)
        {
            _gameScreen[offset] = color;
        }

        if ((drawMask & GfxScreenMask.Priority) != 0)
        {
            _priorityScreen[offset] = priority;
        }
    }

    /// <summary>Puts a pixel on the display screen, upscaled if upscaling is enabled.</summary>
    public void PutPixelOnDisplay(int x, int y, byte color)
    {
        var pixels = Display.Pixels;
        int width = Display.Width;
        int offset = y * width + x;
        if (!UpscaledHires)
        {
            pixels[offset] = color;
        }
        else
        {
            pixels[offset] = color;
            pixels[offset + 1] = color;
            pixels[offset + width] = color;
            pixels[offset + width + 1] = color;
        }
    }

    public void PutPixelOnDisplay(int x, int adjX, int y, int adjY, byte color)
    {
        if (UpscaledHires)
        {
            adjX *= 2;
            adjY *= 2;
        }

        PutPixelOnDisplay(x + adjX, y + adjY, color);
    }

    /// <summary>Puts a font pixel on the display screen. When upscaled, <paramref name="isHires"/> says whether the font itself is hi-res.</summary>
    public void PutFontPixelOnDisplay(int baseX, int baseY, int addX, int addY, byte color, bool isHires)
    {
        var pixels = Display.Pixels;
        int width = Display.Width;
        if (!UpscaledHires || isHires)
        {
            pixels[(baseY + addY) * width + baseX + addX] = color;
        }
        else
        {
            int offset = (baseY + addY * 2) * width + baseX + addX * 2;
            pixels[offset] = color;
            pixels[offset + 1] = color;
            pixels[offset + width] = color;
            pixels[offset + width + 1] = color;
        }
    }

    public byte GetColor(int x, int y) => _gameScreen[y * ScriptWidth + x];

    public byte GetPriority(int x, int y) => _priorityScreen[y * ScriptWidth + x];

    /// <summary>
    /// Used when a control pixel (priority 0 to 2) is found under a sprite pixel: searches downwards
    /// for a real priority and decides whether the sprite pixel should be drawn.
    /// </summary>
    public bool CheckControlPixel(int x, int y, byte viewPriority)
    {
        int offset = y * ScriptWidth + x;
        byte curPriority;
        while (true)
        {
            y++;
            offset += ScriptWidth;
            if (y >= ScriptHeight)
            {
                return true; // end of screen, nothing but control pixels found: draw
            }

            curPriority = _priorityScreen[offset];
            if (curPriority > 2)
            {
                break;
            }
        }

        return curPriority <= viewPriority;
    }

    public byte GetCgaMixtureColor(byte color) => CgaMixtureColorTable[color & 0x0F];

    /// <summary>
    /// Renders a block of the game screen onto the display screen. The y coordinate is the upper
    /// left corner, unlike Sierra's original which passed the lower left.
    /// </summary>
    public void RenderBlock(int x, int y, int width, int height, bool copyToScreen = true)
    {
        if (!RenderClip(ref x, ref y, ref width, ref height, 0, ScriptWidth, ScriptHeight))
        {
            return;
        }

        switch (RenderMode)
        {
            case AgiRenderMode.HerculesGreen:
            case AgiRenderMode.HerculesAmber:
                RenderBlockHercules(x, y, width, height);
                break;
            case AgiRenderMode.Cga:
                RenderBlockCga(x, y, width, height);
                break;
            default:
                RenderBlockEga(x, y, width, height);
                break;
        }

        if (copyToScreen)
        {
            CopyDisplayRectToScreen(x, y, width, height);
        }
    }

    private static bool RenderClip(ref int x, ref int y, ref int width, ref int height, int minY, int clipAgainstWidth, int clipAgainstHeight)
    {
        if (x >= clipAgainstWidth || x + width - 1 < 0 || y < minY || y + height - 1 >= clipAgainstHeight)
        {
            return false;
        }

        if (x < 0)
        {
            width += x;
            x = 0;
        }

        if (x + width - 1 >= clipAgainstWidth)
        {
            width = clipAgainstWidth - x;
        }

        return true;
    }

    private void RenderBlockEga(int x, int y, int width, int height)
    {
        var display = Display.Pixels;
        int displayScreenWidth = Display.Width;
        int offsetVisual = ScriptWidth * y + x;
        int offsetDisplay = GetDisplayOffsetToGameScreenPos(x, y);
        int displayWidth = width * (2 + _displayWidthMulAdjust);

        for (int row = 0; row < height; row++)
        {
            if (!UpscaledHires)
            {
                for (int col = 0; col < width; col++)
                {
                    byte color = _activeScreen[offsetVisual++];
                    display[offsetDisplay++] = color;
                    display[offsetDisplay++] = color;
                }
            }
            else
            {
                for (int col = 0; col < width; col++)
                {
                    byte color = _activeScreen[offsetVisual++];
                    display.AsSpan(offsetDisplay, 4).Fill(color);
                    display.AsSpan(offsetDisplay + displayScreenWidth, 4).Fill(color);
                    offsetDisplay += 4;
                }
            }

            offsetVisual += ScriptWidth - width;
            offsetDisplay += displayScreenWidth - displayWidth;
            if (UpscaledHires)
            {
                offsetDisplay += displayScreenWidth;
            }
        }
    }

    private void RenderBlockCga(int x, int y, int width, int height)
    {
        var display = Display.Pixels;
        int displayScreenWidth = Display.Width;
        int offsetVisual = ScriptWidth * y + x;
        int offsetDisplay = GetDisplayOffsetToGameScreenPos(x, y);
        int displayWidth = width * (2 + _displayWidthMulAdjust);

        for (int row = 0; row < height; row++)
        {
            if (!UpscaledHires)
            {
                for (int col = 0; col < width; col++)
                {
                    byte color = _activeScreen[offsetVisual++];
                    display[offsetDisplay++] = (byte)(color & 0x03); // CGA mixture colour: two 2-bit pixels
                    display[offsetDisplay++] = (byte)(color >> 2);
                }
            }
            else
            {
                for (int col = 0; col < width; col++)
                {
                    byte color = _activeScreen[offsetVisual++];
                    byte left = (byte)(color & 0x03);
                    byte right = (byte)(color >> 2);
                    display[offsetDisplay] = left;
                    display[offsetDisplay + 1] = right;
                    display[offsetDisplay + 2] = left;
                    display[offsetDisplay + 3] = right;
                    display[offsetDisplay + displayScreenWidth] = left;
                    display[offsetDisplay + displayScreenWidth + 1] = right;
                    display[offsetDisplay + displayScreenWidth + 2] = left;
                    display[offsetDisplay + displayScreenWidth + 3] = right;
                    offsetDisplay += 4;
                }
            }

            offsetVisual += ScriptWidth - width;
            offsetDisplay += displayScreenWidth - displayWidth;
            if (UpscaledHires)
            {
                offsetDisplay += displayScreenWidth;
            }
        }
    }

    /// <summary>Hercules rendering dithers each colour with an 8-row pattern. Sierra seems to have rendered the whole screen every time.</summary>
    private void RenderBlockHercules(int x, int y, int width, int height)
    {
        if (!UpscaledHires)
        {
            throw new InvalidOperationException("Hercules rendering requires the upscaled display.");
        }

        var display = Display.Pixels;
        int displayScreenWidth = Display.Width;
        int offsetVisual = ScriptWidth * y + x;
        int offsetDisplay = GetDisplayOffsetToGameScreenPos(x, y);
        int displayWidth = width * (2 + _displayWidthMulAdjust);
        int lookupOffset1 = (y * 2) & 0x07;

        for (int row = 0; row < height; row++)
        {
            lookupOffset1 &= 0x07;
            int lookupOffset2 = (lookupOffset1 + 1) & 0x07;
            bool getUpperNibble = (x & 1) == 0;

            for (int col = 0; col < width; col++)
            {
                int color = _activeScreen[offsetVisual++] & 0x0F;
                byte herculesColors1;
                byte herculesColors2;
                if (getUpperNibble)
                {
                    herculesColors1 = (byte)(HerculesColorMapping[color * 8 + lookupOffset1] & 0x0F);
                    herculesColors2 = (byte)(HerculesColorMapping[color * 8 + lookupOffset2] & 0x0F);
                }
                else
                {
                    herculesColors1 = (byte)(HerculesColorMapping[color * 8 + lookupOffset1] >> 4);
                    herculesColors2 = (byte)(HerculesColorMapping[color * 8 + lookupOffset2] >> 4);
                }

                getUpperNibble = !getUpperNibble;
                display[offsetDisplay] = (byte)((herculesColors1 & 0x08) != 0 ? 1 : 0);
                display[offsetDisplay + 1] = (byte)((herculesColors1 & 0x04) != 0 ? 1 : 0);
                display[offsetDisplay + 2] = (byte)((herculesColors1 & 0x02) != 0 ? 1 : 0);
                display[offsetDisplay + 3] = (byte)((herculesColors1 & 0x01) != 0 ? 1 : 0);
                display[offsetDisplay + displayScreenWidth] = (byte)((herculesColors2 & 0x08) != 0 ? 1 : 0);
                display[offsetDisplay + displayScreenWidth + 1] = (byte)((herculesColors2 & 0x04) != 0 ? 1 : 0);
                display[offsetDisplay + displayScreenWidth + 2] = (byte)((herculesColors2 & 0x02) != 0 ? 1 : 0);
                display[offsetDisplay + displayScreenWidth + 3] = (byte)((herculesColors2 & 0x01) != 0 ? 1 : 0);
                offsetDisplay += 4;
            }

            lookupOffset1 += 2;
            offsetVisual += ScriptWidth - width;
            offsetDisplay += displayScreenWidth - displayWidth;
            offsetDisplay += displayScreenWidth;
        }
    }

    // ---- priority table ----

    public void InitPriorityTable()
    {
        _priorityTableSet = false;
        CreateDefaultPriorityTable(_priorityTable);
    }

    public static void CreateDefaultPriorityTable(Span<byte> priorityTable)
    {
        int yPos = 0;
        for (int priority = 1; priority < 15; priority++)
        {
            for (int step = 0; step < 12; step++)
            {
                priorityTable[yPos++] = (byte)(priority < 4 ? 4 : priority);
            }
        }
    }

    public void SetPriorityTable(int priorityBase)
    {
        _priorityTableSet = true;
        int x = (ScriptHeight - priorityBase) * ScriptHeight / 10;
        for (int priorityY = 0; priorityY < ScriptHeight; priorityY++)
        {
            int priority = priorityY - priorityBase < 0 ? 4 : (priorityY - priorityBase) * ScriptHeight / x + 5;
            if (priority > 15)
            {
                priority = 15;
            }

            _priorityTable[priorityY] = (byte)priority;
        }
    }

    public bool SaveLoadWasPriorityTableModified => _priorityTableSet;

    public int SaveLoadGetPriority(int yPos) => _priorityTable[yPos];

    public void SaveLoadSetPriority(int yPos, int priority) => _priorityTable[yPos] = (byte)priority;

    public void SaveLoadSetPriorityTableModified(bool wasModified) => _priorityTableSet = wasModified;

    public void SaveLoadFigureOutPriorityTableModified()
    {
        Span<byte> defaultTable = stackalloc byte[ScriptHeight];
        CreateDefaultPriorityTable(defaultTable);
        _priorityTableSet = !defaultTable.SequenceEqual(_priorityTable);
    }

    /// <summary>Converts a sprite priority to a y position.</summary>
    public int PriorityToY(int priority)
    {
        if (!_priorityTableSet)
        {
            // priority table wasn't set by scripts: calculate directly
            return (priority - 5) * 12 + 48;
        }

        // Dynamic priority bands were introduced in 2.425, removed again until 2.936, and buggy
        // through 3.002.086: the original started at y = 168 instead of 167 and so always returned
        // 168. King's Quest 4 2.0 depends on that behaviour (ScummVM bug #3182, dwarf sprite priority).
        if (_version <= 0x3086)
        {
            return 168;
        }

        int currentY = 167;
        while (_priorityTable[currentY] >= priority)
        {
            currentY--;
            if (currentY < 0)
            {
                break; // original AGI didn't check this; we abort and return -1
            }
        }

        return currentY;
    }

    public int PriorityFromY(int yPos) => _priorityTable[yPos];

    // ---- block save/restore and rectangles ----

    /// <summary>Copies the visual then priority pixels of a game screen block into a buffer. y is the upper edge.</summary>
    public void BlockSave(int x, int y, int width, int height, byte[] buffer)
    {
        int bufferPos = 0;
        for (int row = 0; row < height; row++)
        {
            _gameScreen.AsSpan((y + row) * ScriptWidth + x, width).CopyTo(buffer.AsSpan(bufferPos, width));
            bufferPos += width;
        }

        for (int row = 0; row < height; row++)
        {
            _priorityScreen.AsSpan((y + row) * ScriptWidth + x, width).CopyTo(buffer.AsSpan(bufferPos, width));
            bufferPos += width;
        }
    }

    /// <summary>Copies a buffer filled by <see cref="BlockSave"/> back to the game and priority screens.</summary>
    public void BlockRestore(int x, int y, int width, int height, byte[] buffer)
    {
        int bufferPos = 0;
        for (int row = 0; row < height; row++)
        {
            buffer.AsSpan(bufferPos, width).CopyTo(_gameScreen.AsSpan((y + row) * ScriptWidth + x, width));
            bufferPos += width;
        }

        for (int row = 0; row < height; row++)
        {
            buffer.AsSpan(bufferPos, width).CopyTo(_priorityScreen.AsSpan((y + row) * ScriptWidth + x, width));
            bufferPos += width;
        }
    }

    /// <summary>
    /// Draws a bordered box on the display screen, used for message boxes and expanded menus.
    /// Coordinates are visual screen coordinates with the upper left corner, clipped against the
    /// visual screen so boxes may extend outside the game area (KQ4 intro, MMMG nursery rhymes).
    /// </summary>
    public void DrawBox(int x, int y, int width, int height, byte backgroundColor, byte lineColor)
    {
        int minY = 0 - _renderStartDisplayOffsetY;
        if (!RenderClip(ref x, ref y, ref width, ref height, minY, VisualWidth, VisualHeight - _renderStartVisualOffsetY))
        {
            return;
        }

        TranslateVisualRectToDisplayScreen(ref x, ref y, ref width, ref height);
        y += _renderStartDisplayOffsetY;

        DrawDisplayRect(x, y, width, height, backgroundColor);

        switch (RenderMode)
        {
            case AgiRenderMode.Apple2Gs:
            case AgiRenderMode.Amiga:
                // slightly different frame, 1 pixel wide ("hi-res")
                DrawDisplayRect(x, +2, y, +2, width, -4, 0, 1, lineColor);
                DrawDisplayRect(x + width, -3, y, +2, 0, 1, height, -4, lineColor);
                DrawDisplayRect(x, +2, y + height, -3, width, -4, 0, 1, lineColor);
                DrawDisplayRect(x, +2, y, +2, 0, 1, height, -4, lineColor);
                break;
            case AgiRenderMode.Macintosh:
                // 1 pixel between box and frame; frame lines were black
                DrawDisplayRect(x, +1, y, +1, width, -2, 0, 1, 0);
                DrawDisplayRect(x + width, -2, y, +1, 0, 1, height, -2, 0);
                DrawDisplayRect(x, +1, y + height, -2, width, -2, 0, 1, 0);
                DrawDisplayRect(x, +1, y, +1, 0, 1, height, -2, 0);
                break;
            case AgiRenderMode.HerculesAmber:
            case AgiRenderMode.HerculesGreen:
                lineColor = 0;
                goto default;
            default:
                DrawDisplayRect(x, +2, y, +1, width, -4, 0, 1, lineColor);
                DrawDisplayRect(x + width, -4, y, +2, 0, 2, height, -4, lineColor);
                DrawDisplayRect(x, +2, y + height, -2, width, -4, 0, 1, lineColor);
                DrawDisplayRect(x, +2, y, +2, 0, 2, height, -4, lineColor);
                break;
        }
    }

    /// <summary>Fills a rectangle on the display screen, in display coordinates.</summary>
    public void DrawDisplayRect(int x, int y, int width, int height, byte color, bool copyToScreen = true)
    {
        switch (RenderMode)
        {
            case AgiRenderMode.Cga:
                DrawDisplayRectCga(x, y, width, height, color);
                break;
            case AgiRenderMode.HerculesGreen:
            case AgiRenderMode.HerculesAmber:
                if (color != 0)
                {
                    color = 1; // anything but black becomes green/amber
                }

                goto default;
            default:
                DrawDisplayRectEga(x, y, width, height, color);
                break;
        }

        if (copyToScreen)
        {
            CopyDisplayRectToScreen(x, y, width, height);
        }
    }

    /// <summary>Rectangle with adjustments that are doubled when upscaled, for frame lines.</summary>
    public void DrawDisplayRect(int x, int adjX, int y, int adjY, int width, int adjWidth, int height, int adjHeight, byte color, bool copyToScreen = true)
    {
        int factor = UpscaledHires ? 2 : 1;
        DrawDisplayRect(x + adjX * factor, y + adjY * factor, width + adjWidth * factor, height + adjHeight * factor, color, copyToScreen);
    }

    private void DrawDisplayRectEga(int x, int y, int width, int height, byte color)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var clipped = new Rect(x, y, width, height).Intersect(Display.Bounds);
        Display.FillRect(clipped, color);
    }

    private void DrawDisplayRectCga(int x, int y, int width, int height, byte color)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        byte mixture = GetCgaMixtureColor(color);
        byte left = (byte)(mixture & 0x03);
        byte right = (byte)(mixture >> 2);
        var pixels = Display.Pixels;
        int displayWidth = Display.Width;
        for (int row = 0; row < height; row++)
        {
            int rowY = y + row;
            if (rowY < 0 || rowY >= Display.Height)
            {
                continue;
            }

            int offset = rowY * displayWidth + x;
            for (int col = 0; col + 1 < width; col += 2)
            {
                if (x + col >= 0 && x + col + 1 < displayWidth)
                {
                    pixels[offset + col] = left;
                    pixels[offset + col + 1] = right;
                }
            }
        }
    }

    // ---- font and text ----

    /// <summary>The 8x8 font bitmap, 8 bytes per character. Replaceable with a platform font.</summary>
    public byte[] FontData { get; set; } = AgiFonts.PcBios;

    /// <summary>True when the font is 16x16, as the Hercules font is.</summary>
    public bool FontIsHires { get; set; }

    public void TranslateFontPosToDisplayScreen(ref int x, ref int y)
    {
        x *= DisplayFontWidth;
        y *= DisplayFontHeight;
    }

    public void TranslateDisplayPosToFontScreen(ref int x, ref int y)
    {
        x /= DisplayFontWidth;
        y /= DisplayFontHeight;
    }

    public void TranslateFontDimensionToDisplayScreen(ref int width, ref int height)
    {
        width *= DisplayFontWidth;
        height *= DisplayFontHeight;
    }

    public void TranslateFontRectToDisplayScreen(ref int x, ref int y, ref int width, ref int height)
    {
        TranslateFontPosToDisplayScreen(ref x, ref y);
        TranslateFontDimensionToDisplayScreen(ref width, ref height);
    }

    public Rect GetFontRectForDisplayScreen(int column, int row, int width, int height)
        => new(column * DisplayFontWidth, row * DisplayFontHeight, width * DisplayFontWidth, height * DisplayFontHeight);

    /// <summary>Draws a character at a text row and column. Background bit 3 inverts the character in graphics mode.</summary>
    public void DrawCharacter(int row, int column, byte character, byte foreground, byte background, bool disabledLook, bool gfxMode = true)
    {
        int x = column;
        int y = row;
        byte transformXor = 0;
        byte transformOr = 0;
        TranslateFontPosToDisplayScreen(ref x, ref y);

        if (gfxMode)
        {
            if ((background & 0x08) != 0)
            {
                background &= 0x07;
                transformXor = 0xFF;
            }

            if (disabledLook)
            {
                transformOr = 0x55;
            }
        }

        DrawCharacterOnDisplay(x, y, character, foreground, background, transformXor, transformOr);
    }

    /// <summary>Draws a string at display coordinates, for the system UI.</summary>
    public void DrawStringOnDisplay(int x, int y, string text, byte foregroundColor, byte backgroundColor)
    {
        foreach (char c in text)
        {
            DrawCharacterOnDisplay(x, y, (byte)c, foregroundColor, backgroundColor);
            x += DisplayFontWidth;
        }
    }

    public void DrawStringOnDisplay(int x, int adjX, int y, int adjY, string text, byte foregroundColor, byte backgroundColor)
    {
        int factor = UpscaledHires ? 2 : 1;
        DrawStringOnDisplay(x + adjX * factor, y + adjY * factor, text, foregroundColor, backgroundColor);
    }

    public void DrawCharacterOnDisplay(int x, int y, byte character, byte foreground, byte background, byte transformXor = 0, byte transformOr = 0)
    {
        int fontHeight = FontIsHires ? 16 : 8;
        int fontWidth = FontIsHires ? 16 : 8;
        int fontBytesPerCharacter = FontIsHires ? 32 : 8;
        int fontPos = character * fontBytesPerCharacter;
        var font = FontData;

        if (x < 0 || y < 0 || x + DisplayFontWidth > Display.Width || y + DisplayFontHeight > Display.Height)
        {
            return;
        }

        byte curByte = 0;
        int curBit = 0;
        for (int curY = 0; curY < fontHeight; curY++)
        {
            for (int curX = 0; curX < fontWidth; curX++)
            {
                if (curBit == 0)
                {
                    curByte = fontPos < font.Length ? font[fontPos] : (byte)0;
                    curByte ^= transformXor;
                    curByte |= transformOr;
                    fontPos++;
                    curBit = 0x80;
                }

                PutFontPixelOnDisplay(x, y, curX, curY, (curByte & curBit) != 0 ? foreground : background, FontIsHires);
                curBit >>= 1;
            }

            if (transformOr != 0)
            {
                transformOr ^= 0xFF;
            }
        }

        CopyDisplayRectToScreen(x, y, DisplayFontWidth, DisplayFontHeight);
    }

    /// <summary>
    /// Loads an AGIPAL palette file ("pal.N") from the game folder, as the AGIPAL fan hack did via
    /// shake.screen with values 100 to 109. Only the low 6 bits of each component are used, as VGA did.
    /// </summary>
    /// <summary>Number of the pal.NNN file in use, 0 when AGIPAL is not active. Saved with the game.</summary>
    public int AgiPalFileNr { get; private set; }

    public void SetAgiPal(int p0, Core.Storage.IGameContainer game)
    {
        if (p0 == 0)
        {
            return;
        }

        string fileName = $"pal.{p0}";
        if (!game.Exists(fileName))
        {
            return; // Naturette 3 uses AGIPAL but provides no palette files
        }

        var data = game.ReadAllBytes(fileName);
        if (data.Length < 72)
        {
            return;
        }

        // chunk 0 holds colours 0-7 (24 bytes), chunk 1 duplicates it, chunk 2 holds colours 8-15
        var palette = new byte[16 * 3];
        Array.Copy(data, 0, palette, 0, 24);
        Array.Copy(data, 48, palette, 24, 24);
        for (int i = 0; i < palette.Length; i++)
        {
            palette[i] &= 0x3F;
        }

        AgiPalettes.Init(_paletteGfxMode, palette);
        SetPalette(true);
        AgiPalFileNr = p0;
    }
}
