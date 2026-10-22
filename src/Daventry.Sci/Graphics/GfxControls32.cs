using System.Text;
using Daventry.Core.Hosting;
using Daventry.Sci.Engine;

namespace Daventry.Sci.Graphics;

/// <summary>The state of a text field while kEditText or kInputText owns the input.</summary>
public sealed class TextEditor
{
    public RegT Bitmap;
    public int Width;
    public SciRect TextRect;
    public StringBuilder Text = new();
    public int BorderColor;
    public byte ForeColor;
    public byte BackColor;
    public byte SkipColor;
    public int FontId;
    public int CursorCharPosition;
    public bool CursorIsDrawn;
    public SciRect CursorRect;
    public int MaxLength;

    /// <summary>The first typed character replaces the existing text, unless the cursor moved first.</summary>
    public bool ClearTextOnInput = true;
}

/// <summary>One line added to a scroll window, with the formatting codes it was given.</summary>
public sealed class ScrollWindowEntry
{
    public RegT Id;
    public TextAlign Alignment;
    public int ForeColor;
    public int FontId;
    public string Text = string.Empty;
}

/// <summary>
/// A scrolling text window (the inventory descriptions and subtitles of SCI2.1 games): a list
/// of entries rendered into one bitmap a visible page at a time. Port of ScummVM's ScrollWindow.
/// </summary>
public sealed class ScrollWindow
{
    private readonly Sci32Graphics _g;
    private readonly GfxText32 _text32;
    private readonly List<ScrollWindowEntry> _entries = [];
    private readonly int _maxNumEntries;
    private readonly List<int> _startsOfLines = [];
    private string _text = string.Empty;
    private string _visibleText = string.Empty;
    private int _firstVisibleChar;
    private int _topVisibleLine;
    private int _lastVisibleChar;
    private int _bottomVisibleLine;
    private int _numLines;
    private int _numVisibleLines;
    private readonly RegT _plane;
    private readonly byte _foreColor;
    private readonly byte _backColor;
    private readonly int _borderColor;
    private readonly int _fontId;
    private readonly TextAlign _alignment;
    private bool _visible;
    private SciRect _textRect;
    private readonly SciPoint _position;
    private readonly int _pointSize;
    private readonly RegT _bitmap;
    private int _nextEntryId = 1;
    private ScreenItem? _screenItem;

    public ScrollWindow(Sci32Graphics g, SciRect gameRect, SciPoint position, RegT plane, byte defaultForeColor, byte defaultBackColor, int defaultFontId, TextAlign defaultAlignment, int defaultBorderColor, int maxNumEntries)
    {
        _g = g;
        _text32 = new GfxText32(g);
        _maxNumEntries = maxNumEntries;
        _plane = plane;
        _foreColor = defaultForeColor;
        _backColor = defaultBackColor;
        _borderColor = defaultBorderColor;
        _fontId = defaultFontId;
        _alignment = defaultAlignment;
        _position = position;

        _text32.SetFont(_fontId);
        _pointSize = _text32.Font.Height;

        int scriptWidth = g.Frameout.ScriptWidth;
        int scriptHeight = g.Frameout.ScriptHeight;

        var bitmapRect = gameRect;
        Rect32Math.MulInc(ref bitmapRect, new Ratio(_text32.XResolution, scriptWidth), new Ratio(_text32.YResolution, scriptHeight));

        _textRect = new SciRect(2, 2, bitmapRect.Width - 2, bitmapRect.Height - 2);

        ComputeLineIndices();

        byte skipColor = 0;
        while (skipColor == _foreColor || skipColor == _backColor)
        {
            skipColor++;
        }

        if (bitmapRect.Width <= 0 || bitmapRect.Height <= 0)
        {
            throw new SciVmException($"ScrollWindow: empty rectangle {gameRect}");
        }

        _bitmap = _text32.CreateFontBitmap(bitmapRect.Width, bitmapRect.Height, _textRect, string.Empty, _foreColor, _backColor, skipColor, _fontId, _alignment, _borderColor, false, false, false);
        g.Log.Debug("sci.gfx", $"New ScrollWindow: textRect size: {_textRect.Width} x {_textRect.Height}, bitmap: {_bitmap}");
    }

