using Daventry.Core.Hosting;
using Daventry.Sci.Engine;

namespace Daventry.Sci.Graphics;

/// <summary>A pull-down menu title. Mirrors GuiMenuEntry.</summary>
public sealed class MenuEntry(int id)
{
    public int Id { get; } = id;
    public string Text = string.Empty;
    public string TextSplit = string.Empty;
    public int TextWidth;
}

/// <summary>An item of a pull-down menu. Mirrors GuiMenuItemEntry.</summary>
public sealed class MenuItemEntry(int menuId, int id)
{
    public int MenuId { get; } = menuId;
    public int Id { get; } = id;
    public bool Enabled = true;
    public int Tag;
    public int KeyPress;
    public int KeyModifier;
    public bool SeparatorLine;
    public RegT SaidVmPtr = RegT.Null;
    public string Text = string.Empty;
    public string TextSplit = string.Empty;
    public RegT TextVmPtr = RegT.Null;
    public int TextWidth;
    public string TextRightAligned = string.Empty;
    public int TextRightAlignedWidth;
}

/// <summary>
/// The menu bar, its pull-down menus and the status line of SCI0 to SCI1.1 games. Port of
/// ScummVM's menu.cpp without the right-to-left paths; the Said matching of menu items waits
/// for the parser, and sounds are not paused while a menu is open because sound is not ported.
/// </summary>
public sealed class GfxMenu
{
    public const int AttributeSaid = 0x6d;
    public const int AttributeText = 0x6e;
    public const int AttributeKeyPress = 0x6f;
    public const int AttributeEnabled = 0x70;
    public const int AttributeTag = 0x71;

    private const char ReplaceOnControl = (char)0x03;
    private const char ReplaceOnAlt = (char)0x02;
    private const char ReplaceOnFunction = 'F';

    private const int KeyEsc = 27;
    private const int KeyEnter = 13;
    private const int KeyTab = 9;

    private readonly EngineState _s;
    private readonly SciEventManager _event;
    private readonly SegManager _segMan;
    private readonly GfxPorts _ports;
    private readonly GfxPaint16 _paint16;
    private readonly GfxText16 _text16;
    private readonly GfxScreen _screen;
    private readonly ILog _log;
    private readonly List<MenuEntry> _list = [];
    private readonly List<MenuItemEntry> _itemList = [];
    private int _curMenuId;
    private int _curItemId;
    private Port? _oldPort;
    private RegT _barSaveHandle = RegT.Null;
    private RegT _menuSaveHandle = RegT.Null;
    private SciRect _menuRect;

    public GfxMenu(EngineState s)
    {
        _s = s;
        _event = s.Events;
        _segMan = s.SegMan;
        _ports = s.Ports;
        _paint16 = s.Paint;
        _text16 = s.Text;
        _screen = s.Screen;
        _log = s.Log;
        Reset();
    }

    public void Reset()
    {
        _list.Clear();
        _itemList.Clear();
        // the last selection is remembered; Sierra always went back to the first item
        _curMenuId = 1;
        _curItemId = 1;
    }

