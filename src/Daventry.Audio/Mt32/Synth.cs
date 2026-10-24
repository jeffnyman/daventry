// Port of Munt (libmt32emu) 2.7.0 as bundled with ScummVM.
// Copyright (C) 2003, 2004, 2005, 2006, 2008, 2009 Dean Beeler, Jerome Fisher
// Copyright (C) 2011-2022 Dean Beeler, Jerome Fisher, Sergey V. Mikayev
// Licensed under the GNU Lesser General Public License, version 2.1 or later.

namespace Daventry.Audio.Mt32;

/// <summary>
/// Methods for emulating the connection between the LA32 and the DAC, which involves some hacks
/// in the real devices for doubling the volume. See also
/// http://en.wikipedia.org/wiki/Roland_MT-32#Digital_overflow
/// </summary>
public enum DACInputMode
{
    /// <summary>
    /// Produces samples at double the volume, without tricks. Nicer overdrive characteristics than
    /// the DAC hacks (it simply clips samples within range). Higher quality than the real devices.
    /// </summary>
    Nice,

    /// <summary>
    /// Produces samples that exactly match the bits output from the emulated LA32. Much less likely
    /// to overdrive than any other mode. Half the volume of any of the other modes.
    /// </summary>
    Pure,

    /// <summary>Re-orders the LA32 output bits as in early generation MT-32s (according to Wikipedia).</summary>
    Generation1,

    /// <summary>Re-orders the LA32 output bits as in later generations (personally confirmed on my CM-32L - KG).</summary>
    Generation2,
}

/// <summary>Methods for emulating the effective delay of incoming MIDI messages introduced by a MIDI interface.</summary>
public enum MIDIDelayMode
{
    /// <summary>Process incoming MIDI events immediately. ScummVM's choice.</summary>
    Immediate,

    /// <summary>Delay incoming short MIDI messages as if they where transferred via a MIDI cable to a real hardware unit and immediate sysex processing.</summary>
    DelayShortMessagesOnly,

    /// <summary>Delay all incoming MIDI events as if they where transferred via a MIDI cable to a real hardware unit.</summary>
    DelayAll,
}

/// <summary>Callbacks the synth reports through. Port of ReportHandler / ReportHandler2; everything is a no-op by default.</summary>
internal class ReportHandler
{
    public virtual void PrintDebug(string message)
    {
    }

    /// <summary>Callback for reporting about displaying a new custom message on LCD.</summary>
    public virtual void ShowLCDMessage(string message)
    {
    }

    public virtual void OnErrorControlROM()
    {
    }

    public virtual void OnErrorPCMROM()
    {
    }

    public virtual void OnPolyStateChanged(byte partNum)
    {
    }

    public virtual void OnProgramChanged(byte partNum, string soundGroupName, string patchName)
    {
    }

    public virtual void OnLCDStateUpdated()
    {
    }
}

/// <summary>
/// The MT-32 / CM-32L: the control ROM's timbres and tables, the PCM ROM's samples, the
/// sysex-addressable memory, nine parts, 32 partials, the Boss reverb and the analog output
/// stage. Port of Synth.cpp with the 16-bit integer renderer (RendererType_BIT16S, Munt's and
/// ScummVM's default), rendering at the native 32 kHz.
/// </summary>
internal sealed class Synth
{
    public const int SampleRate = 32000;
    public const uint DefaultMaxPartials = 32;
    public const int MaxSamplesPerRun = 4096;
    public const uint DefaultMidiEventQueueSize = 1024;

    // This value isn't quite correct: the new-gen MT-32 control ROMs (ver. 2.XX) are twice as big.
    // Nevertheless, this is still relevant for library internal usage because the higher half of
    // those ROMs only contains the demo songs in all cases.
    public const int ControlRomSize = 64 * 1024;

    private const byte SysexManufacturerRoland = 0x41;
    private const byte SysexMdlMt32 = 0x16;
    private const byte SysexMdlD50 = 0x14;
    private const byte SysexCmdRq1 = 0x11; // Request data #1
    private const byte SysexCmdDt1 = 0x12; // Data set 1
    private const byte SysexCmdWsd = 0x40; // Want to send data
    private const byte SysexCmdRqd = 0x41; // Request data
    private const byte SysexCmdDat = 0x42; // Data set
    private const byte SysexCmdEod = 0x45; // End of data

    // MIDI interface data transfer rate in samples. Used to simulate the transfer delay.
    private const double MidiDataTransferRate = SampleRate / 31250.0 * 8.0;

    private static readonly ControlROMFeatureSet OldMt32Elder = new(true, true, true, true, true, true, true, false, true, true, true, true);
    private static readonly ControlROMFeatureSet OldMt32Later = new(true, true, true, true, true, true, true, false, false, true, true, true);
    private static readonly ControlROMFeatureSet NewMt32Compatible = new(false, false, false, false, false, false, false, false, false, false, false, false);
    private static readonly ControlROMFeatureSet Cm32lnCompatible = new(false, false, false, false, false, false, false, true, false, false, false, false);

    // (Note that old MT-32 ROMs actually have 86 entries for rhythmTemp)
    private static readonly ControlROMMap[] ControlROMMaps =
    [
        //  ID                   Features            PCMmap  PCMc  tmbrA   tmbrAO  tmbrAC tmbrB   tmbrBO  tmbrBC tmbrR   trC rhythm  rhyC  rsrv    panpot  prog    rhyMax  patMax  sysMax  timMax  sndGrp  sGC stMsg   sErMsg
        new("ctrl_mt32_1_04",   OldMt32Elder,      0x3000, 128, 0x8000, 0x0000, false, 0xC000, 0x4000, false, 0x3200, 30, 0x73A6, 85, 0x57C7, 0x57E2, 0x57D0, 0x5252, 0x525E, 0x526E, 0x520A, 0x7064, 19, 0x217A, 0x4BB6),
        new("ctrl_mt32_1_05",   OldMt32Elder,      0x3000, 128, 0x8000, 0x0000, false, 0xC000, 0x4000, false, 0x3200, 30, 0x7414, 85, 0x57C7, 0x57E2, 0x57D0, 0x5252, 0x525E, 0x526E, 0x520A, 0x70CA, 19, 0x217A, 0x4BB6),
        new("ctrl_mt32_1_06",   OldMt32Later,      0x3000, 128, 0x8000, 0x0000, false, 0xC000, 0x4000, false, 0x3200, 30, 0x7414, 85, 0x57D9, 0x57F4, 0x57E2, 0x5264, 0x5270, 0x5280, 0x521C, 0x70CA, 19, 0x217A, 0x4BBA),
        new("ctrl_mt32_1_07",   OldMt32Later,      0x3000, 128, 0x8000, 0x0000, false, 0xC000, 0x4000, false, 0x3200, 30, 0x73fe, 85, 0x57B1, 0x57CC, 0x57BA, 0x523C, 0x5248, 0x5258, 0x51F4, 0x70B0, 19, 0x217A, 0x4B92),
        new("ctrl_mt32_bluer",  OldMt32Later,      0x3000, 128, 0x8000, 0x0000, false, 0xC000, 0x4000, false, 0x3200, 30, 0x741C, 85, 0x57E5, 0x5800, 0x57EE, 0x5270, 0x527C, 0x528C, 0x5228, 0x70CE, 19, 0x217A, 0x4BC6),
        new("ctrl_mt32_2_03",   NewMt32Compatible, 0x8100, 128, 0x8000, 0x8000, true,  0x8080, 0x8000, true,  0x8500, 64, 0x8580, 85, 0x4F49, 0x4F64, 0x4F52, 0x4885, 0x4889, 0x48A2, 0x48B9, 0x5A44, 19, 0x1EF0, 0x4066),
        new("ctrl_mt32_2_04",   NewMt32Compatible, 0x8100, 128, 0x8000, 0x8000, true,  0x8080, 0x8000, true,  0x8500, 64, 0x8580, 85, 0x4F5D, 0x4F78, 0x4F66, 0x4899, 0x489D, 0x48B6, 0x48CD, 0x5A58, 19, 0x1EF0, 0x406D),
        new("ctrl_mt32_2_06",   NewMt32Compatible, 0x8100, 128, 0x8000, 0x8000, true,  0x8080, 0x8000, true,  0x8500, 64, 0x8580, 85, 0x4F69, 0x4F84, 0x4F72, 0x48A5, 0x48A9, 0x48C2, 0x48D9, 0x5A64, 19, 0x1EF0, 0x4021),
        new("ctrl_mt32_2_07",   NewMt32Compatible, 0x8100, 128, 0x8000, 0x8000, true,  0x8080, 0x8000, true,  0x8500, 64, 0x8580, 85, 0x4F81, 0x4F9C, 0x4F8A, 0x48B9, 0x48BD, 0x48D6, 0x48ED, 0x5A78, 19, 0x1EE7, 0x4035),
        new("ctrl_cm32l_1_00",  NewMt32Compatible, 0x8100, 256, 0x8000, 0x8000, true,  0x8080, 0x8000, true,  0x8500, 64, 0x8580, 85, 0x4F65, 0x4F80, 0x4F6E, 0x48A1, 0x48A5, 0x48BE, 0x48D5, 0x5A6C, 19, 0x1EF0, 0x401D),
        new("ctrl_cm32l_1_02",  NewMt32Compatible, 0x8100, 256, 0x8000, 0x8000, true,  0x8080, 0x8000, true,  0x8500, 64, 0x8580, 85, 0x4F93, 0x4FAE, 0x4F9C, 0x48CB, 0x48CF, 0x48E8, 0x48FF, 0x5A96, 19, 0x1EE7, 0x4047),
        new("ctrl_cm32ln_1_00", Cm32lnCompatible,  0x8100, 256, 0x8000, 0x8000, true,  0x8080, 0x8000, true,  0x8500, 64, 0x8580, 85, 0x4EC7, 0x4EE2, 0x4ED0, 0x47FF, 0x4803, 0x481C, 0x4833, 0x55A2, 19, 0x1F59, 0x3F7C),
    ];

