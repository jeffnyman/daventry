using Daventry.Core.Hosting;

namespace Daventry.Agi.Graphics;

/// <summary>
/// A rectangular area of the game screen occupied by a screen object's cel, with the visual and
/// priority data it covered so it can be erased. Mirrors ScummVM's Sprite.
/// </summary>
public sealed class Sprite
{
    public int GivenOrderNr;
    public int SortOrder;
    public required ScreenObjEntry ScreenObj;
    public int XPos;
    public int YPos;
    public int XSize;
    public int YSize;
    public required byte[] BackgroundBuffer;
}

/// <summary>
/// Draws and erases screen objects. Sprites live in two lists: regular (updating) sprites and
/// static (non-updating) sprites. Port of ScummVM's SpritesMgr.
/// </summary>
public sealed class SpritesMgr
{
    private readonly AgiEngine _vm;
    private readonly GfxMgr _gfx;
    private readonly List<Sprite> _spriteRegularList = [];
    private readonly List<Sprite> _spriteStaticList = [];

    public SpritesMgr(AgiEngine vm, GfxMgr gfx)
    {
        _vm = vm;
        _gfx = gfx;
    }

    private static int CompareSprites(Sprite a, Sprite b)
        => a.SortOrder == b.SortOrder ? a.GivenOrderNr.CompareTo(b.GivenOrderNr) : a.SortOrder.CompareTo(b.SortOrder);

    public void BuildRegularSpriteList()
    {
        int givenOrderNr = 0;
        FreeList(_spriteRegularList);
        foreach (var screenObj in _vm.State.ScreenObjTable)
        {
            if (screenObj.HasFlags(ViewFlags.Animated | ViewFlags.Update | ViewFlags.Drawn))
            {
                BuildSpriteListAdd(givenOrderNr, screenObj, _spriteRegularList);
                givenOrderNr++;
            }
        }

        _spriteRegularList.Sort(CompareSprites);
    }

    public void BuildStaticSpriteList()
    {
        int givenOrderNr = 0;
        FreeList(_spriteStaticList);
        foreach (var screenObj in _vm.State.ScreenObjTable)
        {
            if ((screenObj.Flags & (ViewFlags.Animated | ViewFlags.Update | ViewFlags.Drawn)) == (ViewFlags.Animated | ViewFlags.Drawn))
            {
                BuildSpriteListAdd(givenOrderNr, screenObj, _spriteStaticList);
                givenOrderNr++;
            }
        }

        _spriteStaticList.Sort(CompareSprites);
    }

    public void BuildAllSpriteLists()
    {
        BuildStaticSpriteList();
        BuildRegularSpriteList();
    }

    private void BuildSpriteListAdd(int givenOrderNr, ScreenObjEntry screenObj, List<Sprite> spriteList)
    {
        // skip screen objects whose view is not loaded
        if (!_vm.Resources.Directory.View[screenObj.CurrentViewNr].IsLoaded)
        {
            return;
        }

        int sortOrder = (screenObj.Flags & ViewFlags.FixedPriority) != 0
            ? _gfx.PriorityToY(screenObj.Priority)
            : screenObj.YPos;

        int xPos = screenObj.XPos;
        int yPos = screenObj.YPos - screenObj.YSize + 1;
        int xSize = screenObj.XSize;
        int ySize = screenObj.YSize;

        // The original did no bounds checking and corrupted memory for off-screen objects; skip them.
        if (xPos < 0)
        {
            _vm.Host.Log.Warning("agi", $"BuildSpriteListAdd(): ignoring screen obj {screenObj.ObjectNr}, because xPos ({xPos}) < 0");
            return;
        }

        if (yPos < 0)
        {
            _vm.Host.Log.Warning("agi", $"BuildSpriteListAdd(): ignoring screen obj {screenObj.ObjectNr}, because yPos ({yPos}) < 0");
            return;
        }

        // ScummVM compares the right edge against SCRIPT_HEIGHT here, which is looser than the
        // width; kept as is so behaviour matches.
        int xRight = xPos + xSize;
        if (xRight > GfxMgr.ScriptHeight)
        {
            _vm.Host.Log.Warning("agi", $"BuildSpriteListAdd(): ignoring screen obj {screenObj.ObjectNr}, because rightPos ({xRight}) > {GfxMgr.ScriptWidth}");
            return;
        }

        int yBottom = yPos + ySize;
        if (yBottom > GfxMgr.ScriptHeight)
        {
            _vm.Host.Log.Warning("agi", $"BuildSpriteListAdd(): ignoring screen obj {screenObj.ObjectNr}, because bottomPos ({yBottom}) > {GfxMgr.ScriptHeight}");
            return;
        }

        spriteList.Add(new Sprite
        {
            GivenOrderNr = givenOrderNr,
            SortOrder = sortOrder,
            ScreenObj = screenObj,
            XPos = xPos,
            YPos = yPos,
            XSize = xSize,
            YSize = ySize,
            BackgroundBuffer = new byte[xSize * ySize * 2], // visual + priority data
        });
    }

