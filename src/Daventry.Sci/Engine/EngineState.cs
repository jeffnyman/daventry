using Daventry.Core.Hosting;
using Daventry.Sci.Graphics;
using Daventry.Core.Storage;
using Daventry.Sci.Resources;
using Daventry.Sci.Sound;

namespace Daventry.Sci.Engine;

/// <summary>Loop and cel counts of SCI32 views, as kNumLoops and kNumCels report them.</summary>
public interface ICelObjViewInfo
{
    int GetNumLoops(int viewId);

    int GetNumCels(int viewId, int loopNo);
}

public enum ExecStackType
{
    Call = 0,
    Kernel = 1,
    VarSelector = 2,
}

public enum AbortGameState
{
    None = 0,
    LoadGame = 1,
    RestartGame = 2,
    QuitGame = 3,
}

public static class GameIsRestartingFlags
{
    public const int None = 0;
    public const int Restart = 1;
    public const int Restore = 2;
}

/// <summary>A reference to an object's property: the object and the index into its variables.</summary>
public struct ObjVarRef
{
    public RegT Obj;
    public int VarIndex;
}

/// <summary>One frame of the VM's call stack. Stack pointers are indexes into <see cref="EngineState.Stack"/>.</summary>
public sealed class ExecStack
{
    /// <summary>Sp value meaning "use the predecessor's stack pointer" (CALL_SP_CARRY).</summary>
    public const int SpCarry = -1;

    public ExecStack(RegT objp, RegT sendp, int sp, int argc, int argp, ushort localsSegment, RegT pc, int debugSelector,
        int debugKernelFunction, int debugKernelSubFunction, int debugExportId, int debugLocalCallOffset, int debugOrigin, ExecStackType type)
    {
        ObjP = objp;
        SendP = sendp;
        Pc = pc;
        Fp = Sp = sp;
        Argc = argc;
        ArgP = argp;
        TempCount = 0;
        LocalSegment = localsSegment != RegT.UninitializedSegment ? localsSegment : pc.Segment;
        DebugSelector = debugSelector;
        DebugKernelFunction = debugKernelFunction;
        DebugKernelSubFunction = debugKernelSubFunction;
        DebugExportId = debugExportId;
        DebugLocalCallOffset = debugLocalCallOffset;
        DebugOrigin = debugOrigin;
        Type = type;
    }

    public RegT ObjP; // the current object
    public RegT SendP; // the object containing the invoked method
    public RegT Pc; // program counter (call frames)
    public ObjVarRef VarP; // the variable (varselector frames)
    public int Fp; // frame pointer
    public int Sp; // stack pointer
    public int Argc;
    public int ArgP; // index of the argc slot; arguments follow
    public int TempCount; // temp variables allocated by op_link
    public ushort LocalSegment;
    public int DebugSelector;
    public int DebugExportId;
    public int DebugLocalCallOffset;
    public int DebugOrigin;
    public int DebugKernelFunction;
    public int DebugKernelSubFunction;
    public ExecStackType Type;
}

/// <summary>Where a call came from, for workaround matching and error messages. Mirrors SciCallOrigin.</summary>
public sealed record SciCallOrigin(int ScriptNr, string ObjectName, string MethodName, int LocalCallOffset, int RoomNr)
{
    public override string ToString() => $"method {ObjectName}::{MethodName} (room {RoomNr}, script {ScriptNr}, localCall {LocalCallOffset:x})";
}

/// <summary>
/// Everything the interpreter works on: the segment manager, registers, the value and execution
/// stacks, and the engine services the kernel functions reach for. Mirrors ScummVM's EngineState
/// plus the parts of SciEngine that kernel code accessed through g_sci.
/// </summary>
public sealed class EngineState
{
    public const int VmStackSize = 0x1000;
    public const int GcInterval = 0x8000;
    public const int MemorySegmentMax = 256;

    public const int VarGlobal = 0;
    public const int VarLocal = 1;
    public const int VarTemp = 2;
    public const int VarParam = 3;

    public EngineState(SegManager segMan, ResourceManager resMan, Kernel kernel, SciVersion version, SciGameId gameId, ILog log)
    {
        SegMan = segMan;
        ResMan = resMan;
        Kernel = kernel;
        Version = version;
        GameId = gameId;
        Log = log;
        Reset(false);
    }

