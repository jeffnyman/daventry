using Daventry.Sci.Graphics;

namespace Daventry.Sci.Engine;

public static partial class KernelFunctions
{
    // ---- kgraphics32.cpp: the text controls, scroll windows, lines and hot rectangles ----

    public static ValueTask<RegT> EditText(EngineState s, KernelArgs argv) => s.RequireGfx32().Controls.KernelEditText(argv[0]);

    public static ValueTask<RegT> InputText(EngineState s, KernelArgs argv) => s.RequireGfx32().Controls.KernelInputText(argv[0], argv[1], argv.Int16(2));

    public static RegT MessageBox(EngineState s, KernelArgs argv)
        => s.RequireGfx32().Controls.KernelMessageBox(s.SegMan.GetString(argv[0]), s.SegMan.GetString(argv[1]), argv.UInt16(2));

    // ---- scroll windows ----

    private static ScrollWindow ScrollWindowOf(EngineState s, RegT id) => s.RequireGfx32().Controls.GetScrollWindow(id);

    public static RegT ScrollWindowCreate(EngineState s, KernelArgs argv)
    {
        var obj = argv[0];
        int maxNumEntries = argv.UInt16(1);
        var sel = s.Kernel.Selectors;
        int borderColor = (short)Selectors.ReadSelectorValue(s, obj, sel.BorderColor);
        var alignment = (TextAlign)(short)Selectors.ReadSelectorValue(s, obj, sel.Mode);
        int fontId = (short)Selectors.ReadSelectorValue(s, obj, sel.Font);
        int backColor = (short)Selectors.ReadSelectorValue(s, obj, sel.Back);
        int foreColor = (short)Selectors.ReadSelectorValue(s, obj, sel.Fore);
        var plane = Selectors.ReadSelector(s, obj, sel.Plane);

        SciRect rect;
        if (s.Features.UsesAlternateSelectors)
        {
            rect = new SciRect(
                (short)Selectors.ReadSelectorValue(s, obj, sel.Left),
                (short)Selectors.ReadSelectorValue(s, obj, sel.Top),
                (short)Selectors.ReadSelectorValue(s, obj, sel.Right) + 1,
                (short)Selectors.ReadSelectorValue(s, obj, sel.Bottom) + 1);
        }
        else
        {
            rect = new SciRect(
                (short)Selectors.ReadSelectorValue(s, obj, sel.NsLeft),
                (short)Selectors.ReadSelectorValue(s, obj, sel.NsTop),
                (short)Selectors.ReadSelectorValue(s, obj, sel.NsRight) + 1,
                (short)Selectors.ReadSelectorValue(s, obj, sel.NsBottom) + 1);
        }

        var position = new SciPoint(rect.Left, rect.Top);
        return s.RequireGfx32().Controls.MakeScrollWindow(rect, position, plane, (byte)foreColor, (byte)backColor, fontId, alignment, borderColor, maxNumEntries);
    }

    public static RegT ScrollWindowAdd(EngineState s, KernelArgs argv)
    {
        var scrollWindow = ScrollWindowOf(s, argv[0]);
        string text = s.SegMan.GetString(argv[1]);
        int fontId = argv.Int16(2);
        int color = argv.Int16(3);
        var alignment = (TextAlign)argv.Int16(4);
        bool scrollTo = argv.Count <= 5 || argv.UInt16(5) != 0;
        return scrollWindow.Add(text, fontId, color, alignment, scrollTo);
    }

    public static RegT ScrollWindowWhere(EngineState s, KernelArgs argv)
    {
        var scrollWindow = ScrollWindowOf(s, argv[0]);
        int where = (argv.UInt16(1) * scrollWindow.Where()).ToInt();
        return RegT.Make(0, where);
    }

    public static RegT ScrollWindowGo(EngineState s, KernelArgs argv)
    {
        ScrollWindowOf(s, argv[0]).Go(new Ratio(argv.Int16(1), argv.Int16(2)));
        return s.Acc;
    }

    public static RegT ScrollWindowModify(EngineState s, KernelArgs argv)
    {
        var scrollWindow = ScrollWindowOf(s, argv[0]);
        var entryId = argv[1];
        string newText = s.SegMan.GetString(argv[2]);
        int fontId = argv.Int16(3);
        int color = argv.Int16(4);
        var alignment = (TextAlign)argv.Int16(5);
        bool scrollTo = argv.Count <= 6 || argv.UInt16(6) != 0;
        return scrollWindow.Modify(entryId, newText, fontId, color, alignment, scrollTo);
    }

    public static RegT ScrollWindowHide(EngineState s, KernelArgs argv)
    {
        ScrollWindowOf(s, argv[0]).Hide();
        return s.Acc;
    }

    public static RegT ScrollWindowShow(EngineState s, KernelArgs argv)
    {
        ScrollWindowOf(s, argv[0]).Show();
        return s.Acc;
    }