    public RegT Bitmap => _bitmap;

    /// <summary>Frees the bitmap; the frameout owns the screen item.</summary>
    public void Dispose() => _g.SegMan.FreeBitmap(_bitmap);

    public Ratio Where() => new(_topVisibleLine, Math.Max(_numLines, 1));

    public void Show()
    {
        if (_visible)
        {
            return;
        }

        if (_screenItem is null)
        {
            var celInfo = new CelInfo32 { Type = CelType.Mem, Bitmap = _bitmap };
            _screenItem = new ScreenItem(_g, _plane, celInfo, _position, new ScaleInfo());
        }

        var plane = _g.Frameout.Planes.FindByObject(_plane)
            ?? throw new SciVmException($"[ScrollWindow::show]: Plane {_plane} not found");
        plane.ScreenItemList.Add(_screenItem);
        _visible = true;
    }

    public void Hide()
    {
        if (!_visible)
        {
            return;
        }

        _g.Frameout.DeleteScreenItem(_screenItem!, _plane);
        _screenItem = null;
        _g.Frameout.FrameOut(true);
        _visible = false;
    }

    public RegT Add(string text, int fontId, int foreColor, TextAlign alignment, bool scrollTo)
    {
        if (_entries.Count == _maxNumEntries)
        {
            var removedEntry = _entries[0];
            _entries.RemoveAt(0);
            _text = _text[removedEntry.Text.Length..];
            // the first visible character is reset shortly when scrolling to the new entry
            if (!scrollTo)
            {
                _firstVisibleChar -= removedEntry.Text.Length;
            }
        }

        var entry = new ScrollWindowEntry();
        _entries.Add(entry);

        // Sierra used the memory handle of the line's string as its id
        entry.Id = RegT.Make(0, _nextEntryId++);
        if (_nextEntryId > _maxNumEntries)
        {
            _nextEntryId = 1;
        }

        if (scrollTo)
        {
            _firstVisibleChar = _text.Length;
        }

        FillEntry(entry, text, fontId, foreColor, alignment);
        _text += entry.Text;

        ComputeLineIndices();
        Update(true);
        return entry.Id;
    }

    private static void FillEntry(ScrollWindowEntry entry, string text, int fontId, int foreColor, TextAlign alignment)
    {
        entry.Alignment = alignment;
        entry.ForeColor = foreColor;
        entry.FontId = fontId;

        // the property codes only go on the first line of an entry, as in Sierra's interpreter;
        // Sierra also added a "|s<line>|" code that the text renderer never understood
        var formatted = new StringBuilder();
        if (entry.FontId != -1)
        {
            formatted.Append($"|f{entry.FontId}|");
        }

        if (entry.ForeColor != -1)
        {
            formatted.Append($"|c{entry.ForeColor}|");
        }

        if ((int)entry.Alignment != -1)
        {
            formatted.Append($"|a{(int)entry.Alignment}|");
        }

        formatted.Append(text);
        entry.Text = formatted.ToString();
    }

    public RegT Modify(RegT id, string text, int fontId, int foreColor, TextAlign alignment, bool scrollTo)
    {
        int firstCharLocation = 0;
        ScrollWindowEntry? entry = null;
        foreach (var candidate in _entries)
        {
            if (candidate.Id == id)
            {
                entry = candidate;
                break;
            }

            firstCharLocation += candidate.Text.Length;
        }

        if (entry is null)
        {
            return RegT.Make(0, 0);
        }

        int oldTextLength = entry.Text.Length;
        FillEntry(entry, text, fontId, foreColor, alignment);
        _text = _text[..firstCharLocation] + entry.Text + _text[(firstCharLocation + oldTextLength)..];

        if (scrollTo)
        {
            _firstVisibleChar = firstCharLocation;
        }

        ComputeLineIndices();
        Update(true);
        return entry.Id;
    }

    private string LineText(int line) => _text[_startsOfLines[line]..Math.Max(_startsOfLines[line], _startsOfLines[line + 1] - 1)];

