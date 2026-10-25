using System.Buffers.Binary;
using Daventry.Core.Hosting;
using Daventry.Sci.Sound;

namespace Daventry.Sci.Video;

/// <summary>
/// Phantasmagoria 2's Duck videos: AVI files with a TrueMotion 1 video stream and a Duck DK3
/// ADPCM audio stream. The part of ScummVM's <c>Video::AVIDecoder</c> these files use: the
/// header, the movie list read chunk by chunk, the first frame decoded to learn the size
/// (<c>checkTruemotion1</c>), and the audio queued three chunks ahead of the video. Like the VMD
/// decoder it keeps time with the engine's clock.
/// </summary>
public sealed class DuckDecoder(Func<long> millis)
{
    private const uint IdRiff = 0x52494646; // 'RIFF'
    private const uint IdAvi = 0x41564920; // 'AVI '
    private const uint IdList = 0x4C495354; // 'LIST'
    private const uint IdHdrl = 0x6864726C;
    private const uint IdMovi = 0x6D6F7669;
    private const uint IdRec = 0x72656320; // 'rec '
    private const uint IdInfo = 0x494E464F;
    private const uint IdAvih = 0x61766968;
    private const uint IdStrh = 0x73747268;
    private const uint IdStrf = 0x73747266;
    private const uint IdJunk = 0x4A554E4B;
    private const uint IdIdx1 = 0x69647831;
    private const uint IdVids = 0x76696473;
    private const uint IdAuds = 0x61756473;
    private const uint IdDuck = 0x4455434B; // 'DUCK': TrueMotion 1
    private const ushort WaveFormatDk3 = 0x62;

    private readonly Func<long> _millis = millis;
    private byte[] _data = [];
    private int _movieListStart;
    private int _movieListEnd;
    private int _videoStream = -1;
    private int _audioStream = -1;
    private int _videoSearchOffset;
    private int _audioSearchOffset;
    private int _audioChunks;
    private int _frameRateNum = 15;
    private int _frameRateDen = 1;
    private int _audioRate;
    private int _audioBlockAlign;
    private bool _audioIsDk3;
    private TrueMotion1Decoder _codec = new();
    private long _startTime;

    public bool IsLoaded { get; private set; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>The frame count from the AVI header.</summary>
    public int FrameCount { get; private set; }

    public int CurFrame { get; private set; } = -1;

    public bool IsPlaying { get; private set; }

    /// <summary>The video's sound, or null when it has none the player can decode.</summary>
    public DuckAudioQueue? Audio { get; private set; }

    /// <summary>The last decoded picture.</summary>
    public TrueMotion1Decoder Codec => _codec;

    public bool Load(byte[] data)
    {
        Close();
        if (data.Length < 12 || ReadTag(data, 0) != IdRiff || ReadTag(data, 8) != IdAvi)
        {
            return false;
        }

        _data = data;
        int fileSize = (int)Math.Min((uint)data.Length, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)) + 8L);
        bool decodedHeader = false;
        int streamCount = 0;
        int pos = 12;
        ParseChunks(ref pos, fileSize, ref decodedHeader, ref streamCount);

        if (!decodedHeader || _movieListEnd == 0 || _videoStream < 0)
        {
            Close();
            return false;
        }

        _videoSearchOffset = _audioSearchOffset = _movieListStart;
        IsLoaded = true;

        // the frame's own size, from its header (checkTruemotion1), then back to the start
        if (DecodeNextFrame() && _codec.Pairs is not null)
        {
            Width = _codec.Width;
            Height = _codec.Height;
        }

