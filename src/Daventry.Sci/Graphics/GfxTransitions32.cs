using Daventry.Core.Hosting;
using Daventry.Sci.Engine;

namespace Daventry.Sci.Graphics;

public enum ShowStyleType
{
    None = 0,
    HShutterOut = 1,
    HShutterIn = 2,
    VShutterOut = 3,
    VShutterIn = 4,
    WipeLeft = 5,
    WipeRight = 6,
    WipeUp = 7,
    WipeDown = 8,
    IrisOut = 9,
    IrisIn = 10,
    DissolveNoMorph = 11,
    Dissolve = 12,
    FadeOut = 13,
    FadeIn = 14,
    Morph = 15,
}

/// <summary>A transition applied to a plane. One show style per plane can be active at a time.</summary>
public sealed class PlaneShowStyle
{
    /// <summary>The plane this transition applies to.</summary>
    public RegT Plane;

    public ShowStyleType Type;

    /// <summary>True for an entry transition into a new room, false for an exit transition away from an old one.</summary>
    public bool FadeUp;

    /// <summary>The number of steps.</summary>
    public short Divisions;

    /// <summary>The colour of the colour cels wipes draw; -1 for transitions that draw none.</summary>
    public short Color;

    /// <summary>The ticks between the steps of the animation.</summary>
    public int Delay;

    /// <summary>When true the transition yields to the game loop after each frame; otherwise it keeps the loop until it finishes.</summary>
    public bool Animate;

    /// <summary>The tick the next step runs at.</summary>
    public uint NextTick;

    /// <summary>The current step, out of <see cref="Divisions"/>.</summary>
    public int CurrentStep;

    /// <summary>Whether the style has finished and is ready for disposal.</summary>
    public bool Processed;

    // SCI2 to SCI2.1 early

    /// <summary>The screen items of a wipe, one block each; the frameout owns them.</summary>
    public List<ScreenItem?> ScreenItems = [];

    /// <summary>For wipes, the number of edges that move: 1, 2 or 4.</summary>
    public byte NumEdges;

    /// <summary>The plane's size in game script coordinates.</summary>
    public short Width;
    public short Height;

    /// <summary>For pixel dissolves, the screen item that shows the dissolving bitmap.</summary>
    public ScreenItem? BitmapScreenItem;

    /// <summary>For pixel dissolves, the bitmap.</summary>
    public RegT Bitmap;

    /// <summary>The bit mask of the pixel dissolve's sequence.</summary>
    public uint DissolveMask;

    /// <summary>The first pixel dissolved.</summary>
    public uint FirstPixel;

    /// <summary>The last pixel dissolved; equals <see cref="FirstPixel"/> again once every pixel went.</summary>
    public uint Pixel;

    // SCI2.1 middle to SCI3

    /// <summary>Palette index pairs [from, to, ...]; only those ranges fade.</summary>
    public List<ushort> FadeColorRanges = [];
}

/// <summary>A transition between two pictures within one plane, scrolling the new one in.</summary>
public sealed class PlaneScroll
{
    public RegT Plane;

    /// <summary>The current position of the scroll.</summary>
    public short X;
    public short Y;

    /// <summary>The distance to scroll each step; only one of them may be set.</summary>
    public short DeltaX;
    public short DeltaY;

    /// <summary>The picture created and scrolled into view.</summary>
    public int NewPictureId;

    /// <summary>The picture scrolled out of view and deleted from the plane.</summary>
    public int OldPictureId;

    public bool Animate;

    /// <summary>The tick after which the animation starts.</summary>
    public uint StartTick;
}

/// <summary>
/// The SCI32 plane transitions: the show styles kSetShowStyle starts (wipes, shutters, irises,
/// pixel dissolves, fades and the palette morph) and the picture scrolls of kSetScroll. The
/// steps that wait between frames are awaited, so the game loop keeps ticking the host.
/// </summary>
public sealed class GfxTransitions32
{
    private static readonly int[][] DissolveSequences =
    [
        /* SCI2.1 early and before */ [3, 6, 12, 20, 48, 96, 184, 272, 576, 1280, 3232, 6912, 13568, 24576, 46080, 0, 0, 0, 0, 0],
        /* SCI2.1 middle and later */ [0, 0, 3, 6, 12, 20, 48, 96, 184, 272, 576, 1280, 3232, 6912, 13568, 24576, 46080, 73728, 132096, 466944],
    ];

    private static readonly short[][] DivisionsDefaults =
    [
        // the last element in SCI2.1 early was added after LSL6 and PQ4
        /* SCI2.1 early and before */ [1, 20, 20, 20, 20, 20, 20, 20, 20, 20, 20, 40, 40, 101, 101, 2],
        /* SCI2.1 middle and later */ [1, 20, 20, 20, 20, 10, 10, 10, 10, 20, 20, 6, 10, 101, 101, 2],
    ];

