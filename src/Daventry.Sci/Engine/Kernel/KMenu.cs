namespace Daventry.Sci.Engine;

public static partial class KernelFunctions
{
    // ---- kmenu.cpp ----

    public static RegT AddMenu(EngineState s, KernelArgs argv)
    {
        string title = s.SegMan.GetString(argv[0]);
        string content = s.SegMan.GetString(argv[1]);
        s.Menu.KernelAddEntry(title, content, argv[1]);
        return s.Acc;
    }

    public static RegT SetMenu(EngineState s, KernelArgs argv)
    {
        int menuId = argv.UInt16(0) >> 8;
        int itemId = argv.UInt16(0) & 0xFF;
        int argPos = 1;
        while (argPos < argv.Count)
        {
            int attributeId = argv.UInt16(argPos);
            // the fan game Cascade Quest passes an odd number of arguments when loading (bug #5118)
            var value = argPos + 1 < argv.Count ? argv[argPos + 1] : RegT.Null;
            s.Menu.KernelSetAttribute(menuId, itemId, attributeId, value);
            argPos += 2;
        }

        return s.Acc;
    }

    public static RegT GetMenu(EngineState s, KernelArgs argv)
    {
        int menuId = argv.UInt16(0) >> 8;
        int itemId = argv.UInt16(0) & 0xFF;
        return s.Menu.KernelGetAttribute(menuId, itemId, argv.UInt16(1));
    }

    public static RegT DrawStatus(EngineState s, KernelArgs argv)
    {
        var textReference = argv[0];
        int colorPen = argv.Count > 1 ? argv.Int16(1) : 0;
        int colorBack = argv.Count > 2 ? argv.Int16(2) : s.Screen.ColorWhite;

        // sometimes this is called without text; then nothing is drawn
        if (!textReference.IsNull)
        {
            string text = s.SegMan.GetString(textReference);
            if (text == "Replaying sound")
            {
                return s.Acc; // the fan game Cascade Quest when loading
            }

            s.Menu.KernelDrawStatus(SciLanguage.StrSplit(s, text, null), colorPen, colorBack);
        }

        return s.Acc;
    }

    public static RegT DrawMenuBar(EngineState s, KernelArgs argv)
    {
        s.Menu.KernelDrawMenuBar(argv[0].IsNull);
        return s.Acc;
    }

    public static async ValueTask<RegT> MenuSelectAsync(EngineState s, KernelArgs argv)
    {
        bool pauseSound = argv.Count <= 1 || !argv[1].IsNull;
        return await s.Menu.KernelSelect(argv[0], pauseSound);
    }
}
