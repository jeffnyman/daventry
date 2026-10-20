using Daventry.Sci.Engine;

namespace Daventry.Sci.Graphics;

/// <summary>Flags of the signal selector. Mirrors ViewSignals.</summary>
public static class ViewSignals
{
    public const int StopUpdate = 0x0001;
    public const int ViewUpdated = 0x0002;
    public const int NoUpdate = 0x0004;
    public const int Hidden = 0x0008;
    public const int FixedPriority = 0x0010;
    public const int AlwaysUpdate = 0x0020;
    public const int ForceUpdate = 0x0040;
    public const int RemoveView = 0x0080;
    public const int Frozen = 0x0100;
    public const int HitObstacle = 0x0400;
    public const int DoesntTurn = 0x0800;
    public const int IgnoreActor = 0x4000;
    public const int DisposeMe = 0x8000;
}

public static class ViewScaleSignals
{
    /// <summary>Enables scaling when drawing that cel (involves scaleX and scaleY).</summary>
    public const int DoScaling = 0x0001;

    /// <summary>Global scaling is applied to that cel, setting scaleX and scaleY.</summary>
    public const int GlobalScaling = 0x0002;

    /// <summary>Hoyle 4 only: special handling inside kAnimate when giving out cards.</summary>
    public const int Hoyle4SpecialHandling = 0x0004;
}

/// <summary>One cast member for the current kAnimate call. Mirrors AnimateEntry.</summary>
public sealed class AnimateEntry
{
    public int GivenOrderNo;
    public RegT Object;
    public int ViewId;
    public int LoopNo;
    public int CelNo;
    public int PaletteNo;
    public int X;
    public int Y;
    public int Z;
    public int Priority;
    public int Signal;
    public int ScaleSignal;
    public int ScaleX;
    public int ScaleY;
    public SciRect CelRect;
    public bool ShowBitsFlag;
    public RegT CastHandle;

    public AnimateEntry Clone() => (AnimateEntry)MemberwiseClone();
}

/// <summary>
/// kAnimate and kAddToPic for SCI0 to SCI1.1: runs the cast's doit methods, sorts the cast by
/// position, saves backgrounds, draws the cels and shows the changed areas. Port of ScummVM's
/// animate.cpp; the cursor is not hidden during transitions because it is not ported yet.
/// </summary>
public sealed class GfxAnimate
{
    // ldi 00; sag global[84]: the fast cast global is cleared by the game super class
    private static readonly byte[] FastCastSignature = [0x35, 0x00, 0xA1, 84];

    private readonly EngineState _s;
    private readonly GfxCache _cache;
    private readonly GfxCompare _compare;
    private readonly GfxPorts _ports;
    private readonly GfxPaint16 _paint16;
    private readonly GfxScreen _screen;
    private readonly GfxPalette16 _palette;
    private readonly GfxTransitions _transitions;
    private readonly List<AnimateEntry> _list = [];
    private readonly List<AnimateEntry> _lastCastData = [];

    public GfxAnimate(EngineState s, GfxTransitions transitions)
    {
        _s = s;
        _cache = s.Cache;
        _compare = s.Compare;
        _ports = s.Ports;
        _paint16 = s.Paint;
        _screen = s.Screen;
        _palette = s.Palette;
        _transitions = transitions;

        if (s.Version == SciVersion.V1_1)
        {
            // available in every SCI1.1 game
            IsFastCastEnabled = true;
        }
        else if (s.Version >= SciVersion.V1Early)
        {
            // only some games between SCI1 early and SCI1 late have it; it must only be
            // enabled for games that use it, so the code signature decides
            IsFastCastEnabled = DetectFastCast();
        }
    }

    public bool IsFastCastEnabled { get; }

    /// <summary>Looks for the fast cast signature in the game super class's script.</summary>
    private bool DetectFastCast()
    {
        var segMan = _s.SegMan;
        var gameObject = segMan.GetObject(_s.GameObjectAddress);
        var gameSuperObject = gameObject?.SuperClassSelector ?? RegT.Null;
        if (gameSuperObject.IsNull)
        {
            gameSuperObject = _s.GameObjectAddress; // KQ5 CD could hit this with old saves
        }

        var objectScript = segMan.GetScriptIfLoaded(gameSuperObject.Segment);
        if (objectScript is null)
        {
            return false;
        }

        // multilingual KQ5 carries the signature too but never sets the global, which matches
        // its interpreter having the feature disabled; harmless, as the global stays null
        return new ReadOnlySpan<byte>(objectScript.Buf, 0, (int)objectScript.ScriptSize).IndexOf(FastCastSignature) >= 0;
    }

