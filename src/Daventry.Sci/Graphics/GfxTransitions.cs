using Daventry.Core.Hosting;

namespace Daventry.Sci.Graphics;

/// <summary>
/// Screen transitions shown by kAnimate when a new picture is drawn. Port of ScummVM's
/// transitions.cpp. The new picture is already in the back buffer; a transition copies it to
/// the presented screen piece by piece, sleeping between frames so the host can show them.
/// </summary>
public sealed class GfxTransitions
{
    public const int VerticalRollFromCenter = 0;
    public const int HorizontalRollFromCenter = 1;
    public const int StraightFromRight = 2;
    public const int StraightFromLeft = 3;
    public const int StraightFromBottom = 4;
    public const int StraightFromTop = 5;
    public const int DiagonalRollToCenter = 6;
    public const int DiagonalRollFromCenter = 7;
    public const int Blocks = 8;
    public const int Pixelation = 9;
    public const int FadePalette = 10;
    public const int ScrollRight = 11;
    public const int ScrollLeft = 12;
    public const int ScrollUp = 13;
    public const int ScrollDown = 14;
    public const int NoneLongbow = 15;
    public const int None = 100;

    // transitions of the old table set that the new one no longer has
    public const int VerticalRollToCenter = 300;
    public const int HorizontalRollToCenter = 301;

    private readonly record struct TranslateEntry(int OrgId, int NewId, bool BlackoutFlag);

    /// <summary>Maps the ids used before SCI1 late to the new ids.</summary>
    private static readonly TranslateEntry[] OldTransitionIds =
    [
        new(0, VerticalRollFromCenter, false),
        new(1, HorizontalRollFromCenter, false),
        new(2, StraightFromRight, false),
        new(3, StraightFromLeft, false),
        new(4, StraightFromBottom, false),
        new(5, StraightFromTop, false),
        new(6, DiagonalRollToCenter, false),
        new(7, DiagonalRollFromCenter, false),
        new(8, Blocks, false),
        new(9, VerticalRollToCenter, false),
        new(10, HorizontalRollToCenter, false),
        new(11, StraightFromRight, true),
        new(12, StraightFromLeft, true),
        new(13, StraightFromBottom, true),
        new(14, StraightFromTop, true),
        new(15, DiagonalRollToCenter, true),
        new(16, DiagonalRollFromCenter, true),
        new(17, Blocks, true),
        new(18, Pixelation, false),
        new(27, Pixelation, true),
        new(30, FadePalette, false),
        new(40, ScrollRight, false),
        new(41, ScrollLeft, false),
        new(42, ScrollUp, false),
        new(43, ScrollDown, false),
        new(100, None, false),
    ];

    /// <summary>The blackout transition that runs before the actual one.</summary>
    private static readonly TranslateEntry[] BlackoutTransitionIds =
    [
        new(VerticalRollFromCenter, VerticalRollToCenter, true),
        new(HorizontalRollFromCenter, HorizontalRollToCenter, true),
        new(StraightFromRight, StraightFromLeft, true),
        new(StraightFromLeft, StraightFromRight, true),
        new(StraightFromBottom, StraightFromTop, true),
        new(StraightFromTop, StraightFromBottom, true),
        new(DiagonalRollFromCenter, DiagonalRollToCenter, true),
        new(DiagonalRollToCenter, DiagonalRollFromCenter, true),
        new(Blocks, Blocks, true),
        new(Pixelation, Pixelation, true),
        new(FadePalette, None, true),
        new(ScrollRight, None, true),
        new(ScrollLeft, None, true),
        new(ScrollUp, None, true),
        new(ScrollDown, None, true),
        new(NoneLongbow, None, true),
        new(None, None, true),
        new(VerticalRollToCenter, None, true),
        new(HorizontalRollToCenter, None, true),
    ];

