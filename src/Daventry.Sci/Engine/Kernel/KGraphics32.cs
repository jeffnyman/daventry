using Daventry.Core.Hosting;
using Daventry.Sci.Graphics;

namespace Daventry.Sci.Engine;

public static partial class KernelFunctions
{
    // ---- kgraphics32.cpp and kevent.cpp: the SCI32 planes, screen items, palette, cursor and cels ----
    // The text renderer, the controls (scroll windows, edit boxes, message boxes), the plane
    // transitions, the line drawing and the video players are not ported yet; those calls are
    // logged once and ignored so the scripts keep running.

    // sub-function parents; the VM dispatches on the first argument
    public static ValueTask<RegT> PalCycleAsync(EngineState s, KernelArgs argv) => new(s.Acc);

    public static ValueTask<RegT> BitmapAsync(EngineState s, KernelArgs argv) => new(s.Acc);

    public static ValueTask<RegT> CelInfoAsync(EngineState s, KernelArgs argv) => new(s.Acc);

    public static ValueTask<RegT> CelLinkAsync(EngineState s, KernelArgs argv) => new(s.Acc);

    public static ValueTask<RegT> FontAsync(EngineState s, KernelArgs argv) => new(s.Acc);

    public static ValueTask<RegT> TextAsync(EngineState s, KernelArgs argv) => new(s.Acc);

    public static ValueTask<RegT> ScrollWindowAsync(EngineState s, KernelArgs argv) => new(s.Acc);

    public static ValueTask<RegT> RemapColors32Async(EngineState s, KernelArgs argv) => new(s.Acc);

    public static ValueTask<RegT> ShowMovieWinAsync(EngineState s, KernelArgs argv) => new(s.Acc);

    public static ValueTask<RegT> PlayVmdAsync(EngineState s, KernelArgs argv) => new(s.Acc);

    public static ValueTask<RegT> RobotAsync(EngineState s, KernelArgs argv) => new(s.Acc);

    private static RobotDecoder RobotOf(EngineState s)
        => s.RequireGfx32().Video?.RobotPlayer ?? throw new SciVmException("kRobot: the video players are not set up");

    public static RegT RobotOpen(EngineState s, KernelArgs argv)
    {
        int robotId = argv.UInt16(0);
        var plane = argv[1];
        int priority = argv.Int16(2);
        int x = argv.Int16(3);
        int y = argv.Int16(4);
        int scale = argv.Count > 5 ? argv.Int16(5) : 128;
        RobotOf(s).Open(robotId, plane, priority, x, y, scale);
        return RegT.Make(0, 0);
    }

    public static RegT RobotShowFrame(EngineState s, KernelArgs argv)
    {
        int frameNo = argv.UInt16(0);
        int newX = argv.Count > 1 ? argv.UInt16(1) : RobotDecoder.Unspecified;
        int newY = argv.Count > 1 ? argv.UInt16(2) : RobotDecoder.Unspecified;
        RobotOf(s).ShowFrame(frameNo, newX, newY, RobotDecoder.Unspecified);
        return s.Acc;
    }

    public static RegT RobotGetFrameSize(EngineState s, KernelArgs argv)
    {
        int numFramesTotal = RobotOf(s).GetFrameSize(out var frameRect);
        var outRect = s.SegMan.LookupArray(argv[0]);
        ReadOnlySpan<RegT> values = [RegT.Make(0, frameRect.Left), RegT.Make(0, frameRect.Top), RegT.Make(0, frameRect.Right - 1), RegT.Make(0, frameRect.Bottom - 1)];
        outRect.SetElements(0, values);
        return RegT.Make(0, numFramesTotal);
    }

    public static RegT RobotPlay(EngineState s, KernelArgs argv)
    {
        RobotOf(s).Resume();
        return s.Acc;
    }

    public static RegT RobotGetIsFinished(EngineState s, KernelArgs argv) => RegT.Make(0, RobotOf(s).Status == RobotStatus.End ? 1 : 0);

    public static RegT RobotGetIsInitialized(EngineState s, KernelArgs argv) => RegT.Make(0, RobotOf(s).Status != RobotStatus.Uninitialized ? 1 : 0);

    public static RegT RobotClose(EngineState s, KernelArgs argv)
    {
        RobotOf(s).Close();
        return s.Acc;
    }

    public static RegT RobotGetCue(EngineState s, KernelArgs argv)
    {
        Selectors.WriteSelectorValue(s, argv[0], s.Kernel.Selectors.Signal, RobotOf(s).GetCue());
        return s.Acc;
    }

    public static RegT RobotPause(EngineState s, KernelArgs argv)
    {
        RobotOf(s).Pause();
        return s.Acc;
    }

    public static RegT RobotGetFrameNo(EngineState s, KernelArgs argv) => RegT.Make(0, RobotOf(s).GetFrameNo());

