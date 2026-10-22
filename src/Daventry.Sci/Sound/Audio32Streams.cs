using System.Buffers.Binary;
using Daventry.Core.Hosting;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Sound;

/// <summary>A stream of 16-bit samples at its own rate, mono or interleaved stereo, read in chunks.</summary>
public interface IAudioStream32
{
    /// <summary>Fills the buffer with samples (not frames) and returns how many were written.</summary>
    int ReadBuffer(Span<short> buffer);

    bool IsStereo { get; }

    int Rate { get; }

    /// <summary>No more samples can be read right now.</summary>
    bool EndOfData { get; }

    /// <summary>No more samples will ever come.</summary>
    bool EndOfStream { get; }
}

/// <summary>A stream that can start over, with a known length.</summary>
public interface IRewindableAudioStream32 : IAudioStream32
{
    bool Rewind();

    int LengthMs { get; }
}

/// <summary>Uncompressed PCM out of a byte array: 8 or 16 bit, signed or unsigned, mono or stereo.</summary>
public sealed class RawPcmStream : IRewindableAudioStream32
{
    private readonly byte[] _data;
    private readonly int _start;
    private readonly int _length;
    private readonly bool _is16Bit;
    private readonly bool _isUnsigned;
    private readonly bool _bigEndian;
    private int _pos;

    public RawPcmStream(byte[] data, int start, int length, int rate, bool is16Bit, bool isUnsigned, bool stereo, bool bigEndian = false)
    {
        _data = data;
        _start = start;
        _length = Math.Max(0, Math.Min(length, data.Length - start));
        _is16Bit = is16Bit;
        _isUnsigned = isUnsigned;
        _bigEndian = bigEndian;
        Rate = rate;
        IsStereo = stereo;
        if (is16Bit)
        {
            _length &= ~1;
        }
    }

    public bool IsStereo { get; }

    public int Rate { get; }

    public bool EndOfData => _pos >= _length;

    public bool EndOfStream => EndOfData;

    public int LengthMs
    {
        get
        {
            int bytesPerSample = _is16Bit ? 2 : 1;
            long samples = _length / bytesPerSample / (IsStereo ? 2 : 1);
            return (int)(samples * 1000 / Math.Max(Rate, 1));
        }
    }

    public bool Rewind()
    {
        _pos = 0;
        return true;
    }

    public int ReadBuffer(Span<short> buffer)
    {
        int written = 0;
        if (_is16Bit)
        {
            while (written < buffer.Length && _pos + 1 < _length)
            {
                var span = _data.AsSpan(_start + _pos, 2);
                short v = _bigEndian ? BinaryPrimitives.ReadInt16BigEndian(span) : BinaryPrimitives.ReadInt16LittleEndian(span);
                buffer[written++] = _isUnsigned ? (short)(v ^ unchecked((short)0x8000)) : v;
                _pos += 2;
            }
        }
        else
        {
            while (written < buffer.Length && _pos < _length)
            {
                byte b = _data[_start + _pos++];
                int v = _isUnsigned ? b - 128 : (sbyte)b;
                buffer[written++] = (short)(v << 8);
            }
        }

        return written;
    }
}

/// <summary>
/// Sierra's SOL audio: DPCM compressed 8 or 16-bit samples, mono or stereo. The compression halves
/// the data; 8-bit nibbles come in an "old" order before SCI2.1 early.
/// </summary>
public sealed class SolStream : IRewindableAudioStream32
{
    private const byte FlagCompressed = 1;
    private const byte Flag16Bit = 4;
    private const byte FlagStereo = 16;
    private const int RepairSlope = 12;