    public void DisposeLastCast() => _lastCastData.Clear();

    /// <summary>Calls doit on every cast member that is not frozen. False when fast cast aborts the call.</summary>
    private async ValueTask<bool> Invoke(SciList list, KernelArgs argv)
    {
        var sel = _s.Kernel.Selectors;
        var segMan = _s.SegMan;
        var curAddress = list.First;
        var curNode = segMan.LookupNode(curAddress);

        while (curNode is not null)
        {
            var curObject = curNode.Value;

            if (IsFastCastEnabled)
            {
                // with a fast cast object set, kAnimate is aborted; KQ5 would otherwise draw
                // animation cels into speech boxes. The global normally points to an object
                // called fastCast, in EcoQuest 1 also to one called EventHandler (bug #5170)
                if (!_s.GetGlobal(GlobalVar.FastCast).IsNull)
                {
                    return false;
                }
            }

            int signal = Selectors.ReadSelectorValue(_s, curObject, sel.Signal);
            if ((signal & ViewSignals.Frozen) == 0)
            {
                await Selectors.InvokeSelector(_s, curObject, sel.Doit, argv);

                // a game being loaded stops the processing
                if (_s.AbortScriptProcessing != AbortGameState.None)
                {
                    return true;
                }

                // look the node up again: the table may have been reallocated, and the node
                // may be gone (LSL2 room 42); a node deleted with kDeleteKey has no successor,
                // which stops the loop here like SSCI
                curNode = segMan.LookupNode(curAddress, false);
            }

            if (curNode is not null)
            {
                curAddress = curNode.Succ;
                curNode = segMan.LookupNode(curAddress);
            }
        }

        return true;
    }

    private static int SortHelper(AnimateEntry entry1, AnimateEntry entry2)
    {
        if (entry1.Y == entry2.Y)
        {
            // with the same y and z the original order stays; Iceman room 35 needs that
            return entry1.Z == entry2.Z ? entry1.GivenOrderNo.CompareTo(entry2.GivenOrderNo) : entry1.Z.CompareTo(entry2.Z);
        }

        return entry1.Y.CompareTo(entry2.Y);
    }

    private void MakeSortedList(SciList list)
    {
        var sel = _s.Kernel.Selectors;
        var segMan = _s.SegMan;
        var curAddress = list.First;
        var curNode = segMan.LookupNode(curAddress);

        _list.Clear();
        _lastCastData.Clear();

        for (int listNr = 0; curNode is not null; listNr++)
        {
            var curObject = curNode.Value;
            var listEntry = new AnimateEntry
            {
                Object = curObject,
                CastHandle = RegT.Null,
                GivenOrderNo = listNr,
                ViewId = (short)Selectors.ReadSelectorValue(_s, curObject, sel.View),
                LoopNo = (short)Selectors.ReadSelectorValue(_s, curObject, sel.Loop),
                CelNo = (short)Selectors.ReadSelectorValue(_s, curObject, sel.Cel),
                PaletteNo = (short)Selectors.ReadSelectorValue(_s, curObject, sel.Palette),
                X = (short)Selectors.ReadSelectorValue(_s, curObject, sel.X),
                Y = (short)Selectors.ReadSelectorValue(_s, curObject, sel.Y),
                Z = (short)Selectors.ReadSelectorValue(_s, curObject, sel.Z),
                Priority = (short)Selectors.ReadSelectorValue(_s, curObject, sel.Priority),
                Signal = Selectors.ReadSelectorValue(_s, curObject, sel.Signal),
                ScaleSignal = 0,
                ScaleX = 128,
                ScaleY = 128,
            };

            if (_s.Version >= SciVersion.V1_1)
            {
                // cel scaling
                listEntry.ScaleSignal = Selectors.ReadSelectorValue(_s, curObject, sel.ScaleSignal);
                if ((listEntry.ScaleSignal & ViewScaleSignals.DoScaling) != 0)
                {
                    listEntry.ScaleX = (short)Selectors.ReadSelectorValue(_s, curObject, sel.ScaleX);
                    listEntry.ScaleY = (short)Selectors.ReadSelectorValue(_s, curObject, sel.ScaleY);
                }
            }

            _list.Add(listEntry);

            curAddress = curNode.Succ;
            curNode = segMan.LookupNode(curAddress);
        }

        // sorted by y and z; the comparer keeps the given order for ties, so the sort is
        // stable (Iceman's cupboard in the submarine shows when it is not)
        _list.Sort(SortHelper);
    }

