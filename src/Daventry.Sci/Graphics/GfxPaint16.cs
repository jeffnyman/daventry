using Daventry.Sci.Resources;

namespace Daventry.Sci.Graphics;

/// <summary>
/// Drawing in port coordinates: pictures, cels and rectangles. Port of the parts of ScummVM's
/// paint16.cpp that do not need the VM; the hires KQ6 Windows cels, transitions and the
/// kernel entry points that take VM handles follow with the interpreter.
/// </summary>
public sealed class GfxPaint16
{
    private readonly ResourceManager _resMan;
    private readonly GfxPorts _ports;
    private readonly GfxScreen _screen;
    private readonly GfxPalette16 _palette;
    private readonly GfxCache _cache;
    private readonly SciVersion _version;
    private readonly SciGameId _gameId;

    public GfxPaint16(ResourceManager resMan, GfxPorts ports, GfxScreen screen, GfxPalette16 palette, GfxCache cache, SciVersion version, SciGameId gameId)
    {
        _resMan = resMan;
        _ports = ports;
        _screen = screen;
        _palette = palette;
        _cache = cache;
        _version = version;
        _gameId = gameId;
    }

    public void DrawPicture(int pictureId, bool mirroredFlag, bool addToFlag, int paletteId)
    {
        // not adding to a picture: clear the screen with white
        if (!addToFlag)
        {
            ClearScreen(_screen.ColorWhite);
        }

        var picture = new GfxPicture(_resMan, _ports, _screen, _palette, pictureId, _version, _gameId);
        picture.Draw(mirroredFlag, addToFlag, paletteId);

        // SCI1.1 bumps the palette timestamp and loads the pal-vary target
        if (_version == SciVersion.V1_1)
        {
            _palette.DrewPicture(pictureId, false);
        }
    }

    /// <summary>The only cel drawing call that updates the screen.</summary>
    public void DrawCelAndShow(int viewId, int loopNo, int celNo, int leftPos, int topPos, byte priority, int paletteNo, int scaleX = 128, int scaleY = 128)
    {
        var view = _cache.GetView(viewId);
        if (view is null)
        {
            return;
        }

        var celRect = new SciRect(leftPos, topPos, leftPos + view.GetWidth(loopNo, celNo), topPos + view.GetHeight(loopNo, celNo));
        DrawCel(view, loopNo, celNo, celRect, priority, paletteNo, scaleX, scaleY);

        if (_version >= SciVersion.V1_1)
        {
            if (_screen.PicNotValidSci11 == 0)
            {
                BitsShow(celRect);
            }
        }
        else if (_screen.PicNotValid == 0)
        {
            BitsShow(celRect);
        }
    }

    public void DrawCel(int viewId, int loopNo, int celNo, SciRect celRect, byte priority, int paletteNo, int scaleX = 128, int scaleY = 128)
    {
        var view = _cache.GetView(viewId);
        if (view is not null)
        {
            DrawCel(view, loopNo, celNo, celRect, priority, paletteNo, scaleX, scaleY);
        }
    }

    public void DrawCel(GfxView view, int loopNo, int celNo, SciRect celRect, byte priority, int paletteNo, int scaleX = 128, int scaleY = 128)
    {
        var clipRect = celRect;
        clipRect.Clip(_ports.CurPort.Rect);
        if (clipRect.IsEmpty)
        {
            return;
        }

        var clipRectTranslated = clipRect;
        _ports.OffsetRect(ref clipRectTranslated);
        if (scaleX == 128 && scaleY == 128)
        {
            view.Draw(celRect, clipRect, clipRectTranslated, loopNo, celNo, priority, paletteNo);
        }
        else
        {
            view.DrawScaled(celRect, clipRect, clipRectTranslated, loopNo, celNo, priority, scaleX, scaleY);
        }
    }

    public void ClearScreen(byte color) => FillRect(_ports.CurPort.Rect, GfxScreenMask.All, color, 0, 0);

    public void InvertRect(SciRect rect)
    {
        int oldPenMode = _ports.CurPort.PenMode;
        _ports.CurPort.PenMode = 2;
        FillRect(rect, GfxScreenMask.Visual, (byte)_ports.CurPort.PenClr, (byte)_ports.CurPort.BackClr, 0);
        _ports.CurPort.PenMode = oldPenMode;
    }

    /// <summary>Used by SCI0 early only.</summary>
    public void InvertRectViaXor(SciRect rect)
    {
        var r = rect;
        r.Clip(_ports.CurPort.Rect);
        if (r.IsEmpty)
        {
            return;
        }

        _ports.OffsetRect(ref r);
        for (int y = r.Top; y < r.Bottom; y++)
        {
            for (int x = r.Left; x < r.Right; x++)
            {
                byte curVisual = _screen.GetVisual(x, y);
                _screen.PutPixel(x, y, GfxScreenMask.Visual, (byte)(curVisual ^ 0x0F), 0, 0);
            }
        }
    }

    public void EraseRect(SciRect rect) => FillRect(rect, GfxScreenMask.Visual, (byte)_ports.CurPort.BackClr, 0, 0);

    public void PaintRect(SciRect rect) => FillRect(rect, GfxScreenMask.Visual, (byte)_ports.CurPort.PenClr, 0, 0);

