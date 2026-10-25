using Daventry.Core.Graphics;
using SDL;
using static SDL.SDL3;

namespace Daventry.Host.Sdl;

/// <summary>
/// Presents the engine's 8-bit framebuffer through an SDL indexed surface scaled onto the window
/// surface, and true-colour frames (Duck video) through an XRGB8888 surface the same way. No GPU
/// renderer is involved, which keeps the path simple and exact. The window can be
/// resized and switched to full screen; the frame keeps its aspect ratio and is centred with black
/// borders.
/// </summary>
internal sealed unsafe class SdlGraphicsDriver : ITrueColorGraphicsDriver, IDisposable
{
    private readonly int _scale;
    private SDL_Window* _window;
    private SDL_Surface* _frame;
    private SDL_Palette* _palette;
    private SDL_Surface* _trueColorFrame; // made on the first true-colour present
    private bool _showsTrueColor;
    private int _width;
    private int _height;
    private SDL_Rect _target; // where the frame lands on the window surface

    private readonly string _title;

    public SdlGraphicsDriver(string title, int scale, bool fullscreen, bool aspectRatioCorrection)
    {
        _title = title;
        _scale = Math.Max(1, scale);
        AspectRatioCorrection = aspectRatioCorrection;
        var flags = SDL_WindowFlags.SDL_WINDOW_RESIZABLE;
        if (fullscreen)
        {
            flags |= SDL_WindowFlags.SDL_WINDOW_FULLSCREEN;
        }

        _window = SdlException.ThrowIfNull(SDL_CreateWindow(title, 320 * _scale, 200 * _scale, flags), "SDL_CreateWindow");
    }

    public int Width => _width;

    public int Height => _height;

    public SDL_Window* Window => _window;

    public bool IsFullscreen => (SDL_GetWindowFlags(_window) & SDL_WindowFlags.SDL_WINDOW_FULLSCREEN) != 0;

    /// <summary>
    /// Shows a 320x200 or 640x400 frame at 4:3, the shape it had on a CRT, where each pixel was a
    /// fifth taller than wide. Other frame sizes are shown with square pixels.
    /// </summary>
    public bool AspectRatioCorrection { get; set; }

    public void SetMode(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        bool changed = width != _width || height != _height;
        DestroyFrame();
        _width = width;
        _height = height;
        _frame = SdlException.ThrowIfNull(SDL_CreateSurface(width, height, SDL_PixelFormat.SDL_PIXELFORMAT_INDEX8), "SDL_CreateSurface");
        _palette = SdlException.ThrowIfNull(SDL_CreateSurfacePalette(_frame), "SDL_CreateSurfacePalette");
        SdlException.ThrowIfFalse(SDL_SetWindowMinimumSize(_window, width, height), "SDL_SetWindowMinimumSize");

        // a new mode gets the window at its natural size, shrunk to fit the display and centred
        // again (resizing keeps the top left corner, which left the 640x480 games off centre);
        // a size the user chose is kept otherwise
        if (changed && !IsFullscreen)
        {
            int scale = _scale;
            var bounds = new SDL_Rect();
            if (SDL_GetDisplayUsableBounds(SDL_GetDisplayForWindow(_window), &bounds))
            {
                while (scale > 1 && (width * scale > bounds.w || height * scale > bounds.h))
                {
                    scale--;
                }
            }

            SdlException.ThrowIfFalse(SDL_SetWindowSize(_window, width * scale, height * scale), "SDL_SetWindowSize");
            SdlException.ThrowIfFalse(SDL_SetWindowPosition(_window, (int)SDL_WINDOWPOS_CENTERED, (int)SDL_WINDOWPOS_CENTERED), "SDL_SetWindowPosition");
        }
    }

    public void SetPalette(ReadOnlySpan<Rgb> colors, int start = 0)
    {
        if (_palette is null)
        {
            throw new InvalidOperationException("SetMode must be called before SetPalette.");
        }

        Span<SDL_Color> sdlColors = stackalloc SDL_Color[colors.Length];
        for (int i = 0; i < colors.Length; i++)
        {
            sdlColors[i] = new SDL_Color { r = colors[i].R, g = colors[i].G, b = colors[i].B, a = 255 };
        }

        fixed (SDL_Color* p = sdlColors)
        {
            SdlException.ThrowIfFalse(SDL_SetPaletteColors(_palette, p, start, colors.Length), "SDL_SetPaletteColors");
        }
    }

    public void Present(Surface8 frame)
    {
        if (_frame is null)
        {
            throw new InvalidOperationException("SetMode must be called before Present.");
        }

        if (frame.Width != _width || frame.Height != _height)
        {
            throw new ArgumentException($"Frame is {frame.Width}x{frame.Height} but mode is {_width}x{_height}.", nameof(frame));
        }

        SdlException.ThrowIfFalse(SDL_LockSurface(_frame), "SDL_LockSurface");
        try
        {
            byte* dest = (byte*)_frame->pixels;
            int pitch = _frame->pitch;
            for (int y = 0; y < _height; y++)
            {
                frame.Row(y).CopyTo(new Span<byte>(dest + y * pitch, _width));
            }
        }
        finally
        {
            SDL_UnlockSurface(_frame);
        }

        _showsTrueColor = false;
        Redraw();
    }

