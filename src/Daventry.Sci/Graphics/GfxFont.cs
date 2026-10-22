using System.Buffers.Binary;
using Daventry.Core.Hosting;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Graphics;

/// <summary>A bitmap font loaded from a font resource. Port of ScummVM's GfxFontFromResource (SCI16 part).</summary>
public sealed class GfxFontFromResource
{
    private struct CharInfo
    {
        public int Offset;
        public byte Width;
        public byte Height;
    }

    private readonly GfxScreen? _screen;
    private readonly byte[] _data;
    private readonly CharInfo[] _chars;
    private readonly ILog _log;

    public GfxFontFromResource(ResourceManager resMan, GfxScreen? screen, int resourceId, ILog? log = null)
        : this(LoadFontData(resMan, resourceId), screen, resourceId, log)
    {
    }

    private static byte[] LoadFontData(ResourceManager resMan, int resourceId)
    {
        // Workaround: LSL1 SCI mixes its own internal fonts with the global SCI ones; their extra bits are removed
        if (resMan.TestResource(new ResourceId(ResourceType.Font, (ushort)resourceId)) is null)
        {
            resourceId &= 0x7FF;
        }

        var resource = resMan.FindResource(ResourceType.Font, resourceId)
            ?? throw new FileNotFoundException($"font resource {resourceId} not found");
        return resource.Data!;
    }

    /// <summary>A font from raw resource data, such as the font built into the SCI32 interpreters.</summary>
    public GfxFontFromResource(byte[] data, GfxScreen? screen, int resourceId, ILog? log = null)
    {
        _screen = screen;
        _log = log ?? NullLog.Instance;
        ResourceId = resourceId;
        _data = data;

        NumChars = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(2));
        Height = (byte)BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(4));
        _chars = new CharInfo[NumChars];
        for (int i = 0; i < NumChars; i++)
        {
            int charOffsetIndex = 6 + i * 2;
            int offset = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(charOffsetIndex));

            // WORKAROUND: several fan games have invalid offsets for character 127. SSCI only
            // uses the offset when drawing, so it is unaffected as long as the character is not
            // used (LSL2 and LSL3 Polish, SQ3 Spanish, bug #10509).
            if (offset + 2 > _data.Length)
            {
                _log.Warning("sci.font", $"font {resourceId} glyph {i} has invalid offset {offset}, resource size {_data.Length}");
                continue;
            }

            _chars[i].Offset = offset;
            _chars[i].Width = _data[offset];
            _chars[i].Height = _data[offset + 1];
        }
    }

    public int ResourceId { get; }

    public int NumChars { get; }

    public byte Height { get; }

    public byte GetCharWidth(int chr) => chr < NumChars ? _chars[chr].Width : (byte)0;

    public byte GetCharHeight(int chr) => chr < NumChars ? _chars[chr].Height : (byte)0;

    public ReadOnlySpan<byte> GetCharData(int chr)
    {
        if (chr >= NumChars)
        {
            return default;
        }

        int bytesPerRow = (_chars[chr].Width + 7) / 8;
        int charDataSize = bytesPerRow * _chars[chr].Height;
        int start = _chars[chr].Offset + 2;
        return _data.AsSpan(start, Math.Min(charDataSize, Math.Max(0, _data.Length - start)));
    }

    public void Draw(int chr, int top, int left, byte color, bool greyedOutput)
    {
        if (chr >= NumChars)
        {
            // SSCI silently ignores characters the font does not have
            _log.Warning("sci.font", $"font {ResourceId} is missing glyph {chr}");
            return;
        }

        var screen = _screen ?? throw new InvalidOperationException("the font has no screen to draw on");
        int screenWidth = screen.FontIsUpscaled ? screen.DisplayWidth : screen.Width;
        int screenHeight = screen.FontIsUpscaled ? screen.DisplayHeight : screen.Height;

        int charWidth = GetCharWidth(chr);
        int charHeight = GetCharHeight(chr);
        byte b = 0;
        byte mask = 0xFF;
        int greyedTop = top;

        var charData = GetCharData(chr);
        int pos = 0;
        for (int y = 0; y < charHeight; y++)
        {
            if (greyedOutput)
            {
                mask = (greyedTop++ % 2) != 0 ? (byte)0xAA : (byte)0x55;
            }

            for (int x = 0; x < charWidth; x++)
            {
                if ((x & 7) == 0)
                {
                    b = pos < charData.Length ? (byte)(charData[pos] & mask) : (byte)0;
                    pos++;
                }

                if ((b & 0x80) != 0)
                {
                    int screenX = left + x;
                    int screenY = top + y;
                    if (0 <= screenX && screenX < screenWidth && 0 <= screenY && screenY < screenHeight)
                    {
                        screen.PutFontPixel(top, screenX, y, color);
                    }
                    else
                    {
                        _log.Warning("sci.font", $"font {ResourceId} glyph {chr} drawn out of bounds: {screenX}, {screenY}");
                    }
                }

                b <<= 1;
            }
        }
    }

    /// <summary>Draws a glyph into an 8-bit buffer, as the SCI32 text renderer does for its bitmaps. Port of GfxFontFromResource::drawToBuffer.</summary>
    public void DrawToBuffer(int chr, int top, int left, byte color, bool greyedOutput, byte[] buffer, int bufferOffset, int bufWidth, int bufHeight)
    {
        if (chr >= NumChars)
        {
            // Sierra's interpreter silently ignored glyphs the font does not have
            _log.Warning("sci.font", $"font {ResourceId} is missing glyph {chr}");
            return;
        }

        int charWidth = GetCharWidth(chr);
        int charHeight = GetCharHeight(chr);
        byte b = 0;
        byte mask = 0xFF;
        int greyedTop = top;
        var charData = GetCharData(chr);
        int pos = 0;
        for (int y = 0; y < charHeight; y++)
        {
            if (greyedOutput)
            {
                mask = (greyedTop++ % 2) != 0 ? (byte)0xAA : (byte)0x55;
            }

            for (int x = 0; x < charWidth; x++)
            {
                if ((x & 7) == 0)
                {
                    b = pos < charData.Length ? (byte)(charData[pos] & mask) : (byte)0;
                    pos++;
                }

                if ((b & 0x80) != 0)
                {
                    int bufX = left + x;
                    int bufY = top + y;
                    if (0 <= bufX && bufX < bufWidth && 0 <= bufY && bufY < bufHeight)
                    {
                        buffer[bufferOffset + bufY * bufWidth + bufX] = color;
                    }
                    else
                    {
                        _log.Warning("sci.font", $"font {ResourceId} glyph {chr} drawn out of bounds: {bufX}, {bufY}");
                    }
                }

                b <<= 1;
            }
        }
    }
}
