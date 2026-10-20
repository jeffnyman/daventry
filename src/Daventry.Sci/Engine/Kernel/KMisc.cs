using System.Buffers.Binary;
using Daventry.Core.Hosting;

namespace Daventry.Sci.Engine;

public static partial class KernelFunctions
{
    // ---- kmisc.cpp ----

    public static RegT RestartGame16(EngineState s, KernelArgs argv)
    {
        s.ShrinkStackToBase();
        s.AbortScriptProcessing = AbortGameState.RestartGame;
        return RegT.Null;
    }

    /// <summary>
    /// Returns and optionally clears the restarting flag. Nearly every game calls it with 0
    /// from its game loop, which makes it the main speed throttle.
    /// </summary>
    public static async ValueTask<RegT> GameIsRestartingAsync(EngineState s, KernelArgs argv)
    {
        int previousRestartingFlag = s.GameIsRestarting;
        if (argv.Count > 0 && argv.UInt16(0) == 0)
        {
            s.GameIsRestarting = GameIsRestartingFlags.None;
        }
        else
        {
            // only game loops and script patches are throttled
            return RegT.Make(0, previousRestartingFlag);
        }

        uint neededSleep = s.SpeedThrottleDelay;

        // WORKAROUNDS for scripts that need specific throttling
        switch (s.GameId)
        {
            case SciGameId.Castlebrain:
                // the memory colour puzzle in room 100 polls its stones so fast they reset before showing
                if (s.CurrentRoomNumber == 100)
                {
                    s.ThrottleTrigger = true;
                    neededSleep = 60;
                }

                break;
            case SciGameId.Iceman:
                // the submarine control room runs far too fast without extra throttling
                if (s.CurrentRoomNumber == 27)
                {
                    s.ThrottleTrigger = true;
                    neededSleep = 60;
                }

                break;
            case SciGameId.Sq5:
                switch (s.CurrentRoomNumber)
                {
                    case 104: // the intro star field (bug #15622)
                        s.ThrottleTrigger = true;
                        neededSleep = 90;
                        break;
                    case 110: // exiting the simulator, before ego walks (bug #15610)
                    {
                        var cast = s.GetGlobal(GlobalVar.Cast);
                        int castSize = Selectors.ReadSelectorValue(s, cast, s.Kernel.Selectors.Size);
                        if (castSize != 5)
                        {
                            s.ThrottleTrigger = true;
                            neededSleep = 90;
                        }

                        break;
                    }
                }

                break;

            // SCI1.1 speed test rooms run unthrottled
            case SciGameId.Ecoquest2 when s.CurrentRoomNumber == 10:
            case SciGameId.Freddypharkas when s.CurrentRoomNumber == 28:
            case SciGameId.Gk1demo when s.CurrentRoomNumber == 17:
            case SciGameId.Kq5 when s.CurrentRoomNumber == 99:
            case SciGameId.Kq6 when s.CurrentRoomNumber == 99:
            case SciGameId.Laurabow2 when s.CurrentRoomNumber == 28:
            case SciGameId.Lsl6 when s.CurrentRoomNumber == 99:
            case SciGameId.Qfg1vga when s.CurrentRoomNumber == 299:
                s.ThrottleTrigger = false;
                break;
        }

        await s.SpeedThrottler(neededSleep);

        s.EventCounter = 0;
        s.PaletteSetIntensityCounter = 0;
        return RegT.Make(0, previousRestartingFlag);
    }

    public static RegT HaveMouse(EngineState s, KernelArgs argv) => RegT.Signal;

    public static RegT MemoryInfo(EngineState s, KernelArgs argv)
    {
        // the free heap must not be 0xFFFF or memory calculations overflow; Crazy Nick's games
        // accept up to 32746 bytes before warning about fragmentation
        const ushort size = 0x7FEA;
        return argv[0].Offset switch
        {
            0 => RegT.Make(0, size - 2), // largest heap block: FREE_HEAP - 2 avoids "Memory fragmented" dialogs
            1 or 2 or 3 or 4 => RegT.Make(0, size),
            var other => throw new SciVmException($"Unknown MemoryInfo operation: {other:x4}"),
        };
    }

    /// <summary>A 256 byte block that survives restarts and restores.</summary>
    public static RegT MemorySegment(EngineState s, KernelArgs argv)
    {
        switch (argv.UInt16(0))
        {
            case 0: // save data
            {
                if (argv.Count < 3)
                {
                    throw new SciVmException("Insufficient number of arguments passed to MemorySegment");
                }

                int size = argv.UInt16(2);
                if (size == 0)
                {
                    size = s.SegMan.Strlen(argv[1]) + 1;
                }

                if (size > EngineState.MemorySegmentMax)
                {
                    throw new SciVmException($"kMemorySegment: Requested to save more than 256 bytes ({size})");
                }

                s.MemorySegmentSize = (ushort)size;
                s.SegMan.Memcpy(s.MemorySegment.AsSpan(), argv[1], size);
                break;
            }

            case 1: // restore data
                s.SegMan.Memcpy(argv[1], s.MemorySegment.AsSpan(), s.MemorySegmentSize);
                break;
            default:
                throw new SciVmException($"Unknown MemorySegment operation {argv.UInt16(0):x4}");
        }

        return argv[1];
    }

