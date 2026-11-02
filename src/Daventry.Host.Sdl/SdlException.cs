using static SDL.SDL3;

namespace Daventry.Host.Sdl;

/// <summary>An SDL call failed; the message has SDL's own error text.</summary>
public sealed class SdlException(string message) : Exception(message)
{
    /// <summary>Throws with SDL's last error text when <paramref name="ok"/> is false.</summary>
    internal static void ThrowIfFalse(bool ok, string operation)
    {
        if (!ok)
        {
            throw new SdlException($"{operation} failed: {SDL_GetError()}");
        }
    }

    internal static unsafe T* ThrowIfNull<T>(T* pointer, string operation)
        where T : unmanaged
    {
        if (pointer is null)
        {
            throw new SdlException($"{operation} failed: {SDL_GetError()}");
        }

        return pointer;
    }
}
