using Daventry.Core.Graphics;
using Daventry.Core.Hosting;
using Daventry.Core.Storage;
using Daventry.Sci.Engine;
using Daventry.Sci.Video;

namespace Daventry.Sci.Graphics;

/// <summary>
/// Plays Phantasmagoria 2's Duck videos (kPlayDuck). Port of ScummVM's <c>DuckPlayer</c> and the
/// <c>VideoPlayer</c> loop under it. The frames are true colour: ScummVM switches the screen to the
/// video's pixel format while one plays and copies the frames straight to the screen; here the
/// player keeps the last frame as an overlay that the engine draws over the game's screen,
/// converted to true colour, and hands the host as a true-colour frame. Without a host that takes
/// true colour the video cannot be shown, as ScummVM cannot on an 8-bit screen.
/// </summary>
public sealed class DuckPlayer
{
    public enum DuckStatus
    {
        Closed = 0,
        Open = 1,
        Playing = 2,
        Paused = 3,
    }

    private const int MaxChannelVolume = 255;

    private readonly Sci32Graphics _g;
    private readonly DuckDecoder _decoder;
    private Audio.Mixer? _mixer;
    private DuckAudioSource? _audioSource;
    private Plane32? _plane;
    private SciRect _drawRect;
    private int _volume = MaxChannelVolume;
    private bool _doublePixels;
    private bool _needsUpdate;
    private bool _haveCurrentFrame;
    private Surface32? _frame;
    private bool _warnedTrueColor;

    public DuckPlayer(Sci32Graphics g)
    {
        _g = g;
        _decoder = new DuckDecoder(() => g.State.Millis());
    }

    /// <summary>The mixer the video's sound plays through; without one the videos are silent.</summary>
    public Audio.Mixer? Mixer
    {
        get => _mixer;
        set => _mixer = value;
    }

    /// <summary>Whether the host can show true-colour frames; set by the engine.</summary>
    public bool TrueColorAvailable { get; set; }

    public DuckStatus Status { get; private set; }

    /// <summary>
    /// Set by kPlayDuckSetFrameOut: an opaque plane is put under the video while it plays, and
    /// the screen updated around it.
    /// </summary>
    public bool DoFrameOut { get; set; }

    /// <summary>The video frame to draw over the screen, at its place on it, while a video plays.</summary>
    public bool HasOverlay => Status != DuckStatus.Closed && _haveCurrentFrame;

    private EngineState State => _g.State;

