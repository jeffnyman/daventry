namespace Daventry.Sci.Engine;

/// <summary>The mark and sweep garbage collector for clones, lists, nodes, hunks and scripts. Port of gc.cpp.</summary>
public static class Gc
{
    private sealed class Worklist
    {
        public readonly HashSet<RegT> Map = [];
        public readonly Stack<RegT> Items = new();

        public void Push(RegT reg)
        {
            if (reg.Segment == 0)
            {
                return; // no numbers
            }

            if (Map.Add(reg))
            {
                Items.Push(reg);
            }
        }

        public void PushAll(List<RegT> regs)
        {
            foreach (var r in regs)
            {
                Push(r);
            }
        }
    }

    private static HashSet<RegT> NormalizeAddresses(SegManager segMan, HashSet<RegT> nonNormal)
    {
        var normal = new HashSet<RegT>();
        foreach (var reg in nonNormal)
        {
            var mobj = segMan.GetSegmentObj(reg.Segment);
            if (mobj is not null)
            {
                normal.Add(mobj.FindCanonicAddress(segMan, reg));
            }
        }

        return normal;
    }

    private static HashSet<RegT> FindAllActiveReferences(EngineState s)
    {
        var wm = new Worklist();
        var tmp = new List<RegT>();

        wm.Push(s.Acc);
        wm.Push(s.Prev);

        // the value stack up to the top non-kernel frame's stack pointer
        int top = s.ExecutionStack.Count - 1;
        if (s.ExecutionStack[top].Type == ExecStackType.Kernel)
        {
            top--;
        }

        int sp = s.ExecutionStack[top].Sp;
        for (int pos = 0; pos < sp && pos < s.Stack.Length; pos++)
        {
            wm.Push(s.Stack[pos]);
        }

        foreach (var es in s.ExecutionStack)
        {
            if (es.Type != ExecStackType.Kernel)
            {
                wm.Push(es.ObjP);
                wm.Push(es.SendP);
                if (es.Type == ExecStackType.VarSelector)
                {
                    var obj = s.SegMan.GetObject(es.VarP.Obj);
                    if (obj is not null)
                    {
                        wm.Push(obj.GetVariable(es.VarP.VarIndex));
                    }
                }
            }
        }

        // explicitly loaded scripts
        var heap = s.SegMan.Segments;
        for (int i = 1; i < heap.Count; i++)
        {
            if (heap[i] is Script script && script.Lockers > 0)
            {
                tmp.Clear();
                script.ListObjectReferences(tmp);
                wm.PushAll(tmp);
            }
            else if (heap[i] is BitmapTable bitmaps)
            {
                // SCI32 bitmaps that opted out of collection (the engine still draws them)
                foreach (var (index, bitmap) in bitmaps.EnumerateValid())
                {
                    if (!bitmap.ShouldGc)
                    {
                        wm.Push(RegT.Make(i, index));
                    }
                }
            }
        }

        ushort stackSegment = s.SegMan.FindSegmentByType(SegmentType.Stack);
        while (wm.Items.Count > 0)
        {
            var reg = wm.Items.Pop();
            if (reg.Segment != stackSegment && reg.Segment < heap.Count && heap[reg.Segment] is { } mobj)
            {
                tmp.Clear();
                mobj.ListAllOutgoingReferences(reg, tmp);
                wm.PushAll(tmp);
            }
        }

        foreach (var hunk in s.Ports?.EngineHunks ?? [])
        {
            wm.Push(hunk);
        }

        return NormalizeAddresses(s.SegMan, wm.Map);
    }

    public static void Run(EngineState s)
    {
        var segMan = s.SegMan;
        var activeRefs = FindAllActiveReferences(s);
        var heap = segMan.Segments;
        var tmp = new List<RegT>();
        for (int seg = 1; seg < heap.Count; seg++)
        {
            var mobj = heap[seg];
            if (mobj is null)
            {
                continue;
            }

            tmp.Clear();
            mobj.ListAllDeallocatable((ushort)seg, tmp);
            foreach (var addr in tmp)
            {
                if (!activeRefs.Contains(addr))
                {
                    mobj.FreeAtAddress(segMan, addr);
                }
            }
        }
    }
}
