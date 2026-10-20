using System.Text;
using Daventry.Core.Hosting;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Engine;

public enum ScriptLoadType
{
    DontLoad = 0,
    Load = 1,
    Lock = 3,
}

public struct ClassEntry
{
    public int Script; // -1 when the class does not exist
    public RegT Reg; // segment 0 when not instantiated
}

/// <summary>
/// Owns every segment of the VM heap: scripts, clones, locals, the stack, lists, nodes, hunks
/// and dynamic memory. Port of ScummVM's SegManager for SCI0 to SCI2.1.
/// </summary>
public sealed class SegManager
{
    private readonly List<SegmentObj?> _heap = [];
    private readonly List<ClassEntry> _classTable = [];
    private readonly Dictionary<int, ushort> _scriptSegMap = [];
    private readonly ResourceManager _resMan;
    private readonly ScriptPatcher _scriptPatcher;
    private readonly SciVersion _version;
    private readonly SciGameId _gameId;
    private ushort _clonesSegId;
    private ushort _listsSegId;
    private ushort _nodesSegId;
    private ushort _hunksSegId;

    public SegManager(ResourceManager resMan, ScriptPatcher scriptPatcher, SciVersion version, SciGameId gameId, ILog log)
    {
        _resMan = resMan;
        _scriptPatcher = scriptPatcher;
        _version = version;
        _gameId = gameId;
        Log = log;
        _heap.Add(null);
        CreateClassTable();
    }

    public ILog Log { get; }

    public SciVersion Version => _version;

    public RegT SaveDirPtr { get; private set; }

    public RegT ParserPtr { get; private set; }

    public IReadOnlyList<SegmentObj?> Segments => _heap;

    public int ClassTableSize => _classTable.Count;

    public void ResetSegMan()
    {
        for (int i = 0; i < _heap.Count; i++)
        {
            if (_heap[i] is not null)
            {
                Deallocate((ushort)i);
            }
        }

        _heap.Clear();
        _heap.Add(null);
        _clonesSegId = 0;
        _listsSegId = 0;
        _nodesSegId = 0;
        _hunksSegId = 0;
        _classTable.Clear();
        CreateClassTable();
    }

    public void InitSysStrings()
    {
        // system strings are allocated in one segment, for compatibility
        AllocDynmem(512, "system strings", out var saveDirPtr);
        SaveDirPtr = saveDirPtr;
        ParserPtr = RegT.Make(saveDirPtr.Segment, saveDirPtr.Offset + 256);
    }

    private ushort FindFreeSegment()
    {
        int seg = 1;
        while (seg < _heap.Count && _heap[seg] is not null)
        {
            seg++;
        }

        return (ushort)seg;
    }

    public ushort AllocSegment(SegmentObj mobj)
    {
        ushort id = FindFreeSegment();
        if (id >= _heap.Count)
        {
            _heap.Add(null);
        }

        _heap[id] = mobj;
        return id;
    }

    public Script AllocateScript(int scriptNr, out ushort segId)
    {
        segId = _scriptSegMap.GetValueOrDefault(scriptNr, (ushort)0);
        if (segId > 0)
        {
            return (Script)_heap[segId]!;
        }

        var script = new Script(_version, _gameId, Log);
        segId = AllocSegment(script);
        _scriptSegMap[scriptNr] = segId;
        return script;
    }

    private void Deallocate(ushort seg)
    {
        if (seg < 1 || seg >= _heap.Count)
        {
            throw new SciVmException("Attempt to deallocate an invalid segment ID");
        }

        var mobj = _heap[seg] ?? throw new SciVmException("Attempt to deallocate an already freed segment");
        if (mobj is Script scr)
        {
            _scriptSegMap.Remove(scr.ScriptNumber);
            if (scr.LocalsSegment != 0 && _heap[scr.LocalsSegment] is not null)
            {
                // the locals segment may already be gone when scripts are re-instantiated
                Deallocate(scr.LocalsSegment);
            }
        }

        _heap[seg] = null;
    }

    public bool IsHeapObject(RegT pos)
    {
        var obj = GetObject(pos);
        if (obj is null || obj.IsFreed)
        {
            return false;
        }

        var scr = GetScriptIfLoaded(pos.Segment);
        return !(scr is not null && scr.IsMarkedAsDeleted);
    }