    private readonly MemParams _mt32Default = new();
    private readonly Part?[] _parts = new Part?[9];
    private readonly BReverbModel?[] _reverbModels = new BReverbModel?[4];

    // Here we keep the reverse mapping of assigned parts per MIDI channel.
    // NOTE: value above 8 means that the channel is not assigned
    private readonly byte[,] _chantable = new byte[16, 9];

    // These buffers are used for building the output streams as they are found at the DAC
    // entrance. The output is mixed down to stereo interleaved further in the analog circuitry emulation.
    private readonly short[] _tmpNonReverbLeft = new short[MaxSamplesPerRun];
    private readonly short[] _tmpNonReverbRight = new short[MaxSamplesPerRun];
    private readonly short[] _tmpReverbDryLeft = new short[MaxSamplesPerRun];
    private readonly short[] _tmpReverbDryRight = new short[MaxSamplesPerRun];
    private readonly short[] _tmpReverbWetLeft = new short[MaxSamplesPerRun];
    private readonly short[] _tmpReverbWetRight = new short[MaxSamplesPerRun];

    private MemoryRegion? _patchTempMemoryRegion;
    private MemoryRegion? _rhythmTempMemoryRegion;
    private MemoryRegion? _timbreTempMemoryRegion;
    private MemoryRegion? _patchesMemoryRegion;
    private MemoryRegion? _timbresMemoryRegion;
    private MemoryRegion? _systemMemoryRegion;
    private MemoryRegion? _displayMemoryRegion;
    private MemoryRegion? _resetMemoryRegion;

    private readonly byte[] _soundGroupIx = new byte[128]; // For each standard timbre
    private byte[][] _soundGroupNames = [];

    private MidiEventQueue? _midiQueue;
    private uint _lastReceivedMIDIEventTimestamp;
    private BReverbModel? _reverbModel;
    private bool _reverbOverridden;
    private float _outputGain;
    private float _reverbOutputGain;
    private bool _opened;
    private bool _activated;
    private Analog? _analog;
    private Display? _display;

    // This stores the index of Part in chantable that failed to play and required partial abortion.
    private uint _abortingPartIx;

    public Synth(ReportHandler? reportHandler = null)
    {
        ReportHandler = reportHandler ?? new ReportHandler();
        SetOutputGain(1.0f);
        SetReverbOutputGain(1.0f);
    }

    public ReportHandler ReportHandler { get; }

    public MemParams Mt32Ram { get; } = new();

    public ControlROMFeatureSet ControlROMFeatures { get; private set; } = NewMt32Compatible;

    public ControlROMMap ControlROMMap { get; private set; } = ControlROMMaps[0];

    public byte[] ControlROMData { get; } = new byte[ControlRomSize];

    public short[] PcmROMData { get; private set; } = [];

    public PCMWaveEntry[] PcmWaves { get; private set; } = [];

    public uint PartialCount { get; private set; } = DefaultMaxPartials;

    public PartialManager PartialManager { get; private set; } = null!;

    // When a partial needs to be aborted to free it up for use by a new Poly, the controller will
    // busy-loop waiting for the sound to finish. We emulate this by delaying new MIDI events
    // processing until abortion finishes.
    public Poly? AbortingPoly { get; set; }

    public bool IsAbortingPoly => AbortingPoly is not null;

    public uint RenderedSampleCount { get; private set; }

    public bool IsOpen => _opened;

    public DACInputMode DACInputMode { get; set; } = DACInputMode.Nice;

    public MIDIDelayMode MIDIDelayMode { get; set; } = MIDIDelayMode.DelayShortMessagesOnly;

    public bool ReversedStereoEnabled { get; set; }

    // In this mode, we want to ensure that amp ramp never jumps to the target value and always
    // gradually increases or decreases. It seems that real units do not bother to always check if
    // a newly started ramp leads to a jump. We also prefer the quality improvement over the
    // emulation accuracy, so this mode is enabled by default.
    public bool NiceAmpRampEnabled { get; set; } = true;

    public bool NicePanningEnabled { get; set; }

    public bool NicePartialMixingEnabled { get; set; }

    public int MasterTunePitchDelta { get; private set; }

    public bool DisplayOldMT32Compatible { get; set; }

    /// <summary>
    /// Where the TVP's small timer jitter comes from; Munt calls rand(). Seeded so that runs repeat.
    /// </summary>
    public Random Random { get; set; } = new(1);

    public int Rand() => Random.Next();

    public bool IsReverbEnabled => _reverbModel is not null;

    public static short ClipSampleEx(int sampleEx)
    {
        // Clamp values above 32767 to 32767, and values below -32768 to -32768
        return sampleEx is >= -0x8000 and <= 0x7FFF ? (short)sampleEx : (short)((sampleEx >> 31) ^ 0x7FFF);
    }

    public static byte CalcSysexChecksum(ReadOnlySpan<byte> data, byte initChecksum = 0)
    {
        uint checksum = (uint)-initChecksum;
        foreach (byte b in data)
        {
            checksum -= b;
        }

        return (byte)(checksum & 0x7f);
    }

    public void PrintDebug(string message) => ReportHandler.PrintDebug(message);

    public void RhythmNotePlayed() => _display!.RhythmNotePlayed();

    public void VoicePartStateChanged(byte partNum, bool partActivated) => _display!.VoicePartStateChanged(partNum, partActivated);

    public void NewTimbreSet(uint partNum)
    {
        var part = GetPart((int)partNum);
        ReportHandler.OnProgramChanged((byte)partNum, NameOf(GetSoundGroupName(part)), part.CurrentInstr);
    }

    private static string NameOf(byte[] chars)
    {
        int n = Array.IndexOf(chars, (byte)0);
        return new string(chars.Take(n < 0 ? chars.Length : n).Select(b => (char)b).ToArray()).TrimEnd();
    }

    public byte[] GetSoundGroupName(Part part)
    {
        var patch = part.PatchTemp.Patch;
        return GetSoundGroupName(patch.TimbreGroup, patch.TimbreNum) ?? [];
    }

