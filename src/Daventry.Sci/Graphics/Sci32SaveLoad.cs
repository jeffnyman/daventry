using Daventry.Sci.Engine;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Graphics;

public sealed partial class GfxPalette32
{
    private static void SaveLoadPalette(SaveSerializer s, SciPalette palette)
    {
        palette.Timestamp = s.SyncUInt32(palette.Timestamp);
        for (int i = 0; i < 256; i++)
        {
            ref var color = ref palette.Colors[i];
            color.Used = s.SyncByte(color.Used);
            color.R = s.SyncByte(color.R);
            color.G = s.SyncByte(color.G);
            color.B = s.SyncByte(color.B);
        }
    }

    private static SciPalette? SaveLoadOptionalPalette(SaveSerializer s, SciPalette? palette)
    {
        bool hasPalette = s.SyncBool(palette is not null);
        if (!hasPalette)
        {
            return s.IsLoading ? null : palette;
        }

        palette ??= new SciPalette();
        SaveLoadPalette(s, palette);
        return palette;
    }

    /// <summary>Port of GfxPalette32::saveLoadWithSerializer: the vary, fade, cycle and gamma state.</summary>
    public void SaveLoad(SaveSerializer s)
    {
        if (s.Version < 34)
        {
            return;
        }

        if (s.IsLoading)
        {
            _version++;
            for (int i = 0; i < NumCyclers; i++)
            {
                _cyclers[i] = null;
            }

            _varyTargetPalette = null;
            _varyStartPalette = null;
        }

        _varyDirection = s.SyncInt16(_varyDirection);
        _varyPercent = s.SyncInt16(_varyPercent);
        _varyTargetPercent = s.SyncInt16(_varyTargetPercent);
        _varyFromColor = (byte)s.SyncInt16(_varyFromColor);
        _varyToColor = (byte)s.SyncInt16(_varyToColor);
        _varyNumTimesPaused = s.SyncUInt16(_varyNumTimesPaused);
        _needsUpdate = s.SyncBool(_needsUpdate);
        _varyTime = s.SyncInt32(_varyTime);
        _varyLastTick = s.SyncUInt32(_varyLastTick);

        // ScummVM keeps the fade table in bytes
        for (int i = 0; i < _fadeTable.Length; i++)
        {
            _fadeTable[i] = s.SyncByte((byte)Math.Min(_fadeTable[i], (ushort)255));
        }

        for (int i = 0; i < _cycleMap.Length; i++)
        {
            _cycleMap[i] = s.SyncBool(_cycleMap[i]);
        }

        if (_g.Features.HasLatePaletteCode && s.Version >= 41)
        {
            _gammaLevel = s.SyncInt16((short)_gammaLevel);
            SaveLoadPalette(s, _sourcePalette);
            _version++;
            _needsUpdate = true;
            _gammaChanged = true;
        }

        _varyTargetPalette = SaveLoadOptionalPalette(s, _varyTargetPalette);
        _varyStartPalette = SaveLoadOptionalPalette(s, _varyStartPalette);

        // the next palette was not saved by Sierra either
        for (int i = 0; i < NumCyclers; i++)
        {
            var cycler = _cyclers[i];
            bool hasCycler = s.SyncBool(cycler is not null);
            if (!hasCycler)
            {
                continue;
            }

            if (s.IsLoading)
            {
                cycler = new PalCycler();
                _cyclers[i] = cycler;
            }

            cycler!.FromColor = s.SyncByte(cycler.FromColor);
            cycler.NumColorsToCycle = s.SyncUInt16(cycler.NumColorsToCycle);
            cycler.CurrentCycle = s.SyncByte(cycler.CurrentCycle);
            cycler.Direction = (PalCyclerDirection)s.SyncByte((byte)cycler.Direction);
            cycler.LastUpdateTick = s.SyncUInt32(cycler.LastUpdateTick);
            cycler.Delay = s.SyncInt16(cycler.Delay);
            cycler.NumTimesPaused = s.SyncUInt16(cycler.NumTimesPaused);
        }
    }
}