    public void DeallocateScript(int scriptNr) => Deallocate(GetScriptSegment(scriptNr));

    public Script GetScript(ushort seg)
    {
        if (seg < 1 || seg >= _heap.Count)
        {
            throw new SciVmException($"SegManager::getScript(): seg id {seg:x} out of bounds");
        }

        var mobj = _heap[seg] ?? throw new SciVmException($"SegManager::getScript(): seg id {seg:x} is not in memory");
        return mobj as Script ?? throw new SciVmException($"SegManager::getScript(): seg id {seg:x} refers to type {mobj.Type} != SEG_TYPE_SCRIPT");
    }

    public Script? GetScriptIfLoaded(ushort seg)
    {
        if (seg < 1 || seg >= _heap.Count)
        {
            return null;
        }

        return _heap[seg] as Script;
    }

    public ushort FindSegmentByType(SegmentType type)
    {
        for (int i = 0; i < _heap.Count; i++)
        {
            if (_heap[i]?.Type == type)
            {
                return (ushort)i;
            }
        }

        return 0;
    }

    public SegmentObj? GetSegmentObj(ushort seg) => seg < 1 || seg >= _heap.Count ? null : _heap[seg];

    public SegmentType GetSegmentType(ushort seg) => GetSegmentObj(seg)?.Type ?? SegmentType.Invalid;

    public SegmentObj? GetSegment(ushort seg, SegmentType type)
    {
        var obj = GetSegmentObj(seg);
        return obj?.Type == type ? obj : null;
    }

    public SciObject? GetObject(RegT pos)
    {
        var mobj = GetSegmentObj(pos.Segment);
        if (mobj is CloneTable ct)
        {
            if (ct.IsValidEntry(pos.Offset))
            {
                return ct[pos.Offset];
            }

            Log.Warning("sci.vm", "getObject(): Trying to get an invalid object");
            return null;
        }

        if (mobj is Script scr)
        {
            if (pos.Offset <= scr.BufSize && pos.Offset >= -scr.ScriptObjectMagicOffset && scr.OffsetIsObject(pos.Offset))
            {
                return scr.GetObject(pos.Offset);
            }
        }

        return null;
    }

    public bool IsObject(RegT obj) => GetObject(obj) is not null;

    public string GetObjectName(RegT pos)
    {
        var obj = GetObject(pos);
        if (obj is null)
        {
            return "<no such object>";
        }

        var nameReg = obj.Name;
        if (nameReg.IsNull)
        {
            return "<no name>";
        }

        var name = DerefString(nameReg);
        return name ?? "<invalid name>";
    }

    public List<RegT> FindObjectsByName(string name)
    {
        var result = new List<RegT>();
        for (int i = 0; i < _heap.Count; i++)
        {
            var mobj = _heap[i];
            if (mobj is Script scr)
            {
                foreach (var obj in scr.Objects.Values)
                {
                    var objpos = RegT.Make(i, obj.Pos.Offset);
                    if (name == GetObjectName(objpos))
                    {
                        result.Add(objpos);
                    }
                }
            }
            else if (mobj is CloneTable ct)
            {
                for (int idx = 0; idx < ct.Count; idx++)
                {
                    if (!ct.IsValidEntry(idx))
                    {
                        continue;
                    }

                    var objpos = RegT.Make(i, idx);
                    if (name == GetObjectName(objpos))
                    {
                        result.Add(objpos);
                    }
                }
            }
        }

        return result;
    }

    /// <summary>The address of the object with a name. With several matches and no index, returns null.</summary>
    public RegT FindObjectByName(string name, int index = -1)
    {
        var result = FindObjectsByName(name);
        if (result.Count == 0)
        {
            return RegT.Null;
        }

        if (result.Count > 1 && index < 0)
        {
            return RegT.Null; // ambiguous
        }

        if (index < 0)
        {
            return result[0];
        }

        return result.Count <= index ? RegT.Null : result[index];
    }

    public ushort GetScriptSegment(int scriptId) => _scriptSegMap.GetValueOrDefault(scriptId, (ushort)0);

