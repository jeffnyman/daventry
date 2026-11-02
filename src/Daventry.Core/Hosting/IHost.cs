using Daventry.Core.Graphics;
using Daventry.Core.Input;
using Daventry.Core.Storage;

namespace Daventry.Core.Hosting;

/// <summary>Everything an engine needs from the platform, in one place.</summary>
public interface IHost
{
    /// <summary>Where frames are presented.</summary>
    IGraphicsDriver Graphics { get; }

    /// <summary>Where the engine reads keys and the mouse.</summary>
    IInputDriver Input { get; }

    /// <summary>Where the engine's sound goes.</summary>
    IAudioDriver Audio { get; }

    /// <summary>Where saved games, and the games' own files, are kept.</summary>
    ISaveStore Saves { get; }

    /// <summary>Where the engine writes its messages.</summary>
    ILog Log { get; }

    /// <summary>
    /// True when the host has no real clock worth following (tests, scripted runs): the engine
    /// then advances the clock the games read by a fixed step per read inside a tick instead of
    /// the real time the tick has taken, so the same inputs give the same frames every run.
    /// </summary>
    bool DeterministicTime => false;

    /// <summary>Seed for the games' random numbers, or null for a different sequence every run.</summary>
    int? RandomSeed => null;
}