    /// <summary>Opens <c>N.duk</c>. A video that cannot be opened leaves the player closed, and play does nothing.</summary>
    public void Open(int resourceId, int displayMode, int x, int y)
    {
        if (Status != DuckStatus.Closed)
        {
            throw new SciVmException($"Attempted to play {resourceId}.duk, but another video was loaded");
        }

        string fileName = $"{resourceId}.duk";
        if (!_g.ResMan.Game.Exists(fileName) || !_decoder.Load(_g.ResMan.Game.ReadAllBytes(fileName)))
        {
            _g.Log.Warning("sci.video", $"Failed to load {fileName}");
            return;
        }

        _doublePixels = displayMode != 0;

        // SSCI seems to incorrectly calculate the draw rect by scaling the origin in addition to
        // the width/height for the BR point
        SetDrawRect(x, y, _decoder.Width << (_doublePixels ? 1 : 0), _decoder.Height << (_doublePixels ? 1 : 0));

        if (DoFrameOut)
        {
            _plane = new Plane32(_g, _drawRect, Plane32.PlanePicColored);
            _g.Frameout.AddPlane(_plane);
            _g.Frameout.FrameOut(true);
        }

        // the frames are 16-bit colour; ScummVM cannot play them on an 8-bit screen either
        if (!TrueColorAvailable)
        {
            if (!_warnedTrueColor)
            {
                _g.Log.Warning("sci.video", "The host shows no true colour, so the Duck videos are skipped");
                _warnedTrueColor = true;
            }

            DeletePlane();
            _decoder.Close();
            return;
        }

        _g.Cursor.Hide();
        _g.Log.Debug("sci.video", $"Duck {fileName}: {_decoder.Width}x{_decoder.Height}, {_decoder.FrameCount} frames, sound {_decoder.Audio is not null}, at {_drawRect}");
        Status = DuckStatus.Open;
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

    /// <summary>Plays the video until it ends, the mouse is clicked or Escape is pressed.</summary>
    public async ValueTask Play(int lastFrameNo)
    {
        // the original does not check, but scripts get no word on whether open worked and play
        // anyway
        if (Status == DuckStatus.Closed)
        {
            return;
        }

        Status = DuckStatus.Playing;

        // ScummVM sets an end frame here; kPlayDuckPlay always plays to the end (-1)
        _ = lastFrameNo;

        await PlayUntilEvent(VmdPlayer.EventFlags.MouseDown | VmdPlayer.EventFlags.EscapeKey);
    }

    /// <summary>VideoPlayer::playUntilEvent.</summary>
    private async ValueTask<VmdPlayer.EventFlags> PlayUntilEvent(VmdPlayer.EventFlags flags)
    {
        // events queued before playback must not stop it
        State.Events.FlushEvents();
        StartPlayback();

        VmdPlayer.EventFlags stopFlag;
        while (true)
        {
            if (!_needsUpdate)
            {
                await State.SleepMs((int)_decoder.TimeToNextFrame);
            }

            // more than one update means playback is behind: those frames are skipped
            bool decoded = false;
            while (_decoder.NeedsUpdate)
            {
                decoded |= _decoder.DecodeNextFrame();
            }

            // some frames carry only sound, which is not an error
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

            stopFlag = VmdPlayer.VideoCheckForEvent(State, _g.Version, flags, _decoder.EndOfVideo);
            if (stopFlag != VmdPlayer.EventFlags.None)
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
            _audioSource = new DuckAudioSource(audio, _mixer.SampleRate) { Volume = _volume };
            _mixer.Add(_audioSource);
        }
    }

    /// <summary>
    /// The frame at the draw rect's size, pixels doubled for the display modes that double them
    /// (renderLQToSurface); ScummVM's black-lined option is off by default and not offered here.
    /// </summary>
    private void RenderFrame()
    {
        int width = _drawRect.Width;
        int height = _drawRect.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (_frame is null || _frame.Width != width || _frame.Height != height)
        {
            _frame = new Surface32(width, height);
        }

        var codec = _decoder.Codec;
        Span<uint> line = codec.Width <= 1024 ? stackalloc uint[codec.Width] : new uint[codec.Width];
        var pairs = codec.Pairs!;
        int pairsPerRow = codec.Width / 2;
        int scale = _doublePixels ? 2 : 1;
        for (int y = 0; y < height; y++)
        {
            int sourceY = y / scale;
            if (sourceY >= codec.Height)
            {
                break;
            }

            for (int i = 0; i < pairsPerRow; i++)
            {
                uint pair = pairs[sourceY * pairsPerRow + i];
                line[i * 2] = TrueMotion1Decoder.Rgb565ToXrgb((ushort)pair);
                line[i * 2 + 1] = TrueMotion1Decoder.Rgb565ToXrgb((ushort)(pair >> 16));
            }

            var row = _frame.Row(y);
            for (int x = 0; x < width && x / scale < codec.Width; x++)
            {
                row[x] = line[x / scale];
            }
        }

        _g.Frameout.ScreenDirty = true;
    }

    /// <summary>Draws the current video frame over a true-colour copy of the screen.</summary>
    public void DrawOverlay(Surface32 screen)
    {
        if (!HasOverlay || _frame is null)
        {
            return;
        }

        int width = Math.Min(_frame.Width, screen.Width - _drawRect.Left);
        for (int y = 0; y < _frame.Height && _drawRect.Top + y < screen.Height; y++)
        {
            _frame.Row(y)[..width].CopyTo(screen.Row(_drawRect.Top + y).Slice(_drawRect.Left, width));
        }
    }

    public void Close()
    {
        if (Status == DuckStatus.Closed)
        {
            return;
        }

        if (_audioSource is not null)
        {
            _mixer?.Remove(_audioSource);
            _audioSource = null;
        }

        _decoder.Close();
        _g.Cursor.Unhide();

        if (DoFrameOut)
        {
            DeletePlane();
            _g.Frameout.FrameOut(true);
        }

        // back to the indexed screen
        _g.Frameout.ScreenDirty = true;

        _drawRect = default;
        Status = DuckStatus.Closed;
        _volume = MaxChannelVolume;
        DoFrameOut = false;
        _needsUpdate = false;
        _haveCurrentFrame = false;
    }

    private void DeletePlane()
    {
        if (_plane is not null)
        {
            _g.Frameout.DeletePlane(_plane);
            _plane = null;
        }
    }

    /// <summary>kPlayDuckSetVolume: 0 to 127, Audio32's scale.</summary>
    public void SetVolume(int value)
    {
        _volume = value * MaxChannelVolume / Sound.Audio32.MaxVolume;
        if (_audioSource is not null)
        {
            _audioSource.Volume = _volume;
        }
    }
}
