using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Daventry.Sci.Engine;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Graphics;

/// <summary>
/// Plays VMD videos for kPlayVMD. Port of ScummVM's VMDPlayer and the parts of its VideoPlayer
/// base that VMDs use. SCI3 composites the video: it is decoded into a bitmap that a screen item
/// shows in a plane of its own, so it goes through the normal renderer and palette. SCI2.1 draws
/// the frames straight into the game buffer and submits their palette. Playback waits on the
/// engine clock and yields to the scripts at the intervals they ask for. ScummVM's high colour
/// video mode, video upscaling and black-lined video options are off here (the hosts present
/// 8-bit frames).
/// </summary>
public sealed class VmdPlayer
{
    [Flags]
    public enum EventFlags
    {
        None = 0,
        End = 1,
        EscapeKey = 2,
        MouseDown = 4,
        HotRectangle = 8,
        ToFrame = 0x10,
        YieldToVm = 0x20,
        Reverse = 0x80,
    }

    [Flags]
    public enum PlayFlags
    {
        None = 0,
        DoublePixels = 1,
        BlackLines = 4,
        Boost = 0x10,
        LeaveScreenBlack = 0x20,
        LeaveLastFrame = 0x40,
        BlackPalette = 0x80,
        StretchVertical = 0x100,
    }

    public enum VmdStatus
    {
        NotOpen = 0,
        Open = 1,
        Playing = 2,
        Paused = 3,
        Stopped = 4,
        Finished = 5,
    }

    public const int OpenFlagMute = 1;
    public const int IoSuccess = 0;
    public const int IoError = 0xFFFF;
    private const int MaxBlobs = 10;
    private const int SciKeyEsc = 27;

    private readonly Sci32Graphics _g;
    private readonly VmdDecoder _decoder;
    private readonly List<Blob> _blobs = [];

    private Audio.Mixer? _mixer;
    private VmdAudioSource? _audioSource;

    private bool _isOpen;
    private bool _isInitialized;
    private int _yieldFrame;
    private int _yieldInterval;
    private int _lastYieldedFrameNo;
    private bool _needsUpdate;
    private bool _haveCurrentFrame;

    private SciRect _drawRect;
    private Plane32? _plane;
    private ScreenItem? _screenItem;
    private RegT _bitmapId;
    private bool _planeIsOwned = true;
    private int _priority;
    private bool _doublePixels;
    private bool _stretchVertical;
    private bool _blackLines;
    private bool _leaveScreenBlack;
    private bool _leaveLastFrame;
    private bool _ignorePalettes;
    private bool _isComposited;
    private bool _showCursor;

    private SciRect _blackoutRect;
    private Plane32? _blackoutPlane;

    private int _startColor;
    private int _endColor = 255;

    private int _boostPercent = 100;
    private int _boostStartColor;
    private int _boostEndColor = 255;

    private record struct Blob(int BlobNumber, int BlockSize, int Top, int Left, int Bottom, int Right);

    public VmdPlayer(Sci32Graphics g)
    {
        _g = g;
        _decoder = new VmdDecoder(() => g.State.Millis(), g.Log);
    }

    /// <summary>The mixer the video's sound plays through; without one the videos are silent.</summary>
    public Audio.Mixer? Mixer
    {
        get => _mixer;
        set => _mixer = value;
    }

    private EngineState State => _g.State;

    // ---- playback ----

