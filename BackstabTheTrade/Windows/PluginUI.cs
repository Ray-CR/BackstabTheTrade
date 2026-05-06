using System;
using Dalamud.Interface.Windowing;

namespace BackstabTheTrade;

public sealed class PluginUI : IDisposable
{
    private readonly BackstabTheTrade _plugin;
    private readonly MainWindow _mainWindow;
    private readonly TrackerWindow _trackerWindow;
    private readonly TradeHistoryWindow _tradeHistoryWindow;
    private readonly InventoryWealthWindow _inventoryWealthWindow;
    private readonly WindowSystem _windowSystem = new("BackstabTheTrade");

    public PluginUI(BackstabTheTrade plugin, TradeManager tradeManager)
    {
        _plugin = plugin;
        _inventoryWealthWindow = new InventoryWealthWindow(plugin);
        _mainWindow = new MainWindow(plugin, tradeManager, OpenTracker, OpenTradeHistory, OpenInventoryWealth);
        _trackerWindow = new TrackerWindow(plugin, _mainWindow);
        _tradeHistoryWindow = new TradeHistoryWindow(tradeManager);
        _windowSystem.AddWindow(_mainWindow);
        _windowSystem.AddWindow(_trackerWindow);
        _windowSystem.AddWindow(_tradeHistoryWindow);
        _windowSystem.AddWindow(_inventoryWealthWindow);
    }

    public void Draw()
    {
        _plugin.InventoryWealth.SetActiveDemand(
            _mainWindow.NeedsInventorySnapshot ||
            _inventoryWealthWindow.NeedsInventorySnapshot);
        _windowSystem.Draw();
    }

    public void Toggle() => _mainWindow.Toggle();

    public void OpenWithTarget(string name)
    {
        _mainWindow.SetTarget(name);
        _mainWindow.IsOpen = true;
    }

    public void OpenTracker() => _trackerWindow.IsOpen = true;

    public void OpenTradeHistory() => _tradeHistoryWindow.IsOpen = true;

    public void OpenInventoryWealth() => _inventoryWealthWindow.IsOpen = true;

    public void Dispose()
    {
        _windowSystem.RemoveAllWindows();
        _mainWindow.Dispose();
        _trackerWindow.Dispose();
        _tradeHistoryWindow.Dispose();
        _inventoryWealthWindow.Dispose();
    }
}
