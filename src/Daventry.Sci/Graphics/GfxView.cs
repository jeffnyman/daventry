using System.Buffers.Binary;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Graphics;

/// <summary>One cel of a view: its size, displacement, transparent colour and where its pixels are.</summary>
public sealed class CelInfo
{
    public int ScriptWidth;
    public int ScriptHeight;
    public int Width;
    public int Height;
    public int DisplaceX;
    public int DisplaceY;
    public byte ClearKey;
    public int OffsetEga;
    public int OffsetRle;
    public int OffsetLiteral;
    public byte[]? RawBitmap;

    public CelInfo Clone() => (CelInfo)MemberwiseClone();
}

public sealed class LoopInfo
{
    public bool MirrorFlag;
    public CelInfo[] Cels = [];
}

/// <summary>
/// A view resource: loops of cels in the SCI0, SCI1 or SCI1.1 layout, unpacked on demand, with
/// EGA undithering, mirroring and scaling. Port of ScummVM's view.cpp for DOS data; the
/// Macintosh pixel swapping, the SCI16 colour remapping and the injected "BOTH" buttons are not ported.
/// </summary>
public sealed class GfxView
{
    public const int EgaMappingSize = 16;
    public const int EgaMappingCount = 8;

    private static readonly byte[] EgaMappingStraight = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    private readonly ResourceManager _resMan;
    private readonly GfxScreen _screen;
    private readonly GfxPalette16 _palette;
    private readonly SciResource _resource;
    private readonly SciVersion _version;
    private readonly SciGameId _gameId;
    private readonly bool _isDemo;
    private readonly SciPalette _viewPalette = new();
    private readonly int _adjustForSci0Early;
    private LoopInfo[] _loops = [];
    private bool _embeddedPal;
    private byte[]? _egaMapping;

    public GfxView(ResourceManager resMan, GfxScreen screen, GfxPalette16 palette, int resourceId, SciVersion version, SciGameId gameId, bool isDemo)
    {
        _resMan = resMan;
        _screen = screen;
        _palette = palette;
        _version = version;
        _gameId = gameId;
        _isDemo = isDemo;
        ResourceId = resourceId;
        _resource = resMan.FindResource(ResourceType.View, resourceId)
            ?? throw new FileNotFoundException($"view resource {resourceId} not found");

        // SCI0 early did not add 1 when calculating the bottom in getCelRect
        _adjustForSci0Early = version == SciVersion.V0Early ? -1 : 0;
        InitData();
    }

    public int ResourceId { get; }

    public bool IsScaleable { get; private set; } = true;

    public int LoopCount => _loops.Length;

    public SciPalette? Palette => _embeddedPal ? _viewPalette : null;

    public bool HasEgaMapping => _egaMapping is not null;

