using System.Buffers.Binary;

namespace Daventry.Sci.Resources;

/// <summary>Why a resource could not be read. Mirrors ScummVM's SCI_ERROR_* codes.</summary>
public enum SciResourceError
{
    None = 0,
    IoError = 1,
    EmptyResource = 2,
    ResMapInvalidEntry = 3,
    ResMapNotFound = 4,
    NoResourceFilesFound = 5,
    UnknownCompression = 6,
    DecompressionError = 7,
    ResourceTooBig = 8,
}

/// <summary>
/// Base decompressor: copies the packed bytes through unchanged and provides the bit readers the
/// real ones use. Port of ScummVM's decompressor.cpp.
/// </summary>
public class Decompressor
{
    private uint _dwBits;
    private int _nBits;
    private uint _szPacked;
    private uint _szUnpacked;
    private uint _dwRead;
    private uint _dwWrote;
    private Stream _src = Stream.Null;
    private byte[] _dest = [];

    public virtual SciResourceError Unpack(Stream src, byte[] dest, uint packed, uint unpacked)
    {
        int read = src.ReadAtLeast(dest.AsSpan(0, (int)Math.Min(packed, (uint)dest.Length)), (int)packed, throwOnEndOfStream: false);
        return read == packed ? SciResourceError.None : SciResourceError.IoError;
    }

    protected void Init(Stream src, byte[] dest, uint packed, uint unpacked)
    {
        _src = src;
        _dest = dest;
        _szPacked = packed;
        _szUnpacked = unpacked;
        _nBits = 0;
        _dwRead = 0;
        _dwWrote = 0;
        _dwBits = 0;
    }

    protected uint Written => _dwWrote;

    protected uint Read => _dwRead;

    /// <summary>Set by decompressors that fail, describing where they stopped.</summary>
    public string? Diagnostics { get; protected set; }

    protected uint UnpackedSize => _szUnpacked;

    protected byte[] Dest => _dest;

    private byte ReadSourceByte()
    {
        int value = _src.ReadByte();
        _dwRead++;
        return value < 0 ? (byte)0 : (byte)value;
    }

    private void FetchBitsMsb()
    {
        while (_nBits <= 24)
        {
            _dwBits |= (uint)ReadSourceByte() << (24 - _nBits);
            _nBits += 8;
        }
    }

    /// <summary>Reads n bits starting with the most significant unread bit.</summary>
    protected uint GetBitsMsb(int n)
    {
        if (_nBits < n)
        {
            FetchBitsMsb();
        }

        uint ret = _dwBits >> (32 - n);
        _dwBits <<= n;
        _nBits -= n;
        return ret;
    }

    protected byte GetByteMsb() => (byte)GetBitsMsb(8);

    private void FetchBitsLsb()
    {
        while (_nBits <= 24)
        {
            _dwBits |= (uint)ReadSourceByte() << _nBits;
            _nBits += 8;
        }
    }

    /// <summary>Reads n bits starting with the least significant unread bit.</summary>
    protected uint GetBitsLsb(int n)
    {
        if (_nBits < n)
        {
            FetchBitsLsb();
        }

        uint ret = _dwBits & ~(0xFFFFFFFFu << n);
        _dwBits >>= n;
        _nBits -= n;
        return ret;
    }

    protected byte GetByteLsb() => (byte)GetBitsLsb(8);

    protected void PutByte(byte b) => _dest[_dwWrote++] = b;

    /// <summary>True once every expected byte has been written and the packed data is exhausted.</summary>
    protected bool IsFinished => _dwWrote == _szUnpacked && _dwRead >= _szPacked;
}

/// <summary>Huffman decompressor for SCI0 pictures.</summary>
public sealed class DecompressorHuffman : Decompressor
{
    private byte[] _nodes = [];

    public override SciResourceError Unpack(Stream src, byte[] dest, uint packed, uint unpacked)
    {
        Init(src, dest, packed, unpacked);

        int numNodes = src.ReadByte();
        int terminator = src.ReadByte() | 0x100;
        if (numNodes < 0)
        {
            return SciResourceError.IoError;
        }

        _nodes = new byte[numNodes << 1];
        src.ReadExactly(_nodes);

        int c;
        while ((c = GetC2()) != terminator && c >= 0 && !IsFinished)
        {
            PutByte((byte)c);
        }

        return Written == UnpackedSize ? SciResourceError.None : SciResourceError.DecompressionError;
    }