    /// <summary>Adds a menu with its items; the content string has one item per ':'. Port of kernelAddEntry.</summary>
    public void KernelAddEntry(string title, string content, RegT contentVmPtr)
    {
        int itemCount = 0;
        var chars = content.ToCharArray();
        int contentSize = chars.Length;

        // Sierra starts with id 1
        var menuEntry = new MenuEntry(_list.Count + 1) { Text = title };
        _list.Add(menuEntry);

        int curPos = 0;
        int listSize = _list.Count;

        do
        {
            itemCount++;
            var itemEntry = new MenuItemEntry(listSize, itemCount);
            int beginPos = curPos;

            // collect the markers up to the ':' that ends the item
            int tagPos = 0;
            int rightAlignedPos = 0;
            int controlPos = 0;
            int altPos = 0;
            int functionPos = 0;
            while (curPos < contentSize && chars[curPos] != ':')
            {
                switch (chars[curPos])
                {
                    case '=': // tag; a right-aligned "=" is the normal animation speed, not a tag
                        if (rightAlignedPos == curPos - 1)
                        {
                            break;
                        }

                        if (tagPos != 0)
                        {
                            throw new SciVmException("multiple tag markers within one menu-item");
                        }

                        tagPos = curPos;
                        break;
                    case '`': // right-aligned (SQ3 Russian has two in a row; tolerated)
                        rightAlignedPos = curPos;
                        break;
                    case '^': // Ctrl prefix
                        if (controlPos != 0)
                        {
                            throw new SciVmException("multiple control markers within one menu-item");
                        }

                        controlPos = curPos;
                        break;
                    case '@': // Alt prefix
                        if (altPos != 0)
                        {
                            throw new SciVmException("multiple alt markers within one menu-item");
                        }

                        altPos = curPos;
                        break;
                    case '#': // function key prefix, only after '`' since #G is a language separator (SQ3 German Amiga)
                        if (rightAlignedPos != 0)
                        {
                            if (functionPos != 0)
                            {
                                throw new SciVmException("multiple function markers within one menu-item");
                            }

                            functionPos = curPos;
                        }

                        break;
                }

                curPos++;
            }

            int endPos = curPos;

            // control, alt and function key mapping
            if (controlPos != 0)
            {
                chars[controlPos] = ReplaceOnControl;
                itemEntry.KeyModifier = KernelFunctions.SciKeyModCtrl;
                int tempPos = controlPos + 1;
                if (tempPos >= contentSize)
                {
                    throw new SciVmException("control marker at end of item");
                }

                itemEntry.KeyPress = char.ToLowerInvariant(chars[tempPos]);
                chars[tempPos] = char.ToUpperInvariant(chars[tempPos]);
            }

            if (altPos != 0)
            {
                chars[altPos] = ReplaceOnAlt;
                itemEntry.KeyModifier = KernelFunctions.SciKeyModAlt;
                int tempPos = altPos + 1;
                if (tempPos >= contentSize)
                {
                    throw new SciVmException("alt marker at end of item");
                }

                itemEntry.KeyPress = char.ToLowerInvariant(chars[tempPos]);
                chars[tempPos] = char.ToUpperInvariant(chars[tempPos]);
            }

            if (functionPos != 0)
            {
                // "#1" becomes "F1"; F10 needs extra handling below
                chars[functionPos] = ReplaceOnFunction;
                int tempPos = functionPos + 1;
                if (tempPos >= contentSize)
                {
                    throw new SciVmException("function marker at end of item");
                }

                itemEntry.KeyPress = chars[tempPos] switch
                {
                    '1' => 59 << 8,
                    '2' => 60 << 8,
                    '3' => 61 << 8,
                    '4' => 62 << 8,
                    '5' => 63 << 8,
                    '6' => 64 << 8,
                    '7' => 65 << 8,
                    '8' => 66 << 8,
                    '9' => 67 << 8,
                    '0' => 68 << 8,
                    _ => throw new SciVmException("illegal function key specified"),
                };
            }

            // the strings
            int textEnd = endPos;
            if (rightAlignedPos != 0)
            {
                textEnd = rightAlignedPos;
            }
            else if (tagPos != 0)
            {
                textEnd = tagPos;
            }

            curPos = beginPos;
            int separatorCount = 0;
            while (curPos < textEnd)
            {
                switch (chars[curPos])
                {
                    case '!':
                    case '-':
                    case ' ':
                        separatorCount++;
                        break;
                    case '%':
                    case '#':
                        // some multilingual SCI01 games use '--!%G--!'
                        separatorCount += 2;
                        curPos++;
                        break;
                }

                curPos++;
            }

            if (separatorCount == textEnd - beginPos)
            {
                itemEntry.SeparatorLine = true;
            }
            else
            {
                // not split for languages here: multilingual SCI01 games switch on the fly, so
                // the split happens whenever the menu is drawn
                itemEntry.Text = new string(chars, beginPos, textEnd - beginPos);

                // LSL6 uses a "Ctrl-" prefix instead of ^
                int ctrl = itemEntry.Text.IndexOf("Ctrl-", StringComparison.Ordinal);
                if (ctrl >= 0 && ctrl + 5 < itemEntry.Text.Length)
                {
                    itemEntry.KeyModifier = KernelFunctions.SciKeyModCtrl;
                    itemEntry.KeyPress = char.ToLowerInvariant(itemEntry.Text[ctrl + 5]);
                }
            }

            itemEntry.TextVmPtr = contentVmPtr.IncOffset(beginPos);

            if (rightAlignedPos != 0)
            {
                rightAlignedPos++;
                int tempPos = endPos;
                // some games have the tag before the right-aligned text, some after (QfG1 EGA)
                if (tagPos != 0 && tagPos >= rightAlignedPos)
                {
                    tempPos = tagPos;
                }

                itemEntry.TextRightAligned = new string(chars, rightAlignedPos, tempPos - rightAlignedPos);
                // some games have a lone space at the end
                if (itemEntry.TextRightAligned.EndsWith(' '))
                {
                    itemEntry.TextRightAligned = itemEntry.TextRightAligned[..^1];
                }

                // - and + are used for volume and animation speed, = for animation speed
                switch (itemEntry.TextRightAligned)
                {
                    case "-":
                        itemEntry.KeyPress = '-';
                        break;
                    case "+":
                        itemEntry.KeyPress = '+';
                        break;
                    case "=":
                        itemEntry.KeyPress = '=';
                        break;
                }

                // "#0" was replaced in place by "F0"; it means F10 (bug #15557)
                if (itemEntry.KeyPress == 68 << 8)
                {
                    int f10Pos = itemEntry.TextRightAligned.IndexOf("F0", StringComparison.Ordinal);
                    if (f10Pos >= 0)
                    {
                        itemEntry.TextRightAligned = itemEntry.TextRightAligned.Insert(f10Pos + 1, "1");
                    }
                }
            }

            if (tagPos != 0)
            {
                int tempPos = functionPos + 1;
                if (tempPos >= contentSize)
                {
                    throw new SciVmException("tag marker at end of item");
                }

                itemEntry.Tag = ParseLeadingInt(chars, tempPos);
            }

            curPos = endPos + 1;
            _itemList.Add(itemEntry);
        }
        while (curPos < contentSize);
    }

