using Daventry.Agi.Graphics;
using Daventry.Audio;
using Daventry.Core.Hosting;
using Daventry.Core.Input;
using Daventry.Core.Storage;

namespace Daventry.Agi.PreAgi;

/// <summary>What <see cref="PreAgiEngine.GetSelectionAsync"/> waits for.</summary>
public enum SelectionType
{
    YesNo,
    Number,
    Space,
    AnyKey,
    Backspace,
}

/// <summary>Options for controlling behaviour during waits and sound playback.</summary>
public enum WaitOptions
{
    /// <summary>No event processing, cannot be interrupted.</summary>
    Block = 0x00,

    /// <summary>Process events, stops on quit.</summary>
    ProcessEvents = 0x01,

    /// <summary>Process events, stops on input or quit.</summary>
    AllowInterrupt = 0x03,
}

/// <summary>
/// Base for the preAGI games: Mickey's Space Adventure, Winnie the Pooh and Troll's Tale. Port of
/// ScummVM's PreAgiEngine. These games were written before the AGI interpreter existed and have
/// their logic in the executable, so each one is a hand-written engine over a shared set of
/// text, input, timing and speaker helpers.
/// <para>
/// The game logic runs as an async method. Wherever the original blocked on input or a delay it
/// awaits the <see cref="Scheduler"/>; <see cref="Tick"/> queues host input, advances the
/// scheduler and presents the display.
/// </para>
/// </summary>
public abstract class PreAgiEngine : Engine
{
    public const int DefaultAttribute = 0x0F;
    public const int DefaultAttributeReversed = 0xF0;
    public const int ColorDefault = 0x1337;
    public const int MaxRowPic = 20;

    /// <summary>Every string drawn is logged at debug level under this category, for tests and tracing.</summary>
    public const string TextLogCategory = "agi.preagi.text";

    private const int OscillatorFrequency = 1193180;
    private const double TimerResolution = 0.0182;
    private const int PollIntervalMs = 10;
    private const int DiscardFrames = 1024;

    private readonly Queue<InputEvent> _events = new();
    private readonly Dictionary<(int Row, int Col), string> _lastTextAt = [];
    private readonly List<MonoAudioSource> _audioSources = [];
    private TimeSpan _engineTime;
    private Task? _task;
    private bool _quitRequested;
    private int _presentedPaletteVersion = -1;
    private bool _selfClocked;
    private short[]? _discard;

    protected PreAgiEngine(IHost host, IGameContainer game, AgiDetectedGame detected, AgiRenderMode renderMode = AgiRenderMode.Ega)
        : base(host, game, detected)
    {
        Description = detected;
        if (host.RandomSeed is { } seed)
        {
            Random = new Random(seed);
        }

        Gfx = new GfxMgr(renderMode, detected.Version, detected.Gid);
        Mixer = new Mixer(host.Audio.SampleRate);
        Speaker = new PcSpeaker(host.Audio.SampleRate);
        AddAudioSource(Speaker);
    }

    public AgiDetectedGame Description { get; }

    public GfxMgr Gfx { get; }

    public FrameScheduler Scheduler { get; } = new();

    /// <summary>Everything the engine plays, handed to the host audio driver in <see cref="Initialize"/>.</summary>
    public Mixer Mixer { get; }

    public PcSpeaker Speaker { get; }

    /// <summary>Seedable for reproducible tests.</summary>
    public Random Random { get; set; } = new();

    /// <summary>The games' sound toggle (Ctrl-S in Winnie, S in Mickey).</summary>
    public bool SoundOn { get; set; } = true;

    public bool ShouldQuit => _quitRequested;

    /// <summary>Time the engine has run, as the scheduler sees it.</summary>
    public TimeSpan EngineTime => _engineTime;

    /// <summary>True while the game waits for a menu choice; for hosts and tests that drive the game.</summary>
    public bool AwaitingMenuSelection { get; protected set; }

    /// <summary>True while the game waits for a key or click (a message is up).</summary>
    public bool AwaitingKey { get; protected set; }

    /// <summary>The text attribute <see cref="DrawStr"/> uses for <see cref="ColorDefault"/>.</summary>
    protected int DefaultColor { get; set; } = DefaultAttribute;

    public override void Initialize()
    {
        Gfx.InitVideo();
        Host.Graphics.SetMode(Gfx.DisplayScreenWidth, Gfx.DisplayScreenHeight);
        Host.Graphics.SetTitle($"Daventry - {Detected.Title}");
        PushPaletteIfChanged();
        StartAudio();

        // Runs synchronously until the first await, then returns here.
        using (NoSynchronizationContext.Enter())
        {
            _task = RunAsync();
        }
    }