    public static RegT RobotSetPriority(EngineState s, KernelArgs argv)
    {
        RobotOf(s).Priority = argv.Int16(0);
        return s.Acc;
    }

    public static ValueTask<RegT> PlayDuckAsync(EngineState s, KernelArgs argv) => new(s.Acc);

    // ---- planes and screen items ----

    public static RegT AddPlane(EngineState s, KernelArgs argv)
    {
        s.RequireGfx32().Frameout.KernelAddPlane(argv[0]);
        return s.Acc;
    }

    public static RegT UpdatePlane(EngineState s, KernelArgs argv)
    {
        s.RequireGfx32().Frameout.KernelUpdatePlane(argv[0]);
        return s.Acc;
    }

    public static RegT DeletePlane(EngineState s, KernelArgs argv)
    {
        s.RequireGfx32().Frameout.KernelDeletePlane(argv[0]);
        return s.Acc;
    }

    public static RegT AddScreenItem(EngineState s, KernelArgs argv)
    {
        s.RequireGfx32().Frameout.KernelAddScreenItem(argv[0]);
        return s.Acc;
    }

    public static RegT UpdateScreenItem(EngineState s, KernelArgs argv)
    {
        s.RequireGfx32().Frameout.KernelUpdateScreenItem(argv[0]);
        return s.Acc;
    }

    public static RegT DeleteScreenItem(EngineState s, KernelArgs argv)
    {
        s.RequireGfx32().Frameout.KernelDeleteScreenItem(argv[0]);
        return s.Acc;
    }

    public static async ValueTask<RegT> FrameOutAsync(EngineState s, KernelArgs argv)
    {
        bool showBits = argv.Count == 0 || argv.UInt16(0) != 0;
        int throttleMs = await s.RequireGfx32().Frameout.KernelFrameOut(showBits);
        s.EventCounter = 0;
        if (throttleMs > 0)
        {
            // the throttler sleeps when the previous frame armed it, then this frame arms it again
            await s.SpeedThrottler((uint)throttleMs);
            s.ThrottleTrigger = true;
        }

        return s.Acc;
    }

    public static RegT GetHighPlanePri(EngineState s, KernelArgs argv) => RegT.Make(0, s.RequireGfx32().Frameout.KernelGetHighPlanePri());

    public static RegT IsHiRes(EngineState s, KernelArgs argv)
    {
        var frameout = s.RequireGfx32().Frameout;
        return frameout.ScreenWidth < 640 || frameout.ScreenHeight < 400 ? RegT.Null : RegT.True;
    }

    public static RegT IsOnMe(EngineState s, KernelArgs argv)
        => s.RequireGfx32().Frameout.KernelIsOnMe(argv[2], new SciPoint(argv.Int16(0), argv.Int16(1)), argv.Int16(3) != 0);

    public static RegT ObjectIntersect(EngineState s, KernelArgs argv) => RegT.Make(0, s.RequireGfx32().Frameout.KernelObjectIntersect(argv[0], argv[1]));

    public static RegT MovePlaneItems(EngineState s, KernelArgs argv)
    {
        bool scrollPics = argv.Count > 3 && argv.UInt16(3) != 0;
        s.RequireGfx32().Frameout.KernelMovePlaneItems(argv[0], argv.Int16(1), argv.Int16(2), scrollPics);
        return s.Acc;
    }

    public static RegT AddPicAt(EngineState s, KernelArgs argv)
    {
        bool mirrorX = argv.Count > 4 && argv.Int16(4) != 0;
        bool deleteDuplicate = argv.Count <= 5 || argv.Int16(5) != 0;
        s.RequireGfx32().Frameout.KernelAddPicAt(argv[0], argv.UInt16(1), argv.Int16(2), argv.Int16(3), mirrorX, deleteDuplicate);
        return s.Acc;
    }

    public static RegT SetNowSeen32(EngineState s, KernelArgs argv)
    {
        bool found = s.RequireGfx32().Frameout.KernelSetNowSeen(argv[0]);

        // Mother Goose Deluxe is assumed to use the older kSetNowSeen, as it came before SQ6
        if (s.Version <= SciVersion.V2_1Early || s.GameId is SciGameId.Sq6 or SciGameId.Mothergoosehires)
        {
            return s.Acc;
        }

        return RegT.Make(0, found ? 1 : 0);
    }

