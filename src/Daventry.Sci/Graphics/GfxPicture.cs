using System.Buffers.Binary;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Graphics;

/// <summary>
/// Draws one picture resource: SCI1.1 pictures with an embedded cel, and the vector format of
/// SCI0 to SCI1 with lines, patterns, flood fills and embedded views. Port of ScummVM's
/// picture.cpp. The format is decided per picture from its data, never from the game version,
/// because games like PQ1 VGA are SCI1.1 but use the older vector format.
/// </summary>
public sealed class GfxPicture
{
    private const int EgaPaletteCount = 4;
    private const int EgaPaletteSize = 40;
    private const int EgaPaletteTotalSize = EgaPaletteCount * EgaPaletteSize;
    private const int EgaPrioritySize = EgaPaletteSize;

    private const byte PatternCodeRectangle = 0x10;
    private const byte PatternCodeUseTexture = 0x20;
    private const byte PatternCodePenSize = 0x07;

    private enum PictureType
    {
        Regular,
        Sci11,
    }

    private enum PicOp : byte
    {
        SetColor = 0xF0,
        DisableVisual = 0xF1,
        SetPriority = 0xF2,
        DisablePriority = 0xF3,
        ShortPatterns = 0xF4,
        MediumLines = 0xF5,
        LongLines = 0xF6,
        ShortLines = 0xF7,
        Fill = 0xF8,
        SetPattern = 0xF9,
        AbsolutePattern = 0xFA,
        SetControl = 0xFB,
        DisableControl = 0xFC,
        MediumPatterns = 0xFD,
        Opx = 0xFE,
        Terminate = 0xFF,
    }

    private const byte PicOpFirst = (byte)PicOp.SetColor;

    // extended opcodes, EGA
    private const byte OpxEgaSetPaletteEntries = 0;
    private const byte OpxEgaSetPalette = 1;
    private const byte OpxEgaMono0 = 2;
    private const byte OpxEgaMono1 = 3;
    private const byte OpxEgaMono2 = 4;
    private const byte OpxEgaMono3 = 5;
    private const byte OpxEgaMono4 = 6;
    private const byte OpxEgaEmbeddedView = 7;
    private const byte OpxEgaSetPriorityTable = 8;

    // extended opcodes, VGA
    private const byte OpxVgaSetPaletteEntries = 0;
    private const byte OpxVgaEmbeddedView = 1;
    private const byte OpxVgaSetPalette = 2;
    private const byte OpxVgaPriorityTableEqDist = 3;
    private const byte OpxVgaPriorityTableExplicit = 4;

    private static readonly byte[] DefaultEgaPalette =
    [
        0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77,
        0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0x88,
        0x88, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x88,
        0x88, 0xf9, 0xfa, 0xfb, 0xfc, 0xfd, 0xfe, 0xff,
        0x08, 0x19, 0x2a, 0x3b, 0x4c, 0x5d, 0x6e, 0x88,
    ];

    private static readonly byte[] DefaultEgaPriority =
    [
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
        0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f,
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
        0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f,
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
    ];

    private readonly ResourceManager _resMan;
    private readonly GfxPorts _ports;
    private readonly GfxScreen _screen;
    private readonly GfxPalette16 _palette;
    private readonly SciResource _resource;
    private readonly SciVersion _version;
    private readonly SciGameId _gameId;

    private PictureType _resourceType = PictureType.Regular;
    private bool _mirroredFlag;
    private bool _addToFlag;
    private int _egaPaletteNo;
    private byte _priority;

    public GfxPicture(ResourceManager resMan, GfxPorts ports, GfxScreen screen, GfxPalette16 palette, int resourceId, SciVersion version, SciGameId gameId)
    {
        _resMan = resMan;
        _ports = ports;
        _screen = screen;
        _palette = palette;
        _version = version;
        _gameId = gameId;
        ResourceId = resourceId;
        _resource = resMan.FindResource(ResourceType.Pic, resourceId)
            ?? throw new FileNotFoundException($"picture resource {resourceId} not found");
    }

    public int ResourceId { get; }

    public void Draw(bool mirroredFlag, bool addToFlag, int egaPaletteNo)
    {
        _mirroredFlag = mirroredFlag;
        _addToFlag = addToFlag;
        _egaPaletteNo = egaPaletteNo;
        _priority = 0;

        ushort headerSize = _resource.GetUInt16LEAt(0);
        switch (headerSize)
        {
            case 0x26: // SCI1.1 VGA picture
                _resourceType = PictureType.Sci11;
                DrawSci11Vga();
                break;
            default:
                // VGA, EGA or Amiga vector data
                _resourceType = PictureType.Regular;
                DrawVectorData(_resource.Data);
                break;
        }
    }

