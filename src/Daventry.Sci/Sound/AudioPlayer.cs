using System.Buffers.Binary;
using Daventry.Core.Hosting;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Sound;

/// <summary>A decoded mono sample: signed 16-bit at its own rate.</summary>
public sealed class PcmSample
{
    public PcmSample(short[] data, int rate)
    {
        Data = data;
        Rate = rate;
    }

    public short[] Data { get; }

    public int Rate { get; }

    /// <summary>The length in game ticks (1/60 s).</summary>
    public int LengthTicks => Rate > 0 ? (int)((long)Data.Length * 60 / Rate) : 0;

    /// <summary>Wraps 8-bit PCM, unsigned unless told otherwise.</summary>
    public static PcmSample From8Bit(ReadOnlySpan<byte> bytes, int rate, bool signedData)
    {
        var data = new short[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            int v = signedData ? (sbyte)bytes[i] : bytes[i] - 128;
            data[i] = (short)(v << 8);
        }

        return new PcmSample(data, rate);
    }
}

/// <summary>
/// Plays one sample at a time through the mixer, resampling to the output rate. Used for the
/// kDoAudio speech of the CD games and for the digital samples of sound resources.
/// </summary>
public sealed class SampleVoice
{
    private readonly Lock _lock = new();
    private readonly int _outputRate;
    private PcmSample? _sample;
    private long _pos; // 16.16 fixed point
    private uint _loopsLeft;
    private long _framesPlayed;
    private bool _active;
    private bool _paused;
    private int _volume = 127;

    public SampleVoice(int outputRate)
    {
        _outputRate = outputRate;
    }

    public bool IsActive
    {
        get
        {
            lock (_lock)
            {
                return _active;
            }
        }
    }

    public bool IsPaused
    {
        get
        {
            lock (_lock)
            {
                return _paused;
            }
        }
    }

    /// <summary>Frames of the sample's own rate played so far.</summary>
    public long FramesPlayed
    {
        get
        {
            lock (_lock)
            {
                return _framesPlayed;
            }
        }
    }

    /// <summary>Complete iterations of a looping sample.</summary>
    public uint Iterations { get; private set; }

    public PcmSample? Sample
    {
        get
        {
            lock (_lock)
            {
                return _sample;
            }
        }
    }

    public void Play(PcmSample sample, uint loops = 1)
    {
        lock (_lock)
        {
            _sample = sample;
            _pos = 0;
            _framesPlayed = 0;
            _loopsLeft = Math.Max(loops, 1u);
            Iterations = 0;
            _paused = false;
            _active = sample.Data.Length > 0 && sample.Rate > 0;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _active = false;
            _sample = null;
        }
    }

    public void Pause(bool pause)
    {
        lock (_lock)
        {
            _paused = pause;
        }
    }

    /// <summary>The volume, 0 to 127.</summary>
    public void SetVolume(int volume)
    {
        lock (_lock)
        {
            _volume = Math.Clamp(volume, 0, 127);
        }
    }

    /// <summary>Adds the voice to an interleaved stereo buffer.</summary>
    public void Mix(Span<short> buffer)
    {
        lock (_lock)
        {
            if (!_active || _paused || _sample is null)
            {
                return;
            }

            var data = _sample.Data;
            long step = ((long)_sample.Rate << 16) / _outputRate;
            int frames = buffer.Length / 2;
            int gain = _volume * 256 / 127;
            for (int i = 0; i < frames; i++)
            {
                int index = (int)(_pos >> 16);
                if (index >= data.Length)
                {
                    if (_loopsLeft > 1)
                    {
                        _loopsLeft--;
                        Iterations++;
                        _pos = 0;
                        index = 0;
                    }
                    else
                    {
                        _active = false;
                        return;
                    }
                }

                int sample = data[index] * gain >> 8;
                buffer[i * 2] = (short)Math.Clamp(buffer[i * 2] + sample, short.MinValue, short.MaxValue);
                buffer[i * 2 + 1] = (short)Math.Clamp(buffer[i * 2 + 1] + sample, short.MinValue, short.MaxValue);
                long before = _pos;
                _pos += step;
                _framesPlayed += (_pos >> 16) - (before >> 16);
            }
        }
    }
}

/// <summary>
/// The kDoAudio player: decodes audio resources (Sierra SOL, WAVE or raw) and plays speech and
/// sound effects. Port of ScummVM's AudioPlayer (sound/audio.cpp) without CD audio.
/// </summary>
public sealed class AudioPlayer : IAudioSource
{
    public const int VolumeMax = 127;

