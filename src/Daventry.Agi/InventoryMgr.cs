using Daventry.Agi.Resources;
using Daventry.Agi.Text;

namespace Daventry.Agi;

/// <summary>
/// The inventory screen shown by the status command. Port of ScummVM's inv.cpp: carried objects
/// are listed in two columns; when flag 13 is set the player may pick one with the cursor keys,
/// otherwise any key returns to the game. Right-to-left layout is not ported.
/// </summary>
public sealed class InventoryMgr
{
    public sealed class InventoryEntry
    {
        public int ObjectNr { get; init; }

        public string Name { get; init; } = string.Empty;

        public int Row { get; init; }

        public int Column { get; set; }
    }

    private readonly AgiEngine _vm;
    private readonly List<InventoryEntry> _array = [];
    private int _activeItemNr = -1;

    public InventoryMgr(AgiEngine vm)
    {
        _vm = vm;
    }

    public IReadOnlyList<InventoryEntry> Items => _array;

    /// <summary>Index of the highlighted item, or -1 when nothing is selected.</summary>
    public int ActiveItemNr => _activeItemNr;

    private TextMgr Text => _vm.Text;

    private void GetPlayerInventory()
    {
        int selectedInventoryItem = _vm.GetVar(AgiVar.SelectedInventoryItem);
        int curRow = 2; // starting at position 2,1
        int curColumn = 1;

        _array.Clear();
        _activeItemNr = 0;

        var objects = _vm.Resources.Objects;
        for (int objectNr = 0; objectNr < objects.Count; objectNr++)
        {
            if (_vm.ObjectGetLocation(objectNr) != AgiObject.EgoOwned)
            {
                continue;
            }

            if (objectNr == selectedInventoryItem)
            {
                _activeItemNr = _array.Count;
            }

            string name = _vm.ObjectName(objectNr);
            var entry = new InventoryEntry { ObjectNr = objectNr, Name = name, Row = curRow, Column = curColumn };
            if (entry.Column > 1)
            {
                // right side, right aligned
                entry.Column -= Math.Min(name.Length, TextMgr.FontColumnCharacters);
            }

            _array.Add(entry);

            if (curColumn == 1)
            {
                curColumn = 39;
            }
            else
            {
                curColumn = 1;
                curRow++;
            }
        }

        if (_array.Count == 0)
        {
            string nothing = _vm.SystemUI.InventoryTextNothing;
            _array.Add(new InventoryEntry { ObjectNr = 0, Name = nothing, Row = 2, Column = 19 - nothing.Length / 2 });
        }
    }

    private void DrawAll()
    {
        Text.CharPos_Set(0, 11);
        Text.DisplayText(_vm.SystemUI.InventoryTextYouAreCarrying);

        for (int inventoryNr = 0; inventoryNr < _array.Count; inventoryNr++)
        {
            DrawItem(inventoryNr);
        }
    }

    private void DrawItem(int itemNr)
    {
        if (itemNr == _activeItemNr)
        {
            Text.CharAttrib_Set(15, 0);
        }
        else
        {
            Text.CharAttrib_Set(0, 15);
        }

        var entry = _array[itemNr];
        Text.CharPos_Set(entry.Row, entry.Column);

        // The original used printf here but strlen for the layout, so formatting could never have
        // worked for right-aligned items; the name is shown as is.
        Text.DisplayText(entry.Name);
    }

    public async ValueTask ShowAsync()
    {
        GetPlayerInventory();

        bool selectItems = _vm.GetFlag(AgiFlag.StatusSelectsItems);
        if (!selectItems)
        {
            _activeItemNr = -1; // so that none is shown as active
        }

        DrawAll();

        Text.CharAttrib_Set(0, 15);
        if (selectItems)
        {
            Text.CharPos_Set(24, 2);
            Text.DisplayText(_vm.SystemUI.InventoryTextSelectItems);
        }
        else
        {
            Text.CharPos_Set(24, 4);
            Text.DisplayText(_vm.SystemUI.InventoryTextReturnToGame);
        }

        if (selectItems)
        {
            _vm.CycleInnerLoopActivate(CycleInnerLoopType.Inventory);
            do
            {
                await _vm.ProcessAgiEventsAsync();
            }
            while (_vm.CycleInnerLoopIsActive && !(_vm.ShouldQuit || _vm.RestartRequested));

            _vm.SetVar(AgiVar.SelectedInventoryItem, _activeItemNr >= 0 ? (byte)_array[_activeItemNr].ObjectNr : (byte)0xFF);
        }
        else
        {
            // no selection possible, just wait for a key and exit
            await _vm.WaitAnyKeyAsync();
        }
    }

    public void KeyPress(ushort newKey)
    {
        switch (newKey)
        {
            case AgiKey.Enter:
                _vm.CycleInnerLoopInactive();
                break;
            case AgiKey.Escape:
                _vm.CycleInnerLoopInactive();
                _activeItemNr = -1; // no item selected
                break;
            case AgiKey.Up:
                ChangeActiveItem(-2);
                break;
            case AgiKey.Down:
                ChangeActiveItem(+2);
                break;
            case AgiKey.Left:
                ChangeActiveItem(-1);
                break;
            case AgiKey.Right:
                ChangeActiveItem(+1);
                break;
        }
    }

    private void ChangeActiveItem(int direction)
    {
        int orgItemNr = _activeItemNr;
        _activeItemNr += direction;

        if (_activeItemNr >= 0 && _activeItemNr < _array.Count)
        {
            DrawItem(orgItemNr);
            DrawItem(_activeItemNr);
        }
        else
        {
            _activeItemNr = orgItemNr;
        }
    }
}