    private void DrawSci11Vga()
    {
        ReadOnlySpan<byte> inbuffer = _resource.Data;
        int priorityBandCount = inbuffer[3];
        int hasCel = inbuffer[4];
        int vectorDataPos = (int)ReadUInt32(inbuffer, 16);
        int vectorSize = inbuffer.Length - vectorDataPos;
        int paletteDataPtr = (int)ReadUInt32(inbuffer, 28);
        int celHeaderPos = (int)ReadUInt32(inbuffer, 32);
        // the cel header is only present with a cel; KQ6 CD picture 155 has none and ends before it
        int celRlePos = hasCel != 0 ? (int)ReadUInt32(inbuffer, celHeaderPos + 24) : 0;
        int celLiteralPos = hasCel != 0 ? (int)ReadUInt32(inbuffer, celHeaderPos + 28) : 0;

        // Header
        // [headerSize:WORD] [unknown:BYTE] [priorityBandCount:BYTE] [hasCel:BYTE] [unknown:BYTE]
        // [unknown:WORD] x5
        // Offset 16: [vectorDataOffset:DWORD] [unknown:DWORD] [unknown:DWORD] [paletteDataOffset:DWORD]
        // Offset 32: [celHeaderOffset:DWORD] [unknown:DWORD]
        // [priorityBandData:WORD] * priorityBandCount
        // [priority:BYTE] [unknown:BYTE]

        if (priorityBandCount != 14)
        {
            throw new InvalidDataException($"invalid priorityBandCount for sci1.1 pic: {priorityBandCount}");
        }

        if (_addToFlag)
        {
            _priority = (byte)(inbuffer[40 + priorityBandCount * 2] & 0xF);
        }

        if (hasCel != 0)
        {
            var palette = new SciPalette();
            _palette.CreateFromData(inbuffer[paletteDataPtr..], palette);
            _palette.Set(palette, true);

            DrawCelData(inbuffer, celHeaderPos, celRlePos, celLiteralPos, 0, 0, 0, 0, false);
        }

        DrawVectorData(inbuffer.Slice(vectorDataPos, vectorSize));

        _ports.PriorityBandsInitSci11(inbuffer[40..]);
    }

    private void DrawCelData(ReadOnlySpan<byte> inbuffer, int headerPos, int rlePos, int literalPos, int drawX, int drawY, int pictureX, int pictureY, bool isEga)
    {
        // if the picture is not an overlay and we are not in EGA mode, use priority 0
        byte priority = !isEga && !_addToFlag ? (byte)0 : _priority;

        int width = ReadUInt16(inbuffer, headerPos);
        int height = ReadUInt16(inbuffer, headerPos + 2);

        // displaceX and displaceY are ignored; they may hold garbage (Dr. Brain 1 Spanish pic 261, bug #6388)

        byte clearColor = _resourceType == PictureType.Sci11
            ? _screen.ColorWhite // SCI1.1 hardcodes the clear colour for pictures
            : inbuffer[headerPos + 6];

        // unpack into a temporary buffer, then plot, because the picture may be mirrored
        var celBitmap = new byte[width * height];
        GfxView.UnpackCelData(inbuffer, celBitmap, clearColor, rlePos, literalPos, _resMan.ViewType, width);

        var displayArea = _ports.PictureGetDisplayArea();

        // horizontal clipping
        int skipCelBitmapPixels = 0;
        int displayWidth = width;
        if (pictureX != 0)
        {
            // horizontal scroll position active: adjust drawX
            drawX -= pictureX;
            if (drawX < 0)
            {
                skipCelBitmapPixels = -drawX;
                displayWidth -= skipCelBitmapPixels;
                drawX = 0;
            }
        }

        // vertical clipping is not finished in ScummVM either
        int skipCelBitmapLines = 0;
        int displayHeight = height;
        _ = pictureY;

        if (displayWidth > 0 && displayHeight > 0)
        {
            int y = displayArea.Top + drawY;
            int lastY = Math.Min(height + y, displayArea.Bottom);
            int leftX = displayArea.Left + drawX;
            int rightX = Math.Min(displayWidth + leftX, displayArea.Right);

            int sourcePixelSkipPerRow = 0;
            if (width > rightX - leftX)
            {
                sourcePixelSkipPerRow = width - (rightX - leftX);
            }

            // Not adding to a picture: the clear colour becomes white, so everything but white is
            // painted. Sierra painted all the data and never skipped transparent pixels; this
            // works the same because the screen is already white.
            if (!_addToFlag)
            {
                clearColor = _screen.ColorWhite;
            }

            var drawMask = priority > 15 ? GfxScreenMask.Visual : GfxScreenMask.Visual | GfxScreenMask.Priority;

            int ptr = skipCelBitmapPixels + skipCelBitmapLines * width;

            if (!isEga || priority < 16)
            {
                // VGA and EGA; EGA only checks priority when the given priority is below 16
                if (!_mirroredFlag)
                {
                    int x = leftX;
                    while (y < lastY && ptr < celBitmap.Length)
                    {
                        byte curByte = celBitmap[ptr++];
                        if (curByte != clearColor && priority >= _screen.GetPriority(x, y))
                        {
                            _screen.PutPixel(x, y, drawMask, curByte, priority, 0);
                        }

                        x++;
                        if (x >= rightX)
                        {
                            ptr += sourcePixelSkipPerRow;
                            x = leftX;
                            y++;
                        }
                    }
                }
                else
                {
                    int x = rightX - 1;
                    while (y < lastY && ptr < celBitmap.Length)
                    {
                        byte curByte = celBitmap[ptr++];
                        if (curByte != clearColor && priority >= _screen.GetPriority(x, y))
                        {
                            _screen.PutPixel(x, y, drawMask, curByte, priority, 0);
                        }

                        if (x == leftX)
                        {
                            ptr += sourcePixelSkipPerRow;
                            x = rightX;
                            y++;
                        }

                        x--;
                    }
                }
            }
            else
            {
                // EGA with priority above 15: neither check nor set priority (KQ5 picture 48, bug #5182)
                if (!_mirroredFlag)
                {
                    int x = leftX;
                    while (y < lastY && ptr < celBitmap.Length)
                    {
                        byte curByte = celBitmap[ptr++];
                        if (curByte != clearColor)
                        {
                            _screen.PutPixel(x, y, GfxScreenMask.Visual, curByte, 0, 0);
                        }

                        x++;
                        if (x >= rightX)
                        {
                            ptr += sourcePixelSkipPerRow;
                            x = leftX;
                            y++;
                        }
                    }
                }
                else
                {
                    int x = rightX - 1;
                    while (y < lastY && ptr < celBitmap.Length)
                    {
                        byte curByte = celBitmap[ptr++];
                        if (curByte != clearColor)
                        {
                            _screen.PutPixel(x, y, GfxScreenMask.Visual, curByte, 0, 0);
                        }

                        if (x == leftX)
                        {
                            ptr += sourcePixelSkipPerRow;
                            x = rightX;
                            y++;
                        }

                        x--;
                    }
                }
            }
        }
    }