    public int Open(string fileName, int flags)
    {
        if (_isOpen)
        {
            throw new SciVmException($"Attempted to play {fileName}, but another VMD was loaded");
        }

        if (_g.Features.VmdOpenStopsAudio)
        {
            State.Audio32?.Stop(Sound.Audio32.AllChannels);
        }

        // a VMD bundled in the resources is named by its number
        byte[]? data = null;
        var bundled = _g.ResMan.FindResource(ResourceType.Vmd, (int)Math.Min(LeadingNumber(Path.GetFileName(fileName.Replace('\\', '/'))), ushort.MaxValue));
        if (bundled?.Data is { } bundledData)
        {
            data = bundledData;
        }
        else if (_g.ResMan.Game.Exists(Path.GetFileName(fileName.Replace('\\', '/'))))
        {
            data = _g.ResMan.Game.ReadAllBytes(Path.GetFileName(fileName.Replace('\\', '/')));
        }
        else if (_g.ResMan.Game.Exists(fileName))
        {
            data = _g.ResMan.Game.ReadAllBytes(fileName);
        }

        if (data is not null && _decoder.Load(data))
        {
            _isOpen = true;
            _decoder.Muted = (flags & OpenFlagMute) != 0;
            _g.Log.Debug("sci.video", $"VMD {fileName}: {_decoder.Width}x{_decoder.Height}, {_decoder.FrameCount} frames, sound {_decoder.HasSound}, flags {flags}");
            return IoSuccess;
        }

        _g.Log.Warning("sci.video", $"Failed to load {fileName}");
        return IoError;
    }

    private static ulong LeadingNumber(string name)
    {
        ulong value = 0;
        foreach (char c in name)
        {
            if (c is < '0' or > '9')
            {
                break;
            }

            value = value * 10 + (ulong)(c - '0');
        }

        return value;
    }

    public void Init(int x, int y, PlayFlags flags, int boostPercent, int boostStartColor, int boostEndColor)
    {
        _doublePixels = (flags & PlayFlags.DoublePixels) != 0;
        _stretchVertical = (flags & PlayFlags.StretchVertical) != 0;

        int width = _decoder.Width << (_doublePixels ? 1 : 0);
        int height = _decoder.Height << (_doublePixels || _stretchVertical ? 1 : 0);

        if (_g.Version < SciVersion.V3)
        {
            x &= ~1;
        }

        // ScummVM's black-lined video option is off by default, and its boost goes with it
        _blackLines = false;
        _boostPercent = 100 + (_blackLines && (flags & PlayFlags.Boost) != 0 ? boostPercent : 0);
        _boostStartColor = Math.Clamp(boostStartColor, 0, 255);
        _boostEndColor = Math.Clamp(boostEndColor, 0, 255);
        _leaveScreenBlack = (flags & PlayFlags.LeaveScreenBlack) != 0;
        _leaveLastFrame = (flags & PlayFlags.LeaveLastFrame) != 0;

        SetDrawRect(x, y, width, height);
    }

    private void SetDrawRect(int x, int y, int width, int height)
    {
        _drawRect = new SciRect(x, y, x + width, y + height);
        var frameout = _g.Frameout;
        if (_drawRect.Right > frameout.ScreenWidth || _drawRect.Bottom > frameout.ScreenHeight)
        {
            _g.Log.Warning("sci.video", $"Draw rect {_drawRect} is out of bounds of the screen; clipping it");
            _drawRect.Clip(new SciRect(frameout.ScreenWidth, frameout.ScreenHeight));
        }
    }

    public int Close()
    {
        if (!_isOpen)
        {
            return IoSuccess;
        }

        var frameout = _g.Frameout;
        if (_isInitialized)
        {
            if (_isComposited)
            {
                CloseComposited();
            }
            else
            {
                CloseOverlay();
            }

            if (_blackoutPlane is not null)
            {
                frameout.DeletePlane(_blackoutPlane);
                _blackoutPlane = null;
            }

            if (!_leaveLastFrame && !_leaveScreenBlack)
            {
                // this actually deletes the blackout plane
                frameout.FrameOut(true);
            }

            if (!_showCursor)
            {
                _g.Cursor.Unhide();
            }
        }

        StopAudio();
        _decoder.Close();

        _isOpen = false;
        _isInitialized = false;
        _ignorePalettes = false;
        _lastYieldedFrameNo = 0;
        _planeIsOwned = true;
        _priority = 0;
        _drawRect = default;
        _blobs.Clear();
        _needsUpdate = false;
        _haveCurrentFrame = false;
        return IoSuccess;
    }

    public VmdStatus Status
    {
        get
        {
            if (!_isOpen)
            {
                return VmdStatus.NotOpen;
            }

            if (_decoder.IsPlaying)
            {
                return VmdStatus.Playing;
            }

            return _decoder.EndOfVideo ? VmdStatus.Finished : VmdStatus.Open;
        }
    }

