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
    /// <summary>A headless host; the saves go to memory unless a store is given.</summary>
    public HeadlessHost(ILog? log = null, ISaveStore? saves = null)
    {
        Log = log ?? NullLog.Instance;
        Saves = saves ?? new MemorySaveStore();
    }

    /// <summary>The frames presented, kept for inspection.</summary>
    public HeadlessGraphics Graphics { get; } = new();

    /// <summary>The scripted input.</summary>
    public HeadlessInput Input { get; } = new();

    /// <summary>The sound, pulled when asked for.</summary>
    public HeadlessAudio Audio { get; } = new();

    /// <inheritdoc/>
    public ISaveStore Saves { get; }

    /// <inheritdoc/>
    public ILog Log { get; }

    /// <summary>On by default: a headless run is the same every time, whatever the machine is doing.</summary>
    public bool DeterministicTime { get; set; } = true;

    /// <summary>The games' random numbers follow this seed; null for a different sequence every run.</summary>
    public int? RandomSeed { get; set; } = 1;

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

    /// <summary>
    /// Clicks as a hand does, running the engine meanwhile: the mouse moves to (<paramref name="x"/>,
    /// <paramref name="y"/>), the button goes down <paramref name="holdTicks"/> ticks later, comes up
    /// as many after that, and the game gets as many again to answer. A move, press and release in one
    /// tick is not a click to every game: SCI32 buttons take a press only on something the mouse has
    /// hovered over, and follow the mouse between the press and the release. Returns false if the
    /// game quit meanwhile.
    /// </summary>
    public bool Click(Engine engine, int x, int y, MouseButton button = MouseButton.Left, int holdTicks = 6)
    {
        Input.Mouse = new MouseState(x, y, false, false, false);
        Input.Push(InputEvent.MouseMove(x, y));
        if (!Step(engine, holdTicks))
        {
            return false;
        }

        Input.Mouse = new MouseState(x, y, button == MouseButton.Left, button == MouseButton.Right, button == MouseButton.Middle);
        Input.Push(InputEvent.MouseDown(button, x, y));
        if (!Step(engine, holdTicks))
        {
            return false;
        }

        Input.Mouse = new MouseState(x, y, false, false, false);
        Input.Push(InputEvent.MouseUp(button, x, y));
        return Step(engine, holdTicks);
    }

    private bool Step(Engine engine, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            if (!engine.Tick(TickInterval).Continue)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>The headless host's display: keeps the last indexed and true-colour frames and the palette.</summary>
public sealed class HeadlessGraphics : ITrueColorGraphicsDriver
{
    private readonly Rgb[] _palette = new Rgb[256];

    /// <summary>The width of the mode set.</summary>
    public int Width { get; private set; }

    /// <summary>The height of the mode set.</summary>
    public int Height { get; private set; }

    /// <summary>The title the engine set.</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>The palette, all 256 entries.</summary>
    public ReadOnlySpan<Rgb> Colors => _palette;

    /// <summary>A copy of the most recently presented frame, or null before the first present.</summary>
    public Surface8? LastFrame { get; private set; }

    /// <summary>A copy of the most recently presented true-colour frame, or null before the first.</summary>
    public Surface32? LastTrueColorFrame { get; private set; }

    /// <summary>Whether the most recent present was a true-colour frame, which then is what the host shows.</summary>
    public bool ShowsTrueColor { get; private set; }

    /// <summary>The number of indexed frames presented.</summary>
    public int PresentCount { get; private set; }

    /// <summary>The number of true-colour frames presented.</summary>
    public int TrueColorPresentCount { get; private set; }

    /// <summary>Whether anything has been presented since the mode was set: until then <see cref="EncodePng"/> and <see cref="CaptureScreen"/> throw.</summary>
    public bool HasFrame => LastFrame is not null || LastTrueColorFrame is not null;

    /// <inheritdoc/>
    public void SetMode(int width, int height)
    {
        Width = width;
        Height = height;
        LastFrame = null;
        LastTrueColorFrame = null;
        ShowsTrueColor = false;
    }

    /// <inheritdoc/>
    public void SetPalette(ReadOnlySpan<Rgb> colors, int start = 0) => colors.CopyTo(_palette.AsSpan(start));

    /// <inheritdoc/>
    public void Present(Surface8 frame)
    {
        if (LastFrame is null || LastFrame.Width != frame.Width || LastFrame.Height != frame.Height)
        {
            LastFrame = new Surface8(frame.Width, frame.Height);
        }

        LastFrame.CopyFrom(frame);
        ShowsTrueColor = false;
        PresentCount++;
    }

    /// <inheritdoc/>
    public void Present(Surface32 frame)
    {
        if (LastTrueColorFrame is null || LastTrueColorFrame.Width != frame.Width || LastTrueColorFrame.Height != frame.Height)
        {
            LastTrueColorFrame = new Surface32(frame.Width, frame.Height);
        }

        LastTrueColorFrame.CopyFrom(frame);
        ShowsTrueColor = true;
        TrueColorPresentCount++;
    }

    /// <inheritdoc/>
    public void SetTitle(string title) => Title = title;

    /// <summary>A copy of what the host shows, in true colour whichever way it was presented.</summary>
    public Surface32 CaptureScreen()
    {
        if ((ShowsTrueColor || LastFrame is null) && LastTrueColorFrame is not null)
        {
            var copy = new Surface32(LastTrueColorFrame.Width, LastTrueColorFrame.Height);
            copy.CopyFrom(LastTrueColorFrame);
            return copy;
        }

        if (LastFrame is null)
        {
            throw new InvalidOperationException("Nothing has been presented yet.");
        }

        var screen = new Surface32(LastFrame.Width, LastFrame.Height);
        screen.FromIndexed(LastFrame, _palette);
        return screen;
    }

    /// <summary>Encodes what the host shows: the last true-colour frame if that came last, else the indexed one.</summary>
    public byte[] EncodePng(int scaleX = 1, int scaleY = 1, bool aspectRatioCorrection = false)
    {
        if ((ShowsTrueColor || LastFrame is null) && LastTrueColorFrame is not null)
        {
            return PngWriter.Encode(LastTrueColorFrame, scaleX, scaleY, aspectRatioCorrection);
        }

        if (LastFrame is null)
        {
            throw new InvalidOperationException("Nothing has been presented yet.");
        }

        return PngWriter.Encode(LastFrame, _palette, scaleX, scaleY, aspectRatioCorrection);
    }
}

/// <summary>The headless host's input: a queue the caller fills, and a mouse state it sets.</summary>
public sealed class HeadlessInput : IInputDriver
{
    private readonly Queue<InputEvent> _queue = new();

    /// <summary>The mouse state the engine reads; set it along with the mouse events pushed.</summary>
    public MouseState Mouse { get; set; }

    /// <summary>Queues an event for the engine.</summary>
    public void Push(InputEvent inputEvent) => _queue.Enqueue(inputEvent);

    /// <summary>
    /// Presses and releases a key. Games read typed text from <paramref name="ascii"/>: a letter
    /// pushed without its character types nothing (<see cref="Type"/> fills it in).
    /// </summary>
    public void PushKey(Key key, KeyModifiers modifiers = KeyModifiers.None, char ascii = default)
    {
        _queue.Enqueue(InputEvent.KeyDown(key, modifiers, ascii));
        _queue.Enqueue(InputEvent.KeyUp(key, modifiers, ascii));
    }

    /// <summary>Types text, a key press for each character as <see cref="KeyText.FromChar"/> makes it; a carriage return or line feed presses Enter.</summary>
    public void Type(string text)
    {
        foreach (char c in text)
        {
            var (key, modifiers, ascii) = KeyText.FromChar(c);
            PushKey(key, modifiers, ascii);
        }
    }

    /// <inheritdoc/>
    public bool TryRead(out InputEvent inputEvent) => _queue.TryDequeue(out inputEvent);

    /// <inheritdoc/>
    public MouseState GetMouseState() => Mouse;
}

/// <summary>The headless host's sound: nothing is played, and <see cref="Pull"/> reads the engine's samples.</summary>
public sealed class HeadlessAudio : IAudioDriver
{
    /// <inheritdoc/>
    public int SampleRate => 44100;

    /// <summary>The engine's sound, or null before it started or after it stopped.</summary>
    public IAudioSource? Source { get; private set; }

    /// <inheritdoc/>
    public int Volume { get; set; } = AudioVolume.Default;

    /// <inheritdoc/>
    public void Start(IAudioSource source) => Source = source;

    /// <inheritdoc/>
    public void Stop() => Source = null;

    /// <summary>Pulls <paramref name="frames"/> stereo frames from the current source, for tests that check sound output.</summary>
    public short[] Pull(int frames)
    {
        var buffer = new short[frames * 2];
        Source?.Read(buffer);
        AudioVolume.Apply(buffer, Volume);
        return buffer;
    }
}
