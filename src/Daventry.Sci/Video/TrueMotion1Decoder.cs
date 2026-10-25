using System.Buffers.Binary;

namespace Daventry.Sci.Video;

/// <summary>
/// The Duck TrueMotion 1 video codec of Phantasmagoria 2's Duck videos. Port of ScummVM's
/// <c>Image::TrueMotion1Decoder</c>, itself based on FFmpeg's. Frames decode to RGB565, kept as
/// pairs of pixels in 32-bit words (the left pixel in the low half) as the predictors add them.
/// </summary>
public sealed class TrueMotion1Decoder
{
    private const int FlagSprite = 1 << 5;
    private const int FlagKeyframe = 1 << 4;
    private const int FlagInterframe = 1 << 3;
    private const int FlagInterpolated = 1 << 2;

    private const int AlgoNop = 0;
    private const int AlgoRgb16V = 1;
    private const int AlgoRgb16H = 2;
    private const int AlgoRgb24H = 3;

    private const int Block2x2 = 0;
    private const int Block2x4 = 1;
    private const int Block4x2 = 2;
    private const int Block4x4 = 3;

    // algorithm, block width (vres), block height (hres), block type
    private static readonly (int Algorithm, int BlockWidth, int BlockHeight, int BlockType)[] CompressionTypes =
    [
        (AlgoNop, 0, 0, 0),

        (AlgoRgb16V, 4, 4, Block4x4),
        (AlgoRgb16H, 4, 4, Block4x4),
        (AlgoRgb16V, 4, 2, Block4x2),
        (AlgoRgb16H, 4, 2, Block4x2),

        (AlgoRgb16V, 2, 4, Block2x4),
        (AlgoRgb16H, 2, 4, Block2x4),
        (AlgoRgb16V, 2, 2, Block2x2),
        (AlgoRgb16H, 2, 2, Block2x2),

        (AlgoNop, 4, 4, Block4x4),
        (AlgoRgb24H, 4, 4, Block4x4),
        (AlgoNop, 4, 2, Block4x2),
        (AlgoRgb24H, 4, 2, Block4x2),

        (AlgoNop, 2, 4, Block2x4),
        (AlgoRgb24H, 2, 4, Block2x4),
        (AlgoNop, 2, 2, Block2x2),
        (AlgoRgb24H, 2, 2, Block2x2),
    ];

    private struct PredictorTableEntry
    {
        public uint Color;
        public bool GetNextIndex;
    }

    private readonly PredictorTableEntry[] _yPredictorTable = new PredictorTableEntry[1024];
    private readonly PredictorTableEntry[] _cPredictorTable = new PredictorTableEntry[1024];
    private readonly short[] _ydt = new short[8];
    private readonly short[] _cdt = new short[8];
    private int _lastDeltaset = -1;
    private int _lastVectable = -1;

    private byte[] _buf = [];
    private int _mbChangeBits; // offsets into _buf
    private int _indexStream;
    private int _indexStreamSize;
    private int _mbChangeBitsRowSize;
    private int _flags;
    private int _blockType;
    private int _blockWidth;
    private uint[]? _vertPred;

    private byte _headerSize;
    private byte _compression;
    private byte _deltaset;
    private byte _vectable;
    private byte _version;
    private byte _headerType;
    private byte _headerFlags;