    /// <summary>atoi: the leading decimal number at a position, 0 when there is none.</summary>
    private static int ParseLeadingInt(char[] chars, int pos)
    {
        while (pos < chars.Length && chars[pos] == ' ')
        {
            pos++;
        }

        int sign = 1;
        if (pos < chars.Length && (chars[pos] == '-' || chars[pos] == '+'))
        {
            sign = chars[pos] == '-' ? -1 : 1;
            pos++;
        }

        int value = 0;
        while (pos < chars.Length && char.IsAsciiDigit(chars[pos]))
        {
            value = value * 10 + (chars[pos] - '0');
            pos++;
        }

        return sign * value;
    }

    private MenuItemEntry? FindItem(int menuId, int itemId)
    {
        foreach (var entry in _itemList)
        {
            if (entry.MenuId == menuId && entry.Id == itemId)
            {
                return entry;
            }
        }

        return null;
    }

    public void KernelSetAttribute(int menuId, int itemId, int attributeId, RegT value)
    {
        var itemEntry = FindItem(menuId, itemId);
        if (itemEntry is null)
        {
            // the PQ2 demo and some fan games set attributes of items that do not exist; the
            // original ignored that too
            _log.Debug("sci.gfx", $"Tried to setAttribute() on non-existent menu-item {menuId}:{itemId}");
            return;
        }

        switch (attributeId)
        {
            case AttributeEnabled:
                itemEntry.Enabled = !value.IsNull;
                break;
            case AttributeSaid:
                itemEntry.SaidVmPtr = value;
                break;
            case AttributeText:
                itemEntry.Text = _segMan.GetString(value);
                itemEntry.TextVmPtr = value;
                // no script is assumed to create a separator line dynamically
                break;
            case AttributeKeyPress:
                itemEntry.KeyPress = char.ToLowerInvariant((char)value.Offset);
                itemEntry.KeyModifier = 0;
                break;
            case AttributeTag:
                itemEntry.Tag = value.Offset;
                break;
            default:
                // happens when loading a game in LSL3 (attribute 1A)
                _log.Warning("sci.gfx", $"setAttribute() called with unsupported attributeId {attributeId:X}");
                break;
        }
    }

    public RegT KernelGetAttribute(int menuId, int itemId, int attributeId)
    {
        var itemEntry = FindItem(menuId, itemId) ?? throw new SciVmException($"Tried to getAttribute() on non-existent menu-item {menuId}:{itemId}");
        switch (attributeId)
        {
            case AttributeEnabled:
                return itemEntry.Enabled ? RegT.Make(0, 1) : RegT.Null;
            case AttributeSaid:
                return itemEntry.SaidVmPtr;
            case AttributeText:
                return itemEntry.TextVmPtr;
            case AttributeKeyPress:
                return RegT.Make(0, itemEntry.KeyPress);
            case AttributeTag:
                return RegT.Make(0, itemEntry.Tag);
            default:
                throw new SciVmException($"getAttribute() called with unsupported attributeId {attributeId:X}");
        }
    }

