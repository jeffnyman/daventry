using Daventry.Core.Graphics;
using Daventry.Core.Hosting;
using Daventry.Sci.Engine;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Graphics;

/// <summary>
/// The SCI32 renderer: keeps the planes and their screen items, works out what changed since
/// the last frame, draws it into the game buffer and copies the dirty parts to the hardware
/// frame. Roughly Sierra's GraphicsMgr; port of ScummVM's GfxFrameout.
/// </summary>
public sealed class GfxFrameout
{
    private readonly Sci32Graphics _g;
    private uint _lastScreenUpdateTick;
    private byte _throttleState;
    private bool _remapOccurred;
    private readonly RectList _showList = new();
    private readonly List<DrawList> _screenItemLists = [];
    private readonly int _overdrawThreshold = 0; // Sierra merged show rectangles only when the merge added no overdraw

    public GfxFrameout(Sci32Graphics g)
    {
        _g = g;
        IsHiRes = DetectHiRes();

        if (g.GameId == SciGameId.Phantasmagoria)
        {
            CurrentBuffer = new Surface8(630, 450);
        }
        else if (IsHiRes)
        {
            CurrentBuffer = new Surface8(640, 480);
        }
        else
        {
            CurrentBuffer = new Surface8(320, 200);
        }

        Screen = new Surface8(CurrentBuffer.Width, CurrentBuffer.Height);

        switch (g.GameId)
        {
            case SciGameId.Hoyle5 when g.ResMan.TestResource(new ResourceId(ResourceType.View, 21)) is not null:
                // the Hoyle school house
                ScriptWidth = 320;
                ScriptHeight = 200;
                break;
            case SciGameId.Hoyle5:
            case SciGameId.Lighthouse:
            case SciGameId.Lsl7:
            case SciGameId.Phantasmagoria2:
            case SciGameId.Torin:
            case SciGameId.Rama:
                ScriptWidth = 640;
                ScriptHeight = 480;
                break;
            case SciGameId.Gk2 or SciGameId.Pqswat when !g.State.IsDemo:
                ScriptWidth = 640;
                ScriptHeight = 480;
                break;
            default:
                ScriptWidth = 320;
                ScriptHeight = 200;
                break;
        }
    }

    /// <summary>Whether the game renders above 320x240.</summary>
    public bool IsHiRes { get; }

    /// <summary>The resolution the scripts work in.</summary>
    public int ScriptWidth { get; }

    public int ScriptHeight { get; }

    /// <summary>The game's back buffer, drawn into by frameOut.</summary>
    public Surface8 CurrentBuffer { get; }

    /// <summary>What the hardware shows: the back buffer's dirty parts copied over, plus the cursor.</summary>
    public Surface8 Screen { get; }

    /// <summary>Set when <see cref="Screen"/> changed since the host last presented it.</summary>
    public bool ScreenDirty { get; set; }

    public int ScreenWidth => CurrentBuffer.Width;

    public int ScreenHeight => CurrentBuffer.Height;

    /// <summary>The planes rendered next frame (Sierra's GraphicsMgr.screen).</summary>
    public PlaneList Planes { get; } = new();

    /// <summary>The planes as drawn last frame (Sierra's GraphicsMgr.visibleScreen).</summary>
    public PlaneList VisiblePlanes { get; } = new();

    /// <summary>Whether kFrameOut throttles the game to 60 frames a second.</summary>
    public bool ThrottleKernelFrameOut { get; set; } = true;

    /// <summary>Set by kMorphOn: the next frame renders through the palette morph.</summary>
    public bool PalMorphIsOn { get; set; }

    /// <summary>The offset kShakeScreen displays the frame at.</summary>
    public (int X, int Y) ShakeOffset { get; set; }

    // serials for the planes and screen items the engine creates, and for creation order
    public ushort NextPlaneObjectId;
    public uint NextPlaneCreationId;
    public ushort NextScreenItemObjectId = 20000;
    public uint NextScreenItemCreationId;

    /// <summary>
    /// The value the screen items' created, updated and deleted counters start from. It is 1 in
    /// every interpreter examined (GK1 DOS, Phantasmagoria m68k, SQ6 DOS and Windows, Phantasmagoria 2).
    /// </summary>
    public int ScreenCount => 1;

    private PixelBuffer Buffer => new(CurrentBuffer);

    /// <summary>Sets up the serials and the background plane that keeps hidden planes (negative priority) from showing.</summary>
    public void Run()
    {
        _g.CelCache.Clear();
        NextPlaneObjectId = (ushort)_g.Features.DetectPlaneIdBase();
        NextPlaneCreationId = 0;
        NextScreenItemObjectId = 20000;
        NextScreenItemCreationId = 0;

        var initPlane = new Plane32(_g, new SciRect(ScriptWidth, ScriptHeight)) { Priority = 0 };
        Planes.Add(initPlane);
    }

    public void Clear()
    {
        Planes.Clear();
        VisiblePlanes.Clear();
        _showList.Clear();
        _screenItemLists.Clear();
    }

    private bool DetectHiRes()
    {
        // QFG4 is always low resolution
        if (_g.GameId == SciGameId.Qfg4)
        {
            return false;
        }

        // the PQ4 DOS floppy is low resolution only
        if (_g.GameId == SciGameId.Pq4 && _g.Platform == SciPlatform.Dos && !_g.State.IsCd)
        {
            return false;
        }

        // the GK1 DOS floppy is low resolution only, the Macintosh floppy high resolution only
        if (_g.GameId == SciGameId.Gk1 && !_g.State.IsCd && _g.Platform != SciPlatform.Macintosh)
        {
            return false;
        }

        // every other game is high resolution (ScummVM offers a toggle)
        return true;
    }

    // ---- screen items ----

