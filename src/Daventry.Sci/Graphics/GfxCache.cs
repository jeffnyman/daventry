using Daventry.Core.Hosting;
using Daventry.Sci.Resources;

namespace Daventry.Sci.Graphics;

/// <summary>Keeps decoded fonts and views so they are parsed once. Port of ScummVM's GfxCache.</summary>
public sealed class GfxCache
{
    private readonly ResourceManager _resMan;
    private readonly GfxScreen _screen;
    private readonly GfxPalette16 _palette;
    private readonly SciVersion _version;
    private readonly SciGameId _gameId;
    private readonly bool _isDemo;
    private readonly ILog _log;
    private readonly Dictionary<int, GfxFontFromResource> _fonts = [];
    private readonly Dictionary<int, GfxView> _views = [];

    public GfxCache(ResourceManager resMan, GfxScreen screen, GfxPalette16 palette, SciVersion version, SciGameId gameId, bool isDemo, ILog? log = null)
    {
        _resMan = resMan;
        _screen = screen;
        _palette = palette;
        _version = version;
        _gameId = gameId;
        _isDemo = isDemo;
        _log = log ?? NullLog.Instance;
    }

    public void Purge()
    {
        _fonts.Clear();
        _views.Clear();
    }

    /// <summary>The font, or null when the resource does not exist.</summary>
    public GfxFontFromResource? GetFont(int fontId)
    {
        if (_fonts.TryGetValue(fontId, out var font))
        {
            return font;
        }

        if (_resMan.TestResource(new ResourceId(ResourceType.Font, (ushort)fontId)) is null
            && _resMan.TestResource(new ResourceId(ResourceType.Font, (ushort)(fontId & 0x7FF))) is null)
        {
            return null;
        }

        font = new GfxFontFromResource(_resMan, _screen, fontId, _log);
        _fonts[fontId] = font;
        return font;
    }

    /// <summary>The view, or null when the resource does not exist.</summary>
    public GfxView? GetView(int viewId)
    {
        if (_views.TryGetValue(viewId, out var view))
        {
            return view;
        }

        if (_resMan.TestResource(new ResourceId(ResourceType.View, (ushort)viewId)) is null)
        {
            return null;
        }

        view = new GfxView(_resMan, _screen, _palette, viewId, _version, _gameId, _isDemo);
        _views[viewId] = view;
        return view;
    }

    public int KernelViewGetCelWidth(int viewId, int loopNo, int celNo) => GetView(viewId)?.GetWidth(loopNo, celNo) ?? 0;

    public int KernelViewGetCelHeight(int viewId, int loopNo, int celNo) => GetView(viewId)?.GetHeight(loopNo, celNo) ?? 0;

    public int KernelViewGetLoopCount(int viewId) => GetView(viewId)?.LoopCount ?? 0;

    public int KernelViewGetCelCount(int viewId, int loopNo) => GetView(viewId)?.GetCelCount(loopNo) ?? 0;
}
