using Daventry.Audio;
using Daventry.Core.Graphics;
using Daventry.Core.Hosting;
using Daventry.Core.Input;
using Daventry.Core.Storage;
using Daventry.Sci.Engine;
using Daventry.Sci.Engine.Parser;
using Daventry.Sci.Graphics;
using Daventry.Sci.Resources;
using Daventry.Sci.Sound;

namespace Daventry.Sci;

/// <summary>
/// The SCI engine for SCI0 to SCI1.1 games. The interpreter runs the game's own scripts; the
/// subsystems that are not ported yet (animation, text, controls, menus, parser, sound, saves)
/// are stubbed in the kernel so that games boot. With <see cref="DebugPicture"/> or
/// <see cref="DebugView"/> set before <see cref="Initialize"/> the engine runs as a resource
/// viewer instead: Left and Right step through pictures, Up and Down through views, L and C
/// through a view's loops and cels, M mirrors the picture, E cycles the EGA palette number,
/// P shows the priority screen, Escape quits.
/// </summary>
public sealed class SciEngine : Core.Hosting.Engine
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(1000.0 / 60);

    private readonly ScriptPatcher _scriptPatcher = new();

    /// <summary>Whether sound effects with a digital sample play the sample rather than the AdLib version.</summary>
    public bool PreferDigitalSfx { get; }

    /// <summary>The sound hardware the music plays through.</summary>
    public Sound.SciSoundDevice SoundDevice { get; }

    /// <summary>The script patcher, which records every patch it applied.</summary>
    public ScriptPatcher ScriptPatcher => _scriptPatcher;
    private int _presentedPaletteVersion = -1;
    private int? _shownPicture;
    private int? _shownView;
    private int _shownLoop = -1;
    private int _shownCel = -1;
    private bool _shownMirrored;
    private int _shownEgaPalette = -1;
    private bool _showPriority;
    private bool _priorityShown;
    private TimeSpan _accumulated;
    private TimeSpan _palVaryAccumulated;
    private int _ticks;
    private bool _quit;
    private bool _viewerMode;
    private List<int> _pictureIds = [];
    private List<int> _viewIds = [];
    private Task? _vmTask;
    private TimeSpan _engineTime;
    private readonly System.Diagnostics.Stopwatch _tickClock = new();
    private double _lastMillis;
    private int _clockReads;

    /// <summary>What a deterministic host's clock advances per read inside a tick, in milliseconds.</summary>
    private const double DeterministicStepMs = 1.0 / 16;

    /// <summary>
    /// The SCI16 palette timestamps are engine milliseconds from here: the system palette's own
    /// timestamps 0 (fresh) and 1 (just set up) must never come out of the clock, which ScummVM gets
    /// from a system clock that has been running for a while.
    /// </summary>
    private const uint PaletteClockBase = 1000;

    /// <summary>
    /// Where a deterministic host's calendar starts: kGetTime also answers the time of day and
    /// the date (KQ5 and QFG1 read it every cycle), and saved games are dated from it.
    /// </summary>
    private static readonly DateTime DeterministicClockStart = new(2000, 1, 1, 12, 0, 0);
    private Surface8? _frame;
    private Surface8? _shakeFrame;

    /// <summary>Presents a frame, shifted while kShakeScreen has the screen shaking.</summary>
    private void PresentFrame(Surface8 frame)
    {
        var (dx, dy) = Screen.ShakeOffset;
        if (dx == 0 && dy == 0)
        {
            Host.Graphics.Present(frame);
            return;
        }

        if (_shakeFrame is null || _shakeFrame.Width != frame.Width || _shakeFrame.Height != frame.Height)
        {
            _shakeFrame = new Surface8(frame.Width, frame.Height);
        }

        _shakeFrame.CopyFromShifted(frame, dx, dy);
        Host.Graphics.Present(_shakeFrame);
    }
    private Mixer? _mixer;
    private ClockedSource? _audioClock;
    private int _tickCount;
    private bool _audioStarted;
    private int _presentedCursorVersion = -1;
    private SciPoint _presentedCursorPos;

    public SciEngine(IHost host, IGameContainer game, SciDetectedGame detected, bool preferDigitalSfx = true, Sound.SciSoundDevice soundDevice = Sound.SciSoundDevice.AdLib)
        : base(host, game, detected)
    {
        Description = detected;
        PreferDigitalSfx = preferDigitalSfx;
        SoundDevice = soundDevice;
        Resources = new ResourceManager(game, host.Log)
        {
            GameId = detected.Gid,
            IsCd = detected.IsCd,
            IsDemo = detected.IsDemo,
            Platform = detected.Platform,
        };
    }

    public SciDetectedGame Description { get; }

    public ResourceManager Resources { get; }

    /// <summary>Scheduler the interpreter waits on; the engine advances it from <see cref="Tick"/>.</summary>
    public FrameScheduler Scheduler { get; } = new();

    public GfxScreen Screen { get; private set; } = null!;

    public GfxPalette16 Palette { get; private set; } = null!;

    public GfxPorts Ports { get; private set; } = null!;

    public GfxCache Cache { get; private set; } = null!;

    public GfxPaint16 Paint { get; private set; } = null!;

    public GfxText16 Text { get; private set; } = null!;

    /// <summary>The interpreter state; null in viewer mode.</summary>
    public EngineState? State { get; private set; }

    public SegManager? SegMan { get; private set; }

    public Kernel? Kernel { get; private set; }

    public GameFeatures? Features { get; private set; }

    public SciEventManager? Events { get; private set; }

    /// <summary>Picture to show in viewer mode. Set before <see cref="Initialize"/>.</summary>
    public int? DebugPicture { get; set; }

    /// <summary>View to place on the picture in viewer mode.</summary>
    public int? DebugView { get; set; }

    public int DebugLoop { get; set; }

    public int DebugCel { get; set; }

    public bool DebugMirrored { get; set; }

    public int DebugEgaPalette { get; set; }

    /// <summary>
    /// Whether the game uses the SCI0 graphics functions that did not adjust against the window
    /// manager port. Detected from script 994 once the interpreter is up; the viewer only counts
    /// the early SCI0 interpreter.
    /// </summary>
    public bool UsesOldGfxFunctions => Features?.UsesOldGfxFunctions ?? Resources.Version == SciVersion.V0Early;

    public bool ShouldQuit => _quit;

    /// <summary>Whether the game runs on the SCI32 interpreter (SCI2 and later): planes and screen items instead of ports and windows.</summary>
    public bool IsSci32 => Resources.Version >= SciVersion.V2;

    public override void Initialize()
    {
        Resources.Init();
        if (Resources.ViewType == ViewType.Unknown)
        {
            throw new NotSupportedException($"{Detected.Title}: the view type could not be detected.");
        }

        if (IsSci32)
        {
            // the SCI32 graphics engine is not ported yet: the interpreter runs and every graphics
            // kernel call is logged once and ignored
            _viewerMode = DebugPicture.HasValue || DebugView.HasValue;
            if (_viewerMode)
            {
                throw new NotSupportedException($"{Detected.Title}: the resource viewer does not show SCI32 resources yet.");
            }

            InitGame();
            InitGraphics32();
            Host.Graphics.SetMode(_frame!.Width, _frame.Height);
            Host.Graphics.SetPalette(Gfx32!.Palette.HardwarePalette);
            _presentedPaletteVersion = Gfx32.Palette.HardwarePaletteVersion;
            Host.Graphics.SetTitle($"Daventry - {Detected.Title}");
            using (NoSynchronizationContext.Enter())
            {
                _vmTask = RunGameAsync();
            }

            return;
        }

        Screen = new GfxScreen(Resources, Resources.Version);
        Palette = new GfxPalette16(Resources, Screen, Game, Resources.Version) { MillisecondsNow = () => Millis() + PaletteClockBase };
        Ports = new GfxPorts(Screen) { IsDemo = Description.IsDemo };
        Cache = new GfxCache(Resources, Screen, Palette, Resources.Version, Description.Gid, Description.IsDemo, Host.Log);
        Paint = new GfxPaint16(Resources, Ports, Screen, Palette, Cache, Resources.Version, Description.Gid);

        _viewerMode = DebugPicture.HasValue || DebugView.HasValue;
        if (!_viewerMode)
        {
            InitGame();
        }

        InitGraphics();
        Host.Graphics.SetMode(Screen.DisplayWidth, Screen.DisplayHeight);
        Host.Graphics.SetTitle($"Daventry - {Detected.Title}");
        PushPaletteIfChanged();

        if (_viewerMode)
        {
            InitViewer();
            return;
        }

        // runs synchronously until the first await, then returns here
        using (NoSynchronizationContext.Enter())
        {
            _vmTask = RunGameAsync();
        }
    }

    /// <summary>Creates the interpreter and loads script 0. Mirrors SciEngine::initGame.</summary>
    private void InitGame()
    {
        SegMan = new SegManager(Resources, _scriptPatcher, Resources.Version, Description.Gid, Host.Log);
        Kernel = new Kernel(Resources, SegMan, Resources.Version, Description.Gid, Description.Platform, Host.Log);
        SegMan.ScriptInstantiated = script => GuestAdditions.InstantiateScriptHook(State!, script);
        var s = State = new EngineState(SegMan, Resources, Kernel, Resources.Version, Description.Gid, Host.Log)
        {
            Platform = Description.Platform,
            IsCd = Description.IsCd,
            IsDemo = Description.IsDemo,
            Language = Description.Language,
        };
        s.Features = Features = new GameFeatures(s);
        _scriptPatcher.Configure(s, PreferDigitalSfx);
        s.Events = Events = new SciEventManager(Resources.DetectFontExtended(), Resources.Version);
        s.Millis = Millis;
        s.SleepMs = SleepAsync;
        if (Host.RandomSeed is { } seed)
        {
            s.Rng = new Random(seed);
        }

        if (Host.DeterministicTime)
        {
            s.ClockStart = DeterministicClockStart;
        }

        s.ShouldQuit = () => _quit;

        InitGameState();

        s.OpcodeFormats = Vm.AdjustOpcodeFormats(Features.DetectLofsType(), Resources.Version);
        // needs the class table, so it comes after script 0 is loaded
        Kernel.LoadKernelNames(s);
        SciLanguage.SetSciLanguage(s);

        // the sound engine comes after the kernel (selectors) and the features (sound version)
        _mixer = new Mixer(Host.Audio.SampleRate);
        s.Audio = new AudioPlayer(Resources, Host.Audio.SampleRate, Host.Log);
        s.Sync = new SciSync(s);
        s.Sound = new SoundCommandParser(s, Features.DetectDoSoundType(), Host.Audio.SampleRate, PreferDigitalSfx, SoundDevice);
        _mixer.Add(s.Sound.Music);
        _mixer.Add(s.Audio);
        if (IsSci32)
        {
            s.Audio32 = new Audio32(s, Host.Audio.SampleRate, Host.Log);
            s.Sound.Music.Audio32 = s.Audio32;
            _mixer.Add(s.Audio32);
        }

        _audioClock = new ClockedSource(_mixer, Host.Audio.SampleRate);
        s.UpdateSci0SoundCues = s.Sound.UpdateSci0Cues;
        StartAudio();

        s.Saves = Host.Saves;
        s.SaveNamePrefix = Description.GameId + ".";

        s.Said = new Said(Host.Log);
        s.Message = new MessageState(Resources, SegMan, Description.Gid, Host.Log);
        if (Features.HasParser)
        {
            s.Vocabulary = new Vocabulary(Resources, Resources.Version, true, false, Host.Log);
        }

        s.InvalidateParserEvent = () =>
        {
            if (s.Vocabulary is { } voc)
            {
                voc.ParserEvent = RegT.Null;
            }
        };
    }

    /// <summary>Resets the interpreter state and instantiates script 0 at segment 1.</summary>
    private void InitGameState()
    {
        var s = State!;
        var segMan = SegMan!;

        // script 0 needs to be allocated before anything else
        int script0Segment = segMan.GetScriptSegment(0, ScriptLoadType.Lock);
        var stack = segMan.AllocateStack(EngineState.VmStackSize);
        s.GcCountDown = EngineState.GcInterval - 1;

        if (script0Segment != 1)
        {
            throw new SciVmException($"{Detected.Title}: script 0 was instantiated at segment {script0Segment} instead of 1");
        }

        s.InitGlobals();
        segMan.InitSysStrings();
        s.Acc = RegT.Null;
        s.Prev = RegT.Null;
        s.ExecutionStack.Clear();
        s.AbortScriptProcessing = AbortGameState.None;
        s.GameIsRestarting = GameIsRestartingFlags.None;
        s.Stack = stack.Entries;

        if (segMan.InstantiateScript(0) == 0)
        {
            throw new SciVmException($"{Detected.Title}: could not instantiate script 0");
        }

        s.ThrottleLastTime = 0;
        s.ThrottleTrigger = false;

        int gameObjectOffset = Resources.FindGameObject(true);
        if (gameObjectOffset <= 0)
        {
            throw new SciVmException($"{Detected.Title}: the game object was not found in script 0");
        }

        s.GameObjectAddress = RegT.Make(1, (ushort)gameObjectOffset);
        if (segMan.GetObject(s.GameObjectAddress) is null)
        {
            throw new SciVmException($"{Detected.Title}: the game object at {s.GameObjectAddress} is not an object");
        }
    }

    /// <summary>Wires the graphics subsystems to the interpreter. Mirrors SciEngine::initGraphics.</summary>
    private void InitGraphics()
    {
        bool useEarlyTextCalculations = Features?.UseEarlyGetLongestTextCalculations ?? false;
        Text = new GfxText16(Cache, Ports, Paint, Screen, Resources.Version, useEarlyTextCalculations);
        Ports.Paint = Paint;
        Ports.SetFont = Text.SetFont;
        Ports.DrawTitle = (title, rect) => Text.Box(title, true, rect, TextAlignment.Center, 0);
        Paint.Text = Text;
        Paint.Log = Host.Log;
        Paint.UsesOldGfxFunctions = UsesOldGfxFunctions;

        if (State is { } s)
        {
            Paint.SegMan = SegMan;
            Text.SegMan = SegMan;
            s.Screen = Screen;
            s.Palette = Palette;
            s.Ports = Ports;
            s.Cache = Cache;
            s.Paint = Paint;
            s.Text = Text;
            s.Compare = new GfxCompare(s);
            s.Transitions = new GfxTransitions(Screen, Palette, Resources.Version, Millis, SleepAsync, Host.Log);
        }

        Ports.Init(UsesOldGfxFunctions, Resources.Version, Description.Gid);
        Palette.SetDefault();

        if (State is { } state)
        {
            // the animation code reads the game object's class, so it comes after the ports
            state.Animate = new GfxAnimate(state, state.Transitions);
            state.IsFastCastEnabled = () => state.Animate.IsFastCastEnabled;
            Paint.Animate = state.Animate;
            state.Controls = new GfxControls16(state);
            state.Menu = new GfxMenu(state);
            state.Cursor = new GfxCursor(state);
            state.RefreshCursorPosition = state.Cursor.RefreshPosition;
            _frame = new Surface8(Screen.DisplayWidth, Screen.DisplayHeight);
        }
    }

    /// <summary>The SCI32 graphics subsystems, or null for an SCI16 game.</summary>
    public Sci32Graphics? Gfx32 { get; private set; }

    /// <summary>The SCI32 side of SciEngine::initGraphics: palette, remapper, cursor, transitions and the frameout with its planes.</summary>
    private void InitGraphics32()
    {
        var s = State!;
        var g = new Sci32Graphics(s, Host.Log);
        Gfx32 = g;
        s.Gfx32 = g;
        g.Palette = new GfxPalette32(g);
        g.Remap = new GfxRemap32(g);
        g.Cursor = new GfxCursor32(g);
        s.Compare = new GfxCompare(s);
        g.Transitions = new GfxTransitions32(g);
        g.Frameout = new GfxFrameout(g);
        g.Cursor.Init(new PixelBuffer(g.Frameout.CurrentBuffer));
        g.Text = new GfxText32(g);
        g.Frameout.Run();
        s.CelObjViewInfo = new CelObjViewInfo(g);
        g.Video = new Video32(g);
        g.Paint = new GfxPaint32(g);
        g.Controls = new GfxControls32(g);
        _frame = new Surface8(g.Frameout.ScreenWidth, g.Frameout.ScreenHeight);
    }

    private sealed class CelObjViewInfo(Sci32Graphics g) : ICelObjViewInfo
    {
        public int GetNumLoops(int viewId) => CelObjView.GetNumLoops(g, viewId);

        public int GetNumCels(int viewId, int loopNo) => CelObjView.GetNumCels(g, viewId, loopNo);
    }

    /// <summary>Presents the SCI32 hardware frame and palette when they changed.</summary>
    private void PresentSci32()
    {
        var g = Gfx32!;
        var palette = g.Palette;
        if (palette.HardwarePaletteVersion != _presentedPaletteVersion)
        {
            Host.Graphics.SetPalette(palette.HardwarePalette);
            _presentedPaletteVersion = palette.HardwarePaletteVersion;
            g.Frameout.ScreenDirty = true;
        }

        var frameout = g.Frameout;
        if (!frameout.ScreenDirty)
        {
            return;
        }

        var (dx, dy) = frameout.ShakeOffset;
        if (dx == 0 && dy == 0)
        {
            Host.Graphics.Present(frameout.Screen);
        }
        else
        {
            _frame!.CopyFromShifted(frameout.Screen, dx, dy);
            Host.Graphics.Present(_frame);
        }

        frameout.ScreenDirty = false;
    }

    /// <summary>The game loop. Mirrors SciEngine::runGame.</summary>
    private async Task RunGameAsync()
    {
        var s = State!;
        var sel = Kernel!.Selectors;
        InitStackBaseWithSelector(sel.Play);

        while (true)
        {
            s.ExecutionStackPosChanged = false;
            s.AbortScriptProcessing = AbortGameState.None;
            await Vm.Run(s);

            if (s.AbortScriptProcessing == AbortGameState.RestartGame)
            {
                s.Sound.ClearPlayList();
                s.Audio.StopAudio();
                SegMan!.ResetSegMan();
                InitGameState();
                if (!IsSci32)
                {
                    Ports.Reset();
                    s.Menu.Reset();
                }

                s.Vocabulary?.Reset();
                InitStackBaseWithSelector(sel.Play);
                s.GameIsRestarting = GameIsRestartingFlags.Restart;
                s.ThrottleLastTime = 0;
                s.AbortScriptProcessing = AbortGameState.None;
            }
            else if (s.AbortScriptProcessing == AbortGameState.LoadGame)
            {
                // the game was restored from within the game: the VM calls the game object's
                // replay method next
                s.ExecutionStack.Clear();
                s.Xs = null;
                s.ExecutionStackBase = 0;
                s.AbortScriptProcessing = AbortGameState.None;
                InitStackBaseWithSelector(sel.Replay);
            }
            else
            {
                break;
            }
        }

        _quit = true;
    }

    /// <summary>Saves the running game to a slot, like the host's own save menu would. Slot 0 is the autosave.</summary>
    public bool SaveGame(int slot, string description)
        => State is { } s && Savegame.Save(s, slot, description, string.Empty);

    /// <summary>Asks the game to restore a slot at its next safe point.</summary>
    public void RequestRestore(int slot)
    {
        if (State is { } s)
        {
            s.DelayedRestoreGameId = slot;
        }
    }

    /// <summary>The saved games of this game, newest first.</summary>
    public List<SavegameDesc> ListSavegames() => State is { } s ? Savegame.ListSavegames(s) : [];

    /// <summary>Calls a selector of the game object as the first frame on the execution stack.</summary>
    private void InitStackBaseWithSelector(int selector)
    {
        var s = State!;
        s.Stack[0] = RegT.Make(0, (ushort)selector);
        s.Stack[1] = RegT.Null;
        if (Vm.SendSelector(s, s.GameObjectAddress, s.GameObjectAddress, 0, 2, 0) is null)
        {
            throw new SciVmException("initStackBaseWithSelector: error while registering the first selector in the call stack");
        }
    }

    /// <summary>
    /// Engine time in milliseconds. Scripts that busy-wait on kGetTime (KQ5's speed benchmark,
    /// Dialog timeouts) need time to pass while the interpreter runs inside one tick, so the
    /// wall-clock time spent in the current tick is added to the tick-driven engine time; a host
    /// without a real clock gets a fixed step per read instead, which keeps a run reproducible.
    /// </summary>
    private uint Millis()
    {
        double inTick = Host.DeterministicTime ? ++_clockReads * DeterministicStepMs : _tickClock.Elapsed.TotalMilliseconds;
        double now = _engineTime.TotalMilliseconds + inTick;
        if (now > _lastMillis)
        {
            _lastMillis = now;
        }

        return (uint)_lastMillis;
    }

    private ValueTask SleepAsync(int milliseconds)
    {
        if (milliseconds <= 0)
        {
            return default;
        }

        State!.LastSleepStep = State.ScriptStepCounter;
        return WaitAsync(milliseconds);

        async ValueTask WaitAsync(int ms) => await Scheduler.Delay(ms);
    }

    public override TickResult Tick(TimeSpan elapsed)
    {
        using var noContext = NoSynchronizationContext.Enter();
        _engineTime += elapsed;
        _tickClock.Restart();
        _clockReads = 0;

        while (Host.Input.TryRead(out var inputEvent))
        {
            if (inputEvent.Type == InputEventType.Quit)
            {
                _quit = true;
                return TickResult.Quit;
            }

            if (_viewerMode)
            {
                HandleViewerKey(inputEvent);
            }
            else
            {
                Events!.Push(inputEvent);
            }
        }

        if (_viewerMode)
        {
            return TickViewer(elapsed);
        }

        if (Gfx32 is { } g32)
        {
            // the hardware mouse moved: the cursor follows it within its restricted area
            var mousePos = Events!.MousePos;
            g32.Cursor.DeviceMoved(ref mousePos);
        }

        if (!IsSci32)
        {
            // the palette timer runs at sixty ticks a second
            _palVaryAccumulated += elapsed;
            while (_palVaryAccumulated >= FrameInterval)
            {
                _palVaryAccumulated -= FrameInterval;
                Palette.PalVaryTick();
            }

            Palette.PalVaryUpdate();
        }

        _tickCount++;
        if (State is { TraceKernelCalls: true } tracing)
        {
            tracing.TraceKernelCall($"-- tick {_tickCount}");
        }

        _audioClock?.Update(_engineTime);
        Scheduler.Advance(_engineTime);

        if (_vmTask is { IsFaulted: true })
        {
            var inner = _vmTask.Exception?.InnerException ?? _vmTask.Exception!;
            string location = State is { } s ? s.DescribeLocation() : string.Empty;
            string stack = State is { } st ? "\n" + st.DescribeCallStack() : string.Empty;
            throw new SciVmException($"The interpreter failed at {location}: {inner.Message}{stack}", inner);
        }

        if (IsSci32)
        {
            PresentSci32();
        }
        else
        {
            PushPaletteIfChanged();
            PresentWithCursor();
        }

        if (_vmTask is null || _vmTask.IsCompleted || _quit)
        {
            return TickResult.Quit;
        }

        var wakeAt = Scheduler.WakeAt ?? _engineTime;
        var delay = wakeAt - _engineTime;
        return delay > TimeSpan.Zero ? TickResult.After(delay) : TickResult.Immediately;
    }

    private void StartAudio()
    {
        try
        {
            Host.Audio.Start(_audioClock!);
            _audioStarted = true;
        }
        catch (Exception ex)
        {
            Host.Log.Warning("sci.sound", $"Audio output unavailable, running silent: {ex.Message}");
        }
    }

    public override void Dispose()
    {
        State?.CloseTrace();
        if (_audioStarted)
        {
            Host.Audio.Stop();
            _audioStarted = false;
        }

        base.Dispose();
    }

    /// <summary>Presents the screen with the software cursor drawn on top when either changed.</summary>
    private void PresentWithCursor()
    {
        var cursor = State!.Cursor;
        var pos = cursor.GetPosition();
        bool cursorChanged = cursor.Version != _presentedCursorVersion || pos.X != _presentedCursorPos.X || pos.Y != _presentedCursorPos.Y;
        if (!Screen.DisplayDirty && !cursorChanged)
        {
            return;
        }

        Array.Copy(Screen.Display.Pixels, _frame!.Pixels, _frame.Pixels.Length);
        cursor.Draw(_frame);
        PresentFrame(_frame);
        Screen.DisplayDirty = false;
        _presentedCursorVersion = cursor.Version;
        _presentedCursorPos = pos;
    }

    private void PushPaletteIfChanged()
    {
        if (Screen.PaletteVersion != _presentedPaletteVersion)
        {
            Host.Graphics.SetPalette(Screen.HostPalette);
            _presentedPaletteVersion = Screen.PaletteVersion;

            // a palette change recolours the whole screen without touching a pixel (intensity fades,
            // palette cycling, pal-vary), so the frame has to be presented for it to show; ScummVM
            // gets this from updateScreen running on every wait
            Screen.DisplayDirty = true;
        }
    }

    // ---- resource viewer ----

    private void InitViewer()
    {
        _pictureIds = Resources.ListResources(ResourceType.Pic).Select(id => (int)id.Number).Distinct().Order().ToList();
        _viewIds = Resources.ListResources(ResourceType.View).Select(id => (int)id.Number).Distinct().Order().ToList();

        // a picture or view number that does not exist snaps to the next one that does
        if (DebugPicture is null || !_pictureIds.Contains(DebugPicture.Value))
        {
            DebugPicture = Step(_pictureIds, DebugPicture is { } p ? p - 1 : null, +1) ?? _pictureIds.LastOrDefault();
        }

        if (DebugView is { } requestedView && !_viewIds.Contains(requestedView))
        {
            DebugView = Step(_viewIds, requestedView - 1, +1) ?? _viewIds.LastOrDefault();
        }
    }

    private TickResult TickViewer(TimeSpan elapsed)
    {
        if (_quit)
        {
            return TickResult.Quit;
        }

        _accumulated += elapsed;
        if (_ticks > 0 && _accumulated < FrameInterval)
        {
            return TickResult.After(FrameInterval - _accumulated);
        }

        _accumulated = TimeSpan.Zero;
        _ticks++;

        RunViewer();

        PushPaletteIfChanged();
        if (Screen.DisplayDirty)
        {
            PresentFrame(Screen.Display);
            Screen.DisplayDirty = false;
        }

        return TickResult.After(FrameInterval);
    }

    private void HandleViewerKey(InputEvent inputEvent)
    {
        if (inputEvent.Type != InputEventType.KeyDown)
        {
            return;
        }

        switch (inputEvent.Key)
        {
            case Key.Escape:
                _quit = true;
                break;
            case Key.Right:
                DebugPicture = Step(_pictureIds, DebugPicture, +1) ?? DebugPicture;
                break;
            case Key.Left:
                DebugPicture = Step(_pictureIds, DebugPicture, -1) ?? DebugPicture;
                break;
            case Key.Up:
                DebugView = Step(_viewIds, DebugView, +1) ?? DebugView;
                DebugLoop = 0;
                DebugCel = 0;
                break;
            case Key.Down:
                DebugView = Step(_viewIds, DebugView, -1) ?? DebugView;
                DebugLoop = 0;
                DebugCel = 0;
                break;
            case Key.L:
                DebugLoop++;
                DebugCel = 0;
                break;
            case Key.C:
                DebugCel++;
                break;
            case Key.M:
                DebugMirrored = !DebugMirrored;
                break;
            case Key.E:
                DebugEgaPalette = (DebugEgaPalette + 1) % 4;
                break;
            case Key.P:
                _showPriority = !_showPriority;
                break;
        }
    }

    private static int? Step(List<int> ids, int? current, int step)
    {
        if (ids.Count == 0)
        {
            return null;
        }

        if (current is null)
        {
            return step > 0 ? ids[0] : ids[^1];
        }

        int index = ids.BinarySearch(current.Value);
        if (index < 0)
        {
            index = ~index - (step > 0 ? 1 : 0);
        }

        index += step;
        return index >= 0 && index < ids.Count ? ids[index] : null;
    }

    private void RunViewer()
    {
        var view = DebugView is { } viewId ? Cache.GetView(viewId) : null;
        if (view is not null)
        {
            DebugLoop %= Math.Max(1, view.LoopCount);
            DebugCel %= Math.Max(1, view.GetCelCount(DebugLoop));
        }

        bool redraw = DebugPicture != _shownPicture
            || DebugView != _shownView
            || DebugLoop != _shownLoop
            || DebugCel != _shownCel
            || DebugMirrored != _shownMirrored
            || DebugEgaPalette != _shownEgaPalette
            || _showPriority != _priorityShown;
        if (!redraw)
        {
            return;
        }

        _shownPicture = DebugPicture;
        _shownView = DebugView;
        _shownLoop = DebugLoop;
        _shownCel = DebugCel;
        _shownMirrored = DebugMirrored;
        _shownEgaPalette = DebugEgaPalette;
        _priorityShown = _showPriority;

        string title = $"Daventry - {Detected.Title}";
        try
        {
            if (DebugPicture is { } pictureId && Resources.TestResource(new ResourceId(ResourceType.Pic, (ushort)pictureId)) is not null)
            {
                Paint.KernelDrawPicture(pictureId, DebugMirrored, false, DebugEgaPalette);
                title += $" - picture {pictureId}" + (DebugMirrored ? " mirrored" : string.Empty);
            }
            else
            {
                Ports.SetPort(Ports.PicWind);
                Paint.ClearScreen(Screen.ColorWhite);
            }

            if (view is not null)
            {
                int x = Screen.ScriptWidth / 2;
                int y = 150;
                var oldPort = Ports.SetPort(Ports.PicWind);
                view.GetCelRect(DebugLoop, DebugCel, x, y, 0, out var celRect);
                byte priority = Ports.KernelCoordinateToPriority(y);
                Paint.DrawCel(view, DebugLoop, DebugCel, celRect, priority, 0);
                Ports.SetPort(oldPort);
                title += $" - view {view.ResourceId} loop {DebugLoop} cel {DebugCel}";
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            Host.Log.Warning("sci.gfx", $"picture {DebugPicture} view {DebugView}: {ex.Message}");
        }

        Screen.PicNotValid = 0;
        Screen.PicNotValidSci11 = 0;
        Palette.SetOnScreen();
        Screen.DebugShowMap(_showPriority ? 1 : 3);
        Screen.CopyToScreen();
        Host.Graphics.SetTitle(title);
    }
}
