using System.Buffers.Binary;
using Daventry.Core.Hosting;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Engine;

public enum ScriptObjectType
{
    Terminator,
    Object,
    Code,
    Synonyms,
    Said,
    Strings,
    Class,
    Exports,
    Pointers,
    PreloadText,
    LocalVars,
}

public enum ScriptOffsetEntryType
{
    Object = 0, // classes too
    String,
    Said,
}

public struct OffsetLookupEntry
{
    public ScriptOffsetEntryType Type;
    public ushort Id; // first item of this type is 1
    public uint Offset; // offset inside the script data
    public ushort StringSize; // including the terminating NUL
}

/// <summary>
/// A loaded script: its bytes (with the SCI1.1 heap appended), exports, locals, objects and
/// the relocation data. Mirrors ScummVM's Script for SCI0 to SCI2.1.
/// </summary>
public sealed partial class Script : SegmentObj
{
    public const uint NoRelocation = 0xFFFFFFFF;
    public const ushort ObjectMagicNumber = 0x1234;
    private const int Sci11NumExportsOffset = 6;
    private const int Sci11ExportTableOffset = 8;

    private readonly SciVersion _version;
    private readonly SciGameId _gameId;
    private readonly Dictionary<uint, SciObject> _objects = [];
    private readonly List<OffsetLookupEntry> _offsetLookupArray = [];
    private int _exportsOffset; // byte offset of the export words in Buf, -1 when absent
    private int _synonymsOffset = -1;
    private uint _lockers;

    public Script(SciVersion version, SciGameId gameId, ILog log)
        : base(SegmentType.Script)
    {
        _version = version;
        _gameId = gameId;
        Log = log;
        FreeScript();
    }

    public ILog Log { get; }

    public int ScriptNumber { get; private set; }

    public byte[] Buf { get; private set; } = [];

    public uint ScriptSize { get; private set; }

    public uint HeapSize { get; private set; }

    public uint BufSize => (uint)Buf.Length;

    public uint HeapOffset => _version is >= SciVersion.V1_1 and <= SciVersion.V2_1Late ? ScriptSize : 0;

    public int LocalsOffset { get; private set; }

    public ushort LocalsCount { get; private set; }

    public ushort LocalsSegment { get; private set; }

    public LocalVariables? LocalsBlock { get; private set; }

    public ushort NumExports { get; private set; }

    public ushort NumSynonyms { get; private set; }

    public int SynonymsOffset => _synonymsOffset;

    public int CodeOffset { get; private set; }

    public bool IsMarkedAsDeleted { get; private set; }

    public uint Lockers => _lockers;

    public IReadOnlyDictionary<uint, SciObject> Objects => _objects;

    public IReadOnlyList<OffsetLookupEntry> OffsetArray => _offsetLookupArray;

    public int OffsetObjectCount { get; private set; }

    public int OffsetStringCount { get; private set; }

    public int OffsetSaidCount { get; private set; }

    public int ScriptObjectMagicOffset => _version < SciVersion.V1_1 ? -8 : 0;

    public bool OffsetIsObject(uint offset)
    {
        long magicPos = (long)offset + ScriptObjectMagicOffset;
        return magicPos >= 0 && magicPos + 2 <= Buf.Length && ReadUInt16(Buf, (int)magicPos) == ObjectMagicNumber;
    }

    public ushort GetUInt16At(int offset) => ReadUInt16(Buf, offset);

    public void FreeScript(bool keepLocalsSegment = false)
    {
        ScriptNumber = 0;
        Buf = [];
        ScriptSize = 0;
        HeapSize = 0;
        _exportsOffset = -1;
        NumExports = 0;
        _synonymsOffset = -1;
        NumSynonyms = 0;
        CodeOffset = 0;
        LocalsOffset = 0;
        if (!keepLocalsSegment)
        {
            LocalsSegment = 0;
        }

        LocalsBlock = null;
        LocalsCount = 0;
        _lockers = 1;
        IsMarkedAsDeleted = false;
        _objects.Clear();
        _offsetLookupArray.Clear();
        OffsetObjectCount = 0;
        OffsetStringCount = 0;
        OffsetSaidCount = 0;
    }

