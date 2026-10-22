using Daventry.Core.Hosting;
using Daventry.Sci.Engine;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Graphics;

/// <summary>
/// The SCI32 graphics subsystems and what they share: the frameout with its planes, the
/// palette, the colour remapper, the cursor, the text renderer and the transitions. ScummVM
/// reaches these through globals; here every part holds this context.
/// </summary>
public sealed class Sci32Graphics
{
    public Sci32Graphics(EngineState state, ILog log)
    {
        State = state;
        Log = log;
        CelCache = new CelCache(this);
    }

    public EngineState State { get; }

    public ILog Log { get; }

    public SegManager SegMan => State.SegMan;

    public ResourceManager ResMan => State.ResMan;

    public GameFeatures Features => State.Features;

    public SciVersion Version => State.Version;

    public SciGameId GameId => State.GameId;

    public SciPlatform Platform => State.Platform;

    public bool IsMacintosh => State.Platform == SciPlatform.Macintosh;

    public GfxFrameout Frameout { get; set; } = null!;

    public GfxPalette32 Palette { get; set; } = null!;

    public GfxRemap32 Remap { get; set; } = null!;

    public GfxCursor32 Cursor { get; set; } = null!;

    public GfxTransitions32 Transitions { get; set; } = null!;

    public CelCache CelCache { get; }

    public GfxText32 Text { get; set; } = null!;

    /// <summary>The video players; null until the graphics are set up.</summary>
    public Video32? Video { get; set; }

    public GfxPaint32 Paint { get; set; } = null!;

    public GfxControls32 Controls { get; set; } = null!;

    private readonly Dictionary<int, GfxFontFromResource?> _fonts = [];

    /// <summary>A font resource, cached; null when it does not exist. The fonts did not change from SCI16.</summary>
    public GfxFontFromResource? GetFont(int fontId)
    {
        if (!_fonts.TryGetValue(fontId, out var font))
        {
            if (fontId == GfxText32.SystemFont)
            {
                font = new GfxFontFromResource(Sci32SystemFont.Data, null, fontId, Log);
            }
            else
            {
                font = ResMan.TestResource(new ResourceId(ResourceType.Font, (ushort)fontId)) is null
                    ? null
                    : new GfxFontFromResource(ResMan, null, fontId, Log);
            }

            _fonts[fontId] = font;
        }

        return font;
    }

    /// <summary>The game's clock in ticks of a sixtieth of a second, as kGetTime counts it.</summary>
    public uint TickCount => (uint)(State.Millis() * 60 / 1000);
}
