using Daventry.Sci.Engine;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Graphics;

public enum ScaleSignals32
{
    None = 0,
    Manual = 1,
    VanishingPoint = 2,
}

/// <summary>How a screen item scales: fixed ratios or by distance from the plane's vanishing point.</summary>
public struct ScaleInfo
{
    public int X = 128;
    public int Y = 128;
    public int Max = 100;
    public ScaleSignals32 Signal = ScaleSignals32.None;

    public ScaleInfo()
    {
    }
}

/// <summary>
/// The engine side of a game script View: where it is, which cel it shows, how it scales, and
/// the rectangles that place it on screen. Port of ScummVM's ScreenItem.
/// </summary>
public sealed class ScreenItem
{
    private readonly Sci32Graphics _g;

    /// <summary>The plane this item belongs to.</summary>
    public RegT Plane;

    public ScaleInfo Scale;

    /// <summary>Position and size in screen coordinates including the plane offset, not clipped to the screen.</summary>
    private SciRect _screenItemRect;

    /// <summary>Whether <see cref="_insetRect"/> crops the cel instead of using its full size.</summary>
    private bool _useInsetRect;

    /// <summary>The cropping rectangle, or the fill rectangle of a colour cel.</summary>
    private SciRect _insetRect;

    /// <summary>The z index in pseudo 3D space; higher draws on top.</summary>
    private int _z;

    /// <summary>Creation order, for a stable sort of items of equal priority and z.</summary>
    public uint CreationId;

    public CelInfo32 CelInfo;

    private CelObj? _celObj;

    /// <summary>Whether the priority is fixed rather than derived from the y position and z.</summary>
    public bool FixedPriority;

    /// <summary>The drawing priority within the plane; higher draws on top.</summary>
    public short Priority;

    /// <summary>The top left corner in game script coordinates relative to the plane.</summary>
    public SciPoint Position;

    /// <summary>The script object, or a number for items the engine created.</summary>
    public RegT Object;

    /// <summary>For picture cels, the picture's resource id.</summary>
    public int PictureId = -1;

    /// <summary>Frame counters: set when the item was created, updated from the script, or deleted.</summary>
    public int Created;
    public int Updated;
    public int Deleted;

    /// <summary>For picture cels, the plane's mirror flag; combined with the cel's own flag when drawing.</summary>
    public bool MirrorX;

    /// <summary>The scaling ratios the item draws with, from the scale info at the last update.</summary>
    public Ratio RatioX = new();
    public Ratio RatioY = new();

    /// <summary>The top left corner in screen coordinates.</summary>
    public SciPoint ScaledPosition;

    /// <summary>Position and size in screen coordinates, clipped to the screen.</summary>
    public SciRect ScreenRect;

    /// <summary>Whether every second line draws black, for pixel doubled video.</summary>
    public bool DrawBlackLines;

    /// <summary>From a script View object.</summary>
    public ScreenItem(Sci32Graphics g, RegT obj)
    {
        _g = g;
        CreationId = g.Frameout.NextScreenItemCreationId++;
        Object = obj;
        Created = g.Frameout.ScreenCount;
        SetFromObject(obj, true, true);
        Plane = Selectors.ReadSelector(g.State, obj, g.State.Kernel.Selectors.Plane);
    }

    /// <summary>An engine generated item in a plane.</summary>
    public ScreenItem(Sci32Graphics g, RegT plane, in CelInfo32 celInfo)
    {
        _g = g;
        CreationId = g.Frameout.NextScreenItemCreationId++;
        Plane = plane;
        CelInfo = celInfo;
        Object = RegT.Make(0, g.Frameout.NextScreenItemObjectId++);
        Created = g.Frameout.ScreenCount;
    }

    /// <summary>An engine generated item at a rectangle; colour cels fill it.</summary>
    public ScreenItem(Sci32Graphics g, RegT plane, in CelInfo32 celInfo, SciRect rect)
        : this(g, plane, celInfo)
    {
        Position = new SciPoint(rect.Left, rect.Top);
        if (celInfo.Type == CelType.Color)
        {
            _insetRect = rect;
        }
    }

    public ScreenItem(Sci32Graphics g, RegT plane, in CelInfo32 celInfo, SciPoint position, ScaleInfo scaleInfo)
        : this(g, plane, celInfo)
    {
        Position = position;
        Scale = scaleInfo;
    }

