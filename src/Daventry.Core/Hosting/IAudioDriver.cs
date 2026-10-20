namespace Daventry.Core.Hosting;

/// <summary>
/// Pull-model audio output. The host asks the engine's <see cref="IAudioSource"/> for samples when
/// its output buffer has room; the engine never blocks on audio.
/// </summary>
public interface IAudioDriver
{
    int SampleRate { get; }

    void Start(IAudioSource source);

    void Stop();
}

/// <summary>Produces interleaved stereo signed 16-bit samples. Must always fill the whole buffer.</summary>
public interface IAudioSource
{
    void Read(Span<short> interleavedStereo);
}