    public static RegT FlushResources(EngineState s, KernelArgs argv)
    {
        Gc.Run(s);
        s.Log.Debug("sci.room", $"Entering room number {argv.UInt16(0)}");
        return s.Acc;
    }

    public static RegT SetDebug(EngineState s, KernelArgs argv)
    {
        s.Log.Info("sci.vm", "Debug mode requested by the scripts (no debugger available)");
        return s.Acc;
    }

    public static RegT GetTime(EngineState s, KernelArgs argv)
    {
        int mode = argv.Count > 0 ? argv.UInt16(0) : 0;
        if (s.Version == SciVersion.V0Early && mode > 1)
        {
            throw new SciVmException($"kGetTime called in SCI0 with mode {mode} (expected 0 or 1)");
        }

        var now = DateTime.Now;
        ushort retval;
        switch (mode)
        {
            case 0: // ticks
                retval = (ushort)s.TickCount;
                break;
            case 1: // 12 hour time
            {
                int hour = now.Hour % 12;
                if (hour == 0)
                {
                    hour = 12;
                }

                retval = (ushort)((hour << 12) | (now.Minute << 6) | now.Second);
                break;
            }

            case 2: // 24 hour time
                retval = (ushort)((now.Hour << 11) | (now.Minute << 5) | (now.Second >> 1));
                break;
            case 3: // date: years since 1920 for SCI0 late DOS, since 1980 otherwise
            {
                int baseYear = s.Version == SciVersion.V0Late && s.Platform == SciPlatform.Dos ? 1920 : 1980;
                retval = (ushort)(now.Day | (now.Month << 5) | (((now.Year - baseYear) & 0x7F) << 9));
                break;
            }

            default:
                throw new SciVmException($"Attempt to use unknown GetTime mode {mode}");
        }

        return RegT.Make(0, retval);
    }

    public static RegT Memory(EngineState s, KernelArgs argv)
    {
        switch (argv.UInt16(0))
        {
            case 1: // allocate critical
            case 2: // allocate non-critical
            {
                // Sierra allocated at least 2 bytes more than requested and made the size even;
                // PQ3 room 202 and LSL5 room 280 rely on the slack
                int byteCount = argv.UInt16(1);
                byteCount += 2 + (byteCount & 1);
                s.SegMan.AllocDynmem(byteCount, argv.UInt16(0) == 1 ? "kMemory() critical" : "kMemory() non-critical", out var addr);
                s.Acc = addr;
                break;
            }

            case 3: // free
                if (!s.SegMan.FreeDynmem(argv[1]) && s.GameId != SciGameId.Qfg1vga)
                {
                    s.Log.Warning("sci.vm", $"Attempt to kMemory::free() non-dynmem pointer {argv[1]}");
                }

                break;
            case 4: // memcpy
                s.SegMan.Memcpy(argv[1], argv[2], argv.UInt16(3));
                break;
            case 5: // peek
            {
                if (argv[1].Segment == 0)
                {
                    // KQ5 CD does this when interacting with certain objects
                    s.Log.Warning("sci.vm", $"Attempt to peek invalid memory at {argv[1]}");
                    return s.Acc;
                }

                var r = s.SegMan.Dereference(argv[1]);
                if (!r.IsValid || r.MaxSize < 2)
                {
                    throw new SciVmException($"Attempt to peek invalid memory at {argv[1]}");
                }

                if (r.IsRaw)
                {
                    return RegT.Make(0, (short)BinaryPrimitives.ReadUInt16LittleEndian(r.Raw.AsSpan(r.RawOffset)));
                }

                if (r.SkipByte)
                {
                    throw new SciVmException($"Attempt to peek memory at odd offset {argv[1]}");
                }

                return r.Regs![r.RegIndex];
            }

            case 6: // poke
            {
                var r = s.SegMan.Dereference(argv[1]);
                if (!r.IsValid || r.MaxSize < 2)
                {
                    throw new SciVmException($"Attempt to poke invalid memory at {argv[1]}");
                }

                if (r.IsRaw)
                {
                    if (argv[2].Segment != 0)
                    {
                        throw new SciVmException($"Attempt to poke memory reference {argv[2]} to {argv[1]}");
                    }

                    BinaryPrimitives.WriteUInt16LittleEndian(r.Raw.AsSpan(r.RawOffset), argv[2].Offset);
                }
                else
                {
                    if (r.SkipByte)
                    {
                        throw new SciVmException($"Attempt to poke memory at odd offset {argv[1]}");
                    }

                    r.Regs![r.RegIndex] = argv[2];
                }

                break;
            }
        }

        return s.Acc;
    }