    private void DrawVectorData(ReadOnlySpan<byte> data)
    {
        byte picColor = _screen.ColorDefaultVectorData;
        byte picPriority = 255;
        byte picControl = 255;
        int x = 0;
        int y = 0;
        var egaPalettes = new byte[EgaPaletteTotalSize];
        var egaPriority = new byte[EgaPrioritySize];
        bool isEga = false;
        int curPos = 0;
        var palette = new SciPalette();
        byte patternCode = 0;
        byte patternTexture = 0;
        bool icemanDrawFix = false;

        if (_egaPaletteNo >= EgaPaletteCount)
        {
            _egaPaletteNo = 0;
        }

        int egaPalette = _egaPaletteNo * EgaPaletteSize;

        if (_resMan.ViewType == ViewType.Ega)
        {
            isEga = true;
            for (int i = 0; i < EgaPaletteTotalSize; i += EgaPaletteSize)
            {
                Array.Copy(DefaultEgaPalette, 0, egaPalettes, i, EgaPaletteSize);
            }

            Array.Copy(DefaultEgaPriority, egaPriority, EgaPrioritySize);

            if (_gameId == SciGameId.Iceman)
            {
                // WORKAROUND: certain visual and priority lines in Iceman's underwater rooms are
                // removed when not dithering. They share the colour of the dithered fill, so they
                // are invisible when dithered and distracting otherwise.
                if (_screen.UnditheringEnabled && (ResourceId is >= 53 and <= 58 || ResourceId == 61))
                {
                    icemanDrawFix = true;
                }
            }
        }

        while (curPos < data.Length)
        {
            byte picOp = data[curPos++];
            switch ((PicOp)picOp)
            {
                case PicOp.SetColor:
                    picColor = data[curPos++];
                    if (isEga)
                    {
                        picColor = egaPalettes[egaPalette + picColor];
                        picColor ^= (byte)(picColor << 4);
                    }

                    break;
                case PicOp.DisableVisual:
                    picColor = 0xFF;
                    break;

                case PicOp.SetPriority:
                    picPriority = (byte)(data[curPos++] & 0x0F);
                    if (isEga)
                    {
                        picPriority = egaPriority[picPriority];
                    }

                    break;
                case PicOp.DisablePriority:
                    picPriority = 255;
                    break;

                case PicOp.SetControl:
                    picControl = (byte)(data[curPos++] & 0x0F);
                    break;
                case PicOp.DisableControl:
                    picControl = 255;
                    break;

                case PicOp.ShortLines:
                    VectorGetAbsCoords(data, ref curPos, ref x, ref y);
                    while (VectorIsNonOpcode(data, curPos))
                    {
                        int oldx = x;
                        int oldy = y;
                        VectorGetRelCoords(data, ref curPos, ref x, ref y);
                        var startPoint = new SciPoint(oldx, oldy);
                        var endPoint = new SciPoint(x, y);
                        _ports.OffsetLine(ref startPoint, ref endPoint);
                        _screen.DrawLine(startPoint, endPoint, picColor, picPriority, picControl);
                    }

                    break;
                case PicOp.MediumLines:
                    VectorGetAbsCoords(data, ref curPos, ref x, ref y);
                    if (icemanDrawFix)
                    {
                        // WORKAROUND: remove certain lines in Iceman, see above
                        if (picColor == 1 && picPriority == 14)
                        {
                            if (y < 100 || (y & 1) == 0)
                            {
                                picColor = 255;
                                picPriority = 255;
                            }
                        }
                    }

                    while (VectorIsNonOpcode(data, curPos))
                    {
                        int oldx = x;
                        int oldy = y;
                        VectorGetRelCoordsMed(data, ref curPos, ref x, ref y);
                        var startPoint = new SciPoint(oldx, oldy);
                        var endPoint = new SciPoint(x, y);
                        _ports.OffsetLine(ref startPoint, ref endPoint);
                        _screen.DrawLine(startPoint, endPoint, picColor, picPriority, picControl);
                    }

                    break;
                case PicOp.LongLines:
                    VectorGetAbsCoords(data, ref curPos, ref x, ref y);
                    while (VectorIsNonOpcode(data, curPos))
                    {
                        int oldx = x;
                        int oldy = y;
                        VectorGetAbsCoords(data, ref curPos, ref x, ref y);
                        var startPoint = new SciPoint(oldx, oldy);
                        var endPoint = new SciPoint(x, y);
                        _ports.OffsetLine(ref startPoint, ref endPoint);
                        _screen.DrawLine(startPoint, endPoint, picColor, picPriority, picControl);
                    }

                    break;

                case PicOp.Fill:
                    while (VectorIsNonOpcode(data, curPos))
                    {
                        VectorGetAbsCoords(data, ref curPos, ref x, ref y);
                        VectorFloodFill(x, y, picColor, picPriority, picControl, isEga);
                    }

                    break;

                // Pattern opcodes are NOPs in Sierra SCI1.1 and later and should not occur there
                case PicOp.SetPattern:
                    if (_resourceType >= PictureType.Sci11)
                    {
                        if (_gameId == SciGameId.Sq4)
                        {
                            // WORKAROUND: some SQ4 pictures carry garbage after the terminator,
                            // left over from the floppy (SCI1) to CD (SCI1.1) conversion
                            switch (ResourceId)
                            {
                                case 35:
                                case 381:
                                case 376:
                                case 390: // NRS SQ4 update patch
                                    return;
                            }
                        }

                        throw new InvalidDataException("pic-operation set pattern inside sci1.1+ vector data");
                    }

                    patternCode = data[curPos++];
                    break;
                case PicOp.ShortPatterns:
                    if (_resourceType >= PictureType.Sci11)
                    {
                        throw new InvalidDataException("pic-operation short pattern inside sci1.1+ vector data");
                    }

                    VectorGetPatternTexture(data, ref curPos, patternCode, ref patternTexture);
                    VectorGetAbsCoords(data, ref curPos, ref x, ref y);
                    VectorPattern(x, y, picColor, picPriority, picControl, patternCode, patternTexture);
                    while (VectorIsNonOpcode(data, curPos))
                    {
                        VectorGetPatternTexture(data, ref curPos, patternCode, ref patternTexture);
                        VectorGetRelCoords(data, ref curPos, ref x, ref y);
                        VectorPattern(x, y, picColor, picPriority, picControl, patternCode, patternTexture);
                    }

                    break;
                case PicOp.MediumPatterns:
                    if (_resourceType >= PictureType.Sci11)
                    {
                        throw new InvalidDataException("pic-operation medium pattern inside sci1.1+ vector data");
                    }

                    VectorGetPatternTexture(data, ref curPos, patternCode, ref patternTexture);
                    VectorGetAbsCoords(data, ref curPos, ref x, ref y);
                    VectorPattern(x, y, picColor, picPriority, picControl, patternCode, patternTexture);
                    while (VectorIsNonOpcode(data, curPos))
                    {
                        VectorGetPatternTexture(data, ref curPos, patternCode, ref patternTexture);
                        VectorGetRelCoordsMed(data, ref curPos, ref x, ref y);
                        VectorPattern(x, y, picColor, picPriority, picControl, patternCode, patternTexture);
                    }

                    break;
                case PicOp.AbsolutePattern:
                    if (_resourceType >= PictureType.Sci11)
                    {
                        throw new InvalidDataException("pic-operation absolute pattern inside sci1.1+ vector data");
                    }

                    while (VectorIsNonOpcode(data, curPos))
                    {
                        VectorGetPatternTexture(data, ref curPos, patternCode, ref patternTexture);
                        VectorGetAbsCoords(data, ref curPos, ref x, ref y);
                        VectorPattern(x, y, picColor, picPriority, picControl, patternCode, patternTexture);
                    }

                    break;

                case PicOp.Opx:
                    if (isEga)
                    {
                        switch (picOp = data[curPos++])
                        {
                            case OpxEgaSetPaletteEntries:
                                while (VectorIsNonOpcode(data, curPos))
                                {
                                    byte pixel = data[curPos++];
                                    if (pixel >= EgaPaletteTotalSize)
                                    {
                                        throw new InvalidDataException("picture trying to write to invalid EGA-palette");
                                    }

                                    egaPalettes[pixel] = data[curPos++];
                                }

                                break;
                            case OpxEgaSetPalette:
                            {
                                byte pixel = data[curPos++];
                                if (pixel >= EgaPaletteCount)
                                {
                                    throw new InvalidDataException($"picture trying to write to invalid palette {pixel}");
                                }

                                int offset = pixel * EgaPaletteSize;
                                for (int i = 0; i < EgaPaletteSize; i++)
                                {
                                    egaPalettes[offset + i] = data[curPos++];
                                }

                                break;
                            }

                            case OpxEgaMono0:
                                curPos += 41;
                                break;
                            case OpxEgaMono1:
                            case OpxEgaMono3:
                                curPos++;
                                break;
                            case OpxEgaMono2:
                            case OpxEgaMono4:
                                break;
                            case OpxEgaEmbeddedView:
                            {
                                VectorGetAbsCoordsNoMirror(data, ref curPos, ref x, ref y);
                                int size = ReadUInt16(data, curPos);
                                curPos += 2;
                                // hardcoded in SSCI: 16 for SCI1 early except Space Quest 4, 0 otherwise
                                // (SQ4 pictures 546, 547 and 631, bugs #5250 and #5249)
                                _priority = _version <= SciVersion.V1Early && _gameId != SciGameId.Sq4 ? (byte)16 : (byte)0;
                                DrawCelData(data, curPos, curPos + 8, 0, x, y, 0, 0, true);
                                curPos += size;
                                break;
                            }

                            case OpxEgaSetPriorityTable:
                                _ports.PriorityBandsInit(data.Slice(curPos, 14));
                                curPos += 14;
                                break;
                            default:
                                throw new InvalidDataException($"Unsupported sci1 extended pic-operation {picOp:X}");
                        }
                    }
                    else
                    {
                        switch (picOp = data[curPos++])
                        {
                            case OpxVgaSetPaletteEntries:
                                while (VectorIsNonOpcode(data, curPos))
                                {
                                    curPos++; // skip commands
                                }

                                break;
                            case OpxVgaSetPalette:
                                if (_resMan.ViewType is ViewType.Amiga or ViewType.Amiga64)
                                {
                                    if (data[curPos] == 0x00 && data[curPos + 1] == 0x01 && (data[curPos + 32] & 0xF0) != 0xF0)
                                    {
                                        // a left-over VGA palette, ignored
                                        curPos += 256 + 4 + 1024;
                                    }
                                    else
                                    {
                                        // sets half of the Amiga palette
                                        _palette.ModifyAmigaPalette(data[curPos..]);
                                        curPos += 32;
                                    }
                                }
                                else
                                {
                                    curPos += 256 + 4; // skip over mapping and timestamp
                                    for (int i = 0; i < 256; i++)
                                    {
                                        palette.Colors[i].Used = data[curPos++];
                                        palette.Colors[i].R = data[curPos++];
                                        palette.Colors[i].G = data[curPos++];
                                        palette.Colors[i].B = data[curPos++];
                                    }

                                    _palette.Set(palette, true);
                                }

                                break;
                            case OpxVgaEmbeddedView:
                            {
                                VectorGetAbsCoordsNoMirror(data, ref curPos, ref x, ref y);
                                int size = ReadUInt16(data, curPos);
                                curPos += 2;
                                if (_version <= SciVersion.V1Early)
                                {
                                    // SCI1 early always used priority 0 for cels inside pictures
                                    // (Space Quest 4 orange ship lifting off, bug #6446)
                                    _priority = 0;
                                }
                                else
                                {
                                    // the cel is drawn with the current priority too
                                    _priority = picPriority;
                                }

                                DrawCelData(data, curPos, curPos + 8, 0, x, y, 0, 0, false);
                                curPos += size;
                                break;
                            }

                            case OpxVgaPriorityTableEqDist:
                                _ports.PriorityBandsInit(-1, ReadUInt16(data, curPos), ReadUInt16(data, curPos + 2));
                                curPos += 4;
                                break;
                            case OpxVgaPriorityTableExplicit:
                                _ports.PriorityBandsInit(data.Slice(curPos, 14));
                                curPos += 14;
                                break;
                            default:
                                throw new InvalidDataException($"Unsupported sci1 extended pic-operation {picOp:X}");
                        }
                    }

                    break;
                case PicOp.Terminate:
                    _priority = picPriority;
                    if (isEga)
                    {
                        _screen.Dither(_addToFlag);
                        if (_gameId == SciGameId.Sq3 && ResourceId == 154)
                        {
                            // SQ3 intro, the ship gets sucked in
                            _screen.DitherForceDitheredColor(0xD0);
                        }
                    }

                    return;
                default:
                    throw new InvalidDataException($"Unsupported pic-operation {picOp:X} at position {curPos - 1}");
            }
        }

        throw new InvalidDataException("picture vector data without terminator");
    }

