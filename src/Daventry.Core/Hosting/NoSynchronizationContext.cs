namespace Daventry.Core.Hosting;

/// <summary>
/// Clears <see cref="SynchronizationContext.Current"/> for the duration of a scope. The engines
/// resume their async interpreter loops synchronously from <see cref="Engine.Tick"/>, which only
/// holds if no ambient context (xunit, WinForms, Blazor) captures the continuations and posts them
/// elsewhere. Hosts and engines wrap every call into interpreter code with this.
/// </summary>
public readonly struct NoSynchronizationContext : IDisposable
{
    private readonly SynchronizationContext? _previous;

    private NoSynchronizationContext(SynchronizationContext? previous)
    {
        _previous = previous;
    }

    public static NoSynchronizationContext Enter()
    {
        var previous = SynchronizationContext.Current;
        if (previous is not null)
        {
            SynchronizationContext.SetSynchronizationContext(null);
        }

        return new NoSynchronizationContext(previous);
    }

    public void Dispose()
    {
        if (_previous is not null)
        {
            SynchronizationContext.SetSynchronizationContext(_previous);
        }
    }
}