    private void DrawBar()
    {
        // hardcoded black on white with a black line below
        _paint16.FillRect(_ports.MenuBarRect, GfxScreenMask.Visual, _screen.ColorWhite, 0, 0);
        _paint16.FillRect(_ports.MenuLine, GfxScreenMask.Visual, 0, 0, 0);
        _ports.PenColor(0);
        _ports.MoveTo(8, 1);
        foreach (var listEntry in _list)
        {
            _text16.DrawString(listEntry.TextSplit);
        }
    }

    private void CalculateMenuWidth()
    {
        foreach (var menuEntry in _list)
        {
            menuEntry.TextSplit = SciLanguage.StrSplit(_s, menuEntry.Text, null);
            _text16.StringWidth(menuEntry.TextSplit, 0, out menuEntry.TextWidth, out _);
        }
    }

    private void CalculateMenuAndItemWidth()
    {
        CalculateMenuWidth();
        foreach (var itemEntry in _itemList)
        {
            // split now, for multilingual SCI01 games
            itemEntry.TextSplit = SciLanguage.StrSplit(_s, itemEntry.Text, null);
            _text16.StringWidth(itemEntry.TextSplit, 0, out itemEntry.TextWidth, out _);
            _text16.StringWidth(itemEntry.TextRightAligned, 0, out itemEntry.TextRightAlignedWidth, out _);
        }
    }

    /// <summary>Handles an event for the menu: a hot key, Escape or a click on the bar. Port of kernelSelect.</summary>
    public async ValueTask<RegT> KernelSelect(RegT eventObject, bool pauseSound)
    {
        var sel = _s.Kernel.Selectors;
        int eventType = Selectors.ReadSelectorValue(_s, eventObject, sel.Type);
        MenuItemEntry? itemEntry = null;
        bool forceClaimed = false;
        _ = pauseSound;

        switch (eventType)
        {
            case KernelFunctions.SciEventKeyDown:
            {
                int keyPress = Selectors.ReadSelectorValue(_s, eventObject, sel.Message);
                int keyModifier = Selectors.ReadSelectorValue(_s, eventObject, sel.Modifiers);

                // Ctrl+key arrives as an ASCII control character; the items are matched on
                // modifier plus printable character
                if ((keyModifier & KernelFunctions.SciKeyModNonSticky) == KernelFunctions.SciKeyModCtrl && keyPress is > 0 and < 27)
                {
                    keyPress += 96;
                }

                switch (keyPress)
                {
                    case 0:
                        break;
                    case KeyEsc:
                        itemEntry = await InteractiveWithKeyboard();
                        forceClaimed = true;
                        break;
                    default:
                        // Tab and Ctrl+I share the ASCII character; SSCI only looked at the
                        // character, so Tab becomes Ctrl+I here (QfG1 EGA inventory)
                        if (keyPress == KeyTab)
                        {
                            keyModifier = KernelFunctions.SciKeyModCtrl;
                            keyPress = 'i';
                        }

                        // only the lower byte, because of the keyboard driver bug (kGetEvent)
                        keyModifier &= 0xFF;
                        foreach (var entry in _itemList)
                        {
                            if (entry.KeyPress == keyPress && entry.KeyModifier == keyModifier && entry.Enabled)
                            {
                                itemEntry = entry;
                                break;
                            }
                        }

                        break;
                }

                break;
            }

            case KernelFunctions.SciEventSaid:
                if (_s.Vocabulary is { } voc)
                {
                    foreach (var entry in _itemList)
                    {
                        if (entry.SaidVmPtr.IsNull || !entry.Enabled)
                        {
                            continue;
                        }

                        var saidSpec = _segMan.DerefBulkPtr(entry.SaidVmPtr, 0);
                        if (saidSpec is null)
                        {
                            _log.Warning("sci.gfx", "Could not dereference saidSpec");
                            continue;
                        }

                        var (buffer, offset) = saidSpec.Value;
                        if (_s.Said.Match(voc, buffer, offset) != Engine.Parser.Said.NoMatch)
                        {
                            itemEntry = entry;
                            break;
                        }
                    }
                }

                break;

            case KernelFunctions.SciEventMousePress:
            {
                int y = (short)Selectors.ReadSelectorValue(_s, eventObject, sel.Y);
                if (y < 10)
                {
                    itemEntry = await InteractiveWithMouse();
                    forceClaimed = true;
                }

                break;
            }
        }

        if (!_menuSaveHandle.IsNull)
        {
            _paint16.BitsRestoreHunk(_menuSaveHandle);
            // the line between the menu bar and the menu
            var menuLine = _menuRect;
            menuLine.Bottom = menuLine.Top + 1;
            _paint16.BitsShow(menuLine);
            _paint16.KernelGraphRedrawBox(_menuRect);
            _menuSaveHandle = RegT.Null;
        }

        if (!_barSaveHandle.IsNull)
        {
            _paint16.BitsRestoreHunk(_barSaveHandle);
            _paint16.BitsShow(_ports.MenuRect);
            _barSaveHandle = RegT.Null;
        }

        if (_oldPort is not null)
        {
            _ports.SetPort(_oldPort);
            _oldPort = null;
        }

        if (itemEntry is not null || forceClaimed)
        {
            Selectors.WriteSelector(_s, eventObject, sel.Claimed, RegT.Make(0, 1));
        }

        return itemEntry is not null ? RegT.Make(0, (itemEntry.MenuId << 8) | itemEntry.Id) : RegT.Null;
    }