    public void Load(int scriptNr, ResourceManager resMan, ScriptPatcher scriptPatcher, bool applyScriptPatches = true)
    {
        FreeScript();

        var script = resMan.FindResource(ResourceType.Script, scriptNr)
            ?? throw new SciVmException($"Script {scriptNr} not found");

        ScriptNumber = scriptNr;
        uint scriptSize = (uint)script.Data!.Length;
        uint bufSize = scriptSize;
        SciResource? heap = null;

        if (_version == SciVersion.V0Early)
        {
            bufSize += (uint)(script.GetUInt16LEAt(0) * 2);
        }
        else if (_version is >= SciVersion.V1_1 and <= SciVersion.V2_1Late)
        {
            // SCI1.1 to SCI2.1 keep the heap apart; it is appended to the script and addressing adjusted
            heap = resMan.FindResource(ResourceType.Heap, scriptNr)
                ?? throw new SciVmException($"Heap {scriptNr} not found");
            bufSize += (uint)heap.Data!.Length;

            // the heap must start word-aligned
            if ((script.Data.Length & 2) != 0)
            {
                bufSize++;
                scriptSize++;
            }

            if (script.Data.Length + heap.Data.Length > 65535)
            {
                throw new SciVmException($"Script and heap {scriptNr} sizes combined exceed 64K");
            }
        }

        uint extraLocalsWorkaround = 0;
        if (_gameId == SciGameId.Fanmade && scriptNr == 1 && script.Data.Length == 11140)
        {
            // WORKAROUND: Ocean Battle's script 1 lacks locals for its shots-left string (bug #5335)
            extraLocalsWorkaround = 10;
        }

        bufSize += extraLocalsWorkaround * 2;

        Buf = new byte[bufSize];
        Array.Copy(script.Data, Buf, script.Data.Length);
        ScriptSize = scriptSize;

        if (heap is not null)
        {
            Array.Copy(heap.Data!, 0, Buf, (int)scriptSize, heap.Data!.Length);
            HeapSize = (uint)heap.Data.Length;
        }

        if (applyScriptPatches)
        {
            scriptPatcher.ProcessScript(scriptNr, Buf);
        }

        if (_version <= SciVersion.V1Late)
        {
            // Some scripts have two export tables (Camelot 912, KQ4 306): the first is broken,
            // so the last one is used (bugs #5151 and #5079)
            var exportTable = FindBlockSci0(ScriptObjectType.Exports, true);
            if (exportTable is { } et)
            {
                // after the block header (4 bytes) and the export count (2 bytes)
                _exportsOffset = et.Offset + 6;
                NumExports = ReadUInt16(Buf, et.Offset + 4);
            }

            var synonymTable = FindBlockSci0(ScriptObjectType.Synonyms);
            if (synonymTable is { } st)
            {
                _synonymsOffset = st.Offset + 4;
                NumSynonyms = (ushort)((st.Size - 4) / 4);
            }

            var localsTable = FindBlockSci0(ScriptObjectType.LocalVars);
            if (localsTable is { } lt)
            {
                LocalsOffset = lt.Offset + 4;
                LocalsCount = (ushort)(lt.Size / 2 - 2);
            }
        }
        else if (_version is >= SciVersion.V1_1 and <= SciVersion.V2_1Late)
        {
            NumExports = ReadUInt16(Buf, Sci11NumExportsOffset);
            if (NumExports != 0)
            {
                _exportsOffset = Sci11ExportTableOffset;
            }

            LocalsOffset = (int)HeapOffset + 4;
            LocalsCount = ReadUInt16(Buf, LocalsOffset - 2);
        }

        LocalsCount += (ushort)extraLocalsWorkaround;

        if (_version == SciVersion.V0Early)
        {
            // the old script block has no locals block; the script starts with the locals count
            LocalsCount = ReadUInt16(Buf, 0);
            LocalsOffset = -LocalsCount * 2; // invalid on purpose
        }
        else
        {
            if (LocalsCount == 0)
            {
                LocalsOffset = 0;
            }

            if (LocalsOffset + LocalsCount * 2 + 1 >= Buf.Length)
            {
                throw new SciVmException($"Locals extend beyond end of script {scriptNr}: offset {LocalsOffset:x4}, count {LocalsCount} vs size {Buf.Length}");
            }
        }

        IdentifyOffsets();
    }

