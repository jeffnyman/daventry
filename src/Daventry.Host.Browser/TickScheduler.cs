namespace Daventry.Host.Browser;

/// <summary>
/// Decides on each animation frame whether the engine is due for a tick. An engine asks for its
/// next tick a delay after the start of the last one; frames arrive at the display's rate, so a
/// frame a little early still counts (otherwise a 60 Hz game on a 60 Hz display would tick every
/// other frame) and a display faster than the game skips frames.
/// </summary>
public sealed class TickScheduler
{
    private TimeSpan? _nextTickAt;

    /// <summary>How early a frame may be and still tick.</summary>
    public TimeSpan Tolerance { get; set; } = TimeSpan.FromMilliseconds(4);

    /// <summary>Ticks run in one frame when the engine keeps asking for more right away.</summary>
    public int MaxTicksPerFrame { get; set; } = 4;

    /// <summary>The most a tick is told has passed: a tab in the background for a minute does not jump the game a minute ahead.</summary>
    public TimeSpan MaxElapsed { get; set; } = TimeSpan.FromMilliseconds(250);

    public void Reset() => _nextTickAt = null;

    public bool IsDue(TimeSpan now) => _nextTickAt is not { } next || now + Tolerance >= next;

    /// <summary>
    /// Records the engine's wish after a tick that ran at <paramref name="now"/>. The next tick
    /// is counted from when this one was due, not from the frame that ran it, so a display whose
    /// frames do not divide the tick (144 Hz against 60) still ticks at the game's rate; a tick
    /// that ran late by more than the delay (a tab in the background) starts over from now.
    /// </summary>
    public void After(TimeSpan now, TimeSpan delay)
    {
        var basis = _nextTickAt is { } due && now - due < delay ? due : now;
        _nextTickAt = basis + delay;
    }

    public TimeSpan ClampElapsed(TimeSpan elapsed) => elapsed > MaxElapsed ? MaxElapsed : elapsed;
}
