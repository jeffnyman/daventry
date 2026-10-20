using Daventry.Agi.Graphics;
using Daventry.Agi.Menu;
using Daventry.Agi.Resources;
using Daventry.Agi.Sound;
using Daventry.Agi.Text;
using Daventry.Core.Hosting;
using Daventry.Core.Input;
using Daventry.Core.Storage;

namespace Daventry.Agi;

/// <summary>
/// The AGI interpreter. The game loop runs as an async task that awaits the frame scheduler
/// wherever the original blocked; <see cref="Tick"/> advances the scheduler and presents the
/// display. A debug viewer mode shows pictures and views without running scripts.
/// </summary>
public sealed partial class AgiEngine : Engine
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(1000.0 / 60);

    private TimeSpan _engineTime;
    private Task? _vmTask;
    private int _presentedPaletteVersion = -1;
    private bool _viewerMode;
    private int? _shownDebugPicture;
    private int? _shownDebugView;
    private TimeSpan _accumulated;
    private int _ticks;

    public AgiEngine(IHost host, IGameContainer game, AgiDetectedGame detected, AgiRenderMode renderMode = AgiRenderMode.Ega)
        : base(host, game, detected)
    {
        Description = detected;
        State = new AgiGameState();
        Resources = new AgiResourceManager(game, detected, host.Log);
        Gfx = new GfxMgr(renderMode, detected.Version, detected.GameId);
        Resources.ViewDecoder = new AgiViewDecoder(detected, Gfx);
        Picture = new PictureMgr(State, Gfx, Resources, host.Log);
        Sprites = new SpritesMgr(this, Gfx);
        Text = new TextMgr(this, Gfx);
        SystemUI = new SystemUI(this, Gfx, Text);
        Inventory = new InventoryMgr(this);
        Menu = new GfxMenu(this);
        Sound = new SoundMgr(this);
        (_opCodes, _opCodesCond) = AgiOpCodes.Setup(detected);
    }

    public AgiDetectedGame Description { get; }

    public AgiGameState State { get; }

    public AgiResourceManager Resources { get; }

    public AgiWords Words => Resources.Words;

    public GfxMgr Gfx { get; }

    public PictureMgr Picture { get; }

    public SpritesMgr Sprites { get; }

    public TextMgr Text { get; }

    public SystemUI SystemUI { get; }

    public InventoryMgr Inventory { get; }

    public GfxMenu Menu { get; }

    public SoundMgr Sound { get; }

    public AgiMouse Mouse { get; } = new();

    /// <summary>Random source for wander and follow motions. Replaceable for deterministic tests.</summary>
    public Random Random { get; set; } = new();

    /// <summary>
    /// Debug picture viewer. When set before <see cref="Initialize"/>, the engine shows pictures
    /// instead of running the game. Left and Right step through pictures, Up and Down through
    /// views, P toggles the priority screen, Escape quits.
    /// </summary>
    public int? DebugPicture { get; set; }

    /// <summary>Debug view placed on the picture as ego, cycling its cels.</summary>
    public int? DebugView { get; set; }

    /// <summary>Number of interpreter instructions executed so far.</summary>
    public uint InstructionCounter => _instructionCounter;

    public override void Initialize()
    {
        Gfx.InitVideo();
        Text.CharAttrib_Set(15, 0);
        Text.ConfigureScreen(1);

        Host.Graphics.SetMode(Gfx.DisplayScreenWidth, Gfx.DisplayScreenHeight);
        Host.Graphics.SetTitle($"Daventry - {Detected.Title}");
        PushPaletteIfChanged();

        _viewerMode = DebugPicture.HasValue || DebugView.HasValue;
        if (_viewerMode)
        {
            Resources.Initialize();
            DebugPicture ??= NextResource(Resources.Directory.Picture, -1, +1);
            return;
        }

        // Runs synchronously until the first await, then returns here.
        _vmTask = RunGameAsync();
    }

    public override TickResult Tick(TimeSpan elapsed)
    {
        _engineTime += elapsed;

        while (Host.Input.TryRead(out var inputEvent))
        {
            if (inputEvent.Type == InputEventType.Quit)
            {
                QuitGame();
                return TickResult.Quit;
            }

            if (_viewerMode)
            {
                HandleViewerKey(inputEvent);
            }
            else
            {
                QueueHostEvent(inputEvent);
            }
        }

        if (_viewerMode)
        {
            return TickViewer(elapsed);
        }

        Scheduler.Advance(_engineTime);

        if (_vmTask is { IsFaulted: true })
        {
            var inner = _vmTask.Exception?.InnerException ?? _vmTask.Exception!;
            throw new AgiResourceException($"The interpreter failed in logic {State.CurLogicNr} at {State.CurLogic?.CIP}: {inner.Message}");
        }

        PushPaletteIfChanged();
        if (Gfx.DisplayDirty)
        {
            Host.Graphics.Present(Gfx.Display);
            Gfx.DisplayDirty = false;
        }

        if (_vmTask is null || _vmTask.IsCompleted || ShouldQuit)
        {
            return TickResult.Quit;
        }

        var wakeAt = Scheduler.WakeAt ?? _engineTime;
        var delay = wakeAt - _engineTime;
        return delay > TimeSpan.Zero ? TickResult.After(delay) : TickResult.Immediately;
    }

    // ---- debug viewer ----

    private void HandleViewerKey(InputEvent inputEvent)
    {
        if (inputEvent.Type != InputEventType.KeyDown)
        {
            return;
        }

        switch (inputEvent.Key)
        {
            case Key.Escape:
                QuitGame();
                break;
            case Key.Right:
                DebugPicture = NextResource(Resources.Directory.Picture, DebugPicture ?? -1, +1) ?? DebugPicture;
                break;
            case Key.Left:
                DebugPicture = NextResource(Resources.Directory.Picture, DebugPicture ?? 256, -1) ?? DebugPicture;
                break;
            case Key.Up:
                DebugView = NextResource(Resources.Directory.View, DebugView ?? -1, +1) ?? DebugView;
                break;
            case Key.Down:
                DebugView = NextResource(Resources.Directory.View, DebugView ?? 256, -1) ?? DebugView;
                break;
            case Key.P:
                Gfx.DebugShowMap(Gfx.PriorityScreenShown ? 0 : 1);
                break;
        }
    }

    private TickResult TickViewer(TimeSpan elapsed)
    {
        if (ShouldQuit)
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

        RunDebugViewer();

        PushPaletteIfChanged();
        if (Gfx.DisplayDirty)
        {
            Host.Graphics.Present(Gfx.Display);
            Gfx.DisplayDirty = false;
        }

        return TickResult.After(FrameInterval);
    }

    private void RunDebugViewer()
    {
        bool redraw = false;
        if (DebugPicture is { } picture && picture != _shownDebugPicture)
        {
            _shownDebugPicture = picture;
            redraw = true;
        }

        if (DebugView != _shownDebugView)
        {
            _shownDebugView = DebugView;
            redraw = true;
        }

        var ego = State.ScreenObjTable[AgiGameState.ScreenObjectsEgoEntry];
        if (redraw && _shownDebugPicture is { } pictureNr)
        {
            try
            {
                Sprites.FreeAllSprites();
                Gfx.ClearDisplay(0, false);
                Picture.DecodePicture(pictureNr, clearScreen: true);
                Picture.ShowPictureWithTransition();
                State.PictureShown = true;
                Host.Graphics.SetTitle($"Daventry - {Detected.Title} - picture {pictureNr}" + (DebugView is { } v ? $" view {v}" : string.Empty));

                ego.Reset();
                if (DebugView is { } viewNr && Resources.LoadView(viewNr) is not null)
                {
                    SetView(ego, viewNr);
                    ego.XPos = (GfxMgr.ScriptWidth - ego.XSize) / 2;
                    ego.YPos = 150;
                    ego.XPosPrev = ego.XPos;
                    ego.YPosPrev = ego.YPos;
                    ego.Flags = ViewFlags.Animated | ViewFlags.Update | ViewFlags.Drawn | ViewFlags.Cycling;
                    ego.CycleTime = 6;
                    ego.CycleTimeCount = 6;
                    ego.StepTime = 1;
                    ego.StepTimeCount = 1;
                    FixPosition(ego);
                    Sprites.BuildAllSpriteLists();
                    Sprites.DrawAllSpriteLists();
                    Sprites.ShowAllSpriteLists();
                }
            }
            catch (AgiResourceException ex)
            {
                Host.Log.Warning("agi", ex.Message);
            }
        }
        else if (_shownDebugView is not null && ego.HasFlags(ViewFlags.Animated | ViewFlags.Drawn))
        {
            UpdateScreenObjTable();
        }
    }

    private static int? NextResource(AgiDir[] entries, int from, int step)
    {
        for (int i = from + step; i >= 0 && i < entries.Length; i += step)
        {
            if (!entries[i].IsEmpty)
            {
                return i;
            }
        }

        return null;
    }

    private void PushPaletteIfChanged()
    {
        if (Gfx.PaletteVersion != _presentedPaletteVersion)
        {
            Host.Graphics.SetPalette(Gfx.CurrentPalette);
            _presentedPaletteVersion = Gfx.PaletteVersion;
        }
    }
}