    private byte[]? GetSoundGroupName(byte timbreGroup, byte timbreNumber)
    {
        switch (timbreGroup)
        {
            case 1:
                timbreNumber += 64;
                goto case 0;
            case 0:
                return _soundGroupNames[_soundGroupIx[timbreNumber]];
            case 2:
                return _soundGroupNames[ControlROMMap.SoundGroupsCount - 2];
            case 3:
                return _soundGroupNames[ControlROMMap.SoundGroupsCount - 1];
            default:
                return null;
        }
    }

    // partNum should be 0..7 for Part 1..8, or 8 for Rhythm
    public Part GetPart(int partNum) => _parts[partNum]!;

    public void SetOutputGain(float newOutputGain)
    {
        if (newOutputGain < 0.0f)
        {
            newOutputGain = -newOutputGain;
        }

        _outputGain = newOutputGain;
        _analog?.SetSynthOutputGain(newOutputGain);
    }

    public void SetReverbOutputGain(float newReverbOutputGain)
    {
        if (newReverbOutputGain < 0.0f)
        {
            newReverbOutputGain = -newReverbOutputGain;
        }

        _reverbOutputGain = newReverbOutputGain;
        _analog?.SetReverbOutputGain(newReverbOutputGain, IsMT32ReverbCompatibilityMode);
    }

    public bool IsMT32ReverbCompatibilityMode => _opened && _reverbModels[(int)ReverbMode.Room]!.IsMT32Compatible(ReverbMode.Room);

    // ---- opening ----

    private bool LoadControlROM(Mt32RomImage controlROMImage)
    {
        var controlROMInfo = controlROMImage.Info;
        if (controlROMInfo.Type != Mt32RomType.Control || controlROMInfo.PairType != Mt32RomPairType.Full)
        {
            return false;
        }

        controlROMImage.Data.AsSpan(0, ControlRomSize).CopyTo(ControlROMData);

        // Control ROM successfully loaded, now check whether it's a known type
        foreach (var map in ControlROMMaps)
        {
            if (map.ShortName == controlROMInfo.ShortName)
            {
                ControlROMMap = map;
                ControlROMFeatures = map.FeatureSet;
                return true;
            }
        }

        return false;
    }

    private bool LoadPCMROM(Mt32RomImage pcmROMImage)
    {
        var pcmROMInfo = pcmROMImage.Info;
        if (pcmROMInfo.Type != Mt32RomType.Pcm || pcmROMInfo.PairType != Mt32RomPairType.Full)
        {
            return false;
        }

        var fileData = pcmROMImage.Data;
        if (fileData.Length != 2 * PcmROMData.Length)
        {
            return false;
        }

        ReadOnlySpan<int> order = [0, 9, 1, 2, 3, 4, 5, 6, 7, 10, 11, 12, 13, 14, 15, 8];
        for (int i = 0; i < PcmROMData.Length; i++)
        {
            byte s = fileData[i * 2];
            byte c = fileData[i * 2 + 1];

            int log = 0;
            for (int u = 0; u < 16; u++)
            {
                int bit;
                if (order[u] < 8)
                {
                    bit = (s >> (7 - order[u])) & 0x1;
                }
                else
                {
                    bit = (c >> (7 - (order[u] - 8))) & 0x1;
                }

                log |= bit << (15 - u);
            }

            PcmROMData[i] = (short)log;
        }

        return true;
    }

    private bool InitPCMList(ushort mapAddress, ushort count)
    {
        for (int i = 0; i < count; i++)
        {
            int tps = mapAddress + i * 4;
            var entry = new ControlROMPCMStruct(ControlROMData[tps], ControlROMData[tps + 1], ControlROMData[tps + 2], ControlROMData[tps + 3]);
            uint rAddr = entry.Pos * 0x800u;
            uint rLenExp = (uint)(entry.Len & 0x70) >> 4;
            uint rLen = 0x800u << (int)rLenExp;
            if (rAddr + rLen > PcmROMData.Length)
            {
                PrintDebug($"Control ROM error: Wave map entry {i} points to invalid PCM address 0x{rAddr:X4}, length 0x{rLen:X4}");
                return false;
            }

            PcmWaves[i].Addr = rAddr;
            PcmWaves[i].Len = rLen;
            PcmWaves[i].Loop = (entry.Len & 0x80) != 0;
            PcmWaves[i].ControlROMPCMStruct = entry;
        }

        return false;
    }

    private bool InitCompressedTimbre(int timbreNum, int src, int srcLen)
    {
        // "Compressed" here means that muted partials aren't present in ROM (except in the case of
        // partial 0 being muted). Instead the data from the previous unmuted partial is used.
        const int commonSize = 14;
        if (srcLen < commonSize)
        {
            return false;
        }

        var timbre = Mt32Ram.Timbre(timbreNum);
        _timbresMemoryRegion!.Write((uint)timbreNum, 0, ControlROMData.AsSpan(src), commonSize, true);
        int srcPos = commonSize;
        int memPos = commonSize;
        for (int t = 0; t < 4; t++)
        {
            if (t != 0 && ((timbre.PartialMute >> t) & 0x1) == 0x00)
            {
                // This partial is muted - we'll copy the previously copied partial, then
                srcPos -= PartialParam.Size;
            }
            else if (srcPos + PartialParam.Size >= srcLen)
            {
                return false;
            }

            _timbresMemoryRegion.Write((uint)timbreNum, (uint)memPos, ControlROMData.AsSpan(src + srcPos), PartialParam.Size);
            srcPos += PartialParam.Size;
            memPos += PartialParam.Size;
        }

        return true;
    }

    private bool InitTimbres(ushort mapAddress, ushort offset, ushort count, ushort startTimbre, bool compressed)
    {
        for (int i = 0; i < count * 2; i += 2)
        {
            int address = (ControlROMData[mapAddress + i + 1] << 8) | ControlROMData[mapAddress + i];
            if (!compressed && address + offset + TimbreParam.Size > ControlRomSize)
            {
                PrintDebug($"Control ROM error: Timbre map entry 0x{i:x4} for timbre {startTimbre} points to invalid timbre address 0x{address:x4}");
                return false;
            }

            address += offset;
            address &= 0xFFFF;
            if (compressed)
            {
                if (!InitCompressedTimbre(startTimbre, address, ControlRomSize - address))
                {
                    PrintDebug($"Control ROM error: Timbre map entry 0x{i:x4} for timbre {startTimbre} points to invalid timbre at 0x{address:x4}");
                    return false;
                }
            }
            else
            {
                _timbresMemoryRegion!.Write(startTimbre, 0, ControlROMData.AsSpan(address), TimbreParam.Size, true);
            }

            startTimbre++;
        }

        return true;
    }

    private void InitReverbModels(bool mt32CompatibleMode)
    {
        for (int mode = (int)ReverbMode.Room; mode <= (int)ReverbMode.TapDelay; mode++)
        {
            _reverbModels[mode] = new BReverbModel((ReverbMode)mode, mt32CompatibleMode);
        }
    }

    private void InitSoundGroups()
    {
        ControlROMData.AsSpan(ControlROMMap.SoundGroupsTable - _soundGroupIx.Length, _soundGroupIx.Length).CopyTo(_soundGroupIx);
        _soundGroupNames = new byte[ControlROMMap.SoundGroupsCount][];
        for (int i = 0; i < ControlROMMap.SoundGroupsCount; i++)
        {
            // SoundGroup: timbreNumberTableAddrLow, timbreNumberTableAddrHigh, displayPosition, name[9], timbreCount, pad
            _soundGroupNames[i] = ControlROMData.AsSpan(ControlROMMap.SoundGroupsTable + i * SoundGroup.Size + 3, 9).ToArray();
        }
    }