    private static bool VectorIsNonOpcode(ReadOnlySpan<byte> data, int pos) => pos < data.Length && data[pos] < PicOpFirst;

    private void VectorGetAbsCoords(ReadOnlySpan<byte> data, ref int curPos, ref int x, ref int y)
    {
        byte pixel = data[curPos++];
        x = data[curPos++] + ((pixel & 0xF0) << 4);
        y = data[curPos++] + ((pixel & 0x0F) << 8);
        if (_mirroredFlag)
        {
            x = 319 - x;
        }
    }

    private static void VectorGetAbsCoordsNoMirror(ReadOnlySpan<byte> data, ref int curPos, ref int x, ref int y)
    {
        byte pixel = data[curPos++];
        x = data[curPos++] + ((pixel & 0xF0) << 4);
        y = data[curPos++] + ((pixel & 0x0F) << 8);
    }

    private void VectorGetRelCoords(ReadOnlySpan<byte> data, ref int curPos, ref int x, ref int y)
    {
        byte pixel = data[curPos++];
        int sign = _mirroredFlag ? -1 : 1;
        if ((pixel & 0x80) != 0)
        {
            x -= ((pixel >> 4) & 7) * sign;
        }
        else
        {
            x += (pixel >> 4) * sign;
        }

        if ((pixel & 0x08) != 0)
        {
            y -= pixel & 7;
        }
        else
        {
            y += pixel & 7;
        }
    }

