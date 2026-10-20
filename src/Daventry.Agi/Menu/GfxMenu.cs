using Daventry.Agi.Graphics;
using Daventry.Agi.Resources;
using Daventry.Agi.Text;
using Daventry.Core.Hosting;

namespace Daventry.Agi.Menu;

/// <summary>
/// The pull-down menu bar. Port of ScummVM's menu.cpp: scripts build the menus at start-up, the
/// interpreter shows them on Escape or a click on the status line, and choosing an item raises its
/// controller. Right-to-left languages and the Apple IIgs speed menu are not ported.
/// </summary>
public sealed class GfxMenu
{
    public sealed class MenuEntry
    {
        public string Text { get; set; } = string.Empty;

        public int TextLength => Text.Length;

        public int Row { get; set; }

        public int Column { get; set; }

        /// <summary>Total number of items in this menu.</summary>
        public int ItemCount { get; set; }

        /// <summary>Index of the first item in the item list.</summary>
        public int FirstItemNr { get; set; }

        public int SelectedItemNr { get; set; }

        public int MaxItemTextLength { get; set; }
    }

    public sealed class MenuItemEntry
    {
        public string Text { get; set; } = string.Empty;

        public int TextLength => Text.Length;

        public int Row { get; set; }

        public int Column { get; set; }

        /// <summary>Set by scripts through enable.item and disable.item.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Controller raised when the item is chosen.</summary>
        public int ControllerSlot { get; set; }
    }

    private readonly AgiEngine _vm;
    private readonly GfxMgr _gfx;
    private readonly TextMgr _text;

    private bool _allowed = true;
    private bool _submitted;
    private bool _delayedExecuteViaKeyboard;
    private bool _delayedExecuteViaMouse;

    private int _setupMenuColumn = 1;
    private int _setupMenuItemColumn = 1;

    private readonly List<MenuEntry> _array = [];
    private readonly List<MenuItemEntry> _itemArray = [];

    private int _lastSelectedMenuNr;
    private int _drawnMenuNr = -1;
    private int _drawnMenuHeight;
    private int _drawnMenuWidth;
    private int _drawnMenuY;
    private int _drawnMenuX;
    private int _mouseModeItemNr = -1;

    public GfxMenu(AgiEngine vm, GfxMgr gfx, TextMgr text)
    {
        _vm = vm;
        _gfx = gfx;
        _text = text;
    }

    public IReadOnlyList<MenuEntry> Menus => _array;

    public IReadOnlyList<MenuItemEntry> Items => _itemArray;

    /// <summary>Index of the menu currently pulled down, or -1.</summary>
    public int DrawnMenuNr => _drawnMenuNr;

    public void AddMenu(string menuText)
    {
        // already submitted? in that case no further changes possible
        if (_submitted)
        {
            return;
        }

        var menuEntry = new MenuEntry { Text = menuText };

        // Cut the menu name in case the menu bar is full, which happens in at least the fan game
        // Get Outta Space Quest. The original interpreter had graphical issues in this case.
        int curColumnEnd = _setupMenuColumn + menuEntry.TextLength;
        while (menuEntry.TextLength > 0 && curColumnEnd > TextMgr.FontColumnCharacters)
        {
            menuEntry.Text = menuEntry.Text[..^1];
            curColumnEnd--;
        }

        menuEntry.Row = 0;
        menuEntry.Column = _setupMenuColumn;
        menuEntry.ItemCount = 0;
        menuEntry.FirstItemNr = _itemArray.Count;
        menuEntry.SelectedItemNr = menuEntry.FirstItemNr;
        menuEntry.MaxItemTextLength = 0;
        _array.Add(menuEntry);

        _setupMenuColumn += menuEntry.TextLength + 1;
    }

