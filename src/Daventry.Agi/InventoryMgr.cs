using Daventry.Core.Hosting;

namespace Daventry.Agi;

/// <summary>The inventory screen. Placeholder until inv.cpp is ported: lists carried items in a message box.</summary>
public sealed class InventoryMgr
{
    private readonly AgiEngine _vm;

    public InventoryMgr(AgiEngine vm)
    {
        _vm = vm;
    }

    public async ValueTask ShowAsync()
    {
        var carried = _vm.Resources.Objects
            .Where(o => o.Location == Resources.AgiObject.EgoOwned)
            .Select(o => o.Name)
            .ToList();

        string text = carried.Count == 0
            ? _vm.SystemUI.InventoryTextNothing
            : _vm.SystemUI.InventoryTextYouAreCarrying + "\n" + string.Join("\n", carried);
        await _vm.Text.MessageBoxAsync(text);
    }

    public void KeyPress(ushort newKey)
    {
    }
}