    private static readonly ushort[] TableDpcm16 =
    [
        0x0000, 0x0008, 0x0010, 0x0020, 0x0030, 0x0040, 0x0050, 0x0060, 0x0070, 0x0080,
        0x0090, 0x00A0, 0x00B0, 0x00C0, 0x00D0, 0x00E0, 0x00F0, 0x0100, 0x0110, 0x0120,
        0x0130, 0x0140, 0x0150, 0x0160, 0x0170, 0x0180, 0x0190, 0x01A0, 0x01B0, 0x01C0,
        0x01D0, 0x01E0, 0x01F0, 0x0200, 0x0208, 0x0210, 0x0218, 0x0220, 0x0228, 0x0230,
        0x0238, 0x0240, 0x0248, 0x0250, 0x0258, 0x0260, 0x0268, 0x0270, 0x0278, 0x0280,
        0x0288, 0x0290, 0x0298, 0x02A0, 0x02A8, 0x02B0, 0x02B8, 0x02C0, 0x02C8, 0x02D0,
        0x02D8, 0x02E0, 0x02E8, 0x02F0, 0x02F8, 0x0300, 0x0308, 0x0310, 0x0318, 0x0320,
        0x0328, 0x0330, 0x0338, 0x0340, 0x0348, 0x0350, 0x0358, 0x0360, 0x0368, 0x0370,
        0x0378, 0x0380, 0x0388, 0x0390, 0x0398, 0x03A0, 0x03A8, 0x03B0, 0x03B8, 0x03C0,
        0x03C8, 0x03D0, 0x03D8, 0x03E0, 0x03E8, 0x03F0, 0x03F8, 0x0400, 0x0440, 0x0480,
        0x04C0, 0x0500, 0x0540, 0x0580, 0x05C0, 0x0600, 0x0640, 0x0680, 0x06C0, 0x0700,
        0x0740, 0x0780, 0x07C0, 0x0800, 0x0900, 0x0A00, 0x0B00, 0x0C00, 0x0D00, 0x0E00,
        0x0F00, 0x1000, 0x1400, 0x1800, 0x1C00, 0x2000, 0x3000, 0x4000,
    ];

    // each nibble indexes one of 16 deltas; the plain decoder uses the first 8 and orders the
    // negative ones by the encoding, the repairing decoder uses the whole table
    private static readonly sbyte[] TableDpcm8 = [0, 1, 2, 3, 6, 10, 15, 21, -21, -15, -10, -6, -3, -2, -1, 0];

    private readonly byte[] _data;
    private readonly int _start;
    private readonly int _rawDataSize;
    private readonly bool _s16Bit;
    private readonly bool _oldDpcm8;
    private readonly bool _popfix;
    private int _pos;
    private short _carry16L;
    private short _carry16R;
    private byte _carry8L;
    private byte _carry8R;
    private byte _repairState;
    private byte _preRepairSample;

    private SolStream(byte[] data, int start, int rawDataSize, int rate, bool stereo, bool s16Bit, bool oldDpcm8, bool popfix)
    {
        _data = data;
        _start = start;
        // SSCI aligns the size of SOL data to 32 bits
        _rawDataSize = Math.Min(rawDataSize, data.Length - start) & ~3;
        Rate = rate;
        IsStereo = stereo;
        _s16Bit = s16Bit;
        _oldDpcm8 = oldDpcm8;
        // the pop fix is only verified with the old 8-bit encoding
        _popfix = popfix && oldDpcm8;
        ResetCarry();

        const int compressionRatio = 2;
        int numChannels = stereo ? 2 : 1;
        int bytesPerSample = s16Bit ? 2 : 1;
        LengthMs = (int)((long)_rawDataSize * compressionRatio * 1000 / (Math.Max(rate, 1) * numChannels * bytesPerSample));
    }

    public bool IsStereo { get; }

    public int Rate { get; }

    public int LengthMs { get; }

    public bool EndOfData => _pos >= _rawDataSize;

    public bool EndOfStream => EndOfData;

