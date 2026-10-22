using Daventry.Core.Storage;

namespace Daventry.Core.Hosting;

/// <summary>
/// What an engine wants the host to do after a tick. <see cref="Delay"/> counts from the moment
/// the host called <see cref="Engine.Tick"/>, not from its return: the time the tick itself took
/// (interpreting, drawing, presenting) is part of the wait, so a host sleeps only the remainder.
/// </summary>
public readonly record struct TickResult(bool Continue, TimeSpan Delay)
{
    /// <summary>Keep running; call <see cref="Engine.Tick"/> again roughly <paramref name="delay"/> after this tick began.</summary>
    public static TickResult After(TimeSpan delay) => new(true, delay);

    public static TickResult Immediately => new(true, TimeSpan.Zero);

    public static TickResult Quit => new(false, TimeSpan.Zero);
}

/// <summary>
/// Base class for an interpreter. Engines are tick driven: the host calls <see cref="Tick"/>
/// repeatedly and the engine advances as far as it can without blocking, then returns.
/// Engines never sleep, spin, or wait for input inside a tick.
/// </summary>
public abstract class Engine : IDisposable
{
    protected Engine(IHost host, IGameContainer game, DetectedGame detected)
    {
        Host = host;
        Game = game;
        Detected = detected;
    }

    public IHost Host { get; }

    public IGameContainer Game { get; }

    public DetectedGame Detected { get; }

    /// <summary>Loads resources and sets the display mode. Called once before the first tick.</summary>
    public abstract void Initialize();

    /// <param name="elapsed">Wall-clock time since the previous tick, or zero on the first tick.</param>
    public abstract TickResult Tick(TimeSpan elapsed);

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
