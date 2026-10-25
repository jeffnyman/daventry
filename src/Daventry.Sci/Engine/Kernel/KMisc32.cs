using Daventry.Core.Hosting;

namespace Daventry.Sci.Engine;

public static partial class KernelFunctions
{
    // ---- kmisc.cpp and kmath.cpp: the SCI32 miscellany ----

    /// <summary>
    /// Reads a setting of resource.cfg. Phantasmagoria stores the results of its CPU and video
    /// benchmarks there and slows itself down below 425, so the fastest values are returned.
    /// </summary>
    public static RegT GetConfig(EngineState s, KernelArgs argv)
    {
        string setting = s.SegMan.GetString(argv[0]).ToLowerInvariant();
        var data = Selectors.ReadSelector(s, argv[1], s.Kernel.Selectors.Data);

        string value = setting switch
        {
            "videospeed" => "500",
            "cpu" => "586", // the fastest CPU that CPUID detects
            "cpuspeed" => "500",
            "language" => SciLanguage.GetSciLanguage(s).ToString(),
            "torindebug" => string.Empty, // the debug mode of Torin's Passage (French)
            "leakdump" => string.Empty, // an unknown LSL7 debugging setting
            "startroom" => string.Empty, // the LSL7 debug room to start in
            "game" => GetHoyle5GameSetting(s),
            "laptop" or "jumpto" or "klonchtsee" or "klonchtarr" => string.Empty, // Hoyle 5 startup
            "deflang" => string.Empty, // the four language Mother Goose Deluxe
            _ => throw new SciVmException($"GetConfig: Unknown configuration setting {setting}"),
        };

        s.SegMan.Strcpy(data, value);
        return argv[1];
    }

    /// <summary>Hoyle 5 Bridge ships only Bridge (script 700) and expects its 700.cfg setting.</summary>
    private static string GetHoyle5GameSetting(EngineState s)
    {
        if (s.GameId == SciGameId.Hoyle5
            && s.ResMan.TestResource(new Resources.ResourceId(Resources.ResourceType.Script, 100)) is null
            && s.ResMan.TestResource(new Resources.ResourceId(Resources.ResourceType.Script, 700)) is not null)
        {
            return "700";
        }

        return string.Empty;
    }

    /// <summary>Modelled on Windows' GetPrivateProfileInt; only the fake video speed is answered.</summary>
    public static RegT GetSierraProfileInt(EngineState s, KernelArgs argv)
    {
        if (s.Platform != SciPlatform.Windows)
        {
            return s.Acc;
        }

        string category = s.SegMan.GetString(argv[0]).ToLowerInvariant(); // always "config"
        string setting = s.SegMan.GetString(argv[1]).ToLowerInvariant();
        if (category == "config" && setting == "videospeed")
        {
            return RegT.Make(0, 500);
        }

        // the third parameter is the default when the key is missing
        s.Log.Warning("sci.vm", $"kGetSierraProfileInt: Returning default value {argv.Int16(2)} for unknown setting {category}.{setting}");
        return argv[2];
    }

    public static RegT GetWindowsOption(EngineState s, KernelArgs argv)
    {
        int windowsOption = argv.UInt16(0);
        switch (windowsOption)
        {
            case 0:
                // the title bar of Phantasmagoria: off
                return RegT.Null;
            default:
                s.Log.Warning("sci.vm", $"GetWindowsOption: Unknown option {windowsOption}");
                return RegT.Null;
        }
    }

    /// <summary>A debug function the Shivers scripts call with a format string and arguments.</summary>
    public static RegT PrintDebug(EngineState s, KernelArgs argv)
    {
        string debugString = s.SegMan.GetString(argv[0]);
        s.Log.Debug("sci.game", Format32(s, debugString, SliceArgs(argv, 1)));
        return s.Acc;
    }