    private void IdentifyOffsets()
    {
        _offsetLookupArray.Clear();
        OffsetObjectCount = 0;
        OffsetStringCount = 0;
        OffsetSaidCount = 0;
        CodeOffset = 0;
        ushort typeObjectId = 0;
        ushort typeStringId = 0;
        ushort typeSaidId = 0;

        if (_version < SciVersion.V1_1)
        {
            int pos = _version == SciVersion.V0Early ? 2 : 0;
            while (true)
            {
                if (pos + 2 > Buf.Length)
                {
                    throw new SciVmException($"Script::identifyOffsets(): unexpected end of script {ScriptNumber}");
                }

                int blockType = ReadUInt16(Buf, pos);
                pos += 2;
                if (blockType == 0)
                {
                    break;
                }

                if (pos + 2 > Buf.Length)
                {
                    throw new SciVmException($"Script::identifyOffsets(): unexpected end of script {ScriptNumber}");
                }

                int blockSize = ReadUInt16(Buf, pos);
                if (blockSize < 4)
                {
                    throw new SciVmException($"Script::identifyOffsets(): invalid block size in script {ScriptNumber}");
                }

                blockSize -= 4; // includes the type and size words
                pos += 2;
                if (pos + blockSize > Buf.Length)
                {
                    throw new SciVmException($"Script::identifyOffsets(): invalid block size in script {ScriptNumber}");
                }

                switch ((ScriptObjectType)blockType)
                {
                    case ScriptObjectType.Object:
                    case ScriptObjectType.Class:
                        typeObjectId++;
                        _offsetLookupArray.Add(new OffsetLookupEntry { Type = ScriptOffsetEntryType.Object, Id = typeObjectId, Offset = (uint)(pos + 8) });
                        OffsetObjectCount++;
                        break;

                    case ScriptObjectType.Strings:
                    {
                        int stringPos = pos;
                        int end = pos + blockSize;
                        while (stringPos < end)
                        {
                            int start = stringPos;
                            if (end - stringPos == 1 && Buf[start] == 0)
                            {
                                break;
                            }

                            byte b;
                            do
                            {
                                b = Buf[stringPos++];
                                if (b != 0 && stringPos >= end)
                                {
                                    Log.Warning("sci.vm", $"Script::identifyOffsets(): string without terminating NUL in script {ScriptNumber}");
                                    break;
                                }
                            }
                            while (b != 0);

                            if (b != 0)
                            {
                                break;
                            }

                            typeStringId++;
                            _offsetLookupArray.Add(new OffsetLookupEntry { Type = ScriptOffsetEntryType.String, Id = typeStringId, Offset = (uint)start, StringSize = (ushort)(stringPos - start) });
                            OffsetStringCount++;
                        }

                        break;
                    }

                    case ScriptObjectType.Said:
                    {
                        // said strings end with 0xFF; words are two bytes whose second byte may be 0xFF
                        int saidPos = pos;
                        int end = pos + blockSize;
                        while (saidPos < end)
                        {
                            int start = saidPos;
                            if (end - saidPos == 1 && Buf[start] == 0)
                            {
                                // Conquests of Camelot script 997 ends with a filler byte
                                break;
                            }

                            while (true)
                            {
                                byte b = Buf[saidPos++];
                                if (b == 0xFF)
                                {
                                    break;
                                }

                                if (saidPos >= end)
                                {
                                    throw new SciVmException($"Script::identifyOffsets(): said-string without terminator in script {ScriptNumber}");
                                }

                                if (b < 0xF0)
                                {
                                    saidPos++;
                                    if (saidPos >= end)
                                    {
                                        throw new SciVmException($"Script::identifyOffsets(): said-string without terminator in script {ScriptNumber}");
                                    }
                                }
                            }

                            typeSaidId++;
                            _offsetLookupArray.Add(new OffsetLookupEntry { Type = ScriptOffsetEntryType.Said, Id = typeSaidId, Offset = (uint)start });
                            OffsetSaidCount++;
                        }

                        break;
                    }
                }

                pos += blockSize;
            }
        }
        else if (_version <= SciVersion.V2_1Late)
        {
            // strings in SCI1.1 to SCI2.1 come after the object instances on the heap
            int heap = (int)HeapOffset;
            int hunkPtr = Sci11ExportTableOffset + NumExports * 2;

            if (HeapSize < 4)
            {
                throw new SciVmException($"Script::identifyOffsets(): unexpected end of script in script {ScriptNumber}");
            }

            int endOfStringOffset = ReadUInt16(Buf, heap);
            int objectStartOffset = ReadUInt16(Buf, heap + 2) * 2 + 4;
            if (HeapSize < objectStartOffset)
            {
                throw new SciVmException($"Script::identifyOffsets(): object start is beyond heap size in script {ScriptNumber}");
            }

            if (HeapSize < endOfStringOffset)
            {
                throw new SciVmException($"Script::identifyOffsets(): end of string is beyond heap size in script {ScriptNumber}");
            }

            int endOfStringPtr = heap + endOfStringOffset;
            int pos = heap + objectStartOffset;

            while (true)
            {
                if (pos + 2 > Buf.Length)
                {
                    throw new SciVmException($"Script::identifyOffsets(): unexpected end of script {ScriptNumber}");
                }

                int blockType = ReadUInt16(Buf, pos);
                pos += 2;
                if (blockType != ObjectMagicNumber)
                {
                    break;
                }

                typeObjectId++;
                _offsetLookupArray.Add(new OffsetLookupEntry { Type = ScriptOffsetEntryType.Object, Id = typeObjectId, Offset = (uint)(pos - 2) });
                OffsetObjectCount++;

                int numProperties = ReadUInt16(Buf, pos);
                int blockSize = numProperties * 2;
                if (blockSize < 4)
                {
                    throw new SciVmException($"Script::identifyOffsets(): invalid block size in script {ScriptNumber}");
                }

                pos += 2;
                int scriptNum = ReadUInt16(Buf, pos + 6);
                if (scriptNum != 0xFFFF)
                {
                    hunkPtr += numProperties * 2;
                }

                int numMethods = ReadUInt16(Buf, hunkPtr);
                hunkPtr += 2 + numMethods * 4;

                blockSize -= 4;
                if (pos + blockSize > Buf.Length)
                {
                    throw new SciVmException($"Script::identifyOffsets(): invalid block size in script {ScriptNumber}");
                }

                pos += blockSize;
            }

            CodeOffset = hunkPtr;

            if (pos > endOfStringPtr)
            {
                throw new SciVmException($"Script::identifyOffsets(): string block / end-of-string block mismatch in script {ScriptNumber}");
            }

            int stringPos = pos;
            while (stringPos < endOfStringPtr)
            {
                int start = stringPos;
                byte b;
                do
                {
                    b = Buf[stringPos++];
                    if (b != 0 && stringPos >= endOfStringPtr)
                    {
                        Log.Warning("sci.vm", $"Script::identifyOffsets(): string without terminating NUL in script {ScriptNumber}");
                        break;
                    }
                }
                while (b != 0);

                if (b != 0)
                {
                    break;
                }

                typeStringId++;
                _offsetLookupArray.Add(new OffsetLookupEntry { Type = ScriptOffsetEntryType.String, Id = typeStringId, Offset = (uint)start, StringSize = (ushort)(stringPos - start) });
                OffsetStringCount++;
            }
        }
    }