    private static void FreeList(List<Sprite> spriteList) => spriteList.Clear();

    public void FreeRegularSprites() => FreeList(_spriteRegularList);

    public void FreeStaticSprites() => FreeList(_spriteStaticList);

    public void FreeAllSprites()
    {
        FreeList(_spriteRegularList);
        FreeList(_spriteStaticList);
    }

    /// <summary>Restores the background under every sprite in the list, last drawn first, then empties the list.</summary>
    private void EraseSprites(List<Sprite> spriteList)
    {
        for (int i = spriteList.Count - 1; i >= 0; i--)
        {
            var sprite = spriteList[i];
            _gfx.BlockRestore(sprite.XPos, sprite.YPos, sprite.XSize, sprite.YSize, sprite.BackgroundBuffer);
        }

        FreeList(spriteList);
    }

    public void EraseRegularSprites() => EraseSprites(_spriteRegularList);

    public void EraseStaticSprites() => EraseSprites(_spriteStaticList);

    public void EraseSprites()
    {
        EraseSprites(_spriteRegularList);
        EraseSprites(_spriteStaticList);
    }

    private void DrawSprites(List<Sprite> spriteList)
    {
        foreach (var sprite in spriteList)
        {
            _gfx.BlockSave(sprite.XPos, sprite.YPos, sprite.XSize, sprite.YSize, sprite.BackgroundBuffer);
            DrawCel(sprite.ScreenObj);
        }
    }

    public void DrawRegularSpriteList() => DrawSprites(_spriteRegularList);

    public void DrawStaticSpriteList() => DrawSprites(_spriteStaticList);

    public void DrawAllSpriteLists()
    {
        DrawSprites(_spriteStaticList);
        DrawSprites(_spriteRegularList);
    }

    /// <summary>Draws a screen object's current cel onto the game and priority screens, respecting priority and control lines.</summary>
    public void DrawCel(ScreenObjEntry screenObj)
    {
        var cel = screenObj.CelData ?? throw new InvalidOperationException($"screen object {screenObj.ObjectNr} has no cel");
        int baseX = screenObj.XPos;
        int curX = baseX;
        int curY = screenObj.YPos - cel.Height + 1; // yPos is the lower left; we need the upper left
        byte celClearKey = cel.ClearKey;
        byte viewPriority = screenObj.Priority;
        bool isViewHidden = true;
        int dataPos = 0;
        var raw = cel.RawBitmap;

        for (int row = 0; row < cel.Height; row++)
        {
            for (int col = 0; col < cel.Width; col++)
            {
                byte curColor = raw[dataPos++];
                if (curColor != celClearKey)
                {
                    byte screenPriority = _gfx.GetPriority(curX, curY);
                    if (screenPriority <= 2)
                    {
                        // control data found
                        if (_gfx.CheckControlPixel(curX, curY, viewPriority))
                        {
                            _gfx.PutPixel(curX, curY, GfxScreenMask.Visual, curColor, 0);
                            isViewHidden = false;
                        }
                    }
                    else if (screenPriority <= viewPriority)
                    {
                        _gfx.PutPixel(curX, curY, GfxScreenMask.All, curColor, viewPriority);
                        isViewHidden = false;
                    }
                }

                curX++;
            }

            curX = baseX;
            curY++;
        }

        if (screenObj.ObjectNr == 0)
        {
            _vm.SetFlag(AgiFlag.EgoInvisible, isViewHidden);
        }
    }