    /// <summary>ScummVM's show list holds at most this many rectangles; the dissolve sends that many per frame.</summary>
    private const int ShowListMaxSize = 200;

    private readonly Sci32Graphics _g;
    private readonly int[] _dissolveSequenceSeeds;
    private readonly short[] _defaultDivisions;
    private readonly List<PlaneShowStyle> _showStyles = [];
    private readonly List<PlaneScroll> _scrolls = [];

    public GfxTransitions32(Sci32Graphics g)
    {
        _g = g;
        for (int i = 0; i < 236; i += 2)
        {
            StyleRanges[i] = 0;
            StyleRanges[i + 1] = -1;
        }

        for (int i = 236; i < StyleRanges.Length; i++)
        {
            StyleRanges[i] = 0;
        }

        int set = g.Version < SciVersion.V2_1Middle ? 0 : 1;
        _dissolveSequenceSeeds = DissolveSequences[set];
        _defaultDivisions = DivisionsDefaults[set];
    }

    /// <summary>Which palette entries the palette morph transition may change.</summary>
    public sbyte[] StyleRanges { get; } = new sbyte[256];

    private SegManager SegMan => _g.SegMan;

    private GfxFrameout Frameout => _g.Frameout;

    /// <summary>
    /// Keeps transitions from being instant on modern machines: show styles wait 10 ms between
    /// frames (the default 101 division fade was designed to take a second), scrolls 33 ms.
    /// </summary>
    private async ValueTask Throttle(uint ms = 10)
    {
        await _g.State.SpeedThrottler(ms);
        _g.State.ThrottleTrigger = true;
    }

    private void ClearShowRects() => Frameout.ClearShowList();

    private void AddShowRect(SciRect rect)
    {
        if (!rect.IsEmpty)
        {
            Frameout.AddShowRect(rect);
        }
    }

    private async ValueTask SendShowRects()
    {
        Frameout.ShowBits();
        await Throttle();
    }

    // ---- show styles ----

    public bool HasShowStyles => _showStyles.Count > 0;

    /// <summary>Runs every active show style, looping until the ones that hold the game loop finish.</summary>
    public async ValueTask ProcessShowStyles()
    {
        uint now = _g.TickCount;

        bool continueProcessing;
        bool doFrameOut;
        do
        {
            continueProcessing = false;
            doFrameOut = false;
            int i = 0;
            while (i < _showStyles.Count)
            {
                var showStyle = _showStyles[i];
                if (!showStyle.Animate)
                {
                    doFrameOut = true;
                }

                bool finished = await ProcessShowStyle(showStyle, now);
                if (!finished)
                {
                    continueProcessing = true;
                }

                if (finished && showStyle.Processed)
                {
                    DeleteShowStyle(i);
                }
                else
                {
                    i++;
                }
            }

            if (_g.State.ShouldQuit())
            {
                return;
            }

            if (doFrameOut)
            {
                Frameout.FrameOut(true);
                await Throttle();
            }
        }
        while (continueProcessing && doFrameOut);
    }

    /// <summary>Runs the show styles the palette morph frame applies.</summary>
    public async ValueTask ProcessEffects(PlaneShowStyle showStyle)
    {
        switch (showStyle.Type)
        {
            case ShowStyleType.HShutterOut:
                ProcessHShutterOut(showStyle);
                break;
            case ShowStyleType.HShutterIn:
                await ProcessHShutterIn(showStyle);
                break;
            case ShowStyleType.VShutterOut:
                ProcessVShutterOut();
                break;
            case ShowStyleType.VShutterIn:
                ProcessVShutterIn();
                break;
            case ShowStyleType.WipeLeft:
                ProcessWipeLeft();
                break;
            case ShowStyleType.WipeRight:
                ProcessWipeRight();
                break;
            case ShowStyleType.WipeUp:
                ProcessWipeUp();
                break;
            case ShowStyleType.WipeDown:
                ProcessWipeDown();
                break;
            case ShowStyleType.IrisOut:
                ProcessIrisOut(showStyle);
                break;
            case ShowStyleType.IrisIn:
                ProcessIrisIn(showStyle);
                break;
            case ShowStyleType.DissolveNoMorph:
            case ShowStyleType.Dissolve:
                await ProcessPixelDissolve(showStyle);
                break;
            default:
                break;
        }
    }

