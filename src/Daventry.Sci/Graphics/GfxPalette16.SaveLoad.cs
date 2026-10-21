using Daventry.Sci.Engine;

namespace Daventry.Sci.Graphics;

public sealed partial class GfxPalette16
{
    private static void PalVarySaveLoadPalette(SaveSerializer s, SciPalette palette)
    {
        s.SyncBytes(palette.Mapping);
        palette.Timestamp = s.SyncUInt32(palette.Timestamp);
        for (int i = 0; i < 256; i++)
        {
            ref var color = ref palette.Colors[i];
            color.Used = s.SyncByte(color.Used);
            color.R = s.SyncByte(color.R);
            color.G = s.SyncByte(color.G);
            color.B = s.SyncByte(color.B);
        }

        s.SyncBytes(palette.Intensity);
    }

    /// <summary>Port of GfxPalette::saveLoadWithSerializer.</summary>
    public void SaveLoad(SaveSerializer s)
    {
        if (s.IsLoading)
        {
            // the cycling schedules are reset on load, since the engine tick count persists
            // and a stale schedule would freeze the animation (SQ5's Kiz Urazgubi waterfalls)
            _schedules.Clear();
        }

        if (s.Version >= 25)
        {
            // the intensity of the system palette matters at least for kq6's dark cave (room 390)
            s.SyncBytes(SysPalette.Intensity);
        }

        if (s.Version >= 24)
        {
            if (s.IsLoading && _palVaryResourceId != -1)
            {
                PalVaryRemoveTimer();
            }

            _palVaryResourceId = s.SyncInt32(_palVaryResourceId);
            if (_palVaryResourceId != -1 || s.Version >= 40)
            {
                if (_palVaryResourceId != -1)
                {
                    PalVarySaveLoadPalette(s, _palVaryOriginPalette);
                    PalVarySaveLoadPalette(s, _palVaryTargetPalette);
                }

                _palVaryStep = s.SyncInt16((short)_palVaryStep);
                _palVaryStepStop = s.SyncInt16((short)_palVaryStepStop);
                _palVaryDirection = s.SyncInt16((short)_palVaryDirection);
                _palVaryTicks = s.SyncUInt16((ushort)_palVaryTicks);
                _palVaryPaused = s.SyncInt32(_palVaryPaused);
                if (s.Version >= 40)
                {
                    _palVarySignal = s.SyncInt32(_palVarySignal);
                }
            }

            if (s.IsLoading && s.Version < 40)
            {
                // before version 40 the pause count and the signal were not stored (QfG3 bug #9674)
                _palVaryPaused = 0;
                _palVarySignal = 0;
            }

            if (s.IsLoading && _palVaryResourceId != -1)
            {
                PalVaryInstallTimer();
            }
        }
    }
}