    public void UpArrow()
    {
        if (_topVisibleLine == 0)
        {
            return;
        }

        _topVisibleLine--;
        _bottomVisibleLine--;
        if (_bottomVisibleLine - _topVisibleLine + 1 < _numVisibleLines)
        {
            _bottomVisibleLine = _numLines - 1;
        }

        _firstVisibleChar = _startsOfLines[_topVisibleLine];
        _lastVisibleChar = _startsOfLines[_bottomVisibleLine + 1] - 1;
        _visibleText = _text[_firstVisibleChar..(_lastVisibleChar + 1)];

        string lineText = LineText(_topVisibleLine);
        _text32.ScrollLine(lineText, _numVisibleLines, _foreColor, _alignment, _fontId, ScrollDirection.Up);

        if (_visible)
        {
            _screenItem!.Update();
            _g.Frameout.FrameOut(true);
        }
    }

    public void DownArrow()
    {
        if (_topVisibleLine + 1 >= _numLines)
        {
            return;
        }

        _topVisibleLine++;
        _bottomVisibleLine++;
        if (_bottomVisibleLine + 1 >= _numLines)
        {
            _bottomVisibleLine = _numLines - 1;
        }

        _firstVisibleChar = _startsOfLines[_topVisibleLine];
        _lastVisibleChar = _startsOfLines[_bottomVisibleLine + 1] - 1;
        _visibleText = _text[_firstVisibleChar..(_lastVisibleChar + 1)];

        // an empty line scrolls in when the window is not full
        string lineText = _bottomVisibleLine - _topVisibleLine + 1 == _numVisibleLines ? LineText(_bottomVisibleLine) : string.Empty;
        _text32.ScrollLine(lineText, _numVisibleLines, _foreColor, _alignment, _fontId, ScrollDirection.Down);

        if (_visible)
        {
            _screenItem!.Update();
            _g.Frameout.FrameOut(true);
        }
    }

    public void Go(Ratio location)
    {
        int line = (location * _numLines).ToInt();
        if (line < 0 || line > _numLines)
        {
            throw new SciVmException("Index is Out of Range in ScrollWindow");
        }

        _firstVisibleChar = _startsOfLines[line];
        Update(true);

        // dragging Phantasmagoria's About slider to the very end must put the top line past
        // the end, which Update never does
        if (location.IsOne)
        {
            _topVisibleLine = _numLines;
        }
    }

    public void Home()
    {
        if (_firstVisibleChar == 0)
        {
            return;
        }

        _firstVisibleChar = 0;
        Update(true);
    }

    public void End()
    {
        if (_bottomVisibleLine + 1 >= _numLines)
        {
            return;
        }

        int line = Math.Max(0, _numLines - _numVisibleLines);
        _firstVisibleChar = _startsOfLines[line];
        Update(true);
    }

    public void PageUp()
    {
        if (_topVisibleLine == 0)
        {
            return;
        }

        _topVisibleLine = Math.Max(0, _topVisibleLine - _numVisibleLines);
        _firstVisibleChar = _startsOfLines[_topVisibleLine];
        Update(true);
    }

    public void PageDown()
    {
        if (_topVisibleLine + 1 >= _numLines)
        {
            return;
        }

        _topVisibleLine += _numVisibleLines;
        if (_topVisibleLine + 1 >= _numLines)
        {
            _topVisibleLine = _numLines - 1;
        }

        _firstVisibleChar = _startsOfLines[_topVisibleLine];
        Update(true);
    }

    private void ComputeLineIndices()
    {
        _text32.SetFont(_fontId);
        // the colour and alignment do not change line widths, so they are not set here
        if (_text32.Font.Height != _pointSize)
        {
            throw new SciVmException($"Illegal font size font = {_fontId} pointSize = {_text32.Font.Height}, should be {_pointSize}.");
        }

        var lineRect = new SciRect(0, 0, _textRect.Width, _pointSize + 3);
        _startsOfLines.Clear();

        // Sierra had a 1000 line limit
        for (int charIndex = 0; charIndex < _text.Length;)
        {
            _startsOfLines.Add(charIndex);
            int count = _text32.GetTextCount(_text, charIndex, lineRect, false);
            charIndex += Math.Max(count, 1);
        }

        _numLines = _startsOfLines.Count;
        _startsOfLines.Add(_text.Length);

        _lastVisibleChar = _text32.GetTextCount(_text, 0, _fontId, _textRect, false) - 1;

        _bottomVisibleLine = 0;
        while (_bottomVisibleLine < _numLines - 1 && _startsOfLines[_bottomVisibleLine + 1] < _lastVisibleChar)
        {
            _bottomVisibleLine++;
        }

        _numVisibleLines = _bottomVisibleLine + 1;
    }