    private void InitMemoryRegions()
    {
        // Timbre max tables are slightly more complicated than the others, which are used directly
        // from the ROM. The ROM (sensibly) just has maximums for TimbreParam.commonParam followed
        // by just one TimbreParam.partialParam, so we produce a table with all partialParams
        // filled out, as well as padding for PaddedTimbre, for quick lookup.
        const int commonSize = 14;
        var paddedTimbreMaxTable = new byte[MemParams.PaddedTimbreSize];
        ControlROMData.AsSpan(ControlROMMap.TimbreMaxTable, commonSize + PartialParam.Size).CopyTo(paddedTimbreMaxTable); // commonParam and one partialParam
        int pos = commonSize + PartialParam.Size;
        for (int i = 0; i < 3; i++)
        {
            ControlROMData.AsSpan(ControlROMMap.TimbreMaxTable + commonSize, PartialParam.Size).CopyTo(paddedTimbreMaxTable.AsSpan(pos));
            pos += PartialParam.Size;
        }

        // the last 10 bytes stay 0: padding
        _patchTempMemoryRegion = MemoryRegion.PatchTemp(Mt32Ram, ControlROMData, ControlROMMap.PatchMaxTable);
        _rhythmTempMemoryRegion = MemoryRegion.RhythmTemp(Mt32Ram, ControlROMData, ControlROMMap.RhythmMaxTable);
        _timbreTempMemoryRegion = MemoryRegion.TimbreTemp(Mt32Ram, paddedTimbreMaxTable);
        _patchesMemoryRegion = MemoryRegion.Patches(Mt32Ram, ControlROMData, ControlROMMap.PatchMaxTable);
        _timbresMemoryRegion = MemoryRegion.Timbres(Mt32Ram, paddedTimbreMaxTable);
        _systemMemoryRegion = MemoryRegion.System(Mt32Ram, ControlROMData, ControlROMMap.SystemMaxTable);
        _displayMemoryRegion = MemoryRegion.Display();
        _resetMemoryRegion = MemoryRegion.Reset();
    }

    /// <summary>
    /// Used to initialise the MT-32. Must be called before any other function. Returns true if
    /// initialization was successful, otherwise returns false.
    /// </summary>
    public bool Open(Mt32RomImage controlROMImage, Mt32RomImage pcmROMImage, uint usePartialCount = DefaultMaxPartials, AnalogOutputMode analogOutputMode = AnalogOutputMode.Coarse)
    {
        if (_opened)
        {
            return false;
        }

        PartialCount = usePartialCount;
        AbortingPoly = null;
        _abortingPartIx = 0;

        // This is to help detect bugs
        Array.Fill(Mt32Ram.Bytes, (byte)'?');

        if (!LoadControlROM(controlROMImage))
        {
            PrintDebug("Init Error - Missing or invalid Control ROM image");
            ReportHandler.OnErrorControlROM();
            Dispose();
            return false;
        }

        InitMemoryRegions();

        // 512KB PCM ROM for MT-32, etc.
        // 1MB PCM ROM for CM-32L, LAPC-I, CM-64, CM-500
        // Note that the size below is given in samples (16-bit), not bytes
        PcmROMData = new short[ControlROMMap.PcmCount == 256 ? 512 * 1024 : 256 * 1024];

        if (!LoadPCMROM(pcmROMImage))
        {
            PrintDebug("Init Error - Missing PCM ROM image");
            ReportHandler.OnErrorPCMROM();
            Dispose();
            return false;
        }

        InitReverbModels(ControlROMFeatures.DefaultReverbMT32Compatible);

        if (!InitTimbres(ControlROMMap.TimbreAMap, ControlROMMap.TimbreAOffset, 0x40, 0, ControlROMMap.TimbreACompressed))
        {
            Dispose();
            return false;
        }

        if (!InitTimbres(ControlROMMap.TimbreBMap, ControlROMMap.TimbreBOffset, 0x40, 64, ControlROMMap.TimbreBCompressed))
        {
            Dispose();
            return false;
        }

        if (!InitTimbres(ControlROMMap.TimbreRMap, 0, ControlROMMap.TimbreRCount, 192, true))
        {
            Dispose();
            return false;
        }

        if (ControlROMMap.TimbreRCount == 30)
        {
            // We must initialise all 64 rhythm timbres to avoid undefined behaviour.
            // SEMI-CONFIRMED: Old-gen MT-32 units likely map timbres 30..59 to 0..29. Attempts to
            // play rhythm timbres 60..63 exhibit undefined behaviour. We want to emulate the wrap
            // around, so merely copy the entire set of standard timbres once more. The last 4
            // dangerous timbres are zeroed out.
            var bytes = Mt32Ram.Bytes;
            int timbre192 = MemParams.TimbresOffset + 192 * MemParams.PaddedTimbreSize;
            int timbre222 = MemParams.TimbresOffset + 222 * MemParams.PaddedTimbreSize;
            int timbre252 = MemParams.TimbresOffset + 252 * MemParams.PaddedTimbreSize;
            Array.Copy(bytes, timbre192, bytes, timbre222, 30 * MemParams.PaddedTimbreSize);
            Array.Clear(bytes, timbre252, 4 * MemParams.PaddedTimbreSize);
        }

        // CM-64 seems to initialise all bytes in this bank to 0.
        Array.Clear(Mt32Ram.Bytes, MemParams.TimbresOffset + 128 * MemParams.PaddedTimbreSize, 64 * MemParams.PaddedTimbreSize);

        var parts = new Part[9];
        PartialManager = new PartialManager(this, parts);

        PcmWaves = new PCMWaveEntry[ControlROMMap.PcmCount];

        InitPCMList(ControlROMMap.PcmTable, ControlROMMap.PcmCount);

        ControlROMData.AsSpan(ControlROMMap.RhythmSettings, ControlROMMap.RhythmSettingsCount * 4).CopyTo(Mt32Ram.Bytes.AsSpan(MemParams.RhythmTempOffset));

        for (int i = 0; i < 128; i++)
        {
            var patch = Mt32Ram.Patch(i);
            patch.TimbreGroup = (byte)(i / 64);
            patch.TimbreNum = (byte)(i % 64);
            patch.KeyShift = 24;
            patch.FineTune = 50;
            patch.BenderRange = 12;
            patch.AssignMode = 0;
            patch.ReverbSwitch = 1;
            patch.Dummy = 0;
        }

        // The MT-32 manual claims that "Standard pitch" is 442Hz.
        var system = Mt32Ram.System;
        system.MasterTune = 0x4A; // Confirmed on CM-64
        system.ReverbMode = 0; // Confirmed
        system.ReverbTime = 5; // Confirmed
        system.ReverbLevel = 3; // Confirmed
        ControlROMData.AsSpan(ControlROMMap.ReserveSettings, 9).CopyTo(Mt32Ram.Bytes.AsSpan(MemParams.SystemOffset + SystemParams.ReserveSettingsStartOff)); // Confirmed
        for (int i = 0; i < 9; i++)
        {
            // This is the default: {1, 2, 3, 4, 5, 6, 7, 8, 9}
            // An alternative configuration can be selected by holding "Master Volume" and pressing
            // "PART button 1" on the real MT-32's frontpanel. The channel assignment is then
            // {0, 1, 2, 3, 4, 5, 6, 7, 9}
            Mt32Ram.Bytes[MemParams.SystemOffset + SystemParams.ChanAssignStartOff + i] = (byte)(i + 1);
        }

        system.MasterVol = 100; // Confirmed

        bool oldReverbOverridden = _reverbOverridden;
        _reverbOverridden = false;
        RefreshSystem();
        ResetMasterTunePitchDelta();
        _reverbOverridden = oldReverbOverridden;

        InitSoundGroups();

        for (int i = 0; i < 9; i++)
        {
            var patchTemp = Mt32Ram.PatchTemp(i);

            // Note that except for the rhythm part, these patch fields will be set in setProgram() below anyway.
            var patch = patchTemp.Patch;
            patch.TimbreGroup = 0;
            patch.TimbreNum = 0;
            patch.KeyShift = 24;
            patch.FineTune = 50;
            patch.BenderRange = 12;
            patch.AssignMode = 0;
            patch.ReverbSwitch = 1;
            patch.Dummy = 0;

            patchTemp.OutputLevel = 80;
            patchTemp.Panpot = ControlROMData[ControlROMMap.PanSettings + i];
            int dummyv = MemParams.PatchTempOffset + i * PatchTemp.Size + 10;
            Array.Clear(Mt32Ram.Bytes, dummyv, 6);
            Mt32Ram.Bytes[dummyv + 1] = 127;

            if (i < 8)
            {
                parts[i] = new Part(this, (uint)i);
                _parts[i] = parts[i];
                parts[i].SetProgram(ControlROMData[ControlROMMap.ProgramSettings + i]);
            }
            else
            {
                parts[i] = new RhythmPart(this, (uint)i);
                _parts[i] = parts[i];
            }
        }

        // For resetting mt32 mid-execution
        Mt32Ram.Bytes.CopyTo(_mt32Default.Bytes, 0);

        _midiQueue = new MidiEventQueue(DefaultMidiEventQueueSize);

        _analog = new Analog(analogOutputMode, ControlROMFeatures.OldMT32AnalogLPF);
        SetOutputGain(_outputGain);
        SetReverbOutputGain(_reverbOutputGain);

        _display = new Display(this);
        DisplayOldMT32Compatible = ControlROMFeatures.OldMT32DisplayFeatures;

        _opened = true;
        _activated = false;

        // the reverb gain depends on the reverb model, which exists only now
        SetReverbOutputGain(_reverbOutputGain);
        return true;
    }