    public void AddMenuItem(string menuItemText, int controllerSlot)
    {
        if (_submitted)
        {
            return;
        }

        if (_array.Count == 0)
        {
            throw new AgiResourceException("tried to add a menu item before adding an actual menu");
        }

        var curMenuEntry = _array[^1];
        var menuItemEntry = new MenuItemEntry
        {
            Enabled = true,
            Text = menuItemText,
            ControllerSlot = controllerSlot,
        };

        // The original PC interpreter used the length of the first item for drawing. In KQ2 on the
        // Apple IIgs later items are longer, which would glitch, so the longest item is used.
        if (curMenuEntry.MaxItemTextLength < menuItemEntry.TextLength)
        {
            curMenuEntry.MaxItemTextLength = menuItemEntry.TextLength;
        }

        if (curMenuEntry.ItemCount == 0)
        {
            // the first item of a menu decides the column of the whole pull-down
            if (menuItemEntry.TextLength + curMenuEntry.Column < TextMgr.FontColumnCharacters - 1)
            {
                _setupMenuItemColumn = curMenuEntry.Column;
            }
            else
            {
                _setupMenuItemColumn = TextMgr.FontColumnCharacters - 1 - menuItemEntry.TextLength;
            }
        }

        menuItemEntry.Row = 2 + curMenuEntry.ItemCount;
        menuItemEntry.Column = _setupMenuItemColumn;

        _itemArray.Add(menuItemEntry);
        curMenuEntry.ItemCount++;
    }

    public void Submit()
    {
        if (_array.Count == 0 || _itemArray.Count == 0)
        {
            return;
        }

        _submitted = true;
        _vm.Host.Log.Debug("agi.menu", $"menu submitted: {string.Join(" | ", _array.Select(m => m.Text.Trim()))}");

        // The Apple IIgs drew its menus with a system font and the Atari ST SQ1 has one bad entry.
        // Both have items shorter than the longest one, which looks wrong with the PC drawing.
        if (_vm.Description.Platform is AgiPlatform.Apple2Gs or AgiPlatform.AtariSt)
        {
            foreach (var menuEntry in _array)
            {
                int lastItemNr = menuEntry.FirstItemNr + menuEntry.ItemCount;
                for (int itemNr = menuEntry.FirstItemNr; itemNr < lastItemNr; itemNr++)
                {
                    var item = _itemArray[itemNr];
                    int missing = menuEntry.MaxItemTextLength - item.TextLength;
                    if (missing <= 0)
                    {
                        continue;
                    }

                    if (item.Text.Contains('>'))
                    {
                        // pad before the '<' so the key hint stays right aligned
                        int textPos = item.Text.LastIndexOf('<');
                        if (textPos > 0)
                        {
                            item.Text = item.Text.Insert(textPos, new string(' ', missing));
                        }
                    }
                    else if (item.Text.All(c => c == '-'))
                    {
                        // separator, which was sometimes too short as well
                        item.Text = new string('-', missing) + item.Text;
                    }
                    else
                    {
                        item.Text += new string(' ', missing);
                    }
                }
            }
        }
    }

    public void ItemEnable(int controllerSlot) => ItemEnableDisable(controllerSlot, true);

    public void ItemDisable(int controllerSlot) => ItemEnableDisable(controllerSlot, false);

    private void ItemEnableDisable(int controllerSlot, bool enabled)
    {
        foreach (var item in _itemArray)
        {
            if (item.ControllerSlot == controllerSlot)
            {
                item.Enabled = enabled;
            }
        }
    }

    public void ItemEnableAll()
    {
        foreach (var item in _itemArray)
        {
            item.Enabled = true;
        }
    }

    /// <summary>True once the scripts have created and submitted a menu.</summary>
    public bool IsAvailable() => _submitted;

    public void AccessAllow() => _allowed = true;

    public void AccessDeny() => _allowed = false;

    public void DelayedExecuteViaKeyboard()
    {
        _delayedExecuteViaKeyboard = true;
        _delayedExecuteViaMouse = false;
    }

    public void DelayedExecuteViaMouse()
    {
        _delayedExecuteViaKeyboard = false;
        _delayedExecuteViaMouse = true;
    }

    public bool DelayedExecuteActive() => _delayedExecuteViaKeyboard || _delayedExecuteViaMouse;

