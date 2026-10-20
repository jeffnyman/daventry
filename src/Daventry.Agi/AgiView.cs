using System.Buffers.Binary;
using System.Text;
using Daventry.Agi.Graphics;
using Daventry.Agi.Resources;

namespace Daventry.Agi;

/// <summary>One frame of animation. Mirrors ScummVM's AgiViewCel.</summary>
public sealed class AgiViewCel
{
    public byte Width { get; init; }

    public byte Height { get; init; }

    public byte ClearKey { get; set; }

    public bool Mirrored { get; init; }

    /// <summary>Width times Height bytes of colour indices, row major.</summary>
    public byte[] RawBitmap { get; init; } = [];
}

/// <summary>A sequence of cels, usually one facing direction. Mirrors ScummVM's AgiViewLoop.</summary>
public sealed class AgiViewLoop
{
    public AgiViewCel[] Cels { get; init; } = [];

    public int CelCount => Cels.Length;
}

/// <summary>A decoded VIEW resource. Mirrors ScummVM's AgiView.</summary>
public sealed class AgiView
{
    public byte HeaderStepSize { get; init; }

    public byte HeaderCycleTime { get; init; }

    public string? Description { get; init; }

    public AgiViewLoop[] Loops { get; init; } = [];

    public int LoopCount => Loops.Length;
}

/// <summary>Decodes VIEW resources. Port of decodeView and the cel unpackers from ScummVM's view.cpp.</summary>
public sealed class AgiViewDecoder
{
    /// <summary>Apple II V2+ views use different colour values.</summary>
    private static readonly byte[] Apple2ViewColorMap =
    [
        0x00, 0x04, 0x01, 0x05, 0x02, 0x08, 0x09, 0x0b,
        0x06, 0x0d, 0x07, 0x0c, 0x0a, 0x0e, 0x03, 0x0f,
    ];

    private readonly AgiDetectedGame _description;
    private readonly GfxMgr _gfx;

    public AgiViewDecoder(AgiDetectedGame description, GfxMgr gfx)
    {
        _description = description;
        _gfx = gfx;
    }

    private bool IsApple2 => _description.Platform == AgiPlatform.Apple2 && _description.Version >= 0x2000;