    private int GetC2()
    {
        int node = 0;
        while (_nodes[node + 1] != 0)
        {
            int next;
            if (GetBitsMsb(1) != 0)
            {
                next = _nodes[node + 1] & 0x0F; // lower four bits
                if (next == 0)
                {
                    return GetByteMsb() | 0x100;
                }
            }
            else
            {
                next = _nodes[node + 1] >> 4; // upper four bits
            }

            node += next << 1;
        }

        return _nodes[node] | (_nodes[node + 1] << 8);
    }
}

/// <summary>
/// LZW decompressor for SCI0, SCI01 and SCI1. SCI0 is LSB first; SCI01 and SCI1 are MSB first and
/// grow the code size one code early, the well-known "early change" quirk. Views and pictures of
/// SCI1 get an extra unpacking pass that rebuilds the resource layout the interpreter expects.
/// </summary>
public sealed class DecompressorLzw : Decompressor
{
    private readonly ResourceCompression _compression;

    public DecompressorLzw(ResourceCompression compression)
    {
        _compression = compression;
    }

    public override SciResourceError Unpack(Stream src, byte[] dest, uint packed, uint unpacked)
    {
        switch (_compression)
        {
            case ResourceCompression.Lzw:
            case ResourceCompression.Lzw1:
                return UnpackLzw(src, dest, packed, unpacked);
            case ResourceCompression.Lzw1View:
            {
                var buffer = new byte[unpacked];
                var result = UnpackLzw(src, buffer, packed, unpacked);
                if (result == SciResourceError.None)
                {
                    UnpackView(buffer, dest);
                }

                return result;
            }

            case ResourceCompression.Lzw1Pic:
            {
                var buffer = new byte[unpacked];
                var result = UnpackLzw(src, buffer, packed, unpacked);
                if (result == SciResourceError.None)
                {
                    UnpackPic(buffer, dest, (int)unpacked);
                }

                return result;
            }

            default:
                return SciResourceError.UnknownCompression;
        }
    }

    private SciResourceError UnpackLzw(Stream src, byte[] dest, uint packed, uint unpacked)
    {
        Init(src, dest, packed, unpacked);

        int codeBitLength = 9;
        int tableSize = 258;
        int codeLimit = _compression == ResourceCompression.Lzw ? 512 : 511;

        var stringOffsets = new ushort[4096]; // 0-257 unused
        var stringLengths = new ushort[4096];

        bool terminatorFound = false;
        while (!IsFinished)
        {
            int code = (int)(_compression == ResourceCompression.Lzw ? GetBitsLsb(codeBitLength) : GetBitsMsb(codeBitLength));

            if (code >= tableSize)
            {
                // "LZW code exceeds table size" in ScummVM, which stops as well
                Diagnostics = $"LZW code {code} exceeds table size {tableSize}";
                break;
            }

            if (code == 257)
            {
                terminatorFound = true;
                break;
            }

            if (code == 256)
            {
                codeBitLength = 9;
                tableSize = 258;
                codeLimit = _compression == ResourceCompression.Lzw ? 512 : 511;
                continue;
            }

            ushort newStringOffset = (ushort)Written;
            if (code <= 255)
            {
                PutByte((byte)code);
            }
            else
            {
                for (int i = 0; i < stringLengths[code] && !IsFinished; i++)
                {
                    PutByte(dest[stringOffsets[code] + i]);
                }
            }

            if (tableSize >= 4096)
            {
                continue;
            }

            if (tableSize == codeLimit && codeBitLength < 12)
            {
                codeBitLength++;
                codeLimit = 1 << codeBitLength;
                if (_compression != ResourceCompression.Lzw)
                {
                    codeLimit--;
                }
            }

            stringOffsets[tableSize] = newStringOffset;
            stringLengths[tableSize] = (ushort)(Written - newStringOffset + 1);
            tableSize++;
        }

        // Reaching the terminator before the unpacked size is expected for views and pictures,
        // whose final size is only known after post-processing.
        if (terminatorFound || Written == UnpackedSize)
        {
            return SciResourceError.None;
        }

        Diagnostics ??= $"LZW stopped: tableSize {tableSize} codeBits {codeBitLength} written {Written} of {UnpackedSize} read {Read} of {packed}";
        return SciResourceError.DecompressionError;
    }