    private readonly GfxScreen _screen;
    private readonly GfxPalette16 _palette;
    private readonly SciVersion _version;
    private readonly Func<uint> _millis;
    private readonly Func<int, ValueTask> _sleepMs;
    private readonly ILog _log;
    private readonly TranslateEntry[]? _translationTable;
    private int _number;
    private bool _blackoutFlag;
    private SciRect _picRect;
    private uint _transitionStartTime;

    public GfxTransitions(GfxScreen screen, GfxPalette16 palette, SciVersion version, Func<uint> millis, Func<int, ValueTask> sleepMs, ILog log)
    {
        _screen = screen;
        _palette = palette;
        _version = version;
        _millis = millis;
        _sleepMs = sleepMs;
        _log = log;
        _translationTable = version >= SciVersion.V1Late ? null : OldTransitionIds;
        _number = HorizontalRollFromCenter;
    }

    /// <summary>Remembers the transition kDrawPic asked for; -1 keeps the previous one.</summary>
    public void Setup(int number, bool blackoutFlag)
    {
        if (number != -1)
        {
            _number = number;
            _blackoutFlag = blackoutFlag;
            _log.Debug("sci.gfx", $"Transition {number}, blackout {blackoutFlag}");
        }
    }

    /// <summary>
    /// True when the frame for <paramref name="shouldBeAtMsec"/> is still due. Frames are dropped
    /// when the host cannot keep up, so transitions take the time they should.
    /// </summary>
    private bool DoCreateFrame(uint shouldBeAtMsec) => shouldBeAtMsec > _millis() - _transitionStartTime;

    private void UpdateScreen() => _screen.DisplayDirty = true;

    private async ValueTask UpdateScreenAndWait(uint shouldBeAtMsec)
    {
        UpdateScreen();
        uint msecPos = _millis() - _transitionStartTime;
        if (shouldBeAtMsec > msecPos)
        {
            await _sleepMs((int)(shouldBeAtMsec - msecPos));
        }
    }

    private static TranslateEntry? TranslateNumber(int number, TranslateEntry[] table)
    {
        foreach (var entry in table)
        {
            if (entry.OrgId == number)
            {
                return entry;
            }
        }

        return null;
    }

    public async ValueTask Doit(SciRect picRect)
    {
        _picRect = picRect;

        if (_translationTable is not null)
        {
            var translationEntry = TranslateNumber(_number, _translationTable);
            if (translationEntry is { } entry)
            {
                _number = entry.NewId;
                _blackoutFlag = entry.BlackoutFlag;
            }
            else
            {
                _log.Warning("sci.gfx", $"Transitions: old ID {_number} not supported");
                _number = None;
                _blackoutFlag = false;
            }
        }

        if (_blackoutFlag)
        {
            var translationEntry = TranslateNumber(_number, BlackoutTransitionIds);
            if (translationEntry is { } entry)
            {
                await DoTransition(entry.NewId, true);
            }
            else
            {
                _log.Warning("sci.gfx", $"Transitions: ID {_number} not listed in blackoutTransitionIDs");
            }
        }

        _palette.PalVaryPrepareForTransition();

        // the actual transition to the new screen
        await DoTransition(_number, false);

        _screen.PicNotValid = 0;
    }