    public AgiView Decode(byte[] resourceData, int resourceSize, int viewNr)
    {
        resourceSize = Math.Min(resourceSize, resourceData.Length);
        if (resourceSize < 5)
        {
            throw new AgiResourceException($"unexpected end of view data for view {viewNr}");
        }

        byte headerStepSize = 0;
        byte headerCycleTime = 0;
        if (_description.Version < 0x2000)
        {
            headerStepSize = resourceData[0];
            headerCycleTime = resourceData[1];
        }

        bool isAgi256Data = false;
        if ((_description.Features & AgiGameFeatures.Agi256) != 0)
        {
            isAgi256Data = BinaryPrimitives.ReadUInt16LittleEndian(resourceData) == 0xF00F; // AGI256-2 view
        }

        // Apple II V2+ views stopped including the first two bytes
        int headerLoopCountOffset = 2;
        int viewHeaderSize = 5;
        bool apple2 = IsApple2;
        if (apple2)
        {
            headerLoopCountOffset = 0;
            viewHeaderSize = 3;
        }

        byte headerLoopCount = resourceData[headerLoopCountOffset];
        ushort headerDescriptionOffset = BinaryPrimitives.ReadUInt16LittleEndian(resourceData.AsSpan(headerLoopCountOffset + 1));

        string? description = null;
        if (headerDescriptionOffset != 0 && headerDescriptionOffset < resourceSize)
        {
            int end = headerDescriptionOffset;
            while (end < resourceSize && resourceData[end] != 0)
            {
                end++;
            }

            description = Encoding.Latin1.GetString(resourceData, headerDescriptionOffset, end - headerDescriptionOffset);
        }

        if (headerLoopCount == 0)
        {
            return new AgiView { HeaderStepSize = headerStepSize, HeaderCycleTime = headerCycleTime, Description = description };
        }

        if (resourceSize < viewHeaderSize + headerLoopCount * 2)
        {
            throw new AgiResourceException($"unexpected end of view data for view {viewNr}");
        }

        bool isMirrorDataInLoopHeader = _description.Version == 0x2230;
        var loops = new AgiViewLoop[headerLoopCount];
        for (int loopNr = 0; loopNr < headerLoopCount; loopNr++)
        {
            ushort loopOffset = BinaryPrimitives.ReadUInt16LittleEndian(resourceData.AsSpan(viewHeaderSize + loopNr * 2));
            if (resourceSize < loopOffset + 1)
            {
                throw new AgiResourceException($"unexpected end of view data for view {viewNr}");
            }

            // loop header: celCount byte (upper nibble is mirror data in 2.230), then relative cel offsets
            byte loopHeaderCelCountByte = resourceData[loopOffset];
            int celCount = isMirrorDataInLoopHeader ? loopHeaderCelCountByte & 0x0F : loopHeaderCelCountByte;

            if (resourceSize < loopOffset + 1 + celCount * 2)
            {
                throw new AgiResourceException($"unexpected end of view data for view {viewNr}");
            }

            var cels = new AgiViewCel[celCount];
            for (int celNr = 0; celNr < celCount; celNr++)
            {
                int celOffset = BinaryPrimitives.ReadUInt16LittleEndian(resourceData.AsSpan(loopOffset + 1 + celNr * 2)) + loopOffset;
                if (resourceSize < celOffset + 3)
                {
                    throw new AgiResourceException($"unexpected end of view data for view {viewNr}");
                }

                // cel header: width, height, transparency + mirroring, then cel data
                byte celHeaderWidth = resourceData[celOffset];
                byte celHeaderHeight = resourceData[celOffset + 1];
                byte celHeaderTransparencyMirror = resourceData[celOffset + 2];
                if (apple2)
                {
                    // Apple II views switched the transparency and mirror bits
                    celHeaderTransparencyMirror = (byte)((celHeaderTransparencyMirror << 4) | (celHeaderTransparencyMirror >> 4));
                }

                byte celHeaderClearKey;
                bool celHeaderMirrored = false;
                if (!isAgi256Data)
                {
                    // bits 0-3: clear key, bits 4-6: original loop that is never mirrored, bit 7: apply mirroring
                    celHeaderClearKey = (byte)(celHeaderTransparencyMirror & 0x0F);
                    if (apple2)
                    {
                        celHeaderClearKey = Apple2ViewColorMap[celHeaderClearKey];
                    }

                    if (isMirrorDataInLoopHeader)
                    {
                        // 2.230 (early version of xmascard): mirror data is in the loop header
                        if ((loopHeaderCelCountByte & 0xC0) == 0xC0)
                        {
                            int celHeaderMirrorLoop = (loopHeaderCelCountByte >> 4) & 0x03;
                            celHeaderMirrored = celHeaderMirrorLoop != loopNr;
                        }
                    }
                    else if ((celHeaderTransparencyMirror & 0x80) != 0)
                    {
                        // 2.272+: mirror data is in the cel header
                        int celHeaderMirrorLoop = (celHeaderTransparencyMirror >> 4) & 0x07;
                        celHeaderMirrored = celHeaderMirrorLoop != loopNr;
                    }
                }
                else
                {
                    celHeaderClearKey = celHeaderTransparencyMirror; // full 8 bits for the clear key
                }

                if (celHeaderWidth == 0 && celHeaderHeight == 0)
                {
                    throw new AgiResourceException($"view {viewNr} cel is 0x0");
                }

                int celCompressedOffset = celOffset + 3;
                int celCompressedSize = resourceSize - celCompressedOffset;
                if (celCompressedSize <= 0)
                {
                    throw new AgiResourceException($"compressed size of loop within view {viewNr} is 0 bytes");
                }

                var cel = new AgiViewCel
                {
                    Width = celHeaderWidth,
                    Height = celHeaderHeight,
                    ClearKey = celHeaderClearKey,
                    Mirrored = celHeaderMirrored,
                    RawBitmap = new byte[celHeaderWidth * celHeaderHeight],
                };

                if (!isAgi256Data)
                {
                    UnpackViewCelData(cel, resourceData, celCompressedOffset, celCompressedSize, viewNr);
                }
                else
                {
                    UnpackViewCelDataAgi256(cel, resourceData, celCompressedOffset, celCompressedSize, viewNr);
                }

                cels[celNr] = cel;
            }

            loops[loopNr] = new AgiViewLoop { Cels = cels };
        }

        return new AgiView
        {
            HeaderStepSize = headerStepSize,
            HeaderCycleTime = headerCycleTime,
            Description = description,
            Loops = loops,
        };
    }