    public static RegT Platform32(EngineState s, KernelArgs argv)
    {
        const int GetPlatform = 0;
        const int GetCdSpeed = 1;
        const int GetColorDepth = 2;
        const int GetCdDrive = 3;
        const int PlatformDos = 1;
        const int PlatformWindows = 2;

        int operation;
        if (s.Version < SciVersion.V2_1Middle)
        {
            if (argv.Count == 0 || argv.Int16(0) == 0)
            {
                operation = GetPlatform;
            }
            else
            {
                return RegT.Null;
            }
        }
        else
        {
            operation = argv.Count > 0 ? argv.Int16(0) : GetPlatform;
        }

        switch (operation)
        {
            case GetPlatform:
                return s.Platform switch
                {
                    SciPlatform.Dos => RegT.Make(0, PlatformDos),
                    SciPlatform.Windows => RegT.Make(0, PlatformWindows),
                    // the SCI32 Macintosh interpreters claimed to be DOS; GK1 relies on it for its view based slide shows
                    SciPlatform.Macintosh => RegT.Make(0, PlatformDos),
                    _ => throw new SciVmException($"Unknown platform {s.Platform}"),
                };
            case GetColorDepth:
                return RegT.Make(0, s.GameId == SciGameId.Phantasmagoria2 ? 3 /* 16-bit colour */ : 2 /* 256 colours */);
            case GetCdSpeed:
                // 4 is what RAMA's resource.cfg holds under DOSBox, the highest speed the scripts expect
                return RegT.Make(0, 4);
            case GetCdDrive:
            default:
                return RegT.Null;
        }
    }

    public static RegT WebConnect(EngineState s, KernelArgs argv)
    {
        string gameUrl = argv.Count > 0 ? s.SegMan.GetString(argv[0]) : "http://www.sierra.com";
        s.Log.Info("sci.vm", $"kWebConnect: the game asked to open {gameUrl}");
        return RegT.Null;
    }

    public static RegT WinExec(EngineState s, KernelArgs argv) => RegT.Null;

    public static RegT WinDLL(EngineState s, KernelArgs argv)
    {
        int operation = argv.UInt16(0);
        string dllName = s.SegMan.GetString(argv[1]);
        switch (operation)
        {
            case 0: // load DLL
                return RegT.Make(0, 1000); // a fake handle, as LoadLibrary would return
            case 1: // free DLL
                return RegT.True;
            case 2: // call a DLL function
                // PENGIN16.DLL is the poker engine of Hoyle 5, which is not ported
                throw new SciVmException($"kWinDLL: Unknown DLL to invoke: {dllName}");
            default:
                return RegT.Null;
        }
    }

    public static RegT MulDiv(EngineState s, KernelArgs argv)
    {
        int multiplicant = argv.Int16(0);
        int multiplier = argv.Int16(1);
        int denominator = argv.Int16(2);
        if (denominator == 0)
        {
            throw new SciVmException($"kMulDiv: attempt to divide by zero ({multiplicant} * {multiplier} / {denominator})");
        }

        int result = (Math.Abs(multiplicant * multiplier) + Math.Abs(denominator) / 2) / Math.Abs(denominator);
        if (multiplicant != 0 && (multiplicant / Math.Abs(multiplicant)) * multiplier * denominator < 0)
        {
            result = -result;
        }

        return RegT.Make(0, (short)result);
    }

    /// <summary>
    /// Mother Goose Deluxe switches its audio directory between languages; a single audio set is
    /// served. The empty name is the game folder itself, the English audio, so it needs nothing.
    /// </summary>
    public static RegT SetLanguage(EngineState s, KernelArgs argv)
    {
        string directory = s.SegMan.GetString(argv[0]);
        if (directory.Length > 0)
        {
            s.Log.Warning("sci.vm", $"kSetLanguage: audio directory '{directory}' requested; alternate audio directories are not supported");
        }

        return s.Acc;
    }

    public static RegT WinHelp(EngineState s, KernelArgs argv)
    {
        switch (argv.UInt16(0))
        {
            case 1:
                s.Log.Info("sci.vm", $"The game asked for its help file {s.SegMan.GetString(argv[1])}; open it with an external viewer");
                break;
            case 2:
                // an init function
                break;
            default:
                s.Log.Warning("sci.vm", $"Unknown kWinHelp subop {argv.UInt16(0)}");
                break;
        }

        return s.Acc;
    }
}