    /// <summary>The copy kept in the visible plane list.</summary>
    public ScreenItem(ScreenItem other)
    {
        _g = other._g;
        CreationId = other.CreationId;
        Plane = other.Plane;
        Scale = other.Scale;
        _useInsetRect = other._useInsetRect;
        CelInfo = other.CelInfo;
        Object = other.Object;
        MirrorX = other.MirrorX;
        ScaledPosition = other.ScaledPosition;
        ScreenRect = other.ScreenRect;
        DrawBlackLines = other.DrawBlackLines;
        if (other._useInsetRect)
        {
            _insetRect = other._insetRect;
        }
    }

    /// <summary>Synchronises the visible copy with the item; a changed cel drops the cached cel object.</summary>
    public void Assign(ScreenItem other)
    {
        // bitmap cels are always invalidated: a game may free a bitmap and create another at
        // the same table slot (LSL7's About screen does when hovering names)
        if (CelInfo.Type == CelType.Mem || CelInfo != other.CelInfo)
        {
            CelInfo = other.CelInfo;
            _celObj = null;
        }

        CreationId = other.CreationId;
        ScreenRect = other.ScreenRect;
        MirrorX = other.MirrorX;
        _useInsetRect = other._useInsetRect;
        if (other._useInsetRect)
        {
            _insetRect = other._insetRect;
        }

        Scale = other.Scale;
        ScaledPosition = other.ScaledPosition;
        DrawBlackLines = other.DrawBlackLines;
    }

    /// <summary>Sorting order: priority, then y plus z, then engine items after script items, then creation order.</summary>
    public static bool IsLess(ScreenItem a, ScreenItem b)
    {
        if (a.Priority < b.Priority)
        {
            return true;
        }

        if (a.Priority == b.Priority)
        {
            if (a.Position.Y + a._z < b.Position.Y + b._z)
            {
                return true;
            }

            if (a.Position.Y + a._z == b.Position.Y + b._z)
            {
                // engine items have numeric ids, which Sierra started at 20000 so they sorted above
                // every script view at the same position and priority
                if (b.Object.IsNumber && !a.Object.IsNumber)
                {
                    return true;
                }

                // Sierra compared object ids as the last resort, which breaks when ids are reused
                // (Phantasmagoria's death dialog, Hoyle 5's text buttons); creation order is stable
                return a.CreationId < b.CreationId;
            }
        }

        return false;
    }

    public static bool IsGreater(ScreenItem a, ScreenItem b)
    {
        if (a.Priority > b.Priority)
        {
            return true;
        }

        if (a.Priority == b.Priority)
        {
            if (a.Position.Y + a._z > b.Position.Y + b._z)
            {
                return true;
            }

            if (a.Position.Y + a._z == b.Position.Y + b._z)
            {
                if (a.Object.IsNumber && !b.Object.IsNumber)
                {
                    return true;
                }

                return a.CreationId > b.CreationId;
            }
        }

        return false;
    }

    public bool HasPriorityAbove(ScreenItem other)
    {
        if (Priority > other.Priority)
        {
            return true;
        }

        if (Priority == other.Priority)
        {
            if (Object.IsNumber && !other.Object.IsNumber)
            {
                return true;
            }

            return CreationId > other.CreationId;
        }

        return false;
    }