    private void Dispose()
    {
        _opened = false;
        _display = null;
        _midiQueue = null;
        _analog = null;
        Array.Clear(_parts);
        PcmWaves = [];
        PcmROMData = [];
        Array.Clear(_reverbModels);
        _reverbModel = null;
    }

    public void Close()
    {
        if (_opened)
        {
            Dispose();
        }
    }

    // ---- MIDI input ----

    private static uint GetShortMessageLength(uint msg)
    {
        if ((msg & 0xF0) == 0xF0)
        {
            return (msg & 0xFF) switch
            {
                0xF1 or 0xF3 => 2,
                0xF2 => 3,
                _ => 1,
            };
        }

        // NOTE: This calculation isn't quite correct as it doesn't consider the running status byte
        return (msg & 0xE0) == 0xC0 ? 2u : 3u;
    }

    private uint AddMIDIInterfaceDelay(uint len, uint timestamp)
    {
        uint transferTime = (uint)(len * MidiDataTransferRate);
        // Dealing with wrapping
        if ((int)(timestamp - _lastReceivedMIDIEventTimestamp) < 0)
        {
            timestamp = _lastReceivedMIDIEventTimestamp;
        }

        timestamp += transferTime;
        _lastReceivedMIDIEventTimestamp = timestamp;
        return timestamp;
    }

    /// <summary>Enqueues a single short MIDI message to be processed ASAP. The message must contain a status byte.</summary>
    public bool PlayMsg(uint msg) => PlayMsg(msg, RenderedSampleCount);

    public bool PlayMsg(uint msg, uint timestamp)
    {
        if ((msg & 0xF8) == 0xF8)
        {
            return true;
        }

        if (_midiQueue is null)
        {
            return false;
        }

        if (MIDIDelayMode != MIDIDelayMode.Immediate)
        {
            timestamp = AddMIDIInterfaceDelay(GetShortMessageLength(msg), timestamp);
        }

        if (!_activated)
        {
            _activated = true;
        }

        return _midiQueue.PushShortMessage(msg, timestamp);
    }

    /// <summary>Enqueues a single well formed System Exclusive MIDI message to be processed ASAP.</summary>
    public bool PlaySysex(ReadOnlySpan<byte> sysex) => PlaySysex(sysex, RenderedSampleCount);

    public bool PlaySysex(ReadOnlySpan<byte> sysex, uint timestamp)
    {
        if (_midiQueue is null)
        {
            return false;
        }

        if (MIDIDelayMode == MIDIDelayMode.DelayAll)
        {
            timestamp = AddMIDIInterfaceDelay((uint)sysex.Length, timestamp);
        }

        if (!_activated)
        {
            _activated = true;
        }

        return _midiQueue.PushSysex(sysex, timestamp);
    }

    /// <summary>Sends a short MIDI message to the synth for immediate playback. The message must contain a status byte.</summary>
    public void PlayMsgNow(uint msg)
    {
        if (!_opened)
        {
            return;
        }

        // NOTE: Active sense IS implemented in real hardware. However, realtime processing is
        // clearly out of the library scope. It is assumed that realtime consumers of the library
        // respond to these MIDI events as appropriate.

        byte code = (byte)((msg & 0x0000F0) >> 4);
        byte chan = (byte)(msg & 0x00000F);
        byte note = (byte)((msg & 0x007F00) >> 8);
        byte velocity = (byte)((msg & 0x7F0000) >> 16);

        if (_chantable[chan, 0] > 8)
        {
            return;
        }

        for (uint i = _abortingPartIx; i <= 8; i++)
        {
            uint partNum = _chantable[chan, i];
            if (partNum > 8)
            {
                break;
            }

            PlayMsgOnPart((byte)partNum, code, note, velocity);
            if (IsAbortingPoly)
            {
                _abortingPartIx = i;
                break;
            }
            else if (_abortingPartIx != 0)
            {
                _abortingPartIx = 0;
            }
        }
    }

    /// <summary>Sends unpacked short MIDI message to the synth for immediate playback. The message must contain a status byte.</summary>
    public void PlayMsgOnPart(byte part, byte code, byte note, byte velocity)
    {
        if (!_opened)
        {
            return;
        }

        var p = _parts[part]!;
        if (!_activated)
        {
            _activated = true;
        }

        switch (code)
        {
            case 0x8:
                // The MT-32 ignores velocity for note off
                p.NoteOff(note);
                break;
            case 0x9:
                if (velocity == 0)
                {
                    // MIDI defines note-on with velocity 0 as being the same as note-off with velocity 40
                    p.NoteOff(note);
                }
                else if (p.VolumeOverride > 0)
                {
                    p.NoteOn(note, velocity);
                }

                break;
            case 0xB: // Control change
                switch (note)
                {
                    case 0x01: // Modulation
                        p.SetModulation(velocity);
                        break;
                    case 0x06:
                        p.SetDataEntryMSB(velocity);
                        break;
                    case 0x07: // Set volume
                        p.SetVolume(velocity);
                        break;
                    case 0x0A: // Pan
                        p.SetPan(velocity);
                        break;
                    case 0x0B:
                        p.SetExpression(velocity);
                        break;
                    case 0x40: // Hold (sustain) pedal
                        p.SetHoldPedal(velocity >= 64);
                        break;
                    case 0x62:
                    case 0x63:
                        p.SetNRPN();
                        break;
                    case 0x64:
                        p.SetRPNLSB(velocity);
                        break;
                    case 0x65:
                        p.SetRPNMSB(velocity);
                        break;
                    case 0x79: // Reset all controllers
                        p.ResetAllControllers();
                        break;
                    case 0x7B: // All notes off
                        p.AllNotesOff();
                        break;
                    case 0x7C:
                    case 0x7D:
                    case 0x7E:
                    case 0x7F:
                        // CONFIRMED:Mok: A real LAPC-I responds to these controllers as follows:
                        p.SetHoldPedal(false);
                        p.AllNotesOff();
                        break;
                    default:
                        return;
                }

                _display!.MidiMessagePlayed();
                break;
            case 0xC: // Program change
                p.SetProgram(note);
                if (part < 8)
                {
                    _display!.MidiMessagePlayed();
                    _display.ProgramChanged(part);
                }

                break;
            case 0xE: // Pitch bender
                uint bend = ((uint)velocity << 7) | note;
                p.SetBend(bend);
                _display!.MidiMessagePlayed();
                break;
            default:
                return;
        }
    }