    // ---- services ----

    public SegManager SegMan { get; }

    public ResourceManager ResMan { get; }

    public Kernel Kernel { get; }

    public GameFeatures Features { get; set; } = null!;

    public SciVersion Version { get; }

    public SciGameId GameId { get; }

    public SciPlatform Platform { get; set; } = SciPlatform.Dos;

    public bool IsCd { get; set; }

    public bool IsDemo { get; set; }

    public string? Language { get; set; }

    public ILog Log { get; }

    /// <summary>
    /// Logs every kernel call. DAVENTRY_TRACE_KERNEL=1 sends them to the debug log;
    /// DAVENTRY_TRACE_FILE=path writes them to a file, which barely slows the interpreter down.
    /// </summary>
    public bool TraceKernelCalls { get; set; } = Environment.GetEnvironmentVariable("DAVENTRY_TRACE_KERNEL") == "1" || Environment.GetEnvironmentVariable("DAVENTRY_TRACE_FILE") is not null;

    private TextWriter? _traceWriter;

    public void TraceKernelCall(string text)
    {
        string? file = Environment.GetEnvironmentVariable("DAVENTRY_TRACE_FILE");
        if (file is null)
        {
            Log.Debug("sci.trace", text);
            return;
        }

        _traceWriter ??= new StreamWriter(file, false) { AutoFlush = false };
        _traceWriter.WriteLine(text);
    }

    public void CloseTrace()
    {
        _traceWriter?.Flush();
        _traceWriter?.Dispose();
        _traceWriter = null;
    }

    public Random Rng { get; set; } = new();

    public RegT GameObjectAddress { get; set; }

    /// <summary>Operand formats of the opcodes, adjusted for the lofs type.</summary>
    public OpcodeFormat[][] OpcodeFormats { get; set; } = Vm.BaseOpcodeFormats;

    /// <summary>The wall clock when the game started; kGetTime's time of day is this plus the engine time.</summary>
    public DateTime ClockStart { get; set; } = DateTime.Now;

    /// <summary>Milliseconds of engine time, from the host scheduler.</summary>
    public Func<uint> Millis { get; set; } = () => (uint)Environment.TickCount64;

    /// <summary>Suspends the interpreter for a number of milliseconds while the host keeps ticking.</summary>
    public Func<int, ValueTask> SleepMs { get; set; } = _ => default;

    public Func<bool> ShouldQuit { get; set; } = () => false;

    /// <summary>kGameIsRestarting throttle in milliseconds (kSpeedThrottleDefaultDelay).</summary>
    public uint SpeedThrottleDelay { get; set; } = 30;

    /// <summary>Input events translated for the scripts.</summary>
    public SciEventManager Events { get; set; } = null!;

    // hooks into subsystems that are ported later; null means the subsystem is absent
    public Action? RefreshCursorPosition { get; set; }

    public Action? InvalidateParserEvent { get; set; }

    public Action? UpdateSci0SoundCues { get; set; }

    public Func<bool>? IsFastCastEnabled { get; set; }

    // graphics, set by the engine once initialised
    public GfxScreen Screen { get; set; } = null!;

    public GfxPalette16 Palette { get; set; } = null!;

    public GfxPorts Ports { get; set; } = null!;

    public GfxCache Cache { get; set; } = null!;

    public GfxPaint16 Paint { get; set; } = null!;

    public GfxCompare Compare { get; set; } = null!;

    public GfxText16 Text { get; set; } = null!;

    public GfxTransitions Transitions { get; set; } = null!;

    public GfxAnimate Animate { get; set; } = null!;

    public GfxControls16 Controls { get; set; } = null!;

    public GfxMenu Menu { get; set; } = null!;

    /// <summary>The parser's vocabulary; null when the game has no parser.</summary>
    public Parser.Vocabulary? Vocabulary { get; set; }

    public Parser.Said Said { get; set; } = null!;

    public MessageState Message { get; set; } = null!;

    public GfxCursor Cursor { get; set; } = null!;

    /// <summary>The kDoSound commands and the music engine behind them (soundcmd.cpp).</summary>
    public SoundCommandParser Sound { get; set; } = null!;