    private void InitData()
    {
        var data = _resource.Data!;
        _embeddedPal = false;
        _egaMapping = null;
        IsScaleable = true;

        // An SCI1 or SCI1.1 view in an EGA game switches to that type, so view patches with more
        // colours can be used for EGA games.
        var curViewType = _resMan.ViewType;
        if (curViewType == ViewType.Ega)
        {
            if (data[1] == 0x80)
            {
                curViewType = ViewType.Vga;
            }
            else if (ReadUInt16(data, 4) == 1)
            {
                curViewType = ViewType.Vga11;
            }
        }

        bool isEga = false;
        switch (curViewType)
        {
            case ViewType.Ega: // SCI0 and Amiga 16 colours
                isEga = true;
                goto case ViewType.Vga;
            case ViewType.Amiga: // Amiga ECS, 32 colours
            case ViewType.Amiga64: // Amiga AGA, 64 colours
            case ViewType.Vga: // SCI1
            {
                // LoopCount:BYTE Flags:BYTE MirrorMask:WORD Version:WORD PaletteOffset:WORD LoopOffset0:WORD LoopOffset1:WORD...
                _loops = new LoopInfo[data[0]];
                // bit 0x80 of byte 1 means a palette is set
                bool isCompressed = (data[1] & 0x40) == 0;
                int mirrorBits = ReadUInt16(data, 2);
                int palOffset = ReadUInt16(data, 6);

                if (palOffset != 0 && palOffset != 0x100)
                {
                    // Some SCI0/SCI01 games have an offset set too, pointing at a 16 byte mapping
                    // table that must not be applied. VGA SCI1 views point at a VGA palette and
                    // EGA SCI1 views at an 8x16 mapping table that is applied.
                    if (!isEga)
                    {
                        _palette.CreateFromData(data.AsSpan(palOffset), _viewPalette);
                        _embeddedPal = true;
                    }
                    else if (_version == SciVersion.V1EgaOnly)
                    {
                        // Only SCI1 EGA uses the mapping. SCI1 VGA conversion games (detected as
                        // SCI1 early, middle or late) have views with broken mapping tables.
                        int egaMapNr;
                        for (egaMapNr = 0; egaMapNr < EgaMappingCount; egaMapNr++)
                        {
                            if (!data.AsSpan(palOffset + egaMapNr * EgaMappingSize, EgaMappingSize).SequenceEqual(EgaMappingStraight))
                            {
                                break;
                            }
                        }

                        // all straight mappings mean the mapping is ignored
                        _egaMapping = egaMapNr == EgaMappingCount
                            ? null
                            : data.AsSpan(palOffset, EgaMappingCount * EgaMappingSize).ToArray();
                    }
                }

                for (int loopNo = 0; loopNo < _loops.Length; loopNo++)
                {
                    var loop = _loops[loopNo] = new LoopInfo();
                    int loopOffset = ReadUInt16(data, 8 + loopNo * 2);
                    // CelCount:WORD Unknown:WORD CelOffset0:WORD CelOffset1:WORD...
                    int celCount = ReadUInt16(data, loopOffset);
                    loop.Cels = new CelInfo[celCount];
                    loop.MirrorFlag = (mirrorBits & 1) != 0;
                    mirrorBits >>= 1;

                    for (int celNo = 0; celNo < celCount; celNo++)
                    {
                        int celOffset = ReadUInt16(data, loopOffset + 4 + celNo * 2);

                        // VGA: Width:WORD Height:WORD DisplaceX:BYTE DisplaceY:BYTE ClearKey:BYTE Unknown:BYTE, then RLE data
                        // EGA: Width:WORD Height:WORD DisplaceX:BYTE DisplaceY:BYTE ClearKey:BYTE, then EGA data
                        var cel = loop.Cels[celNo] = new CelInfo();
                        cel.ScriptWidth = cel.Width = ReadUInt16(data, celOffset);
                        cel.ScriptHeight = cel.Height = ReadUInt16(data, celOffset + 2);
                        cel.DisplaceX = (sbyte)data[celOffset + 4];
                        cel.DisplaceY = data[celOffset + 5];
                        cel.ClearKey = data[celOffset + 6];

                        // HACK: ego jumps above the rope in the QFG3 demo, scene 740; his rope
                        // hanging view is displaced down by 40 pixels (bug #5009)
                        if (_gameId == SciGameId.Qfg3 && _isDemo && ResourceId == 39)
                        {
                            cel.DisplaceY = 98;
                        }

                        if (isEga)
                        {
                            cel.OffsetEga = celOffset + 7;
                            cel.OffsetRle = 0;
                            cel.OffsetLiteral = 0;
                        }
                        else
                        {
                            cel.OffsetEga = 0;
                            if (isCompressed)
                            {
                                cel.OffsetRle = celOffset + 8;
                                cel.OffsetLiteral = 0;
                            }
                            else
                            {
                                cel.OffsetRle = 0;
                                cel.OffsetLiteral = celOffset + 8;
                            }
                        }

                        cel.RawBitmap = null;
                        if (loop.MirrorFlag)
                        {
                            cel.DisplaceX = -cel.DisplaceX;
                        }
                    }
                }

                break;
            }

            case ViewType.Vga11: // SCI1.1 and later
            {
                // HeaderSize:WORD LoopCount:BYTE Flags:BYTE Version:WORD Unknown:WORD PaletteOffset:WORD
                int headerSize = ReadUInt16(data, 0) + 2; // headerSize is not part of the header
                if (headerSize < 16)
                {
                    throw new InvalidDataException($"view {ResourceId}: header too small ({headerSize})");
                }

                int loopCount = data[2];
                if (loopCount == 0)
                {
                    throw new InvalidDataException($"view {ResourceId}: no loops");
                }

                int palOffset = (int)ReadUInt32(data, 8);

                // flags is a bit mask only early SCI1.1 games used (maybe only Laura Bow 2):
                // 0 means no flags support (scalable), 1 not scalable, 0x40 scalable
                switch (data[3])
                {
                    case 1:
                        IsScaleable = false;
                        break;
                    case 0x40:
                    case 0:
                        break;
                    default:
                        throw new InvalidDataException($"view {ResourceId}: unsupported flags byte ({data[3]}) inside sci1.1 view");
                }

                int loopSize = data[12];
                int celSize = data[13];
                if (loopSize < 16 || celSize < 32)
                {
                    throw new InvalidDataException($"view {ResourceId}: bad loop or cel header size");
                }

                if (palOffset != 0)
                {
                    _palette.CreateFromData(data.AsSpan(palOffset), _viewPalette);
                    _embeddedPal = true;
                }

                _loops = new LoopInfo[loopCount];
                for (int loopNo = 0; loopNo < loopCount; loopNo++)
                {
                    var loop = _loops[loopNo] = new LoopInfo();
                    int loopData = headerSize + loopNo * loopSize;

                    byte seekEntry = data[loopData];
                    if (seekEntry != 255)
                    {
                        loop.MirrorFlag = true;

                        // use the root loop for mirroring; some loops mirror loops that mirror loops (FPFP view 844, bug #10953)
                        do
                        {
                            if (seekEntry >= loopCount)
                            {
                                throw new InvalidDataException($"view {ResourceId}: bad loop pointer in sci 1.1 view");
                            }

                            loopData = headerSize + seekEntry * loopSize;
                        }
                        while ((seekEntry = data[loopData]) != 255);
                    }
                    else
                    {
                        loop.MirrorFlag = false;
                    }

                    int celCount = data[loopData + 2];
                    loop.Cels = new CelInfo[celCount];
                    int celDataOffset = (int)ReadUInt32(data, loopData + 12);

                    for (int celNo = 0; celNo < celCount; celNo++)
                    {
                        int celData = celDataOffset + celNo * celSize;
                        var cel = loop.Cels[celNo] = new CelInfo();
                        cel.ScriptWidth = cel.Width = ReadInt16(data, celData);
                        cel.ScriptHeight = cel.Height = ReadInt16(data, celData + 2);
                        cel.DisplaceX = ReadInt16(data, celData + 4);
                        cel.DisplaceY = ReadInt16(data, celData + 6);
                        if (cel.DisplaceY < 0)
                        {
                            cel.DisplaceY += 255; // Sierra did this in their SCI1.1 getCelRect
                        }

                        cel.ClearKey = data[celData + 8];
                        cel.OffsetEga = 0;
                        cel.OffsetRle = (int)ReadUInt32(data, celData + 24);
                        cel.OffsetLiteral = (int)ReadUInt32(data, celData + 28);

                        // GK1 hires content is uncompressed; swap so it is processed as such
                        if (cel.OffsetRle != 0 && cel.OffsetLiteral == 0)
                        {
                            (cel.OffsetRle, cel.OffsetLiteral) = (cel.OffsetLiteral, cel.OffsetRle);
                        }

                        cel.RawBitmap = null;
                        if (loop.MirrorFlag)
                        {
                            cel.DisplaceX = -cel.DisplaceX;
                        }
                    }
                }

                break;
            }

            default:
                throw new InvalidDataException("ViewType was not detected, can't continue");
        }
    }

