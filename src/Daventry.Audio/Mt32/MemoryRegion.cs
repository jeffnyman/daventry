// Port of Munt (libmt32emu) 2.7.0 as bundled with ScummVM.
// Copyright (C) 2003, 2004, 2005, 2006, 2008, 2009 Dean Beeler, Jerome Fisher
// Copyright (C) 2011-2022 Dean Beeler, Jerome Fisher, Sergey V. Mikayev
// Licensed under the GNU Lesser General Public License, version 2.1 or later.

namespace Daventry.Audio.Mt32;

internal enum MemoryRegionType
{
    PatchTemp,
    RhythmTemp,
    TimbreTemp,
    Patches,
    Timbres,
    System,
    Display,
    Reset,
}

/// <summary>
/// One sysex-addressable area of the module's memory: where it starts, how its entries are laid
/// out, and the table of maximum values writes are clamped to. Port of MemoryRegion.h and the
/// read/write methods in Synth.cpp.
/// </summary>
internal sealed class MemoryRegion
{
    private readonly byte[]? _realMemory;
    private readonly int _realMemoryOffset;
    private readonly byte[]? _maxTable;
    private readonly int _maxTableOffset;

    private MemoryRegion(byte[]? useRealMemory, int realMemoryOffset, byte[]? useMaxTable, int maxTableOffset, MemoryRegionType useType, uint useStartAddr, uint useEntrySize, uint useEntries)
    {
        _realMemory = useRealMemory;
        _realMemoryOffset = realMemoryOffset;
        _maxTable = useMaxTable;
        _maxTableOffset = maxTableOffset;
        Type = useType;
        StartAddr = useStartAddr;
        EntrySize = useEntrySize;
        Entries = useEntries;
    }

    public MemoryRegionType Type { get; }

    public uint StartAddr { get; }

    public uint EntrySize { get; }

    public uint Entries { get; }

    public bool IsReadable => _realMemory is not null;

    public static MemoryRegion PatchTemp(MemParams mem, byte[] rom, int maxTable)
        => new(mem.Bytes, MemParams.PatchTempOffset, rom, maxTable, MemoryRegionType.PatchTemp, MemAddr.MemAddress(0x030000), Mt32.PatchTemp.Size, 9);

    public static MemoryRegion RhythmTemp(MemParams mem, byte[] rom, int maxTable)
        => new(mem.Bytes, MemParams.RhythmTempOffset, rom, maxTable, MemoryRegionType.RhythmTemp, MemAddr.MemAddress(0x030110), Mt32.RhythmTemp.Size, 85);

    public static MemoryRegion TimbreTemp(MemParams mem, byte[] maxTable)
        => new(mem.Bytes, MemParams.TimbreTempOffset, maxTable, 0, MemoryRegionType.TimbreTemp, MemAddr.MemAddress(0x040000), TimbreParam.Size, 8);

    public static MemoryRegion Patches(MemParams mem, byte[] rom, int maxTable)
        => new(mem.Bytes, MemParams.PatchesOffset, rom, maxTable, MemoryRegionType.Patches, MemAddr.MemAddress(0x050000), PatchParam.Size, 128);

    public static MemoryRegion Timbres(MemParams mem, byte[] maxTable)
        => new(mem.Bytes, MemParams.TimbresOffset, maxTable, 0, MemoryRegionType.Timbres, MemAddr.MemAddress(0x080000), MemParams.PaddedTimbreSize, 64 + 64 + 64 + 64);

    public static MemoryRegion System(MemParams mem, byte[] rom, int maxTable)
        => new(mem.Bytes, MemParams.SystemOffset, rom, maxTable, MemoryRegionType.System, MemAddr.MemAddress(0x100000), SystemParams.Size, 1);

    // Note, we set realMemory to NULL despite the real devices buffer inbound strings. However, it
    // is impossible to retrieve them. This entrySize permits emulation of handling a 20-byte
    // display message sent to an old-gen device at address 0x207F7F.
    public static MemoryRegion Display()
        => new(null, 0, null, 0, MemoryRegionType.Display, MemAddr.MemAddress(0x200000), 0x4013, 1);

    public static MemoryRegion Reset()
        => new(null, 0, null, 0, MemoryRegionType.Reset, MemAddr.MemAddress(0x7F0000), 0x3FFF, 1);

    public int LastTouched(uint addr, uint len) => (int)((Offset(addr) + len - 1) / EntrySize);

    public int FirstTouchedOffset(uint addr) => (int)(Offset(addr) % EntrySize);

    public int FirstTouched(uint addr) => (int)(Offset(addr) / EntrySize);