    /// <summary>Shows the menu bar and runs the menu until an item is chosen or it is dismissed.</summary>
    public async ValueTask ExecuteAsync()
    {
        bool viaKeyboard = _delayedExecuteViaKeyboard;
        bool viaMouse = _delayedExecuteViaMouse;
        _delayedExecuteViaKeyboard = false;
        _delayedExecuteViaMouse = false;

        if (!_submitted || !_allowed)
        {
            return;
        }

        _text.CharPos_Push();
        _text.CharAttrib_Push();
        _text.ClearLine(0, _text.CalculateTextBackground(15));

        for (int menuNr = 0; menuNr < _array.Count; menuNr++)
        {
            DrawMenuName(menuNr, false);
        }

        // Pull down the last selected menu, unless the mouse decides.
        _drawnMenuNr = _lastSelectedMenuNr;
        if (viaMouse)
        {
            int mouseRow = _vm.Mouse.Y;
            int mouseColumn = _vm.Mouse.X;
            _gfx.TranslateDisplayPosToFontScreen(ref mouseColumn, ref mouseRow);
            MouseFindMenuSelection(mouseRow, mouseColumn, out _drawnMenuNr, out _mouseModeItemNr);
        }

        if (_drawnMenuNr >= 0)
        {
            if (viaKeyboard)
            {
                DrawMenu(_drawnMenuNr, _array[_drawnMenuNr].SelectedItemNr);
            }

            if (viaMouse)
            {
                DrawMenu(_drawnMenuNr, _mouseModeItemNr);
            }
        }

        _vm.CycleInnerLoopActivate(viaKeyboard ? CycleInnerLoopType.MenuViaKeyboard : CycleInnerLoopType.MenuViaMouse);
        do
        {
            await _vm.ProcessAgiEventsAsync();
        }
        while (_vm.CycleInnerLoopIsActive && !(_vm.ShouldQuit || _vm.RestartRequested));

        if (_drawnMenuNr >= 0)
        {
            RemoveActiveMenu(_drawnMenuNr);
        }

        if (viaKeyboard)
        {
            // in keyboard mode the last selection is remembered
            _lastSelectedMenuNr = _drawnMenuNr;
        }

        _text.CharAttrib_Pop();
        _text.CharPos_Pop();

        // Restore the status line
        if (_text.StatusEnabled)
        {
            _text.StatusDraw();
        }
        else if (_text.WindowRowMin == 0)
        {
            // The play area starts right at the top (Donald Duck), so render it instead of clearing.
            // The original cleared the upper pixel rows in this case.
            _gfx.RenderBlock(0, 0, GfxMgr.ScriptWidth, TextMgr.FontVisualHeight);
        }
        else
        {
            _text.ClearLine(0, 0);
        }
    }

    private void DrawMenuName(int menuNr, bool inverted)
    {
        var menuEntry = _array[menuNr];
        if (menuEntry.TextLength == 0)
        {
            return;
        }

        if (!inverted)
        {
            _text.CharAttrib_Set(0, _text.CalculateTextBackground(15));
        }
        else
        {
            _text.CharAttrib_Set(15, _text.CalculateTextBackground(0));
        }

        _text.CharPos_Set(menuEntry.Row, menuEntry.Column);
        _text.DisplayText(menuEntry.Text, disabledLook: menuEntry.ItemCount == 0);
    }

    private void DrawItemName(int itemNr, bool inverted)
    {
        var itemEntry = _itemArray[itemNr];
        if (!inverted)
        {
            _text.CharAttrib_Set(0, _text.CalculateTextBackground(15));
        }
        else
        {
            _text.CharAttrib_Set(15, _text.CalculateTextBackground(0));
        }

        _text.CharPos_Set(itemEntry.Row, itemEntry.Column);
        _text.DisplayText(itemEntry.Text, disabledLook: !itemEntry.Enabled);
    }

