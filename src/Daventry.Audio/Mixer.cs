using Daventry.Core.Hosting;

namespace Daventry.Audio;

/// <summary>
/// Sums any number of <see cref="IAudioSource"/>s into one stereo stream with saturation.
/// Sources are read on the host's audio thread, so adding and removing is locked.
/// </summary>
public sealed class Mixer : IAudioSource
{
    private readonly Lock _lock = new();
    private readonly List<IAudioSource> _sources = [];
    private int[] _accumulator = [];
    private short[] _scratch = [];

    public Mixer(int sampleRate)
    {
        SampleRate = sampleRate;
    }

    public int SampleRate { get; }

    public void Add(IAudioSource source)
    {
        lock (_lock)
        {
            _sources.Add(source);
        }
    }

    public void Remove(IAudioSource source)
    {
        lock (_lock)
        {
            _sources.Remove(source);
        }
    }

    public void Read(Span<short> interleavedStereo)
    {
        lock (_lock)
        {
            if (_sources.Count == 0)
            {
                interleavedStereo.Clear();
                return;
            }

            if (_accumulator.Length < interleavedStereo.Length)
            {
                _accumulator = new int[interleavedStereo.Length];
                _scratch = new short[interleavedStereo.Length];
            }

            var acc = _accumulator.AsSpan(0, interleavedStereo.Length);
            var scratch = _scratch.AsSpan(0, interleavedStereo.Length);
            acc.Clear();

            foreach (var source in _sources)
            {
                source.Read(scratch);
                for (int i = 0; i < acc.Length; i++)
                {
                    acc[i] += scratch[i];
                }
            }

            for (int i = 0; i < acc.Length; i++)
            {
                interleavedStereo[i] = (short)Math.Clamp(acc[i], short.MinValue, short.MaxValue);
            }
        }
    }
}
