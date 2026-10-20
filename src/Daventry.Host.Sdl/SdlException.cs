using static SDL.SDL3;

namespace Daventry.Host.Sdl;

public sealed class SdlException(string message) : Exception(message)
{
    /// <summary>Throws with SDL's last error text when <paramref name="ok"/> is false.</summary>
    public static void ThrowIfFalse(bool ok, string operation)
    {
        if (!ok)
        {
            throw new SdlException($"{operation} failed: {SDL_GetError()}");
        }
    }

    public static unsafe T* ThrowIfNull<T>(T* pointer, string operation)
        where T : unmanaged
    {
        if (pointer is null)
        {
            throw new SdlException($"{operation} failed: {SDL_GetError()}");
        }

        return pointer;
    }
}
