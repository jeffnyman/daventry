using Daventry.Core.Hosting;
using Daventry.Sci.Engine;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Sound;

/// <summary>
/// Lip sync for speech: walks a sync resource of (time, cue) pairs and hands them to the
/// scripts through the syncTime and syncCue selectors. Port of ScummVM's Sync (sound/sync.cpp).
/// </summary>
public sealed class SciSync
{
    private readonly EngineState _s;
    private SciResource? _resource;
    private int _offset;

    public SciSync(EngineState s)
    {
        _s = s;
    }

    public void Start(ResourceId id, RegT syncObjAddr)
    {
        var sel = _s.Kernel.Selectors;
        _resource = _s.ResMan.FindResource(id);
        if (_resource?.Data is null)
        {
            _resource = null;
        }

        _offset = 0;
        if (_resource is not null)
        {
            Selectors.WriteSelectorValue(_s, syncObjAddr, sel.SyncCue, 0);
        }
        else
        {
            _s.Log.Debug("sci.sound", $"Sync::start: failed to find resource {id}");
            // the scripts stop the sound sync on this
            Selectors.WriteSelectorValue(_s, syncObjAddr, sel.SyncCue, SciMusic.SignalOffset);
        }
    }

    public void Next(RegT syncObjAddr)
    {
        var sel = _s.Kernel.Selectors;
        if (_resource?.Data is { } data && _offset < data.Length - 1)
        {
            short syncCue = -1;
            short syncTime = (short)_resource.GetUInt16LEAt(_offset);
            _offset += 2;
            if (syncTime != -1 && _offset < data.Length - 1)
            {
                syncCue = (short)_resource.GetUInt16LEAt(_offset);
                _offset += 2;
            }

            Selectors.WriteSelectorValue(_s, syncObjAddr, sel.SyncTime, syncTime);
            Selectors.WriteSelectorValue(_s, syncObjAddr, sel.SyncCue, syncCue);
        }
    }

    public void Stop() => _resource = null;
}
