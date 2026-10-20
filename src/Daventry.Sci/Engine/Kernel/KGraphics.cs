using Daventry.Core.Hosting;
using Daventry.Sci.Graphics;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Engine;

public static partial class KernelFunctions
{
    // ---- kgraphics.cpp ----

    private const int DrawPicFlagsMirrored = 1 << 14;
    private const int DrawPicFlagsAnimationBlackout = 1 << 15;

    /// <summary>EGA and Amiga games can set colours above 15; only the lower nibble was used (bugs #5267, #5968).</summary>
    private static int AdjustGraphColor(EngineState s, int color) => s.ResMan.ViewType == ViewType.Ega ? color & 0x0F : color;

    public static void DirLoopWorker(EngineState s, RegT obj, int angle)
    {
        var sel = s.Kernel.Selectors;
        int viewId = Selectors.ReadSelectorValue(s, obj, sel.View);
        int signal = Selectors.ReadSelectorValue(s, obj, sel.Signal);
        if ((signal & SignalDoesntTurn) != 0)
        {
            return;
        }

        int useLoop = -1;
        if (s.Version > SciVersion.V0Early)
        {
            if (angle > 315 || angle < 45)
            {
                useLoop = 3;
            }
            else if (angle > 135 && angle < 225)
            {
                useLoop = 2;
            }
        }
        else
        {
            if (angle > 330 || angle < 30)
            {
                useLoop = 3;
            }
            else if (angle > 150 && angle < 210)
            {
                useLoop = 2;
            }
        }

        if (useLoop == -1)
        {
            useLoop = angle >= 180 ? 1 : 0;
        }
        else if (s.Cache.KernelViewGetLoopCount(viewId) < 4)
        {
            return;
        }

        Selectors.WriteSelectorValue(s, obj, sel.Loop, useLoop);
    }

    public static RegT DirLoop(EngineState s, KernelArgs argv)
    {
        DirLoopWorker(s, argv[0], argv.UInt16(1));
        return s.Acc;
    }

    public static RegT PicNotValid(EngineState s, KernelArgs argv)
    {
        int newPicNotValid = argv.Count > 0 ? argv.UInt16(0) : -1;
        return RegT.Make(0, s.Screen.KernelPicNotValid(newPicNotValid, s.Version));
    }

    private static SciRect GetGraphRect(KernelArgs argv)
    {
        int x = argv.Int16(1);
        int y = argv.Int16(0);
        int x1 = argv.Int16(3);
        int y1 = argv.Int16(2);
        if (x > x1)
        {
            (x, x1) = (x1, x);
        }

        if (y > y1)
        {
            (y, y1) = (y1, y);
        }

        return new SciRect(x, y, x1, y1);
    }

    private static SciPoint GetGraphPoint(KernelArgs argv, int offset) => new(argv.Int16(offset + 1), argv.Int16(offset));

    public static RegT GraphGetColorCount(EngineState s, KernelArgs argv)
        => RegT.Make(0, s.ResMan.ViewType switch { ViewType.Ega => 16, ViewType.Amiga => 32, ViewType.Amiga64 => 64, _ => 256 });

    public static RegT GraphDrawLine(EngineState s, KernelArgs argv)
    {
        int color = AdjustGraphColor(s, argv.Int16(4));
        int priority = argv.Count > 5 ? argv.Int16(5) : -1;
        int control = argv.Count > 6 ? argv.Int16(6) : -1;
        s.Paint.KernelGraphDrawLine(GetGraphPoint(argv, 0), GetGraphPoint(argv, 2), (byte)color, (byte)priority, (byte)control);
        return s.Acc;
    }

    public static RegT GraphSaveBox(EngineState s, KernelArgs argv)
    {
        var rect = GetGraphRect(argv);
        var screenMask = (GfxScreenMask)(argv.UInt16(4) & (int)GfxScreenMask.All);
        return s.Paint.KernelGraphSaveBox(rect, screenMask);
    }

    public static RegT GraphRestoreBox(EngineState s, KernelArgs argv)
    {
        s.Paint.KernelGraphRestoreBox(argv[0]);
        return s.Acc;
    }