    private void VectorGetRelCoordsMed(ReadOnlySpan<byte> data, ref int curPos, ref int x, ref int y)
    {
        byte pixel = data[curPos++];
        if ((pixel & 0x80) != 0)
        {
            y -= pixel & 0x7F;
        }
        else
        {
            y += pixel;
        }

        pixel = data[curPos++];
        int sign = _mirroredFlag ? -1 : 1;
        if ((pixel & 0x80) != 0)
        {
            x -= (128 - (pixel & 0x7F)) * sign;
        }
        else
        {
            x += pixel * sign;
        }
    }

    private static void VectorGetPatternTexture(ReadOnlySpan<byte> data, ref int curPos, byte patternCode, ref byte patternTexture)
    {
        if ((patternCode & PatternCodeUseTexture) != 0)
        {
            patternTexture = (byte)((data[curPos++] >> 1) & 0x7F);
        }
    }

    /// <summary>
    /// Sierra's flood fill. Not replaceable by a generic algorithm: it must behave exactly like
    /// the original, including the cases where it does nothing.
    /// </summary>
    private void VectorFloodFill(int x, int y, byte color, byte priority, byte control, bool isEga)
    {
        var curPort = _ports.GetPort();
        var stack = new Stack<SciPoint>();
        var screenMask = GfxScreen.GetDrawingMask(color, priority, control);

        var p = new SciPoint(x + curPort.Left, y + curPort.Top);

        if (!ScreenContains(p))
        {
            return;
        }

        byte searchColor = _screen.VectorGetVisual(p.X, p.Y);
        byte searchPriority = _screen.VectorGetPriority(p.X, p.Y);
        byte searchControl = _screen.VectorGetControl(p.X, p.Y);

        if (isEga)
        {
            // EGA pixels are 4 bits; a full byte is stored for undithering, but only the visible
            // colour is compared when filling
            searchColor = ((x ^ y) & 1) != 0
                ? (byte)((searchColor ^ (searchColor >> 4)) & 0x0F)
                : (byte)(searchColor & 0x0F);
        }

        // taken from Sierra SCI: the fill gets aborted on various occasions
        if ((screenMask & GfxScreenMask.Visual) != 0)
        {
            if (color == _screen.ColorWhite || searchColor != _screen.ColorWhite)
            {
                return;
            }
        }
        else if ((screenMask & GfxScreenMask.Priority) != 0)
        {
            if (priority == 0 || searchPriority != 0)
            {
                return;
            }
        }
        else if ((screenMask & GfxScreenMask.Control) != 0)
        {
            if (control == 0 || searchControl != 0)
            {
                return;
            }
        }

        // remove the screens that already have the right value
        if ((screenMask & GfxScreenMask.Visual) != 0 && searchColor == color)
        {
            screenMask &= ~GfxScreenMask.Visual;
        }

        if ((screenMask & GfxScreenMask.Priority) != 0 && searchPriority == priority)
        {
            screenMask &= ~GfxScreenMask.Priority;
        }

        if ((screenMask & GfxScreenMask.Control) != 0 && searchControl == control)
        {
            screenMask &= ~GfxScreenMask.Control;
        }

        if (screenMask == GfxScreenMask.None)
        {
            return;
        }

        GfxScreenMask matchMask;
        if ((screenMask & GfxScreenMask.Visual) != 0)
        {
            matchMask = GfxScreenMask.Visual;
        }
        else if ((screenMask & GfxScreenMask.Priority) != 0)
        {
            matchMask = GfxScreenMask.Priority;
        }
        else
        {
            matchMask = GfxScreenMask.Control;
        }

        // hard borders for filling
        int borderLeft = curPort.Rect.Left + curPort.Left;
        int borderTop = curPort.Rect.Top + curPort.Top;
        int borderRight = curPort.Rect.Right + curPort.Left - 1;
        int borderBottom = curPort.Rect.Bottom + curPort.Top - 1;

        stack.Push(p);

        while (stack.Count > 0)
        {
            p = stack.Pop();
            if (_screen.VectorIsFillMatch(p.X, p.Y, matchMask, searchColor, searchPriority, searchControl, isEga) == 0)
            {
                continue; // already filled
            }

            _screen.VectorPutPixel(p.X, p.Y, screenMask, color, priority, control);
            int curToLeft = p.X;
            int curToRight = p.X;

            // move the west and east pointers as long as there is a matching colour to fill
            while (curToLeft > borderLeft && _screen.VectorIsFillMatch(curToLeft - 1, p.Y, matchMask, searchColor, searchPriority, searchControl, isEga) != 0)
            {
                _screen.VectorPutPixel(--curToLeft, p.Y, screenMask, color, priority, control);
            }

            while (curToRight < borderRight && _screen.VectorIsFillMatch(curToRight + 1, p.Y, matchMask, searchColor, searchPriority, searchControl, isEga) != 0)
            {
                _screen.VectorPutPixel(++curToRight, p.Y, screenMask, color, priority, control);
            }

            // check the lines above and below for fill targets
            bool aSet = false;
            bool bSet = false;
            while (curToLeft <= curToRight)
            {
                if (p.Y > borderTop && _screen.VectorIsFillMatch(curToLeft, p.Y - 1, matchMask, searchColor, searchPriority, searchControl, isEga) != 0)
                {
                    if (!aSet)
                    {
                        stack.Push(new SciPoint(curToLeft, p.Y - 1));
                        aSet = true;
                    }
                }
                else
                {
                    aSet = false;
                }

                if (p.Y < borderBottom && _screen.VectorIsFillMatch(curToLeft, p.Y + 1, matchMask, searchColor, searchPriority, searchControl, isEga) != 0)
                {
                    if (!bSet)
                    {
                        stack.Push(new SciPoint(curToLeft, p.Y + 1));
                        bSet = true;
                    }
                }
                else
                {
                    bSet = false;
                }

                curToLeft++;
            }
        }
    }