    /// <summary>Runs once for the blackout and once for the real transition.</summary>
    private async ValueTask DoTransition(int number, bool blackoutFlag)
    {
        if (number != FadePalette)
        {
            SetNewPalette(blackoutFlag);
        }

        _transitionStartTime = _millis();
        switch (number)
        {
            case VerticalRollFromCenter:
                await VerticalRollFromCenterAsync(blackoutFlag);
                break;
            case VerticalRollToCenter:
                await VerticalRollToCenterAsync(blackoutFlag);
                break;
            case HorizontalRollFromCenter:
                await HorizontalRollFromCenterAsync(blackoutFlag);
                break;
            case HorizontalRollToCenter:
                await HorizontalRollToCenterAsync(blackoutFlag);
                break;
            case DiagonalRollToCenter:
                await DiagonalRollToCenterAsync(blackoutFlag);
                break;
            case DiagonalRollFromCenter:
                await DiagonalRollFromCenterAsync(blackoutFlag);
                break;
            case StraightFromRight:
            case StraightFromLeft:
            case StraightFromBottom:
            case StraightFromTop:
                await Straight(number, blackoutFlag);
                break;
            case Pixelation:
                await PixelationAsync(blackoutFlag);
                break;
            case Blocks:
                await BlocksAsync(blackoutFlag);
                break;
            case FadePalette:
                if (!blackoutFlag)
                {
                    await FadeOut();
                    SetNewScreen(blackoutFlag);
                    await FadeIn();
                }

                break;
            case ScrollRight:
            case ScrollLeft:
            case ScrollUp:
            case ScrollDown:
                await Scroll(number);
                break;
            case NoneLongbow:
            case None:
                SetNewScreen(blackoutFlag);
                break;
            default:
                _log.Warning("sci.gfx", $"Transitions: ID {number} not implemented");
                SetNewScreen(blackoutFlag);
                break;
        }

        // make sure the final frame is shown even if the last update was skipped for timing
        UpdateScreen();
        _log.Debug("sci.gfx", $"Transition took {_millis() - _transitionStartTime} milliseconds");
    }

    private void SetNewPalette(bool blackoutFlag)
    {
        if (!blackoutFlag)
        {
            _palette.SetOnScreen(false);
        }
    }

    private void SetNewScreen(bool blackoutFlag)
    {
        if (!blackoutFlag)
        {
            _screen.CopyRectToScreen(_picRect);
        }
    }

    private void CopyRectToScreen(SciRect rect, bool blackoutFlag)
    {
        if (!blackoutFlag)
        {
            _screen.CopyRectToScreen(rect);
        }
        else
        {
            _screen.ClearRect(rect);
        }
    }

    /// <summary>Fades the presented palette to black in ten steps; few steps, so the CPU keeps up.</summary>
    private async ValueTask FadeOut()
    {
        var oldPalette = new byte[3 * 256];
        var workPalette = new byte[3 * 256];
        // Sierra did not fade colour 255 for SCI1.1 but used it in several pictures (QfG3 demo
        // and intro), which made the fade look odd
        int tillColorNr = _version >= SciVersion.V1_1 ? 255 : 254;

        _screen.GrabPalette(oldPalette, 0, 256);

        for (int stepNr = 100; stepNr >= 0; stepNr -= 10)
        {
            for (int colorNr = 1; colorNr <= tillColorNr; colorNr++)
            {
                workPalette[colorNr * 3] = (byte)(oldPalette[colorNr * 3] * stepNr / 100);
                workPalette[colorNr * 3 + 1] = (byte)(oldPalette[colorNr * 3 + 1] * stepNr / 100);
                workPalette[colorNr * 3 + 2] = (byte)(oldPalette[colorNr * 3 + 2] * stepNr / 100);
            }

            _screen.SetPalette(workPalette, 1, tillColorNr);
            await _sleepMs(2 * 1000 / 60);
        }
    }

    private async ValueTask FadeIn()
    {
        int tillColorNr = _version >= SciVersion.V1_1 ? 255 : 254;
        for (int stepNr = 0; stepNr <= 100; stepNr += 10)
        {
            _palette.KernelSetIntensity(1, tillColorNr + 1, stepNr, true);
            await _sleepMs(2 * 1000 / 60);
        }
    }

