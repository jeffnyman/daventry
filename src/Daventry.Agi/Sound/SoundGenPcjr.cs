using System.Buffers.Binary;

namespace Daventry.Agi.Sound;

/// <summary>
/// PCjr/Tandy 1000 three-voice-plus-noise synthesiser. Port of ScummVM's sound_pcjr.cpp, itself
/// based on NAGI. The sequencer feeds SN76496 register writes into four channels sixty times a
/// second and the channels render square waves or shift-register noise at the host sample rate.
/// </summary>
public sealed class SoundGenPcjr : SoundGen
{
    private const int ChannelCount = 4;
    private const int NoiseChannel = 3;
    private const int FrequencyDivider = 111844;
    private const int WhiteNoiseFeedback = 0x12000;
    private const int NoisePreset = 0x0F35;
    private const int SilentAttenuation = 0x0F;

    private enum GenType
    {
        Silence,
        Tone,
        Periodic,
        White,
    }

    /// <summary>Register state and note cursor of one voice, as the sequencer sees it.</summary>
    private struct SndGenChan
    {
        public int DataOffset;
        public int Duration;
        public bool Avail;
        public int DissolveCount;
        public int Attenuation;
        public int AttenuationCopy;
        public GenType GenType;
        public int FreqCount;
    }

    /// <summary>The renderer's copy of a voice, with the oscillator counters.</summary>
    private struct ToneChan
    {
        public bool Avail;
        public int NoteCount;
        public int FreqCount;
        public int FreqCountPrev;
        public int Atten;
        public GenType GenType;
        public int GenTypePrev;
        public int Count;
        public int Scale;
        public int Sign;
        public uint NoiseState;
        public int Feedback;
    }

    // "fade out" or possibly "dissolve", AGI v2.9xx
    private static readonly sbyte[] DissolveDataV2 =
    [
        -2, -3, -2, -1,
        0x00, 0x00,
        0x01, 0x01, 0x01, 0x01,
        0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02,
        0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03,
        0x04, 0x04, 0x04, 0x04,
        0x05, 0x05, 0x05, 0x05,
        0x06, 0x06, 0x06, 0x06, 0x06,
        0x07, 0x07, 0x07, 0x07,
        0x08, 0x08, 0x08, 0x08,
        0x09, 0x09, 0x09, 0x09,
        0x0A, 0x0A, 0x0A, 0x0A,
        0x0B, 0x0B, 0x0B, 0x0B, 0x0B, 0x0B,
        0x0C, 0x0C, 0x0C, 0x0C, 0x0C, 0x0C,
        0x0D,
        -100,
    ];

    // AGI v3
    private static readonly sbyte[] DissolveDataV3 =
    [
        -2, -3, -2, -1,
        0x00, 0x00, 0x00, 0x00, 0x00,
        0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
        0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02,
        0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03,
        0x04, 0x04, 0x04, 0x04, 0x04,
        0x05, 0x05, 0x05, 0x05, 0x05,
        0x06, 0x06, 0x06, 0x06, 0x06,
        0x07, 0x07, 0x07, 0x07,
        0x08, 0x08, 0x08, 0x08,
        0x09, 0x09, 0x09, 0x09,
        0x0A, 0x0A, 0x0A, 0x0A,
        0x0B, 0x0B, 0x0B, 0x0B, 0x0B, 0x0B,
        0x0C, 0x0C, 0x0C, 0x0C, 0x0C, 0x0C,
        0x0D,
        -100,
    ];

    // 2 dB steps: value /= 1.258925411794 each time
    private static readonly short[] VolumeTable =
    [
        32767, 26027, 20674, 16422, 13044, 10361, 8230, 6537, 5193, 4125, 3276, 2602, 2067, 1642, 1304, 0,
    ];

    private readonly Lock _lock = new();
    private readonly SndGenChan[] _channel = new SndGenChan[ChannelCount];
    private readonly ToneChan[] _tchannel = new ToneChan[ChannelCount];
    private readonly int _dissolveMethod;
    private readonly bool _isV1;
    private short[] _chanData = new short[10240];