    public void AddScreenItem(ScreenItem screenItem)
    {
        var plane = Planes.FindByObject(screenItem.Plane)
            ?? throw new SciVmException($"GfxFrameout::addScreenItem: Could not find plane {screenItem.Plane} for screen item {screenItem.Object}");
        plane.ScreenItemList.Add(screenItem);
    }

    public void UpdateScreenItem(ScreenItem screenItem) => screenItem.Update();

    public void DeleteScreenItem(ScreenItem screenItem)
    {
        var plane = Planes.FindByObject(screenItem.Plane)
            ?? throw new SciVmException($"GfxFrameout::deleteScreenItem: Could not find plane {screenItem.Plane} for screen item {screenItem.Object}");
        if (plane.ScreenItemList.FindByObject(screenItem.Object) is null)
        {
            throw new SciVmException($"GfxFrameout::deleteScreenItem: Screen item {screenItem.Object} not found in plane {screenItem.Plane}");
        }

        DeleteScreenItem(screenItem, plane);
    }

    public void DeleteScreenItem(ScreenItem screenItem, Plane32 plane)
    {
        if (screenItem.Created == 0)
        {
            screenItem.Created = 0;
            screenItem.Updated = 0;
            screenItem.Deleted = ScreenCount;
        }
        else
        {
            plane.ScreenItemList.Erase(screenItem);
            plane.ScreenItemList.Pack();
        }
    }

    public void DeleteScreenItem(ScreenItem screenItem, RegT planeObject)
    {
        var plane = Planes.FindByObject(planeObject)
            ?? throw new SciVmException($"GfxFrameout::deleteScreenItem: Could not find plane {planeObject} for screen item {screenItem.Object}");
        DeleteScreenItem(screenItem, plane);
    }

    public void KernelAddScreenItem(RegT obj)
    {
        var s = _g.State;
        var planeObject = Selectors.ReadSelector(s, obj, s.Kernel.Selectors.Plane);
        s.SegMan.GetObject(obj)!.SetInfoSelectorFlag(InfoFlags.ViewInserted);

        var plane = Planes.FindByObject(planeObject)
            ?? throw new SciVmException($"kAddScreenItem: Plane {planeObject} not found for screen item {obj}");
        var screenItem = plane.ScreenItemList.FindByObject(obj);
        if (screenItem is not null)
        {
            screenItem.Update(obj);
        }
        else
        {
            plane.ScreenItemList.Add(new ScreenItem(_g, obj));
        }
    }

    public void KernelUpdateScreenItem(RegT obj)
    {
        var s = _g.State;
        var sel = s.Kernel.Selectors;
        var magnifierObject = Selectors.ReadSelector(s, obj, sel.Magnifier);
        if (!magnifierObject.IsNull)
        {
            throw new SciVmException("Magnifier view is not known to be used by any game");
        }

        var planeObject = Selectors.ReadSelector(s, obj, sel.Plane);
        var plane = Planes.FindByObject(planeObject);
        if (plane is null)
        {
            // a script bug in PQ:SWAT when skipping the tactics training
            _g.Log.Warning("sci.gfx", $"kUpdateScreenItem: Plane {planeObject} not found for screen item {obj}");
            return;
        }

        var screenItem = plane.ScreenItemList.FindByObject(obj)
            ?? throw new SciVmException($"kUpdateScreenItem: Screen item {obj} not found in plane {planeObject}");
        screenItem.Update(obj);
    }

    public void KernelDeleteScreenItem(RegT obj)
    {
        var s = _g.State;
        s.SegMan.GetObject(obj)!.ClearInfoSelectorFlag(InfoFlags.ViewInserted);
        var planeObject = Selectors.ReadSelector(s, obj, s.Kernel.Selectors.Plane);
        var plane = Planes.FindByObject(planeObject);
        if (plane is null)
        {
            return;
        }

        var screenItem = plane.ScreenItemList.FindByObject(obj);
        if (screenItem is null)
        {
            return;
        }

        DeleteScreenItem(screenItem, plane);
    }

    // ---- planes ----

    public void KernelAddPlane(RegT obj)
    {
        var plane = Planes.FindByObject(obj);
        if (plane is not null)
        {
            plane.Update(obj);
            UpdatePlane(plane);
        }
        else
        {
            AddPlane(new Plane32(_g, obj));
        }

        // QFG4's character import dialog: Sierra's directory browsing cannot work here, so the
        // change-directory button is disabled (SCI16 games get this in kDrawControl)
        if (_g.State.InQfgImportRoom)
        {
            var s = _g.State;
            var sel = s.Kernel.Selectors;
            foreach (var changeButton in s.SegMan.FindObjectsByName("changeButton"))
            {
                if (Selectors.ReadSelectorValue(s, changeButton, sel.Value) != 0 && Selectors.ReadSelectorValue(s, changeButton, sel.State) != 0)
                {
                    Selectors.WriteSelectorValue(s, changeButton, sel.State, 0);
                    _g.Log.Info("sci.gfx", "Character import: put the saved character files in the game's save folder; the directory button is disabled");
                    break;
                }
            }
        }
    }

    public void KernelUpdatePlane(RegT obj)
    {
        var plane = Planes.FindByObject(obj) ?? throw new SciVmException($"kUpdatePlane: Plane {obj} not found");
        plane.Update(obj);
        UpdatePlane(plane);
    }

    public void KernelDeletePlane(RegT obj)
    {
        var plane = Planes.FindByObject(obj) ?? throw new SciVmException($"kDeletePlane: Plane {obj} not found");
        if (plane.Created != 0)
        {
            Planes.Erase(plane);
        }
        else
        {
            plane.Created = 0;
            plane.Deleted = ScreenCount;
        }
    }

