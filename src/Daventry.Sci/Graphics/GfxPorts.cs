namespace Daventry.Sci.Graphics;

/// <summary>A drawing port: an origin and a clip rectangle with pen state. Mirrors ScummVM's Port.</summary>
public class Port
{
    public const ushort FirstWindowId = 2;
    public const ushort FirstScriptWindowId = 3;

    public Port(ushort id)
    {
        Id = id;
    }

    public ushort Id { get; }

    public int Top;
    public int Left;
    public SciRect Rect;
    public int CurTop;
    public int CurLeft;
    public int FontHeight;
    public int FontId;
    public bool GreyedOutput;
    public int PenClr;
    public int BackClr = 0xFF;
    public int PenMode;
    public int CounterTillFree;

    public bool IsWindow => Id >= FirstWindowId && Id != 0xFFFF;
}

/// <summary>A window: a port with a frame, a title and saved background. Mirrors ScummVM's Window.</summary>
public sealed class Window : Port
{
    public Window(ushort id)
        : base(id)
    {
    }

    /// <summary>Client area.</summary>
    public SciRect Dims;

    /// <summary>Total area including borders.</summary>
    public SciRect RestoreRect;

    public ushort WndStyle;
    public ushort SaveScreenMask;
    public byte[]? Saved1;
    public byte[]? Saved2;
    public string Title = string.Empty;
    public bool Drawn;
}

[Flags]
public enum WindowStyle : ushort
{
    Transparent = 1 << 0,
    NoFrame = 1 << 1,
    Title = 1 << 2,
    Topmost = 1 << 3,
    User = 1 << 7,
}

/// <summary>
/// Port management for SCI0 to SCI1.1: the menu port, the window manager port, the picture window
/// and the priority bands. Port of the parts of ScummVM's ports.cpp that do not draw; window
/// creation, drawing and disposal follow with the paint and text code.
/// </summary>
public sealed class GfxPorts
{
    private readonly GfxScreen _screen;
    private readonly byte[] _priorityBands = new byte[200];
    private readonly List<Port?> _windowsById = [];
    private int _priorityTop;
    private int _priorityBottom;
    private int _priorityBandCount;
    private bool _usesOldGfxFunctions;
    private SciVersion _version;
    private SciGameId _gameId;

    public GfxPorts(GfxScreen screen)
    {
        _screen = screen;
    }

    public Port WmgrPort { get; private set; } = null!;

    public Window PicWind { get; private set; } = null!;

    public Port MenuPort { get; private set; } = null!;

    public SciRect MenuBarRect { get; private set; }

    public SciRect MenuRect { get; private set; }

    public SciRect MenuLine { get; private set; }

    public Port CurPort { get; private set; } = null!;

    /// <summary>Windows in visual order, front last.</summary>
    public LinkedList<Port> WindowList { get; } = new();

    public IReadOnlyList<Port?> WindowsById => _windowsById;

    public ushort StyleUser { get; private set; }

    /// <summary>Port of GfxPorts::init, without drawing the initial picture window.</summary>
    public void Init(bool usesOldGfxFunctions, SciVersion version, SciGameId gameId)
    {
        _usesOldGfxFunctions = usesOldGfxFunctions;
        _version = version;
        _gameId = gameId;

        // the menu port has the hardcoded id 0xFFFF and is not known to the window manager
        MenuPort = new Port(0xFFFF);
        OpenPort(MenuPort);
        SetPort(MenuPort);
        MenuPort.Rect = new SciRect(0, 0, _screen.ScriptWidth, _screen.ScriptHeight);
        MenuBarRect = new SciRect(0, 0, _screen.ScriptWidth, 9);
        MenuRect = new SciRect(0, 0, _screen.ScriptWidth, 10);
        MenuLine = new SciRect(0, 9, _screen.ScriptWidth, 10);

        WmgrPort = new Port(1);
        _windowsById.Clear();
        _windowsById.Add(WmgrPort); // reachable as id 0
        _windowsById.Add(WmgrPort); // and as id 1

        StyleUser = version >= SciVersion.V1Late
            ? (ushort)WindowStyle.User
            : (ushort)(WindowStyle.User | WindowStyle.Transparent);

        // Jones, Slater, Hoyle 3 and 4 and the Crazy Nick's games ran with -Nw 0 0 200 320, so the
        // window manager port does not skip the first 10 lines; Mixed-Up Fairy Tales used -w 26
        int offTop = gameId switch
        {
            SciGameId.Jones or SciGameId.Slater or SciGameId.Hoyle3 or SciGameId.Hoyle4
                or SciGameId.CnickLaurabow or SciGameId.CnickKq or SciGameId.Mothergoose256 => 0,
            SciGameId.Fairytales => 26,
            _ => _screen.Height == 190 ? 0 : 10,
        };

        OpenPort(WmgrPort);
        SetPort(WmgrPort);

        // SCI0 games before KQ4 .502 did not adjust against the window manager port in kNewWindow
        if (!_usesOldGfxFunctions)
        {
            SetOrigin(0, offTop);
            WmgrPort.Rect.Bottom = _screen.Height - offTop;
        }
        else
        {
            WmgrPort.Rect.Bottom = _screen.Height;
        }

        WmgrPort.Rect.Right = _screen.ScriptWidth;
        WmgrPort.Rect.MoveTo(0, 0);
        WmgrPort.CurTop = 0;
        WmgrPort.CurLeft = 0;
        WindowList.AddFirst(WmgrPort);

        var picWindowRect = new SciRect(0, offTop, _screen.ScriptWidth, _screen.ScriptHeight);
        PicWind = AddWindow(picWindowRect, null, null, (ushort)(WindowStyle.Transparent | WindowStyle.NoFrame), 0, true);

        // for SCI0 games before KQ4 .502 the picture window's top is the menu bar height
        if (_usesOldGfxFunctions)
        {
            PicWind.Top = offTop;
        }

        KernelInitPriorityBands();
    }