    private void SetFromObject(RegT obj, bool updateCel, bool updateBitmap)
    {
        var s = _g.State;
        var sel = s.Kernel.Selectors;
        Position.X = (short)Selectors.ReadSelectorValue(s, obj, sel.X);
        Position.Y = (short)Selectors.ReadSelectorValue(s, obj, sel.Y);
        Scale.X = (short)Selectors.ReadSelectorValue(s, obj, sel.ScaleX);
        Scale.Y = (short)Selectors.ReadSelectorValue(s, obj, sel.ScaleY);
        Scale.Max = (short)Selectors.ReadSelectorValue(s, obj, sel.MaxScale);
        Scale.Signal = (ScaleSignals32)(Selectors.ReadSelectorValue(s, obj, sel.ScaleSignal) & 3);

        if (updateCel)
        {
            CelInfo.ResourceId = Selectors.ReadSelectorValue(s, obj, sel.View);
            CelInfo.LoopNo = (short)Selectors.ReadSelectorValue(s, obj, sel.Loop);
            CelInfo.CelNo = (short)Selectors.ReadSelectorValue(s, obj, sel.Cel);

            if (CelInfo.ResourceId <= Plane32.PlanePic)
            {
                var view = _g.ResMan.FindResource(ResourceType.View, CelInfo.ResourceId)
                    ?? throw new SciVmException($"Failed to load {CelInfo}");
                var data = new CelData(view.Data!);
                int headerSize = data.ReadUInt16(0) + 2;
                int loopCount = data[2];
                int loopSize = data[12];

                // the loop number is unsigned in Sierra's interpreter, so negative ones wrap and get fixed here
                if ((ushort)CelInfo.LoopNo >= loopCount)
                {
                    int maxLoopNo = loopCount - 1;
                    CelInfo.LoopNo = (short)maxLoopNo;
                    Selectors.WriteSelectorValue(s, obj, sel.Loop, maxLoopNo);
                }

                int loopData = headerSize + CelInfo.LoopNo * loopSize;
                int seekEntry = (sbyte)data[loopData];
                if (seekEntry != -1)
                {
                    loopData = headerSize + seekEntry * loopSize;
                }

                int celCount = data[loopData + 2];
                if ((ushort)CelInfo.CelNo >= celCount)
                {
                    int maxCelNo = celCount - 1;
                    CelInfo.CelNo = (short)maxCelNo;
                    Selectors.WriteSelectorValue(s, obj, sel.Cel, maxCelNo);
                }
            }
        }

        var bitmap = Selectors.ReadSelector(s, obj, sel.Bitmap);
        if (updateBitmap && !bitmap.IsNull)
        {
            CelInfo.Bitmap = bitmap;
            CelInfo.Type = CelType.Mem;
        }
        else
        {
            CelInfo.Bitmap = RegT.Null;
            CelInfo.Type = CelType.View;
        }

        if (updateCel || updateBitmap)
        {
            _celObj = null;
        }

        if (Selectors.ReadSelectorValue(s, obj, sel.FixPriority) != 0)
        {
            FixedPriority = true;
            Priority = (short)Selectors.ReadSelectorValue(s, obj, sel.Priority);
        }
        else
        {
            FixedPriority = false;
            Selectors.WriteSelectorValue(s, obj, sel.Priority, Position.Y);
        }

        _z = (short)Selectors.ReadSelectorValue(s, obj, sel.Z);
        Position.Y -= _z;

        if (_g.Features.UsesAlternateSelectors)
        {
            if (Selectors.ReadSelectorValue(s, obj, sel.SeenRect) != 0)
            {
                _useInsetRect = true;
                _insetRect = new SciRect(
                    (short)Selectors.ReadSelectorValue(s, obj, sel.Left),
                    (short)Selectors.ReadSelectorValue(s, obj, sel.Top),
                    (short)Selectors.ReadSelectorValue(s, obj, sel.Right) + 1,
                    (short)Selectors.ReadSelectorValue(s, obj, sel.Bottom) + 1);
            }
            else
            {
                _useInsetRect = false;
            }
        }
        else if (Selectors.ReadSelectorValue(s, obj, sel.UseInsetRect) != 0)
        {
            _useInsetRect = true;
            _insetRect = new SciRect(
                (short)Selectors.ReadSelectorValue(s, obj, sel.InLeft),
                (short)Selectors.ReadSelectorValue(s, obj, sel.InTop),
                (short)Selectors.ReadSelectorValue(s, obj, sel.InRight) + 1,
                (short)Selectors.ReadSelectorValue(s, obj, sel.InBottom) + 1);
        }
        else
        {
            _useInsetRect = false;
        }

        s.SegMan.GetObject(obj)!.ClearInfoSelectorFlag(InfoFlags.ViewVisible);
    }

