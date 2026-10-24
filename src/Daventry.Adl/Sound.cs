using Daventry.Audio;

namespace Daventry.Adl;

/// <summary>A speaker tone: a frequency in Hz (0 for silence) and a length in milliseconds.</summary>
public readonly record struct Tone(double Frequency, double Length);

/// <summary>
/// The Apple II speaker as the ADL games drove it: a list of square-wave tones played one after
/// another. Port of ScummVM's Adl::Sound and its Speaker, which produces more accurate frequencies
/// than a PC-speaker emulation by placing each edge at its fractional sample position. ScummVM plays
/// these streams at volume 25 of 255, and so does this.
/// </summary>
public sealed class AdlSpeaker : MonoAudioSource
{
    private const int FracBits = 16;
    private const int FracOne = 1 << FracBits;
    private const int StreamVolume = 25;

    private readonly Lock _lock = new();
    private Tone[] _tones = [];
    private int _toneIndex;
    private int _samplesRem;
    private int _halfWaveLen, _halfWaveRem;
    private short _curSample = 32767;

    public AdlSpeaker(int sampleRate)
        : base(sampleRate)
    {
    }

    /// <summary>True until the last tone has been rendered.</summary>
    public bool IsPlaying
    {
        get
        {
            lock (_lock)
            {
                return _samplesRem != 0 || _toneIndex < _tones.Length;
            }
        }
    }

    /// <summary>Starts a list of tones, replacing whatever was playing.</summary>
    public void Play(IReadOnlyList<Tone> tones)
    {
        lock (_lock)
        {
            _tones = [.. tones];
            _toneIndex = 0;
            _samplesRem = 0;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _tones = [];
            _toneIndex = 0;
            _samplesRem = 0;
            _halfWaveLen = 0;
        }
    }

    protected override void RenderMono(Span<short> buffer)
    {
        lock (_lock)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                if (_samplesRem == 0)
                {
                    if (_toneIndex == _tones.Length)
                    {
                        // no more tones
                        buffer[offset..].Clear();
                        break;
                    }

                    var tone = _tones[_toneIndex++];
                    if (tone.Frequency == 0.0)
                    {
                        _halfWaveLen = 0;
                    }
                    else
                    {
                        StartTone(tone.Frequency);
                    }

                    _samplesRem = (int)(SampleRate * tone.Length / 1000);
                    continue;
                }

                int samples = Math.Min(buffer.Length - offset, _samplesRem);
                GenerateSamples(buffer.Slice(offset, samples));
                _samplesRem -= samples;
                offset += samples;
            }

            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] = (short)(buffer[i] * StreamVolume / 255);
            }
        }
    }

    private void StartTone(double frequency)
    {
        _halfWaveLen = _halfWaveRem = (int)(SampleRate / frequency / 2 * FracOne);

        if (_halfWaveLen < FracOne)
        {
            // tone out of range at this sample rate
            _halfWaveLen = 0;
        }
    }

    private void GenerateSamples(Span<short> buffer)
    {
        if (_halfWaveLen == 0)
        {
            buffer.Clear();
            return;
        }

        int offset = 0;
        while (offset < buffer.Length)
        {
            if (_halfWaveRem is >= 0 and < FracOne)
            {
                // rising or falling edge: switch level, and use the transition point's fraction for this sample
                _curSample = (short)~_curSample;
                buffer[offset++] = (short)(_halfWaveRem ^ _curSample);
                _halfWaveRem += _halfWaveLen - FracOne;
            }
            else
            {
                // high or low level: as many samples as we can
                int samples = Math.Min(buffer.Length - offset, _halfWaveRem >> FracBits);
                buffer.Slice(offset, samples).Fill(_curSample);
                offset += samples;
                _halfWaveRem -= samples << FracBits;
            }
        }
    }
}
