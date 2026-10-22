using Daventry.Sci.Engine;

namespace Daventry.Sci.Graphics;

public enum PlaneType
{
    Colored = 0,
    Picture = 1,
    Transparent = 2,
    Opaque = 3,
    TransparentPicture = 4,
}

/// <summary>A screen item and the rectangle it is drawn within this frame.</summary>
public sealed class DrawItem
{
    public DrawItem(ScreenItem screenItem, SciRect rect)
    {
        ScreenItem = screenItem;
        Rect = rect;
    }

    public ScreenItem ScreenItem;
    public SciRect Rect;
}

/// <summary>The items of a plane to draw this frame.</summary>
public sealed class DrawList : StableList<DrawItem>
{
    public void Add(ScreenItem screenItem, SciRect rect) => Add(new DrawItem(screenItem, rect));

    public void SortByScreenItem()
    {
        Pack();
        Sort((a, b) => ScreenItem.IsLess(a.ScreenItem, b.ScreenItem) ? -1 : ScreenItem.IsLess(b.ScreenItem, a.ScreenItem) ? 1 : 0);
    }
}

/// <summary>
/// A layer of screen items with its own coordinate system: a picture, a colour, or a see
/// through region. Port of ScummVM's Plane (plane32.cpp).
/// </summary>
public sealed class Plane32
{
    // picture ids at or below PlanePic make a picture plane; the rest are the special types
    public const int PlanePic = 65531;
    public const int PlanePicTransparentPicture = 65532;
    public const int PlanePicOpaque = 65533;
    public const int PlanePicTransparent = 65534;
    public const int PlanePicColored = 65535;

    private readonly Sci32Graphics _g;

    /// <summary>Creation order, for a stable sort of planes with equal priority.</summary>
    private uint _creationId;

    /// <summary>The picture of a picture plane, or one of the special codes.</summary>
    private int _pictureId;

    /// <summary>Whether a picture plane draws mirrored.</summary>
    private bool _mirrored;

    /// <summary>Set when the script changed the picture; cleared when the plane is synchronised.</summary>
    private bool _pictureChanged;

    public PlaneType Type = PlaneType.Colored;

    /// <summary>The colour a coloured plane erases with.</summary>
    public byte Back;

    /// <summary>Set when the priority changed in an update; cleared when the draw lists are built.</summary>
    public int PriorityChanged;

    /// <summary>The script object, or a number for planes the engine made.</summary>
    public RegT Object;

    /// <summary>Higher priorities draw above lower ones.</summary>
    public short Priority;

    /// <summary>Set when a change to the plane means every item is redrawn next frame.</summary>
    public int RedrawAllCount;

    /// <summary>Frame counters: created, updated with a changed rectangle, deleted, moved or resized.</summary>
    public int Created;
    public int Updated;
    public int Deleted;
    public int Moved;

    /// <summary>The vanishing point items scale towards.</summary>
    public SciPoint VanishingPoint;

    /// <summary>Position and size in screen coordinates, not clipped to the screen.</summary>
    public SciRect PlaneRect;

    /// <summary>Position and size in game script coordinates.</summary>
    public SciRect GameRect;

    /// <summary>Position and size in screen coordinates, clipped to the screen.</summary>
    public SciRect ScreenRect;

    public ScreenItemList ScreenItemList = new();

    /// <summary>An engine made plane covering a game rectangle. Sierra's constructor took no picture id, but callers set it right after.</summary>
    public Plane32(Sci32Graphics g, SciRect gameRect, int pictureId = PlanePicColored)
    {
        _g = g;
        _creationId = g.Frameout.NextPlaneCreationId++;
        _pictureId = pictureId;
        Object = RegT.Make(0, g.Frameout.NextPlaneObjectId++);
        RedrawAllCount = g.Frameout.ScreenCount;
        Created = g.Frameout.ScreenCount;
        GameRect = gameRect;
        ConvertGameRectToPlaneRect();
        Priority = (short)Math.Max(10000, g.Frameout.Planes.GetTopPlanePriority() + 1);
        SetType();
        ScreenRect = PlaneRect;
    }