    private void Update(bool doFrameOut)
    {
        _topVisibleLine = 0;
        while (_topVisibleLine < _numLines - 1 && _firstVisibleChar >= _startsOfLines[_topVisibleLine + 1])
        {
            _topVisibleLine++;
        }

        _bottomVisibleLine = _topVisibleLine + _numVisibleLines - 1;
        if (_bottomVisibleLine >= _numLines)
        {
            _bottomVisibleLine = _numLines - 1;
        }

        _firstVisibleChar = _startsOfLines[_topVisibleLine];
        _lastVisibleChar = _bottomVisibleLine >= 0 ? _startsOfLines[_bottomVisibleLine + 1] - 1 : -1;
        _visibleText = _lastVisibleChar >= _firstVisibleChar ? _text[_firstVisibleChar..(_lastVisibleChar + 1)] : string.Empty;

        _text32.Erase(_textRect, false);
        _text32.DrawTextBox(_visibleText);

        if (_visible)
        {
            _screenItem!.Update();
            if (doFrameOut)
            {
                _g.Frameout.FrameOut(true);
            }
        }
    }
}

/// <summary>
/// The SCI32 controls: the text fields of kEditText and kInputText, the scroll windows and the
/// message box. Port of ScummVM's GfxControls32.
/// </summary>
public sealed class GfxControls32
{
    private const int KeyEtx = 3;
    private const int KeyBackspace = 8;
    private const int KeyTab = 9;
    private const int KeyEnter = 13;
    private const int KeyEsc = 27;
    private const int KeyShiftTab = 0x0f00;
    private const int KeyInsert = 0x5200;
    private const int KeyDelete = 0x5300;

    private const int MessageBoxOk = 0;
    private const int MessageBoxYesNo = 4;

    private readonly Sci32Graphics _g;
    private bool _overwriteMode;
    private uint _nextCursorFlashTick;
    private int _nextScrollWindowId = 10000;
    private readonly Dictionary<int, ScrollWindow> _scrollWindows = [];
    private bool _reportedMessageBox;

    public GfxControls32(Sci32Graphics g)
    {
        _g = g;
    }

    private GfxText32 Text => _g.Text;

    private GfxFrameout Frameout => _g.Frameout;

    private EngineState State => _g.State;

    // ---- the text input control ----

