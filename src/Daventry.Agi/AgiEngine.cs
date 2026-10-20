using Daventry.Agi.Graphics;
using Daventry.Agi.Resources;
using Daventry.Core.Hosting;
using Daventry.Core.Input;
using Daventry.Core.Storage;

namespace Daventry.Agi;

/// <summary>
/// The AGI interpreter. Resources, pictures, views and sprites are real; the interpreter cycle is
/// not ported yet, so the engine runs a picture and view viewer instead of a game.
/// </summary>
public sealed partial class AgiEngine : Engine
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(1000.0 / 60);

    private int _presentedPaletteVersion = -1;
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
    }

    public AgiDetectedGame Description { get; }

    public AgiGameState State { get; }

    public AgiResourceManager Resources { get; }

    public GfxMgr Gfx { get; }

    public PictureMgr Picture { get; }

    public SpritesMgr Sprites { get; }

    /// <summary>Random source for wander and follow motions. Replaceable for deterministic tests.</summary>
    public Random Random { get; set; } = new();

    /// <summary>
    /// Debug picture viewer. When set, the engine draws this picture instead of running the game.
    /// Left and Right step through pictures, Up and Down through views, P toggles the priority
    /// screen, Escape quits.
    /// </summary>
    public int? DebugPicture { get; set; }

    /// <summary>Debug view placed on the picture as ego, cycling its cels.</summary>
    public int? DebugView { get; set; }

    // ---- VM variables and flags (global.cpp) ----

    public byte GetVar(int varNr) => State.Vars[varNr];

    public void SetVar(int varNr, byte value) => State.Vars[varNr] = value;

    public bool GetFlag(int flagNr) => State.Flags[flagNr];

    public void SetFlag(int flagNr, bool value) => State.Flags[flagNr] = value;

    public override void Initialize()
    {
        Resources.Initialize();

        Gfx.InitVideo();
        // The status line occupies text row 0 and the game screen starts at row 1. In ScummVM this
        // comes from TextMgr::configureScreen(1), which is ported with the text subsystem.
        Gfx.SetRenderStartOffset(1 * 8);

        Host.Graphics.SetMode(Gfx.DisplayScreenWidth, Gfx.DisplayScreenHeight);
        Host.Graphics.SetTitle($"Daventry - {Detected.Title}");
        PushPaletteIfChanged();

        DebugPicture ??= NextResource(Resources.Directory.Picture, -1, +1);
    }

    public override TickResult Tick(TimeSpan elapsed)
    {
        while (Host.Input.TryRead(out var inputEvent))
        {
            if (inputEvent.Type == InputEventType.Quit)
            {
                return TickResult.Quit;
            }

            if (inputEvent.Type == InputEventType.KeyDown)
            {
                switch (inputEvent.Key)
                {
                    case Key.Escape:
                        return TickResult.Quit;
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
            // animate: this is the per-cycle update the interpreter will do once ported
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