    public int GetWidth(int loopNo, int celNo) => _loops.Length > 0 ? GetCelInfo(loopNo, celNo).Width : 0;

    public int GetHeight(int loopNo, int celNo) => _loops.Length > 0 ? GetCelInfo(loopNo, celNo).Height : 0;

    public CelInfo GetCelInfo(int loopNo, int celNo)
    {
        if (_loops.Length == 0)
        {
            throw new InvalidOperationException($"view {ResourceId} has no loops");
        }

        loopNo = Math.Clamp(loopNo, 0, _loops.Length - 1);
        celNo = Math.Clamp(celNo, 0, _loops[loopNo].Cels.Length - 1);
        return _loops[loopNo].Cels[celNo];
    }

    public int GetCelCount(int loopNo)
    {
        if (_loops.Length == 0)
        {
            throw new InvalidOperationException($"view {ResourceId} has no loops");
        }

        loopNo = Math.Clamp(loopNo, 0, _loops.Length - 1);
        return _loops[loopNo].Cels.Length;
    }

    public bool IsMirrored(int loopNo) => _loops[Math.Clamp(loopNo, 0, _loops.Length - 1)].MirrorFlag;

    public void GetCelRect(int loopNo, int celNo, int x, int y, int z, out SciRect outRect)
    {
        var celInfo = GetCelInfo(loopNo, celNo);
        outRect = default;
        outRect.Left = x + celInfo.DisplaceX - (celInfo.Width >> 1);
        outRect.Right = outRect.Left + celInfo.Width;
        outRect.Bottom = y + celInfo.DisplaceY - z + 1 + _adjustForSci0Early;
        outRect.Top = outRect.Bottom - celInfo.Height;
    }