    private void Fill(ref int oldPicNotValid)
    {
        var sel = _s.Kernel.Selectors;
        foreach (var it in _list)
        {
            var view = _cache.GetView(it.ViewId);
            if (view is null)
            {
                continue;
            }

            AdjustInvalidCels(view, it);
            ProcessViewScaling(view, it);
            SetNsRect(view, it);

            // the priority follows the y coordinate unless it is fixed
            if ((it.Signal & ViewSignals.FixedPriority) == 0)
            {
                it.Priority = _ports.KernelCoordinateToPriority(it.Y);
                Selectors.WriteSelectorValue(_s, it.Object, sel.Priority, it.Priority);
            }

            if ((it.Signal & ViewSignals.NoUpdate) != 0)
            {
                if ((it.Signal & (ViewSignals.ForceUpdate | ViewSignals.ViewUpdated)) != 0
                    || ((it.Signal & ViewSignals.Hidden) != 0 && (it.Signal & ViewSignals.RemoveView) == 0)
                    || ((it.Signal & ViewSignals.Hidden) == 0 && (it.Signal & ViewSignals.RemoveView) != 0)
                    || (it.Signal & ViewSignals.AlwaysUpdate) != 0)
                {
                    oldPicNotValid++;
                }

                it.Signal &= ~ViewSignals.StopUpdate;
            }
            else
            {
                if ((it.Signal & ViewSignals.StopUpdate) != 0 || (it.Signal & ViewSignals.AlwaysUpdate) != 0)
                {
                    oldPicNotValid++;
                }

                it.Signal &= ~ViewSignals.ForceUpdate;
            }
        }
    }

    /// <summary>
    /// Sierra reset loop and cel to 0 when they were at or above the counts as signed values,
    /// and later clamped negative ones to the last; games rely on both (LB1's knight in room
    /// 37 with cel 3, KQ5's trunk in the intro with a huge cel number).
    /// </summary>
    private void AdjustInvalidCels(GfxView view, AnimateEntry it)
    {
        var sel = _s.Kernel.Selectors;
        int viewLoopCount = view.LoopCount;
        if (it.LoopNo >= viewLoopCount)
        {
            it.LoopNo = 0;
            Selectors.WriteSelectorValue(_s, it.Object, sel.Loop, it.LoopNo);
        }
        else if (it.LoopNo < 0)
        {
            it.LoopNo = viewLoopCount - 1;
            // not setting the selector is right; Sierra did not either
        }

        int viewCelCount = view.GetCelCount(it.LoopNo);
        if (it.CelNo >= viewCelCount)
        {
            it.CelNo = 0;
            Selectors.WriteSelectorValue(_s, it.Object, sel.Cel, it.CelNo);
        }
        else if (it.CelNo < 0)
        {
            it.CelNo = viewCelCount - 1;
        }
    }

    private void ProcessViewScaling(GfxView view, AnimateEntry it)
    {
        if (!view.IsScaleable)
        {
            // Laura Bow 2 (especially floppy) depends on some views not being scalable
            it.ScaleSignal = 0;
            it.ScaleY = it.ScaleX = 128;
        }
        else if ((it.ScaleSignal & ViewScaleSignals.DoScaling) != 0 && (it.ScaleSignal & ViewScaleSignals.GlobalScaling) != 0)
        {
            ApplyGlobalScaling(it, view);
        }
    }

