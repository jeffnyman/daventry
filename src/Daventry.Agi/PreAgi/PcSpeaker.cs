using Daventry.Audio;

namespace Daventry.Agi.PreAgi;

/// <summary>
/// A square-wave PC speaker, as the preAGI games drove it: one note at a time, for a given
/// number of milliseconds. Port of the parts of ScummVM's Audio::PCSpeaker that Mickey uses.
/// </summary>
public sealed class PcSpeaker : MonoAudioSource
{
    private const short Amplitude = 9000;

    private readonly object _lock = new();
    private int _frequency;
    private long _remainingFrames;
    private double _phase;

    public PcSpeaker(int sampleRate)
        : base(sampleRate)
    {
    }

    public bool IsPlaying
    {
        get
        {
            lock (_lock)
            {
                return _remainingFrames > 0;
            }
        }
    }

    /// <summary>Starts a note. A new note replaces the one playing.</summary>
    public void Play(int frequency, int lengthMs)
    {
        lock (_lock)
        {
            _frequency = Math.Max(frequency, 0);
            _remainingFrames = (long)lengthMs * SampleRate / 1000;
            _phase = 0;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _remainingFrames = 0;
        }
    }

    protected override void RenderMono(Span<short> buffer)
    {
        lock (_lock)
        {
            double step = _frequency / (double)SampleRate;
            for (int i = 0; i < buffer.Length; i++)
            {
                if (_remainingFrames <= 0 || _frequency == 0)
                {
                    buffer[i] = 0;
                    continue;
                }

                buffer[i] = _phase < 0.5 ? Amplitude : (short)-Amplitude;
                _phase += step;
                if (_phase >= 1.0)
                {
                    _phase -= 1.0;
                }

                _remainingFrames--;
            }
        }
    }
}
