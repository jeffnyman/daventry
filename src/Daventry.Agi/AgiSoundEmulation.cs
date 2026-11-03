namespace Daventry.Agi;

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