    public override TickResult Tick(TimeSpan elapsed)
    {
        using var noContext = NoSynchronizationContext.Enter();
        _engineTime += elapsed;

        while (Host.Input.TryRead(out var inputEvent))
        {
            if (inputEvent.Type == InputEventType.Quit)
            {
                QuitGame();
                return TickResult.Quit;
            }

            _events.Enqueue(inputEvent);
        }

        ClockAudio();
        Scheduler.Advance(_engineTime);

        if (_task is { IsFaulted: true })
        {
            var inner = _task.Exception?.InnerException ?? _task.Exception!;
            throw new InvalidOperationException($"{Detected.Title} failed: {inner.Message}", inner);
        }

        PushPaletteIfChanged();
        if (Gfx.DisplayDirty)
        {
            Host.Graphics.Present(Gfx.Display);
            Gfx.DisplayDirty = false;
        }

        if (_task is null || _task.IsCompleted || ShouldQuit)
        {
            return TickResult.Quit;
        }

        var wakeAt = Scheduler.WakeAt ?? _engineTime;
        var delay = wakeAt - _engineTime;
        return delay > TimeSpan.Zero ? TickResult.After(delay) : TickResult.Immediately;
    }

    public override void Dispose()
    {
        Host.Audio.Stop();
        base.Dispose();
    }

    public void QuitGame() => _quitRequested = true;

    /// <summary>The game: intro and main loop. Port of the engines' go().</summary>
    protected abstract Task RunAsync();

    /// <summary>Adds a synthesiser to the mixer and to the sources clocked when the host does not read audio.</summary>
    protected void AddAudioSource(MonoAudioSource source)
    {
        Mixer.Add(source);
        _audioSources.Add(source);
    }

    // ---- files ----

    /// <summary>Reads a game file, or returns null with a warning when it is missing.</summary>
    protected byte[]? TryReadFile(string name)
    {
        if (!Game.Exists(name))
        {
            Host.Log.Warning("agi.preagi", $"Could not open file '{name}'");
            return null;
        }

        return Game.ReadAllBytes(name);
    }

    /// <summary>A NUL-terminated string from a buffer, one char per byte.</summary>
    protected static string ReadCString(ReadOnlySpan<byte> buffer, int offset, int maxLength = int.MaxValue)
    {
        if (offset < 0 || offset >= buffer.Length)
        {
            return string.Empty;
        }

        int end = offset;
        while (end < buffer.Length && end - offset < maxLength && buffer[end] != 0)
        {
            end++;
        }

        return Latin1(buffer[offset..end]);
    }

    protected static string Latin1(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            chars[i] = (char)bytes[i];
        }