    /// <summary>True for the Hoyle 3 demo, which always adds windows to the back of the list.</summary>
    public bool IsDemo { get; set; }

    public bool IsFrontWindow(Window wnd) => WindowList.Last?.Value == wnd;

    /// <summary>
    /// Creates a window and makes it the current port. Port of GfxPorts::addWindow; the frame and
    /// title are not painted yet (that needs the paint and text code), so <paramref name="draw"/>
    /// only marks the window drawn.
    /// </summary>
    public Window AddWindow(SciRect dims, SciRect? restoreRect, string? title, ushort style, int priority, bool draw)
    {
        // find an unused window id
        int id = Port.FirstWindowId;
        while (id < _windowsById.Count && _windowsById[id] is not null)
        {
            if (_windowsById[id]!.CounterTillFree != 0)
            {
                // disposed but not freed yet: reuse the handle (SQ4 CD recreates its inventory window constantly)
                FreeWindow((Window)_windowsById[id]!);
                _freeCounter--;
                break;
            }

            id++;
        }

        if (id == _windowsById.Count)
        {
            _windowsById.Add(null);
        }

        var pwnd = new Window((ushort)id);
        _windowsById[id] = pwnd;

        // KQ1sci, KQ4, Iceman and QfG2 always add windows to the back of the list; KQ5 CD checks the style.
        // The Hoyle 3 demo also always adds to the back.
        bool forceToBack = _version <= SciVersion.V1EgaOnly || (_gameId == SciGameId.Hoyle3 && IsDemo);
        if (!forceToBack && (style & (ushort)WindowStyle.Topmost) != 0)
        {
            WindowList.AddFirst(pwnd);
        }
        else
        {
            WindowList.AddLast(pwnd);
        }

        OpenPort(pwnd);

        var r = dims;
        // Sierra cleared the last bit of the left edge for EGA byte alignment and kept it in VGA games
        r.Left &= unchecked((int)0xFFFFFFFE);

        if (r.Width > _screen.ScriptWidth)
        {
            // invalid dimensions at the end of SQ3 and often in LSL5 and the GK1 demo (script bugs)
            r.Left = 0;
            r.Right = _screen.ScriptWidth - 1;
            if (style != StyleUser && (style & (ushort)WindowStyle.NoFrame) == 0)
            {
                r.Right--;
            }
        }

        pwnd.Rect = r;
        if (restoreRect is { } rr)
        {
            pwnd.RestoreRect = rr;
        }

        pwnd.WndStyle = style;
        pwnd.Saved1 = null;
        pwnd.Saved2 = null;
        pwnd.Drawn = false;
        if ((style & (ushort)WindowStyle.Transparent) == 0)
        {
            pwnd.SaveScreenMask = (ushort)(priority == -1 ? GfxScreenMask.Visual : GfxScreenMask.Visual | GfxScreenMask.Priority);
        }

        if (title is not null && (style & (ushort)WindowStyle.Title) != 0)
        {
            pwnd.Title = title;
        }

        r = pwnd.Rect;
        if (style != StyleUser && (style & (ushort)WindowStyle.NoFrame) == 0)
        {
            r.Left--;
            r.Top--;
            r.Right++;
            r.Bottom++;
            if ((style & (ushort)WindowStyle.Title) != 0)
            {
                r.Top -= 10;
                r.Bottom++;
            }
        }

        pwnd.Dims = r;

        // clip the window to the window manager port if needed
        var wmprect = WmgrPort.Rect;
        int oldtop = pwnd.Dims.Top;
        int oldleft = pwnd.Dims.Left;

        // the restore rect is adjusted along with the window rect, which SSCI did not do (Freddy Pharkas CD, bug #6154)
        if (wmprect.Top > pwnd.Dims.Top)
        {
            pwnd.Dims.MoveTo(pwnd.Dims.Left, wmprect.Top);
            if (restoreRect is not null)
            {
                pwnd.RestoreRect.MoveTo(pwnd.RestoreRect.Left, wmprect.Top);
            }
        }

        if (wmprect.Bottom < pwnd.Dims.Bottom)
        {
            pwnd.Dims.MoveTo(pwnd.Dims.Left, wmprect.Bottom - pwnd.Dims.Bottom + pwnd.Dims.Top);
            if (restoreRect is not null)
            {
                pwnd.RestoreRect.MoveTo(pwnd.RestoreRect.Left, wmprect.Bottom - pwnd.RestoreRect.Bottom + pwnd.RestoreRect.Top);
            }
        }

        if (wmprect.Right < pwnd.Dims.Right)
        {
            pwnd.Dims.MoveTo(wmprect.Right + pwnd.Dims.Left - pwnd.Dims.Right, pwnd.Dims.Top);
            if (restoreRect is not null)
            {
                pwnd.RestoreRect.MoveTo(wmprect.Right + pwnd.RestoreRect.Left - pwnd.RestoreRect.Right, pwnd.RestoreRect.Top);
            }
        }

        if (wmprect.Left > pwnd.Dims.Left)
        {
            pwnd.Dims.MoveTo(wmprect.Left, pwnd.Dims.Top);
            if (restoreRect is not null)
            {
                pwnd.RestoreRect.MoveTo(wmprect.Left, pwnd.RestoreRect.Top);
            }
        }

        pwnd.Rect.MoveTo(pwnd.Rect.Left + pwnd.Dims.Left - oldleft, pwnd.Rect.Top + pwnd.Dims.Top - oldtop);

        if (restoreRect is null)
        {
            pwnd.RestoreRect = pwnd.Dims;
        }

        if (draw)
        {
            DrawWindow(pwnd);
        }

        SetPort(pwnd);

        // SCI0 games before KQ4 .502 did not adjust against the window manager port; its top is 0 then
        SetOrigin(pwnd.Rect.Left, pwnd.Rect.Top + WmgrPort.Top);
        pwnd.Rect.MoveTo(0, 0);
        return pwnd;
    }