    /// <summary>Sends a single well formed System Exclusive MIDI message for immediate processing.</summary>
    public void PlaySysexNow(ReadOnlySpan<byte> sysex)
    {
        int len = sysex.Length;
        if (len < 2)
        {
            PrintDebug($"playSysex: Message is too short for sysex ({len} bytes)");
        }

        if (len == 0 || sysex[0] != 0xF0)
        {
            PrintDebug("playSysex: Message lacks start-of-sysex (0xF0)");
            return;
        }

        // Due to some programs (e.g. Java) sending buffers with junk at the end, we have to go
        // through and find the end marker rather than relying on len.
        int endPos;
        for (endPos = 1; endPos < len; endPos++)
        {
            if (sysex[endPos] == 0xF7)
            {
                break;
            }
        }

        if (endPos == len)
        {
            PrintDebug("playSysex: Message lacks end-of-sysex (0xf7)");
            return;
        }

        PlaySysexWithoutFraming(sysex.Slice(1, endPos - 1));
    }

    /// <summary>Sends inner body of a System Exclusive MIDI message for direct processing.</summary>
    public void PlaySysexWithoutFraming(ReadOnlySpan<byte> sysex)
    {
        if (sysex.Length < 4)
        {
            PrintDebug($"playSysexWithoutFraming: Message is too short ({sysex.Length} bytes)!");
            return;
        }

        if (sysex[0] != SysexManufacturerRoland)
        {
            PrintDebug($"playSysexWithoutFraming: Header not intended for this device manufacturer: {sysex[0]:x2} {sysex[1]:x2} {sysex[2]:x2} {sysex[3]:x2}");
            return;
        }

        if (sysex[2] == SysexMdlD50)
        {
            PrintDebug($"playSysexWithoutFraming: Header is intended for model D-50 (not yet supported): {sysex[0]:x2} {sysex[1]:x2} {sysex[2]:x2} {sysex[3]:x2}");
            return;
        }
        else if (sysex[2] != SysexMdlMt32)
        {
            PrintDebug($"playSysexWithoutFraming: Header not intended for model MT-32: {sysex[0]:x2} {sysex[1]:x2} {sysex[2]:x2} {sysex[3]:x2}");
            return;
        }

        PlaySysexWithoutHeader(sysex[1], sysex[3], sysex[4..]);
    }

    /// <summary>Sends inner body of a System Exclusive MIDI message for direct processing.</summary>
    public void PlaySysexWithoutHeader(byte device, byte command, ReadOnlySpan<byte> sysex)
    {
        if (device > 0x10)
        {
            // We have device ID 0x10 (default, but changeable, on real MT-32), < 0x10 is for channels
            PrintDebug($"playSysexWithoutHeader: Message is not intended for this device ID (provided: {device:x2}, expected: 0x10 or channel)");
            return;
        }

        // All models process the checksum before anything else and ignore messages lacking the
        // checksum, or containing the checksum only.
        if (sysex.Length < 2)
        {
            PrintDebug($"playSysexWithoutHeader: Message is too short ({sysex.Length} bytes)!");
            return;
        }

        byte checksum = CalcSysexChecksum(sysex[..^1]);
        if (checksum != sysex[^1])
        {
            PrintDebug($"playSysexWithoutHeader: Message checksum is incorrect (provided: {sysex[^1]:x2}, expected: {checksum:x2})!");
            if (_opened)
            {
                _display!.ChecksumErrorOccurred();
            }

            return;
        }

        sysex = sysex[..^1]; // Exclude checksum

        if (command == SysexCmdEod)
        {
            return;
        }

        switch (command)
        {
            case SysexCmdWsd:
                break;
            case SysexCmdDat:
            case SysexCmdDt1:
                WriteSysex(device, sysex);
                break;
            case SysexCmdRqd:
                if (HasActivePartials)
                {
                    PrintDebug("playSysexWithoutHeader: Got SYSEX_CMD_RQD but partials are active - ignoring");
                    // FIXME: We should send SYSEX_CMD_RJC in this case
                }

                // readSysex is not implemented in Munt either
                break;
            case SysexCmdRq1:
                break;
            default:
                PrintDebug($"playSysexWithoutHeader: Unsupported command {command:x2}");
                return;
        }
    }

    /// <summary>Sends inner body of a System Exclusive MIDI message for direct processing: an address and data.</summary>
    public void WriteSysex(byte device, ReadOnlySpan<byte> sysex)
    {
        if (!_opened || sysex.Length < 1)
        {
            return;
        }

        // This is checked early in the real devices (before any sysex length checks or further processing)
        if (sysex[0] == 0x7F)
        {
            if (!DisplayOldMT32Compatible)
            {
                _display!.MidiMessagePlayed();
            }

            Reset();
            return;
        }

        _display!.MidiMessagePlayed();

        if (sysex.Length < 3)
        {
            // A short message of just 1 or 2 bytes may be written to the display area yet it may
            // cause a user-visible effect, similarly to the reset area.
            if (sysex[0] == 0x20)
            {
                _display.DisplayControlMessageReceived(sysex, (uint)sysex.Length);
                return;
            }

            PrintDebug($"writeSysex: Message is too short ({sysex.Length} bytes)!");
            return;
        }

        uint addr = ((uint)sysex[0] << 16) | ((uint)sysex[1] << 8) | sysex[2];
        addr = MemAddr.MemAddress(addr);
        sysex = sysex[3..];

        // NOTE: Please keep both lower and upper bounds in each check, for ease of reading

        // Process channel-specific sysex by converting it to device-global
        if (device < 0x10)
        {
            if (addr < MemAddr.MemAddress(0x010000))
            {
                addr += MemAddr.MemAddress(0x030000);
                if (_chantable[device, 0] <= 8)
                {
                    for (int partIx = 0; partIx <= 8; partIx++)
                    {
                        if (_chantable[device, partIx] > 8)
                        {
                            break;
                        }

                        uint offset = _chantable[device, partIx] == 8 ? 0 : (uint)(_chantable[device, partIx] * PatchTemp.Size);
                        WriteSysexGlobal(addr + offset, sysex);
                    }

                    return;
                }
            }
            else if (addr < MemAddr.MemAddress(0x020000))
            {
                addr += MemAddr.MemAddress(0x030110) - MemAddr.MemAddress(0x010000);
            }
            else if (addr < MemAddr.MemAddress(0x030000))
            {
                addr += MemAddr.MemAddress(0x040000) - MemAddr.MemAddress(0x020000);
                if (_chantable[device, 0] <= 8)
                {
                    for (int partIx = 0; partIx <= 8; partIx++)
                    {
                        if (_chantable[device, partIx] > 8)
                        {
                            break;
                        }

                        uint offset = _chantable[device, partIx] == 8 ? 0 : (uint)(_chantable[device, partIx] * TimbreParam.Size);
                        WriteSysexGlobal(addr + offset, sysex);
                    }

                    return;
                }
            }
            else
            {
                return;
            }
        }

        WriteSysexGlobal(addr, sysex);
    }

    // Process device-global sysex (possibly converted from channel-specific sysex above)
    private void WriteSysexGlobal(uint addr, ReadOnlySpan<byte> sysex)
    {
        uint len = (uint)sysex.Length;
        for (;;)
        {
            // Find the appropriate memory region
            var region = FindMemoryRegion(addr);

            if (region is null)
            {
                PrintDebug($"Sysex write to unrecognised address {MemAddr.SysexMemAddress(addr):x6}, len {len}");
                // FIXME: Real devices may respond differently to a long SysEx that covers adjacent regions.
                break;
            }

            WriteMemoryRegion(region, addr, region.GetClampedLen(addr, len), sysex);

            uint next = region.Next(addr, len);
            if (next == 0)
            {
                break;
            }

            addr += next;
            sysex = sysex[(int)next..];
            len -= next;
        }
    }

    private MemoryRegion? FindMemoryRegion(uint addr)
    {
        foreach (var region in new[] { _patchTempMemoryRegion, _rhythmTempMemoryRegion, _timbreTempMemoryRegion, _patchesMemoryRegion, _timbresMemoryRegion, _systemMemoryRegion, _displayMemoryRegion, _resetMemoryRegion })
        {
            if (region is not null && region.Contains(addr))
            {
                return region;
            }
        }

        return null;
    }