    /// <summary>The decoded frame as RGB565 pixel pairs, <see cref="Width"/> / 2 words a row.</summary>
    public uint[]? Pairs { get; private set; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>
    /// Decodes a frame. Returns false for a frame without a picture (the NOP compression types,
    /// and the 24-bit ones ScummVM does not decode either), which leaves the last picture as it was.
    /// </summary>
    public bool DecodeFrame(byte[] data)
    {
        DecodeHeader(data);

        var algorithm = CompressionTypes[_compression].Algorithm;
        if (algorithm == AlgoNop)
        {
            return false;
        }

        if (algorithm == AlgoRgb24H)
        {
            // ScummVM: "Unhandled TrueMotion1 24bpp frame"
            return false;
        }

        Decode16();
        return true;
    }

    /// <summary>Writes the frame as XRGB8888 pixels, a row of <see cref="Width"/> at a time.</summary>
    public void CopyTo(Span<uint> target)
    {
        if (Pairs is null)
        {
            return;
        }

        for (int i = 0; i < Pairs.Length; i++)
        {
            uint pair = Pairs[i];
            target[i * 2] = Rgb565ToXrgb((ushort)pair);
            target[i * 2 + 1] = Rgb565ToXrgb((ushort)(pair >> 16));
        }
    }

    /// <summary>RGB565 to XRGB8888, the components widened by repeating their top bits as ScummVM's PixelFormat does.</summary>
    public static uint Rgb565ToXrgb(ushort color)
    {
        uint r = (uint)(color >> 11) & 0x1F;
        uint g = (uint)(color >> 5) & 0x3F;
        uint b = (uint)color & 0x1F;
        r = (r << 3) | (r >> 2);
        g = (g << 2) | (g >> 4);
        b = (b << 3) | (b >> 2);
        return (r << 16) | (g << 8) | b;
    }

    private void SelectDeltaTables(int deltaTableIndex)
    {
        if (deltaTableIndex > 3)
        {
            return;
        }

        for (int i = 0; i < 8; i++)
        {
            _ydt[i] = TrueMotion1Data.Ydts[deltaTableIndex][i];
            _cdt[i] = TrueMotion1Data.Cdts[deltaTableIndex][i];

            // Y skinny deltas need to be halved for some reason; maybe the skinny Y deltas should
            // be modified. Drop the lsb before dividing by 2-- net effect: round down when
            // dividing a negative number (e.g., -3/2 = -2, not -1)
            _ydt[i] = (short)(_ydt[i] & 0xFFFE);
            _ydt[i] /= 2;
        }
    }

    private uint MakeYdt16Entry(int p1, int p2)
    {
        int lo = _ydt[p1];
        lo += (lo << 6) + (lo << 11);
        int hi = _ydt[p2];
        hi += (hi << 6) + (hi << 11);
        return (uint)(lo + (hi << 16));
    }

    private uint MakeCdt16Entry(int p1, int p2)
    {
        int b = _cdt[p2];
        int r = _cdt[p1] << 11;
        int lo = b + r;
        return (uint)(lo + (lo << 16));
    }

    private void GenVectorTable16(byte[] selVectorTable)
    {
        Array.Clear(_yPredictorTable);
        Array.Clear(_cPredictorTable);

        int pos = 0;
        for (int i = 0; i < 1024; i += 4)
        {
            int len = selVectorTable[pos++] / 2;
            for (int j = 0; j < len; j++)
            {
                byte deltaPair = selVectorTable[pos++];
                _yPredictorTable[i + j].Color = MakeYdt16Entry(deltaPair >> 4, deltaPair & 0xF);
                _cPredictorTable[i + j].Color = MakeCdt16Entry(deltaPair >> 4, deltaPair & 0xF);
            }

            _yPredictorTable[i + (len - 1)].GetNextIndex = true;
            _cPredictorTable[i + (len - 1)].GetNextIndex = true;
        }
    }

    private void DecodeHeader(byte[] data)
    {
        _buf = data;
        if (data.Length == 0)
        {
            throw new InvalidDataException("Empty TrueMotion1 frame");
        }

        _headerSize = (byte)(((data[0] >> 5) | (data[0] << 3)) & 0x7F);
        if (data[0] < 0x10)
        {
            throw new InvalidDataException($"Invalid TrueMotion1 header size {_headerSize}");
        }

        // unscramble the header bytes with a XOR operation
        Span<byte> header = stackalloc byte[128];
        header.Clear();
        for (int i = 1; i < _headerSize; i++)
        {
            header[i - 1] = (byte)(At(i) ^ At(i + 1));
        }

        _compression = header[0];
        _deltaset = header[1];
        _vectable = header[2];
        int ysize = BinaryPrimitives.ReadUInt16LittleEndian(header[3..]);
        int xsize = BinaryPrimitives.ReadUInt16LittleEndian(header[5..]);
        _version = header[9];
        _headerType = header[10];
        _headerFlags = header[11];

        // there is a vertical predictor for each pixel in a line; each vertical predictor is 0 to
        // start with
        if (_vertPred is null || Width != xsize || Height != ysize)
        {
            _vertPred = new uint[xsize];
            Pairs = new uint[xsize / 2 * ysize];
            Width = xsize;
            Height = ysize;
        }

        // There is 1 change bit per 4 pixels, so each change byte represents 32 pixels; divide
        // width by 4 to obtain the number of change bits and then round up to the nearest byte.
        _mbChangeBitsRowSize = ((xsize >> 2) + 7) >> 3;

        if (_version >= 2)
        {
            if (_headerType > 3)
            {
                throw new InvalidDataException($"Invalid header type {_headerType}");
            }
            else if (_headerType == 2 || _headerType == 3)
            {
                _flags = _headerFlags;
                if ((_flags & FlagInterframe) == 0)
                {
                    _flags |= FlagKeyframe;
                }
            }
            else
            {
                _flags = FlagKeyframe;
            }
        }
        else
        {
            _flags = FlagKeyframe;
        }

        if ((_flags & FlagSprite) != 0)
        {
            throw new InvalidDataException("SPRITE frame found, please report the sample to the developers");
        }
        else if (_headerType < 2 && xsize < 213 && ysize >= 176)
        {
            _flags |= FlagInterpolated;
            throw new InvalidDataException("INTERPOLATION selected, please report the sample to the developers");
        }

        if (_compression >= 17)
        {
            throw new InvalidDataException($"Invalid TrueMotion1 compression type {_compression}");
        }

        if (_deltaset != _lastDeltaset || _vectable != _lastVectable)
        {
            SelectDeltaTables(_deltaset);
        }

        byte[] selVectorTable;
        if ((_compression & 1) != 0 && _headerType != 0)
        {
            selVectorTable = TrueMotion1Data.PcTbl2;
        }
        else if (_vectable is >= 1 and < 4)
        {
            selVectorTable = TrueMotion1Data.Tables[_vectable - 1];
        }
        else
        {
            throw new InvalidDataException($"Invalid vector table id {_vectable}");
        }

        if (_deltaset != _lastDeltaset || _vectable != _lastVectable)
        {
            GenVectorTable16(selVectorTable);
        }

        // set up pointers to the other key data chunks
        _mbChangeBits = _headerSize;
        if ((_flags & FlagKeyframe) != 0)
        {
            // no change bits specified for a keyframe; only index bytes
            _indexStream = _mbChangeBits;
        }
        else
        {
            // one change bit per 4x4 block
            _indexStream = _mbChangeBits + _mbChangeBitsRowSize * (ysize >> 2);
        }

        _indexStreamSize = data.Length - _indexStream;

        _lastDeltaset = _deltaset;
        _lastVectable = _vectable;
        (_, _blockWidth, _, _blockType) = CompressionTypes[_compression];
    }

    /// <summary>A byte of the frame, 0 past its end (the C++ reads the buffer the same way, unchecked).</summary>
    private byte At(int offset) => (uint)offset < (uint)_buf.Length ? _buf[offset] : (byte)0;

    private void Decode16()
    {
        bool keyframe = (_flags & FlagKeyframe) != 0;
        int indexStreamIndex = 0;
        var pairs = Pairs!;
        var vertPredBuffer = _vertPred!;
        int pairsPerRow = Width / 2;

        // clean out the line buffer
        Array.Clear(vertPredBuffer);

        int index = GetNextIndex(ref indexStreamIndex);
        int mbChangeBits = _mbChangeBits;

        for (int y = 0; y < Height; y++)
        {
            // re-init variables for the next line iteration
            uint horizPred = 0;
            int currentPixelPair = y * pairsPerRow;
            int vertPred = 0;
            int mbChangeIndex = 0;
            byte mbChangeByte = At(mbChangeBits + mbChangeIndex++);
            byte mbChangeByteMask = 1;

            for (int pixelsLeft = Width; pixelsLeft > 0; pixelsLeft -= 4)
            {
                if (keyframe || (mbChangeByte & mbChangeByteMask) == 0)
                {
                    switch (y & 3)
                    {
                        case 0:
                            // if macroblock width is 2, apply C-Y-C-Y; else apply C-Y-Y
                            if (_blockWidth == 2)
                            {
                                ApplyPredictor(_cPredictorTable, ref index, ref horizPred, ref indexStreamIndex);
                                ApplyPredictor(_yPredictorTable, ref index, ref horizPred, ref indexStreamIndex);
                                OutputPixelPair(pairs, ref currentPixelPair, vertPredBuffer, ref vertPred, horizPred);
                                ApplyPredictor(_cPredictorTable, ref index, ref horizPred, ref indexStreamIndex);
                                ApplyPredictor(_yPredictorTable, ref index, ref horizPred, ref indexStreamIndex);
                                OutputPixelPair(pairs, ref currentPixelPair, vertPredBuffer, ref vertPred, horizPred);
                            }
                            else
                            {
                                ApplyPredictor(_cPredictorTable, ref index, ref horizPred, ref indexStreamIndex);
                                ApplyPredictor(_yPredictorTable, ref index, ref horizPred, ref indexStreamIndex);
                                OutputPixelPair(pairs, ref currentPixelPair, vertPredBuffer, ref vertPred, horizPred);
                                ApplyPredictor(_yPredictorTable, ref index, ref horizPred, ref indexStreamIndex);
                                OutputPixelPair(pairs, ref currentPixelPair, vertPredBuffer, ref vertPred, horizPred);
                            }

                            break;
                        case 1:
                        case 3:
                            // always apply 2 Y predictors on these iterations
                            ApplyPredictor(_yPredictorTable, ref index, ref horizPred, ref indexStreamIndex);
                            OutputPixelPair(pairs, ref currentPixelPair, vertPredBuffer, ref vertPred, horizPred);
                            ApplyPredictor(_yPredictorTable, ref index, ref horizPred, ref indexStreamIndex);
                            OutputPixelPair(pairs, ref currentPixelPair, vertPredBuffer, ref vertPred, horizPred);
                            break;
                        case 2:
                            // this iteration might be C-Y-C-Y, Y-Y, or C-Y-Y depending on the
                            // macroblock type
                            if (_blockType == Block2x2)
                            {
                                ApplyPredictor(_cPredictorTable, ref index, ref horizPred, ref indexStreamIndex);
                                ApplyPredictor(_yPredictorTable, ref index, ref horizPred, ref indexStreamIndex);
                                OutputPixelPair(pairs, ref currentPixelPair, vertPredBuffer, ref vertPred, horizPred);
                                ApplyPredictor(_cPredictorTable, ref index, ref horizPred, ref indexStreamIndex);
                                ApplyPredictor(_yPredictorTable, ref index, ref horizPred, ref indexStreamIndex);
                                OutputPixelPair(pairs, ref currentPixelPair, vertPredBuffer, ref vertPred, horizPred);
                            }
                            else if (_blockType == Block4x2)
                            {
                                ApplyPredictor(_cPredictorTable, ref index, ref horizPred, ref indexStreamIndex);
                                ApplyPredictor(_yPredictorTable, ref index, ref horizPred, ref indexStreamIndex);
                                OutputPixelPair(pairs, ref currentPixelPair, vertPredBuffer, ref vertPred, horizPred);
                                ApplyPredictor(_yPredictorTable, ref index, ref horizPred, ref indexStreamIndex);
                                OutputPixelPair(pairs, ref currentPixelPair, vertPredBuffer, ref vertPred, horizPred);
                            }
                            else
                            {
                                ApplyPredictor(_yPredictorTable, ref index, ref horizPred, ref indexStreamIndex);
                                OutputPixelPair(pairs, ref currentPixelPair, vertPredBuffer, ref vertPred, horizPred);
                                ApplyPredictor(_yPredictorTable, ref index, ref horizPred, ref indexStreamIndex);
                                OutputPixelPair(pairs, ref currentPixelPair, vertPredBuffer, ref vertPred, horizPred);
                            }

                            break;
                    }
                }
                else
                {
                    // skip (copy) four pixels, but reassign the horizontal predictor
                    vertPredBuffer[vertPred++] = pairs[currentPixelPair++];
                    horizPred = pairs[currentPixelPair] - vertPredBuffer[vertPred];
                    vertPredBuffer[vertPred++] = pairs[currentPixelPair++];
                }

                if (!keyframe)
                {
                    mbChangeByteMask <<= 1;

                    // next byte
                    if (mbChangeByteMask == 0)
                    {
                        mbChangeByte = At(mbChangeBits + mbChangeIndex++);
                        mbChangeByteMask = 1;
                    }
                }
            }

            // next change row
            if (((y + 1) & 3) == 0)
            {
                mbChangeBits += _mbChangeBitsRowSize;
            }
        }
    }

    private int GetNextIndex(ref int indexStreamIndex)
    {
        if (indexStreamIndex >= _indexStreamSize)
        {
            throw new InvalidDataException("TrueMotion1 decoder went out of bounds");
        }

        return _buf[_indexStream + indexStreamIndex++] * 4;
    }

    /// <summary>APPLY_C_PREDICTOR and APPLY_Y_PREDICTOR, which differ only in their table.</summary>
    private void ApplyPredictor(PredictorTableEntry[] table, ref int index, ref uint horizPred, ref int indexStreamIndex)
    {
        uint predictorPair = table[index].Color;
        horizPred += predictorPair;
        if (table[index].GetNextIndex)
        {
            index = GetNextIndex(ref indexStreamIndex);
            if (index == 0)
            {
                index = GetNextIndex(ref indexStreamIndex);
                predictorPair = table[index].Color;
                horizPred += predictorPair * 5;
                if (table[index].GetNextIndex)
                {
                    index = GetNextIndex(ref indexStreamIndex);
                }
                else
                {
                    index++;
                }
            }
        }
        else
        {
            index++;
        }
    }

    private static void OutputPixelPair(uint[] pairs, ref int currentPixelPair, uint[] vertPredBuffer, ref int vertPred, uint horizPred)
    {
        pairs[currentPixelPair] = vertPredBuffer[vertPred] + horizPred;
        vertPredBuffer[vertPred++] = pairs[currentPixelPair++];
    }
}