    /// <summary>A plane from a script Plane object.</summary>
    public Plane32(Sci32Graphics g, RegT obj)
    {
        _g = g;
        _creationId = g.Frameout.NextPlaneCreationId++;
        Object = obj;
        RedrawAllCount = g.Frameout.ScreenCount;
        Created = g.Frameout.ScreenCount;

        var s = g.State;
        var sel = s.Kernel.Selectors;
        VanishingPoint = new SciPoint((short)Selectors.ReadSelectorValue(s, obj, sel.VanishingX), (short)Selectors.ReadSelectorValue(s, obj, sel.VanishingY));
        GameRect = ReadGameRect(obj);
        ConvertGameRectToPlaneRect();

        Back = (byte)Selectors.ReadSelectorValue(s, obj, sel.Back);
        Priority = (short)Selectors.ReadSelectorValue(s, obj, sel.Priority);
        _pictureId = Selectors.ReadSelectorValue(s, obj, sel.Picture);
        SetType();

        _mirrored = Selectors.ReadSelectorValue(s, obj, sel.Mirrored) != 0;
        ScreenRect = PlaneRect;
        ChangePic();
    }

    /// <summary>The copy kept in the visible plane list.</summary>
    public Plane32(Plane32 other)
    {
        _g = other._g;
        _creationId = other._creationId;
        _pictureId = other._pictureId;
        _mirrored = other._mirrored;
        Type = other.Type;
        Back = other.Back;
        Object = other.Object;
        Priority = other.Priority;
        PlaneRect = other.PlaneRect;
        GameRect = other.GameRect;
        ScreenRect = other.ScreenRect;
        ScreenItemList = new ScreenItemList();
        for (int i = 0; i < other.ScreenItemList.Count; i++)
        {
            var item = other.ScreenItemList[i];
            ScreenItemList.AddSlot(item is null ? null : new ScreenItem(item));
        }
    }

    /// <summary>Synchronises the visible copy with the plane.</summary>
    public void Assign(Plane32 other)
    {
        _creationId = other._creationId;
        GameRect = other.GameRect;
        PlaneRect = other.PlaneRect;
        VanishingPoint = other.VanishingPoint;
        _pictureId = other._pictureId;
        Type = other.Type;
        _mirrored = other._mirrored;
        Priority = other.Priority;
        Back = other.Back;
        ScreenRect = other.ScreenRect;
        PriorityChanged = other.PriorityChanged;
    }

    private SciRect ReadGameRect(RegT obj)
    {
        var s = _g.State;
        var sel = s.Kernel.Selectors;
        if (_g.Features.UsesAlternateSelectors)
        {
            return new SciRect(
                (short)Selectors.ReadSelectorValue(s, obj, sel.Left),
                (short)Selectors.ReadSelectorValue(s, obj, sel.Top),
                (short)Selectors.ReadSelectorValue(s, obj, sel.Right) + 1,
                (short)Selectors.ReadSelectorValue(s, obj, sel.Bottom) + 1);
        }

        return new SciRect(
            (short)Selectors.ReadSelectorValue(s, obj, sel.InLeft),
            (short)Selectors.ReadSelectorValue(s, obj, sel.InTop),
            (short)Selectors.ReadSelectorValue(s, obj, sel.InRight) + 1,
            (short)Selectors.ReadSelectorValue(s, obj, sel.InBottom) + 1);
    }

    /// <summary>Priority first, then creation order.</summary>
    public static bool IsLess(Plane32 a, Plane32 b)
    {
        if (a.Priority < b.Priority)
        {
            return true;
        }

        return a.Priority == b.Priority && a._creationId < b._creationId;
    }

    /// <summary>Clips the screen rectangle; LSL6 hires makes planes with invalid rectangles, which become empty.</summary>
    public void ClipScreenRect(SciRect screenRect)
    {
        if (ScreenRect.IsValidRect && ScreenRect.Intersects(screenRect))
        {
            ScreenRect.Clip(screenRect);
        }
        else
        {
            ScreenRect = default;
        }
    }

    /// <summary>The game rectangle scaled to screen pixels, fractions rounded up.</summary>
    private void ConvertGameRectToPlaneRect()
    {
        var frameout = _g.Frameout;
        var ratioX = new Ratio(frameout.ScreenWidth, frameout.ScriptWidth);
        var ratioY = new Ratio(frameout.ScreenHeight, frameout.ScriptHeight);
        PlaneRect = GameRect;
        Rect32Math.MulRu(ref PlaneRect, ratioX, ratioY, 1);
    }