    /// <summary>Whether the resource data starts with a SOL header behind its resource header.</summary>
    public static bool Detect(byte[] data)
        => data.Length >= 6 && (data[0] & 0x7f) == (int)ResourceType.Audio && BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(2)) == 0x534F4C00;

    /// <summary>Makes the stream for a SOL resource: DPCM decoded, or raw when it is not compressed.</summary>
    public static IRewindableAudioStream32? Make(byte[] data, SciVersion version, bool popfix, ILog? log)
    {
        if (!Detect(data) || data.Length < 13)
        {
            return null;
        }

        int headerSize = data[1] + SciResource.ResourceHeaderSize;
        ushort sampleRate = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
        byte flags = data[8];
        uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(9));
        int start = headerSize;
        int size = (int)Math.Min(dataSize, (uint)Math.Max(0, data.Length - start));

        bool stereo = (flags & FlagStereo) != 0;
        bool s16 = (flags & Flag16Bit) != 0;
        if ((flags & FlagCompressed) != 0)
        {
            if (stereo && !s16 && version < SciVersion.V2_1Early)
            {
                log?.Warning("sci.sound", "SCI2 and earlier did not support stereo SOL audio");
            }

            bool oldDpcm8 = !stereo && !s16 && version < SciVersion.V2_1Early;
            return new SolStream(data, start, size, sampleRate, stereo, s16, oldDpcm8, popfix);
        }

        return new RawPcmStream(data, start, size, sampleRate, s16, !s16, stereo);
    }

    public bool Rewind()
    {
        _pos = 0;
        ResetCarry();
        return true;
    }

    private void ResetCarry()
    {
        _carry16L = _carry16R = 0;
        _carry8L = _carry8R = 0x80;
        _repairState = 0;
        _preRepairSample = 0;
    }

    public int ReadBuffer(Span<short> buffer)
    {
        int samplesPerByte = _s16Bit ? 1 : 2;
        int numSamples = buffer.Length;
        if (!_s16Bit)
        {
            // one byte holds two samples; an odd request would lose the second nibble
            numSamples &= ~1;
        }

        int bytesToRead = numSamples / samplesPerByte;
        if (_pos + bytesToRead > _rawDataSize)
        {
            bytesToRead = _rawDataSize - _pos;
        }

        if (bytesToRead <= 0)
        {
            return 0;
        }

        var input = _data.AsSpan(_start + _pos, bytesToRead);
        int o = 0;
        if (_s16Bit)
        {
            if (IsStereo)
            {
                for (int i = 0; i + 1 < bytesToRead; i += 2)
                {
                    buffer[o++] = DeDpcm16(ref _carry16L, input[i]);
                    buffer[o++] = DeDpcm16(ref _carry16R, input[i + 1]);
                }
            }
            else
            {
                for (int i = 0; i < bytesToRead; i++)
                {
                    buffer[o++] = DeDpcm16(ref _carry16L, input[i]);
                }
            }
        }
        else if (IsStereo)
        {
            for (int i = 0; i < bytesToRead; i++)
            {
                byte delta = input[i];
                buffer[o++] = DeDpcm8Nibble(ref _carry8L, (byte)(delta >> 4), false);
                buffer[o++] = DeDpcm8Nibble(ref _carry8R, (byte)(delta & 0xf), false);
            }
        }
        else if (_popfix)
        {
            for (int i = 0; i < bytesToRead; i++)
            {
                byte delta = input[i];
                buffer[o++] = DeDpcm8NibbleWithRepair(ref _carry8L, (byte)(delta >> 4));
                buffer[o++] = DeDpcm8NibbleWithRepair(ref _carry8L, (byte)(delta & 0xf));
            }
        }
        else
        {
            for (int i = 0; i < bytesToRead; i++)
            {
                byte delta = input[i];
                buffer[o++] = DeDpcm8Nibble(ref _carry8L, (byte)(delta >> 4), _oldDpcm8);
                buffer[o++] = DeDpcm8Nibble(ref _carry8L, (byte)(delta & 0xf), _oldDpcm8);
            }
        }

        _pos += bytesToRead;
        return o;
    }

    /// <summary>One channel of 16-bit DPCM; the 16-bit register overflow of the x86 original is kept.</summary>
    public static short DeDpcm16(ref short sample, byte delta)
    {
        int next = sample;
        if ((delta & 0x80) != 0)
        {
            next -= TableDpcm16[delta & 0x7f];
        }
        else
        {
            next += TableDpcm16[delta];
        }

        if (next > 32767)
        {
            next -= 65536;
        }
        else if (next < -32768)
        {
            next += 65536;
        }

        sample = (short)next;
        return sample;
    }

    private static short DeDpcm8Nibble(ref byte sample, byte delta, bool old)
    {
        byte lastSample = sample;
        if ((delta & 8) != 0)
        {
            sample = (byte)(sample - TableDpcm8[old ? 7 - (delta & 7) : delta & 7]);
        }
        else
        {
            sample = (byte)(sample + TableDpcm8[delta & 7]);
        }

        return (short)(((lastSample + sample) << 7) ^ 0x8000);
    }

    /// <summary>
    /// Decodes a nibble while repairing the 8-bit overflows of Gabriel Knight's speech: a wrapped
    /// wave is decayed along an artificial slope until it meets the real data again.
    /// </summary>
    private short DeDpcm8NibbleWithRepair(ref byte sample, byte delta)
    {
        byte lastSample = sample;
        switch (_repairState)
        {
            case 0:
            {
                int newSampleOverflow = sample + TableDpcm8[delta & 15];
                if (newSampleOverflow > 255)
                {
                    _repairState = 1;
                    sample = (byte)(lastSample - RepairSlope);
                    _preRepairSample = (byte)newSampleOverflow;
                }
                else if (newSampleOverflow < 0)
                {
                    _repairState = 2;
                    sample = (byte)(lastSample + RepairSlope);
                    _preRepairSample = (byte)newSampleOverflow;
                }
                else
                {
                    sample = (byte)newSampleOverflow;
                }

                break;
            }

            case 1:
            {
                byte slopeSample = (byte)(lastSample - RepairSlope);
                _preRepairSample = (byte)(_preRepairSample + TableDpcm8[delta & 15]);
                if (_preRepairSample >= slopeSample)
                {
                    _repairState = 0;
                    sample = _preRepairSample;
                }
                else
                {
                    sample = slopeSample;
                }

                break;
            }

            case 2:
            {
                byte slopeSample = (byte)(lastSample + RepairSlope);
                _preRepairSample = (byte)(_preRepairSample + TableDpcm8[delta & 15]);
                if (_preRepairSample <= slopeSample)
                {
                    _repairState = 0;
                    sample = _preRepairSample;
                }
                else
                {
                    sample = slopeSample;
                }

                break;
            }

            default:
                _repairState = 0;
                break;
        }

        return (short)(((lastSample + sample) << 7) ^ 0x8000);
    }
}

