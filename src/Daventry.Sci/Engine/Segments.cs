namespace Daventry.Sci.Engine;

/// <summary>A dereferenced pointer: either raw bytes or a run of registers. Mirrors SegmentRef.</summary>
public struct SegmentRef
{
    public bool IsRaw;
    public byte[]? Raw;
    public int RawOffset;
    public RegT[]? Regs;
    public int RegIndex;
    public int MaxSize;
    public bool SkipByte;

    public readonly bool IsValid => IsRaw ? Raw is not null : Regs is not null;

    public static SegmentRef Invalid => new() { IsRaw = true };
}

/// <summary>Anything the segment manager can hold. Mirrors ScummVM's SegmentObj.</summary>
public abstract class SegmentObj
{
    protected SegmentObj(SegmentType type)
    {
        Type = type;
    }

    public SegmentType Type { get; }

    public abstract bool IsValidOffset(uint offset);

    public virtual SegmentRef Dereference(RegT pointer)
        => throw new SciVmException($"Trying to dereference pointer {pointer} to inappropriate segment");

    /// <summary>The address whose deallocation governs <paramref name="subAddr"/>. Used by the garbage collector.</summary>
    public virtual RegT FindCanonicAddress(SegManager segMan, RegT subAddr) => subAddr;

    public virtual void FreeAtAddress(SegManager segMan, RegT subAddr)
    {
    }

    public virtual void ListAllDeallocatable(ushort segId, List<RegT> output)
    {
    }

    public virtual void ListAllOutgoingReferences(RegT obj, List<RegT> output)
    {
    }
}

public sealed class LocalVariables : SegmentObj
{
    public LocalVariables()
        : base(SegmentType.Locals)
    {
    }

    public int ScriptId { get; set; }

    public RegT[] Locals { get; set; } = [];

    public override bool IsValidOffset(uint offset) => offset < Locals.Length * 2;

    public override SegmentRef Dereference(RegT pointer)
    {
        var ret = new SegmentRef { IsRaw = false, MaxSize = (Locals.Length - pointer.Offset / 2) * 2 };
        if ((pointer.Offset & 1) != 0)
        {
            ret.MaxSize -= 1;
            ret.SkipByte = true;
        }

        if (ret.MaxSize > 0)
        {
            ret.Regs = Locals;
            ret.RegIndex = pointer.Offset / 2;
        }
        else
        {
            // Laura Bow 2 CD reads past the end of heap 160 and 220 in its intro via kMemory(peek)
            ret.Regs = null;
        }

        return ret;
    }

    public override RegT FindCanonicAddress(SegManager segMan, RegT subAddr)
    {
        ushort ownerSeg = segMan.GetScriptSegment(ScriptId);
        return RegT.Make(ownerSeg, 0);
    }

    public override void ListAllOutgoingReferences(RegT obj, List<RegT> output) => output.AddRange(Locals);
}

public sealed class DataStack : SegmentObj
{
    public DataStack()
        : base(SegmentType.Stack)
    {
    }

    public RegT[] Entries { get; set; } = [];

    public int Capacity => Entries.Length;

    public override bool IsValidOffset(uint offset) => offset < Capacity * 2;

    public override SegmentRef Dereference(RegT pointer)
    {
        var ret = new SegmentRef { IsRaw = false, MaxSize = (Capacity - pointer.Offset / 2) * 2 };
        if ((pointer.Offset & 1) != 0)
        {
            ret.MaxSize -= 1;
            ret.SkipByte = true;
        }

        ret.Regs = Entries;
        ret.RegIndex = pointer.Offset / 2;
        return ret;
    }

    public override RegT FindCanonicAddress(SegManager segMan, RegT subAddr) => RegT.Make(subAddr.Segment, 0);

    public override void ListAllOutgoingReferences(RegT obj, List<RegT> output) => output.AddRange(Entries);
}

public sealed class Node
{
    public RegT Pred;
    public RegT Succ;
    public RegT Key;
    public RegT Value;
}

public sealed class SciList
{
    /// <summary>How many nested kListEachElementDo style iterations a list supports.</summary>
    public const int MaxRecursions = 10;

    public RegT First;
    public RegT Last;

    /// <summary>SCI32: depth of the running kListEachElementDo, kListFirstTrue and kListAllTrue iterations.</summary>
    public int NumRecursions;

    /// <summary>
    /// SCI32: the next node of each running iteration, kept on the list so kDeleteKey can move
    /// it on when the scripts delete that node from inside the iteration.
    /// </summary>
    public RegT[] NextNodes = new RegT[MaxRecursions];
}

public sealed class Hunk
{
    public byte[]? Mem;
    public string Type = string.Empty;
}