    public void DeletePlane(Plane32 planeToFind)
    {
        var plane = Planes.FindByObject(planeToFind.Object) ?? throw new SciVmException($"deletePlane: Plane {planeToFind.Object} not found");
        if (plane.Created != 0)
        {
            Planes.Erase(plane);
        }
        else
        {
            plane.Created = 0;
            plane.Moved = 0;
            plane.Deleted = ScreenCount;
        }
    }

    public void KernelMovePlaneItems(RegT obj, int deltaX, int deltaY, bool scrollPics)
    {
        var plane = Planes.FindByObject(obj) ?? throw new SciVmException($"kMovePlaneItems: Plane {obj} not found");
        plane.ScrollScreenItems(deltaX, deltaY, scrollPics);

        var s = _g.State;
        var sel = s.Kernel.Selectors;
        foreach (var screenItem in plane.ScreenItemList.Items)
        {
            // items with numeric objects came from the engine, not a script
            if (screenItem.Object.IsNumber)
            {
                continue;
            }

            if (deltaX != 0)
            {
                Selectors.WriteSelectorValue(s, screenItem.Object, sel.X, (short)Selectors.ReadSelectorValue(s, screenItem.Object, sel.X) + deltaX);
            }

            if (deltaY != 0)
            {
                Selectors.WriteSelectorValue(s, screenItem.Object, sel.Y, (short)Selectors.ReadSelectorValue(s, screenItem.Object, sel.Y) + deltaY);
            }
        }
    }

    public short KernelGetHighPlanePri() => Planes.GetTopSciPlanePriority();

    public void AddPlane(Plane32 plane)
    {
        if (Planes.FindByObject(plane.Object) is not null)
        {
            throw new SciVmException($"Plane {plane.Object} already exists");
        }

        plane.ClipScreenRect(new SciRect(CurrentBuffer.Width, CurrentBuffer.Height));
        Planes.Add(plane);
    }

    private void UpdatePlane(Plane32 plane)
    {
        var visiblePlane = VisiblePlanes.FindByObject(plane.Object);
        plane.Sync(visiblePlane, new SciRect(CurrentBuffer.Width, CurrentBuffer.Height));
        Planes.Sort();
    }

    // ---- pictures ----

    public void KernelAddPicAt(RegT planeObject, int pictureId, int x, int y, bool mirrorX, bool deleteDuplicate)
    {
        var plane = Planes.FindByObject(planeObject) ?? throw new SciVmException($"kAddPicAt: Plane {planeObject} not found");
        plane.AddPic(pictureId, new SciPoint(x, y), mirrorX, deleteDuplicate);
    }

    // ---- rendering ----

    /// <summary>Renders the next frame into the game buffer and, when asked, to the hardware. An erase rectangle is added to this frame's erase list.</summary>
    public void FrameOut(bool shouldShowBits, SciRect eraseRect = default)
    {
        var robotPlayer = _g.Video?.RobotPlayer;
        bool robotIsActive = robotPlayer is not null && robotPlayer.Status != RobotStatus.Uninitialized;
        if (robotIsActive)
        {
            robotPlayer!.DoRobot();
        }

        // Sierra kept 100 static lists; these are reused between frames
        while (_screenItemLists.Count < Planes.Count)
        {
            _screenItemLists.Add(new DrawList());
        }

        while (_screenItemLists.Count > Planes.Count)
        {
            _screenItemLists.RemoveAt(_screenItemLists.Count - 1);
        }

        foreach (var list in _screenItemLists)
        {
            list.Clear();
        }

        var eraseLists = new List<RectList>(Planes.Count);
        for (int i = 0; i < Planes.Count; i++)
        {
            eraseLists.Add(new RectList());
        }

        if (_g.Remap.RemapCount > 0 && _remapOccurred)
        {
            RemapMarkRedraw();
        }

        CalcLists(_screenItemLists, eraseLists, eraseRect);
        foreach (var list in _screenItemLists)
        {
            list.SortByScreenItem();
        }

        foreach (var list in _screenItemLists)
        {
            foreach (var drawItem in list.Items)
            {
                drawItem.ScreenItem.GetCelObj().SubmitPalette();
            }
        }

        _remapOccurred = _g.Palette.UpdateForFrame();

        for (int i = 0; i < Planes.Count; i++)
        {
            DrawEraseList(eraseLists[i], Planes[i]);
            DrawScreenItemList(_screenItemLists[i]);
        }

        if (robotIsActive)
        {
            robotPlayer!.FrameAlmostVisible();
        }

        _g.Palette.UpdateHardware();
        if (shouldShowBits)
        {
            ShowBits();
        }

        if (robotIsActive)
        {
            robotPlayer!.FrameNowVisible();
        }
    }

