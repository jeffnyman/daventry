using Daventry.Core.Hosting;

namespace Daventry.Agi.Sound;

/// <summary>
/// Sound playback. Placeholder until sound.cpp and the PCjr/Tandy synthesiser are ported: a sound
/// "finishes" immediately so scripts that wait on the done flag continue.
/// </summary>
public sealed class SoundMgr
{
    private readonly AgiEngine _vm;
    private readonly byte[]?[] _sounds = new byte[256][];
    private int _playingSound = -1;
    private int _endFlag = -1;

    public SoundMgr(AgiEngine vm)
    {
        _vm = vm;
    }

    public bool IsPlaying => _playingSound != -1;

    public void LoadSound(int resourceNr, byte[] data) => _sounds[resourceNr] = data;

    public void UnloadSound(int resourceNr)
    {
        if (_playingSound == resourceNr)
        {
            StopSound();
        }

        _sounds[resourceNr] = null;
    }

    public void StartSound(int resourceNr, int flag)
    {
        StopSound();
        if (_sounds[resourceNr] is null)
        {
            _vm.Host.Log.Debug("agi.sound", $"sound {resourceNr} not loaded");
        }

        _playingSound = resourceNr;
        _endFlag = flag;
        _vm.Host.Log.Debug("agi.sound", $"play sound {resourceNr}, flag {flag} (sound not implemented, finishing immediately)");
        SoundIsFinished();
    }

    public void StopSound()
    {
        _endFlag = -1;
        _playingSound = -1;
    }

    public void SoundIsFinished()
    {
        if (_endFlag != -1)
        {
            _vm.SetFlagOrVar(_endFlag, true);
        }

        _endFlag = -1;
        _playingSound = -1;
    }

    /// <summary>Volume variable 23: 0 is loudest and 15 is mute in AGI. Some fan games invert it.</summary>
    public void ApplyVolume(byte gameVolume, bool veryFirstInitialCycle)
    {
    }
}
