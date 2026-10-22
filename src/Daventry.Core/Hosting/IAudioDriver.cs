namespace Daventry.Core.Hosting;

/// <summary>
/// Pull-model audio output. The host asks the engine's <see cref="IAudioSource"/> for samples when
/// its output buffer has room; the engine never blocks on audio.
/// </summary>
public interface IAudioDriver
{
    int SampleRate { get; }

    /// <summary>
    /// Master volume from 0 (silent) to <see cref="AudioVolume.Unity"/> (the engine's mix as is),
    /// applied to everything the source produces. Starts at <see cref="AudioVolume.Default"/>, the
    /// level ScummVM mixes at.
    /// </summary>
    int Volume { get; set; }

    void Start(IAudioSource source);

    void Stop();
}

/// <summary>Produces interleaved stereo signed 16-bit samples. Must always fill the whole buffer.</summary>
public interface IAudioSource
{
    void Read(Span<short> interleavedStereo);
}

/// <summary>The master volume scale shared by the hosts: 256 steps like ScummVM's mixer.</summary>
public static class AudioVolume
{
    public const int Unity = 256;

    /// <summary>ScummVM's default for music, effects and speech (192 of 256, 75%).</summary>
    public const int Default = 192;

    public static int Clamp(int volume) => Math.Clamp(volume, 0, Unity);

    public static int FromPercent(int percent) => Clamp(percent * Unity / 100);

    public static int ToPercent(int volume) => (Clamp(volume) * 100 + Unity / 2) / Unity;

    /// <summary>Scales samples in place.</summary>
    public static void Apply(Span<short> samples, int volume)
    {
        volume = Clamp(volume);
        if (volume == Unity)
        {
            return;
        }

        if (volume == 0)
        {
            samples.Clear();
            return;
        }

        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(samples[i] * volume / Unity);
        }
    }
}