    private static void UnpackView(byte[] src, byte[] dest)
    {
        int seeker = 0;
        int writer = 0;

        // compressed header
        int celLengths = BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(seeker)) + 2;
        seeker += 2;
        byte loopCount = src[seeker++];
        byte loopHeaderCount = src[seeker++]; // non-mirrored loops
        ushort mirrorMask = BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(seeker));
        seeker += 2;
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(seeker));
        seeker += 2;
        ushort paletteOffset = BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(seeker)); // offset in the output view
        seeker += 2;
        int celHeaderCount = BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(seeker));
        seeker += 2;

        int celCounts = seeker;
        seeker += loopHeaderCount;

        var celDecodedLengths = new ushort[celHeaderCount];
        for (int i = 0; i < celHeaderCount; i++)
        {
            celDecodedLengths[i] = BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(celLengths + 2 * i));
        }

        // locate the pixel data by seeking past every cel's RLE data
        int rleData = celLengths + 2 * celHeaderCount;
        int pixelData = rleData;
        for (int i = 0; i < celHeaderCount; i++)
        {
            SkipRle(src, ref pixelData, celDecodedLengths[i]);
        }

        // view header
        dest[writer++] = loopCount;
        dest[writer++] = 0x80; // flags for a VGA view with RLE compression
        BinaryPrimitives.WriteUInt16LittleEndian(dest.AsSpan(writer), mirrorMask);
        writer += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(dest.AsSpan(writer), version);
        writer += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(dest.AsSpan(writer), paletteOffset);
        writer += 2;

        // loop offset table, filled while the loops are written
        int loopOffsets = writer;
        writer += 2 * loopCount;

        int loopTableIndex = 0;
        int celTableIndex = 0;
        for (int loop = 0; loop < loopCount; loop++)
        {
            if ((mirrorMask & (1 << loop)) != 0)
            {
                // mirrored loop: repeat the previous loop's offset
                dest[loopOffsets] = dest[loopOffsets - 2];
                dest[loopOffsets + 1] = dest[loopOffsets - 1];
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(dest.AsSpan(loopOffsets), (ushort)writer);

                byte celCount = src[celCounts + loopTableIndex];
                dest[writer++] = celCount;
                dest[writer++] = 0;
                dest[writer++] = 0;
                dest[writer++] = 0;

                int celOffset = writer + 2 * celCount;
                for (int cel = 0; cel < celCount; cel++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(dest.AsSpan(writer), (ushort)celOffset);
                    writer += 2;
                    celOffset += 8 + celDecodedLengths[celTableIndex + cel];
                }

                for (int cel = 0; cel < celCount; cel++)
                {
                    Array.Copy(src, seeker, dest, writer, 7);
                    seeker += 7;
                    writer += 7;
                    dest[writer++] = 0;

                    int celDecodedLength = celDecodedLengths[celTableIndex + cel];
                    DecodeRle(src, ref rleData, ref pixelData, dest, writer, celDecodedLength);
                    writer += celDecodedLength;
                }

                celTableIndex += celCount;
                loopTableIndex++;
            }

            loopOffsets += 2;
        }

        if (paletteOffset != 0)
        {
            dest[writer++] = (byte)'P';
            dest[writer++] = (byte)'A';
            dest[writer++] = (byte)'L';

            for (int i = 0; i < 256; i++)
            {
                dest[writer++] = (byte)i;
            }

            // Palette time stamp and colours. The unrelated four bytes before the palette end up
            // in the time stamp, as Sierra's decompressor did; the interpreter ignores the field.
            Array.Copy(src, seeker - 4, dest, writer, Math.Min(4 + 1024, dest.Length - writer));
        }
    }

    private static void UnpackPic(byte[] src, byte[] dest, int unpackedSize)
    {
        int seeker = 0;
        int writer = 0;

        // set-palette instruction
        dest[writer++] = 0xFE; // PIC_OP_OPX
        dest[writer++] = 0x02; // PIC_OPX_VGA_SET_PALETTE

        for (int i = 0; i < 256; i++)
        {
            dest[writer++] = (byte)i;
        }

        // palette time stamp
        writer += 4;

        // compressed header
        ushort viewCelSize = BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(seeker));
        seeker += 2;
        ushort viewOpPos = BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(seeker)); // offset in the output picture
        seeker += 2;
        ushort viewPixelDataSize = BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(seeker));
        seeker += 2;
        int viewHeader = seeker;
        seeker += 7;

        // palette colours
        Array.Copy(src, seeker, dest, writer, 1024);
        seeker += 1024;
        writer += 1024;

        // optional data before the embedded view instruction
        if (viewOpPos != 1286)
        {
            int preViewDataSize = viewOpPos - 1286;
            Array.Copy(src, seeker, dest, writer, preViewDataSize);
            seeker += preViewDataSize;
            writer += preViewDataSize;
        }

        // optional data after the embedded view instruction, written to the end
        int postViewDataPosition = viewOpPos + 15 + viewCelSize;
        if (unpackedSize != postViewDataPosition)
        {
            int postViewDataSize = unpackedSize - postViewDataPosition;
            Array.Copy(src, seeker, dest, postViewDataPosition, postViewDataSize);
            seeker += postViewDataSize;
        }

        // embedded view instruction
        dest[writer++] = 0xFE; // PIC_OP_OPX
        dest[writer++] = 0x01; // PIC_OPX_VGA_EMBEDDED_VIEW

        // coordinates (0, 0)
        writer += 3;

        BinaryPrimitives.WriteUInt16LittleEndian(dest.AsSpan(writer), (ushort)(8 + viewCelSize));
        writer += 2;

        Array.Copy(src, viewHeader, dest, writer, 7);
        writer += 7;
        dest[writer++] = 0;

        // the view cel is stored as pixel data followed by RLE data
        int viewRleData = seeker + viewPixelDataSize;
        int pixelData = seeker;
        DecodeRle(src, ref viewRleData, ref pixelData, dest, writer, viewCelSize);
    }

    /// <summary>Decodes a cel from separate RLE and pixel streams.</summary>
    private static void DecodeRle(byte[] src, ref int rleData, ref int pixelData, byte[] dest, int destPos, int decodedSize)
    {
        int pos = 0;
        while (pos < decodedSize)
        {
            byte current = src[rleData++];
            dest[destPos++] = current;
            pos++;

            int upperBits = current & 0xC0;
            if (upperBits <= 0x40)
            {
                Array.Copy(src, pixelData, dest, destPos, current);
                pixelData += current;
                destPos += current;
                pos += current;
            }
            else if (upperBits == 0x80)
            {
                dest[destPos++] = src[pixelData++];
                pos++;
            }
        }
    }

    private static void SkipRle(byte[] src, ref int rleData, int decodedSize)
    {
        int pos = 0;
        while (pos < decodedSize)
        {
            byte current = src[rleData++];
            pos++;

            int upperBits = current & 0xC0;
            if (upperBits <= 0x40)
            {
                pos += current;
            }
            else if (upperBits == 0x80)
            {
                pos++;
            }
        }
    }
}

