using System.Runtime.CompilerServices;

namespace Daventry.Core.Hosting;

/// <summary>
/// Lets interpreter code written in a blocking style run inside a tick-driven engine. Code awaits
/// <see cref="Delay"/> or <see cref="Yield"/>; the continuation is stored and invoked synchronously
/// from <see cref="Advance"/>, which the engine calls once per tick with the current engine time.
/// Everything runs on the calling thread, so this works where threads are unavailable.
/// Only one continuation may be pending at a time, which is exactly the case for a single interpreter.
/// </summary>
public sealed class FrameScheduler
{
    private Action? _continuation;
    private TimeSpan _wakeAt;

    /// <summary>The engine clock as of the last <see cref="Advance"/>.</summary>
    public TimeSpan Now { get; private set; }

    public bool IsWaiting => _continuation is not null;

    /// <summary>When the pending continuation wants to run, or null if nothing is pending.</summary>
    public TimeSpan? WakeAt => _continuation is null ? null : _wakeAt;

    /// <summary>Suspends until at least <paramref name="duration"/> of engine time has passed.</summary>
    public FrameAwaitable Delay(TimeSpan duration) => new(this, Now + duration);

    public FrameAwaitable Delay(int milliseconds) => Delay(TimeSpan.FromMilliseconds(milliseconds));

    /// <summary>Suspends until the next tick.</summary>
    public FrameAwaitable Yield() => new(this, Now);

    /// <summary>Moves the clock and resumes the pending continuation if its time has come.</summary>
    public void Advance(TimeSpan now)
    {
        Now = now;
        if (_continuation is not null && now >= _wakeAt)
        {
            var continuation = _continuation;
            _continuation = null;
            continuation();
        }
    }

    internal void Register(TimeSpan wakeAt, Action continuation)
    {
        if (_continuation is not null)
        {
            throw new InvalidOperationException("Only one continuation may wait on a FrameScheduler at a time.");
        }

        _wakeAt = wakeAt;
        _continuation = continuation;
    }

    public readonly struct FrameAwaitable(FrameScheduler scheduler, TimeSpan wakeAt) : ICriticalNotifyCompletion
    {
        public FrameAwaitable GetAwaiter() => this;

        /// <summary>Always false: awaiting always suspends at least until the next <see cref="Advance"/>.</summary>
        public bool IsCompleted => false;

        public void OnCompleted(Action continuation) => scheduler.Register(wakeAt, continuation);

        public void UnsafeOnCompleted(Action continuation) => scheduler.Register(wakeAt, continuation);

        public void GetResult()
        {
        }
    }
}
