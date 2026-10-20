using Daventry.Core.Graphics;
using Daventry.Core.Input;
using Daventry.Core.Storage;

namespace Daventry.Core.Hosting;

/// <summary>Everything an engine needs from the platform, in one place.</summary>
public interface IHost
{
    IGraphicsDriver Graphics { get; }

    IInputDriver Input { get; }

    IAudioDriver Audio { get; }

    ISaveStore Saves { get; }

    ILog Log { get; }
}