    /// <summary>Edits a control's text in place until the field loses focus. Returns whether the text changed.</summary>
    public async ValueTask<RegT> KernelEditText(RegT controlObject)
    {
        var s = State;
        var sel = s.Kernel.Selectors;
        var editor = new TextEditor();
        var textObject = Selectors.ReadSelector(s, controlObject, sel.Text);
        editor.Text.Append(s.SegMan.GetString(textObject));
        editor.ForeColor = (byte)Selectors.ReadSelectorValue(s, controlObject, sel.Fore);
        editor.BackColor = (byte)Selectors.ReadSelectorValue(s, controlObject, sel.Back);
        editor.SkipColor = (byte)Selectors.ReadSelectorValue(s, controlObject, sel.Skip);
        editor.FontId = (short)Selectors.ReadSelectorValue(s, controlObject, sel.Font);
        editor.MaxLength = Selectors.ReadSelectorValue(s, controlObject, sel.Width);
        editor.Bitmap = Selectors.ReadSelector(s, controlObject, sel.Bitmap);
        editor.CursorCharPosition = 0;
        editor.CursorIsDrawn = false;
        editor.BorderColor = (short)Selectors.ReadSelectorValue(s, controlObject, sel.BorderColor);

        var titleObject = Selectors.ReadSelector(s, controlObject, sel.Title);
        int titleHeight = 0;
        int titleFontId = (short)Selectors.ReadSelectorValue(s, controlObject, sel.TitleFont);
        if (!titleObject.IsNull)
        {
            var titleFont = _g.GetFont(titleFontId) ?? throw new SciVmException($"kEditText: font {titleFontId} not found");
            titleHeight += Text.ScaleUpHeight(titleFont.Height) + 1;
            if (editor.BorderColor != -1)
            {
                titleHeight += 2;
            }
        }

        int width = 0;
        int height = titleHeight;

        var editorFont = _g.GetFont(editor.FontId) ?? throw new SciVmException($"kEditText: font {editor.FontId} not found");
        height += Text.ScaleUpHeight(editorFont.Height) + 1;
        Text.SetFont(editor.FontId);
        int emSize = Text.GetCharWidth('M', true);
        width += editor.MaxLength * emSize + 1;
        if (editor.BorderColor != -1)
        {
            width += 4;
            height += 2;
        }

        var editorPlaneRect = new SciRect(width, height);
        editorPlaneRect.Translate(Selectors.ReadSelectorValue(s, controlObject, sel.X), Selectors.ReadSelectorValue(s, controlObject, sel.Y));

        var planeObj = Selectors.ReadSelector(s, controlObject, sel.Plane);
        var sourcePlane = Frameout.VisiblePlanes.FindByObject(planeObj)
            ?? Frameout.Planes.FindByObject(planeObj)
            ?? throw new SciVmException($"Could not find plane {planeObj}");
        editorPlaneRect.Translate(sourcePlane.GameRect.Left, sourcePlane.GameRect.Top);

        editor.TextRect = new SciRect(2, titleHeight + 2, width - 1, height - 1);
        editor.Width = width;

        if (editor.Bitmap.IsNull)
        {
            var alignment = (TextAlign)(short)Selectors.ReadSelectorValue(s, controlObject, sel.Mode);
            if (titleObject.IsNull)
            {
                bool dimmed = Selectors.ReadSelectorValue(s, controlObject, sel.Dimmed) != 0;
                editor.Bitmap = Text.CreateFontBitmap(width, height, editor.TextRect, editor.Text.ToString(), editor.ForeColor, editor.BackColor, editor.SkipColor, editor.FontId, alignment, editor.BorderColor, dimmed, true, false);
            }
            else
            {
                throw new SciVmException("Titled bitmaps are not known to be used by any game");
            }
        }

        DrawCursor(editor);

        var plane = new Plane32(_g, editorPlaneRect, Plane32.PlanePicTransparent);
        plane.ChangePic();
        Frameout.AddPlane(plane);

        var celInfo = new CelInfo32 { Type = CelType.Mem, Bitmap = editor.Bitmap };
        var screenItem = new ScreenItem(_g, plane.Object, celInfo, new SciPoint(0, 0), new ScaleInfo());
        plane.ScreenItemList.Add(screenItem);

        // the frame must go out after the item is created and before it is updated, or the item
        // carries both the created and updated flags at once
        Frameout.FrameOut(true);

        bool textChanged = false;
        for (; ; )
        {
            // the last event has to reach the normal event handling as well, so it is peeked
            // and only taken once it is known not to be a defocusing one
            var ev = s.Events.GetSciEvent(KernelFunctions.SciEventAny | KernelFunctions.SciEventPeek);

            bool focused = true;
            if (ev.Type == KernelFunctions.SciEventQuit)
            {
                focused = false;
            }
            else if (ev.Type == KernelFunctions.SciEventMousePress && !editorPlaneRect.Contains(ScriptMousePos(ev.MousePos).X, ScriptMousePos(ev.MousePos).Y))
            {
                focused = false;
            }
            else if (ev.Type == KernelFunctions.SciEventKeyDown)
            {
                switch (ev.Character)
                {
                    case KeyEsc:
                    case KernelFunctions.SciKeyUp:
                    case KernelFunctions.SciKeyDown:
                    case KeyTab:
                    case KeyShiftTab:
                    case KeyEnter:
                        focused = false;
                        break;
                }
            }

            if (!focused)
            {
                break;
            }

            if (ev.Type != KernelFunctions.SciEventNone)
            {
                s.Events.GetSciEvent(KernelFunctions.SciEventAny);
            }

            if (await ProcessEditTextEvent(ev, editor, screenItem))
            {
                textChanged = true;
            }
        }

        Frameout.DeletePlane(plane);
        if (Selectors.ReadSelectorValue(s, controlObject, sel.FrameOut) != 0)
        {
            Frameout.FrameOut(true);
        }

        s.SegMan.FreeBitmap(editor.Bitmap);

        if (textChanged)
        {
            s.SegMan.LookupArray(textObject).FromString(editor.Text.ToString().Trim());
        }

        return RegT.Make(0, textChanged ? 1 : 0);
    }

