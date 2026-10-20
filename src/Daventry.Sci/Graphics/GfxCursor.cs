using Daventry.Core.Graphics;
using Daventry.Core.Hosting;
using Daventry.Sci.Engine;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Graphics;

/// <summary>
/// The mouse cursor of SCI0 to SCI1.1 games: cursor resources (SCI0 and SCI1) and view cels
/// (SCI1.1), the move zone, the magnifier zoom zone, and the position scripts set. Port of
/// ScummVM's cursor.cpp. The cursor is drawn in software onto the presented frame, so the
/// host needs no hardware cursor.
/// </summary>
public sealed class GfxCursor
{
    private const int Sci0HeightWidth = 16;
    private const int Sci0ResourceSize = 68;
    private const int Sci0TransparencyColor = 1;
    private const int Sci1Gray = 7;

    private readonly EngineState _s;
    private readonly ResourceManager _resMan;
    private readonly GfxScreen _screen;
    private readonly GfxCache _cache;
    private readonly SciEventManager _event;
    private readonly ILog _log;
    private byte[] _bitmap = [];
    private int _width;
    private int _height;
    private int _hotspotX;
    private int _hotspotY;
    private byte _clearKey;
    private bool _moveZoneActive;
    private SciRect _moveZone;
    private bool _zoomZoneActive;
    private SciRect _zoomZone;
    private GfxView? _zoomCursorView;
    private int _zoomCursorLoop;
    private int _zoomCursorCel;
    private GfxView? _zoomPicView;
    private byte _zoomColor;
    private int _zoomMultiplier;
    private byte[] _cursorSurface = [];

    public GfxCursor(EngineState s)
    {
        _s = s;
        _resMan = s.ResMan;
        _screen = s.Screen;
        _cache = s.Cache;
        _event = s.Events;
        _log = s.Log;
        IsVisible = true;
        // the cursor starts in the middle of the screen
        SetPosition(new SciPoint(_screen.ScriptWidth / 2, _screen.ScriptHeight / 2));
    }

    public bool IsVisible { get; private set; }

    /// <summary>Incremented whenever the cursor's shape or visibility changes, so the presenter can redraw.</summary>
    public int Version { get; private set; }

    /// <summary>Asks the host to move its pointer; set by the engine when the host supports it.</summary>
    public Action<int, int>? WarpMouse { get; set; }

    public void KernelShow()
    {
        if (!IsVisible)
        {
            Version++;
        }

        IsVisible = true;
    }

    public void KernelHide()
    {
        if (IsVisible)
        {
            Version++;
        }

        IsVisible = false;
    }

    private void ReplaceCursor(byte[] bitmap, int width, int height, int hotspotX, int hotspotY, byte clearKey)
    {
        _bitmap = bitmap;
        _width = width;
        _height = height;
        _hotspotX = hotspotX;
        _hotspotY = hotspotY;
        _clearKey = clearKey;
        Version++;
    }

    /// <summary>Loads a cursor resource: 16x16 with two bit planes.</summary>
    public void KernelSetShape(int resourceId)
    {
        if (resourceId == -1)
        {
            // no resource given: the cursor is hidden
            KernelHide();
            return;
        }

        var resource = _resMan.FindResource(new ResourceId(ResourceType.Cursor, (ushort)resourceId));
        if (resource?.Data is null)
        {
            throw new SciVmException($"cursor resource {resourceId} not found");
        }

        var data = resource.Data;
        if (data.Length != Sci0ResourceSize)
        {
            throw new SciVmException($"cursor resource {resourceId} has invalid size");
        }

        int hotspotX;
        int hotspotY;
        bool isSci0Cursor = _s.Version <= SciVersion.V01;
        if (isSci0Cursor)
        {
            // SCI0 cursors hold a flag: bit 0 of byte 3 centres the hotspot, otherwise it is top left
            hotspotX = hotspotY = data[3] != 0 ? Sci0HeightWidth / 2 : 0;
        }
        else
        {
            hotspotX = data[0] | (data[1] << 8);
            hotspotY = data[2] | (data[3] << 8);
        }

        // SCI0 cursors are black, white and transparent; SCI1 adds grey when both masks are set
        var colorMapping = new byte[4];
        colorMapping[0] = 0;
        colorMapping[1] = _screen.ColorWhite;
        colorMapping[2] = Sci0TransparencyColor;
        colorMapping[3] = isSci0Cursor ? _screen.ColorWhite : (byte)Sci1Gray;

        var rawBitmap = new byte[Sci0HeightWidth * Sci0HeightWidth];
        int pOut = 0;
        for (int y = 0; y < Sci0HeightWidth; y++)
        {
            int maskA = data[4 + (y << 1)] | (data[5 + (y << 1)] << 8);
            int maskB = data[4 + 32 + (y << 1)] | (data[5 + 32 + (y << 1)] << 8);
            for (int x = 0; x < Sci0HeightWidth; x++)
            {
                int color = (((maskA << x) & 0x8000) | (((maskB << x) >> 1) & 0x4000)) >> 14;
                rawBitmap[pOut++] = colorMapping[color];
            }
        }

        if (hotspotX >= Sci0HeightWidth || hotspotY >= Sci0HeightWidth)
        {
            throw new SciVmException($"cursor {resourceId}'s hotspot ({hotspotX}, {hotspotY}) is out of range of the cursor's dimensions");
        }

        ReplaceCursor(rawBitmap, Sci0HeightWidth, Sci0HeightWidth, hotspotX, hotspotY, Sci0TransparencyColor);
        KernelShow();
    }