    private MenuItemEntry? InteractiveGetItem(int menuId, int itemId, bool menuChanged)
    {
        MenuItemEntry? firstItemEntry = null;
        MenuItemEntry? lastItemEntry = null;

        if (menuId > _list.Count)
        {
            menuId = 1;
        }

        if (menuId == 0)
        {
            menuId = _list.Count;
        }

        foreach (var itemEntry in _itemList)
        {
            if (itemEntry.MenuId == menuId)
            {
                if (itemEntry.Id == itemId)
                {
                    return itemEntry;
                }

                firstItemEntry ??= itemEntry;
                if (lastItemEntry is null || itemEntry.Id > lastItemEntry.Id)
                {
                    lastItemEntry = itemEntry;
                }
            }
        }

        return itemId == 0 || menuChanged ? lastItemEntry : firstItemEntry;
    }

    private void DrawMenu(int oldMenuId, int newMenuId)
    {
        int maxTextWidth = 0;
        int maxTextRightAlignedWidth = 0;

        // remove the menu shown so far
        if (!_menuSaveHandle.IsNull)
        {
            _paint16.BitsRestoreHunk(_menuSaveHandle);
            var menuLine = _menuRect;
            menuLine.Bottom = menuLine.Top + 1;
            _paint16.BitsShow(menuLine);
            _paint16.KernelGraphRedrawBox(_menuRect);
        }

        // the menu rect, and inverting the old and the new title
        _menuRect.Top = _ports.MenuBarRect.Bottom;
        var menuTextRect = new SciRect { Top = _ports.MenuBarRect.Top, Bottom = _ports.MenuBarRect.Bottom, Left = 7, Right = 7 };
        int listNr = 0;
        foreach (var listEntry in _list)
        {
            listNr++;
            menuTextRect.Left = menuTextRect.Right;
            menuTextRect.Right += listEntry.TextWidth;
            if (listNr == newMenuId)
            {
                _menuRect.Left = menuTextRect.Left;
            }

            if (listNr == newMenuId || listNr == oldMenuId)
            {
                menuTextRect.Translate(1, 0);
                _paint16.InvertRect(menuTextRect);
                menuTextRect.Translate(-1, 0);
            }
        }

        _paint16.BitsShow(_ports.MenuBarRect);

        _menuRect.Bottom = _menuRect.Top + 2;
        foreach (var listItemEntry in _itemList)
        {
            if (listItemEntry.MenuId == newMenuId)
            {
                _menuRect.Bottom += _ports.CurPort.FontHeight;
                maxTextWidth = Math.Max(maxTextWidth, listItemEntry.TextWidth);
                maxTextRightAlignedWidth = Math.Max(maxTextRightAlignedWidth, listItemEntry.TextRightAlignedWidth);
            }
        }

        _menuRect.Right = _menuRect.Left + 16 + 4 + 2;
        _menuRect.Right += maxTextWidth + maxTextRightAlignedWidth;
        if (maxTextRightAlignedWidth == 0)
        {
            _menuRect.Right -= 5;
        }

        // a menu partly outside the screen is moved in (multilingual SQ3 and LSL3)
        if (_menuRect.Right > _screen.Width)
        {
            _menuRect.Translate(-(_menuRect.Right - _screen.Width), 0);
        }

        if (_menuRect.Left < 0)
        {
            _log.Warning("sci.gfx", "GfxMenu::drawMenu: _menuRect.left < 0");
        }

        _menuSaveHandle = _paint16.BitsSaveHunk(_menuRect, GfxScreenMask.Visual);

        _paint16.FillRect(_menuRect, GfxScreenMask.Visual, 0, 0, 0);
        _menuRect.Left++;
        _menuRect.Right--;
        _menuRect.Bottom--;
        _paint16.FillRect(_menuRect, GfxScreenMask.Visual, _screen.ColorWhite, 0, 0);

        _menuRect.Left += 8;
        int topPos = _menuRect.Top + 1;
        foreach (var listItemEntry in _itemList)
        {
            if (listItemEntry.MenuId != newMenuId)
            {
                continue;
            }

            if (!listItemEntry.SeparatorLine)
            {
                _ports.TextGreyedOutput(!listItemEntry.Enabled);
                _ports.MoveTo(_menuRect.Left, topPos);
                _text16.DrawString(listItemEntry.TextSplit);
                _ports.MoveTo(_menuRect.Right - listItemEntry.TextRightAlignedWidth - 5, topPos);
                _text16.DrawString(listItemEntry.TextRightAligned);
            }
            else
            {
                // drawn left to right, which looks better than Sierra's pattern
                int y = topPos + (_ports.CurPort.FontHeight >> 1) - 1;
                for (int x = _menuRect.Left - 7; x < _menuRect.Right - 1; x += 2)
                {
                    _screen.PutPixel(x, y, GfxScreenMask.Visual, 0, 0, 0);
                }
            }

            topPos += _ports.CurPort.FontHeight;
        }

        _ports.TextGreyedOutput(false);

        // the black line again
        _paint16.FillRect(_ports.MenuLine, GfxScreenMask.Visual, 0, 0, 0);

        _menuRect.Left -= 8;
        _menuRect.Left--;
        _menuRect.Right++;
        _menuRect.Bottom++;
        _paint16.BitsShow(_menuRect);
    }

