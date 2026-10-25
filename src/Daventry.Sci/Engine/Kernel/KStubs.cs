using Daventry.Core.Hosting;
using Daventry.Sci.Graphics;

namespace Daventry.Sci.Engine;

public static partial class KernelFunctions
{
    // Kernel functions whose subsystem (animate, text, controls, menus, parser, cursor, messages,
    // files, save games, pathfinding) is not ported yet. Each one does the least that keeps the
    // scripts going; the real ports replace them one by one.

    // ---- sub-function parents: the VM dispatches on the first argument, these are never called ----

    public static ValueTask<RegT> DoSoundAsync(EngineState s, KernelArgs argv) => new(s.Acc);

    public static ValueTask<RegT> GraphAsync(EngineState s, KernelArgs argv) => new(s.Acc);

    public static ValueTask<RegT> PalVaryAsync(EngineState s, KernelArgs argv) => new(s.Acc);

    public static ValueTask<RegT> PaletteAsync(EngineState s, KernelArgs argv) => new(s.Acc);

    // ---- compare.cpp backed functions ----

    public static RegT BaseSetter(EngineState s, KernelArgs argv)
    {
        s.Compare.KernelBaseSetter(argv[0]);
        return s.Acc;
    }

    public static RegT CanBeHere(EngineState s, KernelArgs argv)
    {
        var listReference = argv.Count > 1 ? argv[1] : RegT.Null;
        var canBeHere = s.Compare.KernelCanBeHere(argv[0], listReference);
        return RegT.Make(0, canBeHere.IsNull ? 1 : 0);
    }

    public static RegT CantBeHere(EngineState s, KernelArgs argv)
    {
        var listReference = argv.Count > 1 ? argv[1] : RegT.Null;
        return s.Compare.KernelCanBeHere(argv[0], listReference);
    }

    public static RegT SetNowSeen(EngineState s, KernelArgs argv)
    {
        s.Compare.KernelSetNowSeen(argv[0]);
        return s.Acc;
    }

    public static RegT IsItSkip(EngineState s, KernelArgs argv)
    {
        var position = new SciPoint(argv.UInt16(4), argv.UInt16(3));
        return RegT.Make(0, s.Compare.KernelIsItSkip(argv.Int16(0), argv.Int16(1), argv.Int16(2), position) ? 1 : 0);
    }

    public static RegT OnControl(EngineState s, KernelArgs argv)
    {
        GfxScreenMask screenMask;
        int argBase = 0;
        if (argv.Count is 2 or 4)
        {
            screenMask = GfxScreenMask.Control;
        }
        else
        {
            screenMask = (GfxScreenMask)argv.UInt16(0);
            argBase = 1;
        }

        var rect = new SciRect { Left = argv.Int16(argBase), Top = argv.Int16(argBase + 1) };
        if (argv.Count > 3)
        {
            rect.Right = argv.Int16(argBase + 2);
            rect.Bottom = argv.Int16(argBase + 3);
        }
        else
        {
            rect.Right = rect.Left + 1;
            rect.Bottom = rect.Top + 1;
        }

        return RegT.Make(0, s.Compare.KernelOnControl(screenMask, rect));
    }

    // ---- miscellaneous ----

    public static RegT Portrait(EngineState s, KernelArgs argv) => RegT.Null;

    public static RegT ShowMovie(EngineState s, KernelArgs argv) => RegT.Null;

    public static RegT RemapColors(EngineState s, KernelArgs argv) => s.Acc;
}