    /// <summary>Works out the screen rectangles and scaling ratios of the item within its plane. Was Update in Sierra's interpreter.</summary>
    public void CalcRects(Plane32 plane)
    {
        var frameout = _g.Frameout;
        int scriptWidth = frameout.ScriptWidth;
        int scriptHeight = frameout.ScriptHeight;
        int screenWidth = frameout.ScreenWidth;
        int screenHeight = frameout.ScreenHeight;

        var celObj = GetCelObj();
        var celRect = new SciRect(celObj.Width, celObj.Height);
        if (_useInsetRect)
        {
            if (_insetRect.Intersects(celRect))
            {
                _insetRect.Clip(celRect);
            }
            else
            {
                _insetRect = default;
            }
        }
        else
        {
            _insetRect = celRect;
        }

        var scaleX = new Ratio();
        var scaleY = new Ratio();
        if (Scale.Signal == ScaleSignals32.Manual)
        {
            scaleX = new Ratio(Scale.X, 128);
            scaleY = new Ratio(Scale.Y, 128);
        }
        else if (Scale.Signal == ScaleSignals32.VanishingPoint)
        {
            int num = Scale.Max * (Position.Y - plane.VanishingPoint.Y) / (scriptWidth - plane.VanishingPoint.Y);
            scaleX = new Ratio(num, 128);
            scaleY = new Ratio(num, 128);
        }

        if (scaleX.Numerator != 0 && scaleY.Numerator != 0)
        {
            _screenItemRect = _insetRect;

            var celToScreenX = new Ratio();
            var celToScreenY = new Ratio();
            if (_g.Version < SciVersion.V2_1Late)
            {
                celToScreenX = new Ratio(screenWidth, celObj.XResolution);
                celToScreenY = new Ratio(screenHeight, celObj.YResolution);
            }

            // the cel may use a larger coordinate system than the scripts (high resolution
            // pictures with low resolution scripts)
            if (celObj.XResolution != Sci32Resolution.LowResX || celObj.YResolution != Sci32Resolution.LowResY)
            {
                if (_useInsetRect)
                {
                    if (_g.Version < SciVersion.V2_1Late)
                    {
                        Rect32Math.MulRu(ref _screenItemRect, new Ratio(celObj.XResolution, scriptWidth), new Ratio(celObj.YResolution, scriptHeight), 0);
                    }

                    if (_screenItemRect.Intersects(celRect))
                    {
                        _screenItemRect.Clip(celRect);
                    }
                    else
                    {
                        _screenItemRect = default;
                    }
                }

                int originX = celObj.Origin.X;
                int originY = celObj.Origin.Y;
                if (MirrorX != celObj.MirrorX && CelInfo.Type != CelType.Pic)
                {
                    originX = celObj.Width - celObj.Origin.X - 1;
                }

                if (!scaleX.IsOne || !scaleY.IsOne)
                {
                    // games changed their scaling mode when scripts moved to high resolution
                    // coordinates during SCI2.1 middle
                    if (scriptWidth == Sci32Resolution.LowResX)
                    {
                        Rect32Math.MulInc(ref _screenItemRect, scaleX, scaleY);
                    }
                    else
                    {
                        _screenItemRect.Left = (_screenItemRect.Left * scaleX).ToInt();
                        _screenItemRect.Top = (_screenItemRect.Top * scaleY).ToInt();
                        _screenItemRect.Right = scaleX.Numerator > scaleX.Denominator
                            ? (_screenItemRect.Right * scaleX).ToInt()
                            : ((_screenItemRect.Right - 1) * scaleX).ToInt() + 1;
                        _screenItemRect.Bottom = scaleY.Numerator > scaleY.Denominator
                            ? (_screenItemRect.Bottom * scaleY).ToInt()
                            : ((_screenItemRect.Bottom - 1) * scaleY).ToInt() + 1;
                    }

                    originX = (originX * scaleX).ToInt();
                    originY = (originY * scaleY).ToInt();
                }

                Rect32Math.MulInc(ref _screenItemRect, celToScreenX, celToScreenY);
                originX = (originX * celToScreenX).ToInt();
                originY = (originY * celToScreenY).ToInt();

                var scriptToScreenX = new Ratio(screenWidth, scriptWidth);
                var scriptToScreenY = new Ratio(screenHeight, scriptHeight);
                ScaledPosition = new SciPoint((Position.X * scriptToScreenX).ToInt() - originX, (Position.Y * scriptToScreenY).ToInt() - originY);
                _screenItemRect.Translate(ScaledPosition.X, ScaledPosition.Y);

                if (MirrorX != celObj.MirrorX && CelInfo.Type == CelType.Pic)
                {
                    var temp = _insetRect;
                    if (!scaleX.IsOne)
                    {
                        Rect32Math.MulInc(ref temp, scaleX, new Ratio());
                    }

                    Rect32Math.MulInc(ref temp, celToScreenX, new Ratio());
                    var celObjPic = _celObj as CelObjPic ?? throw new SciVmException("Expected a CelObjPic");
                    temp.Translate((celObjPic.RelativePosition.X * scriptToScreenX).ToInt() - originX, 0);

                    int deltaX = plane.PlaneRect.Width - temp.Right - temp.Left;
                    ScaledPosition.X += deltaX;
                    _screenItemRect.Translate(deltaX, 0);
                }

                ScaledPosition.X += plane.PlaneRect.Left;
                ScaledPosition.Y += plane.PlaneRect.Top;
                _screenItemRect.Translate(plane.PlaneRect.Left, plane.PlaneRect.Top);

                RatioX = scaleX * celToScreenX;
                RatioY = scaleY * celToScreenY;
            }
            else
            {
                // low resolution coordinates
                int originX = celObj.Origin.X;
                if (MirrorX != celObj.MirrorX && CelInfo.Type != CelType.Pic)
                {
                    originX = celObj.Width - celObj.Origin.X - 1;
                }

                if (!scaleX.IsOne || !scaleY.IsOne)
                {
                    Rect32Math.MulInc(ref _screenItemRect, scaleX, scaleY);
                    // baked into Sierra's multiplication; only this path shrinks the bottom right corner
                    _screenItemRect.Right -= 1;
                    _screenItemRect.Bottom -= 1;
                }

                ScaledPosition = new SciPoint(Position.X - (originX * scaleX).ToInt(), Position.Y - (celObj.Origin.Y * scaleY).ToInt());
                _screenItemRect.Translate(ScaledPosition.X, ScaledPosition.Y);

                if (MirrorX != celObj.MirrorX && CelInfo.Type == CelType.Pic)
                {
                    var temp = _insetRect;
                    if (!scaleX.IsOne)
                    {
                        Rect32Math.MulInc(ref temp, scaleX, new Ratio());
                        temp.Right -= 1;
                    }

                    var celObjPic = _celObj as CelObjPic ?? throw new SciVmException("Expected a CelObjPic");
                    temp.Translate(celObjPic.RelativePosition.X - (originX * scaleX).ToInt(), celObjPic.RelativePosition.Y - (celObj.Origin.Y * scaleY).ToInt());

                    int deltaX = plane.GameRect.Width - temp.Right - temp.Left;
                    ScaledPosition.X += deltaX;
                    _screenItemRect.Translate(deltaX, 0);
                }

                ScaledPosition.X += plane.GameRect.Left;
                ScaledPosition.Y += plane.GameRect.Top;
                _screenItemRect.Translate(plane.GameRect.Left, plane.GameRect.Top);

                if (!celToScreenX.IsOne || !celToScreenY.IsOne)
                {
                    Rect32Math.MulRu(ref ScaledPosition, celToScreenX, celToScreenY);
                    Rect32Math.MulRu(ref _screenItemRect, celToScreenX, celToScreenY, 1);
                }

                RatioX = scaleX * celToScreenX;
                RatioY = scaleY * celToScreenY;
            }

            ScreenRect = _screenItemRect;

            // PQ4 CD creates items with invalid rectangles; only valid ones are clipped
            if (ScreenRect.Intersects(plane.ScreenRect) && ScreenRect.IsValidRect)
            {
                ScreenRect.Clip(plane.ScreenRect);
            }
            else
            {
                ScreenRect = default;
            }

            if (!FixedPriority)
            {
                Priority = (short)(_z + Position.Y);
            }
        }
        else
        {
            ScreenRect = default;
        }
    }