public sealed partial class GfxRemap32
{
    /// <summary>Port of GfxRemap32::saveLoadWithSerializer.</summary>
    public void SaveLoad(SaveSerializer s)
    {
        if (s.Version < 35)
        {
            return;
        }

        RemapCount = s.SyncByte((byte)RemapCount);
        BlockedRangeStart = s.SyncByte(BlockedRangeStart);
        BlockedRangeCount = s.SyncInt16(BlockedRangeCount);

        foreach (var singleRemap in _remaps)
        {
            singleRemap.Type = (RemapType)s.SyncByte((byte)singleRemap.Type);
            if (s.IsLoading && singleRemap.Type != RemapType.None)
            {
                singleRemap.Reset();
            }

            singleRemap.From = s.SyncByte(singleRemap.From);
            singleRemap.To = s.SyncByte(singleRemap.To);
            singleRemap.Delta = (short)(sbyte)s.SyncByte((byte)singleRemap.Delta);
            singleRemap.Percent = (short)(sbyte)s.SyncByte((byte)singleRemap.Percent);
            singleRemap.Gray = s.SyncByte(singleRemap.Gray);
        }

        if (s.IsLoading)
        {
            _needsUpdate = true;
        }
    }
}

public sealed partial class GfxCursor32
{
    /// <summary>Port of GfxCursor32::saveLoadWithSerializer: the hide count, the restricted area and the cursor view.</summary>
    public void SaveLoad(SaveSerializer s)
    {
        if (s.Version < 38)
        {
            return;
        }

        int hideCount = s.SyncInt32(_hideCount);
        _restrictedArea.Left = s.SyncInt16((short)_restrictedArea.Left);
        _restrictedArea.Top = s.SyncInt16((short)_restrictedArea.Top);
        _restrictedArea.Right = s.SyncInt16((short)_restrictedArea.Right);
        _restrictedArea.Bottom = s.SyncInt16((short)_restrictedArea.Bottom);
        CursorInfo.ResourceId = s.SyncUInt16((ushort)CursorInfo.ResourceId);
        CursorInfo.LoopNo = (short)s.SyncUInt16((ushort)CursorInfo.LoopNo);
        CursorInfo.CelNo = (short)s.SyncUInt16((ushort)CursorInfo.CelNo);

        if (s.IsLoading)
        {
            Hide();
            SetView(CursorInfo.ResourceId, CursorInfo.LoopNo, CursorInfo.CelNo);
            if (hideCount == 0)
            {
                Show();
            }
            else
            {
                _hideCount = hideCount;
            }
        }
    }
}

public sealed partial class Video32
{
    /// <summary>Only SCI3 saved a playing robot; nothing to do for the games before it.</summary>
    public void SaveLoad(SaveSerializer s, SciVersion version)
    {
        if (version < SciVersion.V3)
        {
            return;
        }

        bool robotExists = s.SyncBool(RobotPlayer.Status != RobotStatus.Uninitialized);
        if (!robotExists)
        {
            return;
        }

        int robotId = s.SyncUInt16((ushort)RobotPlayer.ResourceId);
        var planeId = s.SyncReg(RobotPlayer.PlaneId);
        short priority = s.SyncInt16(RobotPlayer.Priority);
        short x = s.SyncInt16((short)RobotPlayer.Position.X);
        short y = s.SyncInt16((short)RobotPlayer.Position.Y);
        short scale = s.SyncInt16(RobotPlayer.Scale);
        int frameNo = s.SyncInt32(RobotPlayer.GetFrameNo());
        if (s.IsLoading)
        {
            RobotPlayer.Open(robotId, planeId, priority, x, y, scale);
            RobotPlayer.ShowFrame(frameNo, RobotDecoder.Unspecified, RobotDecoder.Unspecified, RobotDecoder.Unspecified);
        }
    }
}