    /// <summary>Pixelates the new picture over the old one; works against the whole screen.</summary>
    private async ValueTask PixelationAsync(bool blackoutFlag)
    {
        int mask = 0x40;
        int stepNr = 0;
        uint msecCount = 0;
        int scriptWidth = _screen.ScriptWidth;
        int scriptPixels = scriptWidth * _screen.ScriptHeight;

        do
        {
            mask = (mask & 1) != 0 ? (mask >> 1) ^ 0xB400 : mask >> 1;
            if (mask >= scriptPixels)
            {
                continue;
            }

            var pixelRect = new SciRect(mask % scriptWidth, mask / scriptWidth, mask % scriptWidth + 1, mask / scriptWidth + 1);
            pixelRect.Clip(_picRect);
            if (!pixelRect.IsEmpty)
            {
                CopyRectToScreen(pixelRect, blackoutFlag);
            }

            if ((stepNr & 0x3FF) == 0)
            {
                msecCount += 9;
                if (DoCreateFrame(msecCount))
                {
                    await UpdateScreenAndWait(msecCount);
                }
            }

            stepNr++;
        }
        while (mask != 0x40);
    }

    /// <summary>Like pixelation but with 8x8 blocks; works against the whole screen.</summary>
    private async ValueTask BlocksAsync(bool blackoutFlag)
    {
        int mask = 0x40;
        int stepNr = 0;
        uint msecCount = 0;

        do
        {
            mask = (mask & 1) != 0 ? (mask >> 1) ^ 0x240 : mask >> 1;
            if (mask >= 40 * 25)
            {
                continue;
            }

            var blockRect = new SciRect((mask % 40) << 3, (mask / 40) << 3, ((mask % 40) << 3) + 8, ((mask / 40) << 3) + 8);
            blockRect.Clip(_picRect);
            if (!blockRect.IsEmpty)
            {
                CopyRectToScreen(blockRect, blackoutFlag);
            }

            if ((stepNr & 7) == 0)
            {
                msecCount += 5;
                if (DoCreateFrame(msecCount))
                {
                    await UpdateScreenAndWait(msecCount);
                }
            }

            stepNr++;
        }
        while (mask != 0x40);
    }

    /// <summary>Shows the new screen line by line from one edge to the opposite one.</summary>
    private async ValueTask Straight(int number, bool blackoutFlag)
    {
        int stepNr = 0;
        var newScreenRect = _picRect;
        uint msecCount = 0;

        switch (number)
        {
            case StraightFromRight:
                newScreenRect.Left = newScreenRect.Right - 1;
                while (newScreenRect.Left >= _picRect.Left)
                {
                    CopyRectToScreen(newScreenRect, blackoutFlag);
                    if ((stepNr & 1) == 0)
                    {
                        msecCount += 2;
                        if (DoCreateFrame(msecCount))
                        {
                            await UpdateScreenAndWait(msecCount);
                        }
                    }

                    stepNr++;
                    newScreenRect.Translate(-1, 0);
                }

                break;

            case StraightFromLeft:
                newScreenRect.Right = newScreenRect.Left + 1;
                while (newScreenRect.Right <= _picRect.Right)
                {
                    CopyRectToScreen(newScreenRect, blackoutFlag);
                    if ((stepNr & 1) == 0)
                    {
                        msecCount += 2;
                        if (DoCreateFrame(msecCount))
                        {
                            await UpdateScreenAndWait(msecCount);
                        }
                    }

                    stepNr++;
                    newScreenRect.Translate(1, 0);
                }

                break;

            case StraightFromBottom:
                newScreenRect.Top = newScreenRect.Bottom - 1;
                while (newScreenRect.Top >= _picRect.Top)
                {
                    CopyRectToScreen(newScreenRect, blackoutFlag);
                    msecCount += 4;
                    if (DoCreateFrame(msecCount))
                    {
                        await UpdateScreenAndWait(msecCount);
                    }

                    stepNr++;
                    newScreenRect.Translate(0, -1);
                }

                break;

            case StraightFromTop:
                newScreenRect.Bottom = newScreenRect.Top + 1;
                while (newScreenRect.Bottom <= _picRect.Bottom)
                {
                    CopyRectToScreen(newScreenRect, blackoutFlag);
                    msecCount += 4;
                    if (DoCreateFrame(msecCount))
                    {
                        await UpdateScreenAndWait(msecCount);
                    }

                    stepNr++;
                    newScreenRect.Translate(0, 1);
                }

                break;
        }
    }