    /// <summary>A titled text prompt in the middle of the screen (Phantasmagoria 2's easter eggs). Returns whether Enter was pressed.</summary>
    public async ValueTask<RegT> KernelInputText(RegT textObject, RegT titleObject, int maxTextLength)
    {
        var s = State;
        var editor = new TextEditor();
        editor.Text.Append(s.SegMan.GetString(textObject));
        editor.ForeColor = 0;
        editor.BackColor = 255;
        editor.SkipColor = 250;
        editor.FontId = -1;
        editor.MaxLength = maxTextLength;
        editor.CursorCharPosition = 0;
        editor.CursorIsDrawn = false;
        editor.BorderColor = 0;

        string title = s.SegMan.GetString(titleObject);
        Text.SetFont(editor.FontId);
        var systemFont = _g.GetFont(editor.FontId)!;
        int textWidth = Text.GetCharWidth('M', true) * maxTextLength;
        int titleWidth = Text.GetStringWidth(title);
        int textHeight = Text.ScaleUpHeight(systemFont.Height);
        int width = Math.Max(textWidth, titleWidth) + 4;
        int height = textHeight * 2 + 7;

        // Sierra did not scale the position when centring, so the box sits on the left half in Phantasmagoria 2
        var editorPlaneRect = new SciRect(width, height);
        editorPlaneRect.Translate((320 - width) / 2, (200 - height) / 2);
        editor.TextRect = new SciRect(1, height / 2 + 1, width - 1, height - 1);
        editor.Width = width;

        editor.Bitmap = Text.CreateTitledFontBitmap(width, height, editor.TextRect, editor.Text.ToString(), editor.ForeColor, editor.BackColor, editor.SkipColor, editor.FontId, TextAlign.Left, editor.BorderColor, title, editor.BackColor, editor.ForeColor, editor.FontId, true, true);

        DrawCursor(editor);

        var plane = new Plane32(_g, editorPlaneRect, Plane32.PlanePicTransparent);
        plane.ChangePic();
        Frameout.AddPlane(plane);

        var celInfo = new CelInfo32 { Type = CelType.Mem, Bitmap = editor.Bitmap };
        var screenItem = new ScreenItem(_g, plane.Object, celInfo, new SciPoint(0, 0), new ScaleInfo());
        plane.ScreenItemList.Add(screenItem);

        Frameout.FrameOut(true);

        bool wasEnterPressed = false;
        for (; ; )
        {
            var ev = s.Events.GetSciEvent(KernelFunctions.SciEventAny | KernelFunctions.SciEventPeek);
            bool exitEventLoop = false;
            if (ev.Type == KernelFunctions.SciEventQuit)
            {
                exitEventLoop = true;
            }
            else if (ev.Type == KernelFunctions.SciEventKeyDown)
            {
                switch (ev.Character)
                {
                    case KeyEsc:
                        exitEventLoop = true;
                        break;
                    case KeyEnter:
                        wasEnterPressed = true;
                        exitEventLoop = true;
                        break;
                }
            }

            // every event but a quit is consumed, so the engine can still quit
            if (ev.Type != KernelFunctions.SciEventNone && ev.Type != KernelFunctions.SciEventQuit)
            {
                s.Events.GetSciEvent(KernelFunctions.SciEventAny);
            }

            if (exitEventLoop)
            {
                break;
            }

            await ProcessEditTextEvent(ev, editor, screenItem);
        }

        Frameout.DeletePlane(plane);
        Frameout.FrameOut(true);
        s.SegMan.FreeBitmap(editor.Bitmap);

        s.SegMan.LookupArray(textObject).FromString(editor.Text.ToString().Trim());
        return RegT.Make(0, wasEnterPressed ? 1 : 0);
    }