    public void Present(Surface32 frame)
    {
        if (_frame is null)
        {
            throw new InvalidOperationException("SetMode must be called before Present.");
        }

        if (frame.Width != _width || frame.Height != _height)
        {
            throw new ArgumentException($"Frame is {frame.Width}x{frame.Height} but mode is {_width}x{_height}.", nameof(frame));
        }

        if (_trueColorFrame is null)
        {
            _trueColorFrame = SdlException.ThrowIfNull(SDL_CreateSurface(_width, _height, SDL_PixelFormat.SDL_PIXELFORMAT_XRGB8888), "SDL_CreateSurface");
        }

        SdlException.ThrowIfFalse(SDL_LockSurface(_trueColorFrame), "SDL_LockSurface");
        try
        {
            byte* dest = (byte*)_trueColorFrame->pixels;
            int pitch = _trueColorFrame->pitch;
            for (int y = 0; y < _height; y++)
            {
                frame.Row(y).CopyTo(new Span<uint>(dest + y * pitch, _width));
            }
        }
        finally
        {
            SDL_UnlockSurface(_trueColorFrame);
        }

        _showsTrueColor = true;
        Redraw();
    }

    /// <summary>
    /// Paints the last frame onto the window again, scaled to fit. Needed after the window was
    /// resized or exposed, since the engines only present when something changed.
    /// </summary>
    public void Redraw()
    {
        if (_frame is null)
        {
            return;
        }

        var windowSurface = SdlException.ThrowIfNull(SDL_GetWindowSurface(_window), "SDL_GetWindowSurface");
        _target = FitFrame(windowSurface->w, windowSurface->h);

        // the borders, when the window is not the frame's shape
        if (_target.x > 0 || _target.y > 0)
        {
            SdlException.ThrowIfFalse(SDL_FillSurfaceRect(windowSurface, null, SDL_MapSurfaceRGB(windowSurface, 0, 0, 0)), "SDL_FillSurfaceRect");
        }

        var source = _showsTrueColor && _trueColorFrame is not null ? _trueColorFrame : _frame;
        fixed (SDL_Rect* target = &_target)
        {
            SdlException.ThrowIfFalse(
                SDL_BlitSurfaceScaled(source, null, windowSurface, target, SDL_ScaleMode.SDL_SCALEMODE_NEAREST),
                "SDL_BlitSurfaceScaled");
        }

        SdlException.ThrowIfFalse(SDL_UpdateWindowSurface(_window), "SDL_UpdateWindowSurface");
    }

    /// <summary>Switches between a window and the full screen, keeping the frame's aspect ratio either way.</summary>
    public void ToggleFullscreen()
    {
        SdlException.ThrowIfFalse(SDL_SetWindowFullscreen(_window, !IsFullscreen), "SDL_SetWindowFullscreen");
    }

    /// <summary>Switches the aspect ratio correction on or off and repaints.</summary>
    public void ToggleAspectRatioCorrection()
    {
        AspectRatioCorrection = !AspectRatioCorrection;
        Redraw();
    }

    public void SetTitle(string title)
    {
        SDL_SetWindowTitle(_window, title);
    }

    /// <summary>Shows a short status, such as the volume, after the title in the window's title bar.</summary>
    public void ShowStatus(string status) => SetTitle($"{_title}  ({status})");

    /// <summary>Converts window coordinates, as the mouse reports them, to framebuffer coordinates.</summary>
    public (int X, int Y) ToFramebuffer(float windowX, float windowY)
    {
        if (_width == 0 || _height == 0 || _target.w == 0 || _target.h == 0)
        {
            return (0, 0);
        }

        // mouse positions are in window coordinates, the surface is in pixels; the two differ on
        // a high-DPI display
        int winW;
        int winH;
        if (SDL_GetWindowSize(_window, &winW, &winH) && winW > 0 && winH > 0)
        {
            var surface = SDL_GetWindowSurface(_window);
            if (surface is not null)
            {
                windowX = windowX * surface->w / winW;
                windowY = windowY * surface->h / winH;
            }
        }

        int x = (int)MathF.Floor((windowX - _target.x) * _width / _target.w);
        int y = (int)MathF.Floor((windowY - _target.y) * _height / _target.h);
        return (Math.Clamp(x, 0, _width - 1), Math.Clamp(y, 0, _height - 1));
    }

    public void Dispose()
    {
        DestroyFrame();
        if (_window is not null)
        {
            SDL_DestroyWindow(_window);
            _window = null;
        }
    }

    /// <summary>The largest rectangle with the frame's aspect ratio that fits the window, centred.</summary>
    private SDL_Rect FitFrame(int windowWidth, int windowHeight)
    {
        // the frame's shape on screen; a 16:10 frame becomes 4:3 when corrected
        int shapeWidth = _width;
        int shapeHeight = AspectRatioCorrection && _height * 8 == _width * 5 ? _height * 6 / 5 : _height;
        float scale = Math.Min((float)windowWidth / shapeWidth, (float)windowHeight / shapeHeight);
        int w = Math.Max(1, (int)(shapeWidth * scale));
        int h = Math.Max(1, (int)(shapeHeight * scale));
        return new SDL_Rect { x = (windowWidth - w) / 2, y = (windowHeight - h) / 2, w = w, h = h };
    }

    private void DestroyFrame()
    {
        if (_frame is not null)
        {
            // The palette is owned by the surface and freed with it.
            SDL_DestroySurface(_frame);
            _frame = null;
            _palette = null;
        }

        if (_trueColorFrame is not null)
        {
            SDL_DestroySurface(_trueColorFrame);
            _trueColorFrame = null;
        }

        _showsTrueColor = false;
    }
}
