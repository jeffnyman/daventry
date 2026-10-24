// Port of Munt (libmt32emu) 2.7.0 as bundled with ScummVM.
// Copyright (C) 2003, 2004, 2005, 2006, 2008, 2009 Dean Beeler, Jerome Fisher
// Copyright (C) 2011-2022 Dean Beeler, Jerome Fisher, Sergey V. Mikayev
// Licensed under the GNU Lesser General Public License, version 2.1 or later.

namespace Daventry.Audio.Mt32;

// The following structures represent the MT-32's memory. Since sysex allows this memory to be
// written to in blocks of bytes, Munt keeps them packed so that data can be copied into the
// various banks directly. Here the memory is one byte array, and each structure is a view of it
// at an offset, with the same byte layout as the packed C structures of Structures.h.

/// <summary>Address helpers of Structures.h.</summary>
internal static class MemAddr
{
    // MT32EMU_MEMADDR() converts from sysex-padded, MT32EMU_SYSEXMEMADDR converts to it. Roland
    // provides documentation using the sysex-padded addresses, so we tend to use that in code and output
    public static uint MemAddress(uint x) => ((x & 0x7f0000) >> 2) | ((x & 0x7f00) >> 1) | (x & 0x7f);

    public static uint SysexMemAddress(uint x) => ((x & 0x1FC000) << 2) | ((x & 0x3F80) << 1) | (x & 0x7f);
}

/// <summary>TimbreParam::PartialParam: 58 bytes of WG, pitch envelope, pitch LFO, TVF and TVA settings.</summary>
internal readonly struct PartialParam(byte[] mem, int offset)
{
    public const int Size = 58;

    public byte[] Memory => mem;

    public int Offset => offset;

    // WGParam
    public byte WgPitchCoarse => mem[offset]; // 0-96 (C1,C#1-C9)

    public byte WgPitchFine => mem[offset + 1]; // 0-100 (-50 to +50 (cents - confirmed by Mok))

    public byte WgPitchKeyfollow => mem[offset + 2]; // 0-16 (-1, -1/2, -1/4, 0, 1/8, 1/4, 3/8, 1/2, 5/8, 3/4, 7/8, 1, 5/4, 3/2, 2, s1, s2)

    public byte WgPitchBenderEnabled => mem[offset + 3]; // 0-1 (OFF, ON)

    public byte WgWaveform => mem[offset + 4]; // MT-32: 0-1 (SQU/SAW); LAPC-I: WG WAVEFORM/PCM BANK 0 - 3 (SQU/1, SAW/1, SQU/2, SAW/2)

    public byte WgPcmWave => mem[offset + 5]; // 0-127 (1-128)

    public byte WgPulseWidth => mem[offset + 6]; // 0-100

    public byte WgPulseWidthVeloSensitivity => mem[offset + 7]; // 0-14 (-7 - +7)

    // PitchEnvParam
    public byte PitchEnvDepth => mem[offset + 8]; // 0-10

    public byte PitchEnvVeloSensitivity => mem[offset + 9]; // 0-100

    public byte PitchEnvTimeKeyfollow => mem[offset + 10]; // 0-4

    public byte PitchEnvTime(int i) => mem[offset + 11 + i]; // [4] 0-100

    public byte PitchEnvLevel(int i) => mem[offset + 15 + i]; // [5] 0-100 (-50 - +50) // [3]: SUSTAIN LEVEL, [4]: END LEVEL

    // PitchLFOParam
    public byte PitchLfoRate => mem[offset + 20]; // 0-100

    public byte PitchLfoDepth => mem[offset + 21]; // 0-100

    public byte PitchLfoModSensitivity => mem[offset + 22]; // 0-100

    // TVFParam
    public byte TvfCutoff => mem[offset + 23]; // 0-100

    public byte TvfResonance => mem[offset + 24]; // 0-30

    public byte TvfKeyfollow => mem[offset + 25]; // -1, -1/2, -1/4, 0, 1/8, 1/4, 3/8, 1/2, 5/8, 3/4, 7/8, 1, 5/4, 3/2, 2

    public byte TvfBiasPoint => mem[offset + 26]; // 0-127 (<1A-<7C >1A-7C)

    public byte TvfBiasLevel => mem[offset + 27]; // 0-14 (-7 - +7)

    public byte TvfEnvDepth => mem[offset + 28]; // 0-100

    public byte TvfEnvVeloSensitivity => mem[offset + 29]; // 0-100

    public byte TvfEnvDepthKeyfollow => mem[offset + 30]; // DEPTH KEY FOLL0W 0-4

    public byte TvfEnvTimeKeyfollow => mem[offset + 31]; // TIME KEY FOLLOW 0-4

    public byte TvfEnvTime(int i) => mem[offset + 32 + i]; // [5] 0-100

    public byte TvfEnvLevel(int i) => mem[offset + 37 + i]; // [4] 0-100 // [3]: SUSTAIN LEVEL

    // TVAParam
    public byte TvaLevel => mem[offset + 41]; // 0-100

    public byte TvaVeloSensitivity => mem[offset + 42]; // 0-100

    public byte TvaBiasPoint1 => mem[offset + 43]; // 0-127 (<1A-<7C >1A-7C)

    public byte TvaBiasLevel1 => mem[offset + 44]; // 0-12 (-12 - 0)

    public byte TvaBiasPoint2 => mem[offset + 45]; // 0-127 (<1A-<7C >1A-7C)

    public byte TvaBiasLevel2 => mem[offset + 46]; // 0-12 (-12 - 0)

    public byte TvaEnvTimeKeyfollow => mem[offset + 47]; // TIME KEY FOLLOW 0-4

    public byte TvaEnvTimeVeloSensitivity => mem[offset + 48]; // VELOS KEY FOLL0W 0-4

    public byte TvaEnvTime(int i) => mem[offset + 49 + i]; // [5] 0-100

    public byte TvaEnvLevel(int i) => mem[offset + 54 + i]; // [4] 0-100 // [3]: SUSTAIN LEVEL

    /// <summary>A copy of these 58 bytes in an array of their own, as the C structure assignment does.</summary>
    public PartialParam Copy() => new(mem.AsSpan(offset, Size).ToArray(), 0);

    public void CopyTo(PartialParam destination) => mem.AsSpan(offset, Size).CopyTo(destination.Memory.AsSpan(destination.Offset));
}