    public bool IsDefaultPlane => Object.IsNumber && Object.Offset == _g.Features.DetectPlaneIdBase();

    // ---- pictures ----

    private void AddPicInternal(int pictureId, SciPoint? position, bool mirrorX)
    {
        int celCount = 1000;
        bool transparent = true;
        for (short celNo = 0; celNo < celCount; celNo++)
        {
            var celObj = new CelObjPic(_g, pictureId, celNo);
            if (celCount == 1000)
            {
                celCount = celObj.CelCount;
            }

            if (!celObj.Transparent)
            {
                transparent = false;
            }

            var screenItem = new ScreenItem(_g, Object, celObj.Info)
            {
                PictureId = pictureId,
                MirrorX = mirrorX,
                Priority = celObj.Priority,
                FixedPriority = true,
                Position = position is { } p ? new SciPoint(p.X + celObj.RelativePosition.X, p.Y + celObj.RelativePosition.Y) : celObj.RelativePosition,
            };
            screenItem.SetCelObj(celObj);
            ScreenItemList.Add(screenItem);
        }

        Type = _g.Features.HasTransparentPicturePlanes && transparent ? PlaneType.TransparentPicture : PlaneType.Picture;
    }

    /// <summary>Replaces the items of the picture with the cels of a new one at a position.</summary>
    public int AddPic(int pictureId, SciPoint position, bool mirrorX, bool deleteDuplicate = true)
    {
        if (deleteDuplicate)
        {
            DeletePic(pictureId);
        }

        AddPicInternal(pictureId, position, mirrorX);
        return _pictureId;
    }

    /// <summary>Re-adds the cels of a picture plane's picture.</summary>
    public void ChangePic()
    {
        _pictureChanged = false;
        if (Type != PlaneType.Picture && Type != PlaneType.TransparentPicture)
        {
            return;
        }

        AddPicInternal(_pictureId, null, _mirrored);
    }

    private void DeletePic(int pictureId)
    {
        foreach (var screenItem in ScreenItemList.Items)
        {
            if (screenItem.PictureId == pictureId)
            {
                screenItem.Created = 0;
                screenItem.Updated = 0;
                screenItem.Deleted = _g.Frameout.ScreenCount;
            }
        }
    }

    /// <summary>Marks the items of the old picture deleted and takes the new picture id without adding its cels.</summary>
    public void DeletePic(int oldPictureId, int newPictureId)
    {
        DeletePic(oldPictureId);
        _pictureId = newPictureId;
    }

    private void DeleteAllPics()
    {
        for (int i = 0; i < ScreenItemList.Count; i++)
        {
            var screenItem = ScreenItemList[i];
            if (screenItem is not null && screenItem.CelInfo.Type == CelType.Pic)
            {
                if (screenItem.Created == 0)
                {
                    screenItem.Created = 0;
                    screenItem.Updated = 0;
                    screenItem.Deleted = _g.Frameout.ScreenCount;
                }
                else
                {
                    ScreenItemList.EraseAt(i);
                }
            }
        }

        ScreenItemList.Pack();
    }

    // ---- rendering ----

    /// <summary>Splits the draw rectangles at the edges of the opaque planes above this one.</summary>
    private void BreakDrawListByPlanes(DrawList drawList, PlaneList planeList)
    {
        int nextPlaneIndex = planeList.FindIndexByObject(Object) + 1;
        int planeCount = planeList.Count;
        Span<SciRect> outRects = stackalloc SciRect[4];
        for (int i = 0; i < drawList.Count; i++)
        {
            var drawItem = drawList[i];
            if (drawItem is null)
            {
                continue;
            }

            for (int j = nextPlaneIndex; j < planeCount; j++)
            {
                var plane = planeList[j];
                if (plane.Type != PlaneType.Transparent && plane.Type != PlaneType.TransparentPicture)
                {
                    int splitCount = Rect32Math.SplitRects(drawItem.Rect, plane.ScreenRect, outRects);
                    if (splitCount != -1)
                    {
                        while (splitCount-- > 0)
                        {
                            drawList.Add(drawItem.ScreenItem, outRects[splitCount]);
                        }

                        drawList.EraseAt(i);
                        break;
                    }
                }
            }
        }

        drawList.Pack();
    }

