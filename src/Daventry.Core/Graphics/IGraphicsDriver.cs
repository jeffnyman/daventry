namespace Daventry.Core.Graphics;

/// <summary>
/// The host's display. The engine owns an 8-bit framebuffer and a palette and hands both to the
/// host; the host decides how to scale and present them.
/// </summary>
public interface IGraphicsDriver
{
    /// <summary>Sets the logical framebuffer size the engine will present.</summary>
    void SetMode(int width, int height);

    void SetPalette(ReadOnlySpan<Rgb> colors, int start = 0);

    /// <summary>Presents a complete frame. The host must not keep a reference to the surface.</summary>
    void Present(Surface8 frame);

    void SetTitle(string title);
}

/// <summary>
/// A display that can also show true-colour frames, for video that does not fit a palette. A
/// true-colour frame has the mode's size and replaces the indexed picture until the next
/// <see cref="IGraphicsDriver.Present(Surface8)"/>. Engines check for this interface and go
/// without true-colour video when the host lacks it.
/// </summary>
public interface ITrueColorGraphicsDriver : IGraphicsDriver
{
    /// <summary>Presents a complete true-colour frame. The host must not keep a reference to the surface.</summary>
    void Present(Surface32 frame);
}
