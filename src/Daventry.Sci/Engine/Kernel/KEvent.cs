namespace Daventry.Sci.Engine;

public static partial class KernelFunctions
{
    // ---- kevent.cpp ----

    public const int SciEventNone = 0;
    public const int SciEventMousePress = 1;
    public const int SciEventMouseRelease = 1 << 1;
    public const int SciEventMouse = SciEventMousePress | SciEventMouseRelease;
    public const int SciEventKeyDown = 1 << 2;
    public const int SciEventKeyUp = 1 << 3;
    public const int SciEventKey = SciEventKeyDown | SciEventKeyUp;
    public const int SciEventDirection16 = 1 << 6;
    public const int SciEventSaid = 1 << 7;
    public const int SciEventQuit = 1 << 11;
    public const int SciEventPeek = 1 << 15;
    public const int SciEventAny = ~SciEventPeek;

    public const int SciKeyModNone = 0;
    public const int SciKeyModRShift = 1;
    public const int SciKeyModLShift = 1 << 1;
    public const int SciKeyModShift = SciKeyModRShift | SciKeyModLShift;
    public const int SciKeyModCtrl = 1 << 2;
    public const int SciKeyModAlt = 1 << 3;
    public const int SciKeyModNumLock = 1 << 5;
    public const int SciKeyModNonSticky = SciKeyModShift | SciKeyModCtrl | SciKeyModAlt;
    public const int SciKeyModAll = -1;

    public const int SciKeyHome = 71 << 8;
    public const int SciKeyUp = 72 << 8;
    public const int SciKeyPageUp = 73 << 8;
    public const int SciKeyLeft = 75 << 8;
    public const int SciKeyCenter = 76 << 8;
    public const int SciKeyRight = 77 << 8;
    public const int SciKeyEnd = 79 << 8;
    public const int SciKeyDown = 80 << 8;
    public const int SciKeyPageDown = 81 << 8;

    private static readonly (int Key, int Direction)[] KeyToDirMap =
    [
        (SciKeyHome, 8), (SciKeyUp, 1), (SciKeyPageUp, 2),
        (SciKeyLeft, 7), (SciKeyCenter, 0), (SciKeyRight, 3),
        (SciKeyEnd, 6), (SciKeyDown, 5), (SciKeyPageDown, 4),
    ];

    public static async ValueTask<RegT> GetEventAsync(EngineState s, KernelArgs argv)
    {
        var sel = s.Kernel.Selectors;
        int mask = argv.UInt16(0);
        var obj = argv[1];

        var curEvent = s.Events.GetSciEvent(mask);
        var mousePos = curEvent.MousePos;
        s.RefreshCursorPosition?.Invoke();

        // the parser event becomes invalid
        s.InvalidateParserEvent?.Invoke();

        Selectors.WriteSelectorValue(s, obj, sel.X, mousePos.X);
        Selectors.WriteSelectorValue(s, obj, sel.Y, mousePos.Y);

        // only the relevant modifier bits are kept
        int modifierMask = s.Version <= SciVersion.V01 ? SciKeyModAll : SciKeyModNonSticky;
        int modifiers = curEvent.Modifiers & modifierMask;
        if (s.Platform == SciPlatform.Dos && s.Version <= SciVersion.V01)
        {
            // SCI asked the BIOS for the shift status through INT16 AH=02, which left 02 in
            // AH; Betrayed Alliance's darts game relies on it being in the upper byte
            modifiers |= 0x0200;
        }

        switch (curEvent.Type)
        {
            case SciEventQuit:
                s.AbortScriptProcessing = AbortGameState.QuitGame;
                break;
            case SciEventKeyDown:
            case SciEventKeyUp:
                Selectors.WriteSelectorValue(s, obj, sel.Type, curEvent.Type);
                Selectors.WriteSelectorValue(s, obj, sel.Message, curEvent.Character);
                Selectors.WriteSelectorValue(s, obj, sel.Modifiers, modifiers);
                s.Acc = RegT.True;
                break;
            case SciEventMouseRelease:
            case SciEventMousePress:
                if ((mask & curEvent.Type) != 0)
                {
                    Selectors.WriteSelectorValue(s, obj, sel.Type, curEvent.Type);
                    Selectors.WriteSelectorValue(s, obj, sel.Message, 0);
                    Selectors.WriteSelectorValue(s, obj, sel.Modifiers, modifiers);
                    s.Acc = RegT.True;
                }

                break;
            default:
                // a null event
                Selectors.WriteSelectorValue(s, obj, sel.Type, SciEventNone);
                Selectors.WriteSelectorValue(s, obj, sel.Message, 0);
                Selectors.WriteSelectorValue(s, obj, sel.Modifiers, modifiers);
                s.Acc = RegT.Null;
                break;
        }

        if (s.Features.DetectDoSoundType() <= SciVersion.V0Late)
        {
            // sound-SCI0 does not poll its cues itself, so kGetEvent stands in for cmdUpdateSoundCues
            s.UpdateSci0SoundCues?.Invoke();
        }

        // Unthrottled inner loops (fast cast games polling while a message shows, Dialog:doit
        // without kWait) are slowed down a little so the CPU is not maxed out (bugs #5091, #5326, #14020)
        if (++s.EventCounter > 2 || (s.IsFastCastEnabled?.Invoke() == true && !s.GetGlobal(GlobalVar.FastCast).IsNull))
        {
            await s.SleepMs(10);
        }

        return s.Acc;
    }

