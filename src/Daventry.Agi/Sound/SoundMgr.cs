using Daventry.Core.Hosting;

namespace Daventry.Agi.Sound;

/// <summary>
/// Sound playback. Port of ScummVM's sound.cpp: holds the loaded sound resources, owns the
/// synthesiser and sets the game's "sound finished" flag. The synthesiser runs on whatever thread
/// reads the host audio stream, so the finished state is polled from <see cref="Update"/> on the
/// engine tick rather than pushed from the audio thread as in ScummVM. When the host does not read
/// audio at all (headless hosts, tests) the manager clocks the synthesiser itself so that scripts
/// waiting on music still progress.
/// </summary>
public sealed class SoundMgr
{
    private const int DiscardFrames = 1024;

    private readonly AgiEngine _vm;
    private readonly AgiSound?[] _sounds = new AgiSound?[256];
    private readonly bool[] _unsupportedWarned = new bool[256];
    private readonly SoundGen _soundGen;
    private int _playingSound = -1;
    private int _endFlag = -1;
    private bool _setVolumeBrokenFangame;
    private float _masterVolume = 1f;
    private bool _mute;
    private byte _gameVolume = 15;
    private bool _selfClocked;
    private short[]? _discard;

    public SoundMgr(AgiEngine vm, AgiSoundEmulation emulation, int sampleRate)
    {
        _vm = vm;

        // AGIv1 sounds are only understood by the PCjr sequencer. Apple II and CoCo3 sounds have
        // their own formats and generators that are not ported yet, so they get the PCjr one too and
        // simply never play.
        bool isV1Dos = vm.Description.Version <= 0x2001 && vm.Description.Platform == AgiPlatform.Dos;
        if (isV1Dos && emulation != AgiSoundEmulation.Pcjr)
        {
            vm.Host.Log.Warning("agi.sound", $"Unsupported sound emulation {emulation} for AGIv1 sounds, using PCjr");
            emulation = AgiSoundEmulation.Pcjr;
        }

        Emulation = emulation;
        _soundGen = emulation == AgiSoundEmulation.Pcjr
            ? new SoundGenPcjr(sampleRate, vm.Description.Version)
            : new SoundGenSarien(sampleRate, emulation);
    }

    public AgiSoundEmulation Emulation { get; }

    /// <summary>The synthesiser, to be added to the host mixer.</summary>
    public SoundGen Generator => _soundGen;

    public bool IsPlaying => _playingSound != -1;

    /// <summary>Resource number of the sound being played, or -1.</summary>
    public int PlayingSound => _playingSound;

    /// <summary>Host-side volume from 0 to 1, multiplied with the game's own volume variable.</summary>
    public float MasterVolume
    {
        get => _masterVolume;
        set
        {
            _masterVolume = Math.Clamp(value, 0f, 1f);
            UpdateGain();
        }
    }

    public bool Mute
    {
        get => _mute;
        set
        {
            _mute = value;
            UpdateGain();
        }
    }

    /// <summary>Classifies and keeps a sound resource. Returns false when the data is unusable.</summary>
    public bool LoadSound(int resourceNr, byte[] data)
    {
        bool isAgiV1 = _vm.Description.Version <= 0x2001;
        var sound = AgiSound.CreateFromRawResource(data, resourceNr, _vm.Description.Platform, isAgiV1, _vm.Host.Log);
        _sounds[resourceNr] = sound;
        return sound is not null;
    }

    public void UnloadSound(int resourceNr)
    {
        var sound = _sounds[resourceNr];
        if (sound is null)
        {
            return;
        }

        if (sound.IsPlaying)
        {
            StopSound();
        }

        _sounds[resourceNr] = null;
    }

    /// <summary>
    /// Starts a sound. When it finishes the given flag is set, so scripts can wait for it; a new
    /// sound may be started before the previous one ends.
    /// </summary>
    public void StartSound(int resourceNr, int flag)
    {
        var sound = _sounds[resourceNr];
        _vm.Host.Log.Debug("agi.sound", $"startSound(resnum = {resourceNr}, flag = {flag}, type = {sound?.Format})");
        if (sound is null)
        {
            _vm.Host.Log.Warning("agi.sound", $"startSound: sound {resourceNr} does not exist");
            return;
        }

        StopSound();

        if (!sound.IsValid)
        {
            _vm.Host.Log.Warning("agi.sound", $"startSound: sound {resourceNr} is invalid");
            return;
        }

        if (sound.Format != AgiSoundFormat.FourChannel)
        {
            // Apple II, CoCo3 and IIgs sounds have no generator yet. Finishing at once keeps the
            // game moving rather than leaving it waiting on a flag that would never be set.
            if (!_unsupportedWarned[resourceNr])
            {
                _unsupportedWarned[resourceNr] = true;
                _vm.Host.Log.Warning("agi.sound", $"startSound: sound {resourceNr} has format {sound.Format}, which is not supported yet");
            }

            _vm.SetFlagOrVar(flag, true);
            return;
        }

        sound.IsPlaying = true;
        _playingSound = resourceNr;
        _soundGen.SoundEnabled = _vm.GetFlag(AgiFlag.SoundOn);
        _soundGen.Play(sound);

        _endFlag = flag;
        _vm.SetFlagOrVar(_endFlag, false);
    }