    /// <summary>Renders the area covered by a screen object's current and previous position to the display.</summary>
    public void ShowSprite(ScreenObjEntry screenObj)
    {
        if (!_vm.State.PictureShown)
        {
            return;
        }

        int viewHeightPrev = screenObj.YSizePrev;
        int viewWidthPrev = screenObj.XSizePrev;
        screenObj.YSizePrev = screenObj.YSize;
        screenObj.XSizePrev = screenObj.XSize;

        int y;
        int y2;
        int height1;
        int height2;
        if (screenObj.YPos < screenObj.YPosPrev)
        {
            y = screenObj.YPosPrev;
            y2 = screenObj.YPos;
            height1 = viewHeightPrev;
            height2 = screenObj.YSize;
        }
        else
        {
            y = screenObj.YPos;
            y2 = screenObj.YPosPrev;
            height1 = screenObj.YSize;
            height2 = viewHeightPrev;
        }

        int height = y2 - height2 > y - height1 ? height1 : y - y2 + height2;

        int x;
        int x2;
        int width1;
        int width2;
        if (screenObj.XPos > screenObj.XPosPrev)
        {
            x = screenObj.XPosPrev;
            x2 = screenObj.XPos;
            width1 = viewWidthPrev;
            width2 = screenObj.XSize;
        }
        else
        {
            x = screenObj.XPos;
            x2 = screenObj.XPosPrev;
            width1 = screenObj.XSize;
            width2 = viewWidthPrev;
        }

        int width = x2 + width2 < x + width1 ? width1 : width2 + x2 - x;

        if (x + width > 161)
        {
            width = 161 - x;
        }

        if (1 < height - y)
        {
            height = y + 1;
        }

        int upperY = y - height + 1;
        _gfx.RenderBlock(x, upperY, width, height);
    }

    private void ShowSprites(List<Sprite> spriteList)
    {
        foreach (var sprite in spriteList)
        {
            var screenObj = sprite.ScreenObj;
            ShowSprite(screenObj);

            if (screenObj.StepTimeCount == screenObj.StepTime)
            {
                if (screenObj.XPos == screenObj.XPosPrev && screenObj.YPos == screenObj.YPosPrev)
                {
                    screenObj.Flags |= ViewFlags.DidntMove;
                }
                else
                {
                    screenObj.XPosPrev = screenObj.XPos;
                    screenObj.YPosPrev = screenObj.YPos;
                    screenObj.Flags &= ~ViewFlags.DidntMove;
                }
            }
        }

        _gfx.UpdateScreen();
    }

    public void ShowRegularSpriteList() => ShowSprites(_spriteRegularList);

    public void ShowStaticSpriteList() => ShowSprites(_spriteStaticList);

    public void ShowAllSpriteLists()
    {
        ShowSprites(_spriteStaticList);
        ShowSprites(_spriteRegularList);
    }

    /// <summary>Shows an inventory object's view in the middle of the screen with its description. Port of showObject.</summary>
    public async ValueTask ShowObjectAsync(int viewNr)
    {
        var screenObj = new ScreenObjEntry { ObjectNr = 255 };
        _vm.LoadResource(Resources.AgiResourceType.View, viewNr);
        _vm.SetView(screenObj, viewNr);

        screenObj.YSizePrev = screenObj.CelData!.Height;
        screenObj.XSizePrev = screenObj.CelData.Width;
        screenObj.XPosPrev = (GfxMgr.ScriptWidth - 1 - screenObj.XSize) / 2;
        screenObj.XPos = screenObj.XPosPrev;
        screenObj.YPosPrev = GfxMgr.ScriptHeight - 1;
        screenObj.YPos = screenObj.YPosPrev;
        screenObj.Priority = 15;
        screenObj.Flags = ViewFlags.FixedPriority; // the original OR'ed this into uninitialised memory

        var backgroundBuffer = new byte[screenObj.XSize * screenObj.YSize * 2];
        _gfx.BlockSave(screenObj.XPos, screenObj.YPos - screenObj.YSize + 1, screenObj.XSize, screenObj.YSize, backgroundBuffer);
        DrawCel(screenObj);
        ShowSprite(screenObj);

        await _vm.Text.MessageBoxAsync(_vm.Resources.Views[viewNr]?.Description ?? string.Empty);

        _gfx.BlockRestore(screenObj.XPos, screenObj.YPos - screenObj.YSize + 1, screenObj.XSize, screenObj.YSize, backgroundBuffer);
        ShowSprite(screenObj);
    }