    public static RegT GraphFillBoxBackground(EngineState s, KernelArgs argv)
    {
        s.Paint.KernelGraphFillBoxBackground(GetGraphRect(argv));
        return s.Acc;
    }

    public static RegT GraphFillBoxForeground(EngineState s, KernelArgs argv)
    {
        s.Paint.KernelGraphFillBoxForeground(GetGraphRect(argv));
        return s.Acc;
    }

    public static RegT GraphFillBoxAny(EngineState s, KernelArgs argv)
    {
        var rect = GetGraphRect(argv);
        var colorMask = (GfxScreenMask)argv.UInt16(4);
        int color = AdjustGraphColor(s, argv.Int16(5));
        // priority and control may come from the stack beyond the arguments; Sierra read them too
        int priority = argv.Stack[argv.Offset + 6].ToInt16();
        int control = argv.Stack[argv.Offset + 7].ToInt16();
        s.Paint.KernelGraphFillBox(rect, colorMask, (byte)color, (byte)priority, (byte)control);
        return s.Acc;
    }

    public static RegT GraphUpdateBox(EngineState s, KernelArgs argv)
    {
        var rect = GetGraphRect(argv);
        // the original skips the update when a sixth argument is given and not zero
        if (argv.Count <= 5 || argv[5].IsNull)
        {
            s.Paint.KernelGraphUpdateBox(rect);
        }

        return s.Acc;
    }

    public static RegT GraphRedrawBox(EngineState s, KernelArgs argv)
    {
        s.Paint.KernelGraphRedrawBox(GetGraphRect(argv));
        return s.Acc;
    }

    public static RegT GraphAdjustPriority(EngineState s, KernelArgs argv)
    {
        s.Ports.KernelGraphAdjustPriority(argv.UInt16(0), argv.UInt16(1));
        return s.Acc;
    }

    public static RegT GraphSaveUpscaledHiresBox(EngineState s, KernelArgs argv) => RegT.Null; // no hires graphics

    public static async ValueTask<RegT> WaitAsync(EngineState s, KernelArgs argv)
    {
        ushort ticks = argv.UInt16(0);
        ushort delta = await s.Wait(ticks);
        s.EventCounter = 0;
        s.PaletteSetIntensityCounter = 0;
        return RegT.Make(0, delta);
    }

    public static async ValueTask<RegT> ScummVMSleepAsync(EngineState s, KernelArgs argv)
    {
        await s.SleepTicks(argv.UInt16(0));
        return s.Acc;
    }

    public static RegT CoordPri(EngineState s, KernelArgs argv)
    {
        int y = argv.Int16(0);
        if (argv.Count < 2 || y != 1)
        {
            return RegT.Make(0, s.Ports.KernelCoordinateToPriority(y));
        }

        int priority = argv.Int16(1);
        return RegT.Make(0, s.Ports.KernelPriorityToCoordinate((byte)priority));
    }

    public static RegT PriCoord(EngineState s, KernelArgs argv)
        => RegT.Make(0, s.Ports.KernelPriorityToCoordinate((byte)argv.Int16(0)));

    public static RegT CelHigh(EngineState s, KernelArgs argv)
    {
        int viewId = argv.Int16(0);
        if (viewId == -1)
        {
            return RegT.Null;
        }

        int loopNo = argv.Int16(1);
        int celNo = argv.Count >= 3 ? argv.Int16(2) : 0;
        return RegT.Make(0, s.Cache.KernelViewGetCelHeight(viewId, loopNo, celNo));
    }

    public static RegT CelWide(EngineState s, KernelArgs argv)
    {
        int viewId = argv.Int16(0);
        if (viewId == -1)
        {
            return RegT.Null;
        }

        int loopNo = argv.Int16(1);
        int celNo = argv.Count >= 3 ? argv.Int16(2) : 0;
        return RegT.Make(0, s.Cache.KernelViewGetCelWidth(viewId, loopNo, celNo));
    }

    public static RegT NumLoops(EngineState s, KernelArgs argv)
    {
        int viewId = Selectors.ReadSelectorValue(s, argv[0], s.Kernel.Selectors.View);
        return RegT.Make(0, s.Cache.KernelViewGetLoopCount(viewId));
    }