    /// <summary>The cel object, created on first use.</summary>
    public CelObj GetCelObj()
    {
        if (_celObj is null)
        {
            _celObj = CelInfo.Type switch
            {
                CelType.View => new CelObjView(_g, CelInfo.ResourceId, CelInfo.LoopNo, CelInfo.CelNo),
                CelType.Pic => throw new SciVmException("Internal error, pic screen item with no cel."),
                CelType.Mem => new CelObjMem(_g, CelInfo.Bitmap),
                CelType.Color => new CelObjColor(_g, CelInfo.Color, _insetRect.Width, _insetRect.Height),
                _ => throw new SciVmException($"Unknown cel type {CelInfo.Type}"),
            };
        }

        return _celObj;
    }

    /// <summary>The cel object if one exists; picture items get theirs from the plane.</summary>
    public CelObj? CelObjOrNull => _celObj;

    public void SetCelObj(CelObj celObj) => _celObj = celObj;

    /// <summary>Updates the item from its script object.</summary>
    public void Update(RegT obj)
    {
        var s = _g.State;
        var sel = s.Kernel.Selectors;
        int view = Selectors.ReadSelectorValue(s, obj, sel.View);
        short loopNo = (short)Selectors.ReadSelectorValue(s, obj, sel.Loop);
        short celNo = (short)Selectors.ReadSelectorValue(s, obj, sel.Cel);

        bool updateCel = CelInfo.ResourceId != view || CelInfo.LoopNo != loopNo || CelInfo.CelNo != celNo;
        bool updateBitmap = !Selectors.ReadSelector(s, obj, sel.Bitmap).IsNull;
        SetFromObject(obj, updateCel, updateBitmap);

        if (Created == 0)
        {
            Updated = _g.Frameout.ScreenCount;
        }

        Deleted = 0;
    }