/// <summary>DCL implode decompressor used by SCI1.1 and later. Port of ScummVM's common/compression/dcl.cpp.</summary>
public sealed class DecompressorDcl : Decompressor
{
    private const int BinaryMode = 0;
    private const int AsciiMode = 1;
    private const int MaximumDictionarySize = 4096;

    public override SciResourceError Unpack(Stream src, byte[] dest, uint packed, uint unpacked)
    {
        Init(src, dest, packed, unpacked);

        var dictionary = new byte[MaximumDictionarySize];
        int dictionaryPos = 0;

        byte mode = GetByteLsb();
        byte dictionaryType = GetByteLsb();
        if (mode != BinaryMode && mode != AsciiMode)
        {
            return SciResourceError.DecompressionError;
        }

        int dictionarySize = dictionaryType switch
        {
            4 => 1024,
            5 => 2048,
            6 => 4096,
            _ => 0,
        };
        if (dictionarySize == 0)
        {
            return SciResourceError.DecompressionError;
        }

        int dictionaryMask = dictionarySize - 1;

        while (Written < UnpackedSize)
        {
            if (GetBitsLsb(1) != 0)
            {
                // (length, distance) pair
                int value = HuffmanLookup(DclTables.LengthTree);
                int tokenLength = value < 8 ? value + 2 : 8 + (1 << (value - 7)) + (int)GetBitsLsb(value - 7);
                if (tokenLength == 519)
                {
                    break; // end of stream
                }

                value = HuffmanLookup(DclTables.DistanceTree);
                int tokenOffset = tokenLength == 2
                    ? (value << 2) | (int)GetBitsLsb(2)
                    : (value << dictionaryType) | (int)GetBitsLsb(dictionaryType);
                tokenOffset++;

                if (tokenLength + Written > UnpackedSize || Written < tokenOffset)
                {
                    return SciResourceError.DecompressionError;
                }

                int dictionaryBaseIndex = (dictionaryPos - tokenOffset) & dictionaryMask;
                int dictionaryIndex = dictionaryBaseIndex;
                int dictionaryNextIndex = dictionaryPos;
                while (tokenLength > 0)
                {
                    PutByte(dictionary[dictionaryIndex]);
                    dictionary[dictionaryNextIndex] = dictionary[dictionaryIndex];
                    dictionaryNextIndex = (dictionaryNextIndex + 1) & dictionaryMask;
                    dictionaryIndex = (dictionaryIndex + 1) & dictionaryMask;
                    if (dictionaryIndex == dictionaryPos)
                    {
                        dictionaryIndex = dictionaryBaseIndex;
                    }

                    if (dictionaryNextIndex == dictionarySize)
                    {
                        dictionaryNextIndex = 0;
                    }

                    tokenLength--;
                }

                dictionaryPos = dictionaryNextIndex;
            }
            else
            {
                // literal
                byte value = mode == AsciiMode ? (byte)HuffmanLookup(DclTables.AsciiTree) : GetByteLsb();
                PutByte(value);
                dictionary[dictionaryPos] = value;
                dictionaryPos++;
                if (dictionaryPos >= dictionarySize)
                {
                    dictionaryPos = 0;
                }
            }
        }

        return Written == UnpackedSize ? SciResourceError.None : SciResourceError.DecompressionError;
    }