    private byte[]? _data;
    private int _v1offset;
    private int _v1duration;
    private int _reg;

    /// <param name="interpreterVersion">AGI interpreter version, which selects the dissolve pattern and the AGIv1 sequencer.</param>
    /// <param name="sixteenBitNoise">Use a SEGA-like 16-bit periodic noise mask instead of the standard 15-bit one.</param>
    public SoundGenPcjr(int sampleRate, int interpreterVersion, bool sixteenBitNoise = false)
        : base(sampleRate)
    {
        // 0 = no dissolve, play notes for as long as they last (v2.4 and under)
        // 2 = v2.9+ games used a shorter dissolve
        // 3 = v3 games used this slightly longer dissolve pattern
        if (interpreterVersion >= 0x3000)
        {
            _dissolveMethod = 3;
        }
        else if (interpreterVersion >= 0x2900)
        {
            _dissolveMethod = 2;
        }
        else
        {
            _dissolveMethod = 0;
        }

        _isV1 = interpreterVersion <= 0x2001;
        PeriodicNoiseMask = sixteenBitNoise ? 0x10000 : 0x08000;
    }

    public int PeriodicNoiseMask { get; }

    public override void Play(AgiSound sound)
    {
        lock (_lock)
        {
            _data = sound.Data;
            for (int i = 0; i < ChannelCount; i++)
            {
                _channel[i].DataOffset = _isV1 ? 0 : sound.GetVoiceOffset(i % 4);
                _channel[i].Duration = 0;
                _channel[i].Avail = true;
                _channel[i].DissolveCount = 0xFFFF;
                _channel[i].Attenuation = 0;
                _channel[i].AttenuationCopy = 0;
                _channel[i].GenType = GenType.Silence;
                _channel[i].FreqCount = 0;

                _tchannel[i].Avail = true;
                _tchannel[i].NoteCount = 0;
                _tchannel[i].FreqCount = 250;
                _tchannel[i].FreqCountPrev = -1;
                _tchannel[i].Atten = SilentAttenuation;
                _tchannel[i].GenType = GenType.Tone;
                _tchannel[i].GenTypePrev = -1;
                _tchannel[i].Count = 0;
                _tchannel[i].Scale = 0;
                _tchannel[i].Sign = 0;
                _tchannel[i].NoiseState = 0;
                _tchannel[i].Feedback = 0;
            }

            _v1offset = 1;
            _v1duration = 0;
            _reg = 0;
            Finished = false;
        }
    }

    public override void Stop()
    {
        lock (_lock)
        {
            for (int i = 0; i < ChannelCount; i++)
            {
                _channel[i].Avail = false;
                _tchannel[i].Avail = false;
            }
        }
    }

    private int VolumeCalc(ref SndGenChan chan)
    {
        var dissolveData = _dissolveMethod == 2 ? DissolveDataV2 : DissolveDataV3;

        int attenuation = chan.Attenuation;
        if (attenuation != SilentAttenuation)
        {
            if (chan.DissolveCount != 0xFFFF)
            {
                int dissolveValue = dissolveData[chan.DissolveCount];
                if (dissolveValue == -100)
                {
                    // end of list
                    chan.DissolveCount = 0xFFFF;
                    chan.Attenuation = chan.AttenuationCopy;
                    attenuation = chan.Attenuation;
                }
                else
                {
                    chan.DissolveCount++;

                    attenuation += dissolveValue;
                    attenuation = Math.Clamp(attenuation, 0, SilentAttenuation);
                    chan.AttenuationCopy = attenuation;
                    attenuation &= SilentAttenuation;
                }
            }

            // PCjr/Tandy hardware was quieter at the loud end
            if (attenuation < 8)
            {
                attenuation += 2;
            }
        }

        return attenuation;
    }

    private int GetNextNote(int ch) => _isV1 ? GetNextNoteV1(ch) : GetNextNoteV2(ch);