    /// <summary>Paints a window's frame and title. Frames need the paint code; until then only the drawn flag is set.</summary>
    public void DrawWindow(Window wnd)
    {
        if (wnd.Drawn)
        {
            return;
        }

        wnd.Drawn = true;
    }

    private void FreeWindow(Window wnd)
    {
        wnd.Saved1 = null;
        wnd.Saved2 = null;
        _windowsById[wnd.Id] = null;
    }

    private int _freeCounter;

    public Port? GetPortById(int id) => id >= 0 && id < _windowsById.Count ? _windowsById[id] : null;

    public Port SetPort(Port newPort)
    {
        var oldPort = CurPort;
        CurPort = newPort;
        return oldPort;
    }

    public Port GetPort() => CurPort;

    public void SetOrigin(int left, int top)
    {
        CurPort.Left = left;
        CurPort.Top = top;
    }

    public void MoveTo(int left, int top)
    {
        CurPort.CurTop = top;
        CurPort.CurLeft = left;
    }

    public void Move(int left, int top)
    {
        CurPort.CurTop += top;
        CurPort.CurLeft += left;
    }

    public void OpenPort(Port port)
    {
        port.FontId = 0;
        port.FontHeight = 8;

        var tmp = CurPort;
        CurPort = port;
        // the text manager sets the font when it exists; the default is the 8 pixel system font
        CurPort = tmp;

        port.Top = 0;
        port.Left = 0;
        port.GreyedOutput = false;
        port.PenClr = 0;
        port.BackClr = _screen.ColorWhite;
        port.PenMode = 0;
        port.Rect = new SciRect(0, 0, _screen.ScriptWidth, _screen.ScriptHeight);
    }

    public void PenColor(int color) => CurPort.PenClr = color;

    public void BackColor(int color) => CurPort.BackClr = color;

    public void PenMode(int mode) => CurPort.PenMode = mode;

    public void TextGreyedOutput(bool state) => CurPort.GreyedOutput = state;

    public int GetPointSize() => CurPort.FontHeight;