    public static RegT BaseSetter32(EngineState s, KernelArgs argv)
    {
        var g = s.RequireGfx32();
        var sel = s.Kernel.Selectors;
        var obj = argv[0];
        int viewId = Selectors.ReadSelectorValue(s, obj, sel.View);
        short loopNo = (short)Selectors.ReadSelectorValue(s, obj, sel.Loop);
        short celNo = (short)Selectors.ReadSelectorValue(s, obj, sel.Cel);
        short x = (short)Selectors.ReadSelectorValue(s, obj, sel.X);
        short y = (short)Selectors.ReadSelectorValue(s, obj, sel.Y);

        var celObj = new CelObjView(g, viewId, loopNo, celNo);
        var scaleX = new Ratio();
        if (s.Version < SciVersion.V2_1Late)
        {
            scaleX = new Ratio(g.Frameout.ScriptWidth, celObj.XResolution);
        }

        int brLeft = celObj.MirrorX
            ? x - ((celObj.Width - celObj.Origin.X) * scaleX).ToInt()
            : x - (celObj.Origin.X * scaleX).ToInt();
        int brRight = brLeft + (celObj.Width * scaleX).ToInt() - 1;

        Selectors.WriteSelectorValue(s, obj, sel.BrLeft, brLeft);
        Selectors.WriteSelectorValue(s, obj, sel.BrRight, brRight);
        Selectors.WriteSelectorValue(s, obj, sel.BrBottom, y + 1);
        Selectors.WriteSelectorValue(s, obj, sel.BrTop, y + 1 - (short)Selectors.ReadSelectorValue(s, obj, sel.YStep));
        return s.Acc;
    }

    public static async ValueTask<RegT> ShakeScreen32Async(EngineState s, KernelArgs argv)
    {
        var frameout = s.RequireGfx32().Frameout;
        int shakeCount = argv.Int16(0);

        // Sierra's interpreter read a direction argument that GK1 and QFG4 do not pass; vertical
        // is the SCI16 default the scripts expect
        int directions = argv.Count > 1 ? argv.Int16(1) : GfxScreen.ShakeDirectionVertical;
        int offset = frameout.IsHiRes ? 8 : 4;
        while (shakeCount-- > 0)
        {
            if (s.ShouldQuit())
            {
                break;
            }

            frameout.ShakeOffset = ((directions & GfxScreen.ShakeDirectionHorizontal) != 0 ? offset : 0, (directions & GfxScreen.ShakeDirectionVertical) != 0 ? offset : 0);
            frameout.ScreenDirty = true;
            await s.SleepMs(3 * 1000 / 60);
            frameout.ShakeOffset = (0, 0);
            frameout.ScreenDirty = true;
            await s.SleepMs(3 * 1000 / 60);
        }

        return s.Acc;
    }

    public static RegT GlobalToLocal32(EngineState s, KernelArgs argv) => ConvertPlaneCoordinates(s, argv, -1);

    public static RegT LocalToGlobal32(EngineState s, KernelArgs argv) => ConvertPlaneCoordinates(s, argv, 1);

    private static RegT ConvertPlaneCoordinates(EngineState s, KernelArgs argv, int sign)
    {
        var frameout = s.RequireGfx32().Frameout;
        var result = argv[0];
        var planeObj = argv[1];
        bool visible = true;
        var plane = frameout.VisiblePlanes.FindByObject(planeObj);
        if (plane is null)
        {
            plane = frameout.Planes.FindByObject(planeObj);
            visible = false;
        }

        if (plane is null)
        {
            throw new SciVmException($"k{(sign < 0 ? "GlobalToLocal" : "LocalToGlobal")}: Plane {planeObj} not found");
        }

        var sel = s.Kernel.Selectors;
        int x = (short)Selectors.ReadSelectorValue(s, result, sel.X) + sign * plane.GameRect.Left;
        int y = (short)Selectors.ReadSelectorValue(s, result, sel.Y) + sign * plane.GameRect.Top;
        Selectors.WriteSelectorValue(s, result, sel.X, x);
        Selectors.WriteSelectorValue(s, result, sel.Y, y);
        return RegT.Make(0, visible ? 1 : 0);
    }

    public static RegT SetPalStyleRange(EngineState s, KernelArgs argv)
    {
        s.RequireGfx32().Transitions.KernelSetPalStyleRange((byte)argv.UInt16(0), (byte)argv.UInt16(1));
        return s.Acc;
    }

    public static RegT MorphOn(EngineState s, KernelArgs argv)
    {
        s.RequireGfx32().Frameout.PalMorphIsOn = true;
        return s.Acc;
    }