    private void BreakEraseListByPlanes(RectList eraseList, PlaneList planeList)
    {
        int nextPlaneIndex = planeList.FindIndexByObject(Object) + 1;
        int planeCount = planeList.Count;
        Span<SciRect> outRects = stackalloc SciRect[4];
        for (int i = 0; i < eraseList.Count; i++)
        {
            if (eraseList.IsErased(i))
            {
                continue;
            }

            for (int j = nextPlaneIndex; j < planeCount; j++)
            {
                var plane = planeList[j];
                if (plane.Type != PlaneType.Transparent && plane.Type != PlaneType.TransparentPicture)
                {
                    int splitCount = Rect32Math.SplitRects(eraseList[i], plane.ScreenRect, outRects);
                    if (splitCount != -1)
                    {
                        while (splitCount-- > 0)
                        {
                            eraseList.Add(outRects[splitCount]);
                        }

                        eraseList.EraseAt(i);
                        break;
                    }
                }
            }
        }

        eraseList.Pack();
    }

    /// <summary>
    /// Finds the dirty rectangles of the items of this plane, adds them to the draw and erase
    /// lists, and synchronises the item list with the visible plane.
    /// </summary>
    public void CalcLists(Plane32 visiblePlane, PlaneList planeList, DrawList drawList, RectList eraseList)
    {
        int screenItemCount = ScreenItemList.Count;
        int visiblePlaneItemCount = visiblePlane.ScreenItemList.Count;
        bool remapping = _g.Remap.RemapCount != 0;

        for (int i = 0; i < screenItemCount; i++)
        {
            // items may be added or removed while the list is walked
            if (i >= ScreenItemList.Count)
            {
                break;
            }

            var item = ScreenItemList[i];
            if (item is null)
            {
                continue;
            }

            // Sierra indexed the visible list without bounds checks
            ScreenItem? visibleItem = i < visiblePlaneItemCount ? visiblePlane.ScreenItemList[i] : null;

            // erase rectangles of this item stay within its plane
            var visibleItemScreenRect = default(SciRect);
            if (visibleItem is not null)
            {
                visibleItemScreenRect = visibleItem.ScreenRect;
                visibleItemScreenRect.Clip(ScreenRect);
            }

            if (item.Deleted != 0 && visibleItem is not null && !visibleItemScreenRect.IsEmpty)
            {
                if (remapping)
                {
                    MergeToRectList(visibleItemScreenRect, eraseList);
                }
                else
                {
                    eraseList.Add(visibleItemScreenRect);
                }
            }

            if (item.Created == 0 && item.Updated == 0)
            {
                continue;
            }

            item.CalcRects(this);
            var itemScreenRect = item.ScreenRect;

            if (item.Created != 0)
            {
                if (!itemScreenRect.IsEmpty)
                {
                    drawList.Add(item, itemScreenRect);
                    if (remapping)
                    {
                        MergeToRectList(itemScreenRect, eraseList);
                    }
                }
            }
            else if (remapping)
            {
                // the old rectangle is erased and the item redrawn; when they overlap the union is erased
                if (itemScreenRect.IsEmpty || visibleItem is null || visibleItemScreenRect.IsEmpty || !visibleItemScreenRect.Intersects(itemScreenRect))
                {
                    if (!itemScreenRect.IsEmpty)
                    {
                        drawList.Add(item, itemScreenRect);
                        MergeToRectList(itemScreenRect, eraseList);
                    }

                    if (visibleItem is not null && !visibleItemScreenRect.IsEmpty)
                    {
                        MergeToRectList(visibleItemScreenRect, eraseList);
                    }
                }
                else
                {
                    var extendedScreenRect = visibleItemScreenRect;
                    extendedScreenRect.Extend(itemScreenRect);
                    drawList.Add(item, itemScreenRect);
                    MergeToRectList(extendedScreenRect, eraseList);
                }
            }
            else
            {
                if (!itemScreenRect.IsEmpty)
                {
                    drawList.Add(item, itemScreenRect);
                }

                if (visibleItem is not null && !visibleItemScreenRect.IsEmpty)
                {
                    eraseList.Add(visibleItemScreenRect);
                }
            }
        }

        // the parts covered by other planes go
        BreakEraseListByPlanes(eraseList, planeList);
        BreakDrawListByPlanes(drawList, planeList);

        // only the entries present now are looked at below
        int drawListSizePrimary = drawList.Count;
        int eraseListCount = eraseList.Count;

        // every unchanged item touching an erased area is redrawn
        for (int i = 0; i < eraseListCount; i++)
        {
            var rect = eraseList[i];
            for (int j = 0; j < screenItemCount; j++)
            {
                var item = ScreenItemList[j];
                if (item is not null && item.Created == 0 && item.Updated == 0 && item.Deleted == 0 && rect.Intersects(item.ScreenRect))
                {
                    drawList.Add(item, rect.FindIntersectingRect(item.ScreenRect));
                }
            }
        }

        if (!remapping)
        {
            // unchanged items of higher priority that overlap a drawn item are drawn too, but
            // only for the entries that were in the draw list before the erase pass
            for (int i = 0; i < drawListSizePrimary; i++)
            {
                var drawListEntry = i < drawList.Count ? drawList[i] : null;
                for (int j = 0; j < screenItemCount; j++)
                {
                    var newItem = j < ScreenItemList.Count ? ScreenItemList[j] : null;
                    if (drawListEntry is not null && newItem is not null && newItem.Created == 0 && newItem.Updated == 0 && newItem.Deleted == 0)
                    {
                        var drawnItem = drawListEntry.ScreenItem;
                        if (newItem.HasPriorityAbove(drawnItem) && drawListEntry.Rect.Intersects(newItem.ScreenRect))
                        {
                            MergeToDrawList(j, drawListEntry.Rect.FindIntersectingRect(newItem.ScreenRect), drawList);
                        }
                    }
                }
            }
        }

        DecrementScreenItemArrayCounts(visiblePlane, false);
    }