    /// <summary>The kDoAudio player (speech and digital effects of the CD games).</summary>
    public AudioPlayer Audio { get; set; } = null!;

    /// <summary>The SCI32 digital audio mixer; null before SCI2.</summary>
    public Audio32? Audio32 { get; set; }

    /// <summary>The kDoSync lip sync state.</summary>
    public SciSync Sync { get; set; } = null!;

    /// <summary>Where saved games live; null when the host has none.</summary>
    public ISaveStore? Saves { get; set; }

    /// <summary>The prefix of this game's saved game names ("kq5."), followed by the three-digit slot.</summary>
    public string SaveNamePrefix { get; set; } = string.Empty;

    /// <summary>A slot to restore at the next safe point (kGameIsRestarting or kGetEvent), or -1. Mirrors _delayedRestoreGameId.</summary>
    public int DelayedRestoreGameId { get; set; } = -1;

    /// <summary>True while the host's restore runs through the game's own restore method (SCI32).</summary>
    public bool RestoringFromLauncher { get; set; }

    /// <summary>The slot of the last successful restore, or -1.</summary>
    public int LastRestoredSaveId { get; set; } = -1;

    // ---- non-VM information ----

    public uint LastWaitTime;
    public uint ScreenUpdateTime;
    public uint EventCounter;
    public uint PaletteSetIntensityCounter;
    public uint ThrottleLastTime;
    public bool ThrottleTrigger;
    public int ChosenQfGImportItem;

    /// <summary>SCI32: answers kNumLoops and kNumCels from the view resources; null until the SCI32 graphics are up.</summary>
    public ICelObjViewInfo? CelObjViewInfo { get; set; }

    /// <summary>The SCI32 graphics subsystems; null for SCI16 games.</summary>
    public Sci32Graphics? Gfx32 { get; set; }

    /// <summary>The SCI32 graphics, or an error for a kernel call that needs them before they exist.</summary>
    public Sci32Graphics RequireGfx32() => Gfx32 ?? throw new SciVmException("The SCI32 graphics are not initialised");

    /// <summary>The disc the SCI32 multi-disc games believe is in the drive; all resources are on one volume here.</summary>
    public int CurrentDiscNo = 1;

    /// <summary>Whether the current room is a Quest for Glory character import screen (QFG2 room 805, QFG3 and QFG4 room 54).</summary>
    public bool InQfgImportRoom => GameId switch
    {
        SciGameId.Qfg2 => CurrentRoomNumber == 805,
        SciGameId.Qfg3 or SciGameId.Qfg4 => CurrentRoomNumber == 54,
        _ => false,
    };

    // ---- VM information ----

    public List<ExecStack> ExecutionStack { get; } = [];

    /// <summary>Stack base of the VM run started from a kernel function; recursive runs share the stack.</summary>
    public int ExecutionStackBase;
    public bool ExecutionStackPosChanged;

    public RegT Acc;
    public RegT Prev;
    public short Rest;

    /// <summary>The value stack. Index 0 is stack_base; <see cref="Stack"/>.Length is stack_top.</summary>
    public RegT[] Stack = [];

    public ExecStack? Xs;

    /// <summary>Variable arrays for global, local, temp and param, with the index of the first variable.</summary>
    public RegT[]?[] VarArray = new RegT[]?[4];
    public int[] VarIndex = new int[4];
    public ushort[] VariablesSegment = new ushort[4];
    public int[] VariablesMax = new int[4];

    public AbortGameState AbortScriptProcessing;
    public int GameIsRestarting;
    public int ScriptStepCounter;

    /// <summary>The step counter when the interpreter last waited; set by the host's sleep.</summary>
    public int LastSleepStep;
    public int ScriptGcInterval;
    public int GcCountDown;

    public ushort MemorySegmentSize;
    public byte[] MemorySegment = new byte[MemorySegmentMax];

    public int LastSaveVirtualId;
    public int LastSaveNewId;