    private void InvertMenuSelection(int itemId)
    {
        if (itemId == 0)
        {
            return;
        }

        var itemRect = _menuRect;
        itemRect.Top += (itemId - 1) * _ports.CurPort.FontHeight + 1;
        itemRect.Bottom = itemRect.Top + _ports.CurPort.FontHeight;
        itemRect.Left++;
        itemRect.Right--;

        _paint16.InvertRect(itemRect);
        _paint16.BitsShow(itemRect);
    }

    private int MouseFindMenuSelection(SciPoint mousePosition)
    {
        int curXstart = 8;
        foreach (var listEntry in _list)
        {
            if (mousePosition.X >= curXstart && mousePosition.X < curXstart + listEntry.TextWidth)
            {
                return listEntry.Id;
            }

            curXstart += listEntry.TextWidth;
        }

        return 0;
    }

    private int MouseFindMenuItemSelection(SciPoint mousePosition, int menuId)
    {
        int curYstart = 10;
        int itemId = 0;

        if (menuId == 0)
        {
            return 0;
        }

        if (mousePosition.X < _menuRect.Left || mousePosition.X >= _menuRect.Right)
        {
            return 0;
        }

        foreach (var listItemEntry in _itemList)
        {
            if (listItemEntry.MenuId == menuId)
            {
                curYstart += _ports.CurPort.FontHeight;
                if (itemId == 0 && curYstart > mousePosition.Y)
                {
                    itemId = listItemEntry.Id;
                }
            }
        }

        return itemId;
    }

    /// <summary>The menu opened with Escape: arrows move, Enter selects, the mouse works too.</summary>
    private async ValueTask<MenuItemEntry?> InteractiveWithKeyboard()
    {
        var curItemEntry = FindItem(_curMenuId, _curItemId) ?? InteractiveGetItem(_curMenuId, 1, true);
        if (curItemEntry is null)
        {
            return null; // no menus at all
        }

        // the last selection is kept, and the mouse works while the menu was opened by
        // keyboard, both unlike Sierra
        bool mouseOldState = _s.Cursor.IsVisible;
        _s.Cursor.KernelShow();
        try
        {
            return await InteractiveWithKeyboardCore(curItemEntry);
        }
        finally
        {
            if (!mouseOldState)
            {
                _s.Cursor.KernelHide();
            }
        }
    }