    public void GetCelSpecialHoyle4Rect(int loopNo, int celNo, int x, int y, int z, ref SciRect outRect)
    {
        var celInfo = GetCelInfo(loopNo, celNo);
        int adjustY = y + celInfo.DisplaceY - celInfo.Height + 1;
        int adjustX = x + celInfo.DisplaceX - ((celInfo.Width - 1) >> 1);
        outRect.Translate(adjustX, adjustY);
    }

    public void GetCelScaledRect(int loopNo, int celNo, int x, int y, int z, int scaleX, int scaleY, out SciRect outRect)
    {
        var celInfo = GetCelInfo(loopNo, celNo);

        int scaledDisplaceX = celInfo.DisplaceX * scaleX / 128;
        int scaledDisplaceY = celInfo.DisplaceY * scaleY / 128;
        int scaledWidth = (celInfo.Width * scaleX) >> 7;
        int scaledHeight = (celInfo.Height * scaleY) >> 7;
        scaledWidth = Math.Clamp(scaledWidth, 0, _screen.Width);
        scaledHeight = Math.Clamp(scaledHeight, 0, _screen.Height);

        outRect = default;
        outRect.Left = x + scaledDisplaceX - (scaledWidth >> 1);
        outRect.Right = outRect.Left + scaledWidth;
        outRect.Bottom = y + scaledDisplaceY - z + 1;
        outRect.Top = outRect.Bottom - scaledHeight;
    }