    private void ApplyGlobalScaling(AnimateEntry entry, GfxView view)
    {
        var sel = _s.Kernel.Selectors;
        // global scaling uses the current room's vanishing point
        int maxScale = (short)Selectors.ReadSelectorValue(_s, entry.Object, sel.MaxScale);
        int celHeight = view.GetHeight(entry.LoopNo, entry.CelNo);
        int maxCelHeight = (maxScale * celHeight) >> 7;
        var currentRoom = _s.GetGlobal(GlobalVar.CurrentRoom);
        int vanishingY = (short)Selectors.ReadSelectorValue(_s, currentRoom, sel.VanishingY);

        int fixedPortY = _ports.GetPort().Rect.Bottom - vanishingY;
        int fixedEntryY = entry.Y - vanishingY;
        if (fixedEntryY == 0)
        {
            fixedEntryY = 1;
        }

        if (celHeight == 0 || fixedPortY == 0)
        {
            throw new SciVmException("global scaling panic");
        }

        entry.ScaleY = maxCelHeight * fixedEntryY / fixedPortY;
        entry.ScaleY = entry.ScaleY * 128 / celHeight;
        entry.ScaleX = entry.ScaleY;

        Selectors.WriteSelectorValue(_s, entry.Object, sel.ScaleX, entry.ScaleX);
        Selectors.WriteSelectorValue(_s, entry.Object, sel.ScaleY, entry.ScaleY);
    }

    private void SetNsRect(GfxView view, AnimateEntry it)
    {
        bool shouldSetNsRect = true;

        if ((it.ScaleSignal & ViewScaleSignals.DoScaling) != 0)
        {
            view.GetCelScaledRect(it.LoopNo, it.CelNo, it.X, it.Y, it.Z, it.ScaleX, it.ScaleY, out it.CelRect);
            // when scaled, the nsRect is only set if the object gets drawn
            if ((it.Signal & ViewSignals.Hidden) != 0 && (it.Signal & ViewSignals.AlwaysUpdate) == 0)
            {
                shouldSetNsRect = false;
            }
        }
        else if (_s.GameId == SciGameId.Hoyle4 && (it.ScaleSignal & ViewScaleSignals.Hoyle4SpecialHandling) != 0)
        {
            // not in the other SCI1.1 interpreters; checking it there breaks EcoQuest 2 room 200
            it.CelRect = _compare.GetNSRect(it.Object);
            view.GetCelSpecialHoyle4Rect(it.LoopNo, it.CelNo, it.X, it.Y, it.Z, ref it.CelRect);
            shouldSetNsRect = false;
        }
        else
        {
            view.GetCelRect(it.LoopNo, it.CelNo, it.X, it.Y, it.Z, out it.CelRect);
        }

        if (shouldSetNsRect)
        {
            _compare.SetNSRect(it.Object, it.CelRect);
        }
    }