    /// <summary>Stores internal state of emulated synth into an array provided (as it would be acquired from hardware).</summary>
    public void ReadMemory(uint addr, uint len, Span<byte> data)
    {
        if (!_opened || FindMemoryRegion(addr) is not { } region)
        {
            return;
        }

        uint first = (uint)region.FirstTouched(addr);
        uint off = (uint)region.FirstTouchedOffset(addr);
        len = region.GetClampedLen(addr, len);

        if (region.IsReadable)
        {
            region.Read(first, off, data, len);
        }
        else
        {
            // FIXME: We might want to do these properly in future
            for (int m = 0; m < len; m += 2)
            {
                data[m] = 0xff;
                if (m + 1 < len)
                {
                    data[m + 1] = (byte)region.Type;
                }
            }
        }
    }

    private void WriteMemoryRegion(MemoryRegion region, uint addr, uint len, ReadOnlySpan<byte> data)
    {
        uint first = (uint)region.FirstTouched(addr);
        uint last = (uint)region.LastTouched(addr, len);
        uint off = (uint)region.FirstTouchedOffset(addr);
        switch (region.Type)
        {
            case MemoryRegionType.PatchTemp:
                region.Write(first, off, data, len);
                for (uint i = first; i <= last; i++)
                {
                    if (_parts[i] is { } part)
                    {
                        if (i != 8)
                        {
                            // Note: Confirmed on CM-64 that we definitely *should* update the
                            // timbre here, but only in the case that the sysex actually writes to
                            // those values
                            if (!(i == first && off > 2))
                            {
                                part.SetTimbre(Mt32Ram.Timbre((int)part.GetAbsTimbreNum()));
                            }
                        }

                        part.Refresh();
                    }
                }

                break;
            case MemoryRegionType.RhythmTemp:
                region.Write(first, off, data, len);
                _parts[8]?.Refresh();
                break;
            case MemoryRegionType.TimbreTemp:
                region.Write(first, off, data, len);
                for (uint i = first; i <= last; i++)
                {
                    _parts[i]?.Refresh();
                }

                break;
            case MemoryRegionType.Patches:
                region.Write(first, off, data, len);
                break;
            case MemoryRegionType.Timbres:
                // Timbres
                first += 128;
                last += 128;
                region.Write(first, off, data, len);
                for (uint i = first; i <= last; i++)
                {
                    // FIXME:KG: Not sure if the stuff below should be done (for rhythm and/or
                    // parts)... Does the real MT-32 automatically do this?
                    for (int part = 0; part < 9; part++)
                    {
                        _parts[part]?.RefreshTimbre(i);
                    }
                }

                break;
            case MemoryRegionType.System:
                region.Write(0, off, data, len);

                // FIXME: We haven't properly confirmed any of this behaviour. In particular, we
                // tend to reset things such as reverb even if the write contained the same
                // parameters as were already set, which may be wrong. On the other hand, the real
                // thing could be resetting things even when they aren't touched by the write at all.
                if (off <= SystemParams.MasterTuneOff && off + len > SystemParams.MasterTuneOff)
                {
                    RefreshSystemMasterTune();
                }

                if (off <= SystemParams.ReverbLevelOff && off + len > SystemParams.ReverbModeOff)
                {
                    RefreshSystemReverbParameters();
                }

                if (off <= SystemParams.ReserveSettingsEndOff && off + len > SystemParams.ReserveSettingsStartOff)
                {
                    RefreshSystemReserveSettings();
                }

                if (off <= SystemParams.ChanAssignEndOff && off + len > SystemParams.ChanAssignStartOff)
                {
                    int firstPart = (int)off - SystemParams.ChanAssignStartOff;
                    if (firstPart < 0)
                    {
                        firstPart = 0;
                    }

                    int lastPart = (int)(off + len) - SystemParams.ChanAssignStartOff;
                    if (lastPart > 8)
                    {
                        lastPart = 8;
                    }

                    RefreshSystemChanAssign((byte)firstPart, (byte)lastPart);
                }

                if (off <= SystemParams.MasterVolOff && off + len > SystemParams.MasterVolOff)
                {
                    RefreshSystemMasterVol();
                }

                break;
            case MemoryRegionType.Display:
                if (len > Display.LcdTextSize)
                {
                    len = Display.LcdTextSize;
                }

                if (!_display!.CustomDisplayMessageReceived(data, off, len))
                {
                    break;
                }

                // Holds zero-terminated string of the maximum length.
                var chars = new char[len];
                int n = 0;
                for (int i = 0; i < len && data[i] != 0; i++)
                {
                    chars[n++] = (char)data[i];
                }

                ReportHandler.ShowLCDMessage(new string(chars, 0, n));
                break;
            case MemoryRegionType.Reset:
                Reset();
                break;
        }
    }

    private void RefreshSystemMasterTune()
    {
        // 171 is ~half a semitone.
        MasterTunePitchDelta = ((Mt32Ram.System.MasterTune - 64) * 171) >> 6; // PORTABILITY NOTE: Assumes arithmetic shift.
    }

    private void RefreshSystemReverbParameters()
    {
        if (_reverbOverridden)
        {
            return;
        }

        var system = Mt32Ram.System;
        var oldReverbModel = _reverbModel;
        if (system.ReverbTime == 0 && system.ReverbLevel == 0)
        {
            // Setting both time and level to 0 effectively disables wet reverb output on real
            // devices. Take a shortcut in this case to reduce CPU load.
            _reverbModel = null;
        }
        else
        {
            _reverbModel = _reverbModels[system.ReverbMode];
        }

        if (!ReferenceEquals(_reverbModel, oldReverbModel))
        {
            oldReverbModel?.Close();
            _reverbModel?.Open();
        }

        _reverbModel?.SetParameters(system.ReverbTime, system.ReverbLevel);
    }

    private void RefreshSystemReserveSettings()
        => PartialManager.SetReserve(Mt32Ram.Bytes.AsSpan(MemParams.SystemOffset + SystemParams.ReserveSettingsStartOff, 9));

    private void RefreshSystemChanAssign(byte firstPart, byte lastPart)
    {
        for (int c = 0; c < 16; c++)
        {
            for (int p = 0; p < 9; p++)
            {
                _chantable[c, p] = 0xFF;
            }
        }

        // CONFIRMED: In the case of assigning a MIDI channel to multiple parts, the messages
        // received on that MIDI channel are handled by all the parts.
        for (int i = 0; i <= 8; i++)
        {
            if (_parts[i] is { } part && i >= firstPart && i <= lastPart)
            {
                // CONFIRMED: Decay is started for all polys, and all controllers are reset, for
                // every part whose assignment was touched by the sysex write.
                part.AllSoundOff();
                part.ResetAllControllers();
            }

            byte chan = Mt32Ram.System.ChanAssign(i);
            if (chan > 15)
            {
                continue;
            }

            for (int j = 0; j <= 8; j++)
            {
                if (_chantable[chan, j] > 8)
                {
                    _chantable[chan, j] = (byte)i;
                    break;
                }
            }
        }
    }

    private void RefreshSystemMasterVol()
    {
        // Note, this should only occur when the user turns the volume knob. When the master volume
        // is set via a SysEx, display doesn't actually update on all real devices. However, we
        // anyway update the display, as we don't foresee a dedicated API for setting the master
        // volume yet it's rather dubious that one really needs this quirk to be fairly emulated.
        if (_opened)
        {
            _display!.MasterVolumeChanged();
        }
    }

    private void RefreshSystem()
    {
        RefreshSystemMasterTune();
        RefreshSystemReverbParameters();
        RefreshSystemReserveSettings();
        RefreshSystemChanAssign(0, 8);
        RefreshSystemMasterVol();
    }

