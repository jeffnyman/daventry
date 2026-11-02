using Daventry.Core.Graphics;
using Daventry.Core.Hosting;
using Daventry.Core.Input;
using Daventry.Core.Storage;

namespace Daventry.Core.Tests;

public class HeadlessHostTests
{
    /// <summary>An engine that records, tick by tick, the events it read and where the mouse was.</summary>
    private sealed class RecordingEngine(IHost host) : Core.Hosting.Engine(host, new MemoryGameContainer("test"), new DetectedGame("test", "test", "Test"))
    {
        public List<(int Tick, InputEvent Event, MouseState Mouse)> Read { get; } = [];

        public int Ticks { get; private set; }

        public override void Initialize()
        {
        }

        public override TickResult Tick(TimeSpan elapsed)
        {
            while (Host.Input.TryRead(out var e))
            {
                Read.Add((Ticks, e, Host.Input.GetMouseState()));
            }

            Ticks++;
            return TickResult.After(TimeSpan.FromMilliseconds(16));
        }
    }

    [Fact]
    public void ClickMovesThenPressesThenReleasesInLaterTicks()
    {
        var host = new HeadlessHost();
        var engine = new RecordingEngine(host);

        Assert.True(host.Click(engine, 245, 190, holdTicks: 3));

        Assert.Equal(9, engine.Ticks);
        Assert.Collection(
            engine.Read,
            r => Assert.Equal((0, InputEventType.MouseMove, false), (r.Tick, r.Event.Type, r.Mouse.LeftDown)),
            r => Assert.Equal((3, InputEventType.MouseDown, true), (r.Tick, r.Event.Type, r.Mouse.LeftDown)),
            r => Assert.Equal((6, InputEventType.MouseUp, false), (r.Tick, r.Event.Type, r.Mouse.LeftDown)));
        Assert.All(engine.Read, r => Assert.Equal((245, 190), (r.Event.X, r.Event.Y)));
    }

    [Fact]
    public void TypeFillsInKeysModifiersAndCharacters()
    {
        var host = new HeadlessHost();
        host.Input.Type("Go!\r");

        var downs = new List<InputEvent>();
        while (host.Input.TryRead(out var e))
        {
            if (e.Type == InputEventType.KeyDown)
            {
                downs.Add(e);
            }
        }

        Assert.Equal(
            [(Key.G, KeyModifiers.Shift, 'G'), (Key.O, KeyModifiers.None, 'o'), (Key.D1, KeyModifiers.Shift, '!'), (Key.Return, KeyModifiers.None, '\r')],
            downs.Select(e => (e.Key, e.Modifiers, e.Ascii)));
    }

    [Theory]
    [InlineData('a', Key.A, KeyModifiers.None)]
    [InlineData('Z', Key.Z, KeyModifiers.Shift)]
    [InlineData('7', Key.D7, KeyModifiers.None)]
    [InlineData(' ', Key.Space, KeyModifiers.None)]
    [InlineData('?', Key.Slash, KeyModifiers.Shift)]
    [InlineData('"', Key.Quote, KeyModifiers.Shift)]
    [InlineData('\n', Key.Return, KeyModifiers.None)]
    public void KeyTextGivesTheKeyACharacterIsTypedWith(char c, Key key, KeyModifiers modifiers)
    {
        var (k, m, _) = KeyText.FromChar(c);
        Assert.Equal((key, modifiers), (k, m));
    }

    [Fact]
    public void KeyTextIgnoresCharactersOutsideAscii()
        => Assert.Equal((Key.None, KeyModifiers.None, '\0'), KeyText.FromChar('é'));

    [Fact]
    public void CaptureScreenShowsWhatWasPresentedLast()
    {
        var graphics = new HeadlessGraphics();
        graphics.SetMode(2, 1);
        Assert.False(graphics.HasFrame);
        Assert.Throws<InvalidOperationException>(graphics.CaptureScreen);

        graphics.SetPalette([new Rgb(10, 20, 30), new Rgb(40, 50, 60)]);
        var indexed = new Surface8(2, 1);
        indexed[1, 0] = 1;
        graphics.Present(indexed);
        Assert.True(graphics.HasFrame);
        Assert.Equal([Surface32.Pack(10, 20, 30), Surface32.Pack(40, 50, 60)], graphics.CaptureScreen().Pixels);

        var trueColor = new Surface32(2, 1);
        trueColor[0, 0] = 0x123456;
        graphics.Present(trueColor);
        Assert.Equal([0x123456u, 0u], graphics.CaptureScreen().Pixels);
    }
}