    private void Update()
    {
        var sel = _s.Kernel.Selectors;

        // remove all no-update cels, if requested
        for (int i = _list.Count - 1; i >= 0; i--)
        {
            var it = _list[i];
            if ((it.Signal & ViewSignals.NoUpdate) != 0)
            {
                if ((it.Signal & ViewSignals.RemoveView) == 0)
                {
                    var bitsHandle = Selectors.ReadSelector(_s, it.Object, sel.UnderBits);
                    if (_screen.PicNotValid != 1)
                    {
                        _paint16.BitsRestoreHunk(bitsHandle);
                        it.ShowBitsFlag = true;
                    }
                    else
                    {
                        _paint16.FreeHunk(bitsHandle);
                    }

                    Selectors.WriteSelectorValue(_s, it.Object, sel.UnderBits, 0);
                }

                it.Signal &= ~ViewSignals.ForceUpdate;
                if ((it.Signal & ViewSignals.ViewUpdated) != 0)
                {
                    it.Signal &= ~(ViewSignals.ViewUpdated | ViewSignals.NoUpdate);
                }
            }
            else if ((it.Signal & ViewSignals.StopUpdate) != 0)
            {
                it.Signal &= ~ViewSignals.StopUpdate;
                it.Signal |= ViewSignals.NoUpdate;
            }
        }

        // draw always-update cels
        foreach (var it in _list)
        {
            if ((it.Signal & ViewSignals.AlwaysUpdate) != 0)
            {
                _paint16.DrawCel(it.ViewId, it.LoopNo, it.CelNo, it.CelRect, (byte)it.Priority, it.PaletteNo, it.ScaleX, it.ScaleY);
                it.ShowBitsFlag = true;

                it.Signal &= ~(ViewSignals.StopUpdate | ViewSignals.ViewUpdated | ViewSignals.NoUpdate | ViewSignals.ForceUpdate);
                if ((it.Signal & ViewSignals.IgnoreActor) == 0)
                {
                    var rect = it.CelRect;
                    rect.Top = Math.Clamp(_ports.KernelPriorityToCoordinate((byte)it.Priority) - 1, rect.Top, rect.Bottom - 1);
                    _paint16.FillRect(rect, GfxScreenMask.Control, 0, 0, 15);
                }
            }
        }

        // save the background of all no-update cels
        foreach (var it in _list)
        {
            if ((it.Signal & ViewSignals.NoUpdate) != 0)
            {
                if ((it.Signal & ViewSignals.Hidden) != 0)
                {
                    it.Signal |= ViewSignals.RemoveView;
                }
                else
                {
                    it.Signal &= ~ViewSignals.RemoveView;
                    var bitsHandle = (it.Signal & ViewSignals.IgnoreActor) != 0
                        ? _paint16.BitsSaveHunk(it.CelRect, GfxScreenMask.Visual | GfxScreenMask.Priority)
                        : _paint16.BitsSaveHunk(it.CelRect, GfxScreenMask.All);
                    Selectors.WriteSelector(_s, it.Object, sel.UnderBits, bitsHandle);
                }
            }
        }

        // draw no-update cels
        foreach (var it in _list)
        {
            if ((it.Signal & ViewSignals.NoUpdate) != 0 && (it.Signal & ViewSignals.Hidden) == 0)
            {
                _paint16.DrawCel(it.ViewId, it.LoopNo, it.CelNo, it.CelRect, (byte)it.Priority, it.PaletteNo, it.ScaleX, it.ScaleY);
                it.ShowBitsFlag = true;

                if ((it.Signal & ViewSignals.IgnoreActor) == 0)
                {
                    var rect = it.CelRect;
                    rect.Top = Math.Clamp(_ports.KernelPriorityToCoordinate((byte)it.Priority) - 1, rect.Top, rect.Bottom - 1);
                    _paint16.FillRect(rect, GfxScreenMask.Control, 0, 0, 15);
                }
            }
        }
    }

    private void DrawCels()
    {
        var sel = _s.Kernel.Selectors;
        _lastCastData.Clear();

        foreach (var it in _list)
        {
            if ((it.Signal & (ViewSignals.NoUpdate | ViewSignals.Hidden | ViewSignals.AlwaysUpdate)) == 0)
            {
                // save the background
                var bitsHandle = _paint16.BitsSaveHunk(it.CelRect, GfxScreenMask.All);
                Selectors.WriteSelector(_s, it.Object, sel.UnderBits, bitsHandle);

                _paint16.DrawCel(it.ViewId, it.LoopNo, it.CelNo, it.CelRect, (byte)it.Priority, it.PaletteNo, it.ScaleX, it.ScaleY);
                it.ShowBitsFlag = true;

                if ((it.Signal & ViewSignals.RemoveView) != 0)
                {
                    it.Signal &= ~ViewSignals.RemoveView;
                }

                // remembered for reAnimate
                _lastCastData.Add(it.Clone());
            }
        }
    }

    private void UpdateScreen(int oldPicNotValid)
    {
        var sel = _s.Kernel.Selectors;
        foreach (var it in _list)
        {
            bool show = it.ShowBitsFlag
                || (it.Signal & (ViewSignals.RemoveView | ViewSignals.NoUpdate)) == 0
                || ((it.Signal & ViewSignals.RemoveView) == 0 && (it.Signal & ViewSignals.NoUpdate) != 0 && oldPicNotValid != 0);
            if (!show)
            {
                continue;
            }

            var lsRect = new SciRect(
                (short)Selectors.ReadSelectorValue(_s, it.Object, sel.LsLeft),
                (short)Selectors.ReadSelectorValue(_s, it.Object, sel.LsTop),
                (short)Selectors.ReadSelectorValue(_s, it.Object, sel.LsRight),
                (short)Selectors.ReadSelectorValue(_s, it.Object, sel.LsBottom));

            var workerRect = lsRect;
            workerRect.Clip(it.CelRect);

            if (!workerRect.IsEmpty)
            {
                workerRect = lsRect;
                workerRect.Extend(it.CelRect);
            }
            else
            {
                _paint16.BitsShow(lsRect);
                workerRect = it.CelRect;
            }

            Selectors.WriteSelectorValue(_s, it.Object, sel.LsLeft, it.CelRect.Left);
            Selectors.WriteSelectorValue(_s, it.Object, sel.LsTop, it.CelRect.Top);
            Selectors.WriteSelectorValue(_s, it.Object, sel.LsRight, it.CelRect.Right);
            Selectors.WriteSelectorValue(_s, it.Object, sel.LsBottom, it.CelRect.Bottom);
            _paint16.BitsShow(workerRect);

            if ((it.Signal & ViewSignals.Hidden) != 0)
            {
                it.Signal |= ViewSignals.RemoveView;
            }
        }
    }