    /// <summary>Updates an item that has no script object. Was GraphicsMgr::UpdateScreenItem.</summary>
    public void Update()
    {
        var plane = _g.Frameout.Planes.FindByObject(Plane)
            ?? throw new SciVmException($"ScreenItem::update: Invalid plane {Plane}");
        if (plane.ScreenItemList.FindByObject(Object) is null)
        {
            throw new SciVmException($"ScreenItem::update: {Object} not in plane {Plane}");
        }

        if (Created == 0)
        {
            Updated = _g.Frameout.ScreenCount;
        }

        Deleted = 0;
        _celObj = null;
    }

    /// <summary>The item's current size and position on screen in script coordinates.</summary>
    public SciRect GetNowSeenRect(Plane32 plane)
    {
        var celObj = GetCelObj();
        var celObjRect = new SciRect(celObj.Width, celObj.Height);
        SciRect nsRect;
        if (_useInsetRect)
        {
            if (_insetRect.Intersects(celObjRect))
            {
                nsRect = _insetRect;
                nsRect.Clip(celObjRect);
            }
            else
            {
                nsRect = default;
            }
        }
        else
        {
            nsRect = celObjRect;
        }

        int scriptWidth = _g.Frameout.ScriptWidth;
        int scriptHeight = _g.Frameout.ScriptHeight;

        var scaleX = new Ratio();
        var scaleY = new Ratio();
        if (Scale.Signal == ScaleSignals32.Manual)
        {
            scaleX = new Ratio(Scale.X, 128);
            scaleY = new Ratio(Scale.Y, 128);
        }
        else if (Scale.Signal == ScaleSignals32.VanishingPoint)
        {
            int num = Scale.Max * (Position.Y - plane.VanishingPoint.Y) / (scriptWidth - plane.VanishingPoint.Y);
            scaleX = new Ratio(num, 128);
            scaleY = new Ratio(num, 128);
        }

        if (scaleX.Numerator == 0 || scaleY.Numerator == 0)
        {
            return default;
        }

        int originX = celObj.Origin.X;
        int originY = celObj.Origin.Y;
        if (MirrorX != celObj.MirrorX && CelInfo.Type != CelType.Pic)
        {
            originX = celObj.Width - originX - 1;
        }

        if (celObj.XResolution != Sci32Resolution.LowResX || celObj.YResolution != Sci32Resolution.LowResY)
        {
            if (_useInsetRect)
            {
                if (_g.Version < SciVersion.V2_1Late)
                {
                    Rect32Math.MulRu(ref nsRect, new Ratio(celObj.XResolution, scriptWidth), new Ratio(celObj.YResolution, scriptHeight), 0);
                }

                if (nsRect.Intersects(celObjRect))
                {
                    nsRect.Clip(celObjRect);
                }
                else
                {
                    nsRect = default;
                }
            }

            if (!scaleX.IsOne || !scaleY.IsOne)
            {
                if (scriptWidth == Sci32Resolution.LowResX)
                {
                    Rect32Math.MulInc(ref nsRect, scaleX, scaleY);
                    nsRect.Right -= 1;
                    nsRect.Bottom -= 1;
                }
                else
                {
                    nsRect.Left = (nsRect.Left * scaleX).ToInt();
                    nsRect.Top = (nsRect.Top * scaleY).ToInt();
                    nsRect.Right = scaleX.Numerator > scaleX.Denominator ? (nsRect.Right * scaleX).ToInt() : ((nsRect.Right - 1) * scaleX).ToInt() + 1;
                    nsRect.Bottom = scaleY.Numerator > scaleY.Denominator ? (nsRect.Bottom * scaleY).ToInt() : ((nsRect.Bottom - 1) * scaleY).ToInt() + 1;
                }
            }

            var celToScriptX = new Ratio();
            var celToScriptY = new Ratio();
            if (_g.Version < SciVersion.V2_1Late)
            {
                celToScriptX = new Ratio(scriptWidth, celObj.XResolution);
                celToScriptY = new Ratio(scriptHeight, celObj.YResolution);
            }

            originX = (originX * scaleX * celToScriptX).ToInt();
            originY = (originY * scaleY * celToScriptY).ToInt();
            Rect32Math.MulInc(ref nsRect, celToScriptX, celToScriptY);
            nsRect.Translate(Position.X - originX, Position.Y - originY);
        }
        else
        {
            if (!scaleX.IsOne || !scaleY.IsOne)
            {
                Rect32Math.MulInc(ref nsRect, scaleX, scaleY);
                nsRect.Right -= 1;
                nsRect.Bottom -= 1;
            }

            originX = (originX * scaleX).ToInt();
            originY = (originY * scaleY).ToInt();
            nsRect.Translate(Position.X - originX, Position.Y - originY);

            if (MirrorX != celObj.MirrorX && CelInfo.Type != CelType.Pic)
            {
                nsRect.Translate(plane.GameRect.Width - nsRect.Width, 0);
            }
        }

        return nsRect;
    }

