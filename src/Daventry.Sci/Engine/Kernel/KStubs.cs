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

    public static ValueTask<RegT> FileIOAsync(EngineState s, KernelArgs argv) => new(s.Acc);

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

    // ---- controls16.cpp and menu.cpp (not ported) ----

    private static readonly KernelFunction s_drawMenuBar = NotPorted("DrawMenuBar");
    private static readonly KernelFunction s_menuSelect = NotPorted("MenuSelect", returnNull: true);

    public static RegT DrawMenuBar(EngineState s, KernelArgs argv) => s_drawMenuBar(s, argv).Result;

    public static RegT AddMenu(EngineState s, KernelArgs argv) => s.Acc;

    public static RegT SetMenu(EngineState s, KernelArgs argv) => s.Acc;

    public static RegT GetMenu(EngineState s, KernelArgs argv) => RegT.Null;

    public static RegT MenuSelect(EngineState s, KernelArgs argv) => s_menuSelect(s, argv).Result;

    // ---- parser (not ported): nothing is ever understood ----

    public static RegT Parse(EngineState s, KernelArgs argv)
    {
        var sel = s.Kernel.Selectors;
        Selectors.WriteSelectorValue(s, argv[1], sel.Claimed, 1);
        return RegT.Null;
    }

    public static RegT Said(EngineState s, KernelArgs argv) => RegT.Null;

    public static RegT SetSynonyms(EngineState s, KernelArgs argv) => s.Acc;

    // ---- cursor.cpp (not ported) ----

    public static RegT SetCursor(EngineState s, KernelArgs argv) => s.Acc;

    public static RegT MoveCursor(EngineState s, KernelArgs argv) => s.Acc;

    // ---- message.cpp (not ported) ----

    private static readonly KernelFunction s_getMessage = NotPorted("GetMessage", returnNull: true);
    private static readonly KernelFunction s_message = NotPorted("Message", returnNull: true);

    public static RegT GetMessage(EngineState s, KernelArgs argv)
    {
        s.SegMan.Strcpy(argv[3], string.Empty);
        return s_getMessage(s, argv).Result;
    }

    public static RegT Message(EngineState s, KernelArgs argv) => s_message(s, argv).Result;

    // ---- kpathing.cpp (not ported) ----

    private static readonly KernelFunction s_avoidPath = NotPorted("AvoidPath", returnNull: true);

    public static RegT AvoidPath(EngineState s, KernelArgs argv)
    {
        // with two arguments SCI0 asks whether the target can be reached at all
        if (argv.Count == 2)
        {
            return RegT.Make(0, 1);
        }

        return s_avoidPath(s, argv).Result;
    }

    public static RegT Intersections(EngineState s, KernelArgs argv) => RegT.Null;

    public static RegT MergePoly(EngineState s, KernelArgs argv) => RegT.Null;

    // ---- kfile.cpp and save games (not ported): no file ever exists ----

    public static RegT FileIOOpen(EngineState s, KernelArgs argv) => RegT.Signal;

    public static RegT FileIOClose(EngineState s, KernelArgs argv) => s.Acc;

    public static RegT FileIOReadRaw(EngineState s, KernelArgs argv) => RegT.Null;

    public static RegT FileIOWriteRaw(EngineState s, KernelArgs argv) => RegT.Null;

    public static RegT FileIOUnlink(EngineState s, KernelArgs argv) => s.Acc;

    public static RegT FileIOReadString(EngineState s, KernelArgs argv)
    {
        s.SegMan.Strcpy(argv[0], string.Empty);
        return RegT.Null;
    }

    public static RegT FileIOWriteString(EngineState s, KernelArgs argv) => RegT.Null;

    public static RegT FileIOSeek(EngineState s, KernelArgs argv) => RegT.Null;

    public static RegT FileIOFindFirst(EngineState s, KernelArgs argv) => RegT.Null;

    public static RegT FileIOFindNext(EngineState s, KernelArgs argv) => RegT.Null;

    public static RegT FileIOExists(EngineState s, KernelArgs argv) => RegT.Null;

    public static RegT FileIORename(EngineState s, KernelArgs argv) => RegT.Signal;

    public static RegT FileIOCopy(EngineState s, KernelArgs argv) => RegT.Signal;

    public static RegT GetSaveDir(EngineState s, KernelArgs argv) => s.SegMan.SaveDirPtr;

    public static RegT GetCWD(EngineState s, KernelArgs argv)
    {
        s.SegMan.Strcpy(argv[0], "/");
        return argv[0];
    }

    public static RegT ValidPath(EngineState s, KernelArgs argv) => RegT.Make(0, 1);

    public static RegT CheckFreeSpace(EngineState s, KernelArgs argv) => RegT.Make(0, 1);

    public static RegT CheckSaveGame(EngineState s, KernelArgs argv) => RegT.Null;

    public static RegT GetSaveFiles(EngineState s, KernelArgs argv) => RegT.Null;

    public static RegT SaveGame(EngineState s, KernelArgs argv)
    {
        s.Log.Warning("sci.vm", "kSaveGame: saving is not ported yet");
        return RegT.Null;
    }

    public static RegT RestoreGame(EngineState s, KernelArgs argv)
    {
        s.Log.Warning("sci.vm", "kRestoreGame: restoring is not ported yet");
        return s.Acc;
    }

    public static RegT DeviceInfo(EngineState s, KernelArgs argv)
    {
        switch (argv.UInt16(0))
        {
            case 0: // GetDevice
                s.SegMan.Strcpy(argv[2], "/");
                break;
            case 1: // GetCurrentDevice
                s.SegMan.Strcpy(argv[1], "/");
                break;
            case 2: // PathsEqual
                return RegT.Make(0, 1);
            case 3: // IsFloppy
                return RegT.Null;
            case 5: // GetConfigPath
                s.SegMan.Strcpy(argv[1], "/");
                break;
            case 7: // GetSaveCatalogSize
            case 8: // GetSaveFileSize
                return RegT.Null;
            default:
                s.Log.Warning("sci.vm", $"kDeviceInfo: unknown sub-command {argv.UInt16(0)}");
                break;
        }

        return s.Acc;
    }

    // ---- miscellaneous ----

    public static RegT Portrait(EngineState s, KernelArgs argv) => RegT.Null;

    public static RegT ShowMovie(EngineState s, KernelArgs argv) => RegT.Null;

    public static RegT RemapColors(EngineState s, KernelArgs argv) => s.Acc;
}
