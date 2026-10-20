using System.Buffers.Binary;

namespace Daventry.Agi.Sound;

/// <summary>
/// Sarien's soft synthesiser for AGI four-channel sounds. Port of ScummVM's sound_sarien.cpp:
/// wavetable voices with optional envelopes and chorus, stepped once per 410-sample block as in
/// the original (so about 54 steps a second rather than 60). Used for the PC speaker, Amiga,
/// Macintosh and "ramp" emulation modes; <see cref="SoundGenPcjr"/> is the default.
/// </summary>
public sealed class SoundGenSarien : SoundGen
{
    private const int ReferenceRate = 22050;
    private const int ReferenceBufferSize = 410;
    private const int WaveformSize = 64;
    private const int EnvDecay = 1000;
    private const int EnvSustain = 100;
    private const int EnvRelease = 7500;
    private const int NumChannels = 7;
    private const int FlagLoop = 0x0001;
    private const int FlagEnvelope = 0x0002;

    private enum Adsr
    {
        Release = 0,
        Sustain = 1,
        Decay = 2,
        Attack = 3,
    }

    private struct ChannelInfo
    {
        public int Ptr;
        public short[] Ins;
        public int Size;
        public uint Phase;
        public int Flags;
        public Adsr Adsr;
        public int Timer;
        public bool End;
        public uint Freq;
        public uint Vol;
        public uint Env;
    }

    private static readonly short[] WaveformRamp =
    [
        0, 8, 16, 24, 32, 40, 48, 56,
        64, 72, 80, 88, 96, 104, 112, 120,
        128, 136, 144, 152, 160, 168, 176, 184,
        192, 200, 208, 216, 224, 232, 240, 255,
        0, -248, -240, -232, -224, -216, -208, -200,
        -192, -184, -176, -168, -160, -152, -144, -136,
        -128, -120, -112, -104, -96, -88, -80, -72,
        -64, -56, -48, -40, -32, -24, -16, -8,
    ];

    private static readonly short[] WaveformSquare =
    [
        255, 230, 220, 220, 220, 220, 220, 220,
        220, 220, 220, 220, 220, 220, 220, 220,
        220, 220, 220, 220, 220, 220, 220, 220,
        220, 220, 220, 220, 220, 220, 220, 110,
        -255, -230, -220, -220, -220, -220, -220, -220,
        -220, -220, -220, -220, -220, -220, -220, -220,
        -220, -220, -220, -220, -220, -220, -220, -220,
        -220, -220, -220, -110, 0, 0, 0, 0,
    ];

    private static readonly short[] WaveformMac =
    [
        45, 110, 135, 161, 167, 173, 175, 176,
        156, 137, 123, 110, 91, 72, 35, -2,
        -60, -118, -142, -165, -170, -176, -177, -179,
        -177, -176, -164, -152, -117, -82, -17, 47,
        92, 137, 151, 166, 170, 173, 171, 169,
        151, 133, 116, 100, 72, 43, -7, -57,
        -99, -141, -156, -170, -174, -177, -178, -179,
        -175, -172, -165, -159, -137, -114, -67, -19,
    ];

    private readonly Lock _lock = new();
    private readonly ChannelInfo[] _chn = new ChannelInfo[NumChannels];
    private readonly short[] _waveform;
    private readonly bool _env;
    private readonly bool _useChorus = true;
    private readonly int _bufferSize;
    private readonly short[] _sndBuffer;
    private readonly Random _random = new();

    private byte[]? _data;
    private bool _playing;
    private bool _hasSound;
    private int _dataAvailable;
    private int _dataOffset;

    public SoundGenSarien(int sampleRate, AgiSoundEmulation emulation)
        : base(sampleRate)
    {
        Emulation = emulation;
        switch (emulation)
        {
            case AgiSoundEmulation.Amiga:
            case AgiSoundEmulation.PcSpeaker:
                _waveform = WaveformSquare;
                break;
            case AgiSoundEmulation.Mac:
                _waveform = WaveformMac;
                break;
            default:
                _waveform = WaveformRamp;
                _env = true;
                break;
        }

        _bufferSize = (int)((long)ReferenceBufferSize * sampleRate / ReferenceRate);
        _sndBuffer = new short[_bufferSize];
        for (int i = 0; i < NumChannels; i++)
        {
            _chn[i].Ins = _waveform;
        }
    }

    public AgiSoundEmulation Emulation { get; }

    public override void Play(AgiSound sound)
    {
        lock (_lock)
        {
            _data = sound.Data;
            _hasSound = true;
            for (int i = 0; i < NumChannels; i++)
            {
                _chn[i].Flags = FlagLoop;
                if (_env)
                {
                    _chn[i].Flags |= FlagEnvelope;
                    _chn[i].Adsr = Adsr.Attack;
                }

                _chn[i].Ins = _waveform;
                _chn[i].Size = WaveformSize;
                _chn[i].Ptr = sound.GetVoiceOffset(i % 4);
                _chn[i].Timer = 0;
                _chn[i].Vol = 0;
                _chn[i].End = false;
            }

            Array.Clear(_sndBuffer);
            _dataAvailable = 0;
            _dataOffset = 0;
            Finished = false;
        }
    }

    public override void Stop()
    {
        lock (_lock)
        {
            _hasSound = false;
            for (int i = 0; i < NumChannels; i++)
            {
                StopNote(i);
            }
        }
    }

    private void StopNote(int i)
    {
        _chn[i].Adsr = Adsr.Release;

        if (_useChorus && Emulation == AgiSoundEmulation.Ramp && i < 3)
        {
            StopNote(i + 4);
        }
    }