    public void Reset(bool isRestoring)
    {
        if (!isRestoring)
        {
            MemorySegmentSize = 0;
            AbortScriptProcessing = AbortGameState.None;
        }

        DelayedRestoreGameId = -1;

        ExecutionStackBase = 0;
        ExecutionStackPosChanged = false;
        Acc = RegT.Null;
        Prev = RegT.Null;
        Rest = 0;
        LastWaitTime = 0;
        GcCountDown = 0;
        EventCounter = 0;
        PaletteSetIntensityCounter = 0;
        ThrottleLastTime = 0;
        ThrottleTrigger = false;
        LastSaveVirtualId = 100;
        LastSaveNewId = 0;
        ChosenQfGImportItem = 0;
        ScriptStepCounter = 0;
        ScriptGcInterval = GcInterval;
    }

    public async ValueTask SpeedThrottler(uint neededSleep)
    {
        if (!ThrottleTrigger)
        {
            return;
        }

        uint curTime = Millis();
        uint duration = curTime - ThrottleLastTime;
        if (duration < neededSleep)
        {
            await SleepMs((int)(neededSleep - duration));
            ThrottleLastTime = Millis();
        }
        else
        {
            ThrottleLastTime = curTime;
        }

        ThrottleTrigger = false;
    }

    /// <summary>Sleeps until the given number of ticks passed since the last wait. Returns the ticks elapsed.</summary>
    public async ValueTask<ushort> Wait(ushort ticks)
    {
        uint time = Millis();
        uint ms = (uint)(ticks * 1000 / 60);
        uint duration = time - LastWaitTime;
        if (ms > duration)
        {
            uint sleepTime = ms - duration;
            await SleepMs((int)sleepTime);
            time += sleepTime;
        }

        ushort tickDelta = (ushort)(((long)time - LastWaitTime) * 60 / 1000);
        LastWaitTime = time;
        return tickDelta;
    }

    public ValueTask SleepTicks(int ticks) => SleepMs(ticks * 1000 / 60);

    public uint TickCount => (uint)(Millis() * 60 / 1000);

    public void InitGlobals()
    {
        var script000 = SegMan.GetScript(1);
        if (script000.LocalsCount == 0)
        {
            throw new SciVmException("Script 0 has no locals block");
        }

        VariablesSegment[VarGlobal] = script000.LocalsSegment;
        VarArray[VarGlobal] = script000.LocalsBlock!.Locals;
        VarIndex[VarGlobal] = 0;
        VariablesMax[VarGlobal] = script000.LocalsCount;
    }

    public RegT GetGlobal(int index) => VarArray[VarGlobal]![index];

    public void SetGlobal(int index, RegT value) => VarArray[VarGlobal]![index] = value;

    public ushort CurrentRoomNumber => GetGlobal(GlobalVar.NewRoomNo).ToUInt16();

    public void SetRoomNumber(ushort roomNumber) => SetGlobal(GlobalVar.NewRoomNo, RegT.Make(0, roomNumber));

    public void ShrinkStackToBase()
    {
        if (ExecutionStack.Count > 0)
        {
            int size = ExecutionStackBase + 1;
            if (ExecutionStack.Count > size)
            {
                ExecutionStack.RemoveRange(size, ExecutionStack.Count - size);
            }
        }
    }

    /// <summary>The origin of the current call. Must match the workaround tables exactly.</summary>
    public SciCallOrigin GetCurrentCallOrigin()
    {
        var xs = Xs ?? ExecutionStack[^1];
        string curObjectName = SegMan.GetObjectName(xs.SendP);
        string curMethodName = string.Empty;
        var localScript = SegMan.GetScriptIfLoaded(xs.LocalSegment)
            ?? throw new SciVmException($"current script not found at: {xs.LocalSegment:x4}");
        int curScriptNr = localScript.ScriptNumber;

        int debugSelector = xs.DebugSelector;
        int debugExportId = xs.DebugExportId;

        if (xs.DebugLocalCallOffset != -1)
        {
            // the last call was a local call: search back for a real call
            for (int i = ExecutionStack.Count - 1; i >= 0; i--)
            {
                var loopCall = ExecutionStack[i];
                if (loopCall.DebugSelector != -1 || loopCall.DebugExportId != -1)
                {
                    debugSelector = loopCall.DebugSelector;
                    debugExportId = loopCall.DebugExportId;
                    break;
                }
            }
        }

        if (xs.Type == ExecStackType.Call)
        {
            if (debugSelector != -1)
            {
                curMethodName = Kernel.GetSelectorName(debugSelector);
            }
            else if (debugExportId != -1)
            {
                curObjectName = string.Empty;
                curMethodName = $"export {debugExportId}";
            }
        }

        return new SciCallOrigin(curScriptNr, curObjectName, curMethodName, xs.DebugLocalCallOffset, CurrentRoomNumber);
    }

