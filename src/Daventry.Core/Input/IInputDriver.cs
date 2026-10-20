namespace Daventry.Core.Input;

/// <summary>Queue of input events the host has collected since the engine last drained it.</summary>
public interface IInputDriver
{
    bool TryRead(out InputEvent inputEvent);

    /// <summary>Current mouse position in framebuffer pixels and held buttons.</summary>
    MouseState GetMouseState();
}

public readonly record struct MouseState(int X, int Y, bool LeftDown, bool RightDown, bool MiddleDown);