    private int HuffmanLookup(int[] tree)
    {
        int pos = 0;
        while ((tree[pos] & DclTables.HuffmanLeaf) == 0)
        {
            int bit = (int)GetBitsLsb(1);
            pos = bit != 0 ? tree[pos] & 0xFFF : tree[pos] >> 12;
        }

        return tree[pos] & 0xFFFF;
    }
}

/// <summary>STACpack (LZS) decompressor used by SCI32. Port of ScummVM's DecompressorLZS.</summary>
public sealed class DecompressorLzs : Decompressor
{
    public override SciResourceError Unpack(Stream src, byte[] dest, uint packed, uint unpacked)
    {
        Init(src, dest, packed, unpacked);

        while (!IsFinished)
        {
            if (GetBitsMsb(1) != 0)
            {
                int offset;
                if (GetBitsMsb(1) != 0)
                {
                    offset = (int)GetBitsMsb(7);
                    if (offset == 0)
                    {
                        break; // end marker
                    }
                }
                else
                {
                    offset = (int)GetBitsMsb(11);
                }

                uint length = GetCompLen();
                if (length == 0)
                {
                    return SciResourceError.DecompressionError;
                }

                int hpos = (int)Written - offset;
                while (length-- > 0)
                {
                    PutByte(Dest[hpos++]);
                }
            }
            else
            {
                PutByte(GetByteMsb());
            }
        }

        return Written == UnpackedSize ? SciResourceError.None : SciResourceError.DecompressionError;
    }

    private uint GetCompLen()
    {
        switch (GetBitsMsb(2))
        {
            case 0:
                return 2;
            case 1:
                return 3;
            case 2:
                return 4;
            default:
                switch (GetBitsMsb(2))
                {
                    case 0:
                        return 5;
                    case 1:
                        return 6;
                    case 2:
                        return 7;
                    default:
                    {
                        uint length = 8;
                        uint nibble;
                        do
                        {
                            nibble = GetBitsMsb(4);
                            length += nibble;
                        }
                        while (nibble == 0xF);

                        return length;
                    }
                }
        }
    }
}