/// <summary>Makes streams for the formats SCI32 audio resources come in.</summary>
public static class AudioStreamFactory
{
    public static bool DetectWave(byte[] data) => data.Length >= 8 && BinaryPrimitives.ReadUInt32BigEndian(data) == 0x52494646; // RIFF

    public static bool DetectAiff(byte[] data) => data.Length >= 8 && BinaryPrimitives.ReadUInt32BigEndian(data) == 0x464F524D; // FORM

    public static bool DetectMacSnd(byte[] data)
        => data.Length >= 14 && BinaryPrimitives.ReadUInt16BigEndian(data) == 1 && BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2)) == 1
            && BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4)) == 5 && BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(10)) == 0x00018051;

    /// <summary>A plain PCM WAVE file.</summary>
    public static IRewindableAudioStream32? MakeWave(byte[] data, ILog? log)
    {
        int pos = 12;
        int channels = 1;
        int rate = 11025;
        int bits = 8;
        int format = 1;
        while (pos + 8 <= data.Length)
        {
            uint chunkId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            int chunkSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4));
            int chunkStart = pos + 8;
            if (chunkId == 0x666D7420 && chunkStart + 16 <= data.Length) // "fmt "
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(chunkStart));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(chunkStart + 2));
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(chunkStart + 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(chunkStart + 14));
            }
            else if (chunkId == 0x64617461) // "data"
            {
                if (format != 1 || (bits != 8 && bits != 16) || channels is < 1 or > 2)
                {
                    log?.Warning("sci.sound", $"Unsupported WAVE format {format}, {bits} bits, {channels} channels");
                    return null;
                }

                int available = Math.Min(Math.Max(chunkSize, 0), data.Length - chunkStart);
                return new RawPcmStream(data, chunkStart, available, rate, bits == 16, bits == 8, channels == 2);
            }

            pos = chunkStart + chunkSize + (chunkSize & 1);
        }

        log?.Warning("sci.sound", "WAVE resource without a data chunk");
        return null;
    }
}

/// <summary>Wraps a rewindable stream and starts it over at its end while looping is on.</summary>
public sealed class MutableLoopStream : IAudioStream32
{
    private readonly IRewindableAudioStream32 _stream;

    public MutableLoopStream(IRewindableAudioStream32 stream, bool loop)
    {
        _stream = stream;
        Loop = loop;
    }

    public bool Loop { get; set; }

    public bool IsStereo => _stream.IsStereo;

    public int Rate => _stream.Rate;

    public bool EndOfData => !Loop && _stream.EndOfData;

    public bool EndOfStream => !Loop && _stream.EndOfStream;