    public void FillRect(SciRect rect, GfxScreenMask drawFlags, byte color, byte priority, byte control)
    {
        var r = rect;
        r.Clip(_ports.CurPort.Rect);
        if (r.IsEmpty)
        {
            return;
        }

        int oldPenMode = _ports.CurPort.PenMode;
        _ports.OffsetRect(ref r);

        if ((drawFlags & GfxScreenMask.Visual) != 0)
        {
            if (oldPenMode == 2)
            {
                // invert mode
                for (int y = r.Top; y < r.Bottom; y++)
                {
                    for (int x = r.Left; x < r.Right; x++)
                    {
                        byte curVisual = _screen.GetVisual(x, y);
                        if (curVisual == color)
                        {
                            _screen.PutPixel(x, y, GfxScreenMask.Visual, priority, 0, 0);
                        }
                        else if (curVisual == priority)
                        {
                            _screen.PutPixel(x, y, GfxScreenMask.Visual, color, 0, 0);
                        }
                    }
                }
            }
            else
            {
                for (int y = r.Top; y < r.Bottom; y++)
                {
                    for (int x = r.Left; x < r.Right; x++)
                    {
                        _screen.PutPixel(x, y, GfxScreenMask.Visual, color, 0, 0);
                    }
                }
            }
        }

        if ((byte)drawFlags < 2)
        {
            return;
        }

        drawFlags &= GfxScreenMask.Priority | GfxScreenMask.Control;

        // Sierra saved priority and control in one byte; the bits are isolated here
        priority &= 0x0F;
        control &= 0x0F;

        if (oldPenMode != 2)
        {
            for (int y = r.Top; y < r.Bottom; y++)
            {
                for (int x = r.Left; x < r.Right; x++)
                {
                    _screen.PutPixel(x, y, drawFlags, 0, priority, control);
                }
            }
        }
        else
        {
            for (int y = r.Top; y < r.Bottom; y++)
            {
                for (int x = r.Left; x < r.Right; x++)
                {
                    _screen.PutPixel(x, y, drawFlags, 0, (byte)(_screen.GetPriority(x, y) == 0 ? 1 : 0), (byte)(_screen.GetControl(x, y) == 0 ? 1 : 0));
                }
            }
        }
    }

    public void FrameRect(SciRect rect)
    {
        var r = rect;
        // left
        r.Right = rect.Left + 1;
        PaintRect(r);
        // right
        r.Right = rect.Right;
        r.Left = rect.Right - 1;
        PaintRect(r);
        // top
        r.Left = rect.Left;
        r.Bottom = rect.Top + 1;
        PaintRect(r);
        // bottom
        r.Bottom = rect.Bottom;
        r.Top = rect.Bottom - 1;
        PaintRect(r);
    }

    public void BitsShow(SciRect rect)
    {
        var workerRect = rect;
        workerRect.Clip(_ports.CurPort.Rect);
        if (workerRect.IsEmpty)
        {
            return;
        }

        _ports.OffsetRect(ref workerRect);

        // adjust left and right to even coordinates
        workerRect.Left &= unchecked((int)0xFFFFFFFE);
        workerRect.Right = (workerRect.Right + 1) & unchecked((int)0xFFFFFFFE);

        _screen.CopyRectToScreen(workerRect);
    }

    /// <summary>Saves the screen under a rectangle of the current port. Null when nothing is inside the port.</summary>
    public byte[]? BitsSave(SciRect rect, GfxScreenMask screenMask)
    {
        var workerRect = rect;
        workerRect.Clip(_ports.CurPort.Rect);
        if (workerRect.IsEmpty)
        {
            return null;
        }

        _ports.OffsetRect(ref workerRect);
        return _screen.BitsSave(workerRect, screenMask);
    }

    public void BitsRestore(byte[]? memory)
    {
        if (memory is not null)
        {
            _screen.BitsRestore(memory);
        }
    }

    /// <summary>
    /// Draws a picture into the picture window. The transition that shows it is not ported yet,
    /// so the caller presents the screen itself.
    /// </summary>
    public void KernelDrawPicture(int pictureId, bool mirroredFlag, bool addToFlag, int egaPaletteNo)
    {
        var oldPort = _ports.SetPort(_ports.PicWind);
        if (_ports.IsFrontWindow(_ports.PicWind))
        {
            _screen.PicNotValid = 1;
            DrawPicture(pictureId, mirroredFlag, addToFlag, egaPaletteNo);
        }
        else
        {
            // SCI1 early and later set it here too (LSL5 last wakeup, SQ5 after the scrubbing);
            // the 1990 Christmas card EGA did not, VGA did
            if (_version >= SciVersion.V1Early)
            {
                _screen.PicNotValid = 1;
            }

            DrawPicture(pictureId, mirroredFlag, addToFlag, egaPaletteNo);
        }

        _ports.SetPort(oldPort);
    }

    public void KernelGraphFillBoxForeground(SciRect rect) => PaintRect(rect);

    public void KernelGraphFillBoxBackground(SciRect rect) => EraseRect(rect);

    public void KernelGraphFillBox(SciRect rect, GfxScreenMask colorMask, byte color, byte priority, byte control)
        => FillRect(rect, colorMask, color, priority, control);

    public void KernelGraphFrameBox(SciRect rect, byte color)
    {
        int oldColor = _ports.GetPort().PenClr;
        _ports.PenColor(color);
        FrameRect(rect);
        _ports.PenColor(oldColor);
    }

    public void KernelGraphDrawLine(SciPoint startPoint, SciPoint endPoint, byte color, byte priority, byte control)
    {
        _ports.ClipLine(ref startPoint, ref endPoint);
        _ports.OffsetLine(ref startPoint, ref endPoint);
        _screen.DrawLine(startPoint, endPoint, color, priority, control);
    }

    public byte[]? KernelGraphSaveBox(SciRect rect, GfxScreenMask screenMask) => BitsSave(rect, screenMask);

    public void KernelGraphRestoreBox(byte[]? memory) => BitsRestore(memory);

    public void KernelGraphUpdateBox(SciRect rect) => BitsShow(rect);

    public void KernelGraphRedrawBox(SciRect rect)
    {
        // redrawing a box needs the animation list, which comes with the interpreter; for now show it
        BitsShow(rect);
    }
}