    /// <summary>
    /// Implements add.to.pic: stamps a cel onto the current picture. It is not a sprite and cannot
    /// be moved or removed. A border below 4 draws a priority box around the base of the cel.
    /// </summary>
    public void AddToPic(int viewNr, int loopNr, int celNr, int xPos, int yPos, int priority, int border)
    {
        _vm.State.RecordImageStackCall(ImageStackCall.AddView, viewNr, loopNr, celNr, xPos, yPos, priority, border);

        var screenObj = _vm.State.AddToPicView;
        screenObj.ObjectNr = -1;
        _vm.SetView(screenObj, viewNr);
        _vm.SetLoop(screenObj, loopNr);
        _vm.SetCel(screenObj, celNr);

        screenObj.XSizePrev = screenObj.XSize;
        screenObj.YSizePrev = screenObj.YSize;
        screenObj.XPosPrev = xPos;
        screenObj.XPos = xPos;
        screenObj.YPosPrev = yPos;
        screenObj.YPos = yPos;
        screenObj.Flags = ViewFlags.IgnoreObjects | ViewFlags.IgnoreHorizon | ViewFlags.FixedPriority;
        screenObj.Priority = 15;
        _vm.FixPosition(screenObj);

        if (priority == 0)
        {
            screenObj.Flags = ViewFlags.IgnoreHorizon;
        }

        screenObj.Priority = (byte)priority;

        EraseSprites();

        // required by Gold Rush (Sarien bug #587558)
        if (screenObj.Priority == 0)
        {
            screenObj.Priority = (byte)_gfx.PriorityFromY(screenObj.YPos);
        }

        DrawCel(screenObj);

        if (border <= 3)
        {
            AddToPicDrawPriorityBox(screenObj, border);
        }

        BuildAllSpriteLists();
        DrawAllSpriteLists();
        ShowSprite(screenObj);
    }

    private void AddToPicDrawPriorityBox(ScreenObjEntry screenObj, int border)
    {
        int priorityFromY = _gfx.PriorityFromY(screenObj.YPos);
        int priorityHeight = 0;

        // height of the box: the extent of this priority band above the object
        int curY = screenObj.YPos;
        do
        {
            priorityHeight++;
            if (curY <= 0)
            {
                break;
            }

            curY--;
        }
        while (_gfx.PriorityFromY(curY) == priorityFromY);

        if (screenObj.YSize < priorityHeight)
        {
            priorityHeight = screenObj.YSize;
        }

        // lower horizontal line
        curY = screenObj.YPos;
        int curX = screenObj.XPos;
        for (int width = screenObj.XSize; width > 0; width--)
        {
            _gfx.PutPixel(curX, curY, GfxScreenMask.Priority, 0, (byte)border);
            curX++;
        }

        if (priorityHeight > 1)
        {
            curY = screenObj.YPos;
            curX = screenObj.XPos;
            int offsetX = screenObj.XSize - 1;
            for (int height = priorityHeight - 1; height > 0; height--)
            {
                curY--;
                _gfx.PutPixel(curX, curY, GfxScreenMask.Priority, 0, (byte)border);
                _gfx.PutPixel(curX + offsetX, curY, GfxScreenMask.Priority, 0, (byte)border);
            }

            // upper horizontal line
            curX++;
            for (int width = screenObj.XSize - 2; width > 0; width--)
            {
                _gfx.PutPixel(curX, curY, GfxScreenMask.Priority, 0, (byte)border);
                curX++;
            }
        }
    }
}