    public ushort GetScriptSegment(int scriptNr, ScriptLoadType load, bool applyScriptPatches = true)
    {
        if ((load & ScriptLoadType.Load) == ScriptLoadType.Load)
        {
            InstantiateScript(scriptNr, applyScriptPatches);
        }

        ushort segment = GetScriptSegment(scriptNr);
        if (segment > 0 && (load & ScriptLoadType.Lock) == ScriptLoadType.Lock)
        {
            GetScript(segment).IncrementLockers();
        }

        return segment;
    }

    public DataStack AllocateStack(int size)
    {
        var stack = new DataStack();
        AllocSegment(stack);
        stack.Entries = new RegT[size];

        // SSCI filled the stack with "S" characters (uppercase in SCI01 to SCI1, lowercase otherwise)
        byte filler = _version is >= SciVersion.V01 and <= SciVersion.V1Late ? (byte)'S' : (byte)'s';
        for (int i = 0; i < size; i++)
        {
            stack.Entries[i] = RegT.Make(0, filler);
        }

        return stack;
    }

    public void FreeHunkEntry(RegT addr)
    {
        if (addr.IsNull)
        {
            Log.Warning("sci.vm", "Attempt to free a Hunk from a null address");
            return;
        }

        if (GetSegment(addr.Segment, SegmentType.Hunk) is not HunkTable ht)
        {
            Log.Warning("sci.vm", $"Attempt to free Hunk from address {addr}: Invalid segment type {GetSegmentType(addr.Segment)}");
            return;
        }

        ht.FreeEntryContents(addr.Offset);
    }

    public RegT AllocateHunkEntry(string hunkType, int size)
    {
        HunkTable table;
        if (_hunksSegId == 0)
        {
            table = new HunkTable();
            _hunksSegId = AllocSegment(table);
        }
        else
        {
            table = (HunkTable)_heap[_hunksSegId]!;
        }

        int offset = table.AllocEntry();
        var h = table.At(offset);
        h.Mem = new byte[size];
        h.Type = hunkType;
        return RegT.Make(_hunksSegId, offset);
    }

    /// <summary>Stores a ready-made buffer as a hunk (saved screen bits).</summary>
    public RegT AllocateHunkEntry(string hunkType, byte[] data)
    {
        var addr = AllocateHunkEntry(hunkType, 0);
        ((HunkTable)_heap[_hunksSegId]!).At(addr.Offset).Mem = data;
        return addr;
    }

    public byte[]? GetHunkPointer(RegT addr)
    {
        if (GetSegment(addr.Segment, SegmentType.Hunk) is not HunkTable table || !table.IsValidEntry(addr.Offset))
        {
            // valid SCI behaviour, e.g. when loading or quitting
            return null;
        }

        return table.At(addr.Offset).Mem;
    }

    public SciObject AllocateClone(out RegT addr)
    {
        CloneTable table;
        if (_clonesSegId == 0)
        {
            table = new CloneTable(_version);
            _clonesSegId = AllocSegment(table);
        }
        else
        {
            table = (CloneTable)_heap[_clonesSegId]!;
        }

        int offset = table.AllocEntry();
        addr = RegT.Make(_clonesSegId, offset);
        // the table creates a default object; replace it with one of the right version
        var clone = new SciObject(_version);
        table.ReplaceEntry(offset, clone);
        return clone;
    }

    public SciList AllocateList(out RegT addr)
    {
        ListTable table;
        if (_listsSegId == 0)
        {
            table = new ListTable();
            _listsSegId = AllocSegment(table);
        }
        else
        {
            table = (ListTable)_heap[_listsSegId]!;
        }

        int offset = table.AllocEntry();
        addr = RegT.Make(_listsSegId, offset);
        return table.At(offset);
    }

    public Node AllocateNode(out RegT addr)
    {
        NodeTable table;
        if (_nodesSegId == 0)
        {
            table = new NodeTable();
            _nodesSegId = AllocSegment(table);
        }
        else
        {
            table = (NodeTable)_heap[_nodesSegId]!;
        }

        int offset = table.AllocEntry();
        addr = RegT.Make(_nodesSegId, offset);
        return table.At(offset);
    }

    public RegT NewNode(RegT value, RegT key)
    {
        var n = AllocateNode(out var nodeRef);
        n.Pred = RegT.Null;
        n.Succ = RegT.Null;
        n.Key = key;
        n.Value = value;
        return nodeRef;
    }