    /// <summary>An immediate plane change with an optional transition effect.</summary>
    public static RegT SetShowStyle(EngineState s, KernelArgs argv)
    {
        int type = argv.UInt16(0);
        var planeObj = argv[1];
        int seconds = argv.Int16(2);
        // -1 for an "enter" fade, 0 for an "exit" fade; a palette index to fill the screen with otherwise
        int back = argv.Int16(3);
        int priority = argv.Int16(4);
        int animate = argv.Int16(5);
        int refFrame = argv.Int16(6);
        int blackScreen;
        RegT fadeArray;
        int divisions;

        // SCI2 to SCI2.1 early; KQ7 2.00b uses a mismatched SCI2.1 early Styler script, so its
        // calls put the divisions where the fade array belongs
        if (s.Version <= SciVersion.V2_1Early || s.GameId == SciGameId.Kq7)
        {
            blackScreen = 0;
            fadeArray = RegT.Null;
            divisions = argv.Count > 7 ? argv.Int16(7) : -1;
        }
        else if (s.Version <= SciVersion.V2_1Middle || (s.GameId == SciGameId.Rama && s.IsDemo) || (s.GameId == SciGameId.Lighthouse && s.IsDemo && s.Version == SciVersion.V2_1Late))
        {
            // SCI2.1 middle, the RAMA demo and the non-interactive Lighthouse demo
            blackScreen = 0;
            fadeArray = argv.Count > 7 ? argv[7] : RegT.Null;
            divisions = argv.Count > 8 ? argv.Int16(8) : -1;
        }
        else
        {
            // SCI2.1 late and SCI3, the Macintosh SCI2.1 late games and the LSL7 demo
            blackScreen = argv.Int16(7);
            fadeArray = argv.Count > 8 ? argv[8] : RegT.Null;
            divisions = argv.Count > 9 ? argv.Int16(9) : -1;
        }

        // the morph style (15) arrived during SCI2.1 early, after LSL6 and PQ4
        int maxType = s.Version < SciVersion.V2_1Early || s.GameId is SciGameId.Lsl6 or SciGameId.Pq4 ? 14 : 15;
        if (type > maxType)
        {
            throw new SciVmException($"Illegal show style {type} for plane {planeObj}");
        }

        s.RequireGfx32().Transitions.KernelSetShowStyle(planeObj, type, seconds, back, priority, animate, refFrame, fadeArray, divisions, blackScreen);
        return s.Acc;
    }

    /// <summary>LSL6 hires' intro scrolls a picture in (room 110).</summary>
    public static async ValueTask<RegT> SetScrollAsync(EngineState s, KernelArgs argv)
    {
        bool mirrorX = argv.Count > 6 && argv.UInt16(6) != 0;
        // argv[5] was a speed Sierra's interpreter ignored
        await s.RequireGfx32().Transitions.KernelSetScroll(argv[0], argv.Int16(1), argv.Int16(2), argv.UInt16(3), argv.UInt16(4) != 0, mirrorX);
        return s.Acc;
    }

    // ---- the cursor ----

    public static RegT SetCursor32(EngineState s, KernelArgs argv)
    {
        var cursor = s.RequireGfx32().Cursor;
        switch (argv.Count)
        {
            case 1:
                if (argv.Int16(0) == -2)
                {
                    cursor.ClearRestrictedArea();
                }
                else if (argv[0].IsNull)
                {
                    cursor.Hide();
                }
                else
                {
                    cursor.Show();
                }

                break;
            case 2:
                cursor.SetPosition(new SciPoint(argv.Int16(0), argv.Int16(1)));
                break;
            case 3:
                cursor.SetView(argv.UInt16(0), argv.Int16(1), argv.Int16(2));
                break;
            case 4:
                cursor.SetRestrictedArea(new SciRect(argv.Int16(0), argv.Int16(1), argv.Int16(2) + 1, argv.Int16(3) + 1));
                break;
            default:
                throw new SciVmException($"kSetCursor: Invalid number of arguments ({argv.Count})");
        }

        return s.Acc;
    }

    // ---- cels ----

    public static RegT CelHigh32(EngineState s, KernelArgs argv)
    {
        var g = s.RequireGfx32();
        var celObj = new CelObjView(g, argv.UInt16(0), argv.Int16(1), argv.Int16(2));
        int height = celObj.Height;
        if (s.Version < SciVersion.V2_1Late)
        {
            height = Rect32Math.MulRu(height, new Ratio(g.Frameout.ScriptHeight, celObj.YResolution));
        }

        return RegT.Make(0, height);
    }

    public static RegT CelWide32(EngineState s, KernelArgs argv)
    {
        var g = s.RequireGfx32();
        var celObj = new CelObjView(g, argv.UInt16(0), argv.Int16(1), argv.Int16(2));
        int width = celObj.Width;
        if (s.Version < SciVersion.V2_1Late)
        {
            width = Rect32Math.MulRu(width, new Ratio(g.Frameout.ScriptWidth, celObj.XResolution));
        }

        return RegT.Make(0, width);
    }

    // Shivers' red door puzzle (room 23601) and Phantasmagoria 2's save screen read cels this way
    public static RegT CelInfoGetOriginX(EngineState s, KernelArgs argv)
        => RegT.Make(0, new CelObjView(s.RequireGfx32(), argv.UInt16(0), argv.Int16(1), argv.Int16(2)).Origin.X);

    public static RegT CelInfoGetOriginY(EngineState s, KernelArgs argv)
        => RegT.Make(0, new CelObjView(s.RequireGfx32(), argv.UInt16(0), argv.Int16(1), argv.Int16(2)).Origin.Y);

    public static RegT CelInfoGetPixel(EngineState s, KernelArgs argv)
    {
        var view = new CelObjView(s.RequireGfx32(), argv.UInt16(0), argv.Int16(1), argv.Int16(2));
        return RegT.Make(0, view.ReadPixel(argv.Int16(3), argv.Int16(4), view.MirrorX));
    }