    public int LengthMs => _stream.LengthMs;

    public int ReadBuffer(Span<short> buffer)
    {
        int total = 0;
        int samplesRead;
        do
        {
            if (Loop && _stream.EndOfStream)
            {
                _stream.Rewind();
            }

            samplesRead = _stream.ReadBuffer(buffer);
            total += samplesRead;
            buffer = buffer[samplesRead..];
        }
        while (samplesRead > 0 && Loop && buffer.Length > 0);

        return total;
    }
}

/// <summary>
/// Resamples a stream to the output rate with linear interpolation and adds it, with a volume
/// per side, into an interleaved stereo buffer. The input is read in chunks as needed.
/// </summary>
public sealed class RateConverter
{
    private const int FracBits = 16;
    private const long FracOne = 1L << FracBits;
    private const int ChunkFrames = 512;

    private readonly int _outputRate;
    private readonly short[] _inBuffer;
    private int _inPos;
    private int _inLength;
    private long _opos = FracOne; // start by pulling the first frame
    private readonly int[] _last = new int[2];
    private readonly int[] _cur = new int[2];
    private bool _inputStereo;
    private int _inputRate;
    private bool _primed;

    public RateConverter(int inputRate, int outputRate, bool inputStereo)
    {
        _inputRate = inputRate;
        _outputRate = outputRate;
        _inputStereo = inputStereo;
        _inBuffer = new short[ChunkFrames * 2];
    }

    /// <summary>
    /// Writes up to <paramref name="frames"/> stereo frames into the target, mixing with what is
    /// there (saturating). Volumes are 0..255. Returns the frames written.
    /// </summary>
    public int Convert(IAudioStream32 source, Span<short> target, int frames, int leftVolume, int rightVolume)
    {
        if (source.Rate != _inputRate || source.IsStereo != _inputStereo)
        {
            _inputRate = source.Rate;
            _inputStereo = source.IsStereo;
        }

        long step = (long)Math.Max(_inputRate, 1) * FracOne / Math.Max(_outputRate, 1);
        int written = 0;
        while (written < frames)
        {
            while (_opos >= FracOne)
            {
                if (!PullFrame(source))
                {
                    return written;
                }

                _opos -= FracOne;
            }

            long frac = _opos;
            int l = (int)(_last[0] + ((_cur[0] - _last[0]) * frac >> FracBits));
            int r = (int)(_last[1] + ((_cur[1] - _last[1]) * frac >> FracBits));
            int ti = written * 2;
            target[ti] = ClampedAdd(target[ti], l * leftVolume >> 8);
            target[ti + 1] = ClampedAdd(target[ti + 1], r * rightVolume >> 8);
            written++;
            _opos += step;
        }

        return written;
    }

    private bool PullFrame(IAudioStream32 source)
    {
        int channels = _inputStereo ? 2 : 1;
        if (_inPos + channels > _inLength)
        {
            _inLength = source.ReadBuffer(_inBuffer.AsSpan(0, ChunkFrames * channels));
            _inPos = 0;
            if (_inLength < channels)
            {
                return false;
            }
        }

        if (_primed)
        {
            _last[0] = _cur[0];
            _last[1] = _cur[1];
        }

        _cur[0] = _inBuffer[_inPos];
        _cur[1] = _inputStereo ? _inBuffer[_inPos + 1] : _cur[0];
        if (!_primed)
        {
            _last[0] = _cur[0];
            _last[1] = _cur[1];
            _primed = true;
        }

        _inPos += channels;
        return true;
    }

    public static short ClampedAdd(short a, int b) => (short)Math.Clamp(a + b, short.MinValue, short.MaxValue);
}

/// <summary>
/// The audio of a Robot video: 22 kHz 16-bit mono DPCM packets arrive in two interleaved halves
/// (even and odd samples), each written into a loop buffer; a missing half is interpolated from
/// the other. Port of ScummVM's RobotAudioStream.
/// </summary>
public sealed class RobotAudioStream : IAudioStream32
{
    public const int RobotSampleRate = 22050;

    /// <summary>A packet's samples spread over every other slot of the loop buffer.</summary>
    public const int EosExpansion = 2;

    public readonly record struct StreamState(int BytesPlaying, ushort Rate, byte Bits);

