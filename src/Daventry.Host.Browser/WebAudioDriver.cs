using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Daventry.Core.Hosting;

namespace Daventry.Host.Browser;

/// <summary>
/// Pull-model audio on top of Web Audio. The host calls <see cref="Pump"/> every frame and the
/// driver schedules chunks from the engine's source until roughly <see cref="TargetLatency"/> is
/// queued ahead of the output clock. Everything runs on the main thread, like the SDL driver.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class WebAudioDriver(int sampleRate = 44100) : IAudioDriver
{
    private const int Channels = 2;
    private const int ChunkFrames = 1024;

    private readonly short[] _chunk = new short[ChunkFrames * Channels];
    private IAudioSource? _source;

    public int SampleRate { get; } = sampleRate;

    public TimeSpan TargetLatency { get; set; } = TimeSpan.FromMilliseconds(120);

    public int Volume { get; set; } = AudioVolume.Default;

    public void Start(IAudioSource source)
    {
        Stop();
        Interop.AudioStart(SampleRate);
        _source = source;
        Pump();
    }

    public void Stop()
    {
        if (_source is not null)
        {
            Interop.AudioStop();
            _source = null;
        }
    }

    public void Pump()
    {
        if (_source is null)
        {
            return;
        }

        double target = TargetLatency.TotalSeconds;
        int pushed = 0;
        while (Interop.AudioQueued() < target && pushed++ < 16)
        {
            _source.Read(_chunk);
            AudioVolume.Apply(_chunk, Volume);
            Interop.AudioPush(MemoryMarshal.AsBytes(_chunk.AsSpan()), ChunkFrames);
        }
    }
}
