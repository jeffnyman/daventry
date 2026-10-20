using Daventry.Core.Graphics;
using Daventry.Core.Input;
using Daventry.Core.Storage;

namespace Daventry.Core.Hosting;

/// <summary>
/// A host with no window, no audio device and scripted input. Keeps the last presented frame and
/// palette so tests and command line tools can inspect what an engine drew.
/// </summary>
public sealed class HeadlessHost : IHost
{
    public HeadlessHost(ILog? log = null, ISaveStore? saves = null)
    {
        Log = log ?? NullLog.Instance;
        Saves = saves ?? new MemorySaveStore();
    }

    public HeadlessGraphics Graphics { get; } = new();

    public HeadlessInput Input { get; } = new();

    public HeadlessAudio Audio { get; } = new();

    public ISaveStore Saves { get; }

    public ILog Log { get; }

    IGraphicsDriver IHost.Graphics => Graphics;

    IInputDriver IHost.Input => Input;

    IAudioDriver IHost.Audio => Audio;

    /// <summary>Default simulated time between ticks.</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromMilliseconds(1000.0 / 60);

    /// <summary>Initialises the engine and runs up to <paramref name="maxTicks"/> ticks. Returns the number of ticks run.</summary>
    public int Run(Engine engine, int maxTicks)
    {
        engine.Initialize();
        return Continue(engine, maxTicks);
    }

    /// <summary>Runs more ticks on an already initialised engine.</summary>
    public int Continue(Engine engine, int maxTicks)
    {
        int ticks = 0;
        while (ticks < maxTicks)
        {
            var result = engine.Tick(ticks == 0 ? TimeSpan.Zero : TickInterval);
            ticks++;
            if (!result.Continue)
            {
                break;
            }
        }

        return ticks;
    }
}

public sealed class HeadlessGraphics : IGraphicsDriver
{
    private readonly Rgb[] _palette = new Rgb[256];

    public int Width { get; private set; }

    public int Height { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public ReadOnlySpan<Rgb> Colors => _palette;

    /// <summary>A copy of the most recently presented frame, or null before the first present.</summary>
    public Surface8? LastFrame { get; private set; }

    public int PresentCount { get; private set; }

    public void SetMode(int width, int height)
    {
        Width = width;
        Height = height;
        LastFrame = null;
    }

    public void SetPalette(ReadOnlySpan<Rgb> colors, int start = 0) => colors.CopyTo(_palette.AsSpan(start));

    public void Present(Surface8 frame)
    {
        if (LastFrame is null || LastFrame.Width != frame.Width || LastFrame.Height != frame.Height)
        {
            LastFrame = new Surface8(frame.Width, frame.Height);
        }

        LastFrame.CopyFrom(frame);
        PresentCount++;
    }

    public void SetTitle(string title) => Title = title;

    public byte[] EncodePng(int scaleX = 1, int scaleY = 1)
    {
        if (LastFrame is null)
        {
            throw new InvalidOperationException("Nothing has been presented yet.");
        }

        return PngWriter.Encode(LastFrame, _palette, scaleX, scaleY);
    }
}

public sealed class HeadlessInput : IInputDriver
{
    private readonly Queue<InputEvent> _queue = new();

    public MouseState Mouse { get; set; }

    public void Push(InputEvent inputEvent) => _queue.Enqueue(inputEvent);

    public void PushKey(Key key, KeyModifiers modifiers = KeyModifiers.None, char ascii = default)
    {
        _queue.Enqueue(InputEvent.KeyDown(key, modifiers, ascii));
        _queue.Enqueue(InputEvent.KeyUp(key, modifiers, ascii));
    }

    public bool TryRead(out InputEvent inputEvent) => _queue.TryDequeue(out inputEvent);

    public MouseState GetMouseState() => Mouse;
}

public sealed class HeadlessAudio : IAudioDriver
{
    public int SampleRate => 44100;

    public IAudioSource? Source { get; private set; }

    public void Start(IAudioSource source) => Source = source;

    public void Stop() => Source = null;

    /// <summary>Pulls <paramref name="frames"/> stereo frames from the current source, for tests that check sound output.</summary>
    public short[] Pull(int frames)
    {
        var buffer = new short[frames * 2];
        Source?.Read(buffer);
        return buffer;
    }
}