    /// <summary>A packet of compressed audio from a Robot data stream, at an uncompressed byte position.</summary>
    public readonly record struct RobotAudioPacket(byte[] Data, int DataOffset, int DataSize, int Position);

    private readonly Lock _lock = new();
    private readonly byte[] _loopBuffer;
    private readonly int _loopBufferSize;
    private int _readHead;
    private int _readHeadAbs;
    private int _maxWriteAbs;
    private int _writeHeadAbs;
    private readonly int[] _jointMin = new int[2];
    private bool _waiting = true;
    private bool _finished;
    private int _firstPacketPosition = -1;
    private byte[] _decompressionBuffer = [];
    private int _decompressionBufferSize;
    private int _decompressionBufferPosition = -1;

    public RobotAudioStream(int bufferSize)
    {
        _loopBuffer = new byte[bufferSize];
        _loopBufferSize = bufferSize;
    }

    public bool IsStereo => false;

    public int Rate => RobotSampleRate;

    public bool EndOfData
    {
        get
        {
            lock (_lock)
            {
                return _waiting || _writeHeadAbs <= _readHeadAbs;
            }
        }
    }

    public bool EndOfStream
    {
        get
        {
            lock (_lock)
            {
                return _finished && _writeHeadAbs <= _readHeadAbs;
            }
        }
    }

    /// <summary>Adds a packet. Returns true when the packet was fully consumed, false to send it again later.</summary>
    public bool AddPacket(in RobotAudioPacket packet)
    {
        lock (_lock)
        {
            if (_finished)
            {
                return false;
            }

            // the position is doubled by the expansion, so even packets land on multiples of 4
            int bufferIndex = packet.Position % 4 != 0 ? 1 : 0;

            // packet 0 is the first primer, packet 2 the second, packet 4 and up regular data
            if (packet.Position <= 2 && _firstPacketPosition == -1)
            {
                _readHead = 0;
                _readHeadAbs = 0;
                _maxWriteAbs = _loopBufferSize;
                _writeHeadAbs = 2;
                _jointMin[0] = 0;
                _jointMin[1] = 2;
                _waiting = true;
                _finished = false;
                _firstPacketPosition = packet.Position;
                FillRobotBuffer(packet, bufferIndex);
                return true;
            }

            int packetEndByte = packet.Position + packet.DataSize * (sizeof(short) + EosExpansion);

            // already read, or already written to this half, past this packet
            if (packetEndByte <= Math.Max(_readHeadAbs, _jointMin[bufferIndex]))
            {
                return true;
            }

            // the loop buffer is full
            if (_maxWriteAbs <= _jointMin[bufferIndex])
            {
                return false;
            }

            FillRobotBuffer(packet, bufferIndex);

            // the second primer lets playback begin
            if (_firstPacketPosition != -1 && _firstPacketPosition != packet.Position)
            {
                _waiting = false;
                _firstPacketPosition = -1;
            }

            // only part of the packet fit
            return packetEndByte <= _maxWriteAbs;
        }
    }

