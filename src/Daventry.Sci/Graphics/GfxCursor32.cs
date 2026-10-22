using Daventry.Sci.Engine;

namespace Daventry.Sci.Graphics;

/// <summary>
/// The SCI32 mouse cursor. It keeps the pixels of the game buffer under the cursor, draws the
/// cursor onto the hardware frame and puts the saved pixels back when the frame beneath is
/// repainted. Port of ScummVM's GfxCursor32.
/// </summary>
public sealed partial class GfxCursor32
{
    private const byte SkipColor = 255;

    private sealed class DrawRegion
    {
        public SciRect Rect;
        public byte[] Data = [];
    }

    private readonly Sci32Graphics _g;

    /// <summary>The current cursor view, kept for restoring saved games.</summary>
    public CelInfo32 CursorInfo;

    private int _hideCount;
    private readonly DrawRegion _cursorBack = new();
    private readonly DrawRegion _scratch1 = new();
    private readonly DrawRegion _scratch2 = new();
    private readonly DrawRegion _screenRegion = new();
    private readonly DrawRegion _savedScreenRegion = new();
    private readonly DrawRegion _cursor = new();
    private int _width;
    private int _height;
    private SciPoint _position;
    private SciPoint _hotSpot;
    private SciRect _restrictedArea;
    private bool _needsPaint;

    public GfxCursor32(Sci32Graphics g)
    {
        _g = g;
    }

    /// <summary>The rendered cursor position in screen coordinates.</summary>
    public SciPoint Position => _position;

    public bool IsHidden => _hideCount != 0;

    /// <summary>Points the cursor at the game buffer it reads from.</summary>
    public void Init(PixelBuffer outputBuffer)
    {
        _screenRegion.Rect = new SciRect(outputBuffer.Width, outputBuffer.Height);
        _screenRegion.Data = outputBuffer.Pixels;
        _restrictedArea = _screenRegion.Rect;
    }

    /// <summary>Hides the cursor; each hide needs an unhide before it shows again.</summary>
    public void Hide()
    {
        if (_hideCount++ != 0)
        {
            return;
        }

        if (!_cursorBack.Rect.IsEmpty)
        {
            DrawToScreen(_cursorBack);
        }
    }

    private void RevealCursor()
    {
        _cursorBack.Rect = _cursor.Rect;
        _cursorBack.Rect.Clip(_screenRegion.Rect);
        if (_cursorBack.Rect.IsEmpty)
        {
            return;
        }

        CopyFromScreen(_cursorBack);
        _scratch1.Rect = _cursor.Rect;
        Copy(_scratch1, _cursorBack, false);
        Copy(_scratch1, _cursor, true);
        DrawToScreen(_scratch1);
    }

    /// <summary>Copies source into target where they overlap; with <paramref name="skip"/> the cursor's transparent pixels stay out.</summary>
    private static void Copy(DrawRegion target, DrawRegion source, bool skip)
    {
        if (source.Rect.IsEmpty)
        {
            return;
        }

        var drawRect = source.Rect;
        drawRect.Clip(target.Rect);
        if (drawRect.IsEmpty)
        {
            return;
        }

        int sourceXOffset = drawRect.Left - source.Rect.Left;
        int sourceYOffset = drawRect.Top - source.Rect.Top;
        int drawRectWidth = drawRect.Width;
        int drawRectHeight = drawRect.Height;
        int targetStride = target.Rect.Width;
        int sourceStride = source.Rect.Width;
        int targetIndex = (drawRect.Top - target.Rect.Top) * targetStride + (drawRect.Left - target.Rect.Left);
        int sourceIndex = sourceYOffset * sourceStride + sourceXOffset;

        for (int y = 0; y < drawRectHeight; y++)
        {
            if (skip)
            {
                for (int x = 0; x < drawRectWidth; x++)
                {
                    byte pixel = source.Data[sourceIndex + x];
                    if (pixel != SkipColor)
                    {
                        target.Data[targetIndex + x] = pixel;
                    }
                }
            }
            else
            {
                Array.Copy(source.Data, sourceIndex, target.Data, targetIndex, drawRectWidth);
            }

            targetIndex += targetStride;
            sourceIndex += sourceStride;
        }
    }