    public static RegT Platform(EngineState s, KernelArgs argv)
    {
        const int platformMacintosh = 0;
        const int platformDos = 1;
        const int platformWindows = 2;

        if (argv.Count == 0)
        {
            // KQ5 CD calls this without parameters as a graphics driver check; 0 keeps the animations
            return RegT.Null;
        }

        bool isWindows = s.Platform == SciPlatform.Windows;
        int operation = argv.UInt16(0);
        switch (operation)
        {
            case 0: // unknown; the Macintosh versions do more here
            case 4: // get platform
                if (isWindows)
                {
                    return RegT.Make(0, platformWindows);
                }

                return RegT.Make(0, s.Platform == SciPlatform.Macintosh ? platformMacintosh : platformDos);
            case 5: // is small window
                return RegT.Make(0, 1);
            case 6: // is hires
            case 7: // Windows 3.11 or higher
                return RegT.Make(0, isWindows ? 1 : 0);
            default:
                throw new SciVmException($"Unsupported kPlatform operation {operation}");
        }
    }

    public static RegT KawaHacks(EngineState s, KernelArgs argv)
    {
        switch (argv.UInt16(0))
        {
            case 0: // DoAlert
                s.Log.Info("sci.vm", s.SegMan.GetString(argv[1]));
                return RegT.Null;
            case 1: // ZaWarudo: invert a palette range
            {
                int from = argv.UInt16(1);
                int to = argv.UInt16(2);
                var pal = s.Palette.SysPalette.Clone();
                for (int i = from; i <= to && i < 256; i++)
                {
                    pal.Colors[i].R = (byte)(255 - pal.Colors[i].R);
                    pal.Colors[i].G = (byte)(255 - pal.Colors[i].G);
                    pal.Colors[i].B = (byte)(255 - pal.Colors[i].B);
                }

                s.Palette.Set(pal, true);
                return RegT.Null;
            }

            case 2: // SetTitleColors, unused
                return RegT.Null;
            case 3: // IsDebug: 1 internal debugger, 2 AddMenu support, 3 both
                return RegT.Make(0, 3);
            default:
                return RegT.Null;
        }
    }

    public static RegT KawaDbugStr(EngineState s, KernelArgs argv)
    {
        s.Log.Debug("sci.vm", s.SegMan.GetString(argv[0]));
        return RegT.Null;
    }

    /// <summary>For kernel functions that scripts still call but which do nothing.</summary>
    public static RegT Empty(EngineState s, KernelArgs argv) => s.Acc;

    private static string DescribeCall(EngineState s, KernelArgs argv)
    {
        int kernelCallNr = -1;
        int kernelSubCallNr = -1;
        if (s.ExecutionStack.Count > 0)
        {
            var lastCall = s.ExecutionStack[^1];
            kernelCallNr = lastCall.DebugKernelFunction;
            kernelSubCallNr = lastCall.DebugKernelSubFunction;
        }

        string name = kernelSubCallNr == -1
            ? $"k{s.Kernel.GetKernelName(kernelCallNr)}[{kernelCallNr:x}]"
            : $"k{s.Kernel.GetKernelName(kernelCallNr, kernelSubCallNr)}[{kernelCallNr:x}:{kernelSubCallNr:x}]";
        var parts = new List<string>();
        for (int i = 0; i < argv.Count; i++)
        {
            parts.Add(argv[i].ToString());
        }

        return $"{name} invoked. Params: {argv.Count} ({string.Join(", ", parts)})";
    }

    public static RegT Stub(EngineState s, KernelArgs argv)
    {
        s.Log.Warning("sci.vm", "Dummy function " + DescribeCall(s, argv));
        return s.Acc;
    }

    public static RegT StubNull(EngineState s, KernelArgs argv)
    {
        Stub(s, argv);
        return RegT.Null;
    }

    public static RegT Dummy(EngineState s, KernelArgs argv)
    {
        throw new SciVmException("Kernel function was called, which was considered to be unused: " + DescribeCall(s, argv));
    }

    public static ValueTask<RegT> DummyAsync(EngineState s, KernelArgs argv) => new(Dummy(s, argv));

    public static ValueTask<RegT> StubAsync(EngineState s, KernelArgs argv) => new(Stub(s, argv));

    private static readonly HashSet<string> s_reportedNotPorted = [];

    /// <summary>A kernel function that is not ported yet: logged once, then ignored.</summary>
    public static KernelFunction NotPorted(string name, bool returnNull = false)
    {
        return (s, argv) =>
        {
            if (s_reportedNotPorted.Add(name))
            {
                s.Log.Warning("sci.vm", $"k{name} is not ported yet; the call is ignored ({DescribeCall(s, argv)})");
            }

            return new ValueTask<RegT>(returnNull ? RegT.Null : s.Acc);
        };
    }
}