    /// <summary>Uses a view cel as the cursor; the hotspot defaults to the cel's origin.</summary>
    public void KernelSetView(int viewNum, int loopNum, int celNum, SciPoint? hotspot)
    {
        var cursorView = _cache.GetView(viewNum);
        if (cursorView is null)
        {
            _log.Warning("sci.gfx", $"cursor view {viewNum} not found");
            return;
        }

        var celInfo = cursorView.GetCelInfo(loopNum, celNum);
        int width = celInfo.Width;
        int height = celInfo.Height;
        var cursorHotspot = hotspot ?? new SciPoint((celInfo.Width >> 1) - celInfo.DisplaceX, celInfo.Height - celInfo.DisplaceY - 1);

        // EcoQuest 1 hides the cursor with a 1x1 transparent cel
        if (width < 2 || height < 2)
        {
            KernelHide();
            return;
        }

        var rawBitmap = cursorView.GetBitmap(loopNum, celNum);
        ReplaceCursor(rawBitmap, width, height, cursorHotspot.X, cursorHotspot.Y, celInfo.ClearKey);
        KernelShow();
    }

    /// <summary>
    /// Moves the pointer. Ignored while the cursor is hidden: EcoQuest 1 floppy keeps setting it
    /// to the top left corner during its intro.
    /// </summary>
    public void SetPosition(SciPoint pos)
    {
        if (!IsVisible)
        {
            return;
        }

        _event.MousePos = pos;
        WarpMouse?.Invoke(pos.X, pos.Y);
        Version++;
    }

    public SciPoint GetPosition() => _event.MousePos;

    /// <summary>Keeps the pointer inside the move zone and updates the magnifier. Called from kGetEvent.</summary>
    public void RefreshPosition()
    {
        var mousePoint = GetPosition();

        if (_moveZoneActive)
        {
            bool clipped = false;
            if (mousePoint.X < _moveZone.Left)
            {
                mousePoint.X = _moveZone.Left;
                clipped = true;
            }
            else if (mousePoint.X >= _moveZone.Right)
            {
                mousePoint.X = _moveZone.Right - 1;
                clipped = true;
            }

            if (mousePoint.Y < _moveZone.Top)
            {
                mousePoint.Y = _moveZone.Top;
                clipped = true;
            }
            else if (mousePoint.Y >= _moveZone.Bottom)
            {
                mousePoint.Y = _moveZone.Bottom - 1;
                clipped = true;
            }

            if (clipped)
            {
                SetPosition(mousePoint);
            }
        }

        if (_zoomZoneActive && _zoomCursorView is not null && _zoomPicView is not null)
        {
            var cursorCelInfo = _zoomCursorView.GetCelInfo(_zoomCursorLoop, _zoomCursorCel);
            var cursorBitmap = _zoomCursorView.GetBitmap(_zoomCursorLoop, _zoomCursorCel);
            var picCelInfo = _zoomPicView.GetCelInfo(0, 0);
            var rawPicBitmap = _zoomPicView.GetBitmap(0, 0);

            var cursorHotspot = new SciPoint((cursorCelInfo.Width >> 1) - cursorCelInfo.DisplaceX, cursorCelInfo.Height - cursorCelInfo.DisplaceY - 1);
            int targetX = Math.Max(0, (mousePoint.X - _moveZone.Left) * _zoomMultiplier) - cursorHotspot.X;
            int targetY = Math.Max(0, (mousePoint.Y - _moveZone.Top) * _zoomMultiplier) - cursorHotspot.Y;

            // the magnifier colour shows the magnified picture pixels; Sierra drew the cursor by
            // hand inside kAnimate, which also kept it under everything else
            for (int x = 0; x < cursorCelInfo.Width; x++)
            {
                for (int y = 0; y < cursorCelInfo.Height; y++)
                {
                    int curPos = cursorCelInfo.Width * y + x;
                    if (cursorBitmap[curPos] != _zoomColor)
                    {
                        continue;
                    }

                    int rawY = targetY + y;
                    int rawX = targetX + x;
                    if (rawY >= 0 && rawY < picCelInfo.Height && rawX >= 0 && rawX < picCelInfo.Width)
                    {
                        _cursorSurface[curPos] = rawPicBitmap[picCelInfo.Width * rawY + rawX];
                    }
                    else
                    {
                        _cursorSurface[curPos] = rawPicBitmap[0]; // the top left pixel colour
                    }
                }
            }

            ReplaceCursor(_cursorSurface, cursorCelInfo.Width, cursorCelInfo.Height, cursorHotspot.X, cursorHotspot.Y, cursorCelInfo.ClearKey);
        }
    }

