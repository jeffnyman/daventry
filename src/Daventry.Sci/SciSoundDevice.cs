namespace Daventry.Sci;

/// <summary>Which sound hardware the SCI music is played through.</summary>
public enum SciSoundDevice
{
    /// <summary>The AdLib and Sound Blaster FM synthesiser, through the OPL emulator. The default.</summary>
    AdLib,

    /// <summary>The three square-wave voices of the PCjr and Tandy 1000.</summary>
    Pcjr,

    /// <summary>The single square wave of the IBM PC speaker.</summary>
    PcSpeaker,

    /// <summary>A General MIDI synthesiser playing a SoundFont, which the host supplies.</summary>
    GeneralMidi,

    /// <summary>The Roland MT-32 (or CM-32L/LAPC-I), through the emulator with ROMs the host supplies.</summary>
    Mt32,
}