    /// <summary>Was WaitForEvent in SSCI.</summary>
    public ValueTask<EventFlags> KernelPlayUntilEvent(EventFlags flags, int lastFrameNo, int yieldInterval)
    {
        int maxFrameNo = _decoder.FrameCount - 1;

        _yieldFrame = (flags & EventFlags.ToFrame) != 0 && lastFrameNo != 0 ? Math.Min(lastFrameNo, maxFrameNo) : maxFrameNo;

        if ((flags & EventFlags.YieldToVm) != 0)
        {
            _yieldInterval = 3;
            if (yieldInterval == -1 && (flags & EventFlags.ToFrame) == 0)
            {
                _yieldInterval = lastFrameNo;
            }
            else if (yieldInterval != -1)
            {
                _yieldInterval = Math.Min(yieldInterval, maxFrameNo);
            }
        }
        else
        {
            _yieldInterval = maxFrameNo;
        }

        return PlayUntilEvent(flags);
    }

    private async ValueTask<EventFlags> PlayUntilEvent(EventFlags flags)
    {
        if ((flags & EventFlags.Reverse) != 0)
        {
            _g.Log.Warning("sci.video", "VMD reverse playback is not supported; the video plays forwards");
        }

        if (!_isInitialized)
        {
            _isInitialized = true;
            if (!_showCursor)
            {
                _g.Cursor.Hide();
            }

            if (!_blackoutRect.IsEmpty && _planeIsOwned)
            {
                _blackoutPlane = new Plane32(_g, _blackoutRect);
                _g.Frameout.AddPlane(_blackoutPlane);
            }

            if (IsNormallyComposited)
            {
                _isComposited = true;
                InitComposited();
            }
            else
            {
                _isComposited = false;
                InitOverlay();
            }
        }

        // more than a 60th of a second makes the mouse sluggish in VMD action sequences
        return await VideoPlayUntilEvent(flags, 10);
    }

    /// <summary>VideoPlayer::playUntilEvent.</summary>
    private async ValueTask<EventFlags> VideoPlayUntilEvent(EventFlags flags, uint maxSleepMs)
    {
        // events queued before playback must not stop it
        State.Events.FlushEvents();
        StartPlayback();

        EventFlags stopFlag;
        while (true)
        {
            if (!_needsUpdate)
            {
                await State.SleepMs((int)Math.Min(_decoder.TimeToNextFrame, maxSleepMs));
            }

            // more than one update means playback is behind: those frames are skipped, but their
            // palettes are still submitted
            bool decoded = false;
            while (_decoder.NeedsUpdate)
            {
                decoded |= _decoder.DecodeNextFrame();
                if (_decoder.HasDirtyPalette)
                {
                    SubmitPalette(_decoder.GetPalette());
                }
            }

            if (decoded)
            {
                RenderFrame();
                _haveCurrentFrame = true;
                _needsUpdate = false;
            }
            else if (_needsUpdate)
            {
                if (_haveCurrentFrame)
                {
                    RenderFrame();
                }

                _needsUpdate = false;
            }

            // only after the frame, or yields stick on the same frame
            stopFlag = CheckForEvent(flags);
            if (stopFlag != EventFlags.None)
            {
                break;
            }

            _g.Frameout.UpdateScreen();
        }

        return stopFlag;
    }

    private void StartPlayback()
    {
        if (_decoder.IsPlaying)
        {
            return;
        }

        _decoder.Start();
        if (_decoder.Audio is { } audio && _mixer is not null)
        {
            _audioSource = new VmdAudioSource(audio, _mixer.SampleRate, _decoder.Muted);
            _mixer.Add(_audioSource);
        }
    }

    private void StopAudio()
    {
        if (_audioSource is not null)
        {
            _mixer?.Remove(_audioSource);
            _audioSource = null;
        }
    }