    public SciObject? GetObject(uint offset) => _objects.GetValueOrDefault(offset);

    /// <summary>Creates the object record for an object at a position, which must point at the VM's object location.</summary>
    public SciObject ScriptObjInit(RegT objPos, bool fullObjectInit = true)
    {
        if (objPos.Offset >= Buf.Length)
        {
            throw new SciVmException($"Attempt to initialize object beyond end of script {ScriptNumber} ({objPos.Offset} >= {Buf.Length})");
        }

        if (!_objects.TryGetValue(objPos.Offset, out var obj))
        {
            obj = new SciObject(_version);
            _objects[objPos.Offset] = obj;
        }

        obj.Init(this, objPos, fullObjectInit);
        return obj;
    }

    public void RemoveObject(uint offset) => _objects.Remove(offset);

    public static bool RelocateBlock(RegT[] block, int blockLocation, ushort segment, int location, uint heapOffset)
    {
        int rel = location - blockLocation;
        if (rel < 0)
        {
            return false;
        }

        int idx = rel >> 1;
        if (idx >= block.Length)
        {
            return false;
        }

        if ((rel & 1) != 0)
        {
            throw new SciVmException($"Attempt to relocate odd variable #{idx}.5e (relative to {blockLocation:x4})");
        }

        block[idx] = block[idx].WithSegment(segment).IncOffset((int)heapOffset);
        return true;
    }