    public void KernelSetShowStyle(RegT planeObj, int typeNo, int seconds, int back, int priority, int animate, int frameOutNow, RegT fadeArray, int divisions, int blackScreen)
    {
        var type = (ShowStyleType)typeNo;
        _g.Log.Debug("sci.gfx", $"kSetShowStyle {type} on plane {planeObj}: {seconds}s, back {back}, animate {animate}, divisions {divisions}");
        bool hasFadeArray = !fadeArray.IsNull;
        bool hasDivisions = divisions != -1;

        bool isFadeUp;
        short color;
        if (back != -1)
        {
            isFadeUp = false;
            color = (short)back;
        }
        else
        {
            isFadeUp = true;
            color = _g.Palette.PlatformBlack;
        }

        // the Macintosh games swap black and white while drawing cels, which does not reach the
        // dissolve's copy of the frame buffer, so swap its colour here (GK1 room 471)
        if (type == ShowStyleType.Dissolve && _g.IsMacintosh)
        {
            if (color == 0)
            {
                color = 255;
            }
            else if (color == 255)
            {
                color = 0;
            }
        }

        var plane = Frameout.Planes.FindByObject(planeObj)
            ?? throw new SciVmException($"Plane {planeObj} is not present in active planes list");

        bool createNewEntry = true;
        var entry = FindShowStyleForPlane(planeObj);
        if (entry is not null)
        {
            bool useExisting = true;
            if (_g.Version < SciVersion.V2_1Middle)
            {
                useExisting = plane.GameRect.Width == entry.Width && plane.GameRect.Height == entry.Height;
            }

            if (useExisting)
            {
                useExisting = entry.Divisions == (hasDivisions ? divisions : _defaultDivisions[(int)type]);
            }

            if (useExisting)
            {
                createNewEntry = false;
                isFadeUp = true;
                entry.CurrentStep = 0;
            }
            else
            {
                isFadeUp = true;
                color = entry.Color;
                DeleteShowStyle(_showStyles.IndexOf(entry));
                entry = null;
            }
        }

        if (type == ShowStyleType.None)
        {
            if (!createNewEntry)
            {
                DeleteShowStyle(FindIndexForPlane(planeObj));
            }

            // kShowStyleNone is not queued, except by the Windows version of Hoyle 5, which fades
            // out and then calls it to enter the new screen without a fade in; its processNone
            // restores the palette
            if (_g.GameId != SciGameId.Hoyle5)
            {
                return;
            }
        }

        if (createNewEntry)
        {
            entry = new PlaneShowStyle
            {
                CurrentStep = 0,
                Processed = false,
                Divisions = hasDivisions ? (short)divisions : _defaultDivisions[(int)type],
                Plane = planeObj,
            };

            if (_g.Version < SciVersion.V2_1Middle)
            {
                // for the pixel dissolve
                entry.Bitmap = RegT.Null;
                entry.BitmapScreenItem = null;

                // for the wipes
                entry.ScreenItems.Clear();
                entry.Width = (short)plane.GameRect.Width;
                entry.Height = (short)plane.GameRect.Height;
            }
            else if (hasFadeArray)
            {
                var table = SegMan.LookupArray(fadeArray);
                int rangeCount = table.Size;
                entry.FadeColorRanges.Capacity = rangeCount;
                for (int i = 0; i < rangeCount; i++)
                {
                    entry.FadeColorRanges.Add((ushort)table.GetAsInt16(i));
                }
            }
        }

        if (entry is null)
        {
            throw new SciVmException("Cannot edit non-existing ShowStyle entry");
        }

        entry.FadeUp = isFadeUp;
        entry.Color = color;
        entry.NextTick = _g.TickCount;
        entry.Type = type;
        entry.Animate = animate != 0;
        entry.Delay = (seconds * 60 + entry.Divisions - 1) / entry.Divisions;

        if (entry.Delay == 0)
        {
            throw new SciVmException("ShowStyle has no duration");
        }

        if (frameOutNow != 0)
        {
            // creates the reference frame the pixel dissolves copy
            Frameout.FrameOut(false);
        }

        if (createNewEntry)
        {
            if (_g.Version <= SciVersion.V2_1Early)
            {
                switch (entry.Type)
                {
                    case ShowStyleType.WipeLeft:
                    case ShowStyleType.WipeRight:
                        Configure21EarlyHorizontalWipe(entry, (short)priority);
                        break;
                    case ShowStyleType.HShutterOut:
                        Configure21EarlyHorizontalShutter(entry, (short)priority);
                        break;
                    case ShowStyleType.IrisOut:
                    case ShowStyleType.IrisIn:
                        Configure21EarlyIris(entry, (short)priority);
                        break;
                    case ShowStyleType.Dissolve:
                        Configure21EarlyDissolve(entry, (short)priority, plane.GameRect);
                        break;
                    default:
                        break;
                }
            }

            _showStyles.Add(entry);
        }
    }

    /// <summary>Sets the palette range the morph frame may alter.</summary>
    public void KernelSetPalStyleRange(byte fromColor, byte toColor)
    {
        // the comparison is the wrong way round in Sierra's interpreter as well
        if (toColor > fromColor)
        {
            return;
        }

        for (int i = fromColor; i <= toColor; i++)
        {
            StyleRanges[i] = 0;
        }
    }

    private PlaneShowStyle? FindShowStyleForPlane(RegT planeObj)
    {
        foreach (var style in _showStyles)
        {
            if (style.Plane == planeObj)
            {
                return style;
            }
        }

        return null;
    }