    private EventFlags CheckForEvent(EventFlags flags)
    {
        int currentFrameNo = _decoder.CurFrame;
        if (currentFrameNo >= _yieldFrame)
        {
            return EventFlags.End;
        }

        if (_yieldInterval > 0 && currentFrameNo != _lastYieldedFrameNo && currentFrameNo % _yieldInterval == 0)
        {
            _lastYieldedFrameNo = currentFrameNo;
            return EventFlags.YieldToVm;
        }

        var stopFlag = VideoCheckForEvent(flags);
        if (stopFlag != EventFlags.None)
        {
            return stopFlag;
        }

        var ev = State.Events.GetSciEvent(KernelFunctions.SciEventHotRectangle | KernelFunctions.SciEventPeek);
        if ((flags & EventFlags.HotRectangle) != 0 && ev.Type == KernelFunctions.SciEventHotRectangle)
        {
            return EventFlags.HotRectangle;
        }

        return EventFlags.None;
    }

    /// <summary>VideoPlayer::checkForEvent.</summary>
    private EventFlags VideoCheckForEvent(EventFlags flags) => VideoCheckForEvent(State, _g.Version, flags, _decoder.EndOfVideo);

    /// <summary>VideoPlayer::checkForEvent, shared by the VMD and Duck players.</summary>
    internal static EventFlags VideoCheckForEvent(EngineState state, SciVersion version, EventFlags flags, bool endOfVideo)
    {
        if (state.ShouldQuit() || endOfVideo)
        {
            return EventFlags.End;
        }

        var events = state.Events;
        var ev = events.GetSciEvent(KernelFunctions.SciEventMousePress | KernelFunctions.SciEventPeek);
        if ((flags & EventFlags.MouseDown) != 0 && ev.Type == KernelFunctions.SciEventMousePress)
        {
            return EventFlags.MouseDown;
        }

        ev = events.GetSciEvent(KernelFunctions.SciEventKeyDown | KernelFunctions.SciEventPeek);
        if ((flags & EventFlags.EscapeKey) != 0 && ev.Type == KernelFunctions.SciEventKeyDown)
        {
            if (version < SciVersion.V3)
            {
                while ((ev = events.GetSciEvent(KernelFunctions.SciEventKeyDown)).Type != KernelFunctions.SciEventNone)
                {
                    if (ev.Character == SciKeyEsc)
                    {
                        return EventFlags.EscapeKey;
                    }
                }
            }
            else if (ev.Character == SciKeyEsc)
            {
                return EventFlags.EscapeKey;
            }
        }

        return EventFlags.None;
    }

    // ---- blobs (censorship mosaics) ----

    public int AddBlob(int blockSize, int top, int left, int bottom, int right)
    {
        if (_blobs.Count >= MaxBlobs)
        {
            return -1;
        }

        int blobNumber = 0;
        int index = 0;
        for (; index < _blobs.Count; index++, blobNumber++)
        {
            if (blobNumber < _blobs[index].BlobNumber)
            {
                break;
            }
        }

        _blobs.Insert(index, new Blob(blobNumber, blockSize, top, left, bottom, right));
        _needsUpdate = true;
        return blobNumber;
    }

    public void DeleteBlobs()
    {
        if (_blobs.Count > 0)
        {
            _blobs.Clear();
            _needsUpdate = true;
        }
    }

    public void DeleteBlob(int blobNumber)
    {
        int index = _blobs.FindIndex(b => b.BlobNumber == blobNumber);
        if (index >= 0)
        {
            _blobs.RemoveAt(index);
            _needsUpdate = true;
        }
    }

    // ---- rendering ----

    private bool IsNormallyComposited => _g.Version == SciVersion.V3 || (_g.IsMacintosh && _g.Version >= SciVersion.V2_1Late);

    private void InitOverlay()
    {
        // composited videos drawn through the overlay still need to hide what is behind them
        if (IsNormallyComposited && _planeIsOwned)
        {
            _plane = new Plane32(_g, _drawRect, Plane32.PlanePicColored);
            if (_priority != 0)
            {
                _plane.Priority = (short)_priority;
            }

            _g.Frameout.AddPlane(_plane);
        }

        // pending graphics changes go out before playback, or they flash over the video when it
        // yields to the scripts
        _g.Frameout.FrameOut(true);
    }

