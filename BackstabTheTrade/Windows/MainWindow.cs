using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;

namespace BackstabTheTrade;

public sealed class MainWindow : Window, IDisposable
{
    private const int PlayerObjectKindValue = 1;
    private const string KoFiUrl = "https://ko-fi.com/ray_cr";

    private enum SidebarPage
    {
        Main,
        GilTrade,
        ItemManual,
        GilToItem,
        ReceiverMode,
        TrackerWindows,
        TimeSettings,
        UiLanguage,
    }

    private enum ManualSortColumn
    {
        Name,
        Quantity,
        UnitPrice,
    }

    private enum UiLanguage
    {
        English,
        TraditionalChinese,
        SimplifiedChinese,
        Japanese,
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

        AddKoFiTitleBarButton();
    }

    public void SetTarget(string name)
    {
        _targetName = name;
        if (!string.IsNullOrWhiteSpace(name))
            _recentTargetName = name.Trim();
    }

    private void AddKoFiTitleBarButton()
    {
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Heart,
            IconColor = new Vector4(1f, 0.95f, 0.95f, 1f),
            ShowTooltip = () => ImGui.SetTooltip("Ko-Fi"),
            Click = _ => OpenUrl(KoFiUrl),
            Priority = int.MaxValue,
        });
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
        DrawSidebarButton(SidebarPage.Main, T("sidebar.main"));
        DrawSidebarButton(SidebarPage.GilTrade, T("sidebar.gilTrade"));
        DrawSidebarButton(SidebarPage.ItemManual, T("sidebar.itemManual"));
        DrawSidebarButton(SidebarPage.GilToItem, T("sidebar.gilToItem"));
        DrawSidebarButton(SidebarPage.ReceiverMode, T("sidebar.receiver"));
        DrawSidebarButton(SidebarPage.TrackerWindows, T("sidebar.tracker"));
        DrawSidebarButton(SidebarPage.TimeSettings, T("sidebar.time"));
        DrawSidebarButton(SidebarPage.UiLanguage, T("sidebar.uiLanguage"));
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
                DrawTradePageHeader(T("sidebar.gilTrade"), running);
                DrawGilMode();
                ImGui.Spacing();
                DrawAutoStartControls();
                if (!running)
                    ImGui.TextWrapped("Tip: If Auto Start fails, open Trade manually, then use 'Take Over Open Trade Window' in the tracker.");
                DrawRunningStatus();
                break;
            case SidebarPage.ItemManual:
                _mode = AutoTradeMode.ItemManual;
                DrawTradePageHeader(T("sidebar.itemManual"), running);
                DrawManualItemMode();
                DrawRunningStatus();
                break;
            case SidebarPage.GilToItem:
                _mode = AutoTradeMode.GilToItem;
                DrawTradePageHeader(T("sidebar.gilToItem"), running);
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
            case SidebarPage.UiLanguage:
                DrawUiLanguagePage();
                break;
        }
    }

    private void DrawMainPage()
    {
        var snapshot = _plugin.InventoryWealth.GetSnapshot();
        ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), T("main.title"));
        ImGui.TextDisabled(T("main.desc"));
        ImGui.Separator();

        ImGui.TextColored(new Vector4(0.95f, 0.85f, 0.3f, 1f), $"{T("main.playerGil")}: {snapshot.PlayerGil:N0}");
        ImGui.TextColored(new Vector4(0.6f, 0.9f, 1f, 1f), $"{T("main.itemValue")}: {snapshot.TotalItemValue:N0}");
        ImGui.TextColored(new Vector4(0.5f, 1f, 0.5f, 1f), $"{T("main.totalWealth")}: {snapshot.TotalWealth:N0}");
        ImGui.Spacing();

        if (ImGui.Button(T("main.refresh"), new Vector2(-1, 24)))
            _plugin.InventoryWealth.Invalidate();

        ImGui.Spacing();
        ImGui.BeginChild("main_inventory_items", new Vector2(-1, -1), true);
        if (snapshot.Entries.Count == 0)
        {
            ImGui.TextDisabled(T("main.noItems"));
        }
        else if (ImGui.BeginTable("main_inventory_table", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY))
        {
            ImGui.TableSetupColumn(T("table.item"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(T("table.have"), ImGuiTableColumnFlags.WidthFixed, 82);
            ImGui.TableSetupColumn(T("table.value"), ImGuiTableColumnFlags.WidthFixed, 88);
            ImGui.TableSetupColumn(T("table.stacks"), ImGuiTableColumnFlags.WidthFixed, 64);
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
        ImGui.Text(T("shared.targetPlayer"));
        ImGui.SetNextItemWidth(-1);
        ImGui.BeginDisabled(running);
        ImGui.InputText("##target_trade_page", ref _targetName, 64);
        ImGui.EndDisabled();
        if (!string.IsNullOrWhiteSpace(_recentTargetName))
        {
            ImGui.BeginDisabled(running);
            if (ImGui.SmallButton($"{T("shared.useRecentTarget")}: {_recentTargetName}"))
                _targetName = _recentTargetName;
            ImGui.EndDisabled();
        }
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void DrawReceiverPage(bool running, bool hasBuiltSendPlan)
    {
        var cfg = _plugin.Configuration;
        ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), T("sidebar.receiver"));
        ImGui.TextWrapped(T("receiver.desc"));
        ImGui.Separator();

        bool receiverMode = cfg.ReceiverModeAutoConfirm;
        ImGui.BeginDisabled(running || hasBuiltSendPlan);
        if (ImGui.Checkbox(T("receiver.checkbox"), ref receiverMode))
        {
            cfg.ReceiverModeAutoConfirm = receiverMode;
            cfg.Save();
        }
        ImGui.EndDisabled();

        if (ImGui.Button(T("receiver.enable"), new Vector2(190, 0)))
        {
            EnableReceiverMode();
        }

        ImGui.SameLine();
        if (ImGui.Button(T("receiver.disable"), new Vector2(190, 0)))
        {
            DisableReceiverMode();
        }

        ImGui.Spacing();
        if (hasBuiltSendPlan)
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.35f, 1f), T("receiver.lockedByPlan"));
        if (running)
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.35f, 1f), T("receiver.lockedByRunning"));

        ImGui.TextWrapped(T("receiver.note"));
    }

    private void DrawTrackerWindowsPage()
    {
        ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), T("sidebar.tracker"));
        ImGui.TextWrapped(T("tracker.desc"));
        ImGui.Separator();

        float trackerButtonGap = 8f;
        float trackerButtonWidth = (ImGui.GetContentRegionAvail().X - trackerButtonGap) * 0.5f;

        if (ImGui.Button(T("tracker.openLog"), new Vector2(trackerButtonWidth, 26)))
            _openTracker();
        ImGui.SameLine(0f, trackerButtonGap);
        if (ImGui.Button(T("tracker.openHistory"), new Vector2(trackerButtonWidth, 26)))
            _openTradeHistory();

        ImGui.Spacing();
        ImGui.TextWrapped(T("tracker.note"));
    }

    private void DrawTimeSettingsPage()
    {
        var cfg = _plugin.Configuration;
        ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), T("time.title"));
        ImGui.TextDisabled(T("time.desc"));
        ImGui.Separator();
        DrawTimingControls(cfg);
    }

    private void DrawUiLanguagePage()
    {
        ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), T("ui.title"));
        ImGui.TextDisabled(T("ui.desc"));
        ImGui.Separator();

        DrawLanguageButton(UiLanguage.English, "English");
        DrawLanguageButton(UiLanguage.TraditionalChinese, "\u7E41\u9AD4\u4E2D\u6587");
        DrawLanguageButton(UiLanguage.SimplifiedChinese, "\u7C21\u9AD4\u4E2D\u6587");
        DrawLanguageButton(UiLanguage.Japanese, "\u65E5\u6587");
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
        DrawTimingSlider(cfg.YesConfirmRetryDelayMs, 50, 1000, "timing_embedded_yes_retry", "Yes button retry", "80-150", v => cfg.YesConfirmRetryDelayMs = v, cfg);

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
        ImGui.TextColored(new Vector4(0.95f, 0.85f, 0.3f, 1f), $"{T("main.playerGil")}: {playerGil:N0}");
        ImGui.Spacing();
        ImGui.Text(T("gil.totalGilTrade"));
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
                    _gilModeValidationMessage = T("validation.notEnoughGil");
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
        ImGui.Text(T("manual.title"));
        ImGui.TextWrapped(T("manual.step1"));
        ImGui.TextWrapped(T("manual.step2"));
        ImGui.TextWrapped(T("manual.step3"));

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##manualItemFilter", T("manual.filterHint"), ref _manualItemFilter, 64);
        ImGui.Checkbox(T("manual.showSelectedOnly"), ref _manualShowSelectedOnly);
        ImGui.SameLine();
        if (ImGui.SmallButton(T("manual.clearQty")))
        {
            _manualItemQuantities.Clear();
            _manualSelectionVersion++;
            _itemTradePlan = InventoryTradePlan.Empty;
        }
        ImGui.SameLine();
        if (ImGui.SmallButton(T("manual.refresh")))
        {
            _plugin.InventoryWealth.Invalidate();
            _itemTradePlan = _plugin.InventoryWealth.BuildTradePlanFromSelections(_manualItemQuantities);
            DisableReceiverModeForSendPlan();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton(T("manual.openInventory")))
            _openInventoryWealth();
        ImGui.SameLine();
        if (ImGui.SmallButton(T("manual.selectAllSalvaged")))
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
            ImGui.TableSetupColumn(T("manual.all"), ImGuiTableColumnFlags.WidthFixed, 42);
            ImGui.TableSetupColumn(T("manual.qty"), ImGuiTableColumnFlags.WidthFixed, 82);
            ImGui.TableSetupColumn(T("table.item"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(T("manual.quality"), ImGuiTableColumnFlags.WidthFixed, 62);
            ImGui.TableSetupColumn(T("table.have"), ImGuiTableColumnFlags.WidthFixed, 68);
            ImGui.TableSetupColumn(T("table.value"), ImGuiTableColumnFlags.WidthFixed, 78);
            ImGui.TableSetupColumn(T("table.stacks"), ImGuiTableColumnFlags.WidthFixed, 62);

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
                ImGui.SetTooltip(T("manual.visibleTooltip"));

            ImGui.TableSetColumnIndex(1);
            ImGui.TableHeader(T("manual.qty"));

            ImGui.TableSetColumnIndex(2);
            DrawManualSortHeader(T("table.item"), ManualSortColumn.Name);

            ImGui.TableSetColumnIndex(3);
            ImGui.TableHeader(T("manual.quality"));

            ImGui.TableSetColumnIndex(4);
            DrawManualSortHeader(T("table.have"), ManualSortColumn.Quantity);

            ImGui.TableSetColumnIndex(5);
            DrawManualSortHeader(T("table.value"), ManualSortColumn.UnitPrice);

            ImGui.TableSetColumnIndex(6);
            ImGui.TableHeader(T("table.stacks"));

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
        if (ImGui.Button(T("manual.buildPlan"), new Vector2(manualButtonWidth, 24)))
        {
            _itemTradePlan = _plugin.InventoryWealth.BuildTradePlanFromSelections(_manualItemQuantities);
            _showSafetyPreview = false;
            DisableReceiverModeForSendPlan();
        }

        ImGui.SameLine(0f, inlineButtonGap);
        if (ImGui.Button(T("shared.clearTradePlan"), new Vector2(manualButtonWidth, 24)))
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
        ImGui.TextColored(new Vector4(0.6f, 0.9f, 1f, 1f), $"{T("gilToItem.inventoryItemValue")}: {snapshot.TotalItemValue:N0}");
        ImGui.Spacing();
        ImGui.Text(T("gilToItem.title"));
        ImGui.TextWrapped(T("gilToItem.step1"));
        ImGui.TextWrapped(T("gilToItem.step2"));
        ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), T("gilToItem.line1"));
        ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), T("gilToItem.line2"));
        ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), T("gilToItem.line3"));
        ImGui.TextWrapped(T("gilToItem.step3"));
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##itemTargetValue", ref _itemTargetValueText, 24);

        if (_pendingPlanTask is { IsCompleted: false })
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), T("gilToItem.computing"));
        ImGui.BeginDisabled(_pendingPlanTask is { IsCompleted: false });

        float fourButtonWidth = (ImGui.GetContentRegionAvail().X - (inlineButtonGap * 3f)) * 0.25f;

        if (ImGui.Button(T("gilToItem.buildPlan"), new Vector2(fourButtonWidth, 24)))
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
        if (ImGui.Button(T("gilToItem.buildSalvaged"), new Vector2(fourButtonWidth, 24)))
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
        if (ImGui.Button(T("gilToItem.tradeAllSalvaged"), new Vector2(fourButtonWidth, 24)))
        {
            StartPlanTask(() => _plugin.InventoryWealth.BuildAllSalvagedTradePlan());
        }

        ImGui.SameLine(0f, inlineButtonGap);
        if (ImGui.Button(T("shared.clearTradePlan"), new Vector2(fourButtonWidth, 24)))
        {
            _itemTradePlan = InventoryTradePlan.Empty;
            _showSafetyPreview = false;
        }

        ImGui.EndDisabled();

        ImGui.TextWrapped(T("gilToItem.note"));

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

    private UiLanguage GetUiLanguage()
    {
        return (_plugin.Configuration.UiLanguageCode ?? "en").ToLowerInvariant() switch
        {
            "zh-hant" => UiLanguage.TraditionalChinese,
            "zh-hans" => UiLanguage.SimplifiedChinese,
            "ja" => UiLanguage.Japanese,
            _ => UiLanguage.English,
        };
    }

    private void SetUiLanguage(UiLanguage language)
    {
        _plugin.Configuration.UiLanguageCode = language switch
        {
            UiLanguage.TraditionalChinese => "zh-hant",
            UiLanguage.SimplifiedChinese => "zh-hans",
            UiLanguage.Japanese => "ja",
            _ => "en",
        };
        _plugin.Configuration.Save();
    }

    private void DrawLanguageButton(UiLanguage language, string label)
    {
        bool selected = GetUiLanguage() == language;
        if (selected)
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.28f, 0.28f, 0.32f, 1f));

        if (ImGui.Button(label, new Vector2(-1, 28)))
            SetUiLanguage(language);

        if (selected)
            ImGui.PopStyleColor();
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            BackstabTheTrade.Log.Warning(ex, "Failed to open support URL.");
        }
    }

    private string T(string key)
    {
        return GetUiLanguage() switch
        {
            UiLanguage.TraditionalChinese => GetTraditionalChineseText(key),
            UiLanguage.SimplifiedChinese => GetSimplifiedChineseText(key),
            UiLanguage.Japanese => GetJapaneseText(key),
            _ => GetEnglishText(key),
        };
    }

    private static string GetEnglishText(string key)
    {
        return key switch
        {
            "sidebar.main" => "Main",
            "sidebar.gilTrade" => "Gil Trade Mode",
            "sidebar.itemManual" => "Item Manual Mode",
            "sidebar.gilToItem" => "Gil to Item Mode",
            "sidebar.receiver" => "Receiver Mode",
            "sidebar.tracker" => "Tracker + Windows",
            "sidebar.time" => "Time Settings",
            "sidebar.uiLanguage" => "UI Language",
            "time.title" => "Time Settings",
            "time.desc" => "Time settings are independent from the trade mode pages.",
            "ui.title" => "UI Language",
            "ui.desc" => "Choose the language used by the main plugin UI.",
            "main.title" => "Main",
            "main.desc" => "Inventory Items are shown here directly, including your gil summary.",
            "main.refresh" => "Refresh Inventory Items",
            "main.noItems" => "No tradeable inventory items found.",
            "main.playerGil" => "Player Gil",
            "main.itemValue" => "Item Value",
            "main.totalWealth" => "Total Wealth",
            "table.item" => "Item",
            "table.have" => "Have",
            "table.value" => "Value",
            "table.stacks" => "Stacks",
            "shared.targetPlayer" => "Target Player Name:",
            "shared.useRecentTarget" => "Use recent target",
            "receiver.title" => "Receiver Mode",
            "receiver.desc" => "Automatically press Trade and confirm Yes while you are receiving a trade.",
            "receiver.checkbox" => "Receiver mode",
            "receiver.enable" => "Enable receiver mode",
            "receiver.disable" => "Disable receiver mode",
            "receiver.lockedByPlan" => "Receive mode is locked because a send trade plan exists.",
            "receiver.lockedByRunning" => "Receive mode cannot be changed while trading is running.",
            "receiver.note" => "Enable receiver mode clears the current send plan and turns receiver mode on.",
            "tracker.title" => "Tracker + Windows",
            "tracker.desc" => "Open the helper windows from here.",
            "tracker.openLog" => "Open Track Log",
            "tracker.openHistory" => "Open Trade History Window",
            "tracker.note" => "Track Log opens the diagnostic tracker. Trade History Window opens the trade history page.",
            "gil.totalGilTrade" => "Total Gil Trade:",
            "manual.title" => "Manual Item Selection:",
            "manual.step1" => "1. Select and enter the quantity you want to trade. The plugin plans from items that already exist in your inventory.",
            "manual.step2" => "2. Press the build item manual plan, confirm the target player.",
            "manual.step3" => "3. Press auto start, then plugin will trade all item selected by the plan automatically.",
            "manual.filterHint" => "Filter item name...",
            "manual.showSelectedOnly" => "Show selected only",
            "manual.clearQty" => "Clear Qty",
            "manual.refresh" => "Refresh",
            "manual.openInventory" => "Open Inventory Items",
            "manual.selectAllSalvaged" => "Select all salvaged item",
            "manual.all" => "All",
            "manual.qty" => "Qty",
            "manual.quality" => "Quality",
            "manual.visibleTooltip" => "Trade all currently visible items",
            "manual.buildPlan" => "Build Manual Item Plan",
            "shared.clearTradePlan" => "Clear Trade Plan",
            "gilToItem.inventoryItemValue" => "Inventory Item Value",
            "gilToItem.title" => "Gil to Item Mode",
            "gilToItem.step1" => "1. Enter the amount of gil",
            "gilToItem.step2" => "2. Select and press gil to item trade plan button",
            "gilToItem.step3" => "3. Press auto start, then plugin will trade all item selected by the plan automatically.",
            "gilToItem.line1" => "- Build item trade plan: build a trade plan using inventory items with a similar total value.",
            "gilToItem.line2" => "- Build salvaged item plan: build a trade plan using salvaged item only with a similar total value.",
            "gilToItem.line3" => "- Trade all salvaged item: build a trade plan to trade all the salvaged item.",
            "gilToItem.computing" => "Computing plan...",
            "gilToItem.buildPlan" => "Build Item Trade Plan",
            "gilToItem.buildSalvaged" => "Build Salvaged Item Plan",
            "gilToItem.tradeAllSalvaged" => "Trade All Salvaged Items",
            "gilToItem.note" => "Salvaged-only includes Necklace, Earring, Bracelet, Ring, and Extravagant versions.",
            _ => key,
        };
    }

    private static string GetTraditionalChineseText(string key)
    {
        return key switch
        {
            "sidebar.main" => "\u4E3B\u9801",
            "sidebar.uiLanguage" => "UI Language",
            "time.title" => "Time Settings",
            "time.desc" => "\u9019\u4E9B\u6642\u9593\u8A2D\u5B9A\u8207\u5404\u4EA4\u6613\u9801\u9762\u7368\u7ACB\u3002",
            "ui.title" => "UI Language",
            "ui.desc" => "\u9078\u64C7\u4E3B UI \u8981\u4F7F\u7528\u7684\u8A9E\u8A00\u3002",
            "main.title" => "\u4E3B\u9801",
            "main.desc" => "\u9019\u88E1\u6703\u76F4\u63A5\u986F\u793A\u80CC\u5305\u7269\u54C1\u8207 gil \u7E3D\u89BD\u3002",
            "main.refresh" => "\u91CD\u65B0\u6574\u7406\u80CC\u5305\u7269\u54C1",
            "main.noItems" => "\u627E\u4E0D\u5230\u53EF\u4EA4\u6613\u7684\u80CC\u5305\u7269\u54C1\u3002",
            "main.playerGil" => "\u73A9\u5BB6 Gil",
            "main.itemValue" => "\u7269\u54C1\u50F9\u503C",
            "main.totalWealth" => "\u7E3D\u8CC7\u7522",
            "table.item" => "\u7269\u54C1",
            "table.have" => "\u6301\u6709",
            "table.value" => "\u50F9\u503C",
            "table.stacks" => "\u7D44\u6578",
            "shared.targetPlayer" => "\u76EE\u6A19\u73A9\u5BB6\u540D\u7A31:",
            "shared.useRecentTarget" => "\u4F7F\u7528\u6700\u8FD1\u76EE\u6A19",
            "receiver.desc" => "\u7576\u4F60\u662F\u63A5\u6536\u65B9\u6642\uff0c\u81EA\u52D5\u6309 Trade \u4E26\u78BA\u8A8D Yes\u3002",
            "receiver.enable" => "\u555F\u7528 receiver mode",
            "receiver.disable" => "\u505C\u7528 receiver mode",
            "receiver.lockedByPlan" => "\u56E0\u70BA\u5DF2\u6709\u767C\u9001\u4EA4\u6613\u8A08\u5283\uff0Creceiver mode \u5DF2\u88AB\u9396\u5B9A\u3002",
            "receiver.lockedByRunning" => "\u4EA4\u6613\u9032\u884C\u4E2D\u7121\u6CD5\u5207\u63DB receiver mode\u3002",
            "receiver.note" => "\u555F\u7528 receiver mode \u6703\u6E05\u7A7A\u76EE\u524D\u767C\u9001\u8A08\u5283\uff0C\u4E26\u958B\u555F receiver mode\u3002",
            "tracker.desc" => "\u53EF\u4EE5\u5F9E\u9019\u88E1\u6253\u958B\u8F14\u52A9\u8996\u7A97\u3002",
            "tracker.openLog" => "\u6253\u958B Track Log",
            "tracker.openHistory" => "\u6253\u958B Trade History Window",
            "tracker.note" => "Track Log \u6703\u6253\u958B tracker\u3002Trade History Window \u6703\u6253\u958B\u4EA4\u6613\u8A18\u9304\u3002",
            "gil.totalGilTrade" => "\u7E3D Gil \u4EA4\u6613\u91D1\u984D:",
            "manual.title" => "Manual Item Selection:",
            "manual.step1" => "1. \u9078\u64C7\u4E26\u8F38\u5165\u60F3\u4EA4\u6613\u7684\u6578\u91CF\u3002\u63D2\u4EF6\u6703\u6839\u64DA\u80CC\u5305\u73FE\u6709\u7269\u54C1\u898F\u5283\u3002",
            "manual.step2" => "2. \u6309 build item manual plan\uff0C\u78BA\u8A8D\u76EE\u6A19\u73A9\u5BB6\u3002",
            "manual.step3" => "3. \u6309 auto start \u5F8C\uff0C\u63D2\u4EF6\u6703\u81EA\u52D5\u4EA4\u6613\u8A08\u5283\u5167\u7684\u6240\u6709\u7269\u54C1\u3002",
            "manual.filterHint" => "\u7BE9\u9078\u7269\u54C1\u540D\u7A31...",
            "manual.showSelectedOnly" => "\u53EA\u986F\u793A\u5DF2\u9078\u64C7",
            "manual.clearQty" => "\u6E05\u9664\u6578\u91CF",
            "manual.refresh" => "\u91CD\u65B0\u6574\u7406",
            "manual.openInventory" => "\u6253\u958B Inventory Items",
            "manual.selectAllSalvaged" => "\u5168\u9078 salvaged item",
            "manual.all" => "\u5168\u9078",
            "manual.qty" => "\u6578\u91CF",
            "manual.quality" => "\u54C1\u8CEA",
            "manual.visibleTooltip" => "\u4EA4\u6613\u76EE\u524D\u986F\u793A\u7684\u6240\u6709\u7269\u54C1",
            "manual.buildPlan" => "Build Manual Item Plan",
            "shared.clearTradePlan" => "\u6E05\u9664\u4EA4\u6613\u8A08\u5283",
            "gilToItem.inventoryItemValue" => "\u80CC\u5305\u7269\u54C1\u50F9\u503C",
            "gilToItem.title" => "Gil to Item Mode",
            "gilToItem.step1" => "1. \u8F38\u5165 gil \u91D1\u984D",
            "gilToItem.step2" => "2. \u9078\u64C7\u4E26\u6309 gil to item trade plan \u6309\u9215",
            "gilToItem.step3" => "3. \u6309 auto start \u5F8C\uff0C\u63D2\u4EF6\u6703\u81EA\u52D5\u4EA4\u6613\u8A08\u5283\u5167\u7684\u6240\u6709\u7269\u54C1\u3002",
            "gilToItem.line1" => "- Build item trade plan: \u4F7F\u7528\u80CC\u5305\u4E2D\u63A5\u8FD1\u76F8\u540C\u7E3D\u50F9\u503C\u7684\u7269\u54C1\u5EFA\u7ACB\u4EA4\u6613\u8A08\u5283\u3002",
            "gilToItem.line2" => "- Build salvaged item plan: \u53EA\u4F7F\u7528 salvaged item \u5EFA\u7ACB\u63A5\u8FD1\u76F8\u540C\u7E3D\u50F9\u503C\u7684\u4EA4\u6613\u8A08\u5283\u3002",
            "gilToItem.line3" => "- Trade all salvaged item: \u5EFA\u7ACB\u628A\u6240\u6709 salvaged item \u5168\u90E8\u4EA4\u6613\u7684\u8A08\u5283\u3002",
            "gilToItem.computing" => "\u6B63\u5728\u8A08\u7B97\u8A08\u5283...",
            "gilToItem.buildPlan" => "Build Item Trade Plan",
            "gilToItem.buildSalvaged" => "Build Salvaged Item Plan",
            "gilToItem.tradeAllSalvaged" => "Trade All Salvaged Items",
            "gilToItem.note" => "salvaged-only \u5305\u62EC Necklace\u3001Earring\u3001Bracelet\u3001Ring \u548C Extravagant \u7248\u672C\u3002",
            _ => GetEnglishText(key),
        };
    }

    private static string GetSimplifiedChineseText(string key)
    {
        return key switch
        {
            "ui.desc" => "\u9009\u62E9\u4E3B UI \u8981\u4F7F\u7528\u7684\u8BED\u8A00\u3002",
            "main.title" => "\u4E3B\u9875",
            "main.desc" => "\u8FD9\u91CC\u4F1A\u76F4\u63A5\u663E\u793A\u80CC\u5305\u7269\u54C1\u548C gil \u603B\u89C8\u3002",
            "main.refresh" => "\u5237\u65B0\u80CC\u5305\u7269\u54C1",
            "main.noItems" => "\u627E\u4E0D\u5230\u53EF\u4EA4\u6613\u7684\u80CC\u5305\u7269\u54C1\u3002",
            "main.playerGil" => "\u73A9\u5BB6 Gil",
            "main.itemValue" => "\u7269\u54C1\u4EF7\u503C",
            "main.totalWealth" => "\u603B\u8D44\u4EA7",
            "table.item" => "\u7269\u54C1",
            "table.have" => "\u6301\u6709",
            "table.value" => "\u4EF7\u503C",
            "table.stacks" => "\u5806\u53E0",
            "shared.targetPlayer" => "\u76EE\u6807\u73A9\u5BB6\u540D\u79F0:",
            "shared.useRecentTarget" => "\u4F7F\u7528\u6700\u8FD1\u76EE\u6807",
            "receiver.desc" => "\u5F53\u4F60\u662F\u63A5\u6536\u65B9\u65F6\uff0C\u81EA\u52A8\u6309 Trade \u5E76\u786E\u8BA4 Yes\u3002",
            "receiver.enable" => "\u542F\u7528 receiver mode",
            "receiver.disable" => "\u505C\u7528 receiver mode",
            "receiver.lockedByPlan" => "\u56E0\u4E3A\u5DF2\u6709\u53D1\u9001\u4EA4\u6613\u8BA1\u5212\uff0Creceiver mode \u5DF2\u88AB\u9501\u5B9A\u3002",
            "receiver.lockedByRunning" => "\u4EA4\u6613\u8FDB\u884C\u4E2D\u65E0\u6CD5\u5207\u6362 receiver mode\u3002",
            "receiver.note" => "\u542F\u7528 receiver mode \u4F1A\u6E05\u7A7A\u5F53\u524D\u53D1\u9001\u8BA1\u5212\uff0C\u5E76\u5F00\u542F receiver mode\u3002",
            "tracker.desc" => "\u53EF\u4EE5\u5728\u8FD9\u91CC\u6253\u5F00\u8F85\u52A9\u7A97\u53E3\u3002",
            "tracker.openLog" => "\u6253\u5F00 Track Log",
            "tracker.openHistory" => "\u6253\u5F00 Trade History Window",
            "tracker.note" => "Track Log \u4F1A\u6253\u5F00 tracker\u3002Trade History Window \u4F1A\u6253\u5F00\u4EA4\u6613\u8BB0\u5F55\u3002",
            "time.desc" => "\u8FD9\u4E9B\u65F6\u95F4\u8BBE\u5B9A\u4E0E\u5404\u4EA4\u6613\u9875\u9762\u72EC\u7ACB\u3002",
            "gil.totalGilTrade" => "\u603B Gil \u4EA4\u6613\u91D1\u989D:",
            "manual.step1" => "1. \u9009\u62E9\u5E76\u8F93\u5165\u60F3\u4EA4\u6613\u7684\u6570\u91CF\u3002\u63D2\u4EF6\u4F1A\u6839\u636E\u80CC\u5305\u73B0\u6709\u7269\u54C1\u89C4\u5212\u3002",
            "manual.step2" => "2. \u6309 build item manual plan\uff0C\u786E\u8BA4\u76EE\u6807\u73A9\u5BB6\u3002",
            "manual.step3" => "3. \u6309 auto start \u540E\uff0C\u63D2\u4EF6\u4F1A\u81EA\u52A8\u4EA4\u6613\u8BA1\u5212\u5185\u7684\u6240\u6709\u7269\u54C1\u3002",
            "manual.filterHint" => "\u7B5B\u9009\u7269\u54C1\u540D\u79F0...",
            "manual.showSelectedOnly" => "\u53EA\u663E\u793A\u5DF2\u9009\u62E9",
            "manual.clearQty" => "\u6E05\u9664\u6570\u91CF",
            "manual.refresh" => "\u5237\u65B0",
            "manual.openInventory" => "\u6253\u5F00 Inventory Items",
            "manual.selectAllSalvaged" => "\u5168\u9009 salvaged item",
            "manual.all" => "\u5168\u9009",
            "manual.qty" => "\u6570\u91CF",
            "manual.quality" => "\u54C1\u8D28",
            "manual.visibleTooltip" => "\u4EA4\u6613\u5F53\u524D\u663E\u793A\u7684\u6240\u6709\u7269\u54C1",
            "shared.clearTradePlan" => "\u6E05\u9664\u4EA4\u6613\u8BA1\u5212",
            "gilToItem.inventoryItemValue" => "\u80CC\u5305\u7269\u54C1\u4EF7\u503C",
            "gilToItem.step1" => "1. \u8F93\u5165 gil \u91D1\u989D",
            "gilToItem.step2" => "2. \u9009\u62E9\u5E76\u6309 gil to item trade plan \u6309\u94AE",
            "gilToItem.step3" => "3. \u6309 auto start \u540E\uff0C\u63D2\u4EF6\u4F1A\u81EA\u52A8\u4EA4\u6613\u8BA1\u5212\u5185\u7684\u6240\u6709\u7269\u54C1\u3002",
            "gilToItem.line1" => "- Build item trade plan: \u4F7F\u7528\u80CC\u5305\u4E2D\u603B\u4EF7\u503C\u76F8\u8FD1\u7684\u7269\u54C1\u5EFA\u7ACB\u4EA4\u6613\u8BA1\u5212\u3002",
            "gilToItem.line2" => "- Build salvaged item plan: \u53EA\u4F7F\u7528 salvaged item \u5EFA\u7ACB\u603B\u4EF7\u503C\u76F8\u8FD1\u7684\u4EA4\u6613\u8BA1\u5212\u3002",
            "gilToItem.line3" => "- Trade all salvaged item: \u5EFA\u7ACB\u628A\u6240\u6709 salvaged item \u5168\u90E8\u4EA4\u6613\u7684\u8BA1\u5212\u3002",
            "gilToItem.computing" => "\u6B63\u5728\u8BA1\u7B97\u8BA1\u5212...",
            "gilToItem.note" => "salvaged-only \u5305\u62EC Necklace\u3001Earring\u3001Bracelet\u3001Ring \u548C Extravagant \u7248\u672C\u3002",
            _ => GetTraditionalChineseText(key),
        };
    }

    private static string GetJapaneseText(string key)
    {
        return key switch
        {
            "sidebar.main" => "\u30E1\u30A4\u30F3",
            "ui.desc" => "\u30E1\u30A4\u30F3 UI \u3067\u4F7F\u3046\u8A00\u8A9E\u3092\u9078\u629E\u3057\u307E\u3059\u3002",
            "main.title" => "\u30E1\u30A4\u30F3",
            "main.desc" => "\u3053\u3053\u3067\u306F\u6240\u6301\u54C1\u3068 gil \u306E\u7DCF\u89BD\u3092\u76F4\u63A5\u8868\u793A\u3057\u307E\u3059\u3002",
            "main.refresh" => "\u6240\u6301\u54C1\u3092\u66F4\u65B0",
            "main.noItems" => "\u4EA4\u6613\u53EF\u80FD\u306A\u6240\u6301\u54C1\u304C\u3042\u308A\u307E\u305B\u3093\u3002",
            "main.playerGil" => "\u6240\u6301 Gil",
            "main.itemValue" => "\u30A2\u30A4\u30C6\u30E0\u4FA1\u5024",
            "main.totalWealth" => "\u7DCF\u8CC7\u7523",
            "table.item" => "\u30A2\u30A4\u30C6\u30E0",
            "table.have" => "\u6240\u6301",
            "table.value" => "\u4FA1\u5024",
            "table.stacks" => "\u30B9\u30BF\u30C3\u30AF",
            "shared.targetPlayer" => "\u5BFE\u8C61\u30D7\u30EC\u30A4\u30E4\u30FC\u540D:",
            "shared.useRecentTarget" => "\u76F4\u8FD1\u306E\u5BFE\u8C61\u3092\u4F7F\u3046",
            "receiver.desc" => "\u53D7\u3051\u53D6\u308A\u5074\u306E\u3068\u304D\u306B Trade \u3068 Yes \u3092\u81EA\u52D5\u3067\u62BC\u3057\u307E\u3059\u3002",
            "receiver.enable" => "receiver mode \u3092\u6709\u52B9\u5316",
            "receiver.disable" => "receiver mode \u3092\u7121\u52B9\u5316",
            "receiver.lockedByPlan" => "\u9001\u4FE1\u30D7\u30E9\u30F3\u304C\u3042\u308B\u305F\u3081 receiver mode \u306F\u30ED\u30C3\u30AF\u4E2D\u3067\u3059\u3002",
            "receiver.lockedByRunning" => "\u4EA4\u6613\u4E2D\u306F receiver mode \u3092\u5909\u66F4\u3067\u304D\u307E\u305B\u3093\u3002",
            "receiver.note" => "receiver mode \u3092\u6709\u52B9\u306B\u3059\u308B\u3068\u3001\u73FE\u5728\u306E\u9001\u4FE1\u30D7\u30E9\u30F3\u3092\u6D88\u53BB\u3057\u3066 ON \u306B\u3057\u307E\u3059\u3002",
            "tracker.desc" => "\u3053\u3053\u304B\u3089\u88DC\u52A9\u30A6\u30A3\u30F3\u30C9\u30A6\u3092\u958B\u3051\u307E\u3059\u3002",
            "tracker.openLog" => "Track Log \u3092\u958B\u304F",
            "tracker.openHistory" => "Trade History Window \u3092\u958B\u304F",
            "tracker.note" => "Track Log \u306F tracker \u3092\u958B\u304D\u307E\u3059\u3002Trade History Window \u306F\u4EA4\u6613\u5C65\u6B74\u3092\u958B\u304D\u307E\u3059\u3002",
            "time.desc" => "\u3053\u308C\u3089\u306E\u6642\u9593\u8A2D\u5B9A\u306F\u5404\u4EA4\u6613\u30DA\u30FC\u30B8\u3068\u306F\u5225\u3067\u3059\u3002",
            "gil.totalGilTrade" => "\u4EA4\u6613\u3059\u308B Gil \u5408\u8A08:",
            "manual.step1" => "1. \u4EA4\u6613\u3057\u305F\u3044\u6570\u91CF\u3092\u9078\u629E\u3057\u3066\u5165\u529B\u3057\u307E\u3059\u3002\u6240\u6301\u54C1\u304B\u3089\u30D7\u30E9\u30F3\u3092\u4F5C\u6210\u3057\u307E\u3059\u3002",
            "manual.step2" => "2. build item manual plan \u3092\u62BC\u3057\u3001\u5BFE\u8C61\u30D7\u30EC\u30A4\u30E4\u30FC\u3092\u78BA\u8A8D\u3057\u307E\u3059\u3002",
            "manual.step3" => "3. auto start \u3092\u62BC\u3059\u3068\u3001\u30D7\u30E9\u30F3\u5185\u306E\u7269\u54C1\u3092\u81EA\u52D5\u3067\u4EA4\u6613\u3057\u307E\u3059\u3002",
            "manual.filterHint" => "\u30A2\u30A4\u30C6\u30E0\u540D\u3067\u7D5E\u308A\u8FBC\u307F...",
            "manual.showSelectedOnly" => "\u9078\u629E\u6E08\u307F\u306E\u307F\u8868\u793A",
            "manual.clearQty" => "\u6570\u91CF\u30AF\u30EA\u30A2",
            "manual.refresh" => "\u66F4\u65B0",
            "manual.openInventory" => "Inventory Items \u3092\u958B\u304F",
            "manual.selectAllSalvaged" => "salvaged item \u3092\u5168\u9078\u629E",
            "manual.all" => "\u5168\u9078\u629E",
            "manual.qty" => "\u6570\u91CF",
            "manual.quality" => "\u54C1\u8CEA",
            "manual.visibleTooltip" => "\u73FE\u5728\u8868\u793A\u4E2D\u306E\u7269\u54C1\u3092\u3059\u3079\u3066\u4EA4\u6613",
            "shared.clearTradePlan" => "\u4EA4\u6613\u30D7\u30E9\u30F3\u3092\u30AF\u30EA\u30A2",
            "gilToItem.inventoryItemValue" => "\u6240\u6301\u30A2\u30A4\u30C6\u30E0\u4FA1\u5024",
            "gilToItem.step1" => "1. gil \u91D1\u984D\u3092\u5165\u529B",
            "gilToItem.step2" => "2. gil to item trade plan \u30DC\u30BF\u30F3\u3092\u9078\u3093\u3067\u62BC\u3057\u307E\u3059",
            "gilToItem.step3" => "3. auto start \u3092\u62BC\u3059\u3068\u3001\u30D7\u30E9\u30F3\u5185\u306E\u7269\u54C1\u3092\u81EA\u52D5\u3067\u4EA4\u6613\u3057\u307E\u3059\u3002",
            "gilToItem.line1" => "- Build item trade plan: \u6240\u6301\u30A2\u30A4\u30C6\u30E0\u306E\u5408\u8A08\u4FA1\u5024\u304C\u8FD1\u3044\u30D7\u30E9\u30F3\u3092\u4F5C\u6210\u3057\u307E\u3059\u3002",
            "gilToItem.line2" => "- Build salvaged item plan: salvaged item \u3060\u3051\u3067\u5408\u8A08\u4FA1\u5024\u304C\u8FD1\u3044\u30D7\u30E9\u30F3\u3092\u4F5C\u6210\u3057\u307E\u3059\u3002",
            "gilToItem.line3" => "- Trade all salvaged item: \u3059\u3079\u3066\u306E salvaged item \u3092\u4EA4\u6613\u3059\u308B\u30D7\u30E9\u30F3\u3092\u4F5C\u6210\u3057\u307E\u3059\u3002",
            "gilToItem.computing" => "\u30D7\u30E9\u30F3\u8A08\u7B97\u4E2D...",
            "gilToItem.note" => "salvaged-only \u306B\u306F Necklace\u3001Earring\u3001Bracelet\u3001Ring \u3068 Extravagant \u7248\u304C\u542B\u307E\u308C\u307E\u3059\u3002",
            _ => GetEnglishText(key),
        };
    }

    public void Dispose()
    {
    }
}