    /// <summary>Renders a frame through the palette morph used by SQ6's datacorder puzzle and the morph show style.</summary>
    public async ValueTask PalMorphFrameOut(sbyte[] styleRanges, PlaneShowStyle? showStyle = null)
    {
        var sourcePalette = _g.Palette.NextPalette.Clone();
        AlterVmap(sourcePalette, sourcePalette, -1, styleRanges);

        int prevRoom = _g.State.GetGlobal(GlobalVar.PreviousRoomNo).ToInt16();
        _showList.Add(new SciRect(CurrentBuffer.Width, CurrentBuffer.Height));
        ShowBits();

        var screenItemLists = new List<DrawList>();
        var eraseLists = new List<RectList>();
        for (int i = 0; i < Planes.Count; i++)
        {
            screenItemLists.Add(new DrawList());
            eraseLists.Add(new RectList());
        }

        if (_g.Remap.RemapCount > 0 && _remapOccurred)
        {
            RemapMarkRedraw();
        }

        CalcLists(screenItemLists, eraseLists);
        foreach (var list in screenItemLists)
        {
            list.SortByScreenItem();
        }

        foreach (var list in screenItemLists)
        {
            foreach (var drawItem in list.Items)
            {
                drawItem.ScreenItem.GetCelObj().SubmitPalette();
            }
        }

        _remapOccurred = _g.Palette.UpdateForFrame();
        for (int i = 0; i < Planes.Count; i++)
        {
            DrawEraseList(eraseLists[i], Planes[i]);
            DrawScreenItemList(screenItemLists[i]);
        }

        var nextPalette = _g.Palette.NextPalette.Clone();
        for (int i = 0; i < 256; i++)
        {
            bool copy = prevRoom < 1000
                ? styleRanges[i] == -1 || styleRanges[i] == 0
                : styleRanges[i] == -1 || ValidZeroStyle(styleRanges[i], i);
            if (copy)
            {
                var c = nextPalette.Colors[i];
                c.Used = 1;
                sourcePalette.Colors[i] = c;
            }
        }

        _g.Palette.Submit(sourcePalette);
        _g.Palette.UpdateFFrame();
        _g.Palette.UpdateHardware();
        AlterVmap(nextPalette, sourcePalette, 1, _g.Transitions.StyleRanges);

        if (showStyle is not null && showStyle.Type != ShowStyleType.Morph)
        {
            await _g.Transitions.ProcessEffects(showStyle);
        }
        else
        {
            ShowBits();
        }

        foreach (var plane in Planes.Items)
        {
            plane.RedrawAllCount = ScreenCount;
        }

        if (_g.Remap.RemapCount > 0 && _remapOccurred)
        {
            RemapMarkRedraw();
        }

        screenItemLists.Clear();
        eraseLists.Clear();
        for (int i = 0; i < Planes.Count; i++)
        {
            screenItemLists.Add(new DrawList());
            eraseLists.Add(new RectList());
        }

        CalcLists(screenItemLists, eraseLists);
        foreach (var list in screenItemLists)
        {
            list.SortByScreenItem();
        }

        foreach (var list in screenItemLists)
        {
            foreach (var drawItem in list.Items)
            {
                drawItem.ScreenItem.GetCelObj().SubmitPalette();
            }
        }

        _remapOccurred = _g.Palette.UpdateForFrame();
        for (int i = 0; i < Planes.Count; i++)
        {
            DrawEraseList(eraseLists[i], Planes[i]);
            DrawScreenItemList(screenItemLists[i]);
        }

        _g.Palette.Submit(nextPalette);
        _g.Palette.UpdateFFrame();
        _g.Palette.UpdateHardware();
        ShowBits();
    }

    /// <summary>Whether a palette entry of style 0 takes the new colour during a morph; a few games keep most entries.</summary>
    private bool ValidZeroStyle(sbyte style, int i)
    {
        if (style != 0)
        {
            return false;
        }

        return _g.GameId switch
        {
            SciGameId.Kq7 or SciGameId.Phantasmagoria or SciGameId.Sq6 => i > 71 && i < 104,
            _ => true,
        };
    }

    /// <summary>Copies a rectangle of the game buffer to the hardware with nothing else drawn.</summary>
    public void DirectFrameOut(SciRect showRect)
    {
        _showList.Add(showRect);
        ShowBits();
    }

    /// <summary>Sends the whole game buffer to the hardware.</summary>
    public void ResetHardware()
    {
        _showList.Add(new SciRect(CurrentBuffer.Width, CurrentBuffer.Height));
        ShowBits();
    }