    // Lighthouse's weapon building puzzle (room 800) reads cel links
    public static RegT CelLinkGetX(EngineState s, KernelArgs argv)
        => RegT.Make(0, new CelObjView(s.RequireGfx32(), argv.UInt16(0), argv.Int16(1), argv.Int16(2)).GetLinkPosition(argv.Int16(3)).X);

    public static RegT CelLinkGetY(EngineState s, KernelArgs argv)
        => RegT.Make(0, new CelObjView(s.RequireGfx32(), argv.UInt16(0), argv.Int16(1), argv.Int16(2)).GetLinkPosition(argv.Int16(3)).Y);

    // ---- bitmaps ----

    public static RegT BitmapCreate(EngineState s, KernelArgs argv)
    {
        int width = argv.Int16(0);
        int height = argv.Int16(1);
        int skipColor = argv.Int16(2);
        int backColor = argv.Int16(3);
        int xResolution = argv.Count > 4 ? argv.Int16(4) : s.RequireGfx32().Frameout.ScriptWidth;
        int yResolution = argv.Count > 5 ? argv.Int16(5) : s.RequireGfx32().Frameout.ScriptHeight;
        bool useRemap = argv.Count > 6 && argv.Int16(6) != 0;

        var bitmap = s.SegMan.AllocateBitmap(out var bitmapId, width, height, (byte)skipColor, 0, 0, xResolution, yResolution, 0, useRemap, true);
        bitmap.Pixels.Fill((byte)backColor);
        return bitmapId;
    }

    public static RegT BitmapDestroy(EngineState s, KernelArgs argv)
    {
        if (s.SegMan.IsValidAddr(argv[0], SegmentType.Bitmap))
        {
            s.SegMan.FreeBitmap(argv[0]);
        }

        return s.Acc;
    }

    public static RegT BitmapDrawView(EngineState s, KernelArgs argv)
    {
        var bitmap = s.SegMan.LookupBitmap(argv[0]);
        var view = new CelObjView(s.RequireGfx32(), argv.UInt16(1), argv.Int16(2), argv.Int16(3));
        int x = argv.Count > 4 ? argv.Int16(4) : 0;
        int y = argv.Count > 5 ? argv.Int16(5) : 0;
        int alignX = argv.Count > 7 ? argv.Int16(7) : -1;
        int alignY = argv.Count > 8 ? argv.Int16(8) : -1;

        var origin = bitmap.Origin;
        var position = new SciPoint(x == -1 ? origin.X : x, y == -1 ? origin.Y : y);
        position.X -= alignX == -1 ? view.Origin.X : alignX;
        position.Y -= alignY == -1 ? view.Origin.Y : alignY;

        var drawRect = new SciRect(position.X, position.Y, position.X + view.Width, position.Y + view.Height);
        drawRect.Clip(new SciRect(bitmap.Width, bitmap.Height));
        view.Draw(new PixelBuffer(bitmap.RawData, bitmap.PixelsOffset, bitmap.Width, bitmap.Height), drawRect, position, view.MirrorX);
        return s.Acc;
    }

    public static RegT BitmapDrawColor(EngineState s, KernelArgs argv)
    {
        var bitmap = s.SegMan.LookupBitmap(argv[0]);
        var fillRect = new SciRect(argv.Int16(1), argv.Int16(2), argv.Int16(3) + 1, argv.Int16(4) + 1);
        new PixelBuffer(bitmap.RawData, bitmap.PixelsOffset, bitmap.Width, bitmap.Height).FillRect(fillRect, (byte)argv.Int16(5));
        return s.Acc;
    }

    public static RegT BitmapSetOrigin(EngineState s, KernelArgs argv)
    {
        s.SegMan.LookupBitmap(argv[0]).SetOrigin(argv.Int16(1), argv.Int16(2));
        return s.Acc;
    }

    public static RegT BitmapCreateFromView(EngineState s, KernelArgs argv)
    {
        var view = new CelObjView(s.RequireGfx32(), argv.UInt16(0), argv.Int16(1), argv.Int16(2));
        byte skipColor = argv.Count > 3 && argv.Int16(3) != -1 ? (byte)argv.Int16(3) : view.SkipColor;
        byte backColor = argv.Count > 4 && argv.Int16(4) != -1 ? (byte)argv.Int16(4) : view.SkipColor;
        bool useRemap = argv.Count > 5 && argv.Int16(5) != 0;

        var bitmap = s.SegMan.AllocateBitmap(out var bitmapId, view.Width, view.Height, skipColor, 0, 0, view.XResolution, view.YResolution, 0, useRemap, true);
        var buffer = new PixelBuffer(bitmap.RawData, bitmap.PixelsOffset, bitmap.Width, bitmap.Height);
        var viewRect = new SciRect(view.Width, view.Height);
        buffer.FillRect(viewRect, backColor);
        view.Draw(buffer, viewRect, new SciPoint(0, 0), view.MirrorX);

        if (argv.Count > 6 && !argv[6].IsNull)
        {
            var clutHandle = argv[6];
            if (s.SegMan.IsObject(clutHandle))
            {
                clutHandle = Selectors.ReadSelector(s, clutHandle, s.Kernel.Selectors.Data);
            }

            bitmap.ApplyRemap(s.SegMan.LookupArray(clutHandle));
        }

        return bitmapId;
    }