    /// <summary>Copies a region to the hardware frame.</summary>
    private void DrawToScreen(DrawRegion source)
    {
        var drawRect = source.Rect;
        drawRect.Clip(_screenRegion.Rect);
        if (drawRect.IsEmpty)
        {
            return;
        }

        int sourceXOffset = drawRect.Left - source.Rect.Left;
        int sourceYOffset = drawRect.Top - source.Rect.Top;
        var screen = _g.Frameout.Screen;
        for (int y = 0; y < drawRect.Height; y++)
        {
            Array.Copy(source.Data, (sourceYOffset + y) * source.Rect.Width + sourceXOffset, screen.Pixels, (drawRect.Top + y) * screen.Width + drawRect.Left, drawRect.Width);
        }

        _g.Frameout.ScreenDirty = true;
    }

    public void Unhide()
    {
        if (_hideCount == 0 || --_hideCount != 0)
        {
            return;
        }

        _cursor.Rect.MoveTo(_position.X - _hotSpot.X, _position.Y - _hotSpot.Y);
        RevealCursor();
    }

    /// <summary>Shows the cursor whatever the hide count.</summary>
    public void Show()
    {
        if (_hideCount != 0)
        {
            _hideCount = 0;
            _cursor.Rect.MoveTo(_position.X - _hotSpot.X, _position.Y - _hotSpot.Y);
            RevealCursor();
        }
    }

    /// <summary>Limits the cursor to a game rectangle.</summary>
    public void SetRestrictedArea(SciRect rect)
    {
        _restrictedArea = rect;
        var frameout = _g.Frameout;
        Rect32Math.MulRu(ref _restrictedArea, new Ratio(frameout.ScreenWidth, frameout.ScriptWidth), new Ratio(frameout.ScreenHeight, frameout.ScriptHeight), 0);

        // Sierra's interpreter moved the cursor into the area; the position is clamped here and the
        // host mouse is left where it is
        if (_position.X < rect.Left)
        {
            _position.X = rect.Left;
        }

        if (_position.X >= rect.Right)
        {
            _position.X = rect.Right - 1;
        }

        if (_position.Y < rect.Top)
        {
            _position.Y = rect.Top;
        }

        if (_position.Y >= rect.Bottom)
        {
            _position.Y = rect.Bottom - 1;
        }
    }

    public void ClearRestrictedArea() => _restrictedArea = _screenRegion.Rect;

    public void SetView(int viewId, short loopNo, short celNo)
    {
        Hide();
        CursorInfo.ResourceId = viewId;
        CursorInfo.LoopNo = loopNo;
        CursorInfo.CelNo = celNo;

        if (viewId != -1)
        {
            var view = new CelObjView(_g, viewId, loopNo, celNo);
            _hotSpot = view.Origin;
            _width = view.Width;
            _height = view.Height;

            // Sierra never enlarged cursors, but the early games' low resolution cursors are hard
            // to see in high resolution: GK1's are all doubled, PQ4's only the small ones, as
            // inventory items usually have a high resolution cursor
            bool pixelDouble = false;
            if (_g.Frameout.IsHiRes && (_g.GameId == SciGameId.Gk1 || (_g.GameId == SciGameId.Pq4 && _width <= 22 && _height <= 22)))
            {
                _width *= 2;
                _height *= 2;
                _hotSpot.X *= 2;
                _hotSpot.Y *= 2;
                pixelDouble = true;
            }

            _cursor.Data = new byte[_width * _height];
            _cursor.Rect = new SciRect(_width, _height);
            Array.Fill(_cursor.Data, SkipColor);
            var target = new PixelBuffer(_cursor.Data, 0, _width, _height);
            if (pixelDouble)
            {
                view.Draw(target, _cursor.Rect, new SciPoint(0, 0), false, new Ratio(2, 1), new Ratio(2, 1));
            }
            else
            {
                view.Draw(target, _cursor.Rect, new SciPoint(0, 0), false);
            }
        }
        else
        {
            _hotSpot = new SciPoint(0, 0);
            _width = _height = 1;
            _cursor.Data = [SkipColor];
            _cursor.Rect = new SciRect(_width, _height);
            _cursorBack.Rect = _cursor.Rect;
            _cursorBack.Rect.Clip(_screenRegion.Rect);
            if (!_cursorBack.Rect.IsEmpty)
            {
                CopyFromScreen(_cursorBack);
            }
        }

        _cursorBack.Data = new byte[_width * _height];
        _scratch1.Data = new byte[_width * _height];
        _scratch2.Data = new byte[_width * _height * 4];
        _savedScreenRegion.Data = new byte[_width * _height];
        Unhide();
    }

    private void CopyFromScreen(DrawRegion target) => Copy(target, _screenRegion, false);