        return new string(chars);
    }

    // ---- screen ----

    /// <summary>Random number between 1 and max. Rnd(2) returns 1 or 2.</summary>
    public int Rnd(int max) => Random.Next(max) + 1;

    public void ClearScreen(int attr, bool overrideDefault = true)
    {
        if (overrideDefault)
        {
            DefaultColor = attr;
        }

        Gfx.ClearDisplay((byte)((attr & 0xF0) >> 4));
    }

    public void ClearGfxScreen(int attr)
        => Gfx.DrawDisplayRect(0, 0, GfxMgr.DisplayDefaultWidth, MaxRowPic * 8, (byte)((attr & 0xF0) >> 4));

    public byte GetWhite() => Gfx.RenderMode switch
    {
        AgiRenderMode.Cga => 3,
        AgiRenderMode.HerculesAmber or AgiRenderMode.HerculesGreen => 1,
        _ => 15,
    };

    /// <summary>Draws text at a row and column with a text-mode attribute (background in the high nibble).</summary>
    public void DrawStr(int row, int col, int attr, string text)
    {
        if (attr == ColorDefault)
        {
            attr = DefaultColor;
        }

        byte foreground = (byte)(attr & 0x0F);
        byte background = (byte)(attr >> 4);

        // Simplistic CGA and Hercules mapping that handles all text in Mickey and Winnie. Troll
        // text is handled correctly in graphics mode, but the original switched to CGA 16 color
        // text mode for some parts, and we are not doing that.
        switch (Gfx.RenderMode)
        {
            case AgiRenderMode.Cga:
                if (foreground != 0)
                {
                    foreground = 3;
                }

                if (background == 15)
                {
                    background = 3;
                }

                break;
            case AgiRenderMode.HerculesAmber:
            case AgiRenderMode.HerculesGreen:
                if (foreground != 0)
                {
                    foreground = 1;
                }

                background = background == 0x0F ? (byte)1 : (byte)0;
                break;
        }

        // menus are redrawn every animation frame, so only a change at a position is logged
        if (!string.IsNullOrWhiteSpace(text) && (!_lastTextAt.TryGetValue((row, col), out var last) || last != text))
        {
            _lastTextAt[(row, col)] = text;
            Host.Log.Debug(TextLogCategory, text);
        }

        foreach (char c in text)
        {
            int code = c;
            switch (code)
            {
                case '\n':
                case '\r': // winnie
                case 0x8D:
                    if (++row == 200 / 8)
                    {
                        return;
                    }

                    col = 0;
                    break;
                case '|':
                    // swap attribute nibbles
                    break;
                default:
                    // the preAGI games never enter AGI's graphics text mode, so a background
                    // with bit 3 set is a bright colour, not inverse video
                    Gfx.DrawCharacter(row, col, (byte)code, foreground, background, false, gfxMode: false);
                    if (++col == 320 / 8)
                    {
                        col = 0;
                        if (++row == 200 / 8)
                        {
                            return;
                        }
                    }

                    break;
            }
        }

        Gfx.DisplayDirty = true;
    }

    /// <summary>The first text row the games write to; Troll's pictures are a row taller.</summary>
    protected virtual int TextAreaStart => MaxRowPic;

    public void ClearTextArea()
    {
        for (int row = TextAreaStart; row < 200 / 8; row++)
        {
            ClearRow(row);
        }
    }

    public void ClearRow(int row) => DrawStr(row, 0, DefaultAttribute, new string(' ', 40));

    public void PrintStr(string message)
    {
        ClearTextArea();
        DrawStr(21, 0, DefaultAttribute, message);
    }

    /// <summary>Clears the high bit of every character; the games store their text that way.</summary>
    public static string Xor80(string text)
    {
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if ((chars[i] & 0x80) != 0)
            {
                chars[i] = (char)(chars[i] ^ 0x80);
            }
        }

        return new string(chars);
    }

    public void PrintStrXor(string message) => PrintStr(Xor80(message));

    // ---- input ----

    /// <summary>True for a key event with Ctrl, Alt, Shift or a bare modifier key: the games ignore those.</summary>
    protected static bool HasNonStickyModifier(in InputEvent ev)
    {
        if ((ev.Modifiers & (KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta)) != 0)
        {
            return true;
        }

        return ev.Key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LeftMeta or Key.RightMeta
            or Key.NumLock or Key.CapsLock or Key.ScrollLock;
    }

    /// <summary>Takes the next queued host event, if any.</summary>
    protected bool TryDequeueEvent(out InputEvent ev) => _events.TryDequeue(out ev);

    /// <summary>Waits a poll interval. Every input loop yields through this.</summary>
    protected FrameScheduler.FrameAwaitable Poll() => Scheduler.Delay(PollIntervalMs);

    /// <summary>
    /// Waits for a selection of the given type. Yes/no returns 1 for yes, 0 for no and 2 for any
    /// other key; numbers return 1 to 9, or 10 for any other key; the others return 1 for a key or
    /// left click. A right click, escape or quit returns 0.
    /// </summary>
    protected async Task<int> GetSelectionAsync(SelectionType type)
    {
        AwaitingKey = true;
        try
        {
            return await GetSelectionCoreAsync(type);
        }
        finally
        {
            AwaitingKey = false;
        }
    }

    private async Task<int> GetSelectionCoreAsync(SelectionType type)
    {
        while (!ShouldQuit)
        {
            while (_events.TryDequeue(out var ev))
            {
                switch (ev.Type)
                {
                    case InputEventType.Quit:
                        return 0;
                    case InputEventType.MouseUp when ev.Button == MouseButton.Right:
                        return 0;
                    case InputEventType.MouseUp when ev.Button == MouseButton.Left:
                        if (type is SelectionType.YesNo or SelectionType.AnyKey or SelectionType.Backspace)
                        {
                            return 1;
                        }

                        break;
                    case InputEventType.KeyDown:
                        if (HasNonStickyModifier(ev))
                        {
                            break;
                        }

                        switch (ev.Key)
                        {
                            case Key.Y:
                                if (type == SelectionType.YesNo)
                                {
                                    return 1;
                                }

                                break;
                            case Key.N:
                                if (type == SelectionType.YesNo)
                                {
                                    return 0;
                                }

                                break;
                            case Key.Escape:
                                if (type is SelectionType.Number or SelectionType.AnyKey)
                                {
                                    return 0;
                                }

                                break;
                            case >= Key.D1 and <= Key.D9:
                                if (type == SelectionType.Number)
                                {
                                    return ev.Key - Key.D1 + 1;
                                }

                                break;
                            case Key.Space:
                                if (type == SelectionType.Space)
                                {
                                    return 1;
                                }

                                break;
                            case Key.Backspace:
                                if (type == SelectionType.Backspace)
                                {
                                    return 0;
                                }

                                break;
                        }

                        switch (type)
                        {
                            case SelectionType.YesNo:
                                return 2;
                            case SelectionType.Number:
                                return 10;
                            case SelectionType.AnyKey:
                            case SelectionType.Backspace:
                                return 1;
                        }

                        break;
                }
            }

            await Poll();
        }

        return 0;
    }

    /// <summary>
    /// Waits for a delay, optionally processing events and optionally letting keyboard and mouse
    /// events interrupt the wait. Returns false when interrupted by input or quit. Processing
    /// events keeps the window responsive, but for very short delays it may be better not to, so
    /// that they are buffered and not lost.
    /// </summary>
    protected async Task<bool> WaitAsync(int delayMs, WaitOptions options = WaitOptions.ProcessEvents)
    {
        var endTime = Scheduler.Now + TimeSpan.FromMilliseconds(delayMs);
        bool processEvents = (options & WaitOptions.ProcessEvents) != 0;
        bool allowInterrupt = options == WaitOptions.AllowInterrupt;

        while (!ShouldQuit)
        {
            if (processEvents)
            {
                while (_events.TryDequeue(out var ev))
                {
                    switch (ev.Type)
                    {
                        case InputEventType.KeyDown:
                            // don't interrupt if a modifier is pressed
                            if (HasNonStickyModifier(ev))
                            {
                                continue;
                            }

                            goto case InputEventType.MouseUp;
                        case InputEventType.MouseUp:
                            if (!allowInterrupt)
                            {
                                continue;
                            }

                            return false; // interrupted by input
                        case InputEventType.Quit:
                            return false;
                    }
                }
            }

            if (Scheduler.Now >= endTime)
            {
                return true; // delay completed
            }

            var remaining = endTime - Scheduler.Now;
            var poll = TimeSpan.FromMilliseconds(PollIntervalMs);
            await Scheduler.Delay(remaining < poll ? remaining : poll);
        }

        return false; // interrupted by quit
    }

    // ---- speaker ----

    /// <summary>Plays a square wave on the speaker for the note's length; a zero frequency is a pause.</summary>
    protected async Task<bool> PlaySpeakerNoteAsync(int frequency, int lengthMs, WaitOptions options)
    {
        if (frequency != 0)
        {
            Speaker.Play(frequency, lengthMs);
        }

        bool completed = await WaitAsync(lengthMs, options);
        if (!completed && frequency != 0)
        {
            Speaker.Stop();
        }

        return completed;
    }

    /// <summary>Converts a PIT counter and a timer-tick length to a speaker note. Mickey's sound data.</summary>
    protected Task<bool> PlayTimerNoteAsync(int counter, int length, WaitOptions options)
    {
        int frequency = counter != 0 ? OscillatorFrequency / counter : 0;
        int lengthMs = (int)(length / TimerResolution);
        return PlaySpeakerNoteAsync(frequency, lengthMs, options);
    }

    // ---- host plumbing ----

    private void StartAudio()
    {
        try
        {
            Host.Audio.Start(Mixer);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Host.Log.Warning("agi.preagi", $"Audio output unavailable, running silent: {ex.Message}");
        }
    }

    /// <summary>
    /// When nobody reads the mixer (headless hosts, tests) the synthesisers are clocked here so
    /// that sounds still finish; the same heuristic as the AGI sound manager.
    /// </summary>
    private void ClockAudio()
    {
        int sampleRate = Mixer.SampleRate;
        long expected = (long)(_engineTime.TotalSeconds * sampleRate);
        long rendered = Speaker.FramesRendered;
        if (_selfClocked)
        {
            if (rendered > expected)
            {
                // the host has started consuming audio after all
                _selfClocked = false;
            }
        }
        else if (rendered + sampleRate / 4 < expected)
        {
            // nobody has read audio for a quarter of a second: this host is silent
            _selfClocked = true;
        }

        if (!_selfClocked)
        {
            return;
        }

        _discard ??= new short[DiscardFrames * 2];
        while (rendered < expected)
        {
            int frames = (int)Math.Min(DiscardFrames, expected - rendered);
            foreach (var source in _audioSources)
            {
                source.Read(_discard.AsSpan(0, frames * 2));
            }

            rendered += frames;
        }
    }

    private void PushPaletteIfChanged()
    {
        if (Gfx.PaletteVersion != _presentedPaletteVersion)
        {
            Host.Graphics.SetPalette(Gfx.CurrentPalette);
            _presentedPaletteVersion = Gfx.PaletteVersion;
        }
    }
}