/// <summary>TimbreParam: the 14 common bytes, then four partials; 246 bytes.</summary>
internal readonly struct TimbreParam(byte[] mem, int offset)
{
    public const int Size = 14 + 4 * PartialParam.Size;

    public byte[] Memory => mem;

    public int Offset => offset;

    public ReadOnlySpan<byte> Name => mem.AsSpan(offset, 10);

    public byte PartialStructure12 => mem[offset + 10]; // 1 & 2  0-12 (1-13)

    public byte PartialStructure34 => mem[offset + 11]; // 3 & 4  0-12 (1-13)

    public byte PartialMute => mem[offset + 12]; // 0-15 (0000-1111)

    public byte NoSustain => mem[offset + 13]; // ENV MODE 0-1 (Normal, No sustain)

    public PartialParam Partial(int i) => new(mem, offset + 14 + i * PartialParam.Size);
}

/// <summary>PatchParam: 8 bytes.</summary>
internal readonly struct PatchParam(byte[] mem, int offset)
{
    public const int Size = 8;

    public byte[] Memory => mem;

    public int Offset => offset;

    public byte TimbreGroup // TIMBRE GROUP  0-3 (group A, group B, Memory, Rhythm)
    {
        get => mem[offset];
        set => mem[offset] = value;
    }

    public byte TimbreNum // TIMBRE NUMBER 0-63
    {
        get => mem[offset + 1];
        set => mem[offset + 1] = value;
    }

    public byte KeyShift // KEY SHIFT 0-48 (-24 - +24 semitones)
    {
        get => mem[offset + 2];
        set => mem[offset + 2] = value;
    }

    public byte FineTune // FINE TUNE 0-100 (-50 - +50 cents)
    {
        get => mem[offset + 3];
        set => mem[offset + 3] = value;
    }

    public byte BenderRange // BENDER RANGE 0-24
    {
        get => mem[offset + 4];
        set => mem[offset + 4] = value;
    }

    public byte AssignMode // ASSIGN MODE 0-3 (POLY1, POLY2, POLY3, POLY4)
    {
        get => mem[offset + 5];
        set => mem[offset + 5] = value;
    }

    public byte ReverbSwitch // REVERB SWITCH 0-1 (OFF,ON)
    {
        get => mem[offset + 6];
        set => mem[offset + 6] = value;
    }

    public byte Dummy
    {
        get => mem[offset + 7];
        set => mem[offset + 7] = value;
    }

    public void CopyTo(PatchParam destination) => mem.AsSpan(offset, Size).CopyTo(destination.Memory.AsSpan(destination.Offset));
}