    private bool RelocateLocal(ushort segment, int location, uint offset)
        => LocalsBlock is not null && RelocateBlock(LocalsBlock.Locals, LocalsOffset, segment, location, offset);

    public uint GetRelocationOffset(uint offset)
    {
        var (tableOffset, count) = GetRelocationTableSci0Sci21();
        for (int i = 0; i < count; i++)
        {
            if (ReadUInt16(Buf, tableOffset + i * 2) == offset)
            {
                return HeapOffset;
            }
        }

        return NoRelocation;
    }

    /// <summary>The relocation table: a list of heap offsets whose values are object pointers rather than numbers.</summary>
    private (int Offset, int Count) GetRelocationTableSci0Sci21()
    {
        int relocationBlock;
        int blockSize;
        int numEntries;
        int dataOffset;

        if (_version < SciVersion.V1_1)
        {
            var block = FindBlockSci0(ScriptObjectType.Pointers);
            if (block is null)
            {
                return (0, 0);
            }

            var last = FindBlockSci0(ScriptObjectType.Pointers, true);
            if (last is { } lb && lb.Offset != block.Value.Offset)
            {
                Log.Warning("sci.vm", $"Script {ScriptNumber} has multiple relocation tables");
            }

            relocationBlock = block.Value.Offset;
            blockSize = block.Value.Size;
            numEntries = ReadUInt16(Buf, relocationBlock + 4);
            if (numEntries == 0)
            {
                return (0, 0);
            }

            dataOffset = 6;

            // From about SQ1 on, SCI1 relocation tables start with a null entry that is not
            // counted; a few scripts lack it (Castle of Dr Brain 947, PQ3 997, KQ5 CD 975). Zero
            // is never a valid relocation offset, so it is skipped when seen.
            if (ReadUInt16(Buf, relocationBlock + 6) == 0)
            {
                dataOffset += 2;
            }
        }
        else if (_version <= SciVersion.V2_1Late)
        {
            int heap = (int)HeapOffset;
            relocationBlock = heap + ReadUInt16(Buf, heap);
            if (relocationBlock >= Buf.Length)
            {
                return (0, 0);
            }

            blockSize = Buf.Length - relocationBlock;
            numEntries = ReadUInt16(Buf, relocationBlock);
            if (numEntries == 0)
            {
                return (0, 0);
            }

            dataOffset = 2;
        }
        else
        {
            throw new SciVmException($"Invalid engine version called Script::getRelocationTableSci0Sci21 on script {ScriptNumber}");
        }

        if (dataOffset + numEntries * 2 != blockSize)
        {
            Log.Debug("sci.vm", $"Script {ScriptNumber} unexpected relocation table size {blockSize}");
        }

        return (relocationBlock + dataOffset, numEntries);
    }

