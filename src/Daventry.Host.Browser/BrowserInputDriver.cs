using System.Runtime.Versioning;
using Daventry.Core.Hosting;
using Daventry.Core.Input;

namespace Daventry.Host.Browser;

/// <summary>
/// Queues the canvas's keyboard and mouse events for the engine. The JavaScript side calls
/// <see cref="OnKey"/> and <see cref="OnMouse"/>; the host's own keys (Alt+Enter and F11 for
/// full screen, Ctrl+Alt+Up, Down and M for the volume) are handled here and never reach the game,
/// as in the SDL host.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class BrowserInputDriver(CanvasGraphicsDriver graphics, WebAudioDriver audio) : IInputDriver
{
    private readonly Queue<InputEvent> _queue = new();
    private MouseState _mouse;
    private int _volumeBeforeMute;

    public bool TryRead(out InputEvent inputEvent) => _queue.TryDequeue(out inputEvent);

    public MouseState GetMouseState() => _mouse;

    /// <summary>A key event: <paramref name="keyAndCode"/> is the browser's key and code, newline separated.</summary>
    public void OnKey(bool down, string keyAndCode, int flags)
    {
        int newline = keyAndCode.IndexOf('\n');
        var key = newline < 0 ? keyAndCode : keyAndCode[..newline];
        var code = newline < 0 ? string.Empty : keyAndCode[(newline + 1)..];
        var modifierFlags = (KeyTranslator.Flags)flags;
        var modifiers = KeyTranslator.ToModifiers(modifierFlags);
        bool repeat = (modifierFlags & KeyTranslator.Flags.Repeat) != 0;
        var (translated, ascii) = KeyTranslator.Translate(key, code);

        bool altEnter = translated == Key.Return && (modifiers & KeyModifiers.Alt) != 0;
        if (altEnter || translated == Key.F11)
        {
            if (down && !repeat)
            {
                graphics.ToggleFullscreen();
            }

            return;
        }

        if ((modifiers & (KeyModifiers.Ctrl | KeyModifiers.Alt)) == (KeyModifiers.Ctrl | KeyModifiers.Alt)
            && translated is Key.Up or Key.Down or Key.M)
        {
            if (down && (!repeat || translated != Key.M))
            {
                AdjustVolume(translated);
            }

            return;
        }

        if (translated == Key.None && ascii == '\0')
        {
            return;
        }

        _queue.Enqueue(down ? InputEvent.KeyDown(translated, modifiers, ascii, repeat) : InputEvent.KeyUp(translated, modifiers, ascii));
    }

    /// <summary>A mouse event: kind 0 is a move, then down and up for the left, middle and right buttons (1 to 6).</summary>
    public void OnMouse(int kind, int x, int y)
    {
        if (kind == 0)
        {
            _mouse = _mouse with { X = x, Y = y };
            _queue.Enqueue(InputEvent.MouseMove(x, y));
            return;
        }

        bool down = kind % 2 == 1;
        var button = ((kind - 1) / 2) switch
        {
            0 => MouseButton.Left,
            1 => MouseButton.Middle,
            2 => MouseButton.Right,
            _ => MouseButton.None,
        };
        if (button == MouseButton.None)
        {
            return;
        }

        _mouse = button switch
        {
            MouseButton.Left => _mouse with { X = x, Y = y, LeftDown = down },
            MouseButton.Right => _mouse with { X = x, Y = y, RightDown = down },
            _ => _mouse with { X = x, Y = y, MiddleDown = down },
        };
        _queue.Enqueue(down ? InputEvent.MouseDown(button, x, y) : InputEvent.MouseUp(button, x, y));
    }

    private void AdjustVolume(Key key)
    {
        const int Step = AudioVolume.Unity / 16;
        if (key == Key.M)
        {
            if (audio.Volume > 0)
            {
                _volumeBeforeMute = audio.Volume;
                audio.Volume = 0;
            }
            else
            {
                audio.Volume = _volumeBeforeMute > 0 ? _volumeBeforeMute : AudioVolume.Default;
            }
        }
        else
        {
            audio.Volume = AudioVolume.Clamp(audio.Volume + (key == Key.Up ? Step : -Step));
        }

        graphics.ShowStatus(audio.Volume == 0 ? "muted" : $"volume {AudioVolume.ToPercent(audio.Volume)}%");
    }
}
