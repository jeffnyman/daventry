using Daventry.Core.Hosting;
using Daventry.Sci.Engine;

namespace Daventry.Sci.Graphics;

/// <summary>
/// Compares objects with the screen and with each other: kOnControl, kCanBeHere, kSetNowSeen,
/// kBaseSetter and kIsItSkip. Port of ScummVM's compare.cpp with the SCI16 coordinate adjuster.
/// </summary>
public sealed class GfxCompare
{
    public const int SignalNoUpdate = 0x0004;
    public const int SignalRemoveView = 0x0080;
    public const int SignalIgnoreActor = 0x4000;

    private readonly EngineState _s;

    public GfxCompare(EngineState s)
    {
        _s = s;
    }

    /// <summary>Clips a script rectangle to the picture window and moves it to screen coordinates.</summary>
    public SciRect OnControlAdjust(SciRect rect)
    {
        var ports = _s.Ports;
        var oldPort = ports.SetPort(ports.PicWind);
        var adjusted = rect;
        adjusted.Clip(ports.GetPort().Rect);
        ports.OffsetRect(ref adjusted);
        ports.SetPort(oldPort);
        return adjusted;
    }

    public int IsOnControl(GfxScreenMask screenMask, SciRect rect)
    {
        if (rect.IsEmpty)
        {
            return 0;
        }

        var screen = _s.Screen;
        int result = 0;
        if ((screenMask & GfxScreenMask.Priority) != 0)
        {
            for (int y = rect.Top; y < rect.Bottom; y++)
            {
                for (int x = rect.Left; x < rect.Right; x++)
                {
                    result |= 1 << screen.GetPriority(x, y);
                }
            }
        }
        else
        {
            for (int y = rect.Top; y < rect.Bottom; y++)
            {
                for (int x = rect.Left; x < rect.Right; x++)
                {
                    result |= 1 << screen.GetControl(x, y);
                }
            }
        }

        return result;
    }

    public int KernelOnControl(GfxScreenMask screenMask, SciRect rect) => IsOnControl(screenMask, OnControlAdjust(rect));

    public SciRect GetNSRect(RegT obj)
    {
        var sel = _s.Kernel.Selectors;
        return new SciRect(
            (short)Selectors.ReadSelectorValue(_s, obj, sel.NsLeft),
            (short)Selectors.ReadSelectorValue(_s, obj, sel.NsTop),
            (short)Selectors.ReadSelectorValue(_s, obj, sel.NsRight),
            (short)Selectors.ReadSelectorValue(_s, obj, sel.NsBottom));
    }

    public void SetNSRect(RegT obj, SciRect nsRect)
    {
        var sel = _s.Kernel.Selectors;
        Selectors.WriteSelectorValue(_s, obj, sel.NsLeft, nsRect.Left);
        Selectors.WriteSelectorValue(_s, obj, sel.NsTop, nsRect.Top);
        Selectors.WriteSelectorValue(_s, obj, sel.NsRight, nsRect.Right);
        Selectors.WriteSelectorValue(_s, obj, sel.NsBottom, nsRect.Bottom);
    }

    public void KernelSetNowSeen(RegT obj)
    {
        var sel = _s.Kernel.Selectors;
        int viewId = (short)Selectors.ReadSelectorValue(_s, obj, sel.View);
        int loopNo = (short)Selectors.ReadSelectorValue(_s, obj, sel.Loop);
        int celNo = (short)Selectors.ReadSelectorValue(_s, obj, sel.Cel);
        int x = (short)Selectors.ReadSelectorValue(_s, obj, sel.X);
        int y = (short)Selectors.ReadSelectorValue(_s, obj, sel.Y);
        int z = sel.Z > -1 ? (short)Selectors.ReadSelectorValue(_s, obj, sel.Z) : 0;

        var view = _s.Cache.GetView(viewId);
        if (view is null)
        {
            return;
        }

        view.GetCelRect(loopNo, celNo, x, y, z, out var celRect);
        if (Selectors.LookupSelector(_s.SegMan, _s.Version, obj, sel.NsTop, out _, out _) == SelectorType.Variable)
        {
            SetNSRect(obj, celRect);
        }
    }

