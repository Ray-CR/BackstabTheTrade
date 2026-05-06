using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace BackstabTheTrade;

public sealed class InventoryWealthWindow : Window, IDisposable
{
    private readonly BackstabTheTrade _plugin;
    public bool NeedsInventorySnapshot => IsOpen;

    public InventoryWealthWindow(BackstabTheTrade plugin)
        : base("Inventory Wealth###BackstabTheTradeInventoryWealth")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 320),
            MaximumSize = new Vector2(900, 1000),
        };

        _plugin = plugin;
        IsOpen = false;
    }

    public override void Draw()
    {
        if (ImGui.Button("Refresh Inventory Snapshot", new Vector2(-1, 24)))
            _plugin.InventoryWealth.Invalidate();

        var snapshot = _plugin.InventoryWealth.GetSnapshot();

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.95f, 0.85f, 0.3f, 1f), $"Player Gil: {snapshot.PlayerGil:N0}");
        ImGui.TextColored(new Vector4(0.6f, 0.9f, 1f, 1f), $"Item Value: {snapshot.TotalItemValue:N0}");
        ImGui.TextColored(new Vector4(0.5f, 1f, 0.5f, 1f), $"Total Wealth: {snapshot.TotalWealth:N0}");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (snapshot.Entries.Count == 0)
        {
            ImGui.TextDisabled("No valued inventory items found in personal inventory.");
            return;
        }

        ImGui.BeginChild("inventoryWealthList", new Vector2(-1, -1), true);
        var clipper = new ImGuiListClipper();
        clipper.Begin(snapshot.Entries.Count);
        while (clipper.Step())
        {
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var entry = snapshot.Entries[i];
                ImGui.TextUnformatted($"{entry.Name} x{entry.Quantity:N0} @ {entry.UnitPrice:N0} = {entry.TotalValue:N0}");
            }
        }
        clipper.End();
        ImGui.EndChild();
    }

    public void Dispose()
    {
    }
}
