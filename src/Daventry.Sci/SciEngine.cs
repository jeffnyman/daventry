using Daventry.Core.Hosting;
using Daventry.Core.Input;
using Daventry.Core.Storage;
using Daventry.Sci.Graphics;
using Daventry.Sci.Resources;

namespace Daventry.Sci;

/// <summary>
/// The SCI engine. The interpreter is not ported yet, so the engine runs only as a resource
/// viewer: set <see cref="DebugPicture"/> or <see cref="DebugView"/> before <see cref="Initialize"/>
/// and it shows pictures and views with the SCI16 graphics code. Left and Right step through
/// pictures, Up and Down through views, L and C through a view's loops and cels, M mirrors the
/// picture, E cycles the EGA palette number, P shows the priority screen, Escape quits.
/// </summary>
public sealed class SciEngine : Engine
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(1000.0 / 60);

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
    private int _ticks;
    private bool _quit;
    private List<int> _pictureIds = [];
    private List<int> _viewIds = [];

    public SciEngine(IHost host, IGameContainer game, SciDetectedGame detected)
        : base(host, game, detected)
    {
        Description = detected;
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

    public GfxScreen Screen { get; private set; } = null!;

    public GfxPalette16 Palette { get; private set; } = null!;

    public GfxPorts Ports { get; private set; } = null!;

    public GfxCache Cache { get; private set; } = null!;

    public GfxPaint16 Paint { get; private set; } = null!;

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
    /// manager port. ScummVM detects this from script 994 for late SCI0 games; until the script
    /// code is ported only the early SCI0 interpreter counts.
    /// </summary>
    public bool UsesOldGfxFunctions => Resources.Version == SciVersion.V0Early;

    public override void Initialize()
    {
        Resources.Init();
        if (Resources.Version >= SciVersion.V2)
        {
            throw new NotSupportedException($"{Detected.Title}: SCI32 games ({Resources.Version.Describe()}) are not supported yet.");
        }

        if (Resources.ViewType == ViewType.Unknown)
        {
            throw new NotSupportedException($"{Detected.Title}: the view type could not be detected.");
        }

        Screen = new GfxScreen(Resources, Resources.Version);
        Palette = new GfxPalette16(Resources, Screen, Game, Resources.Version);
        Ports = new GfxPorts(Screen) { IsDemo = Description.IsDemo };
        Cache = new GfxCache(Resources, Screen, Palette, Resources.Version, Description.Gid, Description.IsDemo, Host.Log);
        Paint = new GfxPaint16(Resources, Ports, Screen, Palette, Cache, Resources.Version, Description.Gid);

        Ports.Init(UsesOldGfxFunctions, Resources.Version, Description.Gid);
        Palette.SetDefault();

        Host.Graphics.SetMode(Screen.DisplayWidth, Screen.DisplayHeight);
        Host.Graphics.SetTitle($"Daventry - {Detected.Title}");
        PushPaletteIfChanged();

        if (!DebugPicture.HasValue && !DebugView.HasValue)
        {
            throw new NotSupportedException($"{Detected.Title}: the SCI interpreter is not ported yet. Use --picture or --view to browse the game's graphics.");
        }

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

    public override TickResult Tick(TimeSpan elapsed)
    {
        while (Host.Input.TryRead(out var inputEvent))
        {
            if (inputEvent.Type == InputEventType.Quit)
            {
                return TickResult.Quit;
            }

            HandleViewerKey(inputEvent);
        }

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
            Host.Graphics.Present(Screen.Display);
            Screen.DisplayDirty = false;
        }

        return TickResult.After(FrameInterval);
    }

    private void PushPaletteIfChanged()
    {
        if (Screen.PaletteVersion != _presentedPaletteVersion)
        {
            Host.Graphics.SetPalette(Screen.HostPalette);
            _presentedPaletteVersion = Screen.PaletteVersion;
        }
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