    /// <summary>Finds the dirty areas of every plane for the next frame.</summary>
    private void CalcLists(List<DrawList> drawLists, List<RectList> eraseLists, SciRect eraseRect = default)
    {
        var eraseList = new RectList();
        Span<SciRect> outRects = stackalloc SciRect[4];
        int deletedPlaneCount = 0;
        bool addedToEraseList = false;
        bool foundTransparentPlane = false;

        if (!eraseRect.IsEmpty)
        {
            addedToEraseList = true;
            eraseList.Add(eraseRect);
        }

        int planeCount = Planes.Count;
        for (int outerPlaneIndex = 0; outerPlaneIndex < planeCount; outerPlaneIndex++)
        {
            var outerPlane = Planes[outerPlaneIndex];
            var visiblePlane = VisiblePlanes.FindByObject(outerPlane.Object);

            // only the transparent type counts here, not transparent pictures, as in Sierra's interpreter
            if (outerPlane.Type == PlaneType.Transparent)
            {
                foundTransparentPlane = true;
            }

            if (outerPlane.Deleted != 0)
            {
                if (visiblePlane is not null && !visiblePlane.ScreenRect.IsEmpty)
                {
                    eraseList.Add(visiblePlane.ScreenRect);
                    addedToEraseList = true;
                }

                deletedPlaneCount++;
            }
            else if (visiblePlane is not null && outerPlane.Moved != 0)
            {
                // the moved counter is decremented in the final loop
                int splitCount = Rect32Math.SplitRects(visiblePlane.ScreenRect, outerPlane.ScreenRect, outRects);
                if (splitCount != 0)
                {
                    if (splitCount == -1 && !visiblePlane.ScreenRect.IsEmpty)
                    {
                        eraseList.Add(visiblePlane.ScreenRect);
                    }
                    else
                    {
                        for (int i = 0; i < splitCount; i++)
                        {
                            eraseList.Add(outRects[i]);
                        }
                    }

                    addedToEraseList = true;
                }

                if (outerPlane.RedrawAllCount == 0)
                {
                    splitCount = Rect32Math.SplitRects(outerPlane.ScreenRect, visiblePlane.ScreenRect, outRects);
                    if (splitCount != 0)
                    {
                        for (int i = 0; i < splitCount; i++)
                        {
                            eraseList.Add(outRects[i]);
                        }

                        addedToEraseList = true;
                    }
                }
            }

            if (addedToEraseList)
            {
                for (int rectIndex = 0; rectIndex < eraseList.Count; rectIndex++)
                {
                    if (eraseList.IsErased(rectIndex))
                    {
                        continue;
                    }

                    var rect = eraseList[rectIndex];
                    for (int innerPlaneIndex = planeCount - 1; innerPlaneIndex >= 0; innerPlaneIndex--)
                    {
                        var innerPlane = Planes[innerPlaneIndex];
                        if (innerPlane.Deleted == 0 && innerPlane.Type != PlaneType.Transparent && innerPlane.ScreenRect.Intersects(rect))
                        {
                            if (innerPlane.RedrawAllCount == 0)
                            {
                                eraseLists[innerPlaneIndex].Add(innerPlane.ScreenRect.FindIntersectingRect(rect));
                            }

                            int splitCount = Rect32Math.SplitRects(rect, innerPlane.ScreenRect, outRects);
                            for (int i = 0; i < splitCount; i++)
                            {
                                eraseList.Add(outRects[i]);
                            }

                            eraseList.EraseAt(rectIndex);
                            break;
                        }
                    }
                }

                eraseList.Pack();
            }
        }

        if (deletedPlaneCount != 0)
        {
            for (int planeIndex = planeCount - 1; planeIndex >= 0; planeIndex--)
            {
                var plane = Planes[planeIndex];
                if (plane.Deleted != 0)
                {
                    plane.Deleted--;
                    if (plane.Deleted <= 0)
                    {
                        int visiblePlaneIndex = VisiblePlanes.FindIndexByObject(plane.Object);
                        if (visiblePlaneIndex != -1)
                        {
                            VisiblePlanes.RemoveAt(visiblePlaneIndex);
                        }

                        Planes.RemoveAt(planeIndex);
                        eraseLists.RemoveAt(planeIndex);
                        drawLists.RemoveAt(planeIndex);
                    }

                    if (--deletedPlaneCount <= 0)
                    {
                        break;
                    }
                }
            }
        }

        // planes may have gone
        planeCount = Planes.Count;

        for (int outerIndex = 0; outerIndex < planeCount; outerIndex++)
        {
            var outerPlane = Planes[outerIndex];
            if (outerPlane.PriorityChanged == 0)
            {
                continue;
            }

            outerPlane.PriorityChanged--;
            var visibleOuterPlane = VisiblePlanes.FindByObject(outerPlane.Object);
            if (visibleOuterPlane is null)
            {
                _g.Log.Warning("sci.gfx", $"calcLists could not find visible plane for {outerPlane.Object}");
                continue;
            }

            eraseList.Add(outerPlane.ScreenRect.FindIntersectingRect(visibleOuterPlane.ScreenRect));

            for (int innerIndex = planeCount - 1; innerIndex >= 0; innerIndex--)
            {
                var innerPlane = Planes[innerIndex];
                var visibleInnerPlane = VisiblePlanes.FindByObject(innerPlane.Object);
                int rectCount = eraseList.Count;
                for (int rectIndex = 0; rectIndex < rectCount; rectIndex++)
                {
                    if (eraseList.IsErased(rectIndex))
                    {
                        continue;
                    }

                    int splitCount = Rect32Math.SplitRects(eraseList[rectIndex], innerPlane.ScreenRect, outRects);
                    if (splitCount == 0)
                    {
                        if (visibleInnerPlane is not null)
                        {
                            // the same priority, or the order of the two planes changed
                            if ((visibleOuterPlane.Priority - visibleInnerPlane.Priority) * (outerPlane.Priority - innerPlane.Priority) <= 0)
                            {
                                if (outerPlane.Priority <= innerPlane.Priority)
                                {
                                    eraseLists[innerIndex].Add(eraseList[rectIndex]);
                                }
                                else
                                {
                                    eraseLists[outerIndex].Add(eraseList[rectIndex]);
                                }
                            }
                        }

                        eraseList.EraseAt(rectIndex);
                    }
                    else if (splitCount != -1)
                    {
                        for (int i = 0; i < splitCount; i++)
                        {
                            eraseList.Add(outRects[i]);
                        }

                        if (visibleInnerPlane is not null)
                        {
                            if ((visibleOuterPlane.Priority - visibleInnerPlane.Priority) * (outerPlane.Priority - innerPlane.Priority) <= 0)
                            {
                                eraseList[rectIndex] = outerPlane.ScreenRect.FindIntersectingRect(innerPlane.ScreenRect);
                                if (outerPlane.Priority <= innerPlane.Priority)
                                {
                                    eraseLists[innerIndex].Add(eraseList[rectIndex]);
                                }
                                else
                                {
                                    eraseLists[outerIndex].Add(eraseList[rectIndex]);
                                }
                            }
                        }

                        eraseList.EraseAt(rectIndex);
                    }
                }

                eraseList.Pack();
            }
        }

        for (int planeIndex = 0; planeIndex < planeCount; planeIndex++)
        {
            var plane = Planes[planeIndex];
            var visiblePlane = VisiblePlanes.FindByObject(plane.Object);

            if (!plane.ScreenRect.IsEmpty)
            {
                if (plane.RedrawAllCount != 0)
                {
                    plane.RedrawAll(visiblePlane, Planes, drawLists[planeIndex], eraseLists[planeIndex]);
                }
                else
                {
                    if (visiblePlane is null)
                    {
                        throw new SciVmException($"Missing visible plane for source plane {plane.Object}");
                    }

                    plane.CalcLists(visiblePlane, Planes, drawLists[planeIndex], eraseLists[planeIndex]);
                }
            }
            else
            {
                plane.DecrementScreenItemArrayCounts(visiblePlane, false);
            }

            if (plane.Moved != 0)
            {
                // the work for moved planes was done above; this only counts down
                plane.Moved--;
            }

            if (plane.Created != 0)
            {
                VisiblePlanes.Add(new Plane32(plane));
                plane.Created--;
            }
            else if (plane.Updated != 0)
            {
                if (visiblePlane is null)
                {
                    throw new SciVmException("[GfxFrameout::calcLists]: Attempt to update nonexistent visible plane");
                }

                visiblePlane.Assign(plane);
                plane.Updated--;
            }
        }

        // only the transparent type, not transparent pictures, as in Sierra's interpreter
        if (foundTransparentPlane)
        {
            for (int planeIndex = 0; planeIndex < planeCount; planeIndex++)
            {
                for (int i = planeIndex + 1; i < planeCount; i++)
                {
                    if (Planes[i].Type == PlaneType.Transparent)
                    {
                        Planes[i].FilterUpEraseRects(drawLists[i], eraseLists[planeIndex]);
                    }
                }

                if (Planes[planeIndex].Type == PlaneType.Transparent)
                {
                    for (int i = planeIndex - 1; i >= 0; i--)
                    {
                        Planes[i].FilterDownEraseRects(drawLists[i], eraseLists[i], eraseLists[planeIndex]);
                    }

                    if (eraseLists[planeIndex].Count > 0)
                    {
                        throw new SciVmException("Transparent plane's erase list not absorbed");
                    }
                }

                for (int i = planeIndex + 1; i < planeCount; i++)
                {
                    if (Planes[i].Type == PlaneType.Transparent)
                    {
                        Planes[i].FilterUpDrawRects(drawLists[i], drawLists[planeIndex]);
                    }
                }
            }
        }
    }

