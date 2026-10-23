using Daventry.Core.Hosting;
using Daventry.Sci.Engine;
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

    /// <summary>The segment manager that holds saved screen bits as hunks. Null in the viewer.</summary>
    public SegManager? SegMan { get; set; }

    /// <summary>Saves the screen under a rectangle of the current port into a hunk. Null when nothing is inside the port.</summary>
    public RegT BitsSaveHunk(SciRect rect, GfxScreenMask screenMask)
    {
        var data = BitsSave(rect, screenMask);
        if (data is null || SegMan is null)
        {
            return RegT.Null;
        }

        return SegMan.AllocateHunkEntry("SaveBits()", data);
    }

    public void BitsRestoreHunk(RegT memoryHandle)
    {
        if (memoryHandle.IsNull || SegMan is null)
        {
            return;
        }

        var memory = SegMan.GetHunkPointer(memoryHandle);
        if (memory is not null)
        {
            _screen.BitsRestore(memory);
            SegMan.FreeHunkEntry(memoryHandle);
        }
    }

    public void FreeHunk(RegT memoryHandle)
    {
        if (!memoryHandle.IsNull)
        {
            SegMan?.FreeHunkEntry(memoryHandle);
        }
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

    /// <summary>The transition kAnimate shows the next picture with; set by kDrawPic, -1 for none.</summary>
    public int TransitionNumber { get; set; } = -1;

    public bool TransitionBlackoutFlag { get; set; }

    /// <summary>Draws a picture into the picture window and remembers the transition for kAnimate.</summary>
    public void KernelDrawPicture(int pictureId, int animationNr, bool animationBlackoutFlag, bool mirroredFlag, bool addToFlag, int egaPaletteNo)
    {
        var oldPort = _ports.SetPort(_ports.PicWind);
        if (_ports.IsFrontWindow(_ports.PicWind))
        {
            _screen.PicNotValid = 1;
            DrawPicture(pictureId, mirroredFlag, addToFlag, egaPaletteNo);
            TransitionNumber = animationNr;
            TransitionBlackoutFlag = animationBlackoutFlag;
        }
        else
        {
            // SCI1 early and later set it here too (LSL5 last wakeup, SQ5 after the scrubbing);
            // the 1990 Christmas card EGA did not, VGA did
            if (_version >= SciVersion.V1Early)
            {
                _screen.PicNotValid = 1;
            }

            _ports.BeginUpdate(_ports.PicWind);
            DrawPicture(pictureId, mirroredFlag, addToFlag, egaPaletteNo);
            _ports.EndUpdate(_ports.PicWind);
        }

        _ports.SetPort(oldPort);
    }

    /// <summary>Draws a picture without a transition; used by the resource viewer.</summary>
    public void KernelDrawPicture(int pictureId, bool mirroredFlag, bool addToFlag, int egaPaletteNo)
        => KernelDrawPicture(pictureId, -1, false, mirroredFlag, addToFlag, egaPaletteNo);

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

    public RegT KernelGraphSaveBox(SciRect rect, GfxScreenMask screenMask) => BitsSaveHunk(rect, screenMask);

    public void KernelGraphRestoreBox(RegT memoryHandle) => BitsRestoreHunk(memoryHandle);

    public void KernelGraphUpdateBox(SciRect rect) => BitsShow(rect);

    public void KernelGraphRedrawBox(SciRect rect)
    {
        if (Animate is null)
        {
            BitsShow(rect);
            return;
        }

        // the rect is relative to the current port; reAnimate works in the picture window
        rect.Translate(_ports.GetPort().Left, _ports.GetPort().Top);
        var oldPort = _ports.SetPort(_ports.PicWind);
        rect.Translate(-_ports.GetPort().Left, -_ports.GetPort().Top);
        Animate.ReAnimate(rect);
        _ports.SetPort(oldPort);
    }

    /// <summary>The animation code, needed to redraw the cast over restored areas. Null in the viewer.</summary>
    public GfxAnimate? Animate { get; set; }

    /// <summary>The text code, needed by kDisplay. Null in the viewer.</summary>
    public GfxText16? Text { get; set; }

    /// <summary>The rectangle a saved-bits hunk covers, in screen coordinates.</summary>
    public SciRect BitsGetRect(RegT memoryHandle)
    {
        if (!memoryHandle.IsNull && SegMan?.GetHunkPointer(memoryHandle) is { } memory)
        {
            return GfxScreen.BitsGetRect(memory);
        }

        return default;
    }

    // kDisplay argument codes
    private const int DisplayMovePen = 100;
    private const int DisplaySetAlignment = 101;
    private const int DisplaySetPenColor = 102;
    private const int DisplaySetBackgroundColor = 103;
    private const int DisplaySetGreyedOutput = 104;
    private const int DisplaySetFont = 105;
    private const int DisplayWidth = 106;
    private const int DisplaySaveUnder = 107;
    private const int DisplayRestoreUnder = 108;
    private const int DisplayDontShowBits = 121;
    private const int DisplaySetStroke = 122;

    /// <summary>Draws a text with the pen, font, colour and width codes of kDisplay. Port of kernelDisplay.</summary>
    public RegT KernelDisplay(string text, ushort languageSplitter, KernelArgs argv)
    {
        var text16 = Text ?? throw new InvalidOperationException("kDisplay needs the text code");
        var alignment = TextAlignment.Left;
        int colorPen = -1;
        int colorBack = -1;
        int width = -1;
        bool redraw = true;
        bool doSaveUnder = false;
        var rect = default(SciRect);
        var result = RegT.Null;
        int stroke = 0; // Kawa's SCI11+

        // the port settings are restored afterwards for some SCI0 late and SCI01+ games
        var oldPort = _ports.GetPort().Snapshot();

        _ports.PenMode(0);
        _ports.PenColor(0);
        _ports.TextGreyedOutput(false);

        int argc = argv.Count;
        int argi = 0;
        while (argc > 0)
        {
            var displayArg = argv[argi];
            int code = displayArg.Segment != 0 ? 0xFFFF : displayArg.Offset;
            argc--;
            argi++;
            switch (code)
            {
                case DisplayMovePen:
                    _ports.MoveTo(argv.UInt16(argi), argv.UInt16(argi + 1));
                    argc -= 2;
                    argi += 2;
                    break;
                case DisplaySetAlignment:
                    alignment = (TextAlignment)argv.Int16(argi);
                    argc--;
                    argi++;
                    break;
                case DisplaySetPenColor:
                    // the scripts pass -1 (0xffff) for no colour; ScummVM keeps these in an int16
                    colorPen = argv.Int16(argi);
                    _ports.PenColor(colorPen);
                    argc--;
                    argi++;
                    break;
                case DisplaySetBackgroundColor:
                    colorBack = argv.Int16(argi);
                    argc--;
                    argi++;
                    break;
                case DisplaySetGreyedOutput:
                    _ports.TextGreyedOutput(!argv[argi].IsNull);
                    argc--;
                    argi++;
                    break;
                case DisplaySetFont:
                    text16.SetFont(argv.UInt16(argi));
                    argc--;
                    argi++;
                    break;
                case DisplayWidth:
                    width = argv.UInt16(argi);
                    argc--;
                    argi++;
                    break;
                case DisplaySaveUnder:
                    doSaveUnder = true;
                    break;
                case DisplayRestoreUnder:
                    rect = BitsGetRect(argv[argi]);
                    rect.Translate(-_ports.GetPort().Left, -_ports.GetPort().Top);
                    BitsRestoreHunk(argv[argi]);
                    KernelGraphRedrawBox(rect);
                    argc = 0; // finishes the loop
                    break;
                case DisplayDontShowBits:
                    redraw = false;
                    break;
                case DisplaySetStroke:
                    stroke = argv.UInt16(argi);
                    argc--;
                    argi++;
                    break;
                default:
                    // ScummVM errors out unless a workaround ignores the argument; the known
                    // cases are script bugs, so the rest of the arguments are skipped
                    _log?.Warning("sci.gfx", $"Unknown kDisplay argument {displayArg}; ignoring the rest of the call's arguments");
                    argc = 0;
                    break;
            }
        }

        // drawing the text
        text16.Size(ref rect, text, languageSplitter, -1, width);
        rect.MoveTo(_ports.GetPort().CurLeft, _ports.GetPort().CurTop);
        // SCI1 middle and newer keep the text on screen (the LSL1 interpreter has this code too)
        if (_version >= SciVersion.V1Middle)
        {
            int leftPos = rect.Right <= _screen.Width ? 0 : _screen.Width - rect.Right;
            int topPos = rect.Bottom <= _screen.Height ? 0 : _screen.Height - rect.Bottom;
            _ports.Move(leftPos, topPos);
            rect.MoveTo(_ports.GetPort().CurLeft, _ports.GetPort().CurTop);
        }

        if (stroke != 0)
        {
            rect.Grow(1);
        }

        if (doSaveUnder)
        {
            result = BitsSaveHunk(rect, GfxScreenMask.Visual);
        }

        if (colorBack != -1)
        {
            FillRect(rect, GfxScreenMask.Visual, (byte)colorBack, 0, 0);
        }

        if (stroke != 0)
        {
            _ports.PenColor(0);
            rect.Translate(1, 0);
            if ((stroke & 1) != 0)
            {
                text16.Box(text, languageSplitter, false, rect, alignment, -1); // right
            }

            rect.Translate(0, 1);
            if ((stroke & 2) != 0)
            {
                text16.Box(text, languageSplitter, false, rect, alignment, -1); // bottom right
            }

            rect.Translate(-1, 0);
            if ((stroke & 4) != 0)
            {
                text16.Box(text, languageSplitter, false, rect, alignment, -1); // bottom
            }

            rect.Translate(-1, 0);
            if ((stroke & 8) != 0)
            {
                text16.Box(text, languageSplitter, false, rect, alignment, -1); // bottom left
            }

            rect.Translate(0, -1);
            if ((stroke & 16) != 0)
            {
                text16.Box(text, languageSplitter, false, rect, alignment, -1); // left
            }

            rect.Translate(0, -1);
            if ((stroke & 32) != 0)
            {
                text16.Box(text, languageSplitter, false, rect, alignment, -1); // top left
            }

            rect.Translate(1, 0);
            if ((stroke & 64) != 0)
            {
                text16.Box(text, languageSplitter, false, rect, alignment, -1); // top
            }

            rect.Translate(1, 0);
            if ((stroke & 128) != 0)
            {
                text16.Box(text, languageSplitter, false, rect, alignment, -1); // top right
            }

            rect.Translate(-1, 1); // back to the centre
            _ports.PenColor(colorPen);
        }

        text16.Box(text, languageSplitter, false, rect, alignment, -1);

        if (_screen.PicNotValid == 0 && redraw)
        {
            BitsShow(rect);
        }

        // restoring the port and the cursor position. The change happened between .530 (Hoyle 1)
        // and .566 (Hero's Quest); without exe version detection the old graphics functions
        // decide, which only affects Hoyle 1, KQ4 and the Fun Seeker's Guide
        var currport = _ports.GetPort();
        int tTop = currport.CurTop;
        int tLeft = currport.CurLeft;
        if (!_usesOldGfxFunctions)
        {
            currport.CopyFrom(oldPort);
        }

        currport.CurTop = tTop;
        currport.CurLeft = tLeft;
        return result;
    }

    /// <summary>Whether the game uses the SCI0 graphics functions; decides the port restore in kDisplay.</summary>
    public bool UsesOldGfxFunctions
    {
        get => _usesOldGfxFunctions;
        set => _usesOldGfxFunctions = value;
    }

    private bool _usesOldGfxFunctions;
    private ILog? _log;

    public ILog? Log
    {
        get => _log;
        set => _log = value;
    }
}
