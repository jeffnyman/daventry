using System.Buffers.Binary;
using Daventry.Core.Hosting;

namespace Daventry.Agi.Sound;

/// <summary>
/// AGI sound resource formats. The value is the first little-endian word of the resource,
/// except for AGIv1, Apple II and CoCo3 sounds, which have no header.
/// </summary>
public enum AgiSoundFormat : ushort
{
    /// <summary>Apple IIgs digitised sample.</summary>
    Sample = 0x0001,

    /// <summary>Apple IIgs MIDI sequence.</summary>
    Midi = 0x0002,

    /// <summary>PCjr/Tandy four-voice sequence, the format of every DOS, Amiga, Atari ST and Macintosh game.</summary>
    FourChannel = 0x0008,

    Apple2 = 0xFFFF,

    CoCo3 = 0xFFFE,
}

/// <summary>Which synthesiser plays four-channel sounds. Port of ScummVM's SOUND_EMU_* selection.</summary>
public enum AgiSoundEmulation
{
    /// <summary>Square waves and noise as the PCjr/Tandy SN76496 produced them. ScummVM's default.</summary>
    Pcjr,

    /// <summary>Single-voice square wave, as on an IBM PC speaker.</summary>
    PcSpeaker,

    /// <summary>Sarien's soft synthesiser: ramp waveform, envelopes and chorus (ScummVM's SOUND_EMU_NONE).</summary>
    Ramp,

    /// <summary>Sarien's square waveform at full four voices, used for Amiga games.</summary>
    Amiga,

    /// <summary>Sarien's sampled Macintosh waveform.</summary>
    Mac,
}

/// <summary>A loaded sound resource. Port of AgiSound and PCjrSound from ScummVM's sound.h.</summary>
public sealed class AgiSound
{
    private const int VoiceCount = 4;

    public AgiSound(int resourceNr, byte[] data, AgiSoundFormat format)
    {
        ResourceNr = resourceNr;
        Data = data;
        Format = format;
    }

    public int ResourceNr { get; }

    public byte[] Data { get; }

    public AgiSoundFormat Format { get; }

    public bool IsPlaying { get; set; }

    /// <summary>True when a four-channel sound carries at least its voice offset table.</summary>
    public bool IsValid => Format != AgiSoundFormat.FourChannel || Data.Length >= VoiceCount * 2;

    /// <summary>Offset of the first note of a voice in <see cref="Data"/>, from the header's offset table.</summary>
    public int GetVoiceOffset(int voice)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(voice, VoiceCount);
        return BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(voice * 2));
    }

    /// <summary>
    /// Classifies a raw sound resource. Returns null when it is too small or of an unknown format.
    /// Port of AgiSound::createFromRawResource.
    /// </summary>
    public static AgiSound? CreateFromRawResource(byte[]? data, int resourceNr, AgiPlatform platform, bool isAgiV1, ILog log)
    {
        if (data is null || data.Length < 2)
        {
            return null;
        }

        // Platform formats with no header come first; AGIv1 next, since it has none either.
        if (platform == AgiPlatform.Apple2)
        {
            return new AgiSound(resourceNr, data, AgiSoundFormat.Apple2);
        }

        if (platform == AgiPlatform.CoCo3)
        {
            return new AgiSound(resourceNr, data, AgiSoundFormat.CoCo3);
        }

        if (isAgiV1)
        {
            return new AgiSound(resourceNr, data, AgiSoundFormat.FourChannel);
        }

        ushort type = BinaryPrimitives.ReadUInt16LittleEndian(data);
        switch ((AgiSoundFormat)type)
        {
            case AgiSoundFormat.Sample:
            case AgiSoundFormat.Midi:
            case AgiSoundFormat.FourChannel:
                return new AgiSound(resourceNr, data, (AgiSoundFormat)type);
            default:
                log.Warning("agi.sound", $"Sound resource {resourceNr} has unknown type 0x{type:X4}. Not using the sound");
                return null;
        }
    }
}