/// <summary>MemParams::PatchTemp: a patch, its output level and panpot; 16 bytes.</summary>
internal readonly struct PatchTemp(byte[] mem, int offset)
{
    public const int Size = 16;

    public PatchParam Patch => new(mem, offset);

    public byte OutputLevel // OUTPUT LEVEL 0-100
    {
        get => mem[offset + 8];
        set => mem[offset + 8] = value;
    }

    public byte Panpot // PANPOT 0-14 (R-L)
    {
        get => mem[offset + 9];
        set => mem[offset + 9] = value;
    }
}

/// <summary>MemParams::RhythmTemp: 4 bytes.</summary>
internal readonly struct RhythmTemp(byte[] mem, int offset)
{
    public const int Size = 4;

    public byte Timbre // TIMBRE  0-94 (M1-M64,R1-30,OFF); LAPC-I: 0-127 (M01-M64,R01-R63)
    {
        get => mem[offset];
        set => mem[offset] = value;
    }

    public byte OutputLevel // OUTPUT LEVEL 0-100
    {
        get => mem[offset + 1];
        set => mem[offset + 1] = value;
    }

    public byte Panpot // PANPOT 0-14 (R-L)
    {
        get => mem[offset + 2];
        set => mem[offset + 2] = value;
    }

    public byte ReverbSwitch // REVERB SWITCH 0-1 (OFF,ON)
    {
        get => mem[offset + 3];
        set => mem[offset + 3] = value;
    }
}

/// <summary>MemParams::System: 23 bytes.</summary>
internal readonly struct SystemParams(byte[] mem, int offset)
{
    public const int Size = 23;

    public const int MasterTuneOff = 0;
    public const int ReverbModeOff = 1;
    public const int ReverbTimeOff = 2;
    public const int ReverbLevelOff = 3;
    public const int ReserveSettingsStartOff = 4;
    public const int ReserveSettingsEndOff = 12;
    public const int ChanAssignStartOff = 13;
    public const int ChanAssignEndOff = 21;
    public const int MasterVolOff = 22;

    public byte MasterTune // MASTER TUNE 0-127 432.1-457.6Hz
    {
        get => mem[offset];
        set => mem[offset] = value;
    }

    public byte ReverbMode // REVERB MODE 0-3 (room, hall, plate, tap delay)
    {
        get => mem[offset + 1];
        set => mem[offset + 1] = value;
    }

    public byte ReverbTime // REVERB TIME 0-7 (1-8)
    {
        get => mem[offset + 2];
        set => mem[offset + 2] = value;
    }

    public byte ReverbLevel // REVERB LEVEL 0-7 (1-8)
    {
        get => mem[offset + 3];
        set => mem[offset + 3] = value;
    }

    public byte ReserveSettings(int part) => mem[offset + 4 + part]; // PARTIAL RESERVE (PART 1) 0-32

    public byte ChanAssign(int part) => mem[offset + 13 + part]; // MIDI CHANNEL (PART1) 0-16 (1-16,OFF)

    public byte MasterVol // MASTER VOLUME 0-100
    {
        get => mem[offset + 22];
        set => mem[offset + 22] = value;
    }
}

/// <summary>
/// MemParams: the whole sysex-addressable memory. PatchTemp for parts 1-8 plus one for rhythm
/// (the LAPC-I documentation specified an additional area for rhythm at the end, where all
/// parameters but fine tune, assign mode and output level are ignored), the rhythm key map,
/// eight temporary timbres, 128 patches, 256 timbres padded to 256 bytes (group A, group B,
/// memory, rhythm; there are only 30 timbres in the "rhythm" bank for MT-32, the additional 34
/// are for LAPC-I and above), and the system area.
/// </summary>
internal sealed class MemParams
{
    public const int PatchTempOffset = 0;
    public const int PatchTempCount = 9;
    public const int RhythmTempOffset = PatchTempOffset + PatchTempCount * Mt32.PatchTemp.Size;
    public const int RhythmTempCount = 85;
    public const int TimbreTempOffset = RhythmTempOffset + RhythmTempCount * Mt32.RhythmTemp.Size;
    public const int TimbreTempCount = 8;
    public const int PatchesOffset = TimbreTempOffset + TimbreTempCount * TimbreParam.Size;
    public const int PatchesCount = 128;
    public const int PaddedTimbreSize = TimbreParam.Size + 10;
    public const int TimbresOffset = PatchesOffset + PatchesCount * PatchParam.Size;
    public const int TimbresCount = 64 + 64 + 64 + 64;
    public const int SystemOffset = TimbresOffset + TimbresCount * PaddedTimbreSize;
    public const int Size = SystemOffset + SystemParams.Size;

    public byte[] Bytes { get; } = new byte[Size];

    public PatchTemp PatchTemp(int i) => new(Bytes, PatchTempOffset + i * Mt32.PatchTemp.Size);

