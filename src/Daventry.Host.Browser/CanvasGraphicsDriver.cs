using System.Runtime.Versioning;
using Daventry.Core.Graphics;

namespace Daventry.Host.Browser;

/// <summary>
/// Presents the engine's indexed frames, and true-colour ones (Duck video), on a canvas. The canvas keeps the framebuffer's pixel
/// size and the page scales it with CSS, so the browser does the stretching and the engine's
/// pixels stay crisp; with aspect ratio correction on, every mode is shown at 4:3 as a CRT did.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class CanvasGraphicsDriver : ITrueColorGraphicsDriver
{
    private readonly FrameConverter _converter = new();
    private byte[] _rgba = [];
    private string _gameTitle = string.Empty;

    /// <summary>The page's own title, shown before the game's ("Daventry - King's Quest"); null or empty shows the game's alone.</summary>
    public string? PageTitle { get; set; } = "Daventry";

    public int Width { get; private set; }

    public int Height { get; private set; }

    public bool AspectRatioCorrection { get; set; } = true;

    public void SetMode(int width, int height)
    {
        Width = width;
        Height = height;
        _rgba = new byte[width * height * 4];
        Interop.SetMode(width, height, AspectRatioCorrection);
    }

    public void SetPalette(ReadOnlySpan<Rgb> colors, int start = 0) => _converter.SetPalette(colors, start);

    public void Present(Surface8 frame)
    {
        if (frame.Width != Width || frame.Height != Height)
        {
            SetMode(frame.Width, frame.Height);
        }

        _converter.Convert(frame.Pixels, _rgba);
        Interop.Blit(frame.Width, frame.Height, _rgba);
    }

    public void Present(Surface32 frame)
    {
        if (frame.Width != Width || frame.Height != Height)
        {
            SetMode(frame.Width, frame.Height);
        }

        FrameConverter.Convert(frame.Pixels, _rgba);
        Interop.Blit(frame.Width, frame.Height, _rgba);
    }

    public void SetTitle(string title)
    {
        _gameTitle = title;
        Interop.SetTitle(DocumentTitle);
    }

    public void ShowStatus(string status) => Interop.SetTitle($"{DocumentTitle}  ({status})");

    private string DocumentTitle => (string.IsNullOrEmpty(PageTitle), _gameTitle.Length == 0) switch
    {
        (true, _) => _gameTitle,
        (false, true) => PageTitle!,
        (false, false) => $"{PageTitle} - {_gameTitle}",
    };

    public void ToggleFullscreen() => Interop.ToggleFullscreen();
}