    public static RegT ScrollWindowPageUp(EngineState s, KernelArgs argv)
    {
        ScrollWindowOf(s, argv[0]).PageUp();
        return s.Acc;
    }

    public static RegT ScrollWindowPageDown(EngineState s, KernelArgs argv)
    {
        ScrollWindowOf(s, argv[0]).PageDown();
        return s.Acc;
    }

    public static RegT ScrollWindowUpArrow(EngineState s, KernelArgs argv)
    {
        ScrollWindowOf(s, argv[0]).UpArrow();
        return s.Acc;
    }

    public static RegT ScrollWindowDownArrow(EngineState s, KernelArgs argv)
    {
        ScrollWindowOf(s, argv[0]).DownArrow();
        return s.Acc;
    }

    public static RegT ScrollWindowHome(EngineState s, KernelArgs argv)
    {
        ScrollWindowOf(s, argv[0]).Home();
        return s.Acc;
    }

    public static RegT ScrollWindowEnd(EngineState s, KernelArgs argv)
    {
        ScrollWindowOf(s, argv[0]).End();
        return s.Acc;
    }

    public static RegT ScrollWindowDestroy(EngineState s, KernelArgs argv)
    {
        s.RequireGfx32().Controls.DestroyScrollWindow(argv[0]);
        return s.Acc;
    }

    // ---- lines ----

    public static RegT AddLine(EngineState s, KernelArgs argv)
    {
        var plane = argv[0];
        var startPoint = new SciPoint(argv.Int16(1), argv.Int16(2));
        var endPoint = new SciPoint(argv.Int16(3), argv.Int16(4));
        int priority;
        byte color;
        LineStyle style;
        ushort pattern;
        byte thickness;
        if (argv.Count == 10)
        {
            priority = argv.Int16(5);
            color = (byte)argv.UInt16(6);
            style = (LineStyle)argv.Int16(7);
            pattern = argv.UInt16(8);
            thickness = (byte)argv.UInt16(9);
        }
        else
        {
            priority = 1000;
            color = 255;
            style = LineStyle.Solid;
            pattern = 0;
            thickness = 1;
        }

        return s.RequireGfx32().Paint.KernelAddLine(plane, startPoint, endPoint, priority, color, style, pattern, thickness);
    }

    public static RegT UpdateLine(EngineState s, KernelArgs argv)
    {
        var screenItemObject = argv[0];
        var planeObject = argv[1];
        var startPoint = new SciPoint(argv.Int16(2), argv.Int16(3));
        var endPoint = new SciPoint(argv.Int16(4), argv.Int16(5));

        var g = s.RequireGfx32();
        var plane = g.Frameout.Planes.FindByObject(planeObject)
            ?? throw new SciVmException($"kUpdateLine: Plane {planeObject} not found");
        var screenItem = plane.ScreenItemList.FindByObject(screenItemObject)
            ?? throw new SciVmException($"kUpdateLine: Screen item {screenItemObject} not found");

        int priority;
        byte color;
        LineStyle style;
        ushort pattern;
        byte thickness;
        if (argv.Count == 11)
        {
            priority = argv.Int16(6);
            color = (byte)argv.UInt16(7);
            style = (LineStyle)argv.Int16(8);
            pattern = argv.UInt16(9);
            thickness = (byte)argv.UInt16(10);
        }
        else
        {
            priority = screenItem.Priority;
            color = screenItem.CelInfo.Color;
            style = LineStyle.Solid;
            pattern = 0;
            thickness = 1;
        }

        g.Paint.KernelUpdateLine(screenItem, plane, startPoint, endPoint, priority, color, style, pattern, thickness);
        return s.Acc;
    }

    public static RegT DeleteLine(EngineState s, KernelArgs argv)
    {
        s.RequireGfx32().Paint.KernelDeleteLine(argv[0], argv[1]);
        return s.Acc;
    }

    // ---- hot rectangles ----

    public static RegT SetHotRectangles(EngineState s, KernelArgs argv)
    {
        if (argv.Count == 1)
        {
            s.Events.HotRectanglesActive = argv.UInt16(0) != 0;
            return s.Acc;
        }

        int numRects = argv.Int16(0);
        var hotRects = s.SegMan.LookupArray(argv[1]);
        var rects = new List<SciRect>(Math.Max(numRects, 0));
        for (int i = 0; i < numRects; i++)
        {
            rects.Add(new SciRect(
                hotRects.GetAsInt16(i * 4),
                hotRects.GetAsInt16(i * 4 + 1),
                hotRects.GetAsInt16(i * 4 + 2) + 1,
                hotRects.GetAsInt16(i * 4 + 3) + 1));
        }

        s.Events.HotRectanglesActive = true;
        s.Events.SetHotRectangles(rects);
        return s.Acc;
    }
}