        Rewind();
        return Width > 0 && Height > 0;
    }

    private void ParseChunks(ref int pos, int end, ref bool decodedHeader, ref int streamCount)
    {
        while (pos + 8 <= end)
        {
            uint tag = ReadTag(_data, pos);
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(pos + 4));
            int body = pos + 8;
            int next = body + size + (size & 1);
            if (tag == IdList && body + 4 <= end)
            {
                uint listType = ReadTag(_data, body);
                if (listType == IdMovi)
                {
                    _movieListStart = body + 4;
                    _movieListEnd = Math.Min(_movieListStart + (size - 4) + ((size - 4) & 1), _data.Length);
                }
                else if (listType != IdInfo)
                {
                    decodedHeader |= listType == IdHdrl;
                    int inner = body + 4;
                    ParseChunks(ref inner, Math.Min(body + size, end), ref decodedHeader, ref streamCount);
                }
            }
            else if (tag == IdAvih && size >= 20)
            {
                FrameCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(body + 16));
            }
            else if (tag == IdStrh && size >= 48)
            {
                HandleStreamHeader(body, size, streamCount++);
            }

            pos = next;
        }
    }

    private void HandleStreamHeader(int body, int size, int streamIndex)
    {
        uint streamType = ReadTag(_data, body);
        uint handler = ReadTag(_data, body + 4);
        int scale = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(body + 20));
        int rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(body + 24));

        // the stream format follows the stream header
        int strf = body + size + (size & 1);
        if (strf + 8 > _data.Length || ReadTag(_data, strf) != IdStrf)
        {
            return;
        }

        int format = strf + 8;
        if (streamType == IdVids && _videoStream < 0)
        {
            uint compression = format + 20 <= _data.Length ? ReadTag(_data, format + 16) : 0;
            if (handler == IdDuck || compression == IdDuck)
            {
                _videoStream = streamIndex;
                if (rate > 0 && scale > 0)
                {
                    _frameRateNum = rate;
                    _frameRateDen = scale;
                }
            }
        }
        else if (streamType == IdAuds && _audioStream < 0 && format + 16 <= _data.Length)
        {
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(format));
            ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(format + 2));
            _audioStream = streamIndex;
            _audioRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(format + 4));
            _audioBlockAlign = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(format + 12));

            // DK3 only works as a stereo stream; other formats play silent here
            _audioIsDk3 = tag == WaveFormatDk3 && channels == 2 && _audioBlockAlign >= 16;
        }
    }

    public void Close()
    {
        IsLoaded = false;
        IsPlaying = false;
        _data = [];
        _movieListStart = _movieListEnd = 0;
        _videoStream = _audioStream = -1;
        _audioChunks = 0;
        _frameRateNum = 15;
        _frameRateDen = 1;
        _audioIsDk3 = false;
        Width = Height = FrameCount = 0;
        CurFrame = -1;
        Audio = null;
        _codec = new TrueMotion1Decoder();
    }

    private void Rewind()
    {
        _videoSearchOffset = _audioSearchOffset = _movieListStart;
        _audioChunks = 0;
        CurFrame = -1;
        _codec = new TrueMotion1Decoder();
        Audio = _audioIsDk3 ? new DuckAudioQueue(_audioRate) : null;
    }

    // ---- playback (VideoDecoder) ----

    public void Start()
    {
        if (IsPlaying)
        {
            return;
        }

        IsPlaying = true;
        _startTime = _millis();
    }

    /// <summary>The end of the video track; ScummVM also lets the last of the sound play out.</summary>
    public bool EndOfVideo => !IsLoaded || CurFrame >= FrameCount - 1;

    /// <summary>Milliseconds until the next frame is due; 0 when it is.</summary>
    public uint TimeToNextFrame
    {
        get
        {
            if (EndOfVideo || !IsPlaying)
            {
                return 0;
            }

            long elapsed = _millis() - _startTime;
            long nextFrameStartTime = (CurFrame + 1) * 1000L * _frameRateDen / _frameRateNum;
            return nextFrameStartTime <= elapsed ? 0 : (uint)(nextFrameStartTime - elapsed);
        }
    }

    public bool NeedsUpdate => IsPlaying && !EndOfVideo && TimeToNextFrame == 0;

    /// <summary>
    /// Reads the next video chunk and decodes it, then queues the sound up to three chunks ahead
    /// (readNextPacket). Returns whether the frame has a picture: some carry only sound.
    /// </summary>
    public bool DecodeNextFrame()
    {
        if (!IsLoaded)
        {
            return false;
        }

        bool picture = false;
        if (NextChunk(ref _videoSearchOffset, _videoStream, out int offset, out int size, out bool palette))
        {
            if (!palette)
            {
                // an empty chunk is a frame without a picture
                picture = size > 0 && _codec.DecodeFrame(_data.AsSpan(offset, size).ToArray());
                CurFrame++;
            }
        }
        else
        {
            // a premature end: the last frame it is
            CurFrame = Math.Max(CurFrame, FrameCount - 1);
        }

        QueueAudio();
        return picture;
    }

    private void QueueAudio()
    {
        if (_audioStream < 0 || Audio is null)
        {
            return;
        }

        // being three frames ahead should be enough for any video; once the video is done the
        // rest of the sound is queued
        while (EndOfVideo || _audioChunks < CurFrame + 3)
        {
            if (!NextChunk(ref _audioSearchOffset, _audioStream, out int offset, out int size, out _))
            {
                Audio.Finish();
                return;
            }

            _audioChunks++;
            Audio.Enqueue(Dk3Adpcm.Decode(_data.AsSpan(offset, size), _audioBlockAlign));
        }
    }

    /// <summary>
    /// Finds the next chunk of a stream from <paramref name="searchOffset"/>, skipping 'rec '
    /// lists, JUNK, the index and the other streams' chunks (handleNextPacket).
    /// </summary>
    private bool NextChunk(ref int searchOffset, int stream, out int offset, out int size, out bool palette)
    {
        offset = size = 0;
        palette = false;
        int pos = searchOffset;
        while (pos + 8 < _movieListEnd)
        {
            uint tag = ReadTag(_data, pos);
            int chunkSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(pos + 4));
            if (tag == IdList)
            {
                // a list of audio and video chunks: step into it
                pos += 12;
                continue;
            }

            int next = pos + 8 + chunkSize + (chunkSize & 1);
            if (tag == IdJunk || tag == IdIdx1 || StreamIndex(tag) != stream)
            {
                pos = next;
                continue;
            }

            offset = pos + 8;
            size = Math.Max(0, Math.Min(chunkSize, _data.Length - offset));
            palette = (tag & 0xFFFF) == 0x7063; // 'pc': a palette change, not used by TrueMotion
            searchOffset = next;
            return true;
        }

        searchOffset = _movieListEnd;
        return false;
    }

    /// <summary>The stream number of a chunk tag such as '00dc' or '01wb', -1 if it has none.</summary>
    private static int StreamIndex(uint tag)
    {
        int hi = HexDigit((byte)(tag >> 24));
        int lo = HexDigit((byte)(tag >> 16));
        return hi < 0 || lo < 0 ? -1 : hi * 16 + lo;
    }

    private static int HexDigit(byte c) => c switch
    {
        >= (byte)'0' and <= (byte)'9' => c - '0',
        >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
        _ => -1,
    };

    private static uint ReadTag(byte[] data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
}

