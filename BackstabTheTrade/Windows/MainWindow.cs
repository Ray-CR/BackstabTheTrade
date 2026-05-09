using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Interface.Windowing;

namespace BackstabTheTrade;

public sealed class MainWindow : Window, IDisposable
{
    private const int PlayerObjectKindValue = 1;

    private enum SidebarPage
    {
        Main,
        GilTrade,
        ItemManual,
        GilToItem,
        ReceiverMode,
        TrackerWindows,
        TimeSettings,
    }

    private enum ManualSortColumn
    {
        Name,
        Quantity,
        UnitPrice,
    }

    private readonly BackstabTheTrade _plugin;
    private readonly TradeManager _tm;
    private readonly Action _openTracker;
    private readonly Action _openTradeHistory;
    private readonly Action _openInventoryWealth;

    private string _targetName = string.Empty;
    private long _totalGil = 1_000_000;
    private string _totalGilText = "1000000";
    private int _tradeCount = 1;
    private string _gilModeValidationMessage = string.Empty;
    private AutoTradeMode _mode;
    private string _itemTargetValueText = "1000000";
    private string _manualItemFilter = string.Empty;
    private bool _manualShowSelectedOnly;
    private bool _showSafetyPreview;
    private bool _showPlanDetails;
    private string _recentTargetName = string.Empty;
    private InventoryTradePlan _itemTradePlan = InventoryTradePlan.Empty;
    private InventoryTradePlan _cachedBatchPlan = InventoryTradePlan.Empty;
    private IReadOnlyList<InventoryTradeBatch> _cachedBatches = Array.Empty<InventoryTradeBatch>();
    private IReadOnlyList<InventoryWealthEntry> _cachedManualVisibleEntries = Array.Empty<InventoryWealthEntry>();
    private IReadOnlyList<InventoryWealthEntry> _cachedManualSnapshotEntries = Array.Empty<InventoryWealthEntry>();
    private string _cachedManualFilter = string.Empty;
    private bool _cachedManualShowSelectedOnly;
    private int _manualSelectionVersion;
    private int _cachedManualSelectionVersion = -1;
    private ManualSortColumn _manualSortColumn = ManualSortColumn.Name;
    private bool _manualSortAscending = true;
    private Task<InventoryTradePlan>? _pendingPlanTask;
    private readonly Dictionary<uint, long> _manualItemQuantities = new();
    private int _seenCompletedRunSerial;
    private SidebarPage _selectedPage = SidebarPage.Main;

    public bool NeedsInventorySnapshot =>
        IsOpen && (_selectedPage == SidebarPage.Main ||
                   _selectedPage == SidebarPage.ItemManual ||
                   _selectedPage == SidebarPage.GilToItem);

    public MainWindow(BackstabTheTrade plugin, TradeManager tm, Action openTracker, Action openTradeHistory, Action openInventoryWealth)
        : base("Backstab The Trade###BackstabTheTradeMain", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(760, 560),
            MaximumSize = new Vector2(1280, 1200),
        };