    /// <summary>Fills the erase rectangles of a coloured plane with its colour; other plane types redraw from pixels.</summary>
    private void DrawEraseList(RectList eraseList, Plane32 plane)
    {
        if (plane.Type != PlaneType.Colored)
        {
            return;
        }

        var buffer = Buffer;
        for (int i = 0; i < eraseList.Count; i++)
        {
            if (eraseList.IsErased(i))
            {
                continue;
            }

            MergeToShowList(eraseList[i], _showList, _overdrawThreshold);
            buffer.FillRect(eraseList[i], plane.Back);
        }
    }

    private void DrawScreenItemList(DrawList screenItemList)
    {
        var buffer = Buffer;
        foreach (var drawItem in screenItemList.Items)
        {
            MergeToShowList(drawItem.Rect, _showList, _overdrawThreshold);
            var screenItem = drawItem.ScreenItem;
            var celObj = screenItem.GetCelObj();
            celObj.Draw(buffer, screenItem, drawItem.Rect, screenItem.MirrorX ^ celObj.MirrorX);
        }
    }

    /// <summary>Adds a rectangle to the areas copied to the hardware, merging with neighbours when the overdraw is small.</summary>
    private static void MergeToShowList(SciRect drawRect, RectList showList, int overdrawThreshold)
    {
        var mergeList = new RectList();
        mergeList.Add(drawRect);
        Span<SciRect> outRects = stackalloc SciRect[2];

        for (int i = 0; i < mergeList.Count; i++)
        {
            if (mergeList.IsErased(i))
            {
                continue;
            }

            var r1 = mergeList[i];
            if (r1.IsEmpty)
            {
                continue;
            }

            bool didMerge = false;
            for (int j = 0; j < showList.Count; j++)
            {
                if (showList.IsErased(j))
                {
                    continue;
                }

                var r2 = showList[j];
                if (r2.IsEmpty)
                {
                    continue;
                }

                var merged = r1;
                merged.Extend(r2);
                int difference = merged.Width * merged.Height;
                difference -= r1.Width * r1.Height;
                difference -= r2.Width * r2.Height;
                if (r1.Intersects(r2))
                {
                    var overlap = r1.FindIntersectingRect(r2);
                    difference += overlap.Width * overlap.Height;
                }

                if (difference <= overdrawThreshold)
                {
                    mergeList.EraseAt(i);
                    showList.EraseAt(j);
                    mergeList.Add(merged);
                    didMerge = true;
                    break;
                }

                var middle = mergeList[i];
                int splitCount = Rect32Math.SplitRectsForRender(ref middle, showList[j], outRects);
                if (splitCount != -1)
                {
                    mergeList.Add(middle);
                    mergeList.EraseAt(i);
                    showList.EraseAt(j);
                    didMerge = true;
                    while (splitCount-- > 0)
                    {
                        mergeList.Add(outRects[splitCount]);
                    }

                    break;
                }
            }

            if (didMerge)
            {
                showList.Pack();
            }
        }

        mergeList.Pack();
        for (int i = 0; i < mergeList.Count; i++)
        {
            showList.Add(mergeList[i]);
        }
    }

    /// <summary>Empties the list of rectangles the next <see cref="ShowBits"/> copies; the transitions build their own.</summary>
    internal void ClearShowList() => _showList.Clear();

    /// <summary>Adds a rectangle for the next <see cref="ShowBits"/> to copy.</summary>
    internal void AddShowRect(SciRect rect) => _showList.Add(rect);