    private void Reset()
    {
        if (!_opened)
        {
            return;
        }

        PartialManager.DeactivateAll();
        _mt32Default.Bytes.CopyTo(Mt32Ram.Bytes, 0);
        for (int i = 0; i < 9; i++)
        {
            _parts[i]!.Reset();
            if (i != 8)
            {
                _parts[i]!.SetProgram(ControlROMData[ControlROMMap.ProgramSettings + i]);
            }
            else
            {
                _parts[8]!.Refresh();
            }
        }

        RefreshSystem();
        ResetMasterTunePitchDelta();
        IsActive();
    }

    private void ResetMasterTunePitchDelta()
    {
        // This effectively resets master tune to 440.0Hz. Despite that the manual claims 442.0Hz
        // is the default setting for master tune, it doesn't actually take effect upon a reset due
        // to a bug in the reset routine.
        // CONFIRMED: This bug is present in all supported Control ROMs.
        MasterTunePitchDelta = 0;
    }

    /// <summary>The emulated LCD's 20 characters (the active-part block shown as #), and whether the MIDI MESSAGE LED is on.</summary>
    public bool GetDisplayState(out string text)
    {
        if (!_opened)
        {
            text = new string(' ', Display.LcdTextSize);
            return false;
        }

        return _display!.GetDisplayState(out text);
    }

    public bool HasActivePartials
    {
        get
        {
            if (!_opened)
            {
                return false;
            }

            for (uint partialNum = 0; partialNum < PartialCount; partialNum++)
            {
                if (PartialManager.GetPartial(partialNum)!.IsActive)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Returns true if the synth is active and subsequent calls to render() may result in
    /// non-trivial output (i.e. silence). The synth is considered active when either there are
    /// pending MIDI events in the queue, there is at least one active partial, or the reverb is
    /// (somewhat unreliably) detected as being active.
    /// </summary>
    public bool IsActive()
    {
        if (!_opened)
        {
            return false;
        }

        if (!_midiQueue!.IsEmpty || HasActivePartials)
        {
            return true;
        }

        if (IsReverbEnabled && _reverbModel!.IsActive)
        {
            return true;
        }

        _activated = false;
        return false;
    }

    // ---- rendering ----

    /// <summary>
    /// Renders samples to the specified output stream as if they were sampled at the analog stereo
    /// output, at the native 32 kHz. The length is in frames, interleaved stereo.
    /// </summary>
    public void Render(Span<short> stereoStream, int len)
    {
        if (!_opened)
        {
            stereoStream[..(len * 2)].Clear();
            return;
        }

        if (!_activated)
        {
            RenderedSampleCount += (uint)Analog.GetDACStreamsLength(len);
            stereoStream[..(len * 2)].Clear();
            UpdateDisplayState();
            return;
        }

        int written = 0;
        while (len > 0)
        {
            int thisPassLen = len > MaxSamplesPerRun ? MaxSamplesPerRun : len;
            DoRenderStreams(Analog.GetDACStreamsLength(thisPassLen));
            _analog!.Process(stereoStream[(written * 2)..], _tmpNonReverbLeft, _tmpNonReverbRight, _tmpReverbDryLeft, _tmpReverbDryRight, _tmpReverbWetLeft, _tmpReverbWetRight, thisPassLen);
            written += thisPassLen;
            len -= thisPassLen;
        }
    }

    private void DoRenderStreams(int len)
    {
        int position = 0;
        while (len > 0)
        {
            // We need to ensure zero-duration notes will play so add minimum 1-sample delay.
            int thisLen = 1;
            if (!IsAbortingPoly)
            {
                var nextEvent = _midiQueue!.PeekMidiEvent();
                int samplesToNextEvent = nextEvent is { } ev ? (int)(ev.Timestamp - RenderedSampleCount) : MaxSamplesPerRun;
                if (samplesToNextEvent > 0)
                {
                    thisLen = len > MaxSamplesPerRun ? MaxSamplesPerRun : len;
                    if (thisLen > samplesToNextEvent)
                    {
                        thisLen = samplesToNextEvent;
                    }
                }
                else
                {
                    var midiEvent = nextEvent!.Value;
                    if (midiEvent.SysexData is null)
                    {
                        PlayMsgNow(midiEvent.ShortMessageData);
                        // If a poly is aborting we don't drop the event from the queue. Instead,
                        // we'll return to it again when the abortion is done.
                        if (!IsAbortingPoly)
                        {
                            _midiQueue.DropMidiEvent();
                        }
                    }
                    else
                    {
                        PlaySysexNow(midiEvent.SysexData);
                        _midiQueue.DropMidiEvent();
                    }
                }
            }

            ProduceStreams(position, thisLen);
            position += thisLen;
            len -= thisLen;
        }
    }

    // In GENERATION2 units, the output from LA32 goes to the Boss chip already bit-shifted. In
    // NICE mode, it's also better to increase volume before the reverb processing to preserve accuracy.
    private void ProduceLA32Output(Span<short> buffer)
    {
        switch (DACInputMode)
        {
            case DACInputMode.Generation2:
                for (int i = 0; i < buffer.Length; i++)
                {
                    int s = buffer[i];
                    buffer[i] = (short)((s & 0x8000) | ((s << 1) & 0x7FFE) | ((s >> 14) & 0x0001));
                }

                break;
            case DACInputMode.Nice:
                for (int i = 0; i < buffer.Length; i++)
                {
                    buffer[i] = ClipSampleEx(buffer[i] << 1);
                }

                break;
        }
    }

    private void ConvertSamplesToOutput(Span<short> buffer)
    {
        if (DACInputMode == DACInputMode.Generation1)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                int s = buffer[i];
                buffer[i] = (short)((s & 0x8000) | ((s << 1) & 0x7FFE));
            }
        }
    }

    private void ProduceStreams(int position, int len)
    {
        var nonReverbLeft = _tmpNonReverbLeft.AsSpan(position, len);
        var nonReverbRight = _tmpNonReverbRight.AsSpan(position, len);
        var reverbDryLeft = _tmpReverbDryLeft.AsSpan(position, len);
        var reverbDryRight = _tmpReverbDryRight.AsSpan(position, len);
        var reverbWetLeft = _tmpReverbWetLeft.AsSpan(position, len);
        var reverbWetRight = _tmpReverbWetRight.AsSpan(position, len);

        if (_activated)
        {
            nonReverbLeft.Clear();
            nonReverbRight.Clear();
            reverbDryLeft.Clear();
            reverbDryRight.Clear();
            for (int i = 0; i < PartialCount; i++)
            {
                if (PartialManager.ShouldReverb(i))
                {
                    PartialManager.ProduceOutput(i, reverbDryLeft, reverbDryRight, len);
                }
                else
                {
                    PartialManager.ProduceOutput(i, nonReverbLeft, nonReverbRight, len);
                }
            }

            ProduceLA32Output(reverbDryLeft);
            ProduceLA32Output(reverbDryRight);
            if (IsReverbEnabled)
            {
                _reverbModel!.Process(reverbDryLeft, reverbDryRight, reverbWetLeft, reverbWetRight, len);
                ConvertSamplesToOutput(reverbWetLeft);
                ConvertSamplesToOutput(reverbWetRight);
            }
            else
            {
                reverbWetLeft.Clear();
                reverbWetRight.Clear();
            }

            ProduceLA32Output(nonReverbLeft);
            ConvertSamplesToOutput(nonReverbLeft);
            ProduceLA32Output(nonReverbRight);
            ConvertSamplesToOutput(nonReverbRight);
            ConvertSamplesToOutput(reverbDryLeft);
            ConvertSamplesToOutput(reverbDryRight);
        }
        else
        {
            nonReverbLeft.Clear();
            nonReverbRight.Clear();
            reverbDryLeft.Clear();
            reverbDryRight.Clear();
            reverbWetLeft.Clear();
            reverbWetRight.Clear();
        }

        PartialManager.ClearAlreadyOutputed();
        RenderedSampleCount += (uint)len;
        UpdateDisplayState();
    }

    private void UpdateDisplayState()
    {
        _display!.CheckDisplayStateUpdated(out _, out _, out bool lcdUpdated);
        if (lcdUpdated)
        {
            ReportHandler.OnLCDStateUpdated();
        }
    }
}