    public static RegT BitmapGetInfo(EngineState s, KernelArgs argv)
    {
        var bitmap = s.SegMan.LookupBitmap(argv[0]);
        if (argv.Count == 1)
        {
            return RegT.Make(0, bitmap.Width);
        }

        int offset = argv.Count == 2 ? argv.UInt16(1) : argv.Int16(2) * bitmap.Width + argv.Int16(1);
        if (offset < 0 || offset >= bitmap.Width * bitmap.Height)
        {
            throw new SciVmException($"kBitmapGetInfo: offset {offset} outside bitmap {argv[0]}");
        }

        return RegT.Make(0, bitmap.Pixels[offset]);
    }

    // ---- the palette ----

    public static RegT PaletteSetFromResource32(EngineState s, KernelArgs argv)
    {
        s.RequireGfx32().Palette.LoadPalette(argv.UInt16(0));
        return s.Acc;
    }

    public static RegT PaletteFindColor32(EngineState s, KernelArgs argv)
        => RegT.Make(0, s.RequireGfx32().Palette.MatchColor((byte)argv.UInt16(0), (byte)argv.UInt16(1), (byte)argv.UInt16(2)));

    public static RegT PaletteSetGamma(EngineState s, KernelArgs argv)
    {
        s.RequireGfx32().Palette.SetGamma(argv.Int16(0));
        return s.Acc;
    }

    public static RegT PaletteSetFade(EngineState s, KernelArgs argv)
    {
        s.RequireGfx32().Palette.SetFade(argv.UInt16(2), (byte)argv.UInt16(0), argv.UInt16(1));
        return s.Acc;
    }

    public static RegT PalVarySetVary(EngineState s, KernelArgs argv)
    {
        int paletteId = argv.UInt16(0);
        int time = argv.Count > 1 ? argv.Int16(1) * 60 : 0;
        short percent = argv.Count > 2 ? argv.Int16(2) : (short)100;
        short fromColor = -1;
        short toColor = -1;
        if (s.Features.HasMidPaletteCode && argv.Count > 4)
        {
            fromColor = argv.Int16(3);
            toColor = argv.Int16(4);
        }

        s.RequireGfx32().Palette.KernelPalVarySet(paletteId, percent, time, fromColor, toColor);
        return s.Acc;
    }

    public static RegT PalVarySetPercent(EngineState s, KernelArgs argv)
    {
        int time = argv.Count > 0 ? argv.Int16(0) * 60 : 0;
        short percent = argv.Count > 1 ? argv.Int16(1) : (short)0;
        s.RequireGfx32().Palette.SetVaryPercent(percent, time);
        return s.Acc;
    }

    public static RegT PalVaryGetPercent(EngineState s, KernelArgs argv) => RegT.Make(0, s.RequireGfx32().Palette.VaryPercent);

    public static RegT PalVaryOff(EngineState s, KernelArgs argv)
    {
        s.RequireGfx32().Palette.VaryOff();
        return s.Acc;
    }

    public static RegT PalVaryMergeTarget(EngineState s, KernelArgs argv)
    {
        var palette = s.RequireGfx32().Palette;
        palette.KernelPalVaryMergeTarget(argv.UInt16(0));
        return RegT.Make(0, palette.VaryPercent);
    }

    public static RegT PalVarySetTime(EngineState s, KernelArgs argv)
    {
        s.RequireGfx32().Palette.SetVaryTime(argv.Int16(0) * 60);
        return s.Acc;
    }

    public static RegT PalVarySetTarget(EngineState s, KernelArgs argv)
    {
        var palette = s.RequireGfx32().Palette;
        palette.KernelPalVarySetTarget(argv.UInt16(0));
        return RegT.Make(0, palette.VaryPercent);
    }

    public static RegT PalVarySetStart(EngineState s, KernelArgs argv)
    {
        var palette = s.RequireGfx32().Palette;
        palette.KernelPalVarySetStart(argv.UInt16(0));
        return RegT.Make(0, palette.VaryPercent);
    }

    public static RegT PalVaryMergeStart(EngineState s, KernelArgs argv)
    {
        var palette = s.RequireGfx32().Palette;
        palette.KernelPalVaryMergeStart(argv.UInt16(0));
        return RegT.Make(0, palette.VaryPercent);
    }

    public static RegT PalVaryPauseResume32(EngineState s, KernelArgs argv)
    {
        s.RequireGfx32().Palette.KernelPalVaryPause(argv.UInt16(0) != 0);
        return s.Acc;
    }