    /// <summary>
    /// Run-length cel data: each byte is colour (high nibble) and count (low nibble); zero ends a
    /// row and fills the rest with the clear key. Mirrored cels are written right to left.
    /// </summary>
    private void UnpackViewCelData(AgiViewCel cel, byte[] data, int offset, int compressedSize, int viewNr)
    {
        var raw = cel.RawBitmap;
        int remainingHeight = cel.Height;
        int remainingWidth = cel.Width;
        int adjustPreChangeSingle = 0;
        int adjustAfterChangeSingle = +1;
        bool apple2 = IsApple2;
        int pos = 0;

        if (cel.Mirrored)
        {
            adjustPreChangeSingle = -1;
            adjustAfterChangeSingle = 0;
            pos += cel.Width;
        }

        while (remainingHeight > 0)
        {
            if (compressedSize == 0)
            {
                throw new AgiResourceException($"unexpected end of data, while unpacking view {viewNr}");
            }

            byte curByte = data[offset++];
            compressedSize--;

            byte curColor;
            int curChunkLen;
            if (curByte == 0)
            {
                curColor = cel.ClearKey;
                curChunkLen = remainingWidth;
            }
            else
            {
                curColor = (byte)(curByte >> 4);
                curChunkLen = curByte & 0x0F;
                if (curChunkLen > remainingWidth)
                {
                    throw new AgiResourceException($"invalid chunk in view {viewNr}");
                }

                if (apple2)
                {
                    curColor = Apple2ViewColorMap[curColor];
                }
            }

            switch (curChunkLen)
            {
                case 0:
                    break;
                case 1:
                    pos += adjustPreChangeSingle;
                    raw[pos] = curColor;
                    pos += adjustAfterChangeSingle;
                    break;
                default:
                    if (cel.Mirrored)
                    {
                        pos -= curChunkLen;
                    }

                    raw.AsSpan(pos, curChunkLen).Fill(curColor);
                    if (!cel.Mirrored)
                    {
                        pos += curChunkLen;
                    }

                    break;
            }

            remainingWidth -= curChunkLen;

            // Each row is terminated by a zero byte; any remaining pixels are transparent. Apple II
            // views have no terminators: rows end when full, saving a byte per full row.
            if (curByte == 0 || (apple2 && remainingWidth == 0))
            {
                remainingWidth = cel.Width;
                remainingHeight--;
                if (cel.Mirrored)
                {
                    pos += cel.Width * 2;
                }
            }
        }

        if (_gfx.RenderMode == AgiRenderMode.Cga)
        {
            // apply CGA dithering to the whole cel, including the clear key
            cel.ClearKey = _gfx.GetCgaMixtureColor(cel.ClearKey);
            for (int i = 0; i < raw.Length; i++)
            {
                raw[i] = _gfx.GetCgaMixtureColor(raw[i]);
            }
        }
    }

    private static void UnpackViewCelDataAgi256(AgiViewCel cel, byte[] data, int offset, int compressedSize, int viewNr)
    {
        var raw = cel.RawBitmap;
        int remainingHeight = cel.Height;
        int remainingWidth = cel.Width;
        int pos = 0;

        while (remainingHeight > 0)
        {
            if (compressedSize == 0)
            {
                throw new AgiResourceException($"unexpected end of data, while unpacking AGI256 view {viewNr}");
            }

            byte curByte = data[offset++];
            compressedSize--;

            if (curByte == 0)
            {
                // next row; fill the remainder with the clear key
                if (remainingWidth > 0)
                {
                    raw.AsSpan(pos, remainingWidth).Fill(cel.ClearKey);
                    pos += remainingWidth;
                    remainingWidth = 0;
                }
            }
            else
            {
                if (remainingWidth == 0)
                {
                    throw new AgiResourceException($"broken view data, while unpacking AGI256 view {viewNr}");
                }

                raw[pos++] = curByte;
                remainingWidth--;
            }

            if (curByte == 0)
            {
                remainingWidth = cel.Width;
                remainingHeight--;
            }
        }
    }
}