    private RegT CanBeHereCheckRectList(RegT checkObject, SciRect checkRect, SciList list, int signalFlags)
    {
        var sel = _s.Kernel.Selectors;
        var curAddress = list.First;
        var curNode = _s.SegMan.LookupNode(curAddress);
        while (curNode is not null)
        {
            var curObject = curNode.Value;
            if (curObject != checkObject)
            {
                int signal = Selectors.ReadSelectorValue(_s, curObject, sel.Signal);
                if ((signal & signalFlags) == 0)
                {
                    var curRect = new SciRect(
                        (short)Selectors.ReadSelectorValue(_s, curObject, sel.BrLeft),
                        (short)Selectors.ReadSelectorValue(_s, curObject, sel.BrTop),
                        (short)Selectors.ReadSelectorValue(_s, curObject, sel.BrRight),
                        (short)Selectors.ReadSelectorValue(_s, curObject, sel.BrBottom));
                    // slightly odd, but it matches the original engine
                    if (curRect.Right > checkRect.Left && curRect.Left < checkRect.Right
                        && curRect.Bottom > checkRect.Top && curRect.Top < checkRect.Bottom)
                    {
                        return curObject;
                    }
                }
            }

            curAddress = curNode.Succ;
            curNode = _s.SegMan.LookupNode(curAddress);
        }

        return RegT.Null;
    }

    /// <summary>Null when the object can be where it is, otherwise the control bits hit or the object in the way.</summary>
    public RegT KernelCanBeHere(RegT curObject, RegT listReference)
    {
        var sel = _s.Kernel.Selectors;
        var checkRect = new SciRect(
            (short)Selectors.ReadSelectorValue(_s, curObject, sel.BrLeft),
            (short)Selectors.ReadSelectorValue(_s, curObject, sel.BrTop),
            (short)Selectors.ReadSelectorValue(_s, curObject, sel.BrRight),
            (short)Selectors.ReadSelectorValue(_s, curObject, sel.BrBottom));

        if (!checkRect.IsValidRect)
        {
            _s.Log.Warning("sci.vm", $"kCan(t)BeHere - invalid rect {checkRect}");
            return RegT.Null; // this means "can be here"
        }

        var adjustedRect = OnControlAdjust(checkRect);
        int signal = Selectors.ReadSelectorValue(_s, curObject, sel.Signal);
        int controlMask = Selectors.ReadSelectorValue(_s, curObject, sel.IllegalBits);
        int result = IsOnControl(GfxScreenMask.Control, adjustedRect) & controlMask;
        if (result == 0 && (signal & (SignalIgnoreActor | SignalRemoveView)) == 0)
        {
            var list = _s.SegMan.LookupList(listReference) ?? throw new SciVmException("kCanBeHere called with non-list as parameter");
            return CanBeHereCheckRectList(curObject, checkRect, list, SignalIgnoreActor | SignalRemoveView | SignalNoUpdate);
        }

        return RegT.Make(0, result);
    }

    public bool KernelIsItSkip(int viewId, int loopNo, int celNo, SciPoint position)
    {
        var view = _s.Cache.GetView(viewId);
        if (view is null)
        {
            return false;
        }

        var celInfo = view.GetCelInfo(loopNo, celNo);
        int x = Math.Clamp(position.X, 0, celInfo.Width - 1);
        int y = Math.Clamp(position.Y, 0, celInfo.Height - 1);
        var celData = view.GetBitmap(loopNo, celNo);
        return celData[celInfo.Width * y + x] == celInfo.ClearKey;
    }

    public void KernelBaseSetter(RegT obj)
    {
        var sel = _s.Kernel.Selectors;
        if (Selectors.LookupSelector(_s.SegMan, _s.Version, obj, sel.BrLeft, out _, out _) != SelectorType.Variable)
        {
            return;
        }

        int x = (short)Selectors.ReadSelectorValue(_s, obj, sel.X);
        int y = (short)Selectors.ReadSelectorValue(_s, obj, sel.Y);
        int z = sel.Z > -1 ? (short)Selectors.ReadSelectorValue(_s, obj, sel.Z) : 0;
        int yStep = (short)Selectors.ReadSelectorValue(_s, obj, sel.YStep);
        int viewId = (short)Selectors.ReadSelectorValue(_s, obj, sel.View);
        int loopNo = (short)Selectors.ReadSelectorValue(_s, obj, sel.Loop);
        int celNo = (short)Selectors.ReadSelectorValue(_s, obj, sel.Cel);

        // QfG1 VGA sets an invalid view on the "Room" object (view -1)
        if (viewId == -1)
        {
            return;
        }

        var view = _s.Cache.GetView(viewId);
        if (view is null)
        {
            return;
        }

        view.GetCelRect(loopNo, celNo, x, y, z, out var celRect);
        celRect.Bottom = y + 1;
        celRect.Top = celRect.Bottom - yStep;

        Selectors.WriteSelectorValue(_s, obj, sel.BrLeft, celRect.Left);
        Selectors.WriteSelectorValue(_s, obj, sel.BrRight, celRect.Right);
        Selectors.WriteSelectorValue(_s, obj, sel.BrTop, celRect.Top);
        Selectors.WriteSelectorValue(_s, obj, sel.BrBottom, celRect.Bottom);
    }
}