    private const byte SolFlagCompressed = 1 << 0;
    private const byte SolFlag16Bit = 1 << 2;
    private const byte SolFlagIsSigned = 1 << 3;

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

    private static readonly byte[] TableDpcm8 = [0, 1, 2, 3, 6, 10, 15, 21];

    private readonly ResourceManager _resMan;
    private readonly ILog _log;
    private readonly SampleVoice _voice;
    private ushort _audioRate = 11025;
    private bool _wPlayFlag;
    private ushort _playCounter;

    public AudioPlayer(ResourceManager resMan, int outputRate, ILog log)
    {
        _resMan = resMan;
        _log = log;
        _voice = new SampleVoice(outputRate);
    }

    public void SetAudioRate(ushort rate) => _audioRate = rate;

    public void Read(Span<short> interleavedStereo)
    {
        interleavedStereo.Clear();
        _voice.Mix(interleavedStereo);
    }

    /// <summary>Starts a sample; returns its length in ticks, or 0 when it is missing.</summary>
    public int StartAudio(ushort module, uint number)
    {
        var sample = GetAudioSample(number, module);
        if (sample is null)
        {
            // no warning: some games miss audio entries
            return 0;
        }

        _wPlayFlag = false;
        _voice.Play(sample);
        return sample.LengthTicks;
    }

    /// <summary>"Preloads" a sample: only its length is needed, and position reports 0 until play.</summary>
    public int WPlayAudio(ushort module, uint tuple)
    {
        var sample = GetAudioSample(tuple, module);
        if (sample is null)
        {
            _log.Debug("sci.sound", $"wPlayAudio: unable to create stream for audio tuple {tuple}, module {module}");
        }

        _wPlayFlag = true;
        return sample?.LengthTicks ?? 0;
    }

    public void StopAudio() => _voice.Stop();

    public void PauseAudio() => _voice.Pause(true);

    public void ResumeAudio() => _voice.Pause(false);

    public bool IsPlaying => _voice.IsActive;

    /// <summary>The position in ticks, 0 for a loaded but unstarted sample, -1 when finished.</summary>
    public int GetAudioPosition()
    {
        if (_voice.IsActive)
        {
            var sample = _voice.Sample;
            int rate = sample?.Rate ?? _audioRate;
            return (int)(_voice.FramesPlayed * 60 / Math.Max(rate, 1));
        }

        if (_wPlayFlag)
        {
            return 0; // "loaded" but not started
        }

        return -1; // finished
    }

    public void SetVolume(int volume) => _voice.SetVolume(volume);

    public void IncrementPlayCounter() => _playCounter++;

    public ushort PlayCounter => _playCounter;

