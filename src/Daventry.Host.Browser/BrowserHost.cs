using System.Diagnostics;
using System.Runtime.Versioning;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;

namespace Daventry.Host.Browser;

public sealed class BrowserHostOptions
{
    public ILog Log { get; set; } = NullLog.Instance;

    public ISaveStore? Saves { get; set; }

    public int Volume { get; set; } = AudioVolume.Default;

    public bool AspectRatioCorrection { get; set; } = true;

    public int SampleRate { get; set; } = 44100;

    /// <summary>The URL of the host's JavaScript module; null for the default next to the page.</summary>
    public string? ModuleUrl { get; set; }
}

/// <summary>
/// The engines' host in a browser page: a canvas for the frames, Web Audio for the sound, the
/// canvas's keyboard and mouse for input, local storage for saved games, and the page's animation
/// frames as the clock. One host drives one canvas; <see cref="Run"/> starts an engine on it and
/// every animation frame ticks the engine when it is due.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class BrowserHost : IHost, IDisposable
{
    private readonly TickScheduler _scheduler = new();
    private readonly Action<double> _onFrame;
    private readonly Action<bool, string, int> _onKey;
    private readonly Action<int, int, int> _onMouse;
    private long _lastTickTimestamp;
    private bool _firstTick;
    private bool _disposed;
    private TimeSpan _tickTime;
    private long _statsTicks;
    private long _statsStart;

    private BrowserHost(BrowserHostOptions options)
    {
        Log = options.Log;
        Saves = options.Saves ?? new LocalStorageSaveStore(new JsBrowserStorage());
        Graphics = new CanvasGraphicsDriver { AspectRatioCorrection = options.AspectRatioCorrection };
        Audio = new WebAudioDriver(options.SampleRate) { Volume = AudioVolume.Clamp(options.Volume) };
        Input = new BrowserInputDriver(Graphics, Audio);

        // the delegates JavaScript calls back must outlive the call that handed them over
        _onFrame = OnFrame;
        _onKey = Input.OnKey;
        _onMouse = Input.OnMouse;
    }

    public CanvasGraphicsDriver Graphics { get; }

    public BrowserInputDriver Input { get; }

    public WebAudioDriver Audio { get; }

    public ISaveStore Saves { get; }

    public ILog Log { get; }

    Core.Graphics.IGraphicsDriver IHost.Graphics => Graphics;

    Core.Input.IInputDriver IHost.Input => Input;

    IAudioDriver IHost.Audio => Audio;

    /// <summary>The engine being ticked, or null between games.</summary>
    public Engine? Engine { get; private set; }

    public long Ticks { get; private set; }

    /// <summary>Raised when the engine stops, with the exception that stopped it, if any.</summary>
    public event Action<Exception?>? Stopped;

    /// <summary>Loads the JavaScript module, attaches to the canvas and starts the frame loop.</summary>
    public static async Task<BrowserHost> CreateAsync(string canvasId, BrowserHostOptions? options = null)
    {
        options ??= new BrowserHostOptions();
        await Interop.EnsureModuleAsync(options.ModuleUrl);
        var host = new BrowserHost(options);
        Interop.Start(canvasId, host._onFrame, host._onKey, host._onMouse);
        return host;
    }

    /// <summary>Initialises the engine and ticks it from the next animation frame on. Stops the previous engine first.</summary>
    public void Run(Engine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();

        engine.Initialize();
        Engine = engine;
        Ticks = 0;
        _tickTime = TimeSpan.Zero;
        _statsTicks = 0;
        _statsStart = Stopwatch.GetTimestamp();
        _firstTick = true;
        _scheduler.Reset();
        Interop.Focus();
    }

    /// <summary>Stops ticking and disposes the engine.</summary>
    public void Stop() => Stop(null);

    private void Stop(Exception? error)
    {
        if (Engine is not { } engine)
        {
            return;
        }

        Engine = null;
        Audio.Stop();
        engine.Dispose();
        Log.Info("host", $"Engine stopped after {Ticks} ticks.");
        Stopped?.Invoke(error);
    }

    private void OnFrame(double timestampMs)
    {
        if (Engine is not { } engine)
        {
            return;
        }

        var now = TimeSpan.FromMilliseconds(timestampMs);
        if (!_scheduler.IsDue(now))
        {
            return;
        }

        try
        {
            for (int i = 0; i < _scheduler.MaxTicksPerFrame; i++)
            {
                long timestamp = Stopwatch.GetTimestamp();
                var elapsed = _firstTick ? TimeSpan.Zero : _scheduler.ClampElapsed(Stopwatch.GetElapsedTime(_lastTickTimestamp, timestamp));
                _lastTickTimestamp = timestamp;
                _firstTick = false;

                var result = engine.Tick(elapsed);
                Ticks++;
                Audio.Pump();
                _tickTime += Stopwatch.GetElapsedTime(timestamp);
                if (++_statsTicks == 600)
                {
                    // the interpreters run on an interpreted runtime unless published ahead of
                    // time, so the cost of a tick is worth seeing in the console
                    var span = Stopwatch.GetElapsedTime(_statsStart);
                    Log.Info("host", $"{_statsTicks / span.TotalSeconds:F1} ticks/s, {_tickTime.TotalMilliseconds / _statsTicks:F1} ms per tick");
                    _statsTicks = 0;
                    _tickTime = TimeSpan.Zero;
                    _statsStart = Stopwatch.GetTimestamp();
                }

                if (!result.Continue)
                {
                    Stop();
                    return;
                }

                if (result.Delay > TimeSpan.Zero)
                {
                    _scheduler.After(now, result.Delay);
                    return;
                }
            }

            // the engine still wants more right away; the next frame is soon enough
            _scheduler.After(now, TimeSpan.Zero);
        }
        catch (Exception ex)
        {
            Log.Error("host", ex.ToString());
            Stop(ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        Interop.Stop();
    }
}