    public RhythmTemp RhythmTemp(int i) => new(Bytes, RhythmTempOffset + i * Mt32.RhythmTemp.Size);

    public TimbreParam TimbreTemp(int i) => new(Bytes, TimbreTempOffset + i * TimbreParam.Size);

    public PatchParam Patch(int i) => new(Bytes, PatchesOffset + i * PatchParam.Size);

    public TimbreParam Timbre(int i) => new(Bytes, TimbresOffset + i * PaddedTimbreSize);

    public SystemParams System => new(Bytes, SystemOffset);
}

/// <summary>SoundGroup: one entry of the control ROM's sound group table; 14 bytes.</summary>
internal static class SoundGroup
{
    public const int Size = 14;
}

/// <summary>The quirks and features that depend on the control ROM version. Port of ControlROMFeatureSet.</summary>
internal sealed record ControlROMFeatureSet(
    bool QuirkBasePitchOverflow,
    bool QuirkPitchEnvelopeOverflow,
    bool QuirkRingModulationNoMix,
    bool QuirkTVAZeroEnvLevels,
    bool QuirkPanMult,
    bool QuirkKeyShift,
    bool QuirkTVFBaseCutoffLimit,
    bool QuirkFastPitchChanges,
    bool QuirkDisplayCustomMessagePriority,
    bool OldMT32DisplayFeatures,
    // Features below don't actually depend on control ROM version, which is used to identify hardware model
    bool DefaultReverbMT32Compatible,
    bool OldMT32AnalogLPF);

/// <summary>Where the tables are in a control ROM version. Port of ControlROMMap.</summary>
internal sealed record ControlROMMap(
    string ShortName,
    ControlROMFeatureSet FeatureSet,
    ushort PcmTable, // 4 * pcmCount bytes
    ushort PcmCount,
    ushort TimbreAMap, // 128 bytes
    ushort TimbreAOffset,
    bool TimbreACompressed,
    ushort TimbreBMap, // 128 bytes
    ushort TimbreBOffset,
    bool TimbreBCompressed,
    ushort TimbreRMap, // 2 * timbreRCount bytes
    ushort TimbreRCount,
    ushort RhythmSettings, // 4 * rhythmSettingsCount bytes
    ushort RhythmSettingsCount,
    ushort ReserveSettings, // 9 bytes
    ushort PanSettings, // 8 bytes
    ushort ProgramSettings, // 8 bytes
    ushort RhythmMaxTable, // 4 bytes
    ushort PatchMaxTable, // 16 bytes
    ushort SystemMaxTable, // 23 bytes
    ushort TimbreMaxTable, // 72 bytes
    ushort SoundGroupsTable, // 14 bytes each entry
    ushort SoundGroupsCount,
    ushort StartupMessage, // 20 characters + NULL terminator
    ushort SysexErrorMessage); // 20 characters + NULL terminator

/// <summary>One entry of the control ROM's PCM table. Port of ControlROMPCMStruct.</summary>
internal readonly record struct ControlROMPCMStruct(byte Pos, byte Len, byte PitchLsb, byte PitchMsb);

/// <summary>A PCM wave of the PCM ROM, as the control ROM's table describes it. Port of PCMWaveEntry.</summary>
internal struct PCMWaveEntry
{
    public uint Addr;
    public uint Len;
    public bool Loop;
    public ControlROMPCMStruct? ControlROMPCMStruct;
}

/// <summary>
/// This is basically a per-partial, pre-processed combination of timbre and patch/rhythm
/// settings. Port of PatchCache.
/// </summary>
internal sealed class PatchCache
{
    public bool PlayPartial;
    public bool PCMPartial;
    public int Pcm;
    public byte Waveform;

    public uint StructureMix;
    public int StructurePosition;
    public int StructurePair;

    // The following fields are actually common to all partials in the timbre
    public bool Dirty;
    public uint PartialCount;
    public bool Sustain;
    public bool Reverb;

    public PartialParam SrcPartial = new(new byte[PartialParam.Size], 0);

    // The following directly points into live sysex-addressable memory
    public PartialParam PartialParam;

    public void CopyFrom(PatchCache other)
    {
        PlayPartial = other.PlayPartial;
        PCMPartial = other.PCMPartial;
        Pcm = other.Pcm;
        Waveform = other.Waveform;
        StructureMix = other.StructureMix;
        StructurePosition = other.StructurePosition;
        StructurePair = other.StructurePair;
        Dirty = other.Dirty;
        PartialCount = other.PartialCount;
        Sustain = other.Sustain;
        Reverb = other.Reverb;
        other.SrcPartial.CopyTo(SrcPartial);
        PartialParam = other.PartialParam;
    }
}