    public void StopSound()
    {
        _vm.Host.Log.Debug("agi.sound", $"stopSound() --> {_playingSound}");

        if (_playingSound != -1)
        {
            if (_sounds[_playingSound] is { } sound)
            {
                sound.IsPlaying = false;
            }

            _soundGen.Stop();
            _playingSound = -1;
        }

        // Needed all the time: some games wait until music has played, and when a sound got
        // stopped early they would otherwise block (Death Angel jingle in Police Quest 1, room 71).
        if (_endFlag != -1)
        {
            _vm.SetFlagOrVar(_endFlag, true);
        }

        _endFlag = -1;
    }

    public void SoundIsFinished()
    {
        _vm.Host.Log.Debug("agi.sound", $"soundIsFinished() --> {_playingSound}, flag {_endFlag}, sound on {_soundGen.SoundEnabled}");
        if (_endFlag != -1)
        {
            _vm.SetFlagOrVar(_endFlag, true);
        }

        if (_playingSound != -1 && _sounds[_playingSound] is { } sound)
        {
            sound.IsPlaying = false;
        }

        _playingSound = -1;
        _endFlag = -1;
    }

    /// <summary>
    /// Called once per engine tick: mirrors the sound-on flag into the synthesiser, sets the
    /// finished flag when playback has ended and clocks the synthesiser when the host does not.
    /// </summary>
    public void Update(TimeSpan engineTime)
    {
        _soundGen.SoundEnabled = _vm.GetFlag(AgiFlag.SoundOn);

        long expected = (long)(engineTime.TotalSeconds * _soundGen.SampleRate);
        long rendered = _soundGen.FramesRendered;
        if (_selfClocked)
        {
            if (rendered > expected)
            {
                // the host has started consuming audio after all
                _selfClocked = false;
            }
        }
        else if (rendered + _soundGen.SampleRate / 4 < expected)
        {
            // nobody has read audio for a quarter of a second: this host is silent
            _selfClocked = true;
        }

        if (_selfClocked)
        {
            _discard ??= new short[DiscardFrames * 2];
            while (rendered < expected)
            {
                int frames = (int)Math.Min(DiscardFrames, expected - rendered);
                _soundGen.Read(_discard.AsSpan(0, frames * 2));
                rendered += frames;
            }
        }

        if (_playingSound != -1 && _soundGen.Finished)
        {
            SoundIsFinished();
        }
    }

    /// <summary>
    /// Applies game variable 23, where 0 is loudest and 15 is mute. Port of
    /// AgiEngine::applyVolumeToMixer, including the workaround for fan games that set it the
    /// wrong way round.
    /// </summary>
    public void ApplyVolume(byte gameVolume, bool veryFirstInitialCycle)
    {
        _vm.Host.Log.Debug("agi.sound", $"applyVolume() volume: {gameVolume} veryFirstInitialCycle: {veryFirstInitialCycle}");
        gameVolume = Math.Min(gameVolume, (byte)15);

        if (veryFirstInitialCycle && (_vm.Description.Features & AgiGameFeatures.FanMade) != 0 && gameVolume == 15)
        {
            // Volume set to mute at the start: the PC speaker ignored this variable, so plenty of
            // fan games set 15 meaning "loudest". See ScummVM bug #7035.
            _vm.Host.Log.Debug("agi.sound", "Broken volume in fan game detected, enabling workaround");
            _setVolumeBrokenFangame = true;
        }

        _gameVolume = _setVolumeBrokenFangame ? gameVolume : (byte)(15 - gameVolume);
        UpdateGain();
    }

    private void UpdateGain()
    {
        _soundGen.Gain = _mute ? 0f : _masterVolume * _gameVolume / 15f;
    }
}