    /// <summary>Moves the cursor to a game script position.</summary>
    public void SetPosition(SciPoint position)
    {
        var frameout = _g.Frameout;
        var newPosition = new SciPoint(
            (position.X * new Ratio(frameout.ScreenWidth, frameout.ScriptWidth)).ToInt(),
            (position.Y * new Ratio(frameout.ScreenHeight, frameout.ScriptHeight)).ToInt());
        DeviceMoved(ref newPosition);
    }

    /// <summary>Called for each rectangle about to be copied to the hardware.</summary>
    public void GonnaPaint(SciRect paintRect)
    {
        if (_hideCount == 0 && !_needsPaint && !_cursorBack.Rect.IsEmpty)
        {
            paintRect.Left &= ~3;
            paintRect.Right |= 3;
            if (_cursorBack.Rect.Intersects(paintRect))
            {
                _needsPaint = true;
            }
        }
    }

    /// <summary>Paints the cursor into the game buffer so the copies to hardware carry it.</summary>
    public void PaintStarting()
    {
        if (_needsPaint)
        {
            _savedScreenRegion.Rect = _cursor.Rect;
            Copy(_savedScreenRegion, _screenRegion, false);
            Copy(_screenRegion, _cursor, true);
        }
    }

    /// <summary>Takes the cursor out of the game buffer again and remembers what is under it.</summary>
    public void DonePainting()
    {
        if (_needsPaint)
        {
            Copy(_screenRegion, _savedScreenRegion, false);
            _savedScreenRegion.Rect = default;
            _needsPaint = false;
        }

        if (_hideCount == 0 && !_cursorBack.Rect.IsEmpty)
        {
            Copy(_cursorBack, _screenRegion, false);
        }
    }

    /// <summary>The host mouse moved to a screen position; returns whether it was clamped to the restricted area.</summary>
    public bool DeviceMoved(ref SciPoint position)
    {
        bool restricted = false;
        if (position.X < _restrictedArea.Left)
        {
            position.X = _restrictedArea.Left;
            restricted = true;
        }

        if (position.X >= _restrictedArea.Right)
        {
            position.X = _restrictedArea.Right - 1;
            restricted = true;
        }

        if (position.Y < _restrictedArea.Top)
        {
            position.Y = _restrictedArea.Top;
            restricted = true;
        }

        if (position.Y >= _restrictedArea.Bottom)
        {
            position.Y = _restrictedArea.Bottom - 1;
            restricted = true;
        }

        if (_position.X != position.X || _position.Y != position.Y)
        {
            _position = position;
            Move();
        }

        return restricted;
    }

    private void Move()
    {
        if (_hideCount != 0)
        {
            return;
        }

        // back on screen after being off it
        _cursor.Rect.MoveTo(_position.X - _hotSpot.X, _position.Y - _hotSpot.Y);
        if (_cursorBack.Rect.IsEmpty)
        {
            RevealCursor();
            return;
        }

        // moved off screen
        if (!_cursor.Rect.Intersects(_screenRegion.Rect))
        {
            DrawToScreen(_cursorBack);
            return;
        }

        if (!_cursor.Rect.Intersects(_cursorBack.Rect))
        {
            // moved to a different part of the screen
            _scratch1.Rect = _cursor.Rect;
            _scratch1.Rect.Clip(_screenRegion.Rect);
            CopyFromScreen(_scratch1);

            _scratch2.Rect = _scratch1.Rect;
            Copy(_scratch2, _scratch1, false);

            Copy(_scratch1, _cursor, true);
            DrawToScreen(_scratch1);
            DrawToScreen(_cursorBack);

            _cursorBack.Rect = _cursor.Rect;
            _cursorBack.Rect.Clip(_screenRegion.Rect);
            Copy(_cursorBack, _scratch2, false);
        }
        else
        {
            // moved, but still over the old place
            var mergedRect = _cursorBack.Rect;
            mergedRect.Extend(_cursor.Rect);
            mergedRect.Clip(_screenRegion.Rect);

            _scratch2.Rect = mergedRect;
            CopyFromScreen(_scratch2);
            Copy(_scratch2, _cursorBack, false);

            _cursorBack.Rect = _cursor.Rect;
            _cursorBack.Rect.Clip(_screenRegion.Rect);
            Copy(_cursorBack, _scratch2, false);

            Copy(_scratch2, _cursor, true);
            DrawToScreen(_scratch2);
        }
    }
}