    public SciList LookupList(RegT addr)
    {
        if (GetSegmentType(addr.Segment) != SegmentType.Lists)
        {
            throw new SciVmException($"Attempt to use non-list {addr} as list");
        }

        var lt = (ListTable)_heap[addr.Segment]!;
        if (!lt.IsValidEntry(addr.Offset))
        {
            throw new SciVmException($"Attempt to use non-list {addr} as list");
        }

        return lt[addr.Offset];
    }

    public Node? LookupNode(RegT addr, bool stopOnDiscarded = true)
    {
        if (addr.IsNull)
        {
            return null; // not an error
        }

        var type = GetSegmentType(addr.Segment);
        if (type != SegmentType.Nodes)
        {
            throw new SciVmException($"Attempt to use non-node {addr} (type {type}) as list node");
        }

        var nt = (NodeTable)_heap[addr.Segment]!;
        if (!nt.IsValidEntry(addr.Offset))
        {
            if (!stopOnDiscarded)
            {
                return null;
            }

            throw new SciVmException($"Attempt to use invalid or discarded reference {addr} as list node");
        }

        return nt[addr.Offset];
    }

    public SegmentRef Dereference(RegT pointer)
    {
        if (pointer.Segment == 0 || pointer.Segment >= _heap.Count || _heap[pointer.Segment] is null)
        {
            // happens in KQ5 CD when interacting with certain objects
            Log.Warning("sci.vm", $"SegManager::dereference(): Attempt to dereference invalid pointer {pointer}");
            return SegmentRef.Invalid;
        }

        return _heap[pointer.Segment]!.Dereference(pointer);
    }

    private SegmentRef DerefPtr(RegT pointer, int entries, bool wantRaw)
    {
        var ret = Dereference(pointer);
        if (!ret.IsValid)
        {
            return SegmentRef.Invalid;
        }

        if (ret.IsRaw != wantRaw)
        {
            Log.Debug("sci.vm", $"Dereferencing pointer {pointer} (type {GetSegmentType(pointer.Segment)}) which is {(ret.IsRaw ? "raw" : "not raw")}, but expected {(wantRaw ? "raw" : "not raw")}");
        }

        if (!wantRaw && ret.SkipByte)
        {
            Log.Warning("sci.vm", $"Unaligned pointer read: {pointer} expected with word alignment");
            return SegmentRef.Invalid;
        }

        if (entries > ret.MaxSize)
        {
            Log.Warning("sci.vm", $"Trying to dereference pointer {pointer} beyond end of segment");
            return SegmentRef.Invalid;
        }

        return ret;
    }

    /// <summary>Raw bytes behind a pointer, or null. The span covers the rest of the segment.</summary>
    public (byte[] Buffer, int Offset)? DerefBulkPtr(RegT pointer, int entries)
    {
        var r = DerefPtr(pointer, entries, true);
        return r.IsValid && r.IsRaw ? (r.Raw!, r.RawOffset) : null;
    }

    public (RegT[] Regs, int Index)? DerefRegPtr(RegT pointer, int entries)
    {
        var r = DerefPtr(pointer, 2 * entries, false);
        return r.IsValid && !r.IsRaw ? (r.Regs!, r.RegIndex) : null;
    }

    /// <summary>The NUL terminated string behind a raw pointer, or null when it is not raw or invalid.</summary>
    public string? DerefString(RegT pointer, int entries = 0)
    {
        var r = DerefPtr(pointer, entries, true);
        if (!r.IsValid || !r.IsRaw)
        {
            return null;
        }

        return ReadRawString(r.Raw!, r.RawOffset, r.MaxSize);
    }

    private static string ReadRawString(byte[] buf, int offset, int maxSize)
    {
        int end = offset;
        int limit = Math.Min(buf.Length, offset + maxSize);
        while (end < limit && buf[end] != 0)
        {
            end++;
        }

        return Encoding.Latin1.GetString(buf, offset, end - offset);
    }

    private static char GetChar(in SegmentRef r, int offset)
    {
        if (r.SkipByte)
        {
            offset++;
        }

        var val = r.Regs![r.RegIndex + offset / 2];
        bool oddOffset = (offset & 1) != 0;
        return (char)(oddOffset ? val.Offset >> 8 : val.Offset & 0xFF);
    }