    private void RenderOverlay(ReadOnlySpan<byte> frame, int frameWidth, int frameHeight)
    {
        var buffer = _g.Frameout.CurrentBuffer;
        int lineCount = _blackLines ? 2 : 1;
        if (_doublePixels)
        {
            for (int y = 0; y < frameHeight * 2 && _drawRect.Top + y < buffer.Height; y += lineCount)
            {
                int source = (y >> 1) * frameWidth;
                int target = (_drawRect.Top + y) * buffer.Width + _drawRect.Left;
                for (int x = 0; x < frameWidth && _drawRect.Left + x * 2 + 1 < buffer.Width; x++)
                {
                    buffer.Pixels[target++] = frame[source];
                    buffer.Pixels[target++] = frame[source++];
                }
            }
        }
        else
        {
            int width = Math.Min(_drawRect.Width, frameWidth);
            for (int y = 0; y < frameHeight && y < _drawRect.Height; y += lineCount)
            {
                frame.Slice(y * frameWidth, width).CopyTo(buffer.Pixels.AsSpan((_drawRect.Top + y) * buffer.Width + _drawRect.Left, width));
            }
        }

        _g.Frameout.DirectFrameOut(_drawRect);
    }

    private void SubmitPalette(byte[] rawPalette)
    {
        if (_ignorePalettes)
        {
            return;
        }

        var palette = new SciPalette();
        for (int i = 0; i < _startColor; i++)
        {
            palette.Colors[i].Used = 0;
        }

        for (int i = _endColor + 1; i < 256; i++)
        {
            palette.Colors[i].Used = 0;
        }

        FillPalette(rawPalette, palette);

        if (_isComposited)
        {
            // the next frame's render submits it with the bitmap
            _g.SegMan.LookupBitmap(_bitmapId).SetPalette(palette);
        }
        else
        {
            _g.Palette.Submit(palette);
            _g.Palette.UpdateForFrame();
            _g.Palette.UpdateHardware();
        }
    }

    private void CloseOverlay()
    {
        if (IsNormallyComposited && _planeIsOwned && _plane is not null)
        {
            _g.Frameout.DeletePlane(_plane);
            _plane = null;
        }

        if (!_leaveLastFrame && _leaveScreenBlack)
        {
            _g.Frameout.FrameOut(true, _drawRect);
        }
    }

    private void InitComposited()
    {
        var scaleInfo = new ScaleInfo();
        if (_doublePixels)
        {
            scaleInfo.X *= 2;
            scaleInfo.Y *= 2;
            scaleInfo.Signal = ScaleSignals32.Manual;
        }
        else if (_stretchVertical)
        {
            scaleInfo.Y *= 2;
            scaleInfo.Signal = ScaleSignals32.Manual;
        }

        int hunkPaletteSize = HunkPalette.CalculateHunkPaletteSize(256, false);
        var frameout = _g.Frameout;
        var bitmap = _g.SegMan.AllocateBitmap(out _bitmapId, _drawRect.Width, _drawRect.Height, 255, 0, 0, frameout.ScreenWidth, frameout.ScreenHeight, hunkPaletteSize, false, false);
        bitmap.Pixels.Clear();

        var celInfo = new CelInfo32 { Type = CelType.Mem, Bitmap = _bitmapId };
        _decoder.SetSurfaceMemory(bitmap.RawData, bitmap.PixelsOffset, bitmap.Width, bitmap.Height);

        if (_planeIsOwned)
        {
            _plane = new Plane32(_g, _drawRect, Plane32.PlanePicColored);
            if (_priority != 0)
            {
                _plane.Priority = (short)_priority;
            }

            frameout.AddPlane(_plane);
            _screenItem = new ScreenItem(_g, _plane.Object, celInfo, default, scaleInfo);
        }
        else
        {
            _screenItem = new ScreenItem(_g, _plane!.Object, celInfo, new SciPoint(_drawRect.Left, _drawRect.Top), scaleInfo);
            if (_priority != 0)
            {
                _screenItem.Priority = (short)_priority;
            }
        }

        if (_blackLines)
        {
            _screenItem.DrawBlackLines = true;
        }

        frameout.AddScreenItem(_screenItem);

        // the video throttles its own output while the scripts call kFrameOut between yields
        frameout.ThrottleKernelFrameOut = false;
    }