    private void DrawMenu(int selectedMenuNr, int selectedMenuItemNr)
    {
        var menuEntry = _array[selectedMenuNr];
        var firstItem = _itemArray[menuEntry.FirstItemNr];

        DrawMenuName(selectedMenuNr, true);

        _drawnMenuHeight = (menuEntry.ItemCount + 2) * TextMgr.FontVisualHeight;
        _drawnMenuWidth = menuEntry.MaxItemTextLength * TextMgr.FontVisualWidth + 8;
        _drawnMenuY = (1 - _text.WindowRowMin) * TextMgr.FontVisualHeight;
        _drawnMenuX = (firstItem.Column - 1) * TextMgr.FontVisualWidth;

        _gfx.DrawBox(_drawnMenuX, _drawnMenuY, _drawnMenuWidth, _drawnMenuHeight, 15, 0);

        int lastItemNr = menuEntry.FirstItemNr + menuEntry.ItemCount;
        for (int itemNr = menuEntry.FirstItemNr; itemNr < lastItemNr; itemNr++)
        {
            DrawItemName(itemNr, itemNr == selectedMenuItemNr);
        }
    }

    private void RemoveActiveMenu(int selectedMenuNr)
    {
        DrawMenuName(selectedMenuNr, false);

        // overwrite the items by rendering the play screen again
        _gfx.RenderBlock(_drawnMenuX, _drawnMenuY, _drawnMenuWidth, _drawnMenuHeight);
    }

    public void KeyPress(ushort newKey)
    {
        var menuEntry = _array[_drawnMenuNr];
        var itemEntry = _itemArray[menuEntry.SelectedItemNr];
        int newMenuNr = _drawnMenuNr;
        int newItemNr = menuEntry.SelectedItemNr;

        switch (newKey)
        {
            case AgiKey.Enter:
                if (!itemEntry.Enabled)
                {
                    return;
                }

                _vm.State.ControllerOccurred[itemEntry.ControllerSlot] = true;
                _vm.CycleInnerLoopInactive();
                break;
            case AgiKey.Escape:
                _vm.CycleInnerLoopInactive();
                break;
            case AgiKey.Up:
                newItemNr--;
                break;
            case AgiKey.Down:
                newItemNr++;
                break;
            case AgiKey.PageUp:
                newItemNr = menuEntry.FirstItemNr;
                break;
            case AgiKey.PageDown:
                newItemNr = menuEntry.FirstItemNr + menuEntry.ItemCount - 1;
                break;
            case AgiKey.Left:
                newMenuNr--;
                break;
            case AgiKey.Right:
                newMenuNr++;
                break;
            case AgiKey.Home:
                newMenuNr = 0;
                break;
            case AgiKey.End:
                newMenuNr = _array.Count - 1;
                break;
        }

        if (newMenuNr != _drawnMenuNr)
        {
            int lastMenuNr = _array.Count - 1;
            if (newMenuNr < 0)
            {
                newMenuNr = lastMenuNr;
            }
            else if (newMenuNr > lastMenuNr)
            {
                newMenuNr = 0;
            }

            if (newMenuNr != _drawnMenuNr)
            {
                RemoveActiveMenu(_drawnMenuNr);
                _drawnMenuNr = newMenuNr;
                DrawMenu(_drawnMenuNr, _array[_drawnMenuNr].SelectedItemNr);
            }
        }

        if (newItemNr != menuEntry.SelectedItemNr)
        {
            int lastItemNr = menuEntry.FirstItemNr + menuEntry.ItemCount - 1;
            if (newItemNr < menuEntry.FirstItemNr)
            {
                newItemNr = lastItemNr;
            }
            else if (newItemNr > lastItemNr)
            {
                newItemNr = menuEntry.FirstItemNr;
            }

            if (newItemNr != menuEntry.SelectedItemNr)
            {
                DrawItemName(menuEntry.SelectedItemNr, false);
                DrawItemName(newItemNr, true);
                menuEntry.SelectedItemNr = newItemNr;
            }
        }
    }

