using Daventry.Core.Hosting;

namespace Daventry.Agi.Menu;

/// <summary>
/// The pull-down menu bar. Placeholder until menu.cpp is ported: menus are recorded so scripts can
/// enable and disable items, but the bar is never shown.
/// </summary>
public sealed class GfxMenu
{
    public sealed class MenuEntry
    {
        public required string Text { get; init; }

        public List<MenuItemEntry> Items { get; } = [];
    }

    public sealed class MenuItemEntry
    {
        public required string Text { get; init; }

        public bool Enabled { get; set; } = true;

        public int ControllerSlot { get; init; }
    }

    private readonly AgiEngine _vm;
    private bool _allowed = true;
    private bool _submitted;

    public GfxMenu(AgiEngine vm)
    {
        _vm = vm;
    }

    public List<MenuEntry> Menus { get; } = [];

    public void AddMenu(string menuText) => Menus.Add(new MenuEntry { Text = menuText });

    public void AddMenuItem(string menuText, int controlCode)
    {
        if (Menus.Count == 0)
        {
            AddMenu(string.Empty);
        }

        Menus[^1].Items.Add(new MenuItemEntry { Text = menuText, ControllerSlot = controlCode });
    }

    public void Submit()
    {
        _submitted = true;
        _vm.Host.Log.Debug("agi.menu", $"menu submitted: {string.Join(" | ", Menus.Select(m => m.Text.Trim()))}");
    }

    public void ItemEnable(int controllerSlot) => ItemEnableDisable(controllerSlot, true);

    public void ItemDisable(int controllerSlot) => ItemEnableDisable(controllerSlot, false);

    public void ItemEnableAll()
    {
        foreach (var item in Menus.SelectMany(m => m.Items))
        {
            item.Enabled = true;
        }
    }

    private void ItemEnableDisable(int controllerSlot, bool enabled)
    {
        foreach (var item in Menus.SelectMany(m => m.Items).Where(i => i.ControllerSlot == controllerSlot))
        {
            item.Enabled = enabled;
        }
    }

    public void KeyPress(ushort newKey)
    {
    }

    public void MouseEvent(ushort newKey)
    {
    }

    public bool IsAvailable() => _allowed && _submitted && Menus.Count > 0;

    public void AccessAllow() => _allowed = true;

    public void AccessDeny() => _allowed = false;

    public void DelayedExecuteViaKeyboard()
    {
    }

    public void DelayedExecuteViaMouse()
    {
    }

    public bool DelayedExecuteActive() => false;

    public ValueTask ExecuteAsync() => default;
}