    private async ValueTask<MenuItemEntry?> InteractiveWithKeyboardCore(MenuItemEntry curItemEntry)
    {
        int newMenuId = _curMenuId;
        int newItemId = _curItemId;
        var newItemEntry = curItemEntry;
        _oldPort = _ports.SetPort(_ports.MenuPort);
        CalculateMenuAndItemWidth();
        _barSaveHandle = _paint16.BitsSaveHunk(_ports.MenuRect, GfxScreenMask.Visual);

        _ports.PenColor(0);
        _ports.BackColor(_screen.ColorWhite);

        DrawBar();
        DrawMenu(0, curItemEntry.MenuId);
        InvertMenuSelection(curItemEntry.Id);
        _paint16.BitsShow(_ports.MenuRect);
        _paint16.BitsShow(_menuRect);

        while (true)
        {
            var curEvent = _event.GetSciEvent(KernelFunctions.SciEventAny);
            switch (curEvent.Type)
            {
                case KernelFunctions.SciEventKeyDown:
                {
                    // unlike Sierra the selection wraps around and item 0 is never selected
                    int character = curEvent.Character;
                    do
                    {
                        switch (character)
                        {
                            case KeyEsc:
                                _curMenuId = curItemEntry.MenuId;
                                _curItemId = curItemEntry.Id;
                                return null;
                            case KeyEnter:
                                if (curItemEntry.Enabled)
                                {
                                    _curMenuId = curItemEntry.MenuId;
                                    _curItemId = curItemEntry.Id;
                                    return curItemEntry;
                                }

                                break;
                            case KernelFunctions.SciKeyLeft:
                                newMenuId--;
                                newItemId = 1;
                                break;
                            case KernelFunctions.SciKeyRight:
                                newMenuId++;
                                newItemId = 1;
                                break;
                            case KernelFunctions.SciKeyUp:
                                newItemId--;
                                break;
                            case KernelFunctions.SciKeyDown:
                                newItemId++;
                                break;
                        }

                        if (newMenuId != curItemEntry.MenuId || newItemId != curItemEntry.Id)
                        {
                            // the selection changed; fix it up if needed
                            newItemEntry = InteractiveGetItem(newMenuId, newItemId, newMenuId != curItemEntry.MenuId) ?? curItemEntry;
                            newMenuId = newItemEntry.MenuId;
                            newItemId = newItemEntry.Id;

                            // a separator line repeats the step downwards, not sideways
                            if (character is KernelFunctions.SciKeyLeft or KernelFunctions.SciKeyRight)
                            {
                                character = KernelFunctions.SciKeyDown;
                            }
                        }
                    }
                    while (newItemEntry.SeparatorLine);

                    if (newMenuId != curItemEntry.MenuId || newItemId != curItemEntry.Id)
                    {
                        if (newMenuId != curItemEntry.MenuId)
                        {
                            DrawMenu(curItemEntry.MenuId, newMenuId);
                        }
                        else
                        {
                            InvertMenuSelection(curItemEntry.Id);
                        }

                        InvertMenuSelection(newItemId);
                        curItemEntry = newItemEntry;
                    }

                    break;
                }

                case KernelFunctions.SciEventMousePress:
                {
                    var mousePosition = curEvent.MousePos;
                    if (mousePosition.Y < 10)
                    {
                        // on the menu bar
                        newMenuId = MouseFindMenuSelection(mousePosition);
                        if (newMenuId != 0)
                        {
                            newItemId = 1;
                            newItemEntry = InteractiveGetItem(newMenuId, newItemId, newMenuId != curItemEntry.MenuId) ?? curItemEntry;
                            if (newMenuId != curItemEntry.MenuId)
                            {
                                DrawMenu(curItemEntry.MenuId, newMenuId);
                            }
                            else
                            {
                                InvertMenuSelection(curItemEntry.Id);
                            }

                            InvertMenuSelection(newItemId);
                            curItemEntry = newItemEntry;
                        }
                        else
                        {
                            newMenuId = curItemEntry.MenuId;
                        }
                    }
                    else
                    {
                        // below the menu bar
                        newItemId = MouseFindMenuItemSelection(mousePosition, newMenuId);
                        if (newItemId != 0)
                        {
                            newItemEntry = InteractiveGetItem(newMenuId, newItemId, false) ?? curItemEntry;
                            if (newItemEntry.Enabled && !newItemEntry.SeparatorLine)
                            {
                                _curMenuId = newItemEntry.MenuId;
                                _curItemId = newItemEntry.Id;
                                return newItemEntry;
                            }

                            newItemEntry = curItemEntry;
                        }

                        newItemId = curItemEntry.Id;
                    }

                    break;
                }

                case KernelFunctions.SciEventNone:
                    await _s.SleepMs(2);
                    break;

                case KernelFunctions.SciEventQuit:
                    return null;
            }
        }
    }