        _plugin = plugin;
        _tm = tm;
        _openTracker = openTracker;
        _openTradeHistory = openTradeHistory;
        _openInventoryWealth = openInventoryWealth;
    }

    public void SetTarget(string name)
    {
        _targetName = name;
        if (!string.IsNullOrWhiteSpace(name))
            _recentTargetName = name.Trim();
    }

    public override void Draw()
    {
        PollPlanTask();
        var cfg = _plugin.Configuration;
        bool running = _tm.IsRunning;
        if (_seenCompletedRunSerial != _tm.CompletedRunSerial)
        {
            _seenCompletedRunSerial = _tm.CompletedRunSerial;
            ClearTargetName("auto trade stopped or completed");
        }

        bool hasBuiltSendPlan = _itemTradePlan.Entries.Count > 0;
        if ((running || hasBuiltSendPlan) && cfg.ReceiverModeAutoConfirm)
        {
            cfg.ReceiverModeAutoConfirm = false;
            cfg.Save();
        }

        float sidebarWidth = 220f;
        float height = Math.Max(420f, ImGui.GetContentRegionAvail().Y);

        ImGui.BeginChild("btt_sidebar", new Vector2(sidebarWidth, height), true);
        DrawSidebar();
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("btt_content", new Vector2(0, height), true);
        DrawSelectedPage(running, hasBuiltSendPlan);
        ImGui.EndChild();
    }

    private void DrawSidebar()
    {
        DrawSidebarButton(SidebarPage.Main, "Main");
        DrawSidebarButton(SidebarPage.GilTrade, "Gil Trade Mode");
        DrawSidebarButton(SidebarPage.ItemManual, "Item Manual Mode");
        DrawSidebarButton(SidebarPage.GilToItem, "Gil to Item Mode");
        DrawSidebarButton(SidebarPage.ReceiverMode, "Receiver Mode");
        DrawSidebarButton(SidebarPage.TrackerWindows, "Tracker + Windows");
        DrawSidebarButton(SidebarPage.TimeSettings, "Time Settings");
    }

    private void DrawSidebarButton(SidebarPage page, string label)
    {
        bool selected = _selectedPage == page;
        if (selected)
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.28f, 0.28f, 0.32f, 1f));

        if (ImGui.Button(label, new Vector2(-1, 28)))
            _selectedPage = page;

        if (selected)
            ImGui.PopStyleColor();
    }

    private void DrawSelectedPage(bool running, bool hasBuiltSendPlan)
    {
        switch (_selectedPage)
        {
            case SidebarPage.Main:
                DrawMainPage();
                break;
            case SidebarPage.GilTrade:
                _mode = AutoTradeMode.Gil;
                DrawTradePageHeader("Gil Trade Mode", running);
                DrawGilMode();
                ImGui.Spacing();
                DrawAutoStartControls();
                if (!running)
                    ImGui.TextWrapped("Tip: If Auto Start fails, open Trade manually, then use 'Take Over Open Trade Window' in the tracker.");
                DrawRunningStatus();
                break;
            case SidebarPage.ItemManual:
                _mode = AutoTradeMode.ItemManual;
                DrawTradePageHeader("Item Manual Mode", running);
                DrawManualItemMode();
                DrawRunningStatus();
                break;
            case SidebarPage.GilToItem:
                _mode = AutoTradeMode.GilToItem;
                DrawTradePageHeader("Gil to Item Mode", running);
                DrawGilToItemMode();
                DrawRunningStatus();
                break;
            case SidebarPage.ReceiverMode:
                DrawReceiverPage(running, hasBuiltSendPlan);
                DrawRunningStatus();
                break;
            case SidebarPage.TrackerWindows:
                DrawTrackerWindowsPage();
                DrawRunningStatus();
                break;
            case SidebarPage.TimeSettings:
                DrawTimeSettingsPage();
                break;
        }
    }

    private void DrawMainPage()
    {
        var snapshot = _plugin.InventoryWealth.GetSnapshot();
        ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), "Main");
        ImGui.TextDisabled("Inventory Items are shown here directly, including your gil summary.");
        ImGui.Separator();

        ImGui.TextColored(new Vector4(0.95f, 0.85f, 0.3f, 1f), $"Player Gil: {snapshot.PlayerGil:N0}");
        ImGui.TextColored(new Vector4(0.6f, 0.9f, 1f, 1f), $"Item Value: {snapshot.TotalItemValue:N0}");
        ImGui.TextColored(new Vector4(0.5f, 1f, 0.5f, 1f), $"Total Wealth: {snapshot.TotalWealth:N0}");
        ImGui.Spacing();

        if (ImGui.Button("Refresh Inventory Items", new Vector2(-1, 24)))
            _plugin.InventoryWealth.Invalidate();

        ImGui.Spacing();
        ImGui.BeginChild("main_inventory_items", new Vector2(-1, -1), true);
        if (snapshot.Entries.Count == 0)
        {
            ImGui.TextDisabled("No tradeable inventory items found.");
        }
        else if (ImGui.BeginTable("main_inventory_table", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Have", ImGuiTableColumnFlags.WidthFixed, 82);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 88);
            ImGui.TableSetupColumn("Stacks", ImGuiTableColumnFlags.WidthFixed, 64);
            ImGui.TableHeadersRow();

            var clipper = new ImGuiListClipper();
            clipper.Begin(snapshot.Entries.Count);
            while (clipper.Step())
            {
                for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                {
                    var entry = snapshot.Entries[i];
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextUnformatted(entry.Name);
                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextUnformatted(entry.Quantity.ToString("N0"));
                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextUnformatted(entry.TotalValue.ToString("N0"));
                    ImGui.TableSetColumnIndex(3);
                    ImGui.TextDisabled(entry.StackSources.Count.ToString("N0"));
                }
            }
            clipper.End();

            ImGui.EndTable();
        }
        ImGui.EndChild();
    }

    private void DrawTradePageHeader(string title, bool running)
    {
        ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), title);
        ImGui.Text("Target Player Name:");
        ImGui.SetNextItemWidth(-1);
        ImGui.BeginDisabled(running);
        ImGui.InputText("##target_trade_page", ref _targetName, 64);
        ImGui.EndDisabled();
        if (!string.IsNullOrWhiteSpace(_recentTargetName))
        {
            ImGui.BeginDisabled(running);
            if (ImGui.SmallButton($"Use recent target: {_recentTargetName}"))
                _targetName = _recentTargetName;
            ImGui.EndDisabled();
        }
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void DrawReceiverPage(bool running, bool hasBuiltSendPlan)
    {
        var cfg = _plugin.Configuration;
        ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), "Receiver Mode");
        ImGui.TextWrapped("Automatically press Trade and confirm Yes while you are receiving a trade.");
        ImGui.Separator();

        bool receiverMode = cfg.ReceiverModeAutoConfirm;
        ImGui.BeginDisabled(running || hasBuiltSendPlan);
        if (ImGui.Checkbox("Receiver mode", ref receiverMode))
        {
            cfg.ReceiverModeAutoConfirm = receiverMode;
            cfg.Save();
        }
        ImGui.EndDisabled();

        if (ImGui.Button("Enable receiver mode", new Vector2(190, 0)))
        {
            EnableReceiverMode();
        }

        ImGui.SameLine();
        if (ImGui.Button("Disable receiver mode", new Vector2(190, 0)))
        {
            DisableReceiverMode();
        }

        ImGui.Spacing();
        if (hasBuiltSendPlan)
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.35f, 1f), "Receive mode is locked because a send trade plan exists.");
        if (running)
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.35f, 1f), "Receive mode cannot be changed while trading is running.");

        ImGui.TextWrapped("Enable receiver mode clears the current send plan and turns receiver mode on.");
    }

    private void DrawTrackerWindowsPage()
    {
        ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), "Tracker + Windows");
        ImGui.TextWrapped("Open the helper windows from here.");
        ImGui.Separator();

        float trackerButtonGap = 8f;
        float trackerButtonWidth = (ImGui.GetContentRegionAvail().X - trackerButtonGap) * 0.5f;

        if (ImGui.Button("Open Track Log", new Vector2(trackerButtonWidth, 26)))
            _openTracker();
        ImGui.SameLine(0f, trackerButtonGap);
        if (ImGui.Button("Open Trade History Window", new Vector2(trackerButtonWidth, 26)))
            _openTradeHistory();

        ImGui.Spacing();
        ImGui.TextWrapped("Track Log opens the diagnostic tracker. Trade History Window opens the trade history page.");
    }

    private void DrawTimeSettingsPage()
    {
        var cfg = _plugin.Configuration;
        ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), "Time Settings");
        ImGui.TextDisabled("Time settings are independent from the trade mode pages.");
        ImGui.Separator();
        DrawTimingControls(cfg);
    }

    private void DrawTimingControls(Configuration cfg)
    {
        ImGui.TextColored(new Vector4(0.95f, 0.85f, 0.3f, 1f), "General");
        DrawTimingSlider(cfg.ActionDelayMs, 0, 5000, "timing_embedded_d1", "Between trades", "0-300", v => cfg.ActionDelayMs = v, cfg);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.95f, 0.85f, 0.3f, 1f), "Sender");
        DrawTimingSlider(cfg.TargetToTradeDelayMs, 0, 2000, "timing_embedded_dtarget", "Target -> /trade", "0-150", v => cfg.TargetToTradeDelayMs = v, cfg);
        DrawTimingSlider(cfg.TradeRetryDelayMs, 0, 2000, "timing_embedded_dtraderetry", "Retry /trade", "250-800", v => cfg.TradeRetryDelayMs = v, cfg);
        DrawTimingSlider(cfg.TradeOpenDelayMs, 0, 3000, "timing_embedded_d2", "After trade opens", "0-300", v => cfg.TradeOpenDelayMs = v, cfg);
        DrawTimingSlider(cfg.GilBarRetryDelayMs, 0, 2000, "timing_embedded_dgilretry", "Click gil bar retry", "150-500", v => cfg.GilBarRetryDelayMs = v, cfg);
        DrawTimingSlider(cfg.OkButtonDelayMs, 0, 2000, "timing_embedded_dok", "Before OK button", "0-150", v => cfg.OkButtonDelayMs = v, cfg);
        DrawTimingSlider(cfg.InputNumericRetryDelayMs, 0, 2000, "timing_embedded_dinputretry", "InputNumeric retry", "150-500", v => cfg.InputNumericRetryDelayMs = v, cfg);
        DrawTimingSlider(cfg.ConfirmDelayMs, 0, 2000, "timing_embedded_d3", "Before Trade button", "0-200", v => cfg.ConfirmDelayMs = v, cfg);
        DrawTimingSlider(cfg.TradeButtonRetryMs, 0, 5000, "timing_embedded_dtradebtnretry", "Trade button retry", "250-1000", v => cfg.TradeButtonRetryMs = v, cfg);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.95f, 0.85f, 0.3f, 1f), "Receiver");
        DrawTimingSlider(cfg.ReceiverOfferSettleDelayMs, 0, 2000, "timing_embedded_receiver_settle", "Receiver offer settle -> Trade", "250-450", v => cfg.ReceiverOfferSettleDelayMs = v, cfg);
        DrawTimingSlider(cfg.ReceiverTradeRetryDelayMs, 250, 3000, "timing_embedded_receiver_trade_retry", "Receiver Trade retry", "350-900", v => cfg.ReceiverTradeRetryDelayMs = v, cfg);
        DrawTimingSlider(cfg.ReceiverPollDelayMs, 10, 500, "timing_embedded_receiver_poll", "Receiver poll delay", "30-80", v => cfg.ReceiverPollDelayMs = v, cfg);
        DrawTimingSlider(cfg.ReceiverChangedPollDelayMs, 10, 500, "timing_embedded_receiver_changed_poll", "Receiver changed poll delay", "20-60", v => cfg.ReceiverChangedPollDelayMs = v, cfg);
        DrawTimingSlider(cfg.YesButtonDelayMs, 0, 2000, "timing_embedded_dyes", "Before Yes button", "0-150", v => cfg.YesButtonDelayMs = v, cfg);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.95f, 0.85f, 0.3f, 1f), "Item Trade");
        DrawTimingSlider(cfg.PlannedContextMenuTriggerDelayMs, 0, 500, "timing_embedded_planned_trigger", "Planned item context trigger", "40-80", v => cfg.PlannedContextMenuTriggerDelayMs = v, cfg);
        DrawTimingSlider(cfg.PlannedQuantityReadyDelayMs, 0, 500, "timing_embedded_planned_ready", "Planned quantity ready", "10-40", v => cfg.PlannedQuantityReadyDelayMs = v, cfg);
        DrawTimingSlider(cfg.PlannedPopupCloseWaitMs, 0, 500, "timing_embedded_planned_popup", "Planned popup close wait", "30-80", v => cfg.PlannedPopupCloseWaitMs = v, cfg);
        DrawTimingSlider(cfg.PlannedTradeSlotPollMs, 5, 250, "timing_embedded_planned_poll", "Planned trade slot poll", "10-25", v => cfg.PlannedTradeSlotPollMs = v, cfg);
        DrawTimingSlider(cfg.PlannedNextItemWaitMs, 0, 500, "timing_embedded_planned_next", "Planned next item wait", "10-40", v => cfg.PlannedNextItemWaitMs = v, cfg);
    }

    private static void DrawTimingSlider(int value, int min, int max, string id, string label, string recommendedRange, Action<int> setter, Configuration cfg)
    {
        const float sliderWidth = 300f;

        ImGui.TextUnformatted(label);

        int localValue = value;
        ImGui.SetNextItemWidth(sliderWidth);
        if (ImGui.SliderInt($"##{id}", ref localValue, min, max))
        {
            setter(localValue);
            cfg.Save();
        }

        ImGui.SameLine();
        ImGui.TextDisabled("ms");
        ImGui.TextDisabled($"(Recommended {recommendedRange} ms)");
    }

    private void DrawRunningStatus()
    {
        if (_tm.IsRunning)
        {
            ImGui.Spacing();
            int done = _tm.TotalTrades - _tm.TradesRemaining;
            float frac = _tm.TotalTrades > 0 ? (float)done / _tm.TotalTrades : 0f;
            ImGui.ProgressBar(frac, new Vector2(-1, 16), $"{done}/{_tm.TotalTrades}");
        }

        if (_tm.StatusMessage.Length > 0)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(_tm.StatusMessage);
        }
    }

    private void DrawGilMode()
    {
        long playerGil = Math.Max(0, _plugin.InventoryWealth.GetCurrentPlayerGil());
        var gilTradePlan = BuildGilTradePlan(_totalGil);

        ImGui.BeginDisabled(_tm.IsRunning);
        ImGui.TextColored(new Vector4(0.95f, 0.85f, 0.3f, 1f), $"Player Gil: {playerGil:N0}");
        ImGui.Spacing();
        ImGui.Text("Total Gil Trade:");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##totalGil", ref _totalGilText, 16))
        {
            if (string.IsNullOrWhiteSpace(_totalGilText))
            {
                _totalGil = 0;
                _gilModeValidationMessage = string.Empty;
            }
            else if (long.TryParse(_totalGilText, out var v) && v > 0)
            {
                if (v > playerGil)
                {
                    _totalGil = 0;
                    _totalGilText = string.Empty;
                    _gilModeValidationMessage = "Not enough gil.";
                }
                else
                {
                    _totalGil = v;
                    _gilModeValidationMessage = string.Empty;
                }
            }
            RecalcTrades();
        }
        ImGui.Spacing();

        if (_gilModeValidationMessage.Length > 0)
        {
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.35f, 1f), _gilModeValidationMessage);
            ImGui.Spacing();
        }

        ImGui.TextColored(
            new Vector4(0.5f, 1f, 0.5f, 1f),
            FormatGilTradePlanSummary(gilTradePlan));
        ImGui.EndDisabled();
    }

    private void DrawManualItemMode()
    {
        const float inlineButtonGap = 8f;
        var snapshot = _plugin.InventoryWealth.GetSnapshot();
        DrawAutoStartControls();
        ImGui.Spacing();
        ImGui.BeginDisabled(_tm.IsRunning);
        ImGui.Text("Manual Item Selection:");
        ImGui.TextWrapped("1. Select and enter the quantity you want to trade. The plugin plans from items that already exist in your inventory.");
        ImGui.TextWrapped("2. Press the build item manual plan, confirm the target player.");
        ImGui.TextWrapped("3. Press auto start, then plugin will trade all item selected by the plan automatically.");

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##manualItemFilter", "Filter item name...", ref _manualItemFilter, 64);
        ImGui.Checkbox("Show selected only", ref _manualShowSelectedOnly);
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear Qty"))
        {
            _manualItemQuantities.Clear();
            _manualSelectionVersion++;
            _itemTradePlan = InventoryTradePlan.Empty;
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh"))
        {
            _plugin.InventoryWealth.Invalidate();
            _itemTradePlan = _plugin.InventoryWealth.BuildTradePlanFromSelections(_manualItemQuantities);
            DisableReceiverModeForSendPlan();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Open Inventory Items"))
            _openInventoryWealth();
        ImGui.SameLine();
        if (ImGui.SmallButton("Select all salvaged item"))
        {
            foreach (var entry in snapshot.Entries)
            {
                if (!InventoryWealthService.IsSalvagedTradeItem(entry.BaseItemId) || entry.Quantity <= 0)
                    continue;

                _manualItemQuantities[entry.ItemId] = entry.Quantity;
            }

            _manualSelectionVersion++;
        }

        var visibleEntries = GetCachedManualVisibleEntries(snapshot);

        ImGui.BeginChild("manualItemSelection", new Vector2(-1, 260), true);
        if (ImGui.BeginTable("manualItemSelectionTable", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY))
        {
            ImGui.TableSetupColumn("All", ImGuiTableColumnFlags.WidthFixed, 42);
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 82);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthFixed, 62);
            ImGui.TableSetupColumn("Have", ImGuiTableColumnFlags.WidthFixed, 68);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 78);
            ImGui.TableSetupColumn("Stacks", ImGuiTableColumnFlags.WidthFixed, 62);

            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);

            ImGui.TableSetColumnIndex(0);
            bool allSelected = visibleEntries.Count > 0 && visibleEntries.All(entry => GetManualQuantity(entry.ItemId) >= entry.Quantity);
            bool toggleAll = allSelected;
            if (ImGui.Checkbox("##manual_select_all", ref toggleAll) && toggleAll != allSelected)
            {
                foreach (var entry in visibleEntries)
                {
                    if (toggleAll)
                        _manualItemQuantities[entry.ItemId] = entry.Quantity;
                    else
                        _manualItemQuantities.Remove(entry.ItemId);
                }

                _manualSelectionVersion++;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Trade all currently visible items");

            ImGui.TableSetColumnIndex(1);
            ImGui.TableHeader("Qty");

            ImGui.TableSetColumnIndex(2);
            DrawManualSortHeader("Item", ManualSortColumn.Name);

            ImGui.TableSetColumnIndex(3);
            ImGui.TableHeader("Quality");

            ImGui.TableSetColumnIndex(4);
            DrawManualSortHeader("Have", ManualSortColumn.Quantity);

            ImGui.TableSetColumnIndex(5);
            DrawManualSortHeader("Value", ManualSortColumn.UnitPrice);

            ImGui.TableSetColumnIndex(6);
            ImGui.TableHeader("Stacks");

            var clipper = new ImGuiListClipper();
            clipper.Begin(visibleEntries.Count);
            while (clipper.Step())
            {
                for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                {
                    var entry = visibleEntries[i];
                    int selected = (int)Math.Min(int.MaxValue, GetManualQuantity(entry.ItemId));
                    ImGui.PushID((int)entry.ItemId);
                    ImGui.TableNextRow();

                    ImGui.TableSetColumnIndex(0);
                    bool tradeAll = GetManualQuantity(entry.ItemId) >= entry.Quantity && entry.Quantity > 0;
                    if (ImGui.Checkbox("##tradeall", ref tradeAll))
                    {
                        if (tradeAll)
                            _manualItemQuantities[entry.ItemId] = entry.Quantity;
                        else
                            _manualItemQuantities.Remove(entry.ItemId);

                        _manualSelectionVersion++;
                        selected = (int)Math.Min(int.MaxValue, GetManualQuantity(entry.ItemId));
                    }

                    ImGui.TableSetColumnIndex(1);
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputInt("##qty", ref selected))
                    {
                        int maxAvailable = (int)Math.Min(int.MaxValue, entry.Quantity);
                        if (selected > maxAvailable)
                            selected = 0;
                        else
                            selected = Math.Max(0, selected);

                        if (selected <= 0)
                            _manualItemQuantities.Remove(entry.ItemId);
                        else
                            _manualItemQuantities[entry.ItemId] = selected;

                        _manualSelectionVersion++;
                    }

                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextUnformatted(entry.Name);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"{entry.DisplayName}\nstack {entry.MaxTradeQuantity:N0}\nunit value {entry.UnitPrice:N0}");

                    ImGui.TableSetColumnIndex(3);
                    ImGui.TextUnformatted(entry.QualityLabel);

                    ImGui.TableSetColumnIndex(4);
                    ImGui.TextUnformatted(entry.Quantity.ToString("N0"));

                    ImGui.TableSetColumnIndex(5);
                    ImGui.TextUnformatted(entry.UnitPrice.ToString("N0"));

                    ImGui.TableSetColumnIndex(6);
                    ImGui.TextDisabled(entry.StackSources.Count.ToString("N0"));
                    ImGui.PopID();
                }
            }
            clipper.End();

            ImGui.EndTable();
        }
        ImGui.EndChild();

        float manualButtonWidth = (ImGui.GetContentRegionAvail().X - inlineButtonGap) * 0.5f;
        if (ImGui.Button("Build Manual Item Plan", new Vector2(manualButtonWidth, 24)))
        {
            _itemTradePlan = _plugin.InventoryWealth.BuildTradePlanFromSelections(_manualItemQuantities);
            _showSafetyPreview = false;
            DisableReceiverModeForSendPlan();
        }

        ImGui.SameLine(0f, inlineButtonGap);
        if (ImGui.Button("Clear Trade Plan", new Vector2(manualButtonWidth, 24)))
        {
            _itemTradePlan = InventoryTradePlan.Empty;
            _showSafetyPreview = false;
        }

        DrawTradePlanSummary(_itemTradePlan);
        ImGui.EndDisabled();
    }

    private void DrawGilToItemMode()
    {
        const float inlineButtonGap = 8f;
        var snapshot = _plugin.InventoryWealth.GetSnapshot();
        DrawAutoStartControls();
        ImGui.Spacing();
        ImGui.BeginDisabled(_tm.IsRunning);
        ImGui.TextColored(new Vector4(0.6f, 0.9f, 1f, 1f), $"Inventory Item Value: {snapshot.TotalItemValue:N0}");
        ImGui.Spacing();
        ImGui.Text("Gil to Item Trade");
        ImGui.TextWrapped("1. Enter the amount of gil");
        ImGui.TextWrapped("2. Select and press gil to item trade plan button");
        ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), "- Build item trade plan: build a trade plan using inventory items with a similar total value.");
        ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), "- Build salvaged item plan: build a trade plan using salvaged item only with a similar total value.");
        ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), "- Trade all salvaged item: build a trade plan to trade all the salvaged item.");
        ImGui.TextWrapped("3. Press auto start, then plugin will trade all item selected by the plan automatically.");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##itemTargetValue", ref _itemTargetValueText, 24);

        if (_pendingPlanTask is { IsCompleted: false })
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), "Computing plan...");
        ImGui.BeginDisabled(_pendingPlanTask is { IsCompleted: false });

        float fourButtonWidth = (ImGui.GetContentRegionAvail().X - (inlineButtonGap * 3f)) * 0.25f;

        if (ImGui.Button("Build Item Trade Plan", new Vector2(fourButtonWidth, 24)))
        {
            if (long.TryParse(_itemTargetValueText, out var targetValue) && targetValue > 0)
                StartPlanTask(() => _plugin.InventoryWealth.BuildTradePlan(targetValue));
            else
            {
                _itemTradePlan = InventoryTradePlan.Empty;
                _showSafetyPreview = false;
            }
        }

        ImGui.SameLine(0f, inlineButtonGap);
        if (ImGui.Button("Build Salvaged Item Plan", new Vector2(fourButtonWidth, 24)))
        {
            if (long.TryParse(_itemTargetValueText, out var targetValue) && targetValue > 0)
                StartPlanTask(() => _plugin.InventoryWealth.BuildSalvagedTradePlan(targetValue));
            else
            {
                _itemTradePlan = InventoryTradePlan.Empty;
                _showSafetyPreview = false;
            }
        }

        ImGui.SameLine(0f, inlineButtonGap);
        if (ImGui.Button("Trade All Salvaged Items", new Vector2(fourButtonWidth, 24)))
        {
            StartPlanTask(() => _plugin.InventoryWealth.BuildAllSalvagedTradePlan());
        }

        ImGui.SameLine(0f, inlineButtonGap);
        if (ImGui.Button("Clear Trade Plan", new Vector2(fourButtonWidth, 24)))
        {
            _itemTradePlan = InventoryTradePlan.Empty;
            _showSafetyPreview = false;
        }

        ImGui.EndDisabled();

        ImGui.TextWrapped("Salvaged-only includes Necklace, Earring, Bracelet, Ring, and Extravagant versions.");

        DrawTradePlanSummary(_itemTradePlan);
        ImGui.EndDisabled();
    }

    private void DrawTradePlanSummary(InventoryTradePlan plan)
    {
        if (plan.Entries.Count == 0)
            return;

        var batches = GetCachedBatches(plan);
        ImGui.Spacing();
        ImGui.TextColored(
            plan.IsExact ? new Vector4(0.5f, 1f, 0.5f, 1f) : new Vector4(1f, 0.8f, 0.4f, 1f),
            plan.IsExact
                ? $"Exact Plan: {plan.PlannedValue:N0}"
                : $"Closest Plan: {plan.PlannedValue:N0} (Remaining {plan.RemainingValue:N0})");
        ImGui.TextDisabled($"Trade windows needed: {batches.Count}");
        if (batches.Count > 0)
        {
            int totalIncomingSlots = batches.Sum(batch => batch.Chunks.Count);
            int maxIncomingSlots = batches.Max(batch => batch.Chunks.Count);
            ImGui.TextDisabled($"Receiver free slots needed: {maxIncomingSlots} per trade window ({totalIncomingSlots} incoming stack slots total)");
        }

        ImGui.Checkbox("Show plan details", ref _showPlanDetails);
        if (!_showPlanDetails)
            return;

        ImGui.BeginChild("itemPlanSummary", new Vector2(-1, 180), true);
        foreach (var entry in plan.Entries)
        {
            ImGui.TextUnformatted($"{entry.Name} x{entry.Quantity:N0} @ {entry.UnitPrice:N0} = {entry.TotalValue:N0}");
            ImGui.TextDisabled($"max per trade: {entry.MaxTradeQuantity:N0} | trade chunks: {FormatTradeChunks(entry.TradeChunks)}");
        }

        if (batches.Count > 0)
        {
            ImGui.Spacing();
            foreach (var batch in batches)
            {
                ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), $"Batch {batch.BatchNumber}: {batch.TotalValue:N0}");
                foreach (var chunk in batch.Chunks)
                {
                    ImGui.TextDisabled($"  {chunk.Name} x{chunk.Quantity:N0} @ {chunk.UnitPrice:N0}");
                }
            }
        }
        ImGui.EndChild();
    }

    private bool CanAutoStart()
    {
        return GetAutoStartBlockReason().Length == 0;
    }

    private string GetAutoStartBlockReason()
    {
        if (!_tm.CanAutoSend)
            return "Chat command path unavailable.";

        if (_targetName.Trim().Length == 0)
            return "Target player required for Auto Start.";

        if (!IsCurrentTargetPlayer(_targetName))
            return "You must target this player before Auto Start.";

        if (_mode == AutoTradeMode.Gil)
            return _tradeCount > 0 ? string.Empty : "Trade count must be greater than 0.";

        return _itemTradePlan.Entries.Count > 0 ? string.Empty : "Build a trade plan first.";
    }

    private void DrawAutoStartControls()
    {
        var blockReason = GetAutoStartBlockReason();
        bool canAuto = blockReason.Length == 0;
        float fullWidth = ImGui.GetContentRegionAvail().X;
        float gap = ImGui.GetStyle().ItemSpacing.X;
        float buttonWidth = (fullWidth - gap) * 0.5f;

        if (_showSafetyPreview && !_tm.IsRunning)
        {
            DrawSafetyPreview();
            ImGui.BeginDisabled(!canAuto);
            if (ImGui.Button("Confirm Auto Start", new Vector2(buttonWidth, 26)))
                StartAuto();
            ImGui.EndDisabled();
        }
        else
        {
            ImGui.BeginDisabled(!canAuto);
            if (ImGui.Button("Auto Start", new Vector2(buttonWidth, 26)))
                _showSafetyPreview = true;
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        if (ImGui.Button("Stop", new Vector2(buttonWidth, 26)))
            _tm.Stop();

        if (blockReason.Length > 0)
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.35f, 1f), blockReason);
    }

    private void DrawSafetyPreview()
    {
        ImGui.BeginChild("safetyPreview", new Vector2(-1, 92), true);
        ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), "Safety Preview");
        ImGui.TextUnformatted($"Target: {_targetName.Trim()}");
        if (_mode == AutoTradeMode.Gil)
            ImGui.TextUnformatted($"Plan: {FormatGilTradePlanSummary(BuildGilTradePlan(_totalGil))}");
        else
        {
            var batches = GetCachedBatches(_itemTradePlan);
            ImGui.TextUnformatted($"Plan: {_itemTradePlan.PlannedValue:N0} value, {batches.Count} trade window(s), {_itemTradePlan.Entries.Count} item type(s)");
        }
        ImGui.EndChild();
    }

    private IReadOnlyList<InventoryTradeBatch> GetCachedBatches(InventoryTradePlan plan)
    {
        if (plan.Entries.Count == 0)
            return Array.Empty<InventoryTradeBatch>();

        if (SamePlan(_cachedBatchPlan, plan))
            return _cachedBatches;

        _cachedBatchPlan = plan;
        _cachedBatches = _plugin.InventoryWealth.SplitIntoTradeBatches(plan);
        return _cachedBatches;
    }

    private IReadOnlyList<InventoryWealthEntry> GetCachedManualVisibleEntries(InventoryWealthSnapshot snapshot)
    {
        if (ReferenceEquals(_cachedManualSnapshotEntries, snapshot.Entries) &&
            string.Equals(_cachedManualFilter, _manualItemFilter, StringComparison.Ordinal) &&
            _cachedManualShowSelectedOnly == _manualShowSelectedOnly &&
            _cachedManualSelectionVersion == _manualSelectionVersion)
        {
            return _cachedManualVisibleEntries;
        }

        _cachedManualSnapshotEntries = snapshot.Entries;
        _cachedManualFilter = _manualItemFilter;
        _cachedManualShowSelectedOnly = _manualShowSelectedOnly;
        _cachedManualSelectionVersion = _manualSelectionVersion;

        _cachedManualVisibleEntries = snapshot.Entries
            .Where(entry => !_manualShowSelectedOnly || GetManualQuantity(entry.ItemId) > 0)
            .Where(entry => string.IsNullOrWhiteSpace(_manualItemFilter) ||
                            entry.Name.Contains(_manualItemFilter, StringComparison.OrdinalIgnoreCase) ||
                            entry.DisplayName.Contains(_manualItemFilter, StringComparison.OrdinalIgnoreCase) ||
                            entry.QualityLabel.Contains(_manualItemFilter, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        _cachedManualVisibleEntries = SortManualEntries(_cachedManualVisibleEntries);

        return _cachedManualVisibleEntries;
    }

    private IReadOnlyList<InventoryWealthEntry> SortManualEntries(IReadOnlyList<InventoryWealthEntry> entries)
    {
        var ordered = _manualSortColumn switch
        {
            ManualSortColumn.Quantity => _manualSortAscending
                ? entries.OrderBy(entry => entry.Quantity)
                    .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.IsHighQuality ? 1 : 0)
                : entries.OrderByDescending(entry => entry.Quantity)
                    .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(entry => entry.IsHighQuality ? 1 : 0),
            ManualSortColumn.UnitPrice => _manualSortAscending
                ? entries.OrderBy(entry => entry.UnitPrice)
                    .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.IsHighQuality ? 1 : 0)
                : entries.OrderByDescending(entry => entry.UnitPrice)
                    .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(entry => entry.IsHighQuality ? 1 : 0),
            _ => _manualSortAscending
                ? entries.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.IsHighQuality ? 1 : 0)
                : entries.OrderByDescending(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(entry => entry.IsHighQuality ? 1 : 0),
        };

        return ordered.ToArray();
    }

    private void DrawManualSortHeader(string label, ManualSortColumn column)
    {
        string suffix = _manualSortColumn == column ? (_manualSortAscending ? " ^" : " v") : string.Empty;
        if (ImGui.Selectable(label + suffix, false, ImGuiSelectableFlags.SpanAllColumns))
        {
            if (_manualSortColumn == column)
                _manualSortAscending = !_manualSortAscending;
            else
            {
                _manualSortColumn = column;
                _manualSortAscending = true;
            }

            _cachedManualSelectionVersion = -1;
        }
    }

    private static bool SamePlan(InventoryTradePlan left, InventoryTradePlan right)
    {
        return left.TargetValue == right.TargetValue &&
               left.PlannedValue == right.PlannedValue &&
               left.RemainingValue == right.RemainingValue &&
               left.IsExact == right.IsExact &&
               ReferenceEquals(left.Entries, right.Entries);
    }

    private void StartPlanTask(Func<InventoryTradePlan> builder)
    {
        if (_pendingPlanTask is { IsCompleted: false })
            return;

        _pendingPlanTask = Task.Run(builder);
        _showSafetyPreview = false;
    }

    private void PollPlanTask()
    {
        if (_pendingPlanTask is not { IsCompleted: true } task)
            return;

        if (task.IsCompletedSuccessfully)
        {
            _itemTradePlan = task.Result;
            DisableReceiverModeForSendPlan();
        }
        else if (task.IsFaulted)
        {
            BackstabTheTrade.Log.Error(task.Exception, "BuildTradePlan task failed");
            _itemTradePlan = InventoryTradePlan.Empty;
        }

        _pendingPlanTask = null;
    }

    public bool CanManualStart()
    {
        return _mode == AutoTradeMode.Gil ? _tradeCount > 0 : _itemTradePlan.Entries.Count > 0;
    }

    private void StartAuto()
    {
        if (!CanAutoStart())
            return;

        DisableReceiverMode();
        _recentTargetName = _targetName.Trim();
        _showSafetyPreview = false;
        if (_mode == AutoTradeMode.Gil)
            _tm.StartAutoSend(_targetName.Trim(), _totalGil);
        else
            _tm.StartAutoSendItems(_targetName.Trim(), _itemTradePlan, _mode);
    }

    public void StartManual()
    {
        DisableReceiverMode();
        if (_mode == AutoTradeMode.Gil)
            _tm.StartManual(_totalGil, _targetName.Trim());
        else
            _tm.StartManualItems(_itemTradePlan, _mode, _targetName.Trim());
    }

    private void DisableReceiverModeForSendPlan()
    {
        if (_itemTradePlan.Entries.Count > 0)
            DisableReceiverMode();
    }

    private void EnableReceiverMode()
    {
        ClearSendPlanState();
        _plugin.Configuration.ReceiverModeAutoConfirm = true;
        _plugin.Configuration.Save();
    }

    private void DisableReceiverMode()
    {
        if (!_plugin.Configuration.ReceiverModeAutoConfirm)
            return;

        _plugin.Configuration.ReceiverModeAutoConfirm = false;
        _plugin.Configuration.Save();
    }

    private void ClearSendPlanState()
    {
        _itemTradePlan = InventoryTradePlan.Empty;
        _manualItemQuantities.Clear();
        _showSafetyPreview = false;
    }

    private void ClearTargetName(string reason)
    {
        if (_targetName.Trim().Length == 0)
            return;

        _targetName = string.Empty;
        _plugin.EventTracker.LogExternal($"[TargetMonitor] Cleared target player: {reason}.");
    }

    private static bool IsCurrentTargetPlayer(string targetName)
    {
        var trimmed = targetName.Trim();
        if (trimmed.Length == 0)
            return false;

        var target = BackstabTheTrade.TargetManager.Target;
        return target != null &&
               (int)target.ObjectKind == PlayerObjectKindValue &&
               string.Equals(target.Name.TextValue, trimmed, StringComparison.OrdinalIgnoreCase);
    }

    private long GetManualQuantity(uint itemId)
    {
        return _manualItemQuantities.TryGetValue(itemId, out var quantity) ? quantity : 0;
    }

    private void RecalcTrades()
    {
        if (_totalGil <= 0)
        {
            _tradeCount = 0;
            return;
        }

        _tradeCount = BuildGilTradePlan(_totalGil).Count;
    }

    private static string FormatTradeChunks(IReadOnlyList<long> chunks)
    {
        if (chunks.Count == 0)
            return "-";

        var sb = new StringBuilder();
        for (int i = 0; i < chunks.Count; i++)
        {
            if (i > 0)
                sb.Append(" + ");
            sb.Append(chunks[i]);
        }
        return sb.ToString();
    }

    private static IReadOnlyList<long> BuildGilTradePlan(long totalGil)
    {
        if (totalGil <= 0)
            return Array.Empty<long>();

        var plan = new List<long>();
        long remaining = totalGil;
        while (remaining > 0)
        {
            long next = Math.Min(1_000_000, remaining);
            plan.Add(next);
            remaining -= next;
        }

        return plan;
    }

    private static string FormatGilTradePlanSummary(IReadOnlyList<long> plan)
    {
        if (plan.Count == 0)
            return "= 0 trade(s)";

        long total = plan.Sum();
        var grouped = plan
            .GroupBy(value => value)
            .OrderByDescending(group => group.Key)
            .Select(group => group.Count() == 1
                ? group.Key.ToString("N0")
                : $"{group.Key:N0} x{group.Count()}");

        return $"= {plan.Count} trade(s): {string.Join(" + ", grouped)} = {total:N0} gil";
    }

    public void Dispose()
    {
    }
}

