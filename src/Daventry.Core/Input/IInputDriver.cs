namespace Daventry.Core.Input;

/// <summary>
/// Queue of input events the host has collected since the engine last drained it. Engines expect
/// input at the pace a person makes it: a click is the mouse arriving, then the button going down
/// and up in later ticks, with <see cref="GetMouseState"/> agreeing with the events read so far.
/// SCI32 buttons take a press only on something the mouse has hovered over first, so a move, press
/// and release in one tick is not a click to them. (HeadlessHost.Click makes a click this way.)
/// </summary>
public interface IInputDriver
{
    /// <summary>Takes the next event from the queue; false when it is empty.</summary>
    bool TryRead(out InputEvent inputEvent);

    /// <summary>Current mouse position in framebuffer pixels and held buttons.</summary>
    MouseState GetMouseState();
}

/// <summary>Where the mouse is, in framebuffer pixels, and which buttons are held.</summary>
/// <param name="X">The horizontal position.</param>
/// <param name="Y">The vertical position.</param>
/// <param name="LeftDown">Whether the left button is held.</param>
/// <param name="RightDown">Whether the right button is held.</param>
/// <param name="MiddleDown">Whether the middle button is held.</param>
public readonly record struct MouseState(int X, int Y, bool LeftDown, bool RightDown, bool MiddleDown);
