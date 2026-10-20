using Daventry.Core.Hosting;
using Daventry.Sci.Graphics;

namespace Daventry.Sci.Engine;

public static partial class KernelFunctions
{
    // ---- kgraphics.cpp: animation, text and the status line ----

    public static async ValueTask<RegT> AnimateAsync(EngineState s, KernelArgs argv)
    {
        var castListReference = argv.Count > 0 ? argv[0] : RegT.Null;
        bool cycle = argv.Count > 1 && argv.UInt16(1) != 0;
        await s.Animate.KernelAnimate(castListReference, cycle, argv);

        // WORKAROUND: at the end of EcoQuest 1, during the credits, the game does not call
        // kGetEvent, so the events are polled here to keep the host responsive (bug #5495)
        if (s.GameId == SciGameId.Ecoquest && s.CurrentRoomNumber == 680)
        {
            s.Events.GetSciEvent(SciEventPeek);
        }

        return s.Acc;
    }

    public static RegT AddToPic(EngineState s, KernelArgs argv)
    {
        switch (argv.Count)
        {
            case 1:
                if (argv[0].IsNull)
                {
                    return s.Acc;
                }

                s.Animate.KernelAddToPicList(argv[0]);
                break;
            case 7:
                s.Animate.KernelAddToPicView(argv.UInt16(0), argv.Int16(1), argv.Int16(2), argv.Int16(3), argv.Int16(4), argv.Int16(5), argv.Int16(6));
                break;
            default:
                throw new SciVmException($"kAddToPic with unsupported parameter count {argv.Count}");
        }

        return s.Acc;
    }

    public static RegT Display(EngineState s, KernelArgs argv)
    {
        var textp = argv[0];
        int index = argv.Count > 1 ? argv.UInt16(1) : 0;
        string text;
        KernelArgs rest;

        if (textp.Segment != 0)
        {
            text = s.SegMan.GetString(textp);
            rest = argv.Slice(1);
        }
        else
        {
            text = s.Kernel.LookupText(textp, index);
            rest = argv.Slice(2);
        }

        string splitText = SciLanguage.StrSplitLanguage(s, text, out ushort languageSplitter, s.GameId == SciGameId.Pq2 ? "\r" : "\r----------\r");
        return s.Paint.KernelDisplay(splitText, languageSplitter, rest);
    }

    public static RegT TextSize(EngineState s, KernelArgs argv)
    {
        string text = s.SegMan.GetString(argv[1]);
        var dest = s.SegMan.DerefRegPtr(argv[0], 4);
        int maxWidth = argv.Count > 3 ? argv.UInt16(3) : 0;
        int fontNr = argv.UInt16(2);

        if (dest is null)
        {
            s.Log.Debug("sci.vm", "GetTextSize: Empty destination");
            return s.Acc;
        }

        string? sep = argv.Count > 4 && argv[4].Segment != 0 ? s.SegMan.GetString(argv[4]) : null;

        var (regs, index) = dest.Value;
        regs[index] = regs[index + 1] = RegT.Null;
        if (text.Length == 0)
        {
            regs[index + 2] = regs[index + 3] = RegT.Null;
            return s.Acc;
        }

        string splitText = SciLanguage.StrSplitLanguage(s, text, out ushort languageSplitter, sep);
        s.Text.KernelTextSize(splitText, languageSplitter, fontNr, maxWidth, out int textWidth, out int textHeight);

        // one of the German LB2 texts ends in loads of spaces, which would make a window larger
        // than the screen (bug #5710)
        if (textWidth >= s.Screen.DisplayWidth || textHeight >= s.Screen.DisplayHeight)
        {
            text = text.Trim();
            splitText = SciLanguage.StrSplitLanguage(s, text, out languageSplitter, sep);
            s.Text.KernelTextSize(splitText, languageSplitter, fontNr, maxWidth, out textWidth, out textHeight);
        }

        regs[index + 2] = RegT.Make(0, textHeight);
        regs[index + 3] = RegT.Make(0, textWidth);
        return s.Acc;
    }

    public static RegT TextFonts(EngineState s, KernelArgs argv)
    {
        s.Text.KernelTextFonts(argv);
        return s.Acc;
    }

    public static RegT TextColors(EngineState s, KernelArgs argv)
    {
        s.Text.KernelTextColors(argv);
        return s.Acc;
    }

    /// <summary>Draws the status line into the menu port. The menu itself is not ported yet.</summary>
    public static RegT DrawStatus(EngineState s, KernelArgs argv)
    {
        var textReference = argv[0];
        int colorPen = argv.Count > 1 ? argv.Int16(1) : 0;
        int colorBack = argv.Count > 2 ? argv.Int16(2) : s.Screen.ColorWhite;

        // sometimes this is called without text; then nothing is drawn
        if (textReference.IsNull)
        {
            return s.Acc;
        }

        string text = SciLanguage.StrSplit(s, s.SegMan.GetString(textReference), null);
        var ports = s.Ports;
        var paint = s.Paint;
        var oldPort = ports.SetPort(ports.MenuPort);
        paint.FillRect(ports.MenuBarRect, GfxScreenMask.Visual, (byte)colorBack, 0, 0);
        ports.PenColor(colorPen);
        ports.MoveTo(0, 1);
        s.Text.DrawStatus(text);
        paint.BitsShow(ports.MenuBarRect);
        // the line under the status bar; Dr. Brain 1 Mac draws over it with its icon bar
        paint.FillRect(ports.MenuLine, GfxScreenMask.Visual, 0, 0, 0);
        paint.BitsShow(ports.MenuLine);
        ports.SetPort(oldPort);
        return s.Acc;
    }
}