    public static RegT NumCels(EngineState s, KernelArgs argv)
    {
        int viewId = Selectors.ReadSelectorValue(s, argv[0], s.Kernel.Selectors.View);
        int loopNo = (short)Selectors.ReadSelectorValue(s, argv[0], s.Kernel.Selectors.Loop);
        return RegT.Make(0, s.Cache.KernelViewGetCelCount(viewId, loopNo));
    }

    public static RegT DrawPic(EngineState s, KernelArgs argv)
    {
        int pictureId = argv.UInt16(0);
        int animationNr = -1;
        bool animationBlackoutFlag = false;
        bool mirroredFlag = false;
        bool addToFlag = false;
        int egaPaletteNo = 0;

        if (argv.Count >= 2)
        {
            int flags = argv.UInt16(1);
            if ((flags & DrawPicFlagsAnimationBlackout) != 0)
            {
                animationBlackoutFlag = true;
            }

            animationNr = flags & 0xFF;
            if ((flags & DrawPicFlagsMirrored) != 0)
            {
                mirroredFlag = true;
            }
        }

        if (argv.Count >= 3)
        {
            if (!argv[2].IsNull)
            {
                addToFlag = true;
            }

            if (!s.Features.UsesOldGfxFunctions)
            {
                addToFlag = !addToFlag;
            }
        }

        if (argv.Count >= 4)
        {
            egaPaletteNo = argv.UInt16(3);
        }

        s.Paint.KernelDrawPicture(pictureId, animationNr, animationBlackoutFlag, mirroredFlag, addToFlag, egaPaletteNo);
        s.Transitions.Setup(s.Paint.TransitionNumber, s.Paint.TransitionBlackoutFlag);
        s.Paint.TransitionNumber = -1;
        return s.Acc;
    }

    public static RegT PaletteSetFromResource(EngineState s, KernelArgs argv)
    {
        int resourceId = argv.UInt16(0);
        bool force = argv.Count == 2 && argv.UInt16(1) == 2;

        // non-VGA games have no palette resources (Longbow Amiga's 64 colours do)
        if (s.ResMan.ViewType == ViewType.Ega)
        {
            return s.Acc;
        }

        s.Palette.KernelSetFromResource(resourceId, force);
        return s.Acc;
    }

    public static RegT PaletteSetFlag(EngineState s, KernelArgs argv)
    {
        int fromColor = Math.Clamp((int)argv.UInt16(0), 1, 255);
        int toColor = Math.Clamp((int)argv.UInt16(1), 1, 255);
        s.Palette.KernelSetFlag(fromColor, toColor, (byte)argv.UInt16(2));
        return s.Acc;
    }

    public static RegT PaletteUnsetFlag(EngineState s, KernelArgs argv)
    {
        int fromColor = Math.Clamp((int)argv.UInt16(0), 1, 255);
        int toColor = Math.Clamp((int)argv.UInt16(1), 1, 255);
        s.Palette.KernelUnsetFlag(fromColor, toColor, (byte)argv.UInt16(2));
        return s.Acc;
    }

    public static async ValueTask<RegT> PaletteSetIntensityAsync(EngineState s, KernelArgs argv)
    {
        int fromColor = Math.Clamp((int)argv.UInt16(0), 1, 255);
        int toColor = Math.Clamp((int)argv.UInt16(1), 1, 255);
        int intensity = argv.UInt16(2);
        bool setPalette = argv.Count < 4 || argv[3].IsNull;

        // palette intensity does not exist in non-VGA SCI1 games
        if (s.ResMan.ViewType != ViewType.Vga && s.ResMan.ViewType != ViewType.Vga11)
        {
            return s.Acc;
        }

        if (setPalette)
        {
            // an unthrottled script loop fading the palette needs throttling to be visible
            // (KQ6 intro credits, SQ4 CD intro credits); kWait loops are fine
            if (s.PaletteSetIntensityCounter > 0)
            {
                await s.SpeedThrottler(30);
            }

            s.PaletteSetIntensityCounter++;
            s.ThrottleTrigger = true; // scripts without kAnimate, like the LB2 title screen
        }

        s.Palette.KernelSetIntensity(fromColor, toColor, intensity, setPalette);
        return s.Acc;
    }