    /// <summary>
    /// Reads the next note of a voice into its registers. Returns 0 while the voice has data and
    /// -1 at the end of the voice (or when sound is off).
    /// </summary>
    private int GetNextNoteV2(int ch)
    {
        if (!SoundEnabled)
        {
            return -1;
        }

        ref var tpcm = ref _tchannel[ch];
        ref var chan = ref _channel[ch];
        if (!chan.Avail)
        {
            return -1;
        }

        var data = _data!;
        while (chan.Duration <= 0)
        {
            int offset = chan.DataOffset;

            // duration of the note; 0 means not played, 0xFFFF means the voice has finished
            chan.Duration = offset + 5 <= data.Length ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset)) : 0xFFFF;
            if (chan.Duration == 0 || chan.Duration == 0xFFFF)
            {
                tpcm.GenTypePrev = -1;
                tpcm.FreqCountPrev = -1;
                break;
            }

            tpcm.GenTypePrev = (int)tpcm.GenType;
            tpcm.FreqCountPrev = tpcm.FreqCount;

            // only tone channels dissolve
            if (ch != NoiseChannel && _dissolveMethod != 0)
            {
                chan.DissolveCount = 0;
            }

            // attenuation (volume), then frequency
            WriteData(data[offset + 4]);
            WriteData(data[offset + 3]);
            WriteData(data[offset + 2]);