    public void KernelResetMoveZone() => _moveZoneActive = false;

    public void KernelSetMoveZone(SciRect zone)
    {
        _moveZone = zone;
        _moveZoneActive = true;
    }

    public void KernelClearZoomZone()
    {
        KernelResetMoveZone();
        _zoomZone = default;
        _zoomColor = 0;
        _zoomMultiplier = 0;
        _zoomZoneActive = false;
        _zoomCursorView = null;
        _zoomPicView = null;
        _cursorSurface = [];
    }

    /// <summary>The magnifier of Freddy Pharkas and Laura Bow 2: a view cel over a magnified picture cel.</summary>
    public void KernelSetZoomZone(int multiplier, SciRect zone, int viewNum, int loopNum, int celNum, int picNum, byte zoomColor)
    {
        KernelClearZoomZone();

        _zoomMultiplier = multiplier;
        if (multiplier is not (1 or 2 or 4))
        {
            throw new SciVmException("Unexpected zoom multiplier (expected 1, 2 or 4)");
        }

        _zoomCursorView = _cache.GetView(viewNum);
        _zoomCursorLoop = loopNum;
        _zoomCursorCel = celNum;
        _zoomPicView = _cache.GetView(picNum);
        if (_zoomCursorView is null || _zoomPicView is null)
        {
            _log.Warning("sci.gfx", $"zoom zone views {viewNum}/{picNum} not found");
            return;
        }

        _cursorSurface = (byte[])_zoomCursorView.GetBitmap(_zoomCursorLoop, _zoomCursorCel).Clone();
        _zoomZone = zone;
        KernelSetMoveZone(_zoomZone);
        _zoomColor = zoomColor;
        _zoomZoneActive = true;
    }

    public void KernelSetPos(SciPoint pos) => KernelMoveCursor(pos);

    public void KernelMoveCursor(SciPoint pos)
    {
        if (pos.X > _screen.ScriptWidth || pos.Y > _screen.ScriptHeight)
        {
            _log.Warning("sci.gfx", $"attempt to place cursor at invalid coordinates ({pos.Y}, {pos.X})");
            return;
        }

        SetPosition(pos);
        // reading an event makes sure the new coordinates are seen next time
        _event.GetSciEvent(KernelFunctions.SciEventPeek);
    }

    /// <summary>Draws the cursor onto a frame at the current pointer position.</summary>
    public void Draw(Surface8 frame)
    {
        if (!IsVisible || _bitmap.Length == 0)
        {
            return;
        }

        var pos = GetPosition();
        int left = pos.X - _hotspotX;
        int top = pos.Y - _hotspotY;
        var pixels = frame.Pixels;
        for (int y = 0; y < _height; y++)
        {
            int fy = top + y;
            if (fy < 0 || fy >= frame.Height)
            {
                continue;
            }

            for (int x = 0; x < _width; x++)
            {
                int fx = left + x;
                if (fx < 0 || fx >= frame.Width)
                {
                    continue;
                }

                byte color = _bitmap[y * _width + x];
                if (color != _clearKey)
                {
                    pixels[fy * frame.Width + fx] = color;
                }
            }
        }
    }
}
