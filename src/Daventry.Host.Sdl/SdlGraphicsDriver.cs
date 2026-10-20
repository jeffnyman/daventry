using Daventry.Core.Graphics;
using SDL;
using static SDL.SDL3;

namespace Daventry.Host.Sdl;

/// <summary>
/// Presents the engine's 8-bit framebuffer through an SDL indexed surface scaled onto the window
/// surface. No GPU renderer is involved, which keeps the path simple and exact.
/// </summary>
internal sealed unsafe class SdlGraphicsDriver : IGraphicsDriver, IDisposable
{
    private readonly int _scale;
    private SDL_Window* _window;
    private SDL_Surface* _frame;
    private SDL_Palette* _palette;
    private int _width;
    private int _height;

    public SdlGraphicsDriver(string title, int scale)
    {
        _scale = Math.Max(1, scale);
        _window = SdlException.ThrowIfNull(SDL_CreateWindow(title, 320 * _scale, 200 * _scale, 0), "SDL_CreateWindow");
    }

    public int Width => _width;

    public int Height => _height;

    public SDL_Window* Window => _window;

    public void SetMode(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        DestroyFrame();
        _width = width;
        _height = height;
        _frame = SdlException.ThrowIfNull(SDL_CreateSurface(width, height, SDL_PixelFormat.SDL_PIXELFORMAT_INDEX8), "SDL_CreateSurface");
        _palette = SdlException.ThrowIfNull(SDL_CreateSurfacePalette(_frame), "SDL_CreateSurfacePalette");
        SdlException.ThrowIfFalse(SDL_SetWindowSize(_window, width * _scale, height * _scale), "SDL_SetWindowSize");
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

        var windowSurface = SdlException.ThrowIfNull(SDL_GetWindowSurface(_window), "SDL_GetWindowSurface");
        SdlException.ThrowIfFalse(
            SDL_BlitSurfaceScaled(_frame, null, windowSurface, null, SDL_ScaleMode.SDL_SCALEMODE_NEAREST),
            "SDL_BlitSurfaceScaled");
        SdlException.ThrowIfFalse(SDL_UpdateWindowSurface(_window), "SDL_UpdateWindowSurface");
    }

    public void SetTitle(string title)
    {
        SDL_SetWindowTitle(_window, title);
    }

    /// <summary>Converts window pixel coordinates to framebuffer coordinates.</summary>
    public (int X, int Y) ToFramebuffer(float windowX, float windowY)
    {
        if (_width == 0 || _height == 0)
        {
            return (0, 0);
        }

        var surface = SDL_GetWindowSurface(_window);
        int winW = surface is null ? _width * _scale : surface->w;
        int winH = surface is null ? _height * _scale : surface->h;
        int x = Math.Clamp((int)(windowX * _width / Math.Max(1, winW)), 0, _width - 1);
        int y = Math.Clamp((int)(windowY * _height / Math.Max(1, winH)), 0, _height - 1);
        return (x, y);
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

    private void DestroyFrame()
    {
        if (_frame is not null)
        {
            // The palette is owned by the surface and freed with it.
            SDL_DestroySurface(_frame);
            _frame = null;
            _palette = null;
        }
    }
}