    private bool ScreenContains(SciPoint p) => p.X >= 0 && p.X < _screen.Width && p.Y >= 0 && p.Y < _screen.Height;

    // ---- patterns ----

    private static readonly byte[][] PatternCircles =
    [
        [0x01],
        [0x72, 0x02],
        [0xCE, 0xF7, 0x7D, 0x0E],
        [0x1C, 0x3E, 0x7F, 0x7F, 0x7F, 0x3E, 0x1C, 0x00],
        [0x38, 0xF8, 0xF3, 0xDF, 0x7F, 0xFF, 0xFD, 0xF7, 0x9F, 0x3F, 0x38],
        [0x70, 0xC0, 0x1F, 0xFE, 0xE3, 0x3F, 0xFF, 0xF7, 0x7F, 0xFF, 0xE7, 0x3F, 0xFE, 0xC3, 0x1F, 0xF8, 0x00],
        [
            0xF0, 0x01, 0xFF, 0xE1, 0xFF, 0xF8, 0x3F, 0xFF, 0xDF, 0xFF, 0xF7, 0xFF, 0xFD, 0x7F, 0xFF, 0x9F, 0xFF,
            0xE3, 0xFF, 0xF0, 0x1F, 0xF0, 0x01,
        ],
        [
            0xE0, 0x03, 0xF8, 0x0F, 0xFC, 0x1F, 0xFE, 0x3F, 0xFE, 0x3F, 0xFF, 0x7F, 0xFF, 0x7F, 0xFF, 0x7F, 0xFF,
            0x7F, 0xFF, 0x7F, 0xFE, 0x3F, 0xFE, 0x3F, 0xFC, 0x1F, 0xF8, 0x0F, 0xE0, 0x03,
        ],
    ];