    /// <summary>
    /// Decodes cel pixels. EGA: each byte is XXXXYYYY, set the next XXXX pixels to YYYY. Amiga:
    /// XXXXXYYY, YYY pixels of XXXXX, or skip XXXXX when YYY is 0. Amiga64: XXYYYYYY likewise.
    /// VGA: XXYYYYYY where 00 copies YYYYYY bytes, 01 copies YYYYYY+64 bytes, 10 fills YYYYYY
    /// pixels with the next byte and 11 skips YYYYYY pixels. A literal position means the RLE
    /// codes and the literal bytes are two separate streams (SCI1.1).
    /// </summary>
    internal static void UnpackCelData(ReadOnlySpan<byte> inBuffer, Span<byte> celBitmap, byte clearColor, int rlePos, int literalPos, ViewType viewType, int width)
    {
        int pixelCount = celBitmap.Length;
        int rlePtr = rlePos;
        int literalPtr = literalPos;
        int pixelNr = 0;

        celBitmap.Fill(clearColor);

        switch (viewType)
        {
            case ViewType.Ega:
                while (pixelNr < pixelCount && rlePtr < inBuffer.Length)
                {
                    byte curByte = inBuffer[rlePtr++];
                    int runLength = curByte >> 4;
                    celBitmap.Slice(pixelNr, Math.Min(runLength, pixelCount - pixelNr)).Fill((byte)(curByte & 0x0F));
                    pixelNr += runLength;
                }

                break;
            case ViewType.Amiga:
                while (pixelNr < pixelCount && rlePtr < inBuffer.Length)
                {
                    byte curByte = inBuffer[rlePtr++];
                    int runLength;
                    if ((curByte & 0x07) != 0)
                    {
                        runLength = curByte & 0x07;
                        celBitmap.Slice(pixelNr, Math.Min(runLength, pixelCount - pixelNr)).Fill((byte)(curByte >> 3));
                    }
                    else
                    {
                        runLength = curByte >> 3;
                    }

                    pixelNr += runLength;
                }

                break;
            case ViewType.Amiga64:
                while (pixelNr < pixelCount && rlePtr < inBuffer.Length)
                {
                    byte curByte = inBuffer[rlePtr++];
                    int runLength;
                    if ((curByte & 0xC0) != 0)
                    {
                        runLength = curByte >> 6;
                        celBitmap.Slice(pixelNr, Math.Min(runLength, pixelCount - pixelNr)).Fill((byte)(curByte & 0x3F));
                    }
                    else
                    {
                        runLength = curByte & 0x3F;
                    }

                    pixelNr += runLength;
                }

                break;
            case ViewType.Vga:
            case ViewType.Vga11:
                // no RLE data means the image is uncompressed
                if (rlePos == 0)
                {
                    CopyClamped(inBuffer, literalPtr, celBitmap, 0, pixelCount);
                    break;
                }

                while (pixelNr < pixelCount && rlePtr < inBuffer.Length)
                {
                    byte curByte = inBuffer[rlePtr++];
                    int runLength = curByte & 0x3F;

                    switch (curByte & 0xC0)
                    {
                        case 0x40: // copy bytes as is; the run can go up to 127 here (bug #5551)
                            runLength += 64;
                            goto case 0x00;
                        case 0x00: // copy bytes as is
                        {
                            int count = Math.Min(runLength, pixelCount - pixelNr);
                            if (literalPos == 0)
                            {
                                CopyClamped(inBuffer, rlePtr, celBitmap, pixelNr, count);
                                rlePtr += runLength;
                            }
                            else
                            {
                                CopyClamped(inBuffer, literalPtr, celBitmap, pixelNr, count);
                                literalPtr += runLength;
                            }

                            break;
                        }

                        case 0x80: // fill with colour
                        {
                            int count = Math.Min(runLength, pixelCount - pixelNr);
                            if (literalPos == 0)
                            {
                                if (rlePtr < inBuffer.Length)
                                {
                                    celBitmap.Slice(pixelNr, count).Fill(inBuffer[rlePtr]);
                                }

                                rlePtr++;
                            }
                            else
                            {
                                if (literalPtr < inBuffer.Length)
                                {
                                    celBitmap.Slice(pixelNr, count).Fill(inBuffer[literalPtr]);
                                }

                                literalPtr++;
                            }

                            break;
                        }

                        default: // 0xC0: skip the next pixels (transparency)
                            break;
                    }

                    pixelNr += runLength;
                }

                break;
            default:
                throw new InvalidDataException("Unsupported picture viewtype");
        }
    }