    private void RelocateSci0Sci21(ushort segmentId)
    {
        var (tableOffset, count) = GetRelocationTableSci0Sci21();
        uint heapOffset = HeapOffset;
        for (int i = 0; i < count; i++)
        {
            int pos = ReadUInt16(Buf, tableOffset + i * 2) + (int)heapOffset;

            // locals and objects are relocated; code blocks are ignored
            if (!RelocateLocal(segmentId, pos, heapOffset))
            {
                foreach (var obj in _objects.Values)
                {
                    if (obj.RelocateSci0Sci21(segmentId, pos, heapOffset))
                    {
                        break;
                    }
                }
            }
        }
    }

    public void IncrementLockers()
    {
        if (_lockers == 0xFFFFFFFF)
        {
            // the locker on system scripts (script 999) can overflow in long sessions
            Log.Warning("sci.vm", $"script {ScriptNumber} locker maximum reached, resetting");
            _lockers = 0x80000000;
        }

        _lockers++;
    }

    public void DecrementLockers()
    {
        if (_lockers > 0)
        {
            _lockers--;
        }
    }

    public void SetLockers(uint lockers) => _lockers = lockers;

    public void MarkDeleted() => IsMarkedAsDeleted = true;

    public int ExportsOffset => _exportsOffset;

    public uint ValidateExportFunc(int pubfunct, bool exportsAreWide)
    {
        if (NumExports <= pubfunct)
        {
            throw new SciVmException($"Script {ScriptNumber} validateExportFunc(): pubfunct {pubfunct} is invalid");
        }

        if (exportsAreWide)
        {
            pubfunct *= 2;
        }

        int offset = ReadUInt16(Buf, _exportsOffset + pubfunct * 2);

        // SCI2 does not adjust export offsets to the code block when they are zero, which is
        // supposed to mean an empty slot; the offset then points at the start of the code block
        // and several scripts rely on that (script 64909's first export shakes the screen)
        if (_version >= SciVersion.V2 && offset == 0)
        {
            offset = CodeOffset;
        }

        if (offset == -1 || offset >= Buf.Length)
        {
            throw new SciVmException($"Invalid export {pubfunct} function pointer ({offset}) in script {ScriptNumber}");
        }

        return (uint)offset;
    }

    /// <summary>Finds a block of a type in a SCI0 to SCI1 script. Returns its offset and total size.</summary>
    public (int Offset, int Size)? FindBlockSci0(ScriptObjectType type, bool findLastBlock = false)
    {
        (int Offset, int Size)? found = null;
        int pos = _version == SciVersion.V0Early ? 2 : 0;

        while (pos + 4 <= Buf.Length)
        {
            int blockType = ReadUInt16(Buf, pos);
            if (blockType == 0)
            {
                break;
            }

            int blockSize = ReadUInt16(Buf, pos + 2);
            if (blockSize <= 0)
            {
                throw new SciVmException($"Script {ScriptNumber}: block of size 0");
            }

            if (blockType == (int)type)
            {
                found = (pos, blockSize);
                if (!findLastBlock)
                {
                    break;
                }
            }

            pos += blockSize;
        }

        return found;
    }

    public override bool IsValidOffset(uint offset) => offset < Buf.Length;

    public override SegmentRef Dereference(RegT pointer)
    {
        if (pointer.Offset > Buf.Length)
        {
            throw new SciVmException($"Script::dereference(): Attempt to dereference invalid pointer {pointer} into script {ScriptNumber} segment (script size={Buf.Length})");
        }

        return new SegmentRef { IsRaw = true, MaxSize = Buf.Length - pointer.Offset, Raw = Buf, RawOffset = pointer.Offset };
    }