/// <summary>A table of equally sized entries with a free list. Mirrors SegmentObjTable.</summary>
public abstract partial class SegmentObjTable<T> : SegmentObj
    where T : class
{
    private const int HeapEntryInvalid = -1;

    private readonly List<(T? Data, int NextFree)> _table = [];
    private readonly Func<T> _factory;
    private int _firstFree = HeapEntryInvalid;

    protected SegmentObjTable(SegmentType type, Func<T> factory)
        : base(type)
    {
        _factory = factory;
    }

    public int EntriesUsed { get; private set; }

    public int Count => _table.Count;

    public int AllocEntry()
    {
        EntriesUsed++;
        if (_firstFree != HeapEntryInvalid)
        {
            int oldff = _firstFree;
            _firstFree = _table[oldff].NextFree;
            _table[oldff] = (_factory(), oldff);
            return oldff;
        }

        int newIdx = _table.Count;
        _table.Add((_factory(), newIdx)); // tagged valid by pointing at itself
        return newIdx;
    }

    public override bool IsValidOffset(uint offset) => IsValidEntry((int)offset);

    public bool IsValidEntry(int idx) => idx >= 0 && idx < _table.Count && _table[idx].NextFree == idx;

    public virtual void FreeEntry(int idx)
    {
        if (idx < 0 || idx >= _table.Count)
        {
            throw new SciVmException($"Table::freeEntry: Attempt to release invalid table index {idx}");
        }

        _table[idx] = (null, _firstFree);
        _firstFree = idx;
        EntriesUsed--;
    }

    public override void ListAllDeallocatable(ushort segId, List<RegT> output)
    {
        for (int i = 0; i < _table.Count; i++)
        {
            if (IsValidEntry(i))
            {
                output.Add(RegT.Make(segId, i));
            }
        }
    }

    public T At(int index) => _table[index].Data ?? throw new SciVmException($"Table entry {index} is not allocated");

    /// <summary>The allocated entries with their indices.</summary>
    public IEnumerable<(int Index, T Data)> EnumerateValid()
    {
        for (int i = 0; i < _table.Count; i++)
        {
            if (IsValidEntry(i))
            {
                yield return (i, _table[i].Data!);
            }
        }
    }

    /// <summary>Swaps the object held by a valid entry.</summary>
    public void ReplaceEntry(int index, T data)
    {
        if (!IsValidEntry(index))
        {
            throw new SciVmException($"Table::replaceEntry: invalid table index {index}");
        }

        _table[index] = (data, index);
    }

    public T this[int index] => At(index);
}

public sealed class CloneTable : SegmentObjTable<SciObject>
{
    public CloneTable(SciVersion version)
        : base(SegmentType.Clones, () => new SciObject(version))
    {
    }

    public override void ListAllOutgoingReferences(RegT addr, List<RegT> output)
    {
        if (!IsValidEntry(addr.Offset))
        {
            throw new SciVmException($"Unexpected request for outgoing references from clone at {addr}");
        }

        var clone = At(addr.Offset);
        for (int i = 0; i < clone.VarCount; i++)
        {
            output.Add(clone.GetVariable(i));
        }

        // the base object is part of the script and therefore also emits the locals
        output.Add(clone.Pos);
    }

    public override void FreeAtAddress(SegManager segMan, RegT subAddr) => FreeEntry(subAddr.Offset);
}

public sealed class NodeTable : SegmentObjTable<Node>
{
    public NodeTable()
        : base(SegmentType.Nodes, () => new Node())
    {
    }

    public override void FreeAtAddress(SegManager segMan, RegT subAddr) => FreeEntry(subAddr.Offset);

    public override void ListAllOutgoingReferences(RegT addr, List<RegT> output)
    {
        if (!IsValidEntry(addr.Offset))
        {
            throw new SciVmException($"Invalid node referenced for outgoing references: {addr}");
        }

        var node = At(addr.Offset);
        output.Add(node.Pred);
        output.Add(node.Succ);
        output.Add(node.Key);
        output.Add(node.Value);
    }
}

public sealed class ListTable : SegmentObjTable<SciList>
{
    public ListTable()
        : base(SegmentType.Lists, () => new SciList())
    {
    }

    public override void FreeAtAddress(SegManager segMan, RegT subAddr) => FreeEntry(subAddr.Offset);

    public override void ListAllOutgoingReferences(RegT addr, List<RegT> output)
    {
        if (!IsValidEntry(addr.Offset))
        {
            throw new SciVmException($"Invalid list referenced for outgoing references: {addr}");
        }

        var list = At(addr.Offset);
        output.Add(list.First);
        output.Add(list.Last);
    }
}

public sealed class HunkTable : SegmentObjTable<Hunk>
{
    public HunkTable()
        : base(SegmentType.Hunk, () => new Hunk())
    {
    }

    public void FreeEntryContents(int idx) => At(idx).Mem = null;

    public override void FreeEntry(int idx)
    {
        FreeEntryContents(idx);
        base.FreeEntry(idx);
    }

    public override void FreeAtAddress(SegManager segMan, RegT subAddr) => FreeEntry(subAddr.Offset);
}

/// <summary>Free-style memory allocated by the kernel (system strings, kMemory allocations).</summary>
public sealed class DynMem : SegmentObj
{
    public DynMem()
        : base(SegmentType.DynMem)
    {
    }

    public int Size { get; set; }

    public string Description { get; set; } = string.Empty;

    public byte[] Buf { get; set; } = [];

    public override bool IsValidOffset(uint offset) => offset < Size;

    public override SegmentRef Dereference(RegT pointer)
        => new() { IsRaw = true, MaxSize = Size - pointer.Offset, Raw = Buf, RawOffset = pointer.Offset };

    public override RegT FindCanonicAddress(SegManager segMan, RegT subAddr) => RegT.Make(subAddr.Segment, 0);

    public override void ListAllDeallocatable(ushort segId, List<RegT> output) => output.Add(RegT.Make(segId, 0));
}