    private static void SetChar(in SegmentRef r, int offset, byte value)
    {
        if (r.SkipByte)
        {
            offset++;
        }

        ref var val = ref r.Regs![r.RegIndex + offset / 2];
        bool oddOffset = (offset & 1) != 0;
        ushort o = oddOffset ? (ushort)((val.Offset & 0x00FF) | (value << 8)) : (ushort)((val.Offset & 0xFF00) | value);
        val = new RegT(0, o);
    }

    public void Strncpy(RegT dest, string src, int n)
    {
        var destR = Dereference(dest);
        if (!destR.IsValid)
        {
            Log.Warning("sci.vm", $"Attempt to strncpy to invalid pointer {dest}");
            return;
        }

        var bytes = Encoding.Latin1.GetBytes(src);
        if (destR.IsRaw)
        {
            ForwardCopyString(destR.Raw!, destR.RawOffset, bytes, n, destR.MaxSize);
        }
        else
        {
            int i;
            for (i = 0; i < n && i < bytes.Length; i++)
            {
                SetChar(destR, i, bytes[i]);
            }

            if (i < n && i < destR.MaxSize)
            {
                SetChar(destR, i, 0);
            }
            else if (destR.MaxSize > n)
            {
                SetChar(destR, n, 0);
            }
        }
    }

    private static void ForwardCopyString(byte[] dest, int destOffset, byte[] src, int n, int maxSize)
    {
        bool zeroPad = n != int.MaxValue;
        int limit = Math.Min(n, maxSize);
        int i = 0;
        for (; i < limit; i++)
        {
            byte b = i < src.Length ? src[i] : (byte)0;
            dest[destOffset + i] = b;
            if (b == 0)
            {
                i++;
                break;
            }
        }

        if (zeroPad)
        {
            for (; i < limit; i++)
            {
                dest[destOffset + i] = 0;
            }
        }
    }

    public void Strncpy(RegT dest, RegT src, int n)
    {
        if (src.IsNull)
        {
            if (n > 0)
            {
                Strcpy(dest, string.Empty);
            }

            return;
        }

        var srcR = Dereference(src);
        if (!srcR.IsValid)
        {
            Log.Warning("sci.vm", $"Attempt to strncpy from invalid pointer {src}");
            if (n > 0)
            {
                Strcpy(dest, string.Empty);
            }

            return;
        }

        var destR = Dereference(dest);
        if (!destR.IsValid)
        {
            Log.Warning("sci.vm", $"Attempt to strncpy to invalid pointer {dest}");
            return;
        }

        if (srcR.IsRaw)
        {
            Strncpy(dest, ReadRawString(srcR.Raw!, srcR.RawOffset, srcR.MaxSize), n);
        }
        else if (destR.IsRaw)
        {
            for (int i = 0; i < n && i < destR.MaxSize; i++)
            {
                char c = GetChar(srcR, i);
                destR.Raw![destR.RawOffset + i] = (byte)c;
                if (c == 0)
                {
                    break;
                }
            }
        }
        else
        {
            for (int i = 0; i < n && i < destR.MaxSize; i++)
            {
                char c = GetChar(srcR, i);
                SetChar(destR, i, (byte)c);
                if (c == 0)
                {
                    break;
                }
            }
        }
    }

    public void Strcpy(RegT dest, string src) => Strncpy(dest, src, int.MaxValue);

    public void Strcpy(RegT dest, RegT src) => Strncpy(dest, src, int.MaxValue);