    public static RegT PaletteFindColor(EngineState s, KernelArgs argv)
        => RegT.Make(0, s.Palette.KernelFindColor(argv.UInt16(0), argv.UInt16(1), argv.UInt16(2)));

    public static async ValueTask<RegT> PaletteAnimateAsync(EngineState s, KernelArgs argv)
    {
        bool paletteChanged = false;
        if (s.ResMan.ViewType is ViewType.Vga or ViewType.Vga11)
        {
            for (int argNr = 0; argNr + 2 < argv.Count; argNr += 3)
            {
                byte fromColor = (byte)argv.UInt16(argNr);
                byte toColor = (byte)argv.UInt16(argNr + 1);
                int speed = argv.Int16(argNr + 2);
                if (s.Palette.KernelAnimate(fromColor, toColor, speed, s.Millis()))
                {
                    paletteChanged = true;
                }
            }

            if (paletteChanged)
            {
                s.Palette.KernelAnimateSet();
            }
        }

        // WORKAROUND: SQ4 floppy counts kAnimate calls during the logo's palette cycling; a small
        // delay keeps the effect visible and the logo on screen (bugs #6057, #6193)
        if (s.GameId == SciGameId.Sq4 && !s.IsCd)
        {
            await s.SleepMs(10);
        }

        return s.Acc;
    }

    public static RegT PaletteSave(EngineState s, KernelArgs argv)
    {
        var memory = s.Palette.KernelSave();
        return s.SegMan.AllocateHunkEntry("kPalette(save)", memory);
    }

    public static RegT PaletteRestore(EngineState s, KernelArgs argv)
    {
        if (!argv[0].IsNull)
        {
            var memory = s.SegMan.GetHunkPointer(argv[0]);
            if (memory is not null)
            {
                s.Palette.KernelRestore(memory);
            }
        }

        return argv[0];
    }

    public static RegT PalVaryInit(EngineState s, KernelArgs argv)
    {
        int paletteId = argv.UInt16(0);
        int ticks = argv.UInt16(1);
        int stepStop = argv.Count >= 3 ? argv.UInt16(2) : 64;
        int direction = argv.Count >= 4 ? argv.UInt16(3) : 1;
        return s.Palette.KernelPalVaryInit(paletteId, ticks, stepStop, direction) ? RegT.Signal : RegT.Null;
    }

    public static RegT PalVaryReverse(EngineState s, KernelArgs argv)
    {
        int ticks = argv.Count >= 1 ? argv.UInt16(0) : -1;
        int stepStop = argv.Count >= 2 ? argv.UInt16(1) : 0;
        int direction = argv.Count >= 3 ? argv.Int16(2) : -1;
        return RegT.Make(0, s.Palette.KernelPalVaryReverse(ticks, stepStop, direction));
    }

    public static RegT PalVaryGetCurrentStep(EngineState s, KernelArgs argv) => RegT.Make(0, s.Palette.KernelPalVaryGetCurrentStep());

    public static RegT PalVaryDeinit(EngineState s, KernelArgs argv)
    {
        s.Palette.KernelPalVaryDeinit();
        return RegT.Null;
    }

    public static RegT PalVaryChangeTarget(EngineState s, KernelArgs argv) => RegT.Make(0, s.Palette.KernelPalVaryChangeTarget(argv.UInt16(0)));

    public static RegT PalVaryChangeTicks(EngineState s, KernelArgs argv)
    {
        s.Palette.KernelPalVaryChangeTicks(argv.UInt16(0));
        return RegT.Null;
    }

    public static RegT PalVaryPauseResume(EngineState s, KernelArgs argv)
    {
        s.Palette.KernelPalVaryPause(!argv[0].IsNull);
        return RegT.Null;
    }

    public static RegT AssertPalette(EngineState s, KernelArgs argv)
    {
        var view = s.Cache.GetView(argv.UInt16(0));
        if (view is not null)
        {
            s.Palette.KernelAssertPalette(view);
        }

        return s.Acc;
    }

