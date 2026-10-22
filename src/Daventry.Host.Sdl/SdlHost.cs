using System.Diagnostics;
using Daventry.Core.Graphics;
using Daventry.Core.Hosting;
using Daventry.Core.Input;
using Daventry.Core.Storage;
using SDL;
using static SDL.SDL3;

namespace Daventry.Host.Sdl;

public sealed class SdlHostOptions
{
    public string Title { get; init; } = "Daventry";

    /// <summary>Integer scale factor for the window's initial size; the window can be resized afterwards.</summary>
    public int Scale { get; init; } = 3;

    /// <summary>Starts in full screen. Alt+Enter and F11 switch between full screen and the window at any time.</summary>
    public bool Fullscreen { get; init; }

    /// <summary>
    /// Shows 320x200 and 640x400 frames at 4:3, as a CRT did, instead of with square pixels.
    /// Ctrl+Alt+A switches it at any time.
    /// </summary>
    public bool AspectRatioCorrection { get; init; }

    public int SampleRate { get; init; } = 44100;

    public ILog Log { get; init; } = NullLog.Instance;

    public ISaveStore Saves { get; init; } = new MemorySaveStore();
}

/// <summary>
/// Desktop host: one SDL window, keyboard and mouse input, and audio output. Owns the main loop
/// that drives an <see cref="Engine"/> through <see cref="Engine.Tick"/>.
/// </summary>
public sealed class SdlHost : IHost, IDisposable
{
    private readonly SdlGraphicsDriver _graphics;
    private readonly SdlInputDriver _input;
    private readonly SdlAudioDriver _audio;
    private bool _disposed;

    public SdlHost(SdlHostOptions? options = null)
    {
        options ??= new SdlHostOptions();
        Log = options.Log;
        Saves = options.Saves;

        SdlException.ThrowIfFalse(
            SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO | SDL_InitFlags.SDL_INIT_AUDIO | SDL_InitFlags.SDL_INIT_EVENTS),
            "SDL_Init");

        _graphics = new SdlGraphicsDriver(options.Title, options.Scale, options.Fullscreen, options.AspectRatioCorrection);
        _input = new SdlInputDriver(_graphics);
        _audio = new SdlAudioDriver(options.SampleRate);
    }

    public IGraphicsDriver Graphics => _graphics;

    public IInputDriver Input => _input;

    public IAudioDriver Audio => _audio;

    public ISaveStore Saves { get; }

    public ILog Log { get; }

    /// <summary>Longest the loop will sleep between ticks, so input stays responsive whatever the engine asks for.</summary>
    public TimeSpan MaxSleep { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Runs the engine until it quits, the window is closed, or <paramref name="maxTicks"/> ticks
    /// have run. Returns the number of ticks executed.
    /// </summary>
    public long Run(Engine engine, int? maxTicks = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ObjectDisposedException.ThrowIf(_disposed, this);

        engine.Initialize();

        long ticks = 0;
        long last = Stopwatch.GetTimestamp();
        while (true)
        {
            _input.Pump();

            long now = Stopwatch.GetTimestamp();
            var elapsed = ticks == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(last, now);
            last = now;

            var result = engine.Tick(elapsed);
            ticks++;

            _audio.Pump();

            if (!result.Continue || _input.QuitRequested || (maxTicks.HasValue && ticks >= maxTicks.Value))
            {
                break;
            }

            // the delay counts from the start of the tick, so the time the tick and the pumps took
            // is already part of it; sleeping the whole delay on top would slow the game down by
            // the cost of each frame (noticeable in full screen, where the scaled blit is big)
            var remaining = result.Delay - Stopwatch.GetElapsedTime(now);
            if (remaining > TimeSpan.Zero)
            {
                var sleep = remaining < MaxSleep ? remaining : MaxSleep;
                SDL_Delay((uint)Math.Max(1, (int)sleep.TotalMilliseconds));
            }
        }

        Log.Info("host", $"Engine stopped after {ticks} ticks.");
        return ticks;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _audio.Dispose();
        _graphics.Dispose();
        SDL_Quit();
    }
}