    public bool CallInStack(RegT obj, int selector)
    {
        foreach (var call in ExecutionStack)
        {
            if (call.SendP == obj && call.DebugSelector == selector)
            {
                return true;
            }
        }

        return false;
    }

    public string GetGameVersionFromGlobal()
    {
        // the version global was 28, then became 27 (27 was a volume level before)
        var versionRef = GetGlobal(GlobalVar.VersionNew);
        if (versionRef.IsNumber)
        {
            versionRef = GetGlobal(GlobalVar.VersionOld);
        }

        return versionRef.IsPointer ? SegMan.GetString(versionRef) : string.Empty;
    }

    /// <summary>A description of the current call for error messages.</summary>
    public string DescribeLocation()
    {
        if (ExecutionStack.Count == 0)
        {
            return "before the VM started";
        }

        var call = ExecutionStack[^1];
        var pc = call.Pc;
        string function = string.Empty;
        switch (call.Type)
        {
            case ExecStackType.Call:
                if (call.DebugSelector != -1)
                {
                    function = $"{SegMan.GetObjectName(call.SendP)}::{Kernel.GetSelectorName(call.DebugSelector)}";
                }
                else if (call.DebugExportId != -1)
                {
                    function = $"export {call.DebugExportId}";
                }
                else if (call.DebugLocalCallOffset != -1)
                {
                    function = $"call {call.DebugLocalCallOffset:x}";
                }

                break;
            case ExecStackType.Kernel:
                function = call.DebugKernelSubFunction == -1
                    ? "k" + Kernel.GetKernelName(call.DebugKernelFunction)
                    : "k" + Kernel.GetKernelName(call.DebugKernelFunction, call.DebugKernelSubFunction);
                for (int i = ExecutionStack.Count - 1; i >= 0; i--)
                {
                    if (ExecutionStack[i].Type != ExecStackType.Kernel)
                    {
                        pc = ExecutionStack[i].Pc;
                        break;
                    }
                }

                break;
        }

        int room = VarArray[VarGlobal] is null ? -1 : CurrentRoomNumber;
        var script = SegMan.GetScriptIfLoaded(pc.Segment);
        string scriptStr = room.ToString();
        if (script is not null && script.ScriptNumber != room)
        {
            scriptStr += $"/{script.ScriptNumber}";
        }

        return $"room {scriptStr} {function} @ {pc.Offset:x4}";
    }

    /// <summary>The script call stack, innermost call first, for error messages.</summary>
    public string DescribeCallStack()
    {
        var lines = new List<string>();
        for (int i = ExecutionStack.Count - 1; i >= 0 && lines.Count < 24; i--)
        {
            var call = ExecutionStack[i];
            string function;
            switch (call.Type)
            {
                case ExecStackType.Kernel:
                    function = call.DebugKernelSubFunction == -1
                        ? "k" + Kernel.GetKernelName(call.DebugKernelFunction)
                        : "k" + Kernel.GetKernelName(call.DebugKernelFunction, call.DebugKernelSubFunction);
                    lines.Add($"  {function}({call.Argc} args)");
                    continue;
                default:
                    if (call.DebugSelector != -1)
                    {
                        function = $"{SegMan.GetObjectName(call.SendP)}::{Kernel.GetSelectorName(call.DebugSelector)}";
                    }
                    else if (call.DebugExportId != -1)
                    {
                        function = $"export {call.DebugExportId}";
                    }
                    else if (call.DebugLocalCallOffset != -1)
                    {
                        function = $"call {call.DebugLocalCallOffset:x}";
                    }
                    else
                    {
                        function = "?";
                    }

                    break;
            }

            var script = SegMan.GetScriptIfLoaded(call.Pc.Segment);
            string scriptStr = script is null ? call.Pc.Segment.ToString("x4") : script.ScriptNumber.ToString();
            lines.Add($"  {function} (script {scriptStr}, {call.Argc} args) @ {call.Pc.Offset:x4}");
        }

        return string.Join("\n", lines);
    }
}
