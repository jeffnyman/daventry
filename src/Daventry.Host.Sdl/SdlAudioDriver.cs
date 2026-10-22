using Daventry.Core.Hosting;
using SDL;
using static SDL.SDL3;

namespace Daventry.Host.Sdl;

/// <summary>
/// Push-model audio on top of an SDL3 audio stream. The host loop calls <see cref="Pump"/> every
/// iteration and the driver tops the device queue up to roughly <see cref="TargetLatency"/>.
/// Everything runs on the main thread, so engines need no audio locking.
/// </summary>
internal sealed unsafe class SdlAudioDriver : IAudioDriver, IDisposable
{
    private const int Channels = 2;
    private const int ChunkFrames = 512;

    private readonly short[] _chunk = new short[ChunkFrames * Channels];
    private SDL_AudioStream* _stream;
    private IAudioSource? _source;

    public SdlAudioDriver(int sampleRate)
    {
        SampleRate = sampleRate;
    }

    public int SampleRate { get; }

    public TimeSpan TargetLatency { get; set; } = TimeSpan.FromMilliseconds(80);

    public int Volume { get; set; } = AudioVolume.Default;

    public void Start(IAudioSource source)
    {
        Stop();

        var spec = new SDL_AudioSpec
        {
            format = SDL_AudioFormat.SDL_AUDIO_S16LE,
            channels = Channels,
            freq = SampleRate,
        };

        _stream = SdlException.ThrowIfNull(
            SDL_OpenAudioDeviceStream(SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &spec, null, IntPtr.Zero),
            "SDL_OpenAudioDeviceStream");
        _source = source;
        Pump();
        SdlException.ThrowIfFalse(SDL_ResumeAudioStreamDevice(_stream), "SDL_ResumeAudioStreamDevice");
    }

    public void Stop()
    {
        if (_stream is not null)
        {
            SDL_DestroyAudioStream(_stream);
            _stream = null;
        }

        _source = null;
    }

    public void Pump()
    {
        if (_stream is null || _source is null)
        {
            return;
        }

        int targetBytes = (int)(TargetLatency.TotalSeconds * SampleRate) * Channels * sizeof(short);
        int chunkBytes = _chunk.Length * sizeof(short);

        while (SDL_GetAudioStreamQueued(_stream) < targetBytes)
        {
            _source.Read(_chunk);
            AudioVolume.Apply(_chunk, Volume);
            fixed (short* p = _chunk)
            {
                SdlException.ThrowIfFalse(SDL_PutAudioStreamData(_stream, (IntPtr)p, chunkBytes), "SDL_PutAudioStreamData");
            }
        }
    }

    public void Dispose() => Stop();
}