    private void FillRobotBuffer(in RobotAudioPacket packet, int bufferIndex)
    {
        int sourceByte = 0;
        int decompressedSize = packet.DataSize * sizeof(short);
        if (_decompressionBufferPosition != packet.Position)
        {
            if (decompressedSize != _decompressionBufferSize)
            {
                _decompressionBuffer = new byte[decompressedSize];
                _decompressionBufferSize = decompressedSize;
            }

            short carry = 0;
            for (int i = 0; i < packet.DataSize; i++)
            {
                short v = SolStream.DeDpcm16(ref carry, packet.Data[packet.DataOffset + i]);
                BinaryPrimitives.WriteInt16LittleEndian(_decompressionBuffer.AsSpan(i * 2), v);
            }

            _decompressionBufferPosition = packet.Position;
        }

        int numBytes = decompressedSize;
        int packetPosition = packet.Position;
        int endByte = packet.Position + decompressedSize * EosExpansion;
        int startByte = Math.Max(_readHeadAbs + bufferIndex * 2, _jointMin[bufferIndex]);
        int maxWriteByte = _maxWriteAbs + bufferIndex * 2;
        if (packetPosition < startByte)
        {
            sourceByte = (startByte - packetPosition) / EosExpansion;
            numBytes -= sourceByte;
            packetPosition = startByte;
        }

        if (packetPosition > maxWriteByte)
        {
            numBytes += (packetPosition - maxWriteByte) / EosExpansion;
            packetPosition = maxWriteByte;
        }

        if (endByte > maxWriteByte)
        {
            numBytes -= (endByte - maxWriteByte) / EosExpansion;
            endByte = maxWriteByte;
        }

        int maxJointMin = Math.Max(_jointMin[0], _jointMin[1]);
        if (endByte > maxJointMin)
        {
            _writeHeadAbs += endByte - maxJointMin;
        }

        if (packetPosition > _jointMin[bufferIndex])
        {
            int packetEndByte = packetPosition % _loopBufferSize;
            int targetBytePosition;
            int numBytesToEnd;
            if ((packetPosition & ~3) > (_jointMin[1 - bufferIndex] & ~3))
            {
                targetBytePosition = _jointMin[1 - bufferIndex] % _loopBufferSize;
                if (targetBytePosition >= packetEndByte)
                {
                    numBytesToEnd = _loopBufferSize - targetBytePosition;
                    Array.Clear(_loopBuffer, targetBytePosition, numBytesToEnd);
                    targetBytePosition = 1 - bufferIndex != 0 ? 2 : 0;
                }

                numBytesToEnd = packetEndByte - targetBytePosition;
                if (numBytesToEnd > 0)
                {
                    Array.Clear(_loopBuffer, targetBytePosition, numBytesToEnd);
                }
            }

            targetBytePosition = _jointMin[bufferIndex] % _loopBufferSize;
            if (targetBytePosition >= packetEndByte)
            {
                numBytesToEnd = _loopBufferSize - targetBytePosition;
                InterpolateChannel(targetBytePosition, numBytesToEnd / (sizeof(short) + EosExpansion), 0);
                targetBytePosition = bufferIndex != 0 ? 2 : 0;
            }

            numBytesToEnd = packetEndByte - targetBytePosition;
            if (numBytesToEnd > 0)
            {
                InterpolateChannel(targetBytePosition, numBytesToEnd / (sizeof(short) + EosExpansion), 0);
            }
        }

        if (numBytes > 0)
        {
            int targetBytePosition = packetPosition % _loopBufferSize;
            int packetEndByte = endByte % _loopBufferSize;
            int numBytesToEnd = 0;
            if (targetBytePosition >= packetEndByte)
            {
                numBytesToEnd = (_loopBufferSize - (targetBytePosition & ~3)) / EosExpansion;
                CopyEveryOtherSample(targetBytePosition, sourceByte, numBytesToEnd / EosExpansion);
                targetBytePosition = bufferIndex != 0 ? 2 : 0;
            }

            CopyEveryOtherSample(targetBytePosition, sourceByte + numBytesToEnd, (packetEndByte - targetBytePosition) / (sizeof(short) + EosExpansion));
        }

        _jointMin[bufferIndex] = endByte;
    }

    private short LoopSample(int byteOffset) => BinaryPrimitives.ReadInt16LittleEndian(_loopBuffer.AsSpan(byteOffset));

    private void SetLoopSample(int byteOffset, int value) => BinaryPrimitives.WriteInt16LittleEndian(_loopBuffer.AsSpan(byteOffset), (short)value);

    /// <summary>Fills one half of the buffer by averaging the neighbouring samples of the other half.</summary>
    private void InterpolateChannel(int byteOffset, int numSamples, int bufferIndex)
    {
        if (numSamples <= 0)
        {
            return;
        }

        int inOffset;
        int outOffset;
        int sample;
        int previousSample;
        if (bufferIndex != 0)
        {
            outOffset = byteOffset + 2;
            inOffset = byteOffset + 4;
            previousSample = sample = LoopSample(byteOffset);
            numSamples--;
        }
        else
        {
            outOffset = byteOffset;
            inOffset = byteOffset + 2;
            previousSample = sample = LoopSample(inOffset);
        }

        while (numSamples-- > 0)
        {
            int inSample = inOffset + 2 <= _loopBufferSize ? LoopSample(inOffset) : 0;
            sample = (inSample + previousSample) >> 1;
            previousSample = inSample;
            if (outOffset + 2 <= _loopBufferSize)
            {
                SetLoopSample(outOffset, sample);
            }

            inOffset += EosExpansion * 2;
            outOffset += EosExpansion * 2;
        }

        if (bufferIndex != 0 && outOffset + 2 <= _loopBufferSize)
        {
            SetLoopSample(outOffset, sample);
        }
    }