    /// <summary>Copies what exists of the source run; a run past the end of the resource is left transparent.</summary>
    private static void CopyClamped(ReadOnlySpan<byte> source, int sourcePos, Span<byte> dest, int destPos, int count)
    {
        if (sourcePos >= source.Length || count <= 0)
        {
            return;
        }

        count = Math.Min(count, source.Length - sourcePos);
        source.Slice(sourcePos, count).CopyTo(dest.Slice(destPos, count));
    }

    private void UnpackCel(int loopNo, int celNo, Span<byte> outPtr)
    {
        var celInfo = GetCelInfo(loopNo, celNo);
        if (celInfo.OffsetEga != 0)
        {
            UnpackCelData(_resource.Data, outPtr, 0, celInfo.OffsetEga, 0, _resMan.ViewType, celInfo.Width);
        }
        else
        {
            // the buffer is filled with transparent pixels so skipped pixels are transparent
            UnpackCelData(_resource.Data, outPtr, celInfo.ClearKey, celInfo.OffsetRle, celInfo.OffsetLiteral, _resMan.ViewType, celInfo.Width);
        }
    }

    public byte[] GetBitmap(int loopNo, int celNo)
    {
        loopNo = Math.Clamp(loopNo, 0, _loops.Length - 1);
        celNo = Math.Clamp(celNo, 0, _loops[loopNo].Cels.Length - 1);

        var cel = _loops[loopNo].Cels[celNo];
        if (cel.RawBitmap is not null)
        {
            return cel.RawBitmap;
        }

        int width = cel.Width;
        int height = cel.Height;
        var outBitmap = new byte[width * height];

        UnpackCel(loopNo, celNo, outBitmap);

        if (_resMan.ViewType == ViewType.Ega)
        {
            UnditherBitmap(outBitmap, width, height, cel.ClearKey);
        }

        if (_loops[loopNo].MirrorFlag)
        {
            for (int i = 0; i < height; i++)
            {
                var row = outBitmap.AsSpan(i * width, width);
                row.Reverse();
            }
        }

        cel.RawBitmap = outBitmap;
        return outBitmap;
    }