    /// <summary>
    /// The mouse button is down on the bar: the menus follow the pointer until the button is
    /// released, which chooses the item under it, if any.
    /// </summary>
    private async ValueTask<MenuItemEntry?> InteractiveWithMouse()
    {
        bool mouseOldState = _s.Cursor.IsVisible;
        _s.Cursor.KernelShow();
        try
        {
            return await InteractiveWithMouseCore();
        }
        finally
        {
            if (!mouseOldState)
            {
                _s.Cursor.KernelHide();
            }
        }
    }

    private async ValueTask<MenuItemEntry?> InteractiveWithMouseCore()
    {
        int newMenuId = 0;
        int newItemId = 0;
        int curMenuId = 0;
        int curItemId = 0;
        bool firstMenuChange = true;
        MenuItemEntry? curItemEntry = null;

        _oldPort = _ports.SetPort(_ports.MenuPort);
        CalculateMenuAndItemWidth();
        _barSaveHandle = _paint16.BitsSaveHunk(_ports.MenuRect, GfxScreenMask.Visual);

        _ports.PenColor(0);
        _ports.BackColor(_screen.ColorWhite);

        DrawBar();
        _paint16.BitsShow(_ports.MenuRect);

        while (true)
        {
            var curEvent = _event.GetSciEvent(KernelFunctions.SciEventAny);
            switch (curEvent.Type)
            {
                case KernelFunctions.SciEventMouseRelease:
                    if (curMenuId == 0 || curItemId == 0 || curItemEntry is null)
                    {
                        return null;
                    }

                    if (!curItemEntry.Enabled || curItemEntry.SeparatorLine)
                    {
                        return null;
                    }

                    return curItemEntry;

                case KernelFunctions.SciEventNone:
                    await _s.SleepMs(2);
                    break;

                case KernelFunctions.SciEventQuit:
                    return null;
            }

            // where the mouse points now
            var mousePosition = curEvent.MousePos;
            if (mousePosition.Y < 10)
            {
                // on the menu bar
                newMenuId = MouseFindMenuSelection(mousePosition);
                newItemId = 0;
            }
            else
            {
                // below the menu bar
                newItemId = MouseFindMenuItemSelection(mousePosition, newMenuId);
                curItemEntry = InteractiveGetItem(curMenuId, newItemId, false);
            }

            if (newItemId != curItemId)
            {
                InvertMenuSelection(curItemId);
                InvertMenuSelection(newItemId);
                curItemId = newItemId;
            }

            if (newMenuId != curMenuId)
            {
                DrawMenu(curMenuId, newMenuId);
                if (firstMenuChange)
                {
                    _paint16.BitsShow(_ports.MenuBarRect);
                    firstMenuChange = false;
                }

                curMenuId = newMenuId;
            }
        }
    }

    public void KernelDrawStatus(string text, int colorPen, int colorBack)
    {
        var oldPort = _ports.SetPort(_ports.MenuPort);
        _paint16.FillRect(_ports.MenuBarRect, GfxScreenMask.Visual, (byte)colorBack, 0, 0);
        _ports.PenColor(colorPen);
        _ports.MoveTo(0, 1);
        _text16.DrawStatus(text);
        _paint16.BitsShow(_ports.MenuBarRect);
        // the line under the status bar; Dr. Brain 1 Mac draws over it with its icon bar
        _paint16.FillRect(_ports.MenuLine, GfxScreenMask.Visual, 0, 0, 0);
        _paint16.BitsShow(_ports.MenuLine);
        _ports.SetPort(oldPort);
    }

    public void KernelDrawMenuBar(bool clear)
    {
        if (!clear)
        {
            var oldPort = _ports.SetPort(_ports.MenuPort);
            CalculateMenuWidth();
            DrawBar();
            _paint16.BitsShow(_ports.MenuBarRect);
            _ports.SetPort(oldPort);
        }
        else
        {
            KernelDrawStatus(string.Empty, 0, 0);
        }
    }
}
