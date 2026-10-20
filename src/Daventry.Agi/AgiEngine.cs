using Daventry.Core.Graphics;
using Daventry.Core.Hosting;
using Daventry.Core.Input;
using Daventry.Core.Storage;

namespace Daventry.Agi;

/// <summary>
/// The AGI interpreter. Currently a placeholder that proves the host pipeline: it sets the AGI
/// display mode, loads the EGA palette, draws colour bars and exits on Escape or window close.
/// </summary>
public sealed class AgiEngine : Engine
{
    public const int ScreenWidth = 320;
    public const int ScreenHeight = 200;

    /// <summary>Standard 16-colour EGA palette as used by ScummVM's AGI engine (PALETTE_EGA).</summary>
    public static readonly Rgb[] EgaPalette =
    [
        new(0x00, 0x00, 0x00), new(0x00, 0x00, 0xAA), new(0x00, 0xAA, 0x00), new(0x00, 0xAA, 0xAA),
        new(0xAA, 0x00, 0x00), new(0xAA, 0x00, 0xAA), new(0xAA, 0x55, 0x00), new(0xAA, 0xAA, 0xAA),
        new(0x55, 0x55, 0x55), new(0x55, 0x55, 0xFF), new(0x55, 0xFF, 0x55), new(0x55, 0xFF, 0xFF),
        new(0xFF, 0x55, 0x55), new(0xFF, 0x55, 0xFF), new(0xFF, 0xFF, 0x55), new(0xFF, 0xFF, 0xFF),
    ];

    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(1000.0 / 60);

    private readonly Surface8 _frame = new(ScreenWidth, ScreenHeight);
    private TimeSpan _accumulated;
    private int _ticks;

    public AgiEngine(IHost host, IGameContainer game, DetectedGame detected)
        : base(host, game, detected)
    {
    }

    public override void Initialize()
    {
        Host.Graphics.SetMode(ScreenWidth, ScreenHeight);
        Host.Graphics.SetPalette(EgaPalette);
        Host.Graphics.SetTitle($"Daventry - {Detected.Title}");
        Host.Log.Info("agi", $"Initialised for {Detected}. Files: {Game.EnumerateFiles().Count()}");
    }

    public override TickResult Tick(TimeSpan elapsed)
    {
        while (Host.Input.TryRead(out var inputEvent))
        {
            switch (inputEvent.Type)
            {
                case InputEventType.Quit:
                case InputEventType.KeyDown when inputEvent.Key == Key.Escape:
                    return TickResult.Quit;
            }
        }

        _accumulated += elapsed;
        if (_ticks > 0 && _accumulated < FrameInterval)
        {
            return TickResult.After(FrameInterval - _accumulated);
        }

        _accumulated = TimeSpan.Zero;
        _ticks++;

        // Placeholder render: sixteen EGA colour bars, cycling once per second.
        int barWidth = ScreenWidth / EgaPalette.Length;
        int shift = (_ticks / 60) % EgaPalette.Length;
        for (int i = 0; i < EgaPalette.Length; i++)
        {
            _frame.FillRect(new Rect(i * barWidth, 0, barWidth, ScreenHeight), (byte)((i + shift) % EgaPalette.Length));
        }

        Host.Graphics.Present(_frame);
        return TickResult.After(FrameInterval);
    }
}
