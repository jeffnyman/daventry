using Daventry.Sci.Sound;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Engine;

/// <summary>Kernel functions. Each file mirrors one of ScummVM's k*.cpp files.</summary>
public static partial class KernelFunctions
{
    // ---- kscripts.cpp ----

    /// <summary>Loads a resource. Memory resources allocate hunk space; other types return a handle number.</summary>
    public static RegT Load(EngineState s, KernelArgs argv)
    {
        var restype = s.ResMan.ConvertResType((byte)argv.UInt16(0));
        int resnr = argv.UInt16(1);
        if (restype == ResourceType.Memory)
        {
            return s.SegMan.AllocateHunkEntry("kLoad()", resnr);
        }

        return RegT.Make(0, ((int)restype << 11) | resnr);
    }

    public static RegT UnLoad(EngineState s, KernelArgs argv)
    {
        var restype = s.ResMan.ConvertResType((byte)argv.UInt16(0));
        if (restype == ResourceType.Memory)
        {
            s.SegMan.FreeHunkEntry(argv[1]);
        }

        return s.Acc;
    }

    public static RegT Lock(EngineState s, KernelArgs argv)
    {
        // resources are never evicted here, so locking only makes sure the resource is loaded
        var type = s.ResMan.ConvertResType((byte)argv.UInt16(0));
        bool lock_ = argv.Count > 2 ? argv.UInt16(2) != 0 : true;

        // GK2 and SCI3 also saved lock states for views, pics and syncs, but only the audio ones matter
        if (s.Features.HasSci3Audio && type == ResourceType.Audio && s.Audio32 is { } audio32)
        {
            audio32.LockResource(new ResourceId(type, argv.UInt16(1)), lock_);
            return s.Acc;
        }

        if (s.Version == SciVersion.V1_1 && type is ResourceType.Audio36 or ResourceType.Sync36)
        {
            return s.Acc;
        }

        if (lock_ && argv.UInt16(1) != 0xFFFF && type != ResourceType.Invalid)
        {
            s.ResMan.FindResource(new ResourceId(type, argv.UInt16(1)));
        }

        return s.Acc;
    }

    public static RegT ResCheck(EngineState s, KernelArgs argv)
    {
        SciResource? res = null;
        var restype = s.ResMan.ConvertResType((byte)argv.UInt16(0));
        if (restype is ResourceType.Audio36 or ResourceType.Sync36)
        {
            if (argv.Count >= 6)
            {
                byte noun = (byte)argv.UInt16(2);
                byte verb = (byte)argv.UInt16(3);
                byte cond = (byte)argv.UInt16(4);
                byte seq = (byte)argv.UInt16(5);
                res = s.ResMan.TestResource(new ResourceId(restype, argv.UInt16(1), noun, verb, cond, seq));
            }
        }
        else
        {
            res = s.ResMan.TestResource(new ResourceId(restype, argv.UInt16(1)));
        }

        return RegT.Make(0, res is not null ? 1 : 0);
    }

    public static RegT Clone(EngineState s, KernelArgs argv)
    {
        var parentAddr = argv[0];
        var parentObj = s.SegMan.GetObject(parentAddr)
            ?? throw new SciVmException($"Attempt to clone non-object/class at {parentAddr} failed");

        int infoSelector = parentObj.InfoSelector.ToUInt16();
        var cloneObj = s.SegMan.AllocateClone(out var cloneAddr);

        // a parent that is a clone itself must be fetched again after allocating
        if ((infoSelector & InfoFlags.Clone) != 0)
        {
            parentObj = s.SegMan.GetObject(parentAddr)!;
        }

        cloneObj.CopyStateFrom(parentObj);

        // mark as a clone
        infoSelector &= ~InfoFlags.Class;
        cloneObj.InfoSelector = RegT.Make(0, infoSelector | InfoFlags.Clone);
        cloneObj.SpeciesSelector = cloneObj.Pos;
        if (parentObj.IsClass)
        {
            cloneObj.SuperClassSelector = parentObj.Pos;
        }

        s.SegMan.GetScript(parentObj.Pos.Segment).IncrementLockers();
        s.SegMan.GetScript(cloneObj.Pos.Segment).IncrementLockers();
        return cloneAddr;
    }

    public static RegT DisposeClone(EngineState s, KernelArgs argv)
    {
        var obj = argv[0];
        var obj_ = s.SegMan.GetObject(obj)
            ?? throw new SciVmException($"Attempt to dispose non-class/object at {obj}");

        // only real clones are freed: KQ4 early clones Sound, sets bit 1 and disposes it later,
        // and that object must stay valid for kIsObject
        int infoSelector = obj_.InfoSelector.ToUInt16();
        if ((infoSelector & 3) == InfoFlags.Clone)
        {
            obj_.MarkAsFreed();
        }

        return s.Acc;
    }

    /// <summary>The address of an export of a script, loading the script.</summary>
    public static RegT ScriptID(EngineState s, KernelArgs argv)
    {
        int script = argv.UInt16(0);
        int index = argv.Count > 1 ? argv.UInt16(1) : 0;
        if (argv[0].Segment != 0)
        {
            return argv[0];
        }

        ushort scriptSeg = s.SegMan.GetScriptSegment(script, ScriptLoadType.Load);
        if (scriptSeg == 0)
        {
            return RegT.Null;
        }

        var scr = s.SegMan.GetScript(scriptSeg);
        if (scr.NumExports == 0)
        {
            // scripts without a dispatch table are loaded this way, ignoring the result
            if (argv.Count == 2)
            {
                throw new SciVmException($"Script 0x{script:x} does not have a dispatch table and export {index} was requested from it");
            }

            return RegT.Null;
        }

        bool exportsAreWide = s.Features.DetectLofsType() == SciVersion.V1Middle;
        uint address = scr.ValidateExportFunc(index, exportsAreWide) + scr.HeapOffset;
        return RegT.Make(scriptSeg, (int)address);
    }

    public static RegT DisposeScript(EngineState s, KernelArgs argv)
    {
        int script = argv[0].Offset;
        ushort id = s.SegMan.GetScriptSegment(script);
        var scr = s.SegMan.GetScriptIfLoaded(id);
        if (scr is not null && !scr.IsMarkedAsDeleted)
        {
            if (s.ExecutionStack[^1].Pc.Segment != id)
            {
                scr.SetLockers(1);
            }
        }

        s.SegMan.UninstantiateScript(script);
        return argv.Count != 2 ? s.Acc : argv[1];
    }

    public static RegT IsObject(EngineState s, KernelArgs argv)
    {
        if (argv[0].Offset == RegT.SignalOffset)
        {
            return RegT.Null; // treated specially
        }

        return RegT.Make(0, s.SegMan.IsHeapObject(argv[0]) ? 1 : 0);
    }

    public static RegT RespondsTo(EngineState s, KernelArgs argv)
    {
        var obj = argv[0];
        int selector = argv.UInt16(1);
        bool responds = s.SegMan.IsHeapObject(obj) && Selectors.LookupSelector(s.SegMan, s.Version, obj, selector, out _, out _) != SelectorType.None;
        return RegT.Make(0, responds ? 1 : 0);
    }
}