    private async ValueTask RestoreAndDelete(KernelArgs argv)
    {
        var sel = _s.Kernel.Selectors;

        // a separate loop: in SQ1 some dispose methods change the fixed priority flag of
        // another object, which the old signal must not overwrite
        foreach (var it in _list)
        {
            Selectors.WriteSelectorValue(_s, it.Object, sel.Signal, it.Signal);
        }

        for (int i = _list.Count - 1; i >= 0; i--)
        {
            var it = _list[i];
            // the signal is read again on purpose, to get an up-to-date one
            it.Signal = Selectors.ReadSelectorValue(_s, it.Object, sel.Signal);

            if ((it.Signal & (ViewSignals.NoUpdate | ViewSignals.RemoveView)) == 0)
            {
                _paint16.BitsRestoreHunk(Selectors.ReadSelector(_s, it.Object, sel.UnderBits));
                Selectors.WriteSelectorValue(_s, it.Object, sel.UnderBits, 0);
            }

            if ((it.Signal & ViewSignals.DisposeMe) != 0)
            {
                await Selectors.InvokeSelector(_s, it.Object, sel.Delete, argv);
            }
        }
    }

    /// <summary>Redraws the last cast over a restored area and shows it. Used by kGraph redraw box.</summary>
    public void ReAnimate(SciRect rect)
    {
        if (_lastCastData.Count > 0)
        {
            foreach (var it in _lastCastData)
            {
                it.CastHandle = _paint16.BitsSaveHunk(it.CelRect, GfxScreenMask.Visual | GfxScreenMask.Priority);
                _paint16.DrawCel(it.ViewId, it.LoopNo, it.CelNo, it.CelRect, (byte)it.Priority, it.PaletteNo, it.ScaleX, it.ScaleY);
            }

            _paint16.BitsShow(rect);
            for (int i = _lastCastData.Count - 1; i >= 0; i--)
            {
                _paint16.BitsRestoreHunk(_lastCastData[i].CastHandle);
            }
        }
        else
        {
            _paint16.BitsShow(rect);
        }
    }

    private void AddToPicDrawCels()
    {
        foreach (var it in _list)
        {
            var view = _cache.GetView(it.ViewId);
            if (view is null)
            {
                continue;
            }

            // kAddToPic does no loop or cel fix-ups
            if (it.Priority == -1)
            {
                it.Priority = _ports.KernelCoordinateToPriority(it.Y);
            }

            if (!view.IsScaleable)
            {
                // Laura Bow 2 specific, see Fill
                it.ScaleSignal = 0;
                it.ScaleY = it.ScaleX = 128;
            }

            if ((it.ScaleSignal & ViewScaleSignals.DoScaling) != 0)
            {
                if ((it.ScaleSignal & ViewScaleSignals.GlobalScaling) != 0)
                {
                    ApplyGlobalScaling(it, view);
                }

                view.GetCelScaledRect(it.LoopNo, it.CelNo, it.X, it.Y, it.Z, it.ScaleX, it.ScaleY, out it.CelRect);
                _compare.SetNSRect(it.Object, it.CelRect);
            }
            else
            {
                view.GetCelRect(it.LoopNo, it.CelNo, it.X, it.Y, it.Z, out it.CelRect);
            }

            _paint16.DrawCel(view, it.LoopNo, it.CelNo, it.CelRect, (byte)it.Priority, it.PaletteNo, it.ScaleX, it.ScaleY);
            if ((it.Signal & ViewSignals.IgnoreActor) == 0)
            {
                it.CelRect.Top = Math.Clamp(_ports.KernelPriorityToCoordinate((byte)it.Priority) - 1, it.CelRect.Top, it.CelRect.Bottom - 1);
                _paint16.FillRect(it.CelRect, GfxScreenMask.Control, 0, 0, 15);
            }
        }
    }