    public static RegT PalCycleSetCycle(EngineState s, KernelArgs argv)
    {
        short delay = argv.Count > 3 ? (short)argv.UInt16(3) : (short)0;
        s.RequireGfx32().Palette.SetCycle((byte)argv.UInt16(0), (byte)argv.UInt16(1), argv.Int16(2), delay);
        return s.Acc;
    }

    public static RegT PalCycleDoCycle(EngineState s, KernelArgs argv)
    {
        short speed = argv.Count > 1 ? argv.Int16(1) : (short)1;
        s.RequireGfx32().Palette.DoCycle((byte)argv.UInt16(0), speed);
        return s.Acc;
    }

    public static RegT PalCyclePause(EngineState s, KernelArgs argv)
    {
        var palette = s.RequireGfx32().Palette;
        if (argv.Count == 0)
        {
            palette.CycleAllPause();
        }
        else
        {
            palette.CyclePause((byte)argv.UInt16(0));
        }

        return s.Acc;
    }

    public static RegT PalCycleOn(EngineState s, KernelArgs argv)
    {
        var palette = s.RequireGfx32().Palette;
        if (argv.Count == 0)
        {
            palette.CycleAllOn();
        }
        else
        {
            palette.CycleOn((byte)argv.UInt16(0));
        }

        return s.Acc;
    }

    public static RegT PalCycleOff(EngineState s, KernelArgs argv)
    {
        var palette = s.RequireGfx32().Palette;
        if (argv.Count == 0)
        {
            palette.CycleAllOff();
        }
        else
        {
            palette.CycleOff((byte)argv.UInt16(0));
        }

        return s.Acc;
    }

    // ---- colour remapping ----

    public static RegT RemapColorsOff(EngineState s, KernelArgs argv)
    {
        var remap = s.RequireGfx32().Remap;
        if (argv.Count == 0)
        {
            remap.RemapAllOff();
        }
        else
        {
            remap.RemapOff((byte)argv.UInt16(0));
        }

        return s.Acc;
    }

    // the optional last argument of the next four fed the priority map debugger, which release interpreters lack
    public static RegT RemapColorsByRange(EngineState s, KernelArgs argv)
    {
        s.RequireGfx32().Remap.RemapByRange((byte)argv.UInt16(0), argv.Int16(1), argv.Int16(2), argv.Int16(3));
        return s.Acc;
    }

    public static RegT RemapColorsByPercent(EngineState s, KernelArgs argv)
    {
        s.RequireGfx32().Remap.RemapByPercent((byte)argv.UInt16(0), argv.Int16(1));
        return s.Acc;
    }

    public static RegT RemapColorsToGray(EngineState s, KernelArgs argv)
    {
        s.RequireGfx32().Remap.RemapToGray((byte)argv.UInt16(0), (sbyte)argv.Int16(1));
        return s.Acc;
    }

    public static RegT RemapColorsToPercentGray(EngineState s, KernelArgs argv)
    {
        s.RequireGfx32().Remap.RemapToPercentGray((byte)argv.UInt16(0), argv.Int16(1), argv.Int16(2));
        return s.Acc;
    }

    public static RegT RemapColorsBlockRange(EngineState s, KernelArgs argv)
    {
        s.RequireGfx32().Remap.BlockRange((byte)argv.UInt16(0), (short)argv.UInt16(1));
        return s.Acc;
    }

    // ---- not ported yet: text, controls, lines, video ----

    // ---- text ----

    public static RegT CreateTextBitmap(EngineState s, KernelArgs argv)
    {
        var g = s.RequireGfx32();
        var sel = s.Kernel.Selectors;
        int subop = argv.UInt16(0);
        int width = 0;
        int height = 0;
        RegT obj;
        if (subop == 0)
        {
            width = argv.UInt16(1);
            height = argv.UInt16(2);
            obj = argv[3];
        }
        else if (subop == 1)
        {
            obj = argv[1];
        }
        else
        {
            s.Log.Warning("sci.gfx", $"Invalid kCreateTextBitmap subop {subop}");
            return RegT.Null;
        }

        string text = s.SegMan.GetString(Selectors.ReadSelector(s, obj, sel.Text));
        int foreColor = (short)Selectors.ReadSelectorValue(s, obj, sel.Fore);
        int backColor = (short)Selectors.ReadSelectorValue(s, obj, sel.Back);
        int skipColor = (short)Selectors.ReadSelectorValue(s, obj, sel.Skip);
        int fontId = Selectors.ReadSelectorValue(s, obj, sel.Font);
        int borderColor = (short)Selectors.ReadSelectorValue(s, obj, sel.BorderColor);
        bool dimmed = Selectors.ReadSelectorValue(s, obj, sel.Dimmed) != 0;
        var rect = new SciRect(
            (short)Selectors.ReadSelectorValue(s, obj, sel.TextLeft),
            (short)Selectors.ReadSelectorValue(s, obj, sel.TextTop),
            (short)Selectors.ReadSelectorValue(s, obj, sel.TextRight) + 1,
            (short)Selectors.ReadSelectorValue(s, obj, sel.TextBottom) + 1);

        if (subop == 0)
        {
            var alignment = (TextAlign)(short)Selectors.ReadSelectorValue(s, obj, sel.Mode);
            return g.Text.CreateFontBitmap(width, height, rect, text, (byte)foreColor, (byte)backColor, (byte)skipColor, fontId, alignment, borderColor, dimmed, true, true);
        }

        var celInfo = new CelInfo32
        {
            Type = CelType.View,
            ResourceId = Selectors.ReadSelectorValue(s, obj, sel.View),
            LoopNo = (short)Selectors.ReadSelectorValue(s, obj, sel.Loop),
            CelNo = (short)Selectors.ReadSelectorValue(s, obj, sel.Cel),
        };
        return g.Text.CreateFontBitmap(celInfo, rect, text, foreColor, backColor, fontId, skipColor, borderColor, dimmed, true);
    }