    private SciPoint ScriptMousePos(SciPoint screenPos)
    {
        var pos = screenPos;
        Rect32Math.MulRu(ref pos, new Ratio(Frameout.ScriptWidth, Frameout.ScreenWidth), new Ratio(Frameout.ScriptHeight, Frameout.ScreenHeight));
        return pos;
    }

    /// <summary>Applies one event to the field, redraws what changed and shows the frame. Returns whether the text changed.</summary>
    private async ValueTask<bool> ProcessEditTextEvent(SciEvent ev, TextEditor editor, ScreenItem screenItem)
    {
        bool textChanged = false;
        bool shouldDeleteChar = false;
        bool shouldRedrawText = false;
        int lastCursorPosition = editor.CursorCharPosition;
        var text = editor.Text;

        if (ev.Type == KernelFunctions.SciEventKeyDown)
        {
            switch (ev.Character)
            {
                case KernelFunctions.SciKeyLeft:
                    editor.ClearTextOnInput = false;
                    if (editor.CursorCharPosition > 0)
                    {
                        editor.CursorCharPosition--;
                    }

                    break;
                case KernelFunctions.SciKeyRight:
                    editor.ClearTextOnInput = false;
                    if (editor.CursorCharPosition < text.Length)
                    {
                        editor.CursorCharPosition++;
                    }

                    break;
                case KernelFunctions.SciKeyHome:
                    editor.ClearTextOnInput = false;
                    editor.CursorCharPosition = 0;
                    break;
                case KernelFunctions.SciKeyEnd:
                    editor.ClearTextOnInput = false;
                    editor.CursorCharPosition = text.Length;
                    break;
                case KeyInsert:
                    editor.ClearTextOnInput = false;
                    // redrawing also moves the cursor rectangle to the new insertion mode
                    shouldRedrawText = true;
                    _overwriteMode = !_overwriteMode;
                    break;
                case KeyDelete:
                    editor.ClearTextOnInput = false;
                    if (editor.CursorCharPosition < text.Length)
                    {
                        shouldDeleteChar = true;
                    }

                    break;
                case KeyBackspace:
                    editor.ClearTextOnInput = false;
                    shouldDeleteChar = true;
                    if (editor.CursorCharPosition > 0)
                    {
                        editor.CursorCharPosition--;
                    }

                    break;
                case KeyEtx:
                    text.Clear();
                    editor.CursorCharPosition = 0;
                    shouldRedrawText = true;
                    break;
                default:
                    if (ev.Character is >= 20 and < 257)
                    {
                        if (editor.ClearTextOnInput)
                        {
                            editor.ClearTextOnInput = false;
                            text.Clear();
                        }

                        char character = (char)ev.Character;
                        if ((_overwriteMode && editor.CursorCharPosition < editor.MaxLength)
                            || (text.Length < editor.MaxLength && Text.GetCharWidth(character, true) + Text.GetStringWidth(text.ToString()) < editor.TextRect.Width))
                        {
                            if (_overwriteMode && editor.CursorCharPosition < text.Length)
                            {
                                text[editor.CursorCharPosition] = character;
                            }
                            else
                            {
                                text.Insert(editor.CursorCharPosition, character);
                            }

                            editor.CursorCharPosition++;
                            shouldRedrawText = true;
                        }
                    }

                    break;
            }
        }

        if (shouldDeleteChar)
        {
            shouldRedrawText = true;
            if (editor.CursorCharPosition < text.Length)
            {
                text.Remove(editor.CursorCharPosition, 1);
            }
        }

        if (shouldRedrawText)
        {
            EraseCursor(editor);
            Text.Erase(editor.TextRect, true);
            Text.DrawTextBox(text.ToString());
            DrawCursor(editor);
            textChanged = true;
            screenItem.Updated = Frameout.ScreenCount;
        }
        else if (editor.CursorCharPosition != lastCursorPosition)
        {
            EraseCursor(editor);
            DrawCursor(editor);
            screenItem.Updated = Frameout.ScreenCount;
        }
        else
        {
            FlashCursor(editor);
            screenItem.Updated = Frameout.ScreenCount;
        }

        Frameout.FrameOut(true);
        await State.SpeedThrottler((uint)Frameout.Throttle());
        State.ThrottleTrigger = true;
        return textChanged;
    }