    /// <summary>Scrolls the old screen out and the new one in; works on the picture area only.</summary>
    private async ValueTask Scroll(int number)
    {
        int stepNr = 0;
        var oldMoveRect = _picRect;
        var oldScreenRect = _picRect;
        var newMoveRect = _picRect;
        var newScreenRect = _picRect;
        uint msecCount = 0;

        _screen.BakCreateBackup();

        switch (number)
        {
            case ScrollLeft:
                newScreenRect.Right = newScreenRect.Left;
                newMoveRect.Left = newMoveRect.Right;
                while (oldMoveRect.Left < oldMoveRect.Right)
                {
                    oldMoveRect.Right--;
                    oldScreenRect.Left++;
                    newScreenRect.Right++;
                    newMoveRect.Left--;
                    if ((stepNr & 1) == 0)
                    {
                        msecCount += 5;
                        if (DoCreateFrame(msecCount))
                        {
                            if (oldMoveRect.Right > oldMoveRect.Left)
                            {
                                _screen.BakCopyRectToScreen(oldScreenRect, oldMoveRect.Left, oldMoveRect.Top);
                            }

                            _screen.CopyRectToScreen(newScreenRect, newMoveRect.Left, newMoveRect.Top);
                            await UpdateScreenAndWait(msecCount);
                        }
                    }

                    stepNr++;
                }

                break;

            case ScrollRight:
                newScreenRect.Left = newScreenRect.Right;
                while (oldMoveRect.Left < oldMoveRect.Right)
                {
                    oldMoveRect.Left++;
                    oldScreenRect.Right--;
                    newScreenRect.Left--;
                    if ((stepNr & 1) == 0)
                    {
                        msecCount += 5;
                        if (DoCreateFrame(msecCount))
                        {
                            if (oldMoveRect.Right > oldMoveRect.Left)
                            {
                                _screen.BakCopyRectToScreen(oldScreenRect, oldMoveRect.Left, oldMoveRect.Top);
                            }

                            _screen.CopyRectToScreen(newScreenRect, newMoveRect.Left, newMoveRect.Top);
                            await UpdateScreenAndWait(msecCount);
                        }
                    }

                    stepNr++;
                }

                break;

            case ScrollUp:
                newScreenRect.Bottom = newScreenRect.Top;
                newMoveRect.Top = newMoveRect.Bottom;
                while (oldMoveRect.Top < oldMoveRect.Bottom)
                {
                    oldMoveRect.Top++;
                    oldScreenRect.Top++;
                    newScreenRect.Bottom++;
                    newMoveRect.Top--;

                    msecCount += 5;
                    if (DoCreateFrame(msecCount))
                    {
                        if (oldMoveRect.Top < oldMoveRect.Bottom)
                        {
                            _screen.BakCopyRectToScreen(oldScreenRect, _picRect.Left, _picRect.Top);
                        }

                        _screen.CopyRectToScreen(newScreenRect, newMoveRect.Left, newMoveRect.Top);
                        await UpdateScreenAndWait(msecCount);
                    }
                }

                break;

            case ScrollDown:
                newScreenRect.Top = newScreenRect.Bottom;
                while (oldMoveRect.Top < oldMoveRect.Bottom)
                {
                    oldMoveRect.Top++;
                    oldScreenRect.Bottom--;
                    newScreenRect.Top--;

                    msecCount += 5;
                    if (DoCreateFrame(msecCount))
                    {
                        if (oldMoveRect.Top < oldMoveRect.Bottom)
                        {
                            _screen.BakCopyRectToScreen(oldScreenRect, oldMoveRect.Left, oldMoveRect.Top);
                        }

                        _screen.CopyRectToScreen(newScreenRect, _picRect.Left, _picRect.Top);
                        await UpdateScreenAndWait(msecCount);
                    }
                }

                break;
        }

        _screen.BakDiscard();

        // copy over the final position just in case
        _screen.CopyRectToScreen(newScreenRect);
    }