    private LocalVariables? AllocLocalsSegment(SegManager segMan)
    {
        if (LocalsCount == 0)
        {
            return null;
        }

        LocalVariables locals;
        if (LocalsSegment == 0)
        {
            locals = new LocalVariables();
            LocalsSegment = segMan.AllocSegment(locals);
        }
        else
        {
            locals = segMan.GetSegment(LocalsSegment, SegmentType.Locals) as LocalVariables
                ?? throw new SciVmException($"Invalid script {ScriptNumber} locals segment while allocating locals");
            if (locals.ScriptId != ScriptNumber)
            {
                throw new SciVmException($"Invalid script {ScriptNumber} locals segment while allocating locals");
            }
        }

        LocalsBlock = locals;
        locals.ScriptId = ScriptNumber;
        if (locals.Locals.Length != LocalsCount)
        {
            var resized = new RegT[LocalsCount];
            Array.Copy(locals.Locals, resized, Math.Min(locals.Locals.Length, LocalsCount));
            locals.Locals = resized;
        }

        return locals;
    }

    public void InitializeLocals(SegManager segMan)
    {
        var locals = AllocLocalsSegment(segMan);
        if (locals is null)
        {
            return;
        }

        if (_version > SciVersion.V0Early)
        {
            for (int i = 0; i < LocalsCount; i++)
            {
                locals.Locals[i] = RegT.Make(0, ReadUInt16(Buf, LocalsOffset + i * 2));
            }
        }
        else
        {
            // SCI0 early sets locals at run time
            Array.Fill(locals.Locals, RegT.Null);
        }
    }

    public void SyncLocalsBlock(SegManager segMan)
        => LocalsBlock = LocalsSegment == 0 ? null : segMan.GetSegment(LocalsSegment, SegmentType.Locals) as LocalVariables;

    public void InitializeClass(SegManager segMan, int species, uint position)
    {
        // WORKAROUND: the class table (vocab 996) is incomplete in some games (SQ3 1.018 scripts
        // 93 and 99, LSL3 script 500, KQ5 Amiga script 220), so it is grown for plausible species
        if (species >= segMan.ClassTableSize)
        {
            if (species <= 255 && _version < SciVersion.V2)
            {
                Log.Warning("sci.vm", $"Resizing class table for species {species} in script {ScriptNumber}");
                segMan.ResizeClassTable(species + 1);
            }
            else
            {
                throw new SciVmException($"Invalid species {species} in script {ScriptNumber}");
            }
        }

        segMan.SetClassOffset(species, RegT.Make(segMan.GetScriptSegment(ScriptNumber), (int)position));
    }

    private void InitializeObjectsSci0(SegManager segMan, ushort segmentId, bool applyScriptPatches)
    {
        int seeker = _version == SciVersion.V0Early ? 2 : 0;
        do
        {
            int objType = ReadUInt16(Buf, seeker);
            if (objType == 0)
            {
                break;
            }

            if (objType is (int)ScriptObjectType.Object or (int)ScriptObjectType.Class)
            {
                uint objectPosition = (uint)(seeker + 12);
                if (objType == (int)ScriptObjectType.Class)
                {
                    int species = ReadUInt16(Buf, seeker + 12);
                    InitializeClass(segMan, species, objectPosition);
                }

                var addr = RegT.Make(segmentId, (int)objectPosition);
                var obj = ScriptObjInit(addr);
                obj.InitSpecies(segMan, addr, applyScriptPatches);
                if (!obj.InitBaseObject(segMan, addr, true, applyScriptPatches))
                {
                    if ((ScriptNumber == 202 || ScriptNumber == 764) && _gameId == SciGameId.Kq5)
                    {
                        // WORKAROUND: KQ5 French and German script 202 has an invalid object, and
                        // script 764 has junk at its end (bugs #4996 and #5568)
                        _objects.Remove((uint)(addr.Offset - ScriptObjectMagicOffset));
                    }
                    else
                    {
                        throw new SciVmException($"Failed to locate base object for object at {addr} in script {ScriptNumber}");
                    }
                }
            }

            seeker += ReadUInt16(Buf, seeker + 2);
        }
        while (seeker < ScriptSize - 2);

        RelocateSci0Sci21(segmentId);
    }