    private void PlayNote(int i, int freq, int vol)
    {
        if (!SoundEnabled)
        {
            vol = 0;
        }
        else if (vol != 0 && Emulation == AgiSoundEmulation.PcSpeaker)
        {
            vol = 160;
        }

        _chn[i].Phase = 0;
        _chn[i].Freq = (uint)freq;
        _chn[i].Vol = (uint)vol;
        _chn[i].Env = 0x10000;
        _chn[i].Adsr = Adsr.Attack;

        if (_useChorus && Emulation == AgiSoundEmulation.Ramp && i < 3)
        {
            int newfreq = freq * 1007 / 1000;
            if (freq == newfreq)
            {
                newfreq++;
            }

            PlayNote(i + 4, newfreq, vol * 2 / 3);
        }
    }

    /// <summary>One sequencer step: advances note timers and starts new notes.</summary>
    private void PlaySound()
    {
        if (!_hasSound)
        {
            return;
        }

        var data = _data!;
        _playing = false;
        int voices = Emulation == AgiSoundEmulation.PcSpeaker ? 1 : 4;
        for (int i = 0; i < voices; i++)
        {
            _playing |= !_chn[i].End;
            if (_chn[i].End)
            {
                continue;
            }

            // Read a single note without advancing the pointer
            int ptr = _chn[i].Ptr;
            int duration;
            int freqDiv;
            int attenuation;
            if (ptr + 5 <= data.Length)
            {
                duration = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(ptr));
                freqDiv = ((data[ptr + 2] & 0x3F) << 4) | (data[ptr + 3] & 0x0F);
                attenuation = data[ptr + 4] & 0x0F;
            }
            else
            {
                duration = 0xFFFF;
                freqDiv = 0;
                attenuation = 0x0F;
            }

            if (--_chn[i].Timer <= 0)
            {
                StopNote(i);

                if (freqDiv != 0)
                {
                    int volume = attenuation == 0x0F ? 0 : 0xFF - attenuation * 2;
                    PlayNote(i, freqDiv * 10, volume);
                }

                _chn[i].Timer = duration;
                if (_chn[i].Timer == 0xFFFF)
                {
                    _chn[i].End = true;
                    _chn[i].Vol = 0;
                    _chn[i].Env = 0;

                    if (_useChorus && Emulation == AgiSoundEmulation.Ramp && i < 3)
                    {
                        _chn[i + 4].Vol = 0;
                        _chn[i + 4].Env = 0;
                    }
                }

                _chn[i].Ptr += 5;
            }
        }

        if (!_playing)
        {
            Finished = true;
            _hasSound = false;
        }
    }

    /// <summary>Renders one block of all seven channels into the sound buffer.</summary>
    private void MixSound()
    {
        Array.Clear(_sndBuffer);
        if (!_playing || !_hasSound)
        {
            return;
        }

        for (int c = 0; c < NumChannels; c++)
        {
            ref var chn = ref _chn[c];
            if (chn.Vol == 0 || chn.Freq == 0)
            {
                continue;
            }

            int m = (chn.Flags & FlagEnvelope) != 0 ? (int)((chn.Vol * chn.Env) >> 16) : (int)chn.Vol;

            if (c != 3)
            {
                var src = chn.Ins;
                uint p = chn.Phase;
                uint step = (uint)(474400L * ReferenceRate / ((long)chn.Freq * SampleRate));
                uint wrap = (uint)chn.Size << 8;
                for (int i = 0; i < _bufferSize; i++)
                {
                    int index = (int)(p >> 8);
                    int b = src[index];
                    b += ((src[(index + 1) % chn.Size] - src[index]) * (int)(p & 0xFF)) >> 8;
                    _sndBuffer[i] += (short)((b * m) >> 4);

                    p += step;
                    if ((chn.Flags & FlagLoop) != 0)
                    {
                        p %= wrap;
                    }
                    else if (p >= wrap)
                    {
                        p = 0;
                        chn.Vol = 0;
                        chn.End = true;
                        break;
                    }
                }

                chn.Phase = p;
            }
            else
            {
                // Add white noise
                for (int i = 0; i < _bufferSize; i++)
                {
                    int b = _random.Next(256) - 128;
                    _sndBuffer[i] += (short)((b * m) >> 4);
                }
            }

            switch (chn.Adsr)
            {
                case Adsr.Attack:
                    // not implemented
                    chn.Adsr = Adsr.Decay;
                    break;
                case Adsr.Decay:
                    if (chn.Env > chn.Vol * EnvSustain + EnvDecay)
                    {
                        chn.Env -= EnvDecay;
                    }
                    else
                    {
                        chn.Env = chn.Vol * EnvSustain;
                        chn.Adsr = Adsr.Sustain;
                    }

                    break;
                case Adsr.Sustain:
                    break;
                case Adsr.Release:
                    chn.Env = chn.Env >= EnvRelease ? chn.Env - EnvRelease : 0;
                    break;
            }
        }
    }

    protected override void RenderMono(Span<short> buffer)
    {
        lock (_lock)
        {
            int position = 0;
            int len = buffer.Length;

            while (len > _dataAvailable)
            {
                _sndBuffer.AsSpan(_dataOffset, _dataAvailable).CopyTo(buffer.Slice(position));
                position += _dataAvailable;
                len -= _dataAvailable;

                PlaySound();
                MixSound();
                _dataAvailable = _bufferSize;
                _dataOffset = 0;
            }

            _sndBuffer.AsSpan(_dataOffset, len).CopyTo(buffer.Slice(position));
            _dataOffset += len;
            _dataAvailable -= len;
        }
    }
}