            chan.DataOffset += 5;
        }

        if (chan.Duration == 0xFFFF)
        {
            // kill channel
            chan.Avail = false;
            chan.Attenuation = SilentAttenuation;
            chan.AttenuationCopy = SilentAttenuation;
            return -1;
        }

        chan.Duration--;
        return 0;
    }

    private int GetNextNoteV1(int ch)
    {
        if (!SoundEnabled)
        {
            return -1;
        }

        var data = _data;
        if (data is null || _v1offset >= data.Length)
        {
            _channel[ch].Avail = false;
            _channel[ch].Attenuation = SilentAttenuation;
            _channel[ch].AttenuationCopy = SilentAttenuation;
            return -1;
        }

        // In the V1 player the default duration for a row is 3 ticks
        if (_v1duration > 0)
        {
            _v1duration--;
            return 0;
        }

        _v1duration = 3 * ChannelCount;

        // Otherwise fetch a row of register writes for all channels, terminated by a zero
        int offset = _v1offset;
        while (offset < data.Length && data[offset] != 0)
        {
            WriteData(data[offset]);
            offset++;
        }

        _v1offset = offset + 1;
        return 0;
    }

    /// <summary>One SN76496 register write.</summary>
    private void WriteData(byte val)
    {
        if ((val & 0x90) == 0x90)
        {
            _reg = (val >> 5) & 0x3;
            _channel[_reg].Attenuation = val & 0xF;
        }
        else if ((val & 0xF0) == 0xE0)
        {
            _channel[NoiseChannel].GenType = (val & 0x4) != 0 ? GenType.White : GenType.Periodic;
            _channel[NoiseChannel].FreqCount = (val & 0x03) switch
            {
                0 => 32,
                1 => 64,
                2 => 128,
                _ => _channel[2].FreqCount * 2,
            };
        }
        else if ((val & 0x80) != 0)
        {
            _reg = (val >> 5) & 0x3;
            _channel[_reg].FreqCount = val & 0xF;
            _channel[_reg].GenType = GenType.Tone;
        }
        else
        {
            _channel[_reg].FreqCount |= (val & 0x3F) << 4;
        }
    }

    /// <summary>
    /// Renders one voice. Returns true while the voice is still alive at the end of the buffer.
    /// ScummVM instead reports whether a new note was fetched during the buffer, which only works
    /// because its buffers are longer than a note tick; here a buffer may hold just the tail of a note.
    /// </summary>
    private bool ChanGen(int chan, Span<short> stream)
    {
        ref var tpcm = ref _tchannel[chan];
        int position = 0;
        int len = stream.Length;

        while (len > 0)
        {
            if (tpcm.NoteCount <= 0)
            {
                // get new tone data
                if (tpcm.Avail && GetNextNote(chan) == 0)
                {
                    tpcm.Atten = VolumeCalc(ref _channel[chan]);
                    tpcm.FreqCount = _channel[chan].FreqCount;
                    tpcm.GenType = _channel[chan].GenType;

                    // tone changes 60 times per second
                    tpcm.NoteCount = SampleRate / 60;
                }
                else
                {
                    tpcm.GenType = GenType.Silence;
                    tpcm.NoteCount = len;
                    tpcm.Avail = false;
                }
            }

            if (tpcm.FreqCount == 0 || tpcm.Atten == SilentAttenuation)
            {
                tpcm.GenType = GenType.Silence;
            }

            int fillSize = Math.Min(tpcm.NoteCount, len);
            var slice = stream.Slice(position, fillSize);
            switch (tpcm.GenType)
            {
                case GenType.Tone:
                    FillSquare(ref tpcm, slice);
                    break;
                case GenType.Periodic:
                case GenType.White:
                    FillNoise(ref tpcm, slice);
                    break;
                default:
                    slice.Clear();
                    break;
            }

            tpcm.NoteCount -= fillSize;
            position += fillSize;
            len -= fillSize;
        }

        return tpcm.Avail;
    }

    private void FillSquare(ref ToneChan t, Span<short> buf)
    {
        if ((int)t.GenType != t.GenTypePrev)
        {
            // make sure the freqCount is checked
            t.FreqCountPrev = -1;
            t.Sign = 1;
            t.GenTypePrev = (int)t.GenType;
        }

        if (t.FreqCount != t.FreqCountPrev)
        {
            t.Scale = (SampleRate / 2) * t.FreqCount;
            t.FreqCountPrev = t.FreqCount;
        }

        short amp = VolumeTable[t.Atten];
        for (int i = 0; i < buf.Length; i++)
        {
            buf[i] = t.Sign != 0 ? amp : (short)-amp;

            t.Count -= FrequencyDivider;
            while (t.Count <= 0)
            {
                t.Sign ^= 1;
                t.Count += t.Scale;
            }
        }
    }

    private void FillNoise(ref ToneChan t, Span<short> buf)
    {
        if ((int)t.GenType != t.GenTypePrev)
        {
            t.FreqCountPrev = -1;
            t.GenTypePrev = (int)t.GenType;
        }

        if (t.FreqCount != t.FreqCountPrev)
        {
            t.Scale = (SampleRate / 2) * t.FreqCount;
            t.Count = t.Scale;
            t.FreqCountPrev = t.FreqCount;

            t.Feedback = t.GenType == GenType.White ? WhiteNoiseFeedback : PeriodicNoiseMask;
            t.NoiseState = NoisePreset;
            t.Sign = (int)(t.NoiseState & 1);
        }

        short amp = VolumeTable[t.Atten];
        for (int i = 0; i < buf.Length; i++)
        {
            buf[i] = t.Sign != 0 ? amp : (short)-amp;

            t.Count -= FrequencyDivider;
            while (t.Count <= 0)
            {
                if ((t.NoiseState & 1) != 0)
                {
                    t.NoiseState ^= (uint)t.Feedback;
                }

                t.NoiseState >>= 1;
                t.Sign = (int)(t.NoiseState & 1);
                t.Count += t.Scale;
            }
        }
    }

    protected override void RenderMono(Span<short> buffer)
    {
        lock (_lock)
        {
            if (_chanData.Length < buffer.Length)
            {
                _chanData = new short[buffer.Length];
            }

            buffer.Clear();
            var chanData = _chanData.AsSpan(0, buffer.Length);

            bool finished = true;
            for (int i = 0; i < ChannelCount; i++)
            {
                bool alive = ChanGen(i, chanData);

                // divide by number of channels then add to stream
                for (int s = 0; s < buffer.Length; s++)
                {
                    buffer[s] += (short)(chanData[s] / ChannelCount);
                }

                finished &= !alive;
            }

            if (finished)
            {
                Finished = true;
            }
        }
    }
}