/// <summary>
/// Duck DK3 IMA ADPCM, stereo only: per block a header with the sum and difference channels'
/// predictors and step indexes, then nibbles decoding sum, difference, sum for every two sample
/// pairs. Port of ScummVM's <c>DK3_ADPCMStream</c> (from FFmpeg); each AVI chunk is decoded on its
/// own, as ScummVM's packetized stream does.
/// </summary>
public static class Dk3Adpcm
{
    private static readonly short[] ImaTable =
    [
            7,     8,     9,    10,    11,    12,    13,    14,
           16,    17,    19,    21,    23,    25,    28,    31,
           34,    37,    41,    45,    50,    55,    60,    66,
           73,    80,    88,    97,   107,   118,   130,   143,
          157,   173,   190,   209,   230,   253,   279,   307,
          337,   371,   408,   449,   494,   544,   598,   658,
          724,   796,   876,   963,  1060,  1166,  1282,  1411,
         1552,  1707,  1878,  2066,  2272,  2499,  2749,  3024,
         3327,  3660,  4026,  4428,  4871,  5358,  5894,  6484,
         7132,  7845,  8630,  9493, 10442, 11487, 12635, 13899,
        15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794,
        32767,
    ];

    private static readonly short[] StepAdjustTable = [-1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8];

    private struct Channel
    {
        public int Last;
        public int StepIndex;
    }

    /// <summary>Decodes a chunk of whole blocks to interleaved stereo samples.</summary>
    public static short[] Decode(ReadOnlySpan<byte> data, int blockAlign)
    {
        var samples = new List<short>(data.Length * 4);
        Span<Channel> channels = stackalloc Channel[2];
        int pos = 0;
        int audioBytesLeft = data.Length;
        int blockBytesLeft = 0;
        bool topNibble = false;
        byte lastByte = 0;

        while (audioBytesLeft > 0)
        {
            if (blockBytesLeft == 0)
            {
                blockBytesLeft = Math.Min(blockAlign, audioBytesLeft);
                topNibble = false;
                if (blockBytesLeft < 16)
                {
                    // ScummVM: "Truncated DK3 ADPCM block header"
                    break;
                }

                // 2 bytes, the rate, 6 bytes, then the predictors and the step indexes of the
                // sum and difference channels
                channels[0].Last = BinaryPrimitives.ReadInt16LittleEndian(data[(pos + 10)..]);
                channels[1].Last = BinaryPrimitives.ReadInt16LittleEndian(data[(pos + 12)..]);
                channels[0].StepIndex = Math.Min((int)data[pos + 14], ImaTable.Length - 1);
                channels[1].StepIndex = Math.Min((int)data[pos + 15], ImaTable.Length - 1);
                pos += 16;
                blockBytesLeft -= 16;
                audioBytesLeft -= 16;
            }

            ReadNibble(data, channels, 0, ref pos, ref topNibble, ref lastByte, ref blockBytesLeft, ref audioBytesLeft);
            ReadNibble(data, channels, 1, ref pos, ref topNibble, ref lastByte, ref blockBytesLeft, ref audioBytesLeft);
            samples.Add((short)(channels[0].Last + channels[1].Last));
            samples.Add((short)(channels[0].Last - channels[1].Last));

            ReadNibble(data, channels, 0, ref pos, ref topNibble, ref lastByte, ref blockBytesLeft, ref audioBytesLeft);
            samples.Add((short)(channels[0].Last + channels[1].Last));
            samples.Add((short)(channels[0].Last - channels[1].Last));

            // if the last sample of a block ends on an odd byte, the encoder adds an extra
            // alignment byte
            if (!topNibble && blockBytesLeft == 1)
            {
                pos++;
                blockBytesLeft--;
                audioBytesLeft--;
            }
        }

        return [.. samples];
    }