    /// <summary>Copies changed items to the visible plane and drops deleted ones from both.</summary>
    public void DecrementScreenItemArrayCounts(Plane32? visiblePlane, bool forceUpdate)
    {
        int screenItemCount = ScreenItemList.Count;
        for (int i = 0; i < screenItemCount; i++)
        {
            var item = ScreenItemList[i];
            if (item is null)
            {
                continue;
            }

            if (visiblePlane is not null && (item.Updated != 0 || (forceUpdate && visiblePlane.ScreenItemList.FindByObject(item.Object) is not null)))
            {
                visiblePlane.ScreenItemList[i]!.Assign(item);
            }

            if (item.Updated != 0)
            {
                item.Updated--;
            }

            if (item.Created != 0)
            {
                item.Created--;
                visiblePlane?.ScreenItemList.Add(new ScreenItem(item));
            }

            if (item.Deleted != 0)
            {
                item.Deleted--;
                if (item.Deleted == 0)
                {
                    if (visiblePlane is not null && visiblePlane.ScreenItemList.FindByObject(item.Object) is not null)
                    {
                        visiblePlane.ScreenItemList.EraseAt(i);
                    }

                    ScreenItemList.EraseAt(i);
                }
            }
        }

        ScreenItemList.Pack();
        visiblePlane?.ScreenItemList.Pack();
    }

    /// <summary>
    /// Called from the highest plane down: items under a higher plane's erase rectangles are
    /// redrawn, and an opaque plane absorbs the erase rectangles it covers.
    /// </summary>
    public void FilterDownEraseRects(DrawList drawList, RectList eraseList, RectList higherEraseList)
    {
        int higherEraseCount = higherEraseList.Count;
        if (Type == PlaneType.Transparent || Type == PlaneType.TransparentPicture)
        {
            for (int i = 0; i < higherEraseCount; i++)
            {
                var r = higherEraseList[i];
                int screenItemCount = ScreenItemList.Count;
                for (int j = 0; j < screenItemCount; j++)
                {
                    var item = ScreenItemList[j];
                    if (item is not null && r.Intersects(item.ScreenRect))
                    {
                        MergeToDrawList(j, r, drawList);
                    }
                }
            }
        }
        else
        {
            Span<SciRect> outRects = stackalloc SciRect[4];
            for (int i = 0; i < higherEraseCount; i++)
            {
                var r = higherEraseList[i];
                if (r.Intersects(ScreenRect))
                {
                    r.Clip(ScreenRect);
                    MergeToRectList(r, eraseList);

                    int screenItemCount = ScreenItemList.Count;
                    for (int j = 0; j < screenItemCount; j++)
                    {
                        var item = ScreenItemList[j];
                        if (item is not null && r.Intersects(item.ScreenRect))
                        {
                            MergeToDrawList(j, r, drawList);
                        }
                    }

                    var r2 = higherEraseList[i];
                    int splitCount = Rect32Math.SplitRects(r2, r, outRects);
                    if (splitCount > 0)
                    {
                        while (splitCount-- > 0)
                        {
                            higherEraseList.Add(outRects[splitCount]);
                        }
                    }

                    higherEraseList.EraseAt(i);
                }
            }

            higherEraseList.Pack();
        }
    }