    /// <summary>
    /// After unpacking an EGA cel, undithers the parts whose dithering matches the dithering of
    /// the current picture.
    /// </summary>
    private void UnditherBitmap(byte[] bitmap, int width, int height, byte clearKey)
    {
        var ditheredPicColors = _screen.UnditherGetDitheredBgColors();
        if (ditheredPicColors is null)
        {
            return;
        }

        // the algorithm needs at least a 4x2 bitmap
        if (width < 4 || height < 2)
        {
            return;
        }

        // no undithering when an EGA mapping is used
        if (_egaMapping is not null)
        {
            return;
        }

        // Count the seemingly dithered pixel combinations: at least 4 adjacent pixels, with the
        // reverse combination in the following line.
        var ditheredBitmapColors = new short[GfxScreen.DitheredBgColorsSize];
        int checkHeight = height - 1;
        int curPtr = 0;
        int nextPtr = width;
        for (int y = 0; y < checkHeight; y++)
        {
            byte color1 = bitmap[curPtr];
            byte color2 = (byte)((bitmap[curPtr + 1] << 4) | bitmap[curPtr + 2]);
            byte nextColor1 = (byte)(bitmap[nextPtr] << 4);
            byte nextColor2 = (byte)((bitmap[nextPtr + 2] << 4) | bitmap[nextPtr + 1]);
            curPtr += 3;
            nextPtr += 3;
            for (int x = 3; x < width; x++)
            {
                color1 = (byte)((color1 << 4) | (color2 >> 4));
                color2 = (byte)((color2 << 4) | bitmap[curPtr++]);
                nextColor1 = (byte)((nextColor1 >> 4) | (nextColor2 << 4));
                nextColor2 = (byte)((nextColor2 >> 4) | (bitmap[nextPtr++] << 4));
                if (color1 == color2 && color1 == nextColor1 && color1 == nextColor2)
                {
                    ditheredBitmapColors[color1]++;
                }
            }
        }

        // compare both tables to find the matching dithered combinations
        var unditherTable = new bool[GfxScreen.DitheredBgColorsSize];
        int unditherCount = 0;
        for (int color = 0; color < 255; color++)
        {
            if (ditheredBitmapColors[color] > 5 && ditheredPicColors[color] > 200)
            {
                // a match, unless the colour key is part of it
                byte color1 = (byte)(color & 0x0F);
                byte color2 = (byte)(color >> 4);
                if (color1 != clearKey && color2 != clearKey && color1 != color2)
                {
                    unditherTable[color] = true;
                    unditherTable[(color1 << 4) | color2] = true;
                    unditherCount++;
                }
            }
        }

        if (unditherCount == 0)
        {
            return;
        }

        // replace the colour combinations
        curPtr = 0;
        for (int y = 0; y < height; y++)
        {
            byte color = bitmap[curPtr];
            for (int x = 1; x < width; x++)
            {
                color = (byte)((color << 4) | bitmap[curPtr + 1]);
                if (unditherTable[color])
                {
                    // a colour with black is turned around, otherwise it would not be the right colour
                    byte unditheredColor = color;
                    if ((color & 0xF0) == 0)
                    {
                        unditheredColor = (byte)((color << 4) | (color >> 4));
                    }

                    bitmap[curPtr] = unditheredColor;
                    bitmap[curPtr + 1] = unditheredColor;
                }

                curPtr++;
            }

            curPtr++;
        }
    }

    public void Draw(SciRect rect, SciRect clipRect, SciRect clipRectTranslated, int loopNo, int celNo, byte priority, int egaMappingNr)
    {
        var palette = _embeddedPal ? _viewPalette : _palette.SysPalette;
        var celInfo = GetCelInfo(loopNo, celNo);
        var bitmap = GetBitmap(loopNo, celNo);
        int celHeight = celInfo.Height;
        int celWidth = celInfo.Width;
        byte clearKey = celInfo.ClearKey;
        var drawMask = priority > 15 ? GfxScreenMask.Visual : GfxScreenMask.Visual | GfxScreenMask.Priority;

        if (_embeddedPal)
        {
            // merge the view palette in
            _palette.Set(_viewPalette, false);
        }

        int width = Math.Min(clipRect.Width, celWidth);
        int height = Math.Min(clipRect.Height, celHeight);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        int bitmapData = (clipRect.Top - rect.Top) * celWidth + (clipRect.Left - rect.Left);

        if (_egaMapping is not null)
        {
            int egaMapping = egaMappingNr * EgaMappingSize;
            for (int y = 0; y < height; y++, bitmapData += celWidth)
            {
                for (int x = 0; x < width; x++)
                {
                    byte color = _egaMapping[egaMapping + bitmap[bitmapData + x]];
                    int x2 = clipRectTranslated.Left + x;
                    int y2 = clipRectTranslated.Top + y;
                    if (color != clearKey && priority >= _screen.GetPriority(x2, y2))
                    {
                        _screen.PutPixel(x2, y2, drawMask, color, priority, 0);
                    }
                }
            }
        }
        else
        {
            for (int y = 0; y < height; y++, bitmapData += celWidth)
            {
                for (int x = 0; x < width; x++)
                {
                    byte color = bitmap[bitmapData + x];
                    if (color != clearKey)
                    {
                        int x2 = clipRectTranslated.Left + x;
                        int y2 = clipRectTranslated.Top + y;
                        if (priority >= _screen.GetPriority(x2, y2))
                        {
                            _screen.PutPixel(x2, y2, drawMask, palette.Mapping[color], priority, 0);
                        }
                    }
                }
            }
        }
    }