    private int FindIndexForPlane(RegT planeObj)
    {
        for (int i = 0; i < _showStyles.Count; i++)
        {
            if (_showStyles[i].Plane == planeObj)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Frees what a show style owns and removes it from the list.</summary>
    private void DeleteShowStyle(int index)
    {
        if (index < 0)
        {
            return;
        }

        var showStyle = _showStyles[index];
        switch (showStyle.Type)
        {
            case ShowStyleType.DissolveNoMorph:
            case ShowStyleType.Dissolve:
                if (_g.Version <= SciVersion.V2_1Early)
                {
                    SegMan.FreeBitmap(showStyle.Bitmap);
                    Frameout.DeleteScreenItem(showStyle.BitmapScreenItem!);
                }

                break;
            case ShowStyleType.WipeLeft:
            case ShowStyleType.WipeRight:
            case ShowStyleType.IrisOut:
            case ShowStyleType.IrisIn:
            case ShowStyleType.HShutterOut:
                if (_g.Version <= SciVersion.V2_1Early)
                {
                    foreach (var screenItem in showStyle.ScreenItems)
                    {
                        if (screenItem is not null)
                        {
                            Frameout.DeleteScreenItem(screenItem);
                        }
                    }
                }

                break;
            case ShowStyleType.FadeIn:
            case ShowStyleType.FadeOut:
            case ShowStyleType.None:
            case ShowStyleType.Morph:
            case ShowStyleType.HShutterIn:
                break;
            default:
                throw new SciVmException($"Unknown delete transition type {showStyle.Type}");
        }

        _showStyles.RemoveAt(index);
    }

    private ScreenItem NewColorItem(PlaneShowStyle showStyle, in CelInfo32 celInfo, SciRect rect, short priority)
    {
        var item = new ScreenItem(_g, showStyle.Plane, celInfo, rect)
        {
            Priority = priority,
            FixedPriority = true,
        };
        showStyle.ScreenItems.Add(item);
        return item;
    }

    private void Configure21EarlyHorizontalWipe(PlaneShowStyle showStyle, short priority)
    {
        showStyle.NumEdges = 1;
        int divisions = showStyle.Divisions;
        showStyle.ScreenItems.Capacity = divisions;

        var celInfo = new CelInfo32 { Type = CelType.Color, Color = (byte)showStyle.Color };
        for (int i = 0; i < divisions; i++)
        {
            var rect = new SciRect(showStyle.Width * i / divisions, 0, showStyle.Width * (i + 1) / divisions, showStyle.Height);
            NewColorItem(showStyle, celInfo, rect, priority);
        }

        if (showStyle.FadeUp)
        {
            for (int i = 0; i < divisions; i++)
            {
                Frameout.AddScreenItem(showStyle.ScreenItems[i]!);
            }
        }
    }

    private void Configure21EarlyHorizontalShutter(PlaneShowStyle showStyle, short priority)
    {
        showStyle.NumEdges = 2;
        int numScreenItems = showStyle.NumEdges * showStyle.Divisions;
        showStyle.ScreenItems.Capacity = numScreenItems;

        var celInfo = new CelInfo32 { Type = CelType.Color, Color = (byte)showStyle.Color };
        int width = showStyle.Width;
        int divisions = showStyle.Divisions;

        for (int i = 0; i < divisions; i++)
        {
            // left; Sierra left the rectangle's left edge uninitialised, which in practice was 0
            var rect = new SciRect(0, 0, (width + 1) * (i + 1) / (2 * divisions), showStyle.Height);
            int leftLeft = rect.Left;
            NewColorItem(showStyle, celInfo, rect, priority);

            // right
            rect = new SciRect(width - rect.Right, 0, width - leftLeft, showStyle.Height);
            NewColorItem(showStyle, celInfo, rect, priority);
        }

        if (showStyle.FadeUp)
        {
            for (int i = 0; i < numScreenItems; i++)
            {
                Frameout.AddScreenItem(showStyle.ScreenItems[i]!);
            }
        }
    }

    private void Configure21EarlyIris(PlaneShowStyle showStyle, short priority)
    {
        showStyle.NumEdges = 4;
        int numScreenItems = showStyle.NumEdges * showStyle.Divisions;
        showStyle.ScreenItems.Capacity = numScreenItems;

        var celInfo = new CelInfo32 { Type = CelType.Color, Color = (byte)showStyle.Color };
        int width = showStyle.Width;
        int height = showStyle.Height;
        int divisions = showStyle.Divisions;

        for (int i = 0; i < divisions; i++)
        {
            // top
            int left = width * i / (2 * divisions);
            int top = height * i / (2 * divisions);
            int right = width - left;
            int bottom = (height + 1) * (i + 1) / (2 * divisions);
            int topTop = top;
            int topBottom = bottom;
            NewColorItem(showStyle, celInfo, new SciRect(left, top, right, bottom), priority);

            // bottom
            top = height - bottom;
            bottom = height - topTop;
            int bottomTop = top;
            NewColorItem(showStyle, celInfo, new SciRect(left, top, right, bottom), priority);

            // left
            top = topBottom;
            right = (width + 1) * (i + 1) / (2 * divisions);
            bottom = bottomTop;
            int leftLeft = left;
            NewColorItem(showStyle, celInfo, new SciRect(left, top, right, bottom), priority);

            // right
            left = width - right;
            right = width - leftLeft;
            NewColorItem(showStyle, celInfo, new SciRect(left, top, right, bottom), priority);
        }

        if (showStyle.FadeUp)
        {
            for (int i = 0; i < numScreenItems; i++)
            {
                Frameout.AddScreenItem(showStyle.ScreenItems[i]!);
            }
        }
    }

    private void Configure21EarlyDissolve(PlaneShowStyle showStyle, short priority, SciRect gameRect)
    {
        var bitmap = SegMan.AllocateBitmap(out var bitmapId, showStyle.Width, showStyle.Height, SciBitmap.DefaultSkipColor, 0, 0, Sci32Resolution.LowResX, Sci32Resolution.LowResY, 0, false, false);
        showStyle.Bitmap = bitmapId;

        var source = Frameout.CurrentBuffer;
        var target = new PixelBuffer(bitmap.RawData, bitmap.PixelsOffset, showStyle.Width, showStyle.Height);
        target.FillRect(new SciRect(bitmap.Width, bitmap.Height), SciBitmap.DefaultSkipColor);

        // copy the plane's part of the frame buffer to the bitmap's top left
        var copyRect = gameRect;
        copyRect.Clip(new SciRect(source.Width, source.Height));
        int rows = Math.Min(copyRect.Height, showStyle.Height);
        int columns = Math.Min(copyRect.Width, showStyle.Width);
        for (int y = 0; y < rows; y++)
        {
            Array.Copy(source.Pixels, (copyRect.Top + y) * source.Width + copyRect.Left, target.Pixels, target.IndexOf(0, y), columns);
        }

        var celInfo = new CelInfo32 { Type = CelType.Mem, Bitmap = bitmapId };
        showStyle.BitmapScreenItem = new ScreenItem(_g, showStyle.Plane, celInfo, new SciPoint(0, 0), new ScaleInfo())
        {
            Priority = priority,
            FixedPriority = true,
        };

        Frameout.AddScreenItem(showStyle.BitmapScreenItem);
    }

    /// <summary>Runs one tick of a show style. Returns true once it is finished.</summary>
    private async ValueTask<bool> ProcessShowStyle(PlaneShowStyle showStyle, uint now)
    {
        if (showStyle.NextTick >= now && showStyle.Animate)
        {
            return false;
        }

        bool early = _g.Version <= SciVersion.V2_1Early;
        switch (showStyle.Type)
        {
            case ShowStyleType.HShutterIn:
            case ShowStyleType.VShutterOut:
            case ShowStyleType.VShutterIn:
            case ShowStyleType.WipeUp:
            case ShowStyleType.WipeDown:
            case ShowStyleType.DissolveNoMorph:
            case ShowStyleType.Morph:
                return await ProcessMorph(showStyle);
            case ShowStyleType.HShutterOut:
                return early ? ProcessHShutterOut(showStyle) : await ProcessMorph(showStyle);
            case ShowStyleType.WipeLeft:
                return early ? ProcessWipe(-1, showStyle) : await ProcessMorph(showStyle);
            case ShowStyleType.WipeRight:
                return early ? ProcessWipe(1, showStyle) : await ProcessMorph(showStyle);
            case ShowStyleType.Dissolve:
                return early ? await ProcessPixelDissolve(showStyle) : await ProcessMorph(showStyle);
            case ShowStyleType.IrisOut:
                return early ? ProcessIrisOut(showStyle) : await ProcessMorph(showStyle);
            case ShowStyleType.IrisIn:
                return early ? ProcessIrisIn(showStyle) : await ProcessMorph(showStyle);
            case ShowStyleType.FadeOut:
                return ProcessFade(-1, showStyle);
            case ShowStyleType.FadeIn:
                return ProcessFade(1, showStyle);
            case ShowStyleType.None:
            default:
                return ProcessNone(showStyle);
        }
    }

    /// <summary>An instant change between rooms.</summary>
    private bool ProcessNone(PlaneShowStyle showStyle)
    {
        _g.Palette.SetFade((ushort)(showStyle.FadeUp ? 100 : 0), 0, 255);
        showStyle.Processed = true;
        return true;
    }

    private bool ProcessHShutterOut(PlaneShowStyle showStyle)
    {
        if (_g.Version > SciVersion.V2_1Early)
        {
            throw new SciVmException("HShutterOut is not known to be used by any game");
        }

        return ProcessWipe(-1, showStyle);
    }

    /// <summary>Reveals the new room from both sides towards the middle.</summary>
    private async ValueTask ProcessHShutterIn(PlaneShowStyle showStyle)
    {
        if (_g.Version <= SciVersion.V2_1Early)
        {
            throw new SciVmException("HShutterIn is not known to be used by any SCI2.1early- game");
        }

        var plane = Frameout.VisiblePlanes.FindByObject(showStyle.Plane)
            ?? throw new SciVmException($"HShutterIn: visible plane {showStyle.Plane} not found");
        var screenRect = plane.ScreenRect;

        int divisions = showStyle.Divisions;
        int width = screenRect.Width;
        int divisionWidth = width / divisions - 1;

        ClearShowRects();

        if (width % divisions != 0)
        {
            AddShowRect(new SciRect(
                (divisionWidth + 1) * divisions + screenRect.Left,
                screenRect.Top,
                (divisionWidth + 1) * divisions + (width % divisions) + screenRect.Left,
                screenRect.Bottom));
            await SendShowRects();
        }

        for (int i = 0; i < width / (2 * divisions); i++)
        {
            // left side
            AddShowRect(new SciRect(i * divisions + screenRect.Left, screenRect.Top, i * divisions + divisions + screenRect.Left, screenRect.Bottom));

            // right side
            AddShowRect(new SciRect((divisionWidth - i) * divisions + screenRect.Left, screenRect.Top, (divisionWidth - i) * divisions + divisions + screenRect.Left, screenRect.Bottom));

            await SendShowRects();
        }

        AddShowRect(screenRect);
        await SendShowRects();
    }

    private static void ProcessVShutterOut() => throw new SciVmException("VShutterOut is not known to be used by any game");

    private static void ProcessVShutterIn() => throw new SciVmException("VShutterIn is not known to be used by any game");

    private void ProcessWipeLeft()
    {
        if (_g.Version > SciVersion.V2_1Early)
        {
            throw new SciVmException("WipeLeft is not known to be used by any SCI2.1mid+ game");
        }
    }

    private void ProcessWipeRight()
    {
        if (_g.Version > SciVersion.V2_1Early)
        {
            throw new SciVmException("WipeRight is not known to be used by any SCI2.1mid+ game");
        }
    }

    private static void ProcessWipeUp() => throw new SciVmException("WipeUp is not known to be used by any game");

    private static void ProcessWipeDown() => throw new SciVmException("WipeDown is not known to be used by any game");

    private bool ProcessIrisOut(PlaneShowStyle showStyle)
    {
        if (_g.Version > SciVersion.V2_1Early)
        {
            throw new SciVmException("IrisOut is not known to be used by any SCI2.1mid+ game");
        }

        return ProcessWipe(-1, showStyle);
    }

    private bool ProcessIrisIn(PlaneShowStyle showStyle)
    {
        if (_g.Version > SciVersion.V2_1Early)
        {
            throw new SciVmException("IrisIn is not known to be used by any SCI2.1mid+ game");
        }

        return ProcessWipe(1, showStyle);
    }

    private static int BitWidth(int number)
    {
        int width = 0;
        while (number != 0)
        {
            number >>= 1;
            width++;
        }

        return width;
    }

    private ValueTask<bool> ProcessPixelDissolve(PlaneShowStyle showStyle)
        => _g.Version > SciVersion.V2_1Early
            ? ProcessPixelDissolve21Mid(showStyle)
            : new ValueTask<bool>(ProcessPixelDissolve21Early(showStyle));

    /// <summary>Advances the linear feedback sequence to the next pixel index below the pixel count.</summary>
    private static uint NextDissolvePixel(uint index, uint mask, uint numPixels)
    {
        for (; ; )
        {
            if ((index & 1) != 0)
            {
                index >>= 1;
                index ^= mask;
            }
            else
            {
                index >>= 1;
            }

            if (index < numPixels)
            {
                return index;
            }
        }
    }

    /// <summary>SCI2 to SCI2.1 early: paints the dissolve colour into the bitmap pixel by pixel in a pseudo random order.</summary>
    private bool ProcessPixelDissolve21Early(PlaneShowStyle showStyle)
    {
        bool unchanged = true;

        var bitmap = SegMan.LookupBitmap(showStyle.Bitmap);
        var buffer = new PixelBuffer(bitmap.RawData, bitmap.PixelsOffset, showStyle.Width, showStyle.Height);

        uint numPixels = (uint)(showStyle.Width * showStyle.Height);
        uint numPixelsPerDivision = (numPixels + (uint)showStyle.Divisions) / (uint)showStyle.Divisions;

        uint index;
        if (showStyle.CurrentStep == 0)
        {
            int i = 0;
            index = numPixels;
            if (index != 1)
            {
                for (; ; )
                {
                    index >>= 1;
                    if (index == 1)
                    {
                        break;
                    }

                    i++;
                }
            }

            showStyle.DissolveMask = (uint)_dissolveSequenceSeeds[i];
            index = 53427;

            showStyle.FirstPixel = index;
            showStyle.Pixel = index;
        }
        else
        {
            index = NextDissolvePixel(showStyle.Pixel, showStyle.DissolveMask, numPixels);
            if (index == showStyle.FirstPixel)
            {
                index = 0;
            }
        }

        if (showStyle.CurrentStep < showStyle.Divisions)
        {
            for (uint i = 0; i < numPixelsPerDivision; i++)
            {
                buffer.Pixels[buffer.IndexOf((int)(index % showStyle.Width), (int)(index / showStyle.Width))] = (byte)showStyle.Color;

                index = NextDissolvePixel(index, showStyle.DissolveMask, numPixels);
                if (index == showStyle.FirstPixel)
                {
                    buffer.FillRect(new SciRect(0, 0, showStyle.Width, showStyle.Height), (byte)showStyle.Color);
                    break;
                }
            }

            showStyle.Pixel = index;
            showStyle.NextTick += (uint)showStyle.Delay;
            showStyle.CurrentStep++;
            unchanged = false;
            if (showStyle.BitmapScreenItem!.Created == 0)
            {
                showStyle.BitmapScreenItem.Updated = Frameout.ScreenCount;
            }
        }

        if (showStyle.CurrentStep >= showStyle.Divisions && unchanged)
        {
            if (showStyle.FadeUp)
            {
                showStyle.Processed = true;
            }

            return true;
        }

        return false;
    }

    /// <summary>SCI2.1 middle and later: shows the new frame block by block in a pseudo random order (SQ6 room 530, LSL7 room 130).</summary>
    private async ValueTask<bool> ProcessPixelDissolve21Mid(PlaneShowStyle showStyle)
    {
        var plane = Frameout.VisiblePlanes.FindByObject(showStyle.Plane)
            ?? throw new SciVmException($"Dissolve: visible plane {showStyle.Plane} not found");
        var screenRect = plane.ScreenRect;

        int planeWidth = screenRect.Width;
        int planeHeight = screenRect.Height;
        int divisions = showStyle.Divisions;
        int width = planeWidth / divisions + (planeWidth % divisions != 0 ? 1 : 0);
        int height = planeHeight / divisions + (planeHeight % divisions != 0 ? 1 : 0);

        uint mask = (uint)_dissolveSequenceSeeds[BitWidth(width * height - 1)];
        int seq = 1;

        int iteration = 0;
        ClearShowRects();

        do
        {
            int row = seq / width;
            int col = seq % width;

            if (row < height)
            {
                int right = col == width - 1 && planeWidth % divisions != 0 ? col * divisions + planeWidth % divisions : col * divisions + divisions;
                int bottom = row == height - 1 && planeHeight % divisions != 0 ? row * divisions + planeHeight % divisions : row * divisions + divisions;
                AddShowRect(new SciRect(col * divisions, row * divisions, right, bottom));
            }

            if ((seq & 1) != 0)
            {
                seq = (int)((uint)(seq >> 1) ^ mask);
            }
            else
            {
                seq >>= 1;
            }

            if (++iteration == ShowListMaxSize)
            {
                await SendShowRects();
                iteration = 0;
            }
        }
        while (seq != 1 && !_g.State.ShouldQuit());

        AddShowRect(new SciRect(screenRect.Left, screenRect.Top, screenRect.Left + divisions, screenRect.Top + divisions));
        await SendShowRects();

        AddShowRect(screenRect);
        await SendShowRects();

        return true;
    }

    /// <summary>Fades the palette towards black (direction below zero) or back up.</summary>
    private bool ProcessFade(int direction, PlaneShowStyle showStyle)
    {
        bool unchanged = true;
        if (showStyle.CurrentStep < showStyle.Divisions)
        {
            int percent = direction <= 0 ? showStyle.Divisions - showStyle.CurrentStep - 1 : showStyle.CurrentStep;
            percent *= 100;
            percent /= showStyle.Divisions - 1;

            if (showStyle.FadeColorRanges.Count > 0)
            {
                for (int i = 0, len = showStyle.FadeColorRanges.Count; i < len; i += 2)
                {
                    _g.Palette.SetFade((ushort)percent, (byte)showStyle.FadeColorRanges[i], showStyle.FadeColorRanges[i + 1]);
                }
            }
            else
            {
                _g.Palette.SetFade((ushort)percent, 0, 255);
            }

            showStyle.CurrentStep++;
            showStyle.NextTick += (uint)showStyle.Delay;
            unchanged = false;
        }

        if (showStyle.CurrentStep >= showStyle.Divisions && unchanged)
        {
            if (direction > 0)
            {
                showStyle.Processed = true;
            }

            return true;
        }

        return false;
    }

    /// <summary>The morph renders through the palette morph frame, which runs the other effects from <see cref="ProcessEffects"/>.</summary>
    private async ValueTask<bool> ProcessMorph(PlaneShowStyle showStyle)
    {
        await Frameout.PalMorphFrameOut(StyleRanges, showStyle);
        showStyle.Processed = true;
        return true;
    }

    /// <summary>One step of a wipe, shutter or iris: adds or removes the blocks of one edge.</summary>
    private bool ProcessWipe(int direction, PlaneShowStyle showStyle)
    {
        bool unchanged = true;
        if (showStyle.CurrentStep < showStyle.Divisions)
        {
            int index = direction > 0 ? showStyle.CurrentStep : showStyle.Divisions - showStyle.CurrentStep - 1;
            index *= showStyle.NumEdges;
            for (int i = 0; i < showStyle.NumEdges; i++)
            {
                var screenItem = showStyle.ScreenItems[index + i]!;
                if (showStyle.FadeUp)
                {
                    Frameout.DeleteScreenItem(screenItem);
                    showStyle.ScreenItems[index + i] = null;
                }
                else
                {
                    Frameout.AddScreenItem(screenItem);
                }
            }

            showStyle.CurrentStep++;
            showStyle.NextTick += (uint)showStyle.Delay;
            unchanged = false;
        }

        if (showStyle.CurrentStep >= showStyle.Divisions && unchanged)
        {
            if (showStyle.FadeUp)
            {
                showStyle.Processed = true;
            }

            return true;
        }

        return false;
    }

    // ---- scrolls ----

    public bool HasScrolls => _scrolls.Count > 0;

    /// <summary>Advances every active scroll by one step.</summary>
    public async ValueTask ProcessScrolls()
    {
        for (int i = 0; i < _scrolls.Count;)
        {
            if (ProcessScroll(_scrolls[i]))
            {
                _scrolls.RemoveAt(i);
            }
            else
            {
                i++;
            }
        }

        await Throttle(33);
    }

    public async ValueTask KernelSetScroll(RegT planeId, int deltaX, int deltaY, int pictureId, bool animate, bool mirrorX)
    {
        foreach (var existing in _scrolls)
        {
            if (existing.Plane == planeId)
            {
                throw new SciVmException($"Scroll already exists on plane {planeId}");
            }
        }

        if (deltaX == 0 && deltaY == 0)
        {
            throw new SciVmException("kSetScroll: Scroll has no movement");
        }

        if (deltaX != 0 && deltaY != 0)
        {
            throw new SciVmException("kSetScroll: Cannot scroll in two dimensions");
        }

        var scroll = new PlaneScroll
        {
            Plane = planeId,
            X = 0,
            Y = 0,
            DeltaX = (short)deltaX,
            DeltaY = (short)deltaY,
            NewPictureId = pictureId,
            Animate = animate,
            StartTick = _g.TickCount,
        };

        var plane = Frameout.Planes.FindByObject(planeId)
            ?? throw new SciVmException($"kSetScroll: Plane {planeId} not found");

        // Sierra looked the plane up in the active list twice here, not the visible list
        var gameRect = plane.GameRect;
        SciPoint picOrigin;
        if (deltaX != 0)
        {
            short x = (short)(deltaX > 0 ? -gameRect.Width : gameRect.Width);
            scroll.X = x;
            picOrigin = new SciPoint(x, 0);
        }
        else
        {
            short y = (short)(deltaY > 0 ? -gameRect.Height : gameRect.Height);
            scroll.Y = y;
            picOrigin = new SciPoint(0, y);
        }

        scroll.OldPictureId = plane.AddPic(pictureId, picOrigin, mirrorX);

        if (animate)
        {
            _scrolls.Insert(0, scroll);
        }
        else
        {
            bool finished = false;
            while (!finished && !_g.State.ShouldQuit())
            {
                finished = ProcessScroll(scroll);
                Frameout.FrameOut(true);
                await Throttle(33);
            }
        }
    }

    /// <summary>Moves a plane's content one step. Returns true once the new picture is in place.</summary>
    private bool ProcessScroll(PlaneScroll scroll)
    {
        bool finished = false;
        uint now = _g.TickCount;
        if (scroll.StartTick >= now)
        {
            return false;
        }

        int deltaX = scroll.DeltaX;
        int deltaY = scroll.DeltaY;
        if ((scroll.X + deltaX) * scroll.X <= 0)
        {
            deltaX = -scroll.X;
        }

        if ((scroll.Y + deltaY) * scroll.Y <= 0)
        {
            deltaY = -scroll.Y;
        }

        scroll.X += (short)deltaX;
        scroll.Y += (short)deltaY;

        var plane = Frameout.Planes.FindByObject(scroll.Plane)
            ?? throw new SciVmException($"processScroll: Plane {scroll.Plane} not found");

        if (scroll.X == 0 && scroll.Y == 0)
        {
            plane.DeletePic(scroll.OldPictureId, scroll.NewPictureId);
            finished = true;
        }

        plane.ScrollScreenItems(deltaX, deltaY, true);
        return finished;
    }
}