    /// <summary>Called from the lowest plane up: items of a transparent plane over a lower plane's redrawn areas are drawn.</summary>
    public void FilterUpDrawRects(DrawList drawList, DrawList lowerDrawList)
    {
        int lowerDrawCount = lowerDrawList.Count;
        for (int i = 0; i < lowerDrawCount; i++)
        {
            var lower = lowerDrawList[i];
            if (lower is null)
            {
                continue;
            }

            var r = lower.Rect;
            int screenItemCount = ScreenItemList.Count;
            for (int j = 0; j < screenItemCount; j++)
            {
                var item = ScreenItemList[j];
                if (item is not null && r.Intersects(item.ScreenRect))
                {
                    MergeToDrawList(j, r, drawList);
                }
            }
        }
    }

    public void FilterUpEraseRects(DrawList drawList, RectList lowerEraseList)
    {
        int lowerEraseCount = lowerEraseList.Count;
        for (int i = 0; i < lowerEraseCount; i++)
        {
            if (lowerEraseList.IsErased(i))
            {
                continue;
            }

            var r = lowerEraseList[i];
            int screenItemCount = ScreenItemList.Count;
            for (int j = 0; j < screenItemCount; j++)
            {
                var item = ScreenItemList[j];
                if (item is not null && r.Intersects(item.ScreenRect))
                {
                    MergeToDrawList(j, r, drawList);
                }
            }
        }
    }

    /// <summary>Adds the item at an index to the draw list within a rectangle, splitting around what is already listed for it.</summary>
    private void MergeToDrawList(int index, SciRect rect, DrawList drawList)
    {
        var mergeList = new RectList();
        var item = ScreenItemList[index]!;
        var r = item.ScreenRect;
        r.Clip(rect);
        mergeList.Add(r);

        Span<SciRect> outRects = stackalloc SciRect[4];
        for (int i = 0; i < mergeList.Count; i++)
        {
            if (mergeList.IsErased(i))
            {
                continue;
            }

            r = mergeList[i];
            int drawCount = drawList.Count;
            for (int j = 0; j < drawCount; j++)
            {
                var drawItem = drawList[j];
                if (drawItem is null || item.Object != drawItem.ScreenItem.Object)
                {
                    continue;
                }

                if (drawItem.Rect.Contains(r))
                {
                    mergeList.EraseAt(i);
                    break;
                }

                int splitCount = Rect32Math.SplitRects(r, drawItem.Rect, outRects);
                if (splitCount != -1)
                {
                    while (splitCount-- > 0)
                    {
                        mergeList.Add(outRects[splitCount]);
                    }

                    mergeList.EraseAt(i);

                    // on to the next rectangle
                    i++;
                    if (i >= mergeList.Count)
                    {
                        break;
                    }

                    r = mergeList[i];
                }
            }
        }

        mergeList.Pack();
        for (int i = 0; i < mergeList.Count; i++)
        {
            drawList.Add(item, mergeList[i]);
        }
    }