    public void OffsetRect(ref SciRect r)
    {
        r.Top += CurPort.Top;
        r.Bottom += CurPort.Top;
        r.Left += CurPort.Left;
        r.Right += CurPort.Left;
    }

    public void OffsetLine(ref SciPoint start, ref SciPoint end)
    {
        start.X += CurPort.Left;
        start.Y += CurPort.Top;
        end.X += CurPort.Left;
        end.Y += CurPort.Top;
    }

    public void ClipLine(ref SciPoint start, ref SciPoint end)
    {
        start.Y = Math.Clamp(start.Y, CurPort.Rect.Top, CurPort.Rect.Bottom - 1);
        start.X = Math.Clamp(start.X, CurPort.Rect.Left, CurPort.Rect.Right - 1);
        end.Y = Math.Clamp(end.Y, CurPort.Rect.Top, CurPort.Rect.Bottom - 1);
        end.X = Math.Clamp(end.X, CurPort.Rect.Left, CurPort.Rect.Right - 1);
    }

    /// <summary>The area a picture is drawn into: the current port's size at its origin.</summary>
    public SciRect PictureGetDisplayArea()
    {
        var displayArea = new SciRect(CurPort.Rect.Right, CurPort.Rect.Bottom);
        displayArea.MoveTo(CurPort.Left, CurPort.Top);
        return displayArea;
    }

    // ---- priority bands ----

    public void PriorityBandsInit(int bandCount, int top, int bottom)
    {
        if (bandCount != -1)
        {
            _priorityBandCount = bandCount;
        }

        _priorityTop = top;
        _priorityBottom = bottom;

        // Sierra used 32 bit integer maths here; anything else changes the result
        int bandSize = (_priorityBottom - _priorityTop) * 2000 / _priorityBandCount;

        Array.Clear(_priorityBands, 0, Math.Min(_priorityTop, _priorityBands.Length));
        int y;
        for (y = _priorityTop; y < _priorityBottom; y++)
        {
            _priorityBands[y] = (byte)(1 + (y - _priorityTop) * 2000 / bandSize);
        }

        if (_priorityBandCount == 15)
        {
            // with 15 bands, band 15 becomes band 14, as the original did
            y = _priorityBottom;
            while (_priorityBands[--y] == _priorityBandCount)
            {
                _priorityBands[y]--;
            }
        }

        // the rest is the highest band (200 is hardcoded, the algorithm is not meant for hires)
        for (y = _priorityBottom; y < 200; y++)
        {
            _priorityBands[y] = (byte)_priorityBandCount;
        }

        // a bottom of 200 is one past the screen; the original adjusted the same way
        if (_priorityBottom == 200)
        {
            _priorityBottom--;
        }
    }

    public void PriorityBandsInit(ReadOnlySpan<byte> data)
    {
        int i = 0;
        int inx;
        for (inx = 0; inx < 14; inx++)
        {
            byte priority = data[inx];
            while (i < priority && i < 200)
            {
                _priorityBands[i++] = (byte)inx;
            }
        }

        while (i < 200)
        {
            _priorityBands[i++] = (byte)inx;
        }
    }

    /// <summary>Reads the priority band words of a SCI1.1 picture.</summary>
    public void PriorityBandsInitSci11(ReadOnlySpan<byte> data)
    {
        Span<byte> priorityBands = stackalloc byte[14];
        for (int bandNo = 0; bandNo < 14; bandNo++)
        {
            priorityBands[bandNo] = (byte)(data[bandNo * 2] | (data[bandNo * 2 + 1] << 8));
        }

        PriorityBandsInit(priorityBands);
    }

    public void KernelInitPriorityBands()
    {
        if (_usesOldGfxFunctions)
        {
            PriorityBandsInit(15, 42, 200);
        }
        else if (_version >= SciVersion.V1_1)
        {
            PriorityBandsInit(14, 0, 190);
        }
        else
        {
            PriorityBandsInit(14, 42, 190);
        }
    }

    public void KernelGraphAdjustPriority(int top, int bottom) => PriorityBandsInit(_usesOldGfxFunctions ? 15 : 14, top, bottom);

    public byte KernelCoordinateToPriority(int y)
    {
        if (y < 0)
        {
            return _priorityBands[0];
        }

        // do not check the top; LSL2 room 54 relies on that
        if (y > _priorityBottom)
        {
            return _priorityBands[_priorityBottom];
        }

        return _priorityBands[y];
    }

    public int KernelPriorityToCoordinate(byte priority)
    {
        if (priority <= _priorityBandCount)
        {
            for (int y = 0; y <= _priorityBottom; y++)
            {
                if (_priorityBands[y] == priority)
                {
                    return y;
                }
            }
        }

        return _priorityBottom;
    }
}