    public void Memcpy(RegT dest, ReadOnlySpan<byte> src, int n)
    {
        var destR = Dereference(dest);
        if (!destR.IsValid)
        {
            Log.Warning("sci.vm", $"Attempt to memcpy to invalid pointer {dest}");
            return;
        }

        if (n > destR.MaxSize)
        {
            Log.Warning("sci.vm", $"Trying to dereference pointer {dest} beyond end of segment");
            return;
        }

        if (destR.IsRaw)
        {
            src[..n].CopyTo(destR.Raw.AsSpan(destR.RawOffset, n));
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                SetChar(destR, i, src[i]);
            }
        }
    }

    public void Memcpy(RegT dest, RegT src, int n)
    {
        var destR = Dereference(dest);
        var srcR = Dereference(src);
        if (!destR.IsValid)
        {
            Log.Warning("sci.vm", $"Attempt to memcpy to invalid pointer {dest}");
            return;
        }

        if (n > destR.MaxSize)
        {
            Log.Warning("sci.vm", $"Trying to dereference pointer {dest} beyond end of segment");
            return;
        }

        if (!srcR.IsValid)
        {
            Log.Warning("sci.vm", $"Attempt to memcpy from invalid pointer {src}");
            return;
        }

        if (n > srcR.MaxSize)
        {
            Log.Warning("sci.vm", $"Trying to dereference pointer {src} beyond end of segment");
            return;
        }

        if (srcR.IsRaw)
        {
            Memcpy(dest, srcR.Raw.AsSpan(srcR.RawOffset), n);
        }
        else if (destR.IsRaw)
        {
            for (int i = 0; i < n; i++)
            {
                destR.Raw![destR.RawOffset + i] = (byte)GetChar(srcR, i);
            }
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                SetChar(destR, i, (byte)GetChar(srcR, i));
            }
        }
    }

    public void Memcpy(Span<byte> dest, RegT src, int n)
    {
        var srcR = Dereference(src);
        if (!srcR.IsValid)
        {
            Log.Warning("sci.vm", $"Attempt to memcpy from invalid pointer {src}");
            return;
        }

        if (n > srcR.MaxSize)
        {
            Log.Warning("sci.vm", $"Trying to dereference pointer {src} beyond end of segment");
            return;
        }

        if (srcR.IsRaw)
        {
            srcR.Raw.AsSpan(srcR.RawOffset, n).CopyTo(dest);
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                dest[i] = (byte)GetChar(srcR, i);
            }
        }
    }

    public int Strlen(RegT str)
    {
        if (str.IsNull)
        {
            return 0;
        }

        var r = Dereference(str);
        if (!r.IsValid)
        {
            Log.Warning("sci.vm", $"Attempt to call strlen on invalid pointer {str}");
            return 0;
        }

        if (r.IsRaw)
        {
            int i = 0;
            while (i < r.MaxSize && r.Raw![r.RawOffset + i] != 0)
            {
                i++;
            }

            return i;
        }
        else
        {
            int i = 0;
            while (i < r.MaxSize && GetChar(r, i) != 0)
            {
                i++;
            }

            return i;
        }
    }

    public string GetString(RegT pointer)
    {
        if (pointer.IsNull)
        {
            return string.Empty;
        }

        var r = Dereference(pointer);
        if (!r.IsValid)
        {
            Log.Warning("sci.vm", $"SegManager::getString(): Attempt to dereference invalid pointer {pointer}");
            return string.Empty;
        }

        if (r.IsRaw)
        {
            return ReadRawString(r.Raw!, r.RawOffset, r.MaxSize);
        }

        var sb = new StringBuilder();
        for (int i = 0; i < r.MaxSize; i++)
        {
            char c = GetChar(r, i);
            if (c == 0)
            {
                break;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    public byte[] AllocDynmem(int size, string descr, out RegT addr)
    {
        var dynmem = new DynMem();
        ushort segid = AllocSegment(dynmem);
        addr = RegT.Make(segid, 0);
        dynmem.Size = size;
        dynmem.Buf = new byte[size];
        dynmem.Description = descr;
        return dynmem.Buf;
    }

    public bool FreeDynmem(RegT addr)
    {
        if (addr.Segment < 1 || addr.Segment >= _heap.Count || _heap[addr.Segment]?.Type != SegmentType.DynMem)
        {
            return false;
        }

        Deallocate(addr.Segment);
        return true;
    }

    private void CreateClassTable()
    {
        var vocab996 = _resMan.FindResource(ResourceType.Vocab, 996)
            ?? throw new SciVmException("SegManager: failed to open vocab 996");
        int totalClasses = vocab996.Data!.Length >> 2;
        for (int classNr = 0; classNr < totalClasses; classNr++)
        {
            int scriptNr = vocab996.GetUInt16LEAt(classNr * 4 + 2);
            _classTable.Add(new ClassEntry { Reg = RegT.Null, Script = scriptNr });
        }
    }

    public ClassEntry GetClass(int index) => _classTable[index];

    public void SetClassOffset(int index, RegT offset)
    {
        var entry = _classTable[index];
        entry.Reg = offset;
        _classTable[index] = entry;
    }

    public void ResizeClassTable(int size)
    {
        while (_classTable.Count < size)
        {
            _classTable.Add(new ClassEntry { Script = -1 });
        }
    }

    public RegT GetClassAddress(int classnr, ScriptLoadType lock_, ushort callerSegment, bool applyScriptPatches = true)
    {
        if (classnr == 0xFFFF)
        {
            return RegT.Null;
        }

        if (classnr < 0 || _classTable.Count <= classnr || _classTable[classnr].Script < 0)
        {
            throw new SciVmException($"[VM] Attempt to dereference class {classnr:x}, which doesn't exist (max {_classTable.Count:x})");
        }

        var theClass = _classTable[classnr];
        if (theClass.Reg.Segment == 0)
        {
            GetScriptSegment(theClass.Script, lock_, applyScriptPatches);
            theClass = _classTable[classnr];
            if (theClass.Reg.Segment == 0)
            {
                if (lock_ == ScriptLoadType.DontLoad)
                {
                    return RegT.Null;
                }

                throw new SciVmException($"[VM] Trying to instantiate class {classnr:x} by instantiating script 0x{theClass.Script:x} ({theClass.Script:D3}) failed");
            }
        }
        else if (callerSegment != theClass.Reg.Segment)
        {
            GetScript(theClass.Reg.Segment).IncrementLockers();
        }

        return theClass.Reg;
    }

    public int InstantiateScript(int scriptNum, bool applyScriptPatches = true)
    {
        ushort segmentId = GetScriptSegment(scriptNum);
        var scr = GetScriptIfLoaded(segmentId);
        if (scr is not null)
        {
            if (!scr.IsMarkedAsDeleted)
            {
                scr.IncrementLockers();
                return segmentId;
            }

            scr.FreeScript(true);
        }
        else
        {
            scr = AllocateScript(scriptNum, out segmentId);
        }

        scr.Load(scriptNum, _resMan, _scriptPatcher, applyScriptPatches);
        scr.InitializeLocals(this);
        scr.InitializeObjects(this, segmentId, applyScriptPatches);
        return segmentId;
    }

    public void UninstantiateScript(int scriptNr)
    {
        ushort segmentId = GetScriptSegment(scriptNr);
        var scr = GetScriptIfLoaded(segmentId);
        if (scr is null || scr.IsMarkedAsDeleted)
        {
            return; // valid SCI behaviour
        }

        scr.DecrementLockers();
        if (scr.Lockers > 0)
        {
            return;
        }

        // free all class table references to this script
        for (int i = 0; i < _classTable.Count; i++)
        {
            if (_classTable[i].Reg.Segment == segmentId)
            {
                SetClassOffset(i, RegT.Null);
            }
        }

        if (_version < SciVersion.V1_1)
        {
            UninstantiateScriptSci0(scriptNr);
        }

        // SCI1.1 has no proper uninstantiation in ScummVM either

        if (scr.Lockers == 0)
        {
            // the actual deletion is done by the scripts themselves
            scr.MarkDeleted();
            Log.Debug("sci.vm", $"Unloaded script 0x{scriptNr:x}.");
        }
    }

    private void UninstantiateScriptSci0(int scriptNr)
    {
        bool oldScriptHeader = _version == SciVersion.V0Early;
        ushort segmentId = GetScriptSegment(scriptNr);
        var scr = GetScript(segmentId);
        int reg = oldScriptHeader ? 2 : 0;
        int objLength = 0;

        // one pass over the objects to uninstantiate all superclasses
        while (true)
        {
            reg += objLength;
            if (reg + 4 > scr.Buf.Length)
            {
                break;
            }

            int objType = scr.GetUInt16At(reg);
            if (objType == 0)
            {
                break;
            }

            objLength = scr.GetUInt16At(reg + 2);
            reg += 4;

            if (objType is (int)ScriptObjectType.Object or (int)ScriptObjectType.Class)
            {
                reg += 8; // magic offset
                short superclass = (short)scr.GetUInt16At(reg + 2);
                if (superclass >= 0 && superclass < _classTable.Count)
                {
                    int superclassScript = GetClass(superclass).Script;
                    if (superclassScript == scriptNr)
                    {
                        if (scr.Lockers > 0)
                        {
                            scr.DecrementLockers(); // this is us ourselves
                        }
                    }
                    else
                    {
                        UninstantiateScript(superclassScript);
                    }
                }

                reg += -8;
            }

            reg -= 4;
        }
    }
}