    private void InitializeObjectsSci11(SegManager segMan, ushort segmentId, bool applyScriptPatches)
    {
        int heap = (int)HeapOffset;
        int seeker = heap + 4 + ReadUInt16(Buf, heap + 2) * 2;
        var mismatchedVarCountObjects = new List<RegT>();

        while (ReadUInt16(Buf, seeker) == ObjectMagicNumber)
        {
            uint objectPosition = (uint)seeker;
            bool isClass = (ReadUInt16(Buf, seeker + 14) & InfoFlags.Class) != 0; // -info- selector
            if (isClass)
            {
                int species = ReadUInt16(Buf, seeker + 10);
                InitializeClass(segMan, species, objectPosition);
            }

            var reg = RegT.Make(segmentId, (int)objectPosition);
            var obj = ScriptObjInit(reg);

            // the base is copied from the species class, as its selector ids are needed
            obj.SuperClassSelector = segMan.GetClassAddress(obj.SuperClassSelector.Offset, ScriptLoadType.Lock, 0, applyScriptPatches);

            // -propDict- is used by Obj::isMemberOf; classes get it relocated to the script's
            // segment and instances copy it from their class (SQ4 CD room 381 robot talk clicks)
            if (obj.IsClass)
            {
                obj.PropDictSelector = obj.PropDictSelector.WithSegment(segmentId);
            }
            else
            {
                var classObj = segMan.GetObject(obj.SuperClassSelector);
                if (classObj is not null)
                {
                    obj.PropDictSelector = classObj.PropDictSelector;
                    if (obj.VarCount != classObj.VarCount)
                    {
                        mismatchedVarCountObjects.Add(reg);
                    }
                }
            }

            // -classScript- holds the script number, which is enough for Obj::isKindOf
            obj.ClassScriptSelector = RegT.Make(0, ScriptNumber);

            seeker += ReadUInt16(Buf, seeker + 2) * 2;
        }

        RelocateSci0Sci21(segmentId);

        foreach (var pos in mismatchedVarCountObjects)
        {
            var obj = segMan.GetObject(pos)!;
            var classObj = segMan.GetObject(obj.SuperClassSelector)!;
            Log.Debug("sci.vm", $"Object {pos} ({segMan.GetObjectName(pos)}) from script {ScriptNumber} declares {obj.VarCount} variables, but its class declares {classObj.VarCount} variables");
        }
    }

    public void InitializeObjects(SegManager segMan, ushort segmentId, bool applyScriptPatches)
    {
        if (_version <= SciVersion.V1Late)
        {
            InitializeObjectsSci0(segMan, segmentId, applyScriptPatches);
        }
        else
        {
            InitializeObjectsSci11(segMan, segmentId, applyScriptPatches);
        }
    }

    public override RegT FindCanonicAddress(SegManager segMan, RegT subAddr) => subAddr.WithOffset(0);

    public override void FreeAtAddress(SegManager segMan, RegT subAddr)
    {
        if (IsMarkedAsDeleted)
        {
            segMan.DeallocateScript(ScriptNumber);
        }
    }

    public override void ListAllDeallocatable(ushort segId, List<RegT> output) => output.Add(RegT.Make(segId, 0));

    public override void ListAllOutgoingReferences(RegT addr, List<RegT> output)
    {
        if (addr.Offset <= Buf.Length && OffsetIsObject(addr.Offset))
        {
            var obj = GetObject(addr.Offset);
            if (obj is null)
            {
                throw new SciVmException($"Request for outgoing script-object reference at {addr} failed in script {ScriptNumber}");
            }

            if (LocalsSegment != 0)
            {
                output.Add(RegT.Make(LocalsSegment, 0));
            }

            for (int i = 0; i < obj.VarCount; i++)
            {
                output.Add(obj.GetVariable(i));
            }
        }

        // otherwise it is a string or similar: nothing outgoing
    }

    /// <summary>All objects of the script and its locals, for the garbage collector's root set.</summary>
    public void ListObjectReferences(List<RegT> output)
    {
        if (LocalsSegment != 0)
        {
            output.Add(RegT.Make(LocalsSegment, 0));
        }

        foreach (var obj in _objects.Values)
        {
            output.Add(obj.Pos);
        }
    }

    private static ushort ReadUInt16(byte[] buf, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(offset));
}