    private static void ReadNibble(ReadOnlySpan<byte> data, Span<Channel> channels, int channel, ref int pos, ref bool topNibble, ref byte lastByte, ref int blockBytesLeft, ref int audioBytesLeft)
    {
        byte nibble;
        if (topNibble)
        {
            nibble = (byte)(lastByte >> 4);
            topNibble = false;
        }
        else
        {
            // past the end the C++ stream reads zeros
            lastByte = pos < data.Length ? data[pos] : (byte)0;
            pos++;
            nibble = (byte)(lastByte & 0xF);
            topNibble = true;
            blockBytesLeft--;
            audioBytesLeft--;
        }

        DecodeIma(ref channels[channel], nibble);
    }

    /// <summary>Ima_ADPCMStream::decodeIMA.</summary>
    private static void DecodeIma(ref Channel channel, byte code)
    {
        int e = (2 * (code & 0x7) + 1) * ImaTable[channel.StepIndex] >> 3;
        int diff = (code & 0x08) != 0 ? -e : e;
        channel.Last = Math.Clamp(channel.Last + diff, -32768, 32767);
        channel.StepIndex = Math.Clamp(channel.StepIndex + StepAdjustTable[code], 0, ImaTable.Length - 1);
    }
}

/// <summary>The decoded sound of a Duck video, queued chunk by chunk and read by the mixer.</summary>
public sealed class DuckAudioQueue(int rate) : IAudioStream32
{
    private readonly Lock _lock = new();
    private readonly Queue<short[]> _queue = new();
    private short[]? _current;
    private int _currentPos;
    private bool _finished;

    public int Rate { get; } = rate;

    public bool IsStereo => true;

    public bool EndOfData
    {
        get
        {
            lock (_lock)
            {
                return (_current is null || _currentPos >= _current.Length) && _queue.Count == 0;
            }
        }
    }

    public bool EndOfStream
    {
        get
        {
            lock (_lock)
            {
                return _finished && (_current is null || _currentPos >= _current.Length) && _queue.Count == 0;
            }
        }
    }

    public void Enqueue(short[] samples)
    {
        if (samples.Length == 0)
        {
            return;
        }

        lock (_lock)
        {
            _queue.Enqueue(samples);
        }
    }

    /// <summary>No more chunks will come.</summary>
    public void Finish()
    {
        lock (_lock)
        {
            _finished = true;
        }
    }

    public int ReadBuffer(Span<short> buffer)
    {
        lock (_lock)
        {
            int written = 0;
            while (written < buffer.Length)
            {
                if (_current is null || _currentPos >= _current.Length)
                {
                    if (!_queue.TryDequeue(out _current))
                    {
                        _current = null;
                        break;
                    }

                    _currentPos = 0;
                }

                int count = Math.Min(buffer.Length - written, _current.Length - _currentPos);
                _current.AsSpan(_currentPos, count).CopyTo(buffer[written..]);
                written += count;
                _currentPos += count;
            }

            return written;
        }
    }
}

/// <summary>Plays a Duck video's sound into the mixer, converted to the output rate.</summary>
public sealed class DuckAudioSource(DuckAudioQueue queue, int outputRate) : IAudioSource
{
    private readonly RateConverter _converter = new(queue.Rate, outputRate, inputStereo: true);

    /// <summary>The volume, 0 to 255 (ScummVM's channel volume).</summary>
    public int Volume { get; set; } = 255;

    public void Read(Span<short> interleavedStereo)
    {
        interleavedStereo.Clear();
        _converter.Convert(queue, interleavedStereo, interleavedStereo.Length / 2, Volume, Volume);
    }
}