    private void DrawCursor(TextEditor editor)
    {
        if (!editor.CursorIsDrawn)
        {
            string text = editor.Text.ToString();
            editor.CursorRect.Left = editor.TextRect.Left + Text.GetTextWidth(text, 0, editor.CursorCharPosition);

            int scaledFontHeight = Text.ScaleUpHeight(Text.Font.Height);
            if (_overwriteMode)
            {
                editor.CursorRect.Top = editor.TextRect.Top;
                editor.CursorRect.SetHeight(scaledFontHeight);
            }
            else
            {
                editor.CursorRect.Top = editor.TextRect.Top + scaledFontHeight - 1;
                editor.CursorRect.SetHeight(1);
            }

            char currentChar = editor.CursorCharPosition < text.Length ? text[editor.CursorCharPosition] : ' ';
            editor.CursorRect.SetWidth(Text.GetCharWidth(currentChar, true));

            Text.InvertRect(editor.Bitmap, editor.Width, editor.CursorRect, editor.ForeColor, editor.BackColor, true);
            editor.CursorIsDrawn = true;
        }

        _nextCursorFlashTick = _g.TickCount + 30;
    }

    private void EraseCursor(TextEditor editor)
    {
        if (editor.CursorIsDrawn)
        {
            Text.InvertRect(editor.Bitmap, editor.Width, editor.CursorRect, editor.ForeColor, editor.BackColor, true);
            editor.CursorIsDrawn = false;
        }

        _nextCursorFlashTick = _g.TickCount + 30;
    }

    private void FlashCursor(TextEditor editor)
    {
        if (_g.TickCount > _nextCursorFlashTick)
        {
            Text.InvertRect(editor.Bitmap, editor.Width, editor.CursorRect, editor.ForeColor, editor.BackColor, true);
            editor.CursorIsDrawn = !editor.CursorIsDrawn;
            _nextCursorFlashTick = _g.TickCount + 30;
        }
    }

    // ---- the scroll windows ----

    public RegT MakeScrollWindow(SciRect gameRect, SciPoint position, RegT plane, byte defaultForeColor, byte defaultBackColor, int defaultFontId, TextAlign defaultAlignment, int defaultBorderColor, int maxNumEntries)
    {
        var scrollWindow = new ScrollWindow(_g, gameRect, position, plane, defaultForeColor, defaultBackColor, defaultFontId, defaultAlignment, defaultBorderColor, maxNumEntries);
        int id = _nextScrollWindowId++;
        _scrollWindows[id] = scrollWindow;
        return RegT.Make(0, id);
    }

    public ScrollWindow GetScrollWindow(RegT id)
        => _scrollWindows.TryGetValue(id.ToUInt16(), out var window) ? window : throw new SciVmException("Invalid ScrollWindow ID");

    public void DestroyScrollWindow(RegT id)
    {
        var scrollWindow = GetScrollWindow(id);
        scrollWindow.Hide();
        _scrollWindows.Remove(id.ToUInt16());
        scrollWindow.Dispose();
    }

    // ---- the message box ----

    /// <summary>
    /// kMessageBox showed a Windows dialog; without one, the box is logged and answered with
    /// its default button (OK, or Yes).
    /// </summary>
    public RegT KernelMessageBox(string message, string title, int style)
    {
        int result;
        switch (style & 0xF)
        {
            case MessageBoxOk:
                result = 1;
                break;
            case MessageBoxYesNo:
                result = 6;
                break;
            default:
                throw new SciVmException($"Unsupported MessageBox style 0x{style & 0xF:x}");
        }

        if (!_reportedMessageBox)
        {
            _reportedMessageBox = true;
            _g.Log.Warning("sci.gfx", "kMessageBox has no dialog here; boxes are answered with their default button");
        }

        _g.Log.Info("sci.gfx", $"MessageBox \"{title}\": {message} -> {result}");
        return RegT.Make(0, result);
    }
}