    /// <summary>Adds a rectangle to the erase list, less whatever is already covered.</summary>
    private void MergeToRectList(SciRect rect, RectList eraseList)
    {
        var mergeList = new RectList();
        mergeList.Add(rect);

        Span<SciRect> outRects = stackalloc SciRect[4];
        for (int i = 0; i < mergeList.Count; i++)
        {
            if (mergeList.IsErased(i))
            {
                continue;
            }

            var r = mergeList[i];
            int eraseCount = eraseList.Count;
            for (int j = 0; j < eraseCount; j++)
            {
                if (eraseList.IsErased(j))
                {
                    continue;
                }

                var eraseRect = eraseList[j];
                if (eraseRect.Contains(r))
                {
                    mergeList.EraseAt(i);
                    break;
                }

                int splitCount = Rect32Math.SplitRects(r, eraseRect, outRects);
                if (splitCount != -1)
                {
                    while (splitCount-- > 0)
                    {
                        mergeList.Add(outRects[splitCount]);
                    }

                    mergeList.EraseAt(i);
                    i++;
                    if (i >= mergeList.Count)
                    {
                        break;
                    }

                    r = mergeList[i];
                }
            }
        }

        mergeList.Pack();
        for (int i = 0; i < mergeList.Count; i++)
        {
            eraseList.Add(mergeList[i]);
        }
    }

    /// <summary>Puts every live item of the plane in the draw list and erases the plane's area.</summary>
    public void RedrawAll(Plane32? visiblePlane, PlaneList planeList, DrawList drawList, RectList eraseList)
    {
        int screenItemCount = ScreenItemList.Count;
        for (int i = 0; i < screenItemCount; i++)
        {
            var screenItem = ScreenItemList[i];
            if (screenItem is not null && screenItem.Deleted == 0)
            {
                screenItem.CalcRects(this);
                if (!screenItem.ScreenRect.IsEmpty)
                {
                    MergeToDrawList(i, screenItem.ScreenRect, drawList);
                }
            }
        }

        eraseList.Clear();
        if (!ScreenRect.IsEmpty && Type != PlaneType.Picture && Type != PlaneType.Opaque)
        {
            eraseList.Add(ScreenRect);
        }

        BreakEraseListByPlanes(eraseList, planeList);
        BreakDrawListByPlanes(drawList, planeList);
        RedrawAllCount--;
        DecrementScreenItemArrayCounts(visiblePlane, true);
    }

    private void SetType()
    {
        switch (_pictureId)
        {
            case PlanePicColored:
                Type = PlaneType.Colored;
                break;
            case PlanePicTransparent:
                Type = PlaneType.Transparent;
                break;
            case PlanePicOpaque:
                Type = PlaneType.Opaque;
                break;
            case PlanePicTransparentPicture when _g.Features.HasTransparentPicturePlanes:
                Type = PlaneType.TransparentPicture;
                break;
            default:
                if (!_g.Features.HasTransparentPicturePlanes || Type != PlaneType.TransparentPicture)
                {
                    Type = PlaneType.Picture;
                }

                break;
        }
    }

    /// <summary>Finds what changed against the visible copy after an update from the script. Was UpdatePlane in Sierra's interpreter.</summary>
    public void Sync(Plane32? other, SciRect screenRect)
    {
        int screenCount = _g.Frameout.ScreenCount;
        if (other is null)
        {
            if (_pictureChanged)
            {
                DeleteAllPics();
                SetType();
                ChangePic();
                RedrawAllCount = screenCount;
            }
            else
            {
                SetType();
            }
        }
        else
        {
            if (PlaneRect.Top != other.PlaneRect.Top || PlaneRect.Left != other.PlaneRect.Left || PlaneRect.Right > other.PlaneRect.Right || PlaneRect.Bottom > other.PlaneRect.Bottom)
            {
                // the plane moved or grew
                RedrawAllCount = screenCount;
                Moved = screenCount;
            }
            else if (PlaneRect.Left != other.PlaneRect.Left || PlaneRect.Top != other.PlaneRect.Top || PlaneRect.Right != other.PlaneRect.Right || PlaneRect.Bottom != other.PlaneRect.Bottom)
            {
                // the plane shrank
                Moved = screenCount;
            }

            if (Priority != other.Priority)
            {
                PriorityChanged = screenCount;
            }

            if (_pictureId != other._pictureId || _mirrored != other._mirrored || _pictureChanged)
            {
                DeleteAllPics();
                SetType();
                ChangePic();
                RedrawAllCount = screenCount;
            }

            if (Back != other.Back)
            {
                RedrawAllCount = screenCount;
            }
        }

        Deleted = 0;
        if (Created == 0)
        {
            Updated = screenCount;
        }

        ConvertGameRectToPlaneRect();
        ScreenRect = PlaneRect;
        ClipScreenRect(screenRect);
    }