    private void RenderComposited()
    {
        _g.Frameout.UpdateScreenItem(_screenItem!);
        _g.Frameout.FrameOut(true);
    }

    private void CloseComposited()
    {
        var frameout = _g.Frameout;
        if (!_bitmapId.IsNull)
        {
            _g.SegMan.FreeBitmap(_bitmapId);
            _bitmapId = RegT.Null;
        }

        if (!_planeIsOwned && _screenItem is not null)
        {
            frameout.DeleteScreenItem(_screenItem);
            _screenItem = null;
        }
        else if (_plane is not null)
        {
            frameout.DeletePlane(_plane);
            _plane = null;
        }

        if (!_leaveLastFrame && _leaveScreenBlack)
        {
            // this actually deletes the plane or screen item
            frameout.FrameOut(true);
        }

        frameout.ThrottleKernelFrameOut = true;
    }

    private void RenderFrame()
    {
        if (_isComposited)
        {
            RenderComposited();
            return;
        }

        var frame = _decoder.Surface;
        int width = _decoder.SurfaceWidth;
        int height = _decoder.SurfaceHeight;
        if (_blobs.Count == 0)
        {
            RenderOverlay(frame, width, height);
            return;
        }

        var censored = frame.ToArray();
        DrawBlobs(censored, width, height);
        RenderOverlay(censored, width, height);
    }

    private void DrawBlobs(byte[] frame, int width, int height)
    {
        foreach (var blob in _blobs)
        {
            for (int blockLeft = blob.Left; blockLeft < blob.Right; blockLeft += blob.BlockSize)
            {
                for (int blockTop = blob.Top; blockTop < blob.Bottom; blockTop += blob.BlockSize)
                {
                    byte color = frame[blockTop * width + blockLeft];
                    for (int y = blockTop; y < Math.Min(blockTop + blob.BlockSize, height); y++)
                    {
                        for (int x = blockLeft; x < Math.Min(blockLeft + blob.BlockSize, width); x++)
                        {
                            frame[y * width + x] = color;
                        }
                    }
                }
            }
        }
    }

    private void FillPalette(byte[] rawPalette, SciPalette outPalette)
    {
        for (int i = _startColor; i <= _endColor; i++)
        {
            int r = rawPalette[i * 3];
            int g = rawPalette[i * 3 + 1];
            int b = rawPalette[i * 3 + 2];
            if (_boostPercent != 100 && i >= _boostStartColor && i <= _boostEndColor)
            {
                r = Math.Clamp(r * _boostPercent / 100, 0, 255);
                g = Math.Clamp(g * _boostPercent / 100, 0, 255);
                b = Math.Clamp(b * _boostPercent / 100, 0, 255);
            }

            outPalette.Colors[i].R = (byte)r;
            outPalette.Colors[i].G = (byte)g;
            outPalette.Colors[i].B = (byte)b;
            outPalette.Colors[i].Used = 1;
        }
    }

    public void IgnorePalettes() => _ignorePalettes = true;

    public void SetShowCursor(bool showCursor) => _showCursor = showCursor;

    public void SetBlackoutArea(SciRect rect) => _blackoutRect = rect;

    public void SetPlane(int priority, RegT planeId)
    {
        _priority = priority;
        if (!planeId.IsNull)
        {
            _plane = _g.Frameout.Planes.FindByObject(planeId) ?? throw new SciVmException($"kPlayVMD: plane {planeId} not found");
            _planeIsOwned = false;
        }
    }

    /// <summary>GK2 sends 256 as the end colour; SSCI clamped what it used to 255.</summary>
    public void RestrictPalette(int startColor, int endColor)
    {
        _startColor = startColor & 0xFF;
        _endColor = Math.Min(255, endColor);
    }
}