    public uint RegionEnd => StartAddr + EntrySize * Entries;

    public bool Contains(uint addr) => addr >= StartAddr && addr < RegionEnd;

    public uint Offset(uint addr) => addr - StartAddr;

    public uint GetClampedLen(uint addr, uint len) => addr + len > RegionEnd ? RegionEnd - addr : len;

    public uint Next(uint addr, uint len) => addr + len > RegionEnd ? RegionEnd - addr : 0;

    public byte GetMaxValue(uint off) => _maxTable is null ? (byte)0xFF : _maxTable[_maxTableOffset + (int)(off % EntrySize)];

    public void Read(uint entry, uint off, Span<byte> dst, uint len)
    {
        off += entry * EntrySize;
        // This method should never be called with out-of-bounds parameters, or on an unsupported
        // region - seeing any of this debug output indicates a bug in the emulator
        if (off > EntrySize * Entries - 1)
        {
            return;
        }

        if (off + len > EntrySize * Entries)
        {
            len = EntrySize * Entries - off;
        }

        if (_realMemory is null)
        {
            return;
        }

        _realMemory.AsSpan(_realMemoryOffset + (int)off, (int)len).CopyTo(dst);
    }

    public void Write(uint entry, uint off, ReadOnlySpan<byte> src, uint len, bool init = false)
    {
        uint memOff = entry * EntrySize + off;
        // This method should never be called with out-of-bounds parameters, or on an unsupported
        // region - seeing any of this debug output indicates a bug in the emulator
        if (off > EntrySize * Entries - 1)
        {
            return;
        }

        if (off + len > EntrySize * Entries)
        {
            len = EntrySize * Entries - off;
        }

        if (_realMemory is null)
        {
            return;
        }

        for (int i = 0; i < len; i++)
        {
            byte desiredValue = src[i];
            byte maxValue = GetMaxValue(memOff);
            // maxValue == 0 means write-protected unless called from initialisation code, in which
            // case it really means the maximum value is 0.
            if (maxValue != 0 || init)
            {
                if (desiredValue > maxValue)
                {
                    desiredValue = maxValue;
                }

                _realMemory[_realMemoryOffset + (int)memOff] = desiredValue;
            }

            memOff++;
        }
    }
}

/// <summary>
/// Simple queue implementation using a ring buffer to store incoming MIDI event before the synth
/// actually processes it. Port of MidiEventQueue with the dynamic SysEx storage (the default).
/// </summary>
internal sealed class MidiEventQueue
{
    public struct MidiEvent
    {
        public byte[]? SysexData;
        public uint ShortMessageData;
        public uint Timestamp;
    }

    private readonly MidiEvent[] _ringBuffer;
    private readonly uint _ringBufferMask;
    private uint _startPosition;
    private uint _endPosition;

    /// <param name="ringBufferSize">Must be a power of 2.</param>
    public MidiEventQueue(uint ringBufferSize)
    {
        _ringBuffer = new MidiEvent[ringBufferSize];
        _ringBufferMask = ringBufferSize - 1;
        Reset();
    }

    public bool IsEmpty => _startPosition == _endPosition;

    public void Reset()
    {
        _startPosition = 0;
        _endPosition = 0;
    }

    public bool PushShortMessage(uint shortMessageData, uint timestamp)
    {
        uint newEndPosition = (_endPosition + 1) & _ringBufferMask;
        // If ring buffer is full, bail out.
        if (_startPosition == newEndPosition)
        {
            return false;
        }

        ref var newEvent = ref _ringBuffer[_endPosition];
        newEvent.SysexData = null;
        newEvent.ShortMessageData = shortMessageData;
        newEvent.Timestamp = timestamp;
        _endPosition = newEndPosition;
        return true;
    }

    public bool PushSysex(ReadOnlySpan<byte> sysexData, uint timestamp)
    {
        uint newEndPosition = (_endPosition + 1) & _ringBufferMask;
        // If ring buffer is full, bail out.
        if (_startPosition == newEndPosition)
        {
            return false;
        }

        ref var newEvent = ref _ringBuffer[_endPosition];
        newEvent.SysexData = sysexData.ToArray();
        newEvent.Timestamp = timestamp;
        _endPosition = newEndPosition;
        return true;
    }

    /// <summary>The next event, or null when the queue is empty.</summary>
    public MidiEvent? PeekMidiEvent() => IsEmpty ? null : _ringBuffer[_startPosition];

    public void DropMidiEvent()
    {
        if (IsEmpty)
        {
            return;
        }

        _startPosition = (_startPosition + 1) & _ringBufferMask;
    }
}
