using Daventry.Core.Hosting;

namespace Daventry.Audio;

/// <summary>
/// Wraps an audio source and keeps it running in step with engine time when the host reads no
/// audio: headless hosts and tests never pull samples, so anything the engine times by audio
/// (music cues, speech length) would stall. <see cref="Update"/> is called once per engine tick;
/// after a quarter of a second without a host read it renders and discards the missing audio.
/// </summary>
public sealed class ClockedSource : IAudioSource
{
    private const int DiscardFrames = 1024;

    private readonly IAudioSource _inner;
    private readonly int _sampleRate;
    private long _framesRendered;
    private bool _selfClocked;
    private short[]? _discard;

    public ClockedSource(IAudioSource inner, int sampleRate)
    {
        _inner = inner;
        _sampleRate = sampleRate;
    }

    /// <summary>Stereo frames rendered so far, by the host or by the self clock.</summary>
    public long FramesRendered => Volatile.Read(ref _framesRendered);

    /// <summary>True while the engine clocks the audio itself because the host reads none.</summary>
    public bool IsSelfClocked => _selfClocked;

    public void Read(Span<short> interleavedStereo)
    {
        _inner.Read(interleavedStereo);
        Interlocked.Add(ref _framesRendered, interleavedStereo.Length / 2);
    }

    /// <summary>Renders whatever audio engine time has passed without the host reading it.</summary>
    public void Update(TimeSpan engineTime)
    {
        long expected = (long)(engineTime.TotalSeconds * _sampleRate);
        long rendered = FramesRendered;

        if (_selfClocked)
        {
            if (rendered > expected)
            {
                // the host has started consuming audio after all
                _selfClocked = false;
            }
        }
        else if (rendered + _sampleRate / 4 < expected)
        {
            // nobody has read audio for a quarter of a second: this host is silent
            _selfClocked = true;
        }

        if (_selfClocked)
        {
            _discard ??= new short[DiscardFrames * 2];
            while (rendered < expected)
            {
                int frames = (int)Math.Min(DiscardFrames, expected - rendered);
                Read(_discard.AsSpan(0, frames * 2));
                rendered += frames;
            }
        }
    }
}