    public void DrawScaled(SciRect rect, SciRect clipRect, SciRect clipRectTranslated, int loopNo, int celNo, byte priority, int scaleX, int scaleY)
    {
        var palette = _embeddedPal ? _viewPalette : _palette.SysPalette;
        var celInfo = GetCelInfo(loopNo, celNo);
        var bitmap = GetBitmap(loopNo, celNo);
        int celHeight = celInfo.Height;
        int celWidth = celInfo.Width;
        byte clearKey = celInfo.ClearKey;
        var drawMask = priority > 15 ? GfxScreenMask.Visual : GfxScreenMask.Visual | GfxScreenMask.Priority;

        if (_embeddedPal)
        {
            _palette.Set(_viewPalette, false);
        }

        bool mirrorFlag = _loops[Math.Clamp(loopNo, 0, _loops.Length - 1)].MirrorFlag;
        var scalingX = CreateScalingTable(celWidth, _screen.Width, scaleX, mirrorFlag);
        if (mirrorFlag)
        {
            // reverse the table when mirroring; the bitmap is already reversed
            Array.Reverse(scalingX);
        }

        var scalingY = CreateScalingTable(celHeight, _screen.Height, scaleY, false);

        int scaledWidth = Math.Min(clipRect.Width, scalingX.Length);
        int scaledHeight = Math.Min(clipRect.Height, scalingY.Length);

        int offsetY = clipRect.Top - rect.Top;
        int offsetX = clipRect.Left - rect.Left;

        for (int y = 0; y < scaledHeight; y++)
        {
            for (int x = 0; x < scaledWidth; x++)
            {
                byte color = bitmap[scalingY[y + offsetY] * celWidth + scalingX[x + offsetX]];
                int x2 = clipRectTranslated.Left + x;
                int y2 = clipRectTranslated.Top + y;
                if (color != clearKey && priority >= _screen.GetPriority(x2, y2))
                {
                    _screen.PutPixel(x2, y2, drawMask, palette.Mapping[color], priority, 0);
                }
            }
        }
    }

    public static ushort[] CreateScalingTable(int celSize, int maxSize, int scale, bool mirrorFlag)
    {
        int scaledSize = (short)((celSize * scale) >> 7);
        int clippedScaledSize = Math.Clamp(scaledSize, 0, maxSize);
        int stepCount = scaledSize - 1;

        if (clippedScaledSize <= 0)
        {
            return [];
        }

        // the table produced when mirroring differs slightly from the reversed normal table
        int start = mirrorFlag ? celSize - 1 : 0;
        int end = mirrorFlag ? 0 : celSize - 1;

        int acc;
        int inc;
        bool negative = false;
        if (stepCount == 0)
        {
            acc = start << 16;
            inc = 0;
        }
        else
        {
            acc = start << 16;
            inc = end << 16;
            inc -= acc;
            inc /= stepCount;
            if (inc < 0)
            {
                inc = -inc;
                negative = true;
            }

            if ((inc & unchecked((int)0xFFFF8000)) == 0)
            {
                acc = (int)((acc & 0xFFFF0000) | 0x8000);
            }
            else
            {
                acc = (int)((acc & 0xFFFF0000) | (uint)(inc & 0xFFFF));
            }
        }

        if (negative)
        {
            inc = -inc;
        }

        var table = new ushort[clippedScaledSize];
        for (int i = 0; i < clippedScaledSize; i++)
        {
            table[i] = (ushort)(acc >> 16);
            acc += inc;
        }

        return table;
    }

    private static ushort ReadUInt16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));

    private static short ReadInt16(byte[] data, int offset) => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset));

    private static uint ReadUInt32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
}