    public static RegT TextSize32(EngineState s, KernelArgs argv)
    {
        var text32 = s.RequireGfx32().Text;
        text32.SetFont(argv.UInt16(2));
        var rect = s.SegMan.LookupArray(argv[0]);
        string text = s.SegMan.GetString(argv[1]);
        int maxWidth = argv.Count > 3 ? argv.Int16(3) : 0;
        bool doScaling = argv.Count <= 4 || argv.Int16(4) != 0;
        var textRect = text32.GetTextSize(text, maxWidth, doScaling);
        rect.SetElements(0, [RegT.Make(0, textRect.Left), RegT.Make(0, textRect.Top), RegT.Make(0, textRect.Right - 1), RegT.Make(0, textRect.Bottom - 1)]);
        return s.Acc;
    }

    public static RegT TextWidth(EngineState s, KernelArgs argv)
    {
        var text32 = s.RequireGfx32().Text;
        text32.SetFont(argv.UInt16(1));
        return RegT.Make(0, text32.GetStringWidth(s.SegMan.GetString(argv[0])));
    }

    public static RegT PointSize(EngineState s, KernelArgs argv)
    {
        var text32 = s.RequireGfx32().Text;
        text32.SetFont(argv.UInt16(0));
        return RegT.Make(0, text32.GetScaledFontHeight());
    }

    public static RegT SetFontRes(EngineState s, KernelArgs argv)
    {
        var text32 = s.RequireGfx32().Text;
        text32.XResolution = argv.UInt16(0);
        text32.YResolution = argv.UInt16(1);
        return s.Acc;
    }

    /// <summary>Torin's TextButton::createBitmap (script 64894) draws text into an existing bitmap this way.</summary>
    public static RegT BitmapDrawText(EngineState s, KernelArgs argv)
    {
        var g = s.RequireGfx32();
        var bitmap = s.SegMan.LookupBitmap(argv[0]);
        string text = s.SegMan.GetString(argv[1]);
        var textRect = new SciRect(argv.Int16(2), argv.Int16(3), argv.Int16(4) + 1, argv.Int16(5) + 1);
        int foreColor = argv.Int16(6);
        int backColor = argv.Int16(7);
        int skipColor = argv.Int16(8);
        int fontId = argv.UInt16(9);
        var alignment = (TextAlign)argv.Int16(10);
        int borderColor = argv.Int16(11);
        bool dimmed = argv.UInt16(12) != 0;

        textRect.Clip(new SciRect(bitmap.Width, bitmap.Height));
        var textBitmapObject = g.Text.CreateFontBitmap(textRect.Width, textRect.Height, new SciRect(textRect.Width, textRect.Height), text, (byte)foreColor, (byte)backColor, (byte)skipColor, fontId, alignment, borderColor, dimmed, false, false);
        var textCel = new CelObjMem(g, textBitmapObject);
        textCel.Draw(new PixelBuffer(bitmap.RawData, bitmap.PixelsOffset, bitmap.Width, bitmap.Height), textRect, new SciPoint(textRect.Left, textRect.Top), false);
        s.SegMan.FreeBitmap(textBitmapObject);
        return s.Acc;
    }

    public static readonly KernelFunction ShowMovie32Async = NotPorted("ShowMovie");

    /// <summary>kAvoidPath already answers the point-in-polygon query.</summary>
    public static RegT InPolygon(EngineState s, KernelArgs argv) => AvoidPath(s, argv);

    /// <summary>SCI32's CantBeHere checks only the other cast members, never a control map.</summary>
    public static RegT CantBeHere32(EngineState s, KernelArgs argv)
    {
        var listReference = argv.Count > 1 ? argv[1] : RegT.Null;
        return s.Compare.KernelCantBeHere32(argv[0], listReference);
    }
}