    /// <summary>Takes the plane's properties from its script object.</summary>
    public void Update(RegT obj)
    {
        var s = _g.State;
        var sel = s.Kernel.Selectors;
        VanishingPoint = new SciPoint((short)Selectors.ReadSelectorValue(s, obj, sel.VanishingX), (short)Selectors.ReadSelectorValue(s, obj, sel.VanishingY));
        GameRect = ReadGameRect(obj);
        ConvertGameRectToPlaneRect();

        Priority = (short)Selectors.ReadSelectorValue(s, obj, sel.Priority);
        int pictureId = Selectors.ReadSelectorValue(s, obj, sel.Picture);
        if (_pictureId != pictureId)
        {
            _pictureId = pictureId;
            _pictureChanged = true;
        }

        _mirrored = Selectors.ReadSelectorValue(s, obj, sel.Mirrored) != 0;
        Back = (byte)Selectors.ReadSelectorValue(s, obj, sel.Back);
    }

    /// <summary>Moves every item that is not a picture cel (or those too) by a delta.</summary>
    public void ScrollScreenItems(int deltaX, int deltaY, bool scrollPics)
    {
        RedrawAllCount = _g.Frameout.ScreenCount;
        foreach (var screenItem in ScreenItemList.Items)
        {
            if (screenItem.Deleted == 0 && (screenItem.CelInfo.Type != CelType.Pic || scrollPics))
            {
                screenItem.Position.X += deltaX;
                screenItem.Position.Y += deltaY;
            }
        }
    }

    /// <summary>Marks the remapped items for redrawing next frame.</summary>
    public void RemapMarkRedraw()
    {
        foreach (var screenItem in ScreenItemList.Items)
        {
            if (screenItem.Deleted == 0 && screenItem.Created == 0 && screenItem.GetCelObj().Remap)
            {
                screenItem.Updated = _g.Frameout.ScreenCount;
            }
        }
    }

    public override string ToString() => $"{Object}: type {Type}, prio {Priority}, pic {_pictureId}, game rect {GameRect}, screen rect {ScreenRect}, {ScreenItemList.Count} items";
}

/// <summary>The planes in priority order. The main and visible lists are kept in step by index.</summary>
public sealed class PlaneList
{
    private readonly List<Plane32> _planes = [];

    public int Count => _planes.Count;

    public Plane32 this[int index] => _planes[index];

    public IReadOnlyList<Plane32> Items => _planes;

    public int FindIndexByObject(RegT obj)
    {
        for (int i = 0; i < _planes.Count; i++)
        {
            if (_planes[i].Object == obj)
            {
                return i;
            }
        }

        return -1;
    }

    public Plane32? FindByObject(RegT obj)
    {
        foreach (var plane in _planes)
        {
            if (plane.Object == obj)
            {
                return plane;
            }
        }

        return null;
    }

    public short GetTopPlanePriority() => _planes.Count > 0 ? _planes[^1].Priority : (short)0;

    /// <summary>The priority of the topmost plane a script created.</summary>
    public short GetTopSciPlanePriority()
    {
        short priority = 0;
        foreach (var plane in _planes)
        {
            if (plane.Priority >= 10000)
            {
                break;
            }

            priority = plane.Priority;
        }

        return priority;
    }

    public void Add(Plane32 plane)
    {
        for (int i = 0; i < _planes.Count; i++)
        {
            if (_planes[i].Priority > plane.Priority)
            {
                _planes.Insert(i, plane);
                return;
            }
        }

        _planes.Add(plane);
    }

    public void Clear() => _planes.Clear();

    public void Erase(Plane32 plane) => _planes.Remove(plane);

    public void RemoveAt(int index) => _planes.RemoveAt(index);

    /// <summary>A stable sort by priority and creation order.</summary>
    public void Sort()
    {
        var sorted = _planes.OrderBy(p => p, Comparer<Plane32>.Create((a, b) => Plane32.IsLess(a, b) ? -1 : Plane32.IsLess(b, a) ? 1 : 0)).ToList();
        _planes.Clear();
        _planes.AddRange(sorted);
    }
}
