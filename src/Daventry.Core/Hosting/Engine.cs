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

    /// <summary>
    /// The room the game is in, by the game's own numbering (AGI's variable 0, SCI's room script,
    /// an Apple II adventure's room), or null while the game has not started or for an engine that
    /// has no rooms. For a host's status line, logs and scripted runs.
    /// </summary>
    public virtual int? CurrentRoom => null;

    // ---- saved games, as a host's own save menu uses them (ScummVM's Engine has the same) ----

    /// <summary>The reason given for a game that cannot be saved by the host at all (ScummVM's words).</summary>
    protected const string SavingNotSupported = "This game does not support saving";

    /// <summary>The reason given for a game that cannot be restored by the host at all (ScummVM's words).</summary>
    protected const string LoadingNotSupported = "This game does not support loading";

    /// <summary>
    /// Whether the host may save now. Games allow it only at some moments (when the player has
    /// control, say, or the prompt is showing), as in ScummVM; <paramref name="reason"/> says why not.
    /// </summary>
    public virtual bool CanSave(out string reason)
    {
        reason = SavingNotSupported;
        return false;
    }

    /// <summary>Saves the game to a slot of the host's save store, if <see cref="CanSave"/> allows it now.</summary>
    public virtual SaveResult Save(int slot, string description) => SaveResult.Failed(slot, SavingNotSupported);

    /// <summary>
    /// Asks the game to restore a slot at its next safe point: the next tick, or the next time it
    /// waits for the player. The outcome comes as <see cref="RestoreFinished"/> and
    /// <see cref="LastRestore"/>; a restore that fails (a damaged save, one from another release)
    /// leaves the game as it was. False when the game cannot be restored by the host or the slot
    /// holds no save, and then nothing is asked.
    /// </summary>
    public virtual bool RequestRestore(int slot) => false;

    /// <summary>The outcome of the last restore asked for with <see cref="RequestRestore"/>, or null before one finished.</summary>
    public RestoreResult? LastRestore { get; private set; }

    /// <summary>Raised, during a tick, when a restore asked for with <see cref="RequestRestore"/> has finished or failed.</summary>
    public event Action<RestoreResult>? RestoreFinished;

    /// <summary>The game's saved games in the host's save store, by slot.</summary>
    public virtual IReadOnlyList<SaveSlot> ListSaves() => [];

    /// <summary>The name a slot's save has in the host's save store, or null for a game the host cannot save.</summary>
    public virtual string? SaveFileName(int slot) => null;

    /// <summary>Records and announces the outcome of a restore the host asked for.</summary>
    protected void ReportRestore(RestoreResult result)
    {
        LastRestore = result;
        if (!result.Succeeded)
        {
            Host.Log.Warning("save", $"Restore of slot {result.Slot} failed: {result.Message}");
        }

        RestoreFinished?.Invoke(result);
    }

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