    public static RegT MapKeyToDir(EngineState s, KernelArgs argv)
    {
        var sel = s.Kernel.Selectors;
        var obj = argv[0];
        if (Selectors.ReadSelectorValue(s, obj, sel.Type) == SciEventKeyDown)
        {
            int message = Selectors.ReadSelectorValue(s, obj, sel.Message);
            int eventType = SciEventDirection16;
            // from SCI1 on the keyboard driver added the direction bit instead of replacing the
            // type; the PseudoMouse class (script 933) needs that
            if (s.Features.DetectPseudoMouseAbility() == PseudoMouseAbilityType.True)
            {
                eventType |= SciEventKeyDown;
            }

            foreach (var (key, direction) in KeyToDirMap)
            {
                if (key == message)
                {
                    Selectors.WriteSelectorValue(s, obj, sel.Type, eventType);
                    Selectors.WriteSelectorValue(s, obj, sel.Message, direction);
                    return RegT.True;
                }
            }

            return RegT.Null;
        }

        return s.Acc; // no keyboard event to map
    }

    public static RegT GlobalToLocal(EngineState s, KernelArgs argv)
    {
        var sel = s.Kernel.Selectors;
        var obj = argv[0];
        if (obj.Segment != 0)
        {
            short x = (short)Selectors.ReadSelectorValue(s, obj, sel.X);
            short y = (short)Selectors.ReadSelectorValue(s, obj, sel.Y);
            var curPort = s.Ports.GetPort();
            x -= (short)curPort.Left;
            y -= (short)curPort.Top;
            Selectors.WriteSelectorValue(s, obj, sel.X, x);
            Selectors.WriteSelectorValue(s, obj, sel.Y, y);
        }

        return s.Acc;
    }

    public static RegT LocalToGlobal(EngineState s, KernelArgs argv)
    {
        var sel = s.Kernel.Selectors;
        var obj = argv[0];
        if (obj.Segment != 0)
        {
            short x = (short)Selectors.ReadSelectorValue(s, obj, sel.X);
            short y = (short)Selectors.ReadSelectorValue(s, obj, sel.Y);
            var curPort = s.Ports.GetPort();
            x += (short)curPort.Left;
            y += (short)curPort.Top;
            Selectors.WriteSelectorValue(s, obj, sel.X, x);
            Selectors.WriteSelectorValue(s, obj, sel.Y, y);
        }

        return s.Acc;
    }

    public static RegT Joystick(EngineState s, KernelArgs argv) => RegT.Null;
}
