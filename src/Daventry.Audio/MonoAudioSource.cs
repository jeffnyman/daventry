using Daventry.Core.Hosting;

namespace Daventry.Audio;

/// <summary>
/// Base for synthesisers that render mono signed 16-bit audio at a fixed rate. Duplicates the
/// result into the host's interleaved stereo buffer, applies a master gain and counts frames so
/// an engine can tell whether the host is actually consuming audio.
/// </summary>
public abstract class MonoAudioSource : IAudioSource
{
    private const int UnityGain = 256;

    private short[] _mono = [];
    private long _framesRendered;
    private int _gain = UnityGain;

    protected MonoAudioSource(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        SampleRate = sampleRate;
    }

    public int SampleRate { get; }

    /// <summary>Master gain from 0 (silent) to 1 (unity). Safe to set from any thread.</summary>
    public float Gain
    {
        get => Volatile.Read(ref _gain) / (float)UnityGain;
        set => Volatile.Write(ref _gain, (int)Math.Clamp(value * UnityGain, 0, UnityGain));
    }

    /// <summary>Total stereo frames rendered so far through <see cref="Read"/>.</summary>
    public long FramesRendered => Volatile.Read(ref _framesRendered);

    public void Read(Span<short> interleavedStereo)
    {
        int frames = interleavedStereo.Length / 2;
        if (_mono.Length < frames)
        {
            _mono = new short[frames];
        }

        var mono = _mono.AsSpan(0, frames);
        RenderMono(mono);

        int gain = Volatile.Read(ref _gain);
        for (int i = 0; i < frames; i++)
        {
            short sample = gain == UnityGain ? mono[i] : (short)((mono[i] * gain) >> 8);
            interleavedStereo[2 * i] = sample;
            interleavedStereo[2 * i + 1] = sample;
        }

        Interlocked.Add(ref _framesRendered, frames);
    }

    /// <summary>Fills the whole buffer with mono samples at <see cref="SampleRate"/>.</summary>
    protected abstract void RenderMono(Span<short> buffer);
}