    /// <summary>Copies the dirty rectangles from the game buffer to the hardware frame, with the cursor over them.</summary>
    internal void ShowBits()
    {
        if (_showList.Count == 0)
        {
            UpdateScreen();
            return;
        }

        for (int i = 0; i < _showList.Count; i++)
        {
            if (!_showList.IsErased(i))
            {
                _g.Cursor.GonnaPaint(RoundShowRect(_showList[i]));
            }
        }

        _g.Cursor.PaintStarting();

        for (int i = 0; i < _showList.Count; i++)
        {
            if (_showList.IsErased(i))
            {
                continue;
            }

            var rounded = RoundShowRect(_showList[i]);

            // SCI2.1 early transitions (PQ4's asteroids) produce zero sized rectangles, which copy nothing
            if (rounded.Width == 0 || rounded.Height == 0)
            {
                continue;
            }

            rounded.Clip(new SciRect(CurrentBuffer.Width, CurrentBuffer.Height));
            for (int y = rounded.Top; y < rounded.Bottom; y++)
            {
                Array.Copy(CurrentBuffer.Pixels, y * CurrentBuffer.Width + rounded.Left, Screen.Pixels, y * Screen.Width + rounded.Left, rounded.Width);
            }
        }

        _g.Cursor.DonePainting();
        _showList.Clear();
        ScreenDirty = true;
        UpdateScreen();
    }

    /// <summary>Sierra's inclusive rectangles kept copied widths even; the same rounding keeps the output alike.</summary>
    private static SciRect RoundShowRect(SciRect rect)
    {
        rect.Left &= ~1;
        rect.Right = (rect.Right + 1) & ~1;
        return rect;
    }

    /// <summary>Recolours the game buffer for a palette morph: pixels in the given style range take the nearest colour outside it.</summary>
    private void AlterVmap(SciPalette palette1, SciPalette palette2, sbyte style, sbyte[] styleRanges)
    {
        var clut = new byte[256];
        for (int paletteIndex = 0; paletteIndex < 256; paletteIndex++)
        {
            int outerR = palette1.Colors[paletteIndex].R;
            int outerG = palette1.Colors[paletteIndex].G;
            int outerB = palette1.Colors[paletteIndex].B;

            if (styleRanges[paletteIndex] == style)
            {
                int minDiff = 262140;
                int minDiffIndex = paletteIndex;
                for (int i = 0; i < 236; i++)
                {
                    if (styleRanges[i] != style)
                    {
                        int r = palette1.Colors[i].R;
                        int g = palette1.Colors[i].G;
                        int b = palette1.Colors[i].B;
                        int diffSquared = (outerR - r) * (outerR - r) + (outerG - g) * (outerG - g) + (outerB - b) * (outerB - b);
                        if (diffSquared < minDiff)
                        {
                            minDiff = diffSquared;
                            minDiffIndex = i;
                        }
                    }
                }

                clut[paletteIndex] = (byte)minDiffIndex;
            }

            if (style == 1 && styleRanges[paletteIndex] == 0)
            {
                int minDiff = 262140;
                int minDiffIndex = paletteIndex;
                for (int i = 0; i < 236; i++)
                {
                    int r = palette2.Colors[i].R;
                    int g = palette2.Colors[i].G;
                    int b = palette2.Colors[i].B;
                    int diffSquared = (outerR - r) * (outerR - r) + (outerG - g) * (outerG - g) + (outerB - b) * (outerB - b);
                    if (diffSquared < minDiff)
                    {
                        minDiff = diffSquared;
                        minDiffIndex = i;
                    }
                }

                clut[paletteIndex] = (byte)minDiffIndex;
            }
        }

        var pixels = CurrentBuffer.Pixels;
        for (int pixelIndex = 0; pixelIndex < pixels.Length; pixelIndex++)
        {
            byte currentValue = pixels[pixelIndex];
            sbyte styleRangeValue = styleRanges[currentValue];
            if (styleRangeValue == -1 && styleRangeValue == style)
            {
                currentValue = pixels[pixelIndex] = clut[currentValue];
                styleRangeValue = styleRanges[currentValue];
            }

            if ((styleRangeValue == 1 && styleRangeValue == style) || (styleRangeValue == 0 && style == 1))
            {
                pixels[pixelIndex] = clut[currentValue];
            }
        }
    }

    /// <summary>Marks the hardware frame for presenting, at most once per tick plus <paramref name="delta"/>.</summary>
    public void UpdateScreen(int delta = 0)
    {
        uint now = _g.TickCount;
        if (now <= _lastScreenUpdateTick + delta)
        {
            return;
        }

        _lastScreenUpdateTick = now;
        ScreenDirty = true;
    }

    /// <summary>
    /// kFrameOut: runs the pending show styles, the palette morph or a plain frame. Returns the
    /// milliseconds the caller should throttle the scripts by, or 0.
    /// </summary>
    public async ValueTask<int> KernelFrameOut(bool shouldShowBits)
    {
        if (_g.Transitions.HasShowStyles)
        {
            await _g.Transitions.ProcessShowStyles();
        }
        else if (PalMorphIsOn)
        {
            await PalMorphFrameOut(_g.Transitions.StyleRanges);
            PalMorphIsOn = false;
        }
        else
        {
            if (_g.Transitions.HasScrolls)
            {
                await _g.Transitions.ProcessScrolls();
            }

            FrameOut(shouldShowBits);
        }

        return ThrottleKernelFrameOut ? Throttle() : 0;
    }

    /// <summary>The delay that keeps the game at 60 frames a second: 17, 17, 16 milliseconds in turn.</summary>
    public int Throttle()
    {
        int throttleTime;
        if (_throttleState == 2)
        {
            throttleTime = 16;
            _throttleState = 0;
        }
        else
        {
            throttleTime = 17;
            _throttleState++;
        }

        return throttleTime;
    }

    // ---- the mouse ----

    public RegT KernelIsOnMe(RegT obj, SciPoint position, bool checkPixel)
    {
        var s = _g.State;
        var planeObject = Selectors.ReadSelector(s, obj, s.Kernel.Selectors.Plane);
        var plane = VisiblePlanes.FindByObject(planeObject);
        if (plane is null)
        {
            return RegT.Null;
        }

        var screenItem = plane.ScreenItemList.FindByObject(obj);
        if (screenItem is null)
        {
            return RegT.Null;
        }

        // Sierra passed a copy of the item because the visible items kept stale cel objects; the
        // visible copy here drops its cel object when its cel changes
        return RegT.Make(0, IsOnMe(screenItem, plane, position, checkPixel) ? 1 : 0);
    }