    // Sierra's texture table read bitwise upwards (bit 0 to bit 7) instead of downwards, so it can
    // be walked without worrying about clipping. The last bit of the last byte is ignored, as the
    // original interpreter ignored it. The table is duplicated to avoid wrapping around.
    private static readonly bool[] PatternTextures = BuildPatternTextures();

    private static bool[] BuildPatternTextures()
    {
        ReadOnlySpan<byte> source =
        [
            0x04, 0x29, 0x40, 0x24, 0x09, 0x41, 0x25, 0x45,
            0x41, 0x90, 0x50, 0x44, 0x48, 0x08, 0x42, 0x28,
            0x89, 0x52, 0x89, 0x88, 0x10, 0x48, 0xA4, 0x08,
            0x44, 0x15, 0x28, 0x24, 0x00, 0x0A, 0x24, 0x20,
        ];

        var half = new List<bool>(32 * 8);
        for (int i = 0; i < source.Length; i++)
        {
            int bits = i == source.Length - 1 ? 7 : 8;
            for (int bit = 0; bit < bits; bit++)
            {
                half.Add(((source[i] >> bit) & 1) != 0);
            }
        }

        var table = new bool[half.Count * 2];
        half.CopyTo(table, 0);
        half.CopyTo(table, half.Count);
        return table;
    }

    // bit offsets into the texture table
    private static readonly byte[] PatternTextureOffset =
    [
        0x00, 0x18, 0x30, 0xc4, 0xdc, 0x65, 0xeb, 0x48,
        0x60, 0xbd, 0x89, 0x04, 0x0a, 0xf4, 0x7d, 0x6d,
        0x85, 0xb0, 0x8e, 0x95, 0x1f, 0x22, 0x0d, 0xdf,
        0x2a, 0x78, 0xd5, 0x73, 0x1c, 0xb4, 0x40, 0xa1,
        0xb9, 0x3c, 0xca, 0x58, 0x92, 0x34, 0xcc, 0xce,
        0xd7, 0x42, 0x90, 0x0f, 0x8b, 0x7f, 0x32, 0xed,
        0x5c, 0x9d, 0xc8, 0x99, 0xad, 0x4e, 0x56, 0xa6,
        0xf7, 0x68, 0xb7, 0x25, 0x82, 0x37, 0x3a, 0x51,
        0x69, 0x26, 0x38, 0x52, 0x9e, 0x9a, 0x4f, 0xa7,
        0x43, 0x10, 0x80, 0xee, 0x3d, 0x59, 0x35, 0xcf,
        0x79, 0x74, 0xb5, 0xa2, 0xb1, 0x96, 0x23, 0xe0,
        0xbe, 0x05, 0xf5, 0x6e, 0x19, 0xc5, 0x66, 0x49,
        0xf0, 0xd1, 0x54, 0xa9, 0x70, 0x4b, 0xa4, 0xe2,
        0xe6, 0xe5, 0xab, 0xe4, 0xd2, 0xaa, 0x4c, 0xe3,
        0x06, 0x6f, 0xc6, 0x4a, 0x75, 0xa3, 0x97, 0xe1,
    ];

    private void VectorPatternBox(SciRect box, SciRect clipBox, byte color, byte prio, byte control)
    {
        var drawMask = GfxScreen.GetDrawingMask(color, prio, control);
        for (int y = box.Top; y < box.Bottom; y++)
        {
            for (int x = box.Left; x < box.Right; x++)
            {
                VectorPatternBoxPixel(x, y, clipBox, drawMask, color, prio, control);
            }
        }
    }