    public static RegT DrawCel(EngineState s, KernelArgs argv)
    {
        int viewId = argv.Int16(0);
        int loopNo = argv.Int16(1);
        int celNo = argv.Int16(2);
        int x = argv.UInt16(3);
        int y = argv.UInt16(4);
        int priority = argv.Count > 5 ? argv.Int16(5) : -1;
        int paletteNo = argv.Count > 6 ? argv.UInt16(6) : 0;
        int scaleX = 128;
        int scaleY = 128;

        if (argv.Count > 7)
        {
            // either KQ6 hires or scaling
            if (paletteNo > 0)
            {
                scaleX = argv.UInt16(6);
                scaleY = argv.UInt16(7);
                paletteNo = 0;
            }
        }

        s.Paint.DrawCelAndShow(viewId, loopNo, celNo, x, y, (byte)priority, paletteNo, scaleX, scaleY);
        return s.Acc;
    }

    public static RegT GetPort(EngineState s, KernelArgs argv) => s.Ports.KernelGetActive();

    public static RegT SetPort(EngineState s, KernelArgs argv)
    {
        switch (argv.Count)
        {
            case 1:
                s.Ports.KernelSetActive(argv.UInt16(0));
                break;
            case 7:
            case 6:
            {
                var picRect = new SciRect(argv.Int16(1), argv.Int16(0), argv.Int16(3), argv.Int16(2));
                int picTop = argv.Int16(4);
                int picLeft = argv.Int16(5);
                s.Ports.KernelSetPicWindow(picRect, picTop, picLeft, argv.Count == 7);
                break;
            }

            default:
                throw new SciVmException($"SetPort was called with {argv.Count} parameters");
        }

        return s.Acc;
    }

    public static RegT NewWindow(EngineState s, KernelArgs argv)
    {
        var rect1 = new SciRect(argv.Int16(1), argv.Int16(0), argv.Int16(3), argv.Int16(2));
        var rect2 = default(SciRect);
        int argextra = argv.Count >= 13 ? 4 : 0; // PQ3 and SCI1.1 games pass 13 arguments
        int style = argv.Int16(5 + argextra);
        int priority = argv.Count > 6 + argextra ? argv.Int16(6 + argextra) : -1;
        int colorPen = AdjustGraphColor(s, argv.Count > 7 + argextra ? argv.Int16(7 + argextra) : 0);
        int colorBack = AdjustGraphColor(s, argv.Count > 8 + argextra ? argv.Int16(8 + argextra) : 255);

        if (argv.Count >= 13)
        {
            rect2 = new SciRect(argv.Int16(5), argv.Int16(4), argv.Int16(7), argv.Int16(6));
        }

        string? title = null;
        if (argv[4 + argextra].Segment != 0)
        {
            title = SciLanguage.StrSplit(s, s.SegMan.GetString(argv[4 + argextra]), null);
        }

        return s.Ports.KernelNewWindow(rect1, rect2, (ushort)style, priority, colorPen, colorBack, title);
    }

    public static RegT DisposeWindow(EngineState s, KernelArgs argv)
    {
        int windowId = argv.Int16(0);
        bool reanimate = argv.Count != 2 || argv[1].IsNull;
        s.Ports.KernelDisposeWindow(windowId, reanimate);
        return s.Acc;
    }

    public static RegT SetVideoMode(EngineState s, KernelArgs argv) => s.Acc; // KQ6's intro switches planar memory on and off

    public static RegT Show(EngineState s, KernelArgs argv)
    {
        switch (argv.UInt16(0))
        {
            case 1:
                s.Screen.DebugShowMap(3);
                break;
            case 2:
                s.Screen.DebugShowMap(1);
                break;
            case 3:
            case 4:
                s.Screen.DebugShowMap(2);
                break;
            default:
                s.Log.Warning("sci.vm", $"Map {argv.UInt16(0)} is not available");
                break;
        }

        return s.Acc;
    }

    public static async ValueTask<RegT> ShakeScreenAsync(EngineState s, KernelArgs argv)
    {
        int shakeCount = argv.Count > 0 ? argv.UInt16(0) : 1;
        int directions = argv.Count > 1 ? argv.UInt16(1) : 1;
        await s.Screen.KernelShakeScreen(shakeCount, directions, s.SleepTicks);
        return s.Acc;
    }
}