    private void CopyEveryOtherSample(int targetByteOffset, int sourceByteOffset, int numSamples)
    {
        for (int i = 0; i < numSamples; i++)
        {
            int src = sourceByteOffset + i * 2;
            if (src + 2 > _decompressionBufferSize || targetByteOffset + 2 > _loopBufferSize)
            {
                break;
            }

            _loopBuffer[targetByteOffset] = _decompressionBuffer[src];
            _loopBuffer[targetByteOffset + 1] = _decompressionBuffer[src + 1];
            targetByteOffset += 4;
        }
    }

    private void InterpolateMissingSamples(int numSamples)
    {
        int numBytes = numSamples * (sizeof(short) + EosExpansion);
        int targetPosition = _readHead;
        int nextReadHeadPosition = _readHeadAbs + numBytes;

        if (nextReadHeadPosition > _jointMin[1])
        {
            if (nextReadHeadPosition > _jointMin[0])
            {
                if (targetPosition + numBytes >= _loopBufferSize)
                {
                    int numBytesToEdge = _loopBufferSize - targetPosition;
                    Array.Clear(_loopBuffer, targetPosition, numBytesToEdge);
                    numBytes -= numBytesToEdge;
                    targetPosition = 0;
                }

                Array.Clear(_loopBuffer, targetPosition, numBytes);
                _jointMin[0] = nextReadHeadPosition;
                _jointMin[1] = nextReadHeadPosition + sizeof(short);
            }
            else
            {
                if (targetPosition + numBytes >= _loopBufferSize)
                {
                    int numSamplesToEdge = (_loopBufferSize - targetPosition) / (sizeof(short) + EosExpansion);
                    InterpolateChannel(targetPosition, numSamplesToEdge, 1);
                    numSamples -= numSamplesToEdge;
                    targetPosition = 0;
                }

                InterpolateChannel(targetPosition, numSamples, 1);
                _jointMin[1] = nextReadHeadPosition + sizeof(short);
            }
        }
        else if (nextReadHeadPosition > _jointMin[0])
        {
            if (targetPosition + numBytes >= _loopBufferSize)
            {
                int numSamplesToEdge = (_loopBufferSize - targetPosition) / (sizeof(short) + EosExpansion);
                InterpolateChannel(targetPosition, numSamplesToEdge, 0);
                numSamples -= numSamplesToEdge;
                targetPosition = 2;
            }

            InterpolateChannel(targetPosition, numSamples, 0);
            _jointMin[0] = nextReadHeadPosition;
        }
    }

    /// <summary>No more packets will be added.</summary>
    public void Finish()
    {
        lock (_lock)
        {
            _finished = true;
        }
    }

    public StreamState GetStatus()
    {
        lock (_lock)
        {
            return new StreamState(_readHeadAbs, RobotSampleRate, 16);
        }
    }

    public int ReadBuffer(Span<short> buffer)
    {
        lock (_lock)
        {
            if (_waiting)
            {
                return 0;
            }

            int maxNumSamples = (_writeHeadAbs - _readHeadAbs) / sizeof(short);
            int numSamples = Math.Min(buffer.Length, maxNumSamples);
            if (numSamples <= 0)
            {
                return 0;
            }

            InterpolateMissingSamples(numSamples);

            int numSamplesToEnd = (_loopBufferSize - _readHead) / sizeof(short);
            int numSamplesToRead = Math.Min(numSamples, numSamplesToEnd);
            int o = 0;
            for (int i = 0; i < numSamplesToRead; i++)
            {
                buffer[o++] = LoopSample(_readHead + i * 2);
            }

            if (numSamplesToRead < numSamples)
            {
                int rest = numSamples - numSamplesToRead;
                for (int i = 0; i < rest; i++)
                {
                    buffer[o++] = LoopSample(i * 2);
                }
            }

            int numBytes = numSamples * sizeof(short);
            _readHead += numBytes;
            if (_readHead > _loopBufferSize)
            {
                _readHead -= _loopBufferSize;
            }

            _readHeadAbs += numBytes;
            _maxWriteAbs += numBytes;
            return numSamples;
        }
    }
}