    private void VectorPatternTexturedBox(SciRect box, SciRect clipBox, byte color, byte prio, byte control, byte texture)
    {
        var drawMask = GfxScreen.GetDrawingMask(color, prio, control);
        int textureData = PatternTextureOffset[texture];
        for (int y = box.Top; y < box.Bottom; y++)
        {
            for (int x = box.Left; x < box.Right; x++)
            {
                if (PatternTextures[textureData])
                {
                    VectorPatternBoxPixel(x, y, clipBox, drawMask, color, prio, control);
                }

                textureData++;
            }
        }
    }

    /// <summary>
    /// Draws one rectangle pattern pixel with the original's wrap-around: a pattern placed at the
    /// right edge has its last column go beyond the edge, and those pixels wrap to the next row
    /// with the opposite dither colour. Circles are unaffected, their last column is transparent.
    /// </summary>
    private void VectorPatternBoxPixel(int x, int y, SciRect clipBox, GfxScreenMask drawMask, byte color, byte prio, byte control)
    {
        if (!(clipBox.Left <= x && clipBox.Top <= y && y < clipBox.Bottom))
        {
            return;
        }

        if (x < clipBox.Right)
        {
            _screen.VectorPutPixel(x, y, drawMask, color, prio, control);
        }
        else if (y < clipBox.Bottom - 1)
        {
            if ((color & 0xF0) != 0)
            {
                color ^= (byte)(color << 4); // decode
                color = (byte)((color << 4) | (color >> 4)); // swap
                color ^= (byte)(color << 4); // re-encode
            }

            _screen.VectorPutPixel(0, y + 1, drawMask, color, prio, control);
        }
    }

    private void VectorPatternCircle(SciRect box, SciRect clipBox, byte size, byte color, byte prio, byte control)
    {
        var drawMask = GfxScreen.GetDrawingMask(color, prio, control);
        var circleData = PatternCircles[size];
        int circlePos = 0;
        byte bitmap = circleData[0];
        int bitNo = 0;

        for (int y = box.Top; y < box.Bottom; y++)
        {
            for (int x = box.Left; x < box.Right; x++)
            {
                if (bitNo == 8)
                {
                    circlePos++;
                    bitmap = circlePos < circleData.Length ? circleData[circlePos] : (byte)0;
                    bitNo = 0;
                }

                if ((bitmap & 1) != 0 && clipBox.Contains(x, y))
                {
                    _screen.VectorPutPixel(x, y, drawMask, color, prio, control);
                }

                bitNo++;
                bitmap >>= 1;
            }
        }
    }

    private void VectorPatternTexturedCircle(SciRect box, SciRect clipBox, byte size, byte color, byte prio, byte control, byte texture)
    {
        var drawMask = GfxScreen.GetDrawingMask(color, prio, control);
        var circleData = PatternCircles[size];
        int circlePos = 0;
        byte bitmap = circleData[0];
        int bitNo = 0;
        int textureData = PatternTextureOffset[texture];

        for (int y = box.Top; y < box.Bottom; y++)
        {
            for (int x = box.Left; x < box.Right; x++)
            {
                if (bitNo == 8)
                {
                    circlePos++;
                    bitmap = circlePos < circleData.Length ? circleData[circlePos] : (byte)0;
                    bitNo = 0;
                }

                if ((bitmap & 1) != 0)
                {
                    if (PatternTextures[textureData] && clipBox.Contains(x, y))
                    {
                        _screen.VectorPutPixel(x, y, drawMask, color, prio, control);
                    }

                    textureData++;
                }

                bitNo++;
                bitmap >>= 1;
            }
        }
    }

    private void VectorPattern(int x, int y, byte color, byte priority, byte control, byte code, byte texture)
    {
        byte size = (byte)(code & PatternCodePenSize);

        // the pattern box is centred on x,y and one pixel wider than high
        var box = new SciRect(x - size, y - size, x + size + 2, y + size + 1);

        // move the box if it goes off the left or top of the picture
        if (box.Left < 0)
        {
            box.MoveTo(0, box.Top);
        }

        if (box.Top < 0)
        {
            box.MoveTo(box.Left, 0);
        }

        _ports.OffsetRect(ref box);

        // Move the box if it goes off the right or bottom of the screen, leaving the last column
        // beyond the right edge even though it is not drawn. The box cannot simply be clipped:
        // the texture and circle data are consumed by evaluating every pixel, including the ones
        // skipped for being out of bounds (SQ3 picture 2).
        if (box.Right > _screen.ScriptWidth + 1)
        {
            box.MoveTo(_screen.ScriptWidth + 1 - box.Width, box.Top);
        }

        if (box.Bottom > _screen.ScriptHeight)
        {
            box.MoveTo(box.Left, _screen.ScriptHeight - box.Height);
        }

        var clipBox = new SciRect(0, 0, _screen.ScriptWidth, _screen.ScriptHeight);

        if ((code & PatternCodeRectangle) != 0)
        {
            if ((code & PatternCodeUseTexture) != 0)
            {
                VectorPatternTexturedBox(box, clipBox, color, priority, control, texture);
            }
            else
            {
                VectorPatternBox(box, clipBox, color, priority, control);
            }
        }
        else
        {
            if ((code & PatternCodeUseTexture) != 0)
            {
                VectorPatternTexturedCircle(box, clipBox, size, color, priority, control, texture);
            }
            else
            {
                VectorPatternCircle(box, clipBox, size, color, priority, control);
            }
        }
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
}