    private async ValueTask VerticalRollFromCenterAsync(bool blackoutFlag)
    {
        var leftRect = new SciRect(_picRect.Left + (_picRect.Width / 2) - 1, _picRect.Top, _picRect.Left + (_picRect.Width / 2), _picRect.Bottom);
        var rightRect = new SciRect(leftRect.Right, _picRect.Top, leftRect.Right + 1, _picRect.Bottom);
        uint msecCount = 0;

        while (leftRect.Left >= _picRect.Left || rightRect.Right <= _picRect.Right)
        {
            if (leftRect.Left < _picRect.Left)
            {
                leftRect.Translate(1, 0);
            }

            if (rightRect.Right > _picRect.Right)
            {
                rightRect.Translate(-1, 0);
            }

            CopyRectToScreen(leftRect, blackoutFlag);
            leftRect.Translate(-1, 0);
            CopyRectToScreen(rightRect, blackoutFlag);
            rightRect.Translate(1, 0);
            msecCount += 3;
            if (DoCreateFrame(msecCount))
            {
                await UpdateScreenAndWait(msecCount);
            }
        }
    }

    private async ValueTask VerticalRollToCenterAsync(bool blackoutFlag)
    {
        var leftRect = new SciRect(_picRect.Left, _picRect.Top, _picRect.Left + 1, _picRect.Bottom);
        var rightRect = new SciRect(_picRect.Right - 1, _picRect.Top, _picRect.Right, _picRect.Bottom);
        uint msecCount = 0;

        while (leftRect.Left < rightRect.Right)
        {
            CopyRectToScreen(leftRect, blackoutFlag);
            leftRect.Translate(1, 0);
            CopyRectToScreen(rightRect, blackoutFlag);
            rightRect.Translate(-1, 0);
            msecCount += 3;
            if (DoCreateFrame(msecCount))
            {
                await UpdateScreenAndWait(msecCount);
            }
        }
    }

    private async ValueTask HorizontalRollFromCenterAsync(bool blackoutFlag)
    {
        var upperRect = new SciRect(_picRect.Left, _picRect.Top + (_picRect.Height / 2) - 1, _picRect.Right, _picRect.Top + (_picRect.Height / 2));
        var lowerRect = new SciRect(upperRect.Left, upperRect.Bottom, upperRect.Right, upperRect.Bottom + 1);
        uint msecCount = 0;

        while (upperRect.Top >= _picRect.Top || lowerRect.Bottom <= _picRect.Bottom)
        {
            if (upperRect.Top < _picRect.Top)
            {
                upperRect.Translate(0, 1);
            }

            if (lowerRect.Bottom > _picRect.Bottom)
            {
                lowerRect.Translate(0, -1);
            }

            CopyRectToScreen(upperRect, blackoutFlag);
            upperRect.Translate(0, -1);
            CopyRectToScreen(lowerRect, blackoutFlag);
            lowerRect.Translate(0, 1);
            msecCount += 4;
            if (DoCreateFrame(msecCount))
            {
                await UpdateScreenAndWait(msecCount);
            }
        }
    }

    private async ValueTask HorizontalRollToCenterAsync(bool blackoutFlag)
    {
        var upperRect = new SciRect(_picRect.Left, _picRect.Top, _picRect.Right, _picRect.Top + 1);
        var lowerRect = new SciRect(upperRect.Left, _picRect.Bottom - 1, upperRect.Right, _picRect.Bottom);
        uint msecCount = 0;

        while (upperRect.Top < lowerRect.Bottom)
        {
            CopyRectToScreen(upperRect, blackoutFlag);
            upperRect.Translate(0, 1);
            CopyRectToScreen(lowerRect, blackoutFlag);
            lowerRect.Translate(0, -1);
            msecCount += 4;
            if (DoCreateFrame(msecCount))
            {
                await UpdateScreenAndWait(msecCount);
            }
        }
    }