    public override string ToString() => $"{Object} prio {Priority} at {Position} z {_z}: {CelInfo}";
}

/// <summary>The screen items of a plane, with stable indices so the visible copy can be matched by index.</summary>
public sealed class ScreenItemList : StableList<ScreenItem>
{
    private int[] _unsorted = [];

    /// <summary>
    /// SCI3 sorts the list in place for a pass and puts it back afterwards (a bubble sort that
    /// moves erased slots to the end, remembering where each entry came from).
    /// </summary>
    public void SortInPlace()
    {
        if (Count < 2)
        {
            return;
        }

        _unsorted = new int[Count];
        for (int i = 0; i < Count; i++)
        {
            _unsorted[i] = i;
        }

        for (int i = Count - 1; i > 0; i--)
        {
            bool swap = false;
            for (int j = 0; j < i; j++)
            {
                var a = this[j];
                var b = this[j + 1];
                if (a is null || (b is not null && ScreenItem.IsGreater(a, b)))
                {
                    this[j] = b;
                    this[j + 1] = a;
                    (_unsorted[j], _unsorted[j + 1]) = (_unsorted[j + 1], _unsorted[j]);
                    swap = true;
                }
            }

            if (!swap)
            {
                break;
            }
        }
    }

    /// <summary>Undoes <see cref="SortInPlace"/>.</summary>
    public void Unsort()
    {
        if (Count < 2)
        {
            return;
        }

        for (int i = 0; i < Count; i++)
        {
            while (_unsorted[i] != i)
            {
                int k = _unsorted[i];
                (this[k], this[i]) = (this[i], this[k]);
                (_unsorted[k], _unsorted[i]) = (_unsorted[i], _unsorted[k]);
            }
        }
    }

    public ScreenItem? FindByObject(RegT obj)
    {
        for (int i = 0; i < Count; i++)
        {
            var item = this[i];
            if (item is not null && item.Object == obj)
            {
                return item;
            }
        }

        return null;
    }
}