    private bool IsOnMe(ScreenItem screenItem, Plane32 plane, SciPoint position, bool checkPixel)
    {
        var scaledPosition = position;
        Rect32Math.MulRu(ref scaledPosition, new Ratio(CurrentBuffer.Width, ScriptWidth), new Ratio(CurrentBuffer.Height, ScriptHeight));
        scaledPosition.X += plane.PlaneRect.Left;
        scaledPosition.Y += plane.PlaneRect.Top;

        if (!screenItem.ScreenRect.Contains(scaledPosition.X, scaledPosition.Y))
        {
            return false;
        }

        if (checkPixel)
        {
            var celObj = screenItem.GetCelObj();
            bool mirrorX = screenItem.MirrorX ^ celObj.MirrorX;
            scaledPosition.X -= screenItem.ScaledPosition.X;
            scaledPosition.Y -= screenItem.ScaledPosition.Y;

            if (_g.Version < SciVersion.V2_1Late)
            {
                Rect32Math.MulRu(ref scaledPosition, new Ratio(celObj.XResolution, CurrentBuffer.Width), new Ratio(celObj.YResolution, CurrentBuffer.Height));
            }

            if (screenItem.Scale.Signal != ScaleSignals32.None && screenItem.Scale.X != 0 && screenItem.Scale.Y != 0)
            {
                scaledPosition.X = scaledPosition.X * 128 / screenItem.Scale.X;
                scaledPosition.Y = scaledPosition.Y * 128 / screenItem.Scale.Y;
            }

            // a click at the very bottom edge of a scaled cel can land one pixel outside it
            if (scaledPosition.X < 0 || scaledPosition.Y < 0 || scaledPosition.X >= celObj.Width || scaledPosition.Y >= celObj.Height)
            {
                return false;
            }

            byte pixel = celObj.ReadPixel(scaledPosition.X, scaledPosition.Y, mirrorX);
            return pixel != celObj.SkipColor;
        }

        return true;
    }

    private bool GetNowSeenRect(RegT screenItemObject, out SciRect result)
    {
        var s = _g.State;
        var planeObject = Selectors.ReadSelector(s, screenItemObject, s.Kernel.Selectors.Plane);
        var plane = Planes.FindByObject(planeObject)
            ?? throw new SciVmException($"getNowSeenRect: Plane {planeObject} not found for screen item {screenItemObject}");
        var screenItem = plane.ScreenItemList.FindByObject(screenItemObject);
        if (screenItem is null)
        {
            // Mother Goose Deluxe is assumed to use the older behaviour, as it came before SQ6
            if (_g.Version <= SciVersion.V2_1Early || _g.GameId is SciGameId.Sq6 or SciGameId.Mothergoosehires)
            {
                throw new SciVmException($"getNowSeenRect: Unable to find screen item {screenItemObject}");
            }

            _g.Log.Warning("sci.gfx", $"getNowSeenRect: Unable to find screen item {screenItemObject}");
            result = default;
            return false;
        }

        result = screenItem.GetNowSeenRect(plane);
        return true;
    }

    public bool KernelSetNowSeen(RegT screenItemObject)
    {
        if (!GetNowSeenRect(screenItemObject, out var nsrect))
        {
            return false;
        }

        var s = _g.State;
        var sel = s.Kernel.Selectors;
        if (_g.Features.UsesAlternateSelectors)
        {
            Selectors.WriteSelectorValue(s, screenItemObject, sel.Left, nsrect.Left);
            Selectors.WriteSelectorValue(s, screenItemObject, sel.Top, nsrect.Top);
            Selectors.WriteSelectorValue(s, screenItemObject, sel.Right, nsrect.Right - 1);
            Selectors.WriteSelectorValue(s, screenItemObject, sel.Bottom, nsrect.Bottom - 1);
        }
        else
        {
            Selectors.WriteSelectorValue(s, screenItemObject, sel.NsLeft, nsrect.Left);
            Selectors.WriteSelectorValue(s, screenItemObject, sel.NsTop, nsrect.Top);
            Selectors.WriteSelectorValue(s, screenItemObject, sel.NsRight, nsrect.Right - 1);
            Selectors.WriteSelectorValue(s, screenItemObject, sel.NsBottom, nsrect.Bottom - 1);
        }

        return true;
    }

    public short KernelObjectIntersect(RegT object1, RegT object2)
    {
        bool found1 = GetNowSeenRect(object1, out var nsrect1);
        bool found2 = GetNowSeenRect(object2, out var nsrect2);

        // Sierra's uninitialised rectangle sat at 0x89ABCDEF and intersected nothing; a missing
        // object therefore gives no intersection
        if (!found1 && !found2)
        {
            _g.Log.Warning("sci.gfx", "Both objects not found in kObjectIntersect");
        }

        if (!found1 || !found2)
        {
            return 0;
        }

        var intersection = nsrect1.FindIntersectingRect(nsrect2);
        return (short)(intersection.Width * intersection.Height);
    }

    private void RemapMarkRedraw()
    {
        foreach (var plane in Planes.Items)
        {
            plane.RemapMarkRedraw();
        }
    }

    /// <summary>The topmost visible picture plane, for debugging.</summary>
    public Plane32? GetTopVisiblePlane()
    {
        foreach (var plane in VisiblePlanes.Items)
        {
            if (plane.Type == PlaneType.Picture)
            {
                return plane;
            }
        }

        return null;
    }
}