    /// <summary>Assumes the picture is wider than it is high.</summary>
    private async ValueTask DiagonalRollFromCenterAsync(bool blackoutFlag)
    {
        int halfHeight = _picRect.Height / 2;
        var upperRect = new SciRect(_picRect.Left + halfHeight - 2, _picRect.Top + halfHeight, _picRect.Right - halfHeight + 1, _picRect.Top + halfHeight + 1);
        var lowerRect = new SciRect(upperRect.Left, upperRect.Top, upperRect.Right, upperRect.Bottom);
        var leftRect = new SciRect(upperRect.Left, upperRect.Top, upperRect.Left + 1, lowerRect.Bottom);
        var rightRect = new SciRect(upperRect.Right, upperRect.Top, upperRect.Right + 1, lowerRect.Bottom);
        uint msecCount = 0;

        while (upperRect.Top >= _picRect.Top || lowerRect.Bottom <= _picRect.Bottom)
        {
            if (upperRect.Top < _picRect.Top)
            {
                upperRect.Translate(0, 1);
                leftRect.Top++;
                rightRect.Top++;
            }

            if (lowerRect.Bottom > _picRect.Bottom)
            {
                lowerRect.Translate(0, -1);
                leftRect.Bottom--;
                rightRect.Bottom--;
            }

            if (leftRect.Left < _picRect.Left)
            {
                leftRect.Translate(1, 0);
                upperRect.Left++;
                lowerRect.Left++;
            }

            if (rightRect.Right > _picRect.Right)
            {
                rightRect.Translate(-1, 0);
                upperRect.Right--;
                lowerRect.Right--;
            }

            CopyRectToScreen(upperRect, blackoutFlag);
            upperRect.Translate(0, -1);
            upperRect.Left--;
            upperRect.Right++;
            CopyRectToScreen(lowerRect, blackoutFlag);
            lowerRect.Translate(0, 1);
            lowerRect.Left--;
            lowerRect.Right++;
            CopyRectToScreen(leftRect, blackoutFlag);
            leftRect.Translate(-1, 0);
            leftRect.Top--;
            leftRect.Bottom++;
            CopyRectToScreen(rightRect, blackoutFlag);
            rightRect.Translate(1, 0);
            rightRect.Top--;
            rightRect.Bottom++;
            msecCount += 4;
            if (DoCreateFrame(msecCount))
            {
                await UpdateScreenAndWait(msecCount);
            }
        }
    }

    private async ValueTask DiagonalRollToCenterAsync(bool blackoutFlag)
    {
        var upperRect = new SciRect(_picRect.Left, _picRect.Top, _picRect.Right, _picRect.Top + 1);
        var lowerRect = new SciRect(_picRect.Left, _picRect.Bottom - 1, _picRect.Right, _picRect.Bottom);
        var leftRect = new SciRect(_picRect.Left, _picRect.Top, _picRect.Left + 1, _picRect.Bottom);
        var rightRect = new SciRect(_picRect.Right - 1, _picRect.Top, _picRect.Right, _picRect.Bottom);
        uint msecCount = 0;

        while (upperRect.Top < lowerRect.Bottom)
        {
            CopyRectToScreen(upperRect, blackoutFlag);
            upperRect.Translate(0, 1);
            upperRect.Left++;
            upperRect.Right--;
            CopyRectToScreen(lowerRect, blackoutFlag);
            lowerRect.Translate(0, -1);
            lowerRect.Left++;
            lowerRect.Right--;
            CopyRectToScreen(leftRect, blackoutFlag);
            leftRect.Translate(1, 0);
            CopyRectToScreen(rightRect, blackoutFlag);
            rightRect.Translate(-1, 0);
            msecCount += 4;
            if (DoCreateFrame(msecCount))
            {
                await UpdateScreenAndWait(msecCount);
            }
        }
    }
}