    /// <summary>
    /// Called on clicks in keyboard mode, and all the time in mouse mode so the pointer position
    /// moves the selection; releasing the button in mouse mode chooses the item under it.
    /// </summary>
    public void MouseEvent(ushort newKey)
    {
        int mouseRow = _vm.Mouse.Y;
        int mouseColumn = _vm.Mouse.X;
        _gfx.TranslateDisplayPosToFontScreen(ref mouseColumn, ref mouseRow);

        MouseFindMenuSelection(mouseRow, mouseColumn, out int activeMenuNr, out int activeItemNr);

        if (newKey == AgiKey.MouseButtonLeft)
        {
            // Keyboard mode: a click on a shown item chooses it, a click on a menu name opens that
            // menu, anything else is ignored rather than closing the menu.
            if (activeItemNr >= 0)
            {
                var itemEntry = _itemArray[activeItemNr];
                if (!itemEntry.Enabled)
                {
                    return;
                }

                _vm.State.ControllerOccurred[itemEntry.ControllerSlot] = true;
                _vm.CycleInnerLoopInactive();
                return;
            }

            if (activeMenuNr >= 0 && activeMenuNr != _drawnMenuNr)
            {
                RemoveActiveMenu(_drawnMenuNr);
                _drawnMenuNr = activeMenuNr;
                DrawMenu(_drawnMenuNr, _array[_drawnMenuNr].SelectedItemNr);
            }

            return;
        }

        // If the mouse is not over any menu, keep the last one
        if (activeMenuNr < 0)
        {
            activeMenuNr = _drawnMenuNr;
        }

        if (activeMenuNr != _drawnMenuNr)
        {
            if (_drawnMenuNr >= 0)
            {
                RemoveActiveMenu(_drawnMenuNr);
            }

            _drawnMenuNr = activeMenuNr;
            if (_drawnMenuNr >= 0)
            {
                DrawMenu(_drawnMenuNr, activeItemNr);
            }

            _mouseModeItemNr = activeItemNr;
        }

        if (activeItemNr != _mouseModeItemNr)
        {
            if (_mouseModeItemNr >= 0)
            {
                DrawItemName(_mouseModeItemNr, false);
            }

            if (activeItemNr >= 0)
            {
                DrawItemName(activeItemNr, true);
            }

            _mouseModeItemNr = activeItemNr;
        }

        if (_vm.Mouse.Button == AgiMouseButton.Up)
        {
            // button released: choose whatever is under the pointer
            if (activeItemNr >= 0)
            {
                var itemEntry = _itemArray[activeItemNr];
                if (itemEntry.Enabled)
                {
                    _vm.State.ControllerOccurred[itemEntry.ControllerSlot] = true;
                }
            }

            _vm.CycleInnerLoopInactive();
        }
    }

    private void MouseFindMenuSelection(int mouseRow, int mouseColumn, out int activeMenuNr, out int activeItemNr)
    {
        for (int menuNr = 0; menuNr < _array.Count; menuNr++)
        {
            var menuEntry = _array[menuNr];
            if (mouseRow == menuEntry.Row && mouseColumn >= menuEntry.Column && mouseColumn < menuEntry.Column + menuEntry.TextLength)
            {
                activeMenuNr = menuNr;
                activeItemNr = -1;
                return;
            }
        }

        if (_drawnMenuNr >= 0)
        {
            var menuEntry = _array[_drawnMenuNr];
            int lastItemNr = menuEntry.FirstItemNr + menuEntry.ItemCount;
            for (int itemNr = menuEntry.FirstItemNr; itemNr < lastItemNr; itemNr++)
            {
                var itemEntry = _itemArray[itemNr];
                if (mouseRow == itemEntry.Row && mouseColumn >= itemEntry.Column && mouseColumn < itemEntry.Column + itemEntry.TextLength && itemEntry.Enabled)
                {
                    activeMenuNr = _drawnMenuNr;
                    activeItemNr = itemNr;
                    return;
                }
            }
        }

        activeMenuNr = -1;
        activeItemNr = -1;
    }
}