    /// <summary>Decodes an audio resource (module 65535) or an audio36 resource of a module.</summary>
    public PcmSample? GetAudioSample(uint number, uint module)
    {
        SciResource? audioRes;
        if (module == 65535)
        {
            audioRes = _resMan.FindResource(new ResourceId(ResourceType.Audio, (ushort)number));
            if (audioRes?.Data is null)
            {
                _log.Debug("sci.sound", $"Failed to find audio entry {number}");
                return null;
            }
        }
        else
        {
            audioRes = _resMan.FindResource(new ResourceId(ResourceType.Audio36, (ushort)module, number));
            if (audioRes?.Data is null)
            {
                _log.Debug("sci.sound", $"Failed to find audio entry ({module}, {(number >> 24) & 0xff}, {(number >> 16) & 0xff}, {(number >> 8) & 0xff}, {number & 0xff})");
                return null;
            }
        }

        byte[] data = audioRes.Data;
        try
        {
            if (data.Length > 6 && (data[0] & 0x7f) == (int)ResourceType.Audio && BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(2)) == 0x534F4C00)
            {
                // SCI1.1: a SOL header behind the resource header
                return DecodeSol(data, data[1]);
            }

            if (data.Length > 4 && BinaryPrimitives.ReadUInt32BigEndian(data) == 0x52494646)
            {
                return DecodeWave(data); // RIFF
            }

            if (data.Length > 14 && BinaryPrimitives.ReadUInt16BigEndian(data) == 1 && BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2)) == 1
                && BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4)) == 5 && BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(10)) == 0x00018051)
            {
                _log.Warning("sci.sound", "Mac snd audio resources are not supported");
                return null;
            }

            // SCI1 raw audio: unsigned 8-bit at 11025 Hz
            _audioRate = 11025;
            return PcmSample.From8Bit(data, _audioRate, false);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            _log.Warning("sci.sound", $"Truncated audio resource {audioRes.Id}: {ex.Message}");
            return null;
        }
    }

    private PcmSample? DecodeSol(byte[] data, int headerSize)
    {
        if (headerSize != 7 && headerSize != 11 && headerSize != 12)
        {
            _log.Warning("sci.sound", $"SOL audio header of size {headerSize} not supported");
            return null;
        }

        int header = SciResource.ResourceHeaderSize;
        ushort rate = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(header + 4));
        byte audioFlags = data[header + 6];
        // the QFG3 demo format (7 byte header) has no size: the resource size is used
        uint size = headerSize == 7
            ? (uint)(data.Length - header - headerSize)
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(header + 7));
        _audioRate = rate;

        int dataStart = header + headerSize;
        var audio = data.AsSpan(dataStart, (int)Math.Min(size, (uint)(data.Length - dataStart)));

        bool is16Bit = (audioFlags & SolFlag16Bit) != 0;
        bool isSigned = (audioFlags & SolFlagIsSigned) != 0;
        if ((audioFlags & SolFlagCompressed) != 0)
        {
            return is16Bit ? new PcmSample(DeDpcm16(audio), rate) : new PcmSample(DeDpcm8(audio), rate);
        }

        // raw PCM
        if (is16Bit)
        {
            var samples = new short[audio.Length / 2];
            for (int i = 0; i < samples.Length; i++)
            {
                short v = BinaryPrimitives.ReadInt16LittleEndian(audio.Slice(i * 2));
                samples[i] = isSigned ? v : (short)(v ^ unchecked((short)0x8000));
            }

            return new PcmSample(samples, rate);
        }

        return PcmSample.From8Bit(audio, rate, isSigned);
    }

    private static short[] DeDpcm16(ReadOnlySpan<byte> input)
    {
        var output = new short[input.Length];
        int s = 0;
        for (int i = 0; i < input.Length; i++)
        {
            byte b = input[i];
            if ((b & 0x80) != 0)
            {
                s -= TableDpcm16[b & 0x7f];
            }
            else
            {
                s += TableDpcm16[b];
            }

            s = Math.Clamp(s, -32768, 32767);
            output[i] = (short)s;
        }

        return output;
    }

    private static short[] DeDpcm8(ReadOnlySpan<byte> input)
    {
        var output = new short[input.Length * 2];
        int s = 0x80;
        int o = 0;
        for (int i = 0; i < input.Length; i++)
        {
            byte b = input[i];
            output[o++] = DeDpcm8Nibble(ref s, (byte)(b >> 4));
            output[o++] = DeDpcm8Nibble(ref s, (byte)(b & 0xf));
        }

        return output;
    }

    private static short DeDpcm8Nibble(ref int s, byte b)
    {
        if ((b & 8) != 0)
        {
            s -= TableDpcm8[7 - (b & 7)];
        }
        else
        {
            s += TableDpcm8[b & 7];
        }

        s = Math.Clamp(s, 0, 255);
        return (short)((s - 128) << 8);
    }

    /// <summary>A plain PCM WAVE file: 8 or 16 bit, mono or stereo (mixed down).</summary>
    private PcmSample? DecodeWave(byte[] data)
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
            if (chunkId == 0x666D7420) // "fmt "
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(chunkStart));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(chunkStart + 2));
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(chunkStart + 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(chunkStart + 14));
            }
            else if (chunkId == 0x64617461) // "data"
            {
                if (format != 1 || (bits != 8 && bits != 16) || channels < 1)
                {
                    _log.Warning("sci.sound", $"Unsupported WAVE format {format}, {bits} bits, {channels} channels");
                    return null;
                }

                int available = Math.Min(chunkSize, data.Length - chunkStart);
                int bytesPerFrame = channels * bits / 8;
                int frames = available / bytesPerFrame;
                var samples = new short[frames];
                for (int i = 0; i < frames; i++)
                {
                    int sum = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        int at = chunkStart + i * bytesPerFrame + c * bits / 8;
                        sum += bits == 8 ? (data[at] - 128) << 8 : BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(at));
                    }

                    samples[i] = (short)(sum / channels);
                }

                _audioRate = (ushort)rate;
                return new PcmSample(samples, rate);
            }

            pos = chunkStart + chunkSize + (chunkSize & 1);
        }

        _log.Warning("sci.sound", "WAVE resource without a data chunk");
        return null;
    }
}