    private void AddToPicDrawView(int viewId, int loopNo, int celNo, int x, int y, int priority, int control)
    {
        var view = _cache.GetView(viewId);
        if (view is null)
        {
            return;
        }

        if (priority == -1)
        {
            priority = _ports.KernelCoordinateToPriority(y);
        }

        view.GetCelRect(loopNo, celNo, x, y, 0, out var celRect);
        _paint16.DrawCel(view, loopNo, celNo, celRect, (byte)priority, 0);

        if (control != -1)
        {
            celRect.Top = Math.Clamp(_ports.KernelPriorityToCoordinate((byte)priority) - 1, celRect.Top, celRect.Bottom - 1);
            _paint16.FillRect(celRect, GfxScreenMask.Control, 0, 0, (byte)control);
        }
    }

    private async ValueTask AnimateShowPic()
    {
        var picPort = _ports.PicWind;
        var picRect = picPort.Rect;
        // the rect becomes relative to the screen
        picRect.Translate(picPort.Left, picPort.Top);
        await _transitions.Doit(picRect);
    }

    /// <summary>
    /// A zero-tick pal-vary gets a few ticks to trigger before the first kAnimate, as pictures
    /// load much faster than on the original hardware (Freddy Pharkas at night, bug #5298).
    /// </summary>
    private async ValueTask DelayForPalVaryWorkaround()
    {
        if (!_palette.NeedsPalVaryDelay)
        {
            return;
        }

        for (int i = 0; i < 4 && _palette.NeedsPalVaryDelay; i++)
        {
            await _s.SleepMs(17);
        }
    }

    public async ValueTask KernelAnimate(RegT listReference, bool cycle, KernelArgs argv)
    {
        if (_screen.PicNotValid != 0)
        {
            await DelayForPalVaryWorkaround();
        }

        int oldPicNotValid = _screen.PicNotValid;

        if (_s.Version >= SciVersion.V1_1)
        {
            _palette.PalVaryUpdate();
        }

        if (listReference.IsNull)
        {
            DisposeLastCast();
            if (_screen.PicNotValid != 0)
            {
                await AnimateShowPic();
            }

            return;
        }

        var list = _s.SegMan.LookupList(listReference) ?? throw new SciVmException("kAnimate called with non-list as parameter");

        if (cycle)
        {
            if (!await Invoke(list, argv))
            {
                return;
            }

            // the list may have been modified
            list = _s.SegMan.LookupList(listReference) ?? throw new SciVmException("kAnimate: the cast list vanished while its doit methods ran");
        }

        var oldPort = _ports.SetPort(_ports.PicWind);
        DisposeLastCast();

        MakeSortedList(list);
        Fill(ref oldPicNotValid);

        if (oldPicNotValid != 0)
        {
            // beginUpdate and endUpdate came with SCI1; for SCI0 they break small things like
            // the percentage bar of QfG1 EGA's character skill screen
            if (_s.Version >= SciVersion.V1EgaOnly)
            {
                _ports.BeginUpdate(_ports.PicWind);
            }

            Update();
            if (_s.Version >= SciVersion.V1EgaOnly)
            {
                _ports.EndUpdate(_ports.PicWind);
            }
        }

        DrawCels();

        if (_screen.PicNotValid != 0)
        {
            await AnimateShowPic();
        }

        UpdateScreen(oldPicNotValid);
        await RestoreAndDelete(argv);

        _ports.SetPort(oldPort);

        // trigger the speed throttler
        _s.ThrottleTrigger = true;
    }

    private void AddToPicSetPicNotValid() => _screen.PicNotValid = _s.Version <= SciVersion.V1Early ? 1 : 2;

    public void KernelAddToPicList(RegT listReference)
    {
        _ports.SetPort(_ports.PicWind);
        var list = _s.SegMan.LookupList(listReference) ?? throw new SciVmException("kAddToPic called with non-list as parameter");
        MakeSortedList(list);
        AddToPicDrawCels();
        AddToPicSetPicNotValid();
    }

    public void KernelAddToPicView(int viewId, int loopNo, int celNo, int x, int y, int priority, int control)
    {
        _ports.SetPort(_ports.PicWind);
        AddToPicDrawView(viewId, loopNo, celNo, x, y, priority, control);
        AddToPicSetPicNotValid();
    }
}
