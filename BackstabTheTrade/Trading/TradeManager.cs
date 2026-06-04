using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BackstabTheTrade;

public sealed class TradeManager : IDisposable
{
    private const int PopupCloseRetryMs = 10;
    private const int MaxTradeHistory = 50;
    private const long GilVerifyTolerance = 1;
    private const int TradeCloseStableMs = 500;
    private const int PostCloseVerifyGraceMs = 2500;
    private const int ClosedWithoutConfirmRetryGraceMs = 750;
    private const int ReceiverTradeConfirmGraceMs = 500;

    public bool IsRunning { get; private set; }
    public int CompletedRunSerial { get; private set; }
    public int TotalTrades { get; private set; }
    public int TradesRemaining { get; private set; }
    public long GilPerTrade { get; private set; }
    public string StatusMessage { get; private set; } = string.Empty;
    public string TargetName { get; private set; } = string.Empty;
    public bool TradeWindowOpen { get; private set; }
    public bool CanAutoSend => _chat.IsAvailable;
    public AutoTradeMode Mode { get; private set; } = AutoTradeMode.Gil;
    public InventoryTradePlan ActiveItemPlan { get; private set; } = InventoryTradePlan.Empty;
    public IReadOnlyList<TradeHistoryEntry> TradeHistory => _tradeHistory;
    public bool TradeHistoryTrackingEnabled { get; private set; } = true;

    private readonly BackstabTheTrade _plugin;
    private readonly ChatSender _chat;
    private readonly List<TradeHistoryEntry> _tradeHistory = new();
    private IReadOnlyList<long> _gilTradePlan = Array.Empty<long>();
    private bool _inputNumericReady;
    private int _tradeSendAttempt;
    private int _visibleInventoryDispatchAttempts;
    private int _visibleInventoryDispatchMatched = -1;
    private int _contextApiOpenMatched = -1;
    private int _contextApiTraceMatched = -1;
    private int _contextApiSkipLoggedMatched = -1;
    private DateTime _contextApiOpenRetryAt = DateTime.MinValue;
    private int _activeInventoryBlockNumber;
    private DateTime _nextActionAt;
    private DateTime _tradeCloseDeadline;
    private DateTime _tradeCloseObservedAt = DateTime.MinValue;
    private DateTime _tradeButtonClickedAt = DateTime.MinValue;
    private bool _tradeConfirmSeenForWindow;
    private bool _tradePostCloseGraceApplied;
    private IReadOnlyList<InventoryTradeBatch> _itemTradeBatches = Array.Empty<InventoryTradeBatch>();
    private DateTime _contextMenuInputDeadline;
    private DateTime _contextMenuTradeReadyDeadline;
    private bool _autoConfirmCurrentInputOnly;
    private bool _plannedContextMenuItemTrade;
    private bool _contextMenuTradeAfterReady;
    private bool _pendingPlannedContextMenuTrigger;
    private long _pendingDirectPlannedQuantity;
    private DateTime _pendingDirectPlannedQuantityDeadline = DateTime.MinValue;
    private DateTime _pendingDirectPlannedQuantityReadyAt = DateTime.MinValue;
    private long _pendingPlannedChunkQuantity;
    private uint _pendingPlannedItemId;
    private string _pendingPlannedItemName = string.Empty;
    private Dictionary<uint, long> _tradeVerifyBefore = new();
    private long _tradeVerifyBeforeGil;
    private long _tradeVerifyObservedIncomingGil;
    private InventoryTradeBatch? _tradeVerifyBatch;
    private DateTime _tradeVerifyDeadline = DateTime.MinValue;
    private DateTime _receiverNextActionAt = DateTime.MinValue;
    private DateTime _receiverOfferFirstSeenAt = DateTime.MinValue;
    private DateTime _receiverOfferLastChangedAt = DateTime.MinValue;
    private DateTime _receiverTradeClickedAt = DateTime.MinValue;
    private bool _receiverTradeClickedForWindow;
    private string _receiverTradeClickOfferSummary = string.Empty;
    private bool _receiverSawTradeWindow;
    private bool _receiverOfferSeenForWindow;
    private bool _receiverConfirmedForWindow;
    private DateTime _receiverConfirmedAt = DateTime.MinValue;
    private bool _receiverHistoryStartedForWindow;
    private string _receiverWindowSummary = string.Empty;
    private string _receiverOfferStableSummary = string.Empty;
    private YesConfirmOwner _yesConfirmOwner = YesConfirmOwner.None;
    private bool _yesConfirmClicked;
    private DateTime _yesConfirmVisibleSince = DateTime.MinValue;
    private DateTime _yesConfirmNextRetryAt = DateTime.MinValue;
    private bool _yesConfirmLoggedWaiting;
    private bool _passiveTradeTrackingActive;
    private DateTime _passiveTradeFinalizeAt = DateTime.MinValue;
    private long _passiveBeforeGil;
    private Dictionary<uint, long> _passiveBeforeItems = new();
    private string _passiveTradeMode = "Manual";

    private enum Step
    {
        Idle,
        SendTarget,
        SendTrade,
        WaitTrade,
        ClickGilBar,
        WaitInputNumeric,
        WaitContextMenuInputNumeric,
        TriggerPlannedContextMenuTrade,
        EnterAndOk,
        WaitPopupClose,
        WaitContextMenuTradeReady,
        WaitItemSlots,
        ClickTrade,
        WaitTradeClose,
        WaitRetryTradeClosed,
        WaitManualTradeReopen,
    }

    private enum YesConfirmOwner
    {
        None,
        Sender,
        Receiver,
    }

    private Step _step = Step.Idle;

    public TradeManager(BackstabTheTrade plugin, ChatSender chat)
    {
        _plugin = plugin;
        _chat = chat;

        BackstabTheTrade.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "InputNumeric", OnInputNumericSetup);
        BackstabTheTrade.AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "InputNumeric", OnInputNumericRequestedUpdate);
        BackstabTheTrade.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "InputNumeric", OnInputNumericFinalize);
        BackstabTheTrade.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesnoSetup);
        BackstabTheTrade.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesNo", OnSelectYesnoSetup);
        BackstabTheTrade.Framework.Update += Tick;
    }

    public void StartAutoSend(string targetName, long totalGil)
    {
        if (IsRunning)
            return;

        targetName = ResolveTradeTargetName(targetName);
        if (string.IsNullOrWhiteSpace(targetName))
        {
            StatusMessage = "Auto Start requires a target player.";
            return;
        }

        var gilPlan = BuildGilTradePlan(totalGil);
        if (gilPlan.Count == 0)
        {
            StatusMessage = "Total gil must be greater than 0.";
            return;
        }

        Mode = AutoTradeMode.Gil;
        ActiveItemPlan = InventoryTradePlan.Empty;
        _itemTradeBatches = Array.Empty<InventoryTradeBatch>();
        _gilTradePlan = gilPlan;
        TargetName = targetName;
        GilPerTrade = gilPlan[0];
        TotalTrades = gilPlan.Count;
        TradesRemaining = gilPlan.Count;
        IsRunning = true;
        _tradeSendAttempt = 0;
        _visibleInventoryDispatchAttempts = 0;
        _visibleInventoryDispatchMatched = -1;
        ResetContextApiState();
        _activeInventoryBlockNumber = 0;
        _step = Step.SendTarget;
        StatusMessage = "Starting...";
        _nextActionAt = DateTime.Now;
    }

    public void StartManual(long totalGil, string targetName = "")
    {
        if (IsRunning)
            return;

        targetName = ResolveTradeTargetName(targetName);
        if (string.IsNullOrWhiteSpace(targetName))
        {
            StatusMessage = "Manual Start requires a target player.";
            return;
        }

        var gilPlan = BuildGilTradePlan(totalGil);
        if (gilPlan.Count == 0)
        {
            StatusMessage = "Total gil must be greater than 0.";
            return;
        }

        Mode = AutoTradeMode.Gil;
        ActiveItemPlan = InventoryTradePlan.Empty;
        _itemTradeBatches = Array.Empty<InventoryTradeBatch>();
        _gilTradePlan = gilPlan;
        TargetName = targetName;
        GilPerTrade = gilPlan[0];
        TotalTrades = gilPlan.Count;
        TradesRemaining = gilPlan.Count;
        IsRunning = true;
        _visibleInventoryDispatchAttempts = 0;
        _visibleInventoryDispatchMatched = -1;
        ResetContextApiState();
        _activeInventoryBlockNumber = 0;
        _step = Step.ClickGilBar;
        StatusMessage = "Taking over Trade window...";
        _nextActionAt = DateTime.Now;
    }

    public void StartAutoSendItems(string targetName, InventoryTradePlan plan, AutoTradeMode mode)
    {
        if (IsRunning)
            return;

        targetName = ResolveTradeTargetName(targetName);
        if (string.IsNullOrWhiteSpace(targetName))
        {
            StatusMessage = "Auto Start requires a target player.";
            return;
        }

        var batches = _plugin.InventoryWealth.SplitIntoTradeBatches(plan);
        if (batches.Count == 0)
            return;

        Mode = mode;
        ActiveItemPlan = plan;
        _itemTradeBatches = batches;
        _gilTradePlan = Array.Empty<long>();
        TargetName = targetName;
        GilPerTrade = 0;
        TotalTrades = batches.Count;
        TradesRemaining = batches.Count;
        IsRunning = true;
        _tradeSendAttempt = 0;
        _visibleInventoryDispatchAttempts = 0;
        _visibleInventoryDispatchMatched = -1;
        ResetContextApiState();
        _activeInventoryBlockNumber = 0;
        _step = Step.SendTarget;
        StatusMessage = "Starting item trade...";
        _nextActionAt = DateTime.Now;
    }

    public void StartManualItems(InventoryTradePlan plan, AutoTradeMode mode, string targetName = "")
    {
        if (IsRunning)
            return;

        targetName = ResolveTradeTargetName(targetName);
        if (string.IsNullOrWhiteSpace(targetName))
        {
            StatusMessage = "Manual Start requires a target player.";
            return;
        }

        var batches = _plugin.InventoryWealth.SplitIntoTradeBatches(plan);
        if (batches.Count == 0)
            return;

        Mode = mode;
        ActiveItemPlan = plan;
        _itemTradeBatches = batches;
        _gilTradePlan = Array.Empty<long>();
        TargetName = targetName;
        GilPerTrade = 0;
        TotalTrades = batches.Count;
        TradesRemaining = batches.Count;
        IsRunning = true;
        _visibleInventoryDispatchAttempts = 0;
        _visibleInventoryDispatchMatched = -1;
        ResetContextApiState();
        _activeInventoryBlockNumber = 0;
        _step = Step.WaitItemSlots;
        StatusMessage = "Taking over Trade window for item trade...";
        _nextActionAt = DateTime.Now;
        TrackVerbose(
            $"[ManualFullAuto] StartManualItems mode={mode}, batches={batches.Count}, first={DescribeFirstBatchChunkForLog(batches)}");
    }

    public void Stop()
    {
        if (IsRunning)
            AddTradeHistory("Stopped", BuildCurrentTradeSummary(), "User pressed Stop");

        _autoConfirmCurrentInputOnly = false;
        _plannedContextMenuItemTrade = false;
        _contextMenuTradeAfterReady = false;
        _pendingPlannedContextMenuTrigger = false;
        ResetPendingDirectPlannedQuantity();
        _pendingPlannedChunkQuantity = 0;
        _pendingPlannedItemId = 0;
        _pendingPlannedItemName = string.Empty;
        ResetTradeVerification();
        IsRunning = false;
        _step = Step.Idle;
        ActiveItemPlan = InventoryTradePlan.Empty;
        _itemTradeBatches = Array.Empty<InventoryTradeBatch>();
        _gilTradePlan = Array.Empty<long>();
        _visibleInventoryDispatchAttempts = 0;
        _visibleInventoryDispatchMatched = -1;
        ResetContextApiState();
        _activeInventoryBlockNumber = 0;
        StatusMessage = "Stopped.";
        CompletedRunSerial++;
    }

    public void ClearTradeHistory()
    {
        _tradeHistory.Clear();
    }

    public void SetTradeHistoryTracking(bool enabled)
    {
        TradeHistoryTrackingEnabled = enabled;
        if (!enabled)
        {
            _passiveTradeTrackingActive = false;
            _passiveTradeFinalizeAt = DateTime.MinValue;
            _passiveBeforeGil = 0;
            _passiveBeforeItems.Clear();
        }
    }

    public bool IsWaitingForPlannedInventoryContext(
        uint itemId,
        InventoryType containerType,
        int slotIndex,
        out string reason)
    {
        reason = string.Empty;

        if (!IsRunning || Mode == AutoTradeMode.Gil || _step != Step.WaitItemSlots)
        {
            reason = "manual item plan is not waiting for an inventory item.";
            return false;
        }

        var batch = GetCurrentBatch();
        if (batch == null)
        {
            reason = "no active manual item batch.";
            return false;
        }

        int matched = GetMatchedChunkCount(batch.Value);
        if (matched >= batch.Value.Chunks.Count)
        {
            reason = "current batch is already ready.";
            return false;
        }

        var nextChunk = batch.Value.Chunks[matched];
        if (nextChunk.ItemId != itemId)
        {
            reason = $"expected {nextChunk.Name}, got item {itemId}.";
            return false;
        }

        reason = $"matched {nextChunk.Name} x{nextChunk.Quantity:N0} at {containerType} slot {slotIndex}.";
        if (nextChunk.StackSources.Count > 0)
        {
            var source = nextChunk.StackSources[0];
            if (source.ContainerType != containerType || source.SlotIndex != slotIndex)
                reason += $" Source differs from plan ({source.ContainerType} slot {source.SlotIndex}), continuing by ItemId.";
        }

        return true;
    }

    public string TryRunContextMenuTradeAutoConfirm(bool clickTradeAfterReady)
    {
        if (IsRunning)
            return "AutoTrade busy.";

        Mode = AutoTradeMode.ItemManual;
        ActiveItemPlan = InventoryTradePlan.Empty;
        _itemTradeBatches = Array.Empty<InventoryTradeBatch>();
        _gilTradePlan = Array.Empty<long>();
        GilPerTrade = 0;
        TargetName = string.Empty;
        TotalTrades = 1;
        TradesRemaining = 1;
        IsRunning = true;
        _inputNumericReady = false;
        _tradeSendAttempt = 0;
        _visibleInventoryDispatchAttempts = 0;
        _activeInventoryBlockNumber = 0;
        _autoConfirmCurrentInputOnly = true;
        _contextMenuTradeAfterReady = clickTradeAfterReady;
        _step = Step.WaitContextMenuInputNumeric;
        _nextActionAt = DateTime.Now;
        _contextMenuInputDeadline = DateTime.Now.AddSeconds(5);
        StatusMessage = clickTradeAfterReady
            ? "Triggering Trade - waiting for Specify quantity, then final Trade..."
            : "Triggering Trade - waiting for Specify quantity...";

        var result = _plugin.EventTracker.TryTriggerContextMenuTrade();
        if (!result.Contains("FireCallback sent", StringComparison.Ordinal))
            ResetContextMenuAutoConfirm("Failed to trigger Trade.");

        return result;
    }

    public string TryRunPlannedContextMenuTrade(uint itemId, string itemName, bool clickTradeAfterReady)
    {
        return TryPreparePlannedContextMenuTrade(itemId, itemName, clickTradeAfterReady, TimeSpan.Zero);
    }

    public string SchedulePlannedContextMenuTrade(uint itemId, string itemName, bool clickTradeAfterReady, int? delayMs = null)
    {
        return TryPreparePlannedContextMenuTrade(
            itemId,
            itemName,
            clickTradeAfterReady,
            TimeSpan.FromMilliseconds(Math.Max(0, delayMs ?? _plugin.Configuration.PlannedContextMenuTriggerDelayMs)));
    }

    private string TryPreparePlannedContextMenuTrade(uint itemId, string itemName, bool clickTradeAfterReady, TimeSpan triggerDelay)
    {
        TradeWindowOpen = IsAddonVisible("Trade");

        if (!IsRunning || Mode == AutoTradeMode.Gil || ActiveItemPlan.Entries.Count == 0)
            return "Manual item plan not active.";

        if (!TradeWindowOpen)
            return "Trade window not open.";

        var batch = GetCurrentBatch();
        if (batch == null)
            return "No remaining planned item batches.";

        int matched = GetMatchedChunkCount(batch.Value);
        if (matched >= batch.Value.Chunks.Count)
            return "Current batch already ready.";

        var nextChunk = batch.Value.Chunks[matched];
        if (nextChunk.ItemId != itemId)
            return $"Expected {nextChunk.Name} x{nextChunk.Quantity:N0}, got {itemName}.";

        itemName = nextChunk.Name;
        bool shouldFinalizeAfterThisItem = clickTradeAfterReady && matched + 1 >= batch.Value.Chunks.Count;

        _inputNumericReady = false;
        _plannedContextMenuItemTrade = true;
        _autoConfirmCurrentInputOnly = false;
        _contextMenuTradeAfterReady = shouldFinalizeAfterThisItem;
        _pendingPlannedChunkQuantity = nextChunk.Quantity;
        _step = Step.WaitContextMenuInputNumeric;
        _nextActionAt = DateTime.Now;
        _contextMenuInputDeadline = DateTime.Now.AddSeconds(5);
        StatusMessage = shouldFinalizeAfterThisItem
            ? $"Triggering planned trade chunk {matched + 1}/{batch.Value.Chunks.Count} for {nextChunk.Name} x{nextChunk.Quantity:N0}, then final Trade..."
            : $"Triggering planned trade chunk {matched + 1}/{batch.Value.Chunks.Count} for {nextChunk.Name} x{nextChunk.Quantity:N0}...";

        if (triggerDelay > TimeSpan.Zero)
        {
            _pendingPlannedContextMenuTrigger = true;
            _pendingPlannedItemId = itemId;
            _pendingPlannedItemName = itemName;
            _nextActionAt = DateTime.Now.Add(triggerDelay);
            _step = Step.TriggerPlannedContextMenuTrade;
            return shouldFinalizeAfterThisItem
                ? $"Scheduled planned context-menu trade chunk {matched + 1}/{batch.Value.Chunks.Count} in {triggerDelay.TotalMilliseconds:0}ms; final Trade after this item."
                : $"Scheduled planned context-menu trade chunk {matched + 1}/{batch.Value.Chunks.Count} in {triggerDelay.TotalMilliseconds:0}ms; waiting for more items after this.";
        }

        var result = _plugin.EventTracker.TryTriggerContextMenuTrade();
        if (!result.Contains("FireCallback sent", StringComparison.Ordinal))
            AbortPlannedContextMenuTrade("Failed to trigger planned item trade.");

        return result;
    }

    private void Tick(IFramework fw)
    {
        var tradeWindowVisible = IsAddonVisible("Trade");
        TickPassiveTradeHistory(tradeWindowVisible);
        TradeWindowOpen = tradeWindowVisible;
        if (!IsRunning)
        {
            TickReceiverMode();
            return;
        }

        if (TryRunPendingDirectPlannedQuantity())
            return;

        if (ShouldStopBecauseTargetChanged())
        {
            StopBecauseTargetChanged();
            return;
        }

        if (DateTime.Now < _nextActionAt)
            return;

        switch (_step)
        {
            case Step.TriggerPlannedContextMenuTrade:
            {
                if (!_pendingPlannedContextMenuTrigger)
                {
                    _step = Step.WaitItemSlots;
                    _nextActionAt = DateTime.Now;
                    break;
                }

                _pendingPlannedContextMenuTrigger = false;
                var result = _plugin.EventTracker.TryTriggerContextMenuTrade();
                TrackVerbose($"[ManualFullAuto] delayed context-menu trigger for {_pendingPlannedItemName} ({_pendingPlannedItemId}): {result}");
                if (!result.Contains("FireCallback sent", StringComparison.Ordinal))
                    AbortPlannedContextMenuTrade("Failed to trigger planned item trade.");
                else
                    _step = Step.WaitContextMenuInputNumeric;
                break;
            }

            case Step.SendTrade:
            {
                int round = TotalTrades - TradesRemaining + 1;
                string tradeCommand = GetTradeCommandVariant();
                StatusMessage = $"[{round}/{TotalTrades}] Sending {tradeCommand}";
                if (!_chat.IsAvailable)
                {
                    StatusMessage = "ChatSender not available.";
                    IsRunning = false;
                    _step = Step.Idle;
                    break;
                }

                if (_chat.Send(tradeCommand))
                {
                    _step = Step.WaitTrade;
                    _nextActionAt = DateTime.Now.AddMilliseconds(10);
                }
                else
                {
                    StatusMessage = "Failed to send /trade command.";
                    IsRunning = false;
                    _step = Step.Idle;
                }
                break;
            }

            case Step.SendTarget:
            {
                string targetCommand = $"/target {QuoteTradeTarget(TargetName.Trim())}";
                StatusMessage = $"Targeting {TargetName}...";
                if (!_chat.IsAvailable)
                {
                    StatusMessage = "ChatSender not available.";
                    IsRunning = false;
                    _step = Step.Idle;
                    break;
                }

                if (_chat.Send(targetCommand))
                    Delay(_plugin.Configuration.TargetToTradeDelayMs, Step.SendTrade);
                else
                {
                    StatusMessage = "Failed to send /target command.";
                    IsRunning = false;
                    _step = Step.Idle;
                }
                break;
            }

            case Step.WaitTrade:
                if (TradeWindowOpen)
                {
                    _tradeSendAttempt = 0;
                    StatusMessage = "Trade window open.";
                    Delay(_plugin.Configuration.TradeOpenDelayMs, Mode == AutoTradeMode.Gil ? Step.ClickGilBar : Step.WaitItemSlots);
                }
                else
                {
                    _tradeSendAttempt++;
                    StatusMessage = "Timeout - retrying /trade...";
                    Delay(_plugin.Configuration.TradeRetryDelayMs, Step.SendTrade);
                }
                break;

            case Step.ClickGilBar:
                if (!TradeWindowOpen)
                {
                    StatusMessage = "Trade closed.";
                    IsRunning = false;
                    _step = Step.Idle;
                    break;
                }

                if (DoFireCallback("Trade", 2, 2, -1))
                {
                    _inputNumericReady = false;
                    _step = Step.WaitInputNumeric;
                    _nextActionAt = DateTime.Now.AddSeconds(5);
                    StatusMessage = "Gil bar clicked - waiting for InputNumeric popup...";
                }
                else
                {
                    Delay(_plugin.Configuration.GilBarRetryDelayMs, Step.ClickGilBar);
                }
                break;

            case Step.WaitInputNumeric:
                if (_inputNumericReady || IsTradeAmountInputOpen())
                {
                    _inputNumericReady = false;
                    StatusMessage = "InputNumeric open.";
                    Delay(_plugin.Configuration.OkButtonDelayMs, Step.EnterAndOk);
                }
                else
                {
                    StatusMessage = "Popup timeout - retrying...";
                    Delay(250, Step.ClickGilBar);
                }
                break;

            case Step.WaitContextMenuInputNumeric:
                if (_inputNumericReady || IsItemQuantityInputOpen())
                {
                    _inputNumericReady = false;
                    StatusMessage = "Specify quantity open.";
                    Delay(_plugin.Configuration.OkButtonDelayMs, Step.EnterAndOk);
                }
                else if (_plannedContextMenuItemTrade && HasAnyTradeItemSelected())
                {
                    StatusMessage = "Trade slot ready without quantity popup.";
                    _step = Step.WaitContextMenuTradeReady;
                    _nextActionAt = DateTime.Now;
                }
                else if (DateTime.Now > _contextMenuInputDeadline)
                {
                    if (_plannedContextMenuItemTrade)
                        AbortPlannedContextMenuTrade("Specify quantity popup timeout.");
                    else
                        ResetContextMenuAutoConfirm("Specify quantity popup timeout.");
                }
                else
                {
                    _nextActionAt = DateTime.Now.AddMilliseconds(_plugin.Configuration.PlannedTradeSlotPollMs);
                }
                break;

            case Step.EnterAndOk:
                if (_autoConfirmCurrentInputOnly)
                {
                    var currentInputValue = GetVisibleInputNumericValue();
                    if (currentInputValue <= 0)
                    {
                        Delay(_plugin.Configuration.InputNumericRetryDelayMs, Step.EnterAndOk);
                        break;
                    }

                    if (DoSetValueAndOk(currentInputValue))
                    {
                        StatusMessage = $"Confirmed item quantity {currentInputValue:N0} - waiting for popup close...";
                        _step = Step.WaitPopupClose;
                        _nextActionAt = DateTime.Now.AddMilliseconds(_plugin.Configuration.PlannedPopupCloseWaitMs);
                    }
                    else
                    {
                        Delay(_plugin.Configuration.InputNumericRetryDelayMs, Step.EnterAndOk);
                    }
                    break;
                }

                if (_plannedContextMenuItemTrade)
                {
                    if (!IsAddonVisible("InputNumeric"))
                    {
                        TrackVerbose("[ManualFullAuto] Planned quantity step waiting: InputNumeric not visible.");
                        Delay(_plugin.Configuration.InputNumericRetryDelayMs, Step.EnterAndOk);
                        break;
                    }

                    var plannedChunkQuantity = GetPendingPlannedChunkQuantity();
                    if (plannedChunkQuantity <= 0)
                    {
                        AbortPlannedContextMenuTrade("No pending chunk quantity for planned item trade.");
                        break;
                    }

                    TrackVerbose($"[ManualFullAuto] Setting planned item quantity {plannedChunkQuantity:N0}.");
                    if (DoSetValueAndOk(plannedChunkQuantity))
                    {
                        StatusMessage = $"Set planned item quantity {plannedChunkQuantity:N0} - waiting for popup close...";
                        _step = Step.WaitPopupClose;
                        _nextActionAt = DateTime.Now.AddMilliseconds(_plugin.Configuration.PlannedPopupCloseWaitMs);
                    }
                    else
                    {
                        Delay(_plugin.Configuration.InputNumericRetryDelayMs, Step.EnterAndOk);
                    }
                    break;
                }

                if (Mode == AutoTradeMode.Gil)
                {
                    if (!IsTradeAmountInputOpen())
                    {
                        Delay(_plugin.Configuration.InputNumericRetryDelayMs, Step.EnterAndOk);
                        break;
                    }

                    var currentGilTradeAmount = GetCurrentGilTradeAmount();
                    if (currentGilTradeAmount <= 0)
                    {
                        StatusMessage = "No gil trade amount planned for this step.";
                        IsRunning = false;
                        _step = Step.Idle;
                        break;
                    }

                    if (DoSetValueAndOk(currentGilTradeAmount))
                    {
                        StatusMessage = $"Set {currentGilTradeAmount:N0} gil - waiting for popup close...";
                        _step = Step.WaitPopupClose;
                        _nextActionAt = DateTime.Now.AddMilliseconds(_plugin.Configuration.PlannedPopupCloseWaitMs);
                    }
                    else
                    {
                        Delay(_plugin.Configuration.InputNumericRetryDelayMs, Step.EnterAndOk);
                    }
                    break;
                }

                if (!IsItemQuantityInputOpen())
                {
                    Delay(_plugin.Configuration.InputNumericRetryDelayMs, Step.EnterAndOk);
                    break;
                }

                var pendingChunkQuantity = GetPendingBatchChunkQuantity();
                if (pendingChunkQuantity <= 0)
                {
                    Delay(_plugin.Configuration.InputNumericRetryDelayMs, Step.WaitItemSlots);
                    break;
                }

                if (DoSetValueAndOk(pendingChunkQuantity))
                {
                    StatusMessage = $"Set item quantity {pendingChunkQuantity:N0} - waiting for popup close...";
                    _step = Step.WaitPopupClose;
                    _nextActionAt = DateTime.Now.AddMilliseconds(100);
                }
                else
                {
                    Delay(_plugin.Configuration.InputNumericRetryDelayMs, Step.EnterAndOk);
                }
                break;

            case Step.WaitPopupClose:
                if (!IsTradeAmountInputOpen() && !IsItemQuantityInputOpen())
                {
                    if (_autoConfirmCurrentInputOnly || _plannedContextMenuItemTrade)
                    {
                        StatusMessage = "Specify quantity confirmed - waiting for trade slot...";
                        _contextMenuTradeReadyDeadline = DateTime.Now.AddSeconds(3);
                        Delay(Math.Min(Math.Max(1, _plugin.Configuration.ConfirmDelayMs), _plugin.Configuration.PlannedPopupCloseWaitMs), Step.WaitContextMenuTradeReady);
                        break;
                    }

                    Delay(
                        Mode == AutoTradeMode.Gil ? _plugin.Configuration.ConfirmDelayMs : 0,
                        Mode == AutoTradeMode.Gil ? Step.ClickTrade : Step.WaitItemSlots);
                }
                else if (DateTime.Now > _nextActionAt)
                {
                    Delay(PopupCloseRetryMs, Step.EnterAndOk);
                }
                break;

            case Step.WaitContextMenuTradeReady:
                if (!TradeWindowOpen)
                {
                    if (_plannedContextMenuItemTrade)
                        AbortPlannedContextMenuTrade("Trade closed before item slot became ready.");
                    else
                        ResetContextMenuAutoConfirm("Trade closed before item slot became ready.");
                    break;
                }

                if (HasAnyTradeItemSelected())
                {
                    if (_contextMenuTradeAfterReady)
                    {
                        _pendingPlannedChunkQuantity = 0;
                        StatusMessage = "Trade slot ready - clicking Trade...";
                        TrackVerbose("[AutoTrade2] Trade slot ready - scheduling final Trade click.");
                        Delay(Math.Max(1, _plugin.Configuration.ConfirmDelayMs), Step.ClickTrade);
                    }
                    else if (_plannedContextMenuItemTrade)
                    {
                        _plannedContextMenuItemTrade = false;
                        _contextMenuTradeAfterReady = false;
                        _inputNumericReady = false;
                        _pendingPlannedChunkQuantity = 0;
                        StatusMessage = "Trade slot ready - waiting for next item...";
                        _step = Step.WaitItemSlots;
                        _nextActionAt = DateTime.Now.AddMilliseconds(Math.Max(_plugin.Configuration.PlannedNextItemWaitMs, _plugin.Configuration.InputNumericRetryDelayMs));
                    }
                    else
                    {
                        FinishContextMenuFillOnly("Item added to trade slot.");
                    }
                }
                else if (DateTime.Now > _contextMenuTradeReadyDeadline)
                {
                    TrackVerbose("[AutoTrade] Trade slot not populated in time.");
                    if (_plannedContextMenuItemTrade)
                        AbortPlannedContextMenuTrade("Trade slot not populated in time.");
                    else
                        ResetContextMenuAutoConfirm("Trade slot not populated in time.");
                }
                else
                {
                    _nextActionAt = DateTime.Now.AddMilliseconds(_plugin.Configuration.PlannedTradeSlotPollMs);
                }
                break;

            case Step.WaitItemSlots:
            {
                if (!TradeWindowOpen)
                {
                    StatusMessage = "Trade closed.";
                    IsRunning = false;
                    _step = Step.Idle;
                    break;
                }

                if (IsItemQuantityInputOpen())
                {
                    StatusMessage = "Specify quantity open.";
                    Delay(_plugin.Configuration.OkButtonDelayMs, Step.EnterAndOk);
                    break;
                }

                var batch = GetCurrentBatch();
                if (batch == null)
                {
                    StatusMessage = "No remaining item trade batches.";
                    IsRunning = false;
                    _step = Step.Idle;
                    break;
                }

                int matched = GetMatchedChunkCount(batch.Value);
                if (matched >= batch.Value.Chunks.Count)
                {
                    _visibleInventoryDispatchAttempts = 0;
                    _visibleInventoryDispatchMatched = -1;
                    ResetContextApiState();
                    StatusMessage = $"Batch {batch.Value.BatchNumber}/{_itemTradeBatches.Count} ready - clicking Trade...";
                    Delay(_plugin.Configuration.ConfirmDelayMs, Step.ClickTrade);
                }
                else
                {
                    var nextChunk = batch.Value.Chunks[matched];
                    if (Mode != AutoTradeMode.Gil && _contextApiTraceMatched != matched)
                    {
                        _contextApiTraceMatched = matched;
                        TrackVerbose(
                            $"[ManualFullAuto] WaitItemSlots batch={batch.Value.BatchNumber}/{_itemTradeBatches.Count}, chunk={matched + 1}/{batch.Value.Chunks.Count}, next={DescribeChunkForLog(nextChunk)}");
                    }

                    if (Mode != AutoTradeMode.Gil &&
                        TryOpenInventoryContextForChunk(nextChunk, matched, out var contextStatus))
                    {
                        StatusMessage = contextStatus;
                        break;
                    }

                    if (Mode != AutoTradeMode.ItemManual && TryFillVisibleInventoryNode(nextChunk, matched, out var fillStatus))
                    {
                        StatusMessage = fillStatus;
                        _nextActionAt = DateTime.Now.AddMilliseconds(Math.Max(60, _plugin.Configuration.InputNumericRetryDelayMs));
                        break;
                    }

                    StatusMessage = Mode != AutoTradeMode.Gil
                        ? $"Batch {batch.Value.BatchNumber}/{_itemTradeBatches.Count}: waiting for item slots ({matched}/{batch.Value.Chunks.Count}). Next: right-click {nextChunk.Name} x{nextChunk.Quantity:N0} in inventory if context API did not open it."
                        : $"Batch {batch.Value.BatchNumber}/{_itemTradeBatches.Count}: waiting for item slots ({matched}/{batch.Value.Chunks.Count}). Next: {nextChunk.Name} x{nextChunk.Quantity:N0}. {DescribeInventorySourceHint(nextChunk)}";
                    _nextActionAt = DateTime.Now.AddMilliseconds(Math.Max(_plugin.Configuration.PlannedNextItemWaitMs, _plugin.Configuration.InputNumericRetryDelayMs));
                }
                break;
            }

            case Step.ClickTrade:
                if (!TradeWindowOpen)
                {
                    StatusMessage = "Trade closed before button.";
                    IsRunning = false;
                    _step = Step.Idle;
                    break;
                }

                if (DoFireCallback("Trade", 2, 0, -1))
                {
                    CaptureTradeVerificationBaseline();
                    TrackVerbose("[AutoTrade] Final Trade callback fired.");
                    _tradeButtonClickedAt = DateTime.Now;
                    _tradeCloseObservedAt = DateTime.MinValue;
                    _tradeConfirmSeenForWindow = false;
                    _tradePostCloseGraceApplied = false;
                    ArmYesConfirm(YesConfirmOwner.Sender);
                    StatusMessage = "Trade button clicked - waiting for partner...";
                    _step = Step.WaitTradeClose;
                    _tradeCloseDeadline = DateTime.Now.AddSeconds(60);
                    _nextActionAt = DateTime.Now;
                }
                else
                {
                    Delay(_plugin.Configuration.TradeButtonRetryMs, Step.ClickTrade);
                }
                break;

            case Step.WaitTradeClose:
                if (TryTickYesConfirm(YesConfirmOwner.Sender))
                {
                    _tradeConfirmSeenForWindow = true;
                    StatusMessage = "Confirming trade...";
                    Delay(Math.Max(50, _plugin.Configuration.YesConfirmRetryDelayMs), Step.WaitTradeClose);
                    break;
                }

                if (TradeWindowOpen &&
                    !_tradeConfirmSeenForWindow &&
                    !IsTradeConfirmWindowVisible() &&
                    HasIncomingTradeOffer() &&
                    DateTime.Now >= _tradeButtonClickedAt.AddMilliseconds(Math.Max(250, _plugin.Configuration.ReceiverTradeRetryDelayMs)))
                {
                    if (DoFireCallback("Trade", 2, 0, -1))
                    {
                        _tradeButtonClickedAt = DateTime.Now;
                        StatusMessage = "Partner offer detected - clicking Trade again...";
                        TrackVerbose($"[AutoTrade] Partner offer detected during {Mode} trade; Trade callback fired again.");
                        _nextActionAt = DateTime.Now.AddMilliseconds(Math.Max(250, _plugin.Configuration.ReceiverTradeRetryDelayMs));
                        break;
                    }
                }

                if (TradeWindowOpen && Mode == AutoTradeMode.Gil)
                    _tradeVerifyObservedIncomingGil = Math.Max(_tradeVerifyObservedIncomingGil, GetIncomingTradeGilAmount());

                if (!TradeWindowOpen)
                {
                    ResetYesConfirm(YesConfirmOwner.Sender);

                    if (_tradeCloseObservedAt == DateTime.MinValue)
                    {
                        _tradeCloseObservedAt = DateTime.Now;
                        StatusMessage = "Trade window disappeared - waiting for stable close...";
                        _nextActionAt = DateTime.Now.AddMilliseconds(TradeCloseStableMs);
                        break;
                    }

                    if ((DateTime.Now - _tradeCloseObservedAt).TotalMilliseconds < TradeCloseStableMs)
                    {
                        StatusMessage = "Trade window disappeared - waiting for stable close...";
                        _nextActionAt = DateTime.Now.AddMilliseconds(Math.Max(50, TradeCloseStableMs / 2));
                        break;
                    }

                    if (!_tradePostCloseGraceApplied)
                    {
                        int verifyGraceMs = _tradeConfirmSeenForWindow
                            ? PostCloseVerifyGraceMs
                            : ClosedWithoutConfirmRetryGraceMs;
                        var desiredDeadline = DateTime.Now.AddMilliseconds(verifyGraceMs);
                        if (_tradeVerifyDeadline < desiredDeadline)
                            _tradeVerifyDeadline = desiredDeadline;
                        _tradePostCloseGraceApplied = true;
                    }

                    bool allowVerifyFailureLogging = DateTime.Now >= _tradeVerifyDeadline;
                    if (!VerifyCompletedTrade(allowVerifyFailureLogging))
                    {
                        if (DateTime.Now < _tradeVerifyDeadline)
                        {
                            StatusMessage = _tradeConfirmSeenForWindow
                                ? "Trade closed - waiting for success confirmation..."
                                : "Trade closed before confirm - waiting briefly before retry...";
                            _nextActionAt = DateTime.Now.AddMilliseconds(_plugin.Configuration.PlannedPopupCloseWaitMs);
                            break;
                        }

                        ResetContextApiState();
                        _visibleInventoryDispatchAttempts = 0;
                        _visibleInventoryDispatchMatched = -1;
                        ResetTradeVerification();
                        if (_tradeConfirmSeenForWindow)
                        {
                            StatusMessage = "Trade verification failed - waiting for trade window to close before retry.";
                            TrackImportant("[AutoTrade] Trade verification failed; waiting for stable close before retry.");
                        }
                        else
                        {
                            StatusMessage = "Trade closed before confirmation - retrying target and /trade...";
                            TrackImportant("[AutoTrade] Trade closed before confirmation; retrying target and /trade.");
                        }
                        Delay(Math.Max(250, _plugin.Configuration.ActionDelayMs), Step.WaitRetryTradeClosed);
                        break;
                    }

                    ResetTradeVerification();
                    TradesRemaining--;
                    if (TradesRemaining <= 0)
                    {
                        _autoConfirmCurrentInputOnly = false;
                        _plannedContextMenuItemTrade = false;
                        IsRunning = false;
                        _step = Step.Idle;
                        StatusMessage = $"Done! All {TotalTrades} trades complete.";
                        CompletedRunSerial++;
                    }
                    else
                    {
                        _visibleInventoryDispatchAttempts = 0;
                        ResetContextApiState();
                        if (Mode != AutoTradeMode.Gil && string.IsNullOrWhiteSpace(TargetName))
                        {
                            StatusMessage = $"{TotalTrades - TradesRemaining}/{TotalTrades} done - waiting for next Trade window...";
                            TrackVerbose("[ManualFullAuto] Waiting for next Trade window because no target name is configured.");
                            Delay(_plugin.Configuration.ActionDelayMs, Step.WaitManualTradeReopen);
                        }
                        else
                        {
                            StatusMessage = $"{TotalTrades - TradesRemaining}/{TotalTrades} done - sending next /trade...";
                            Delay(_plugin.Configuration.ActionDelayMs, GetNextAutoTradeStartStep());
                        }
                    }
                }
                else
                {
                    _tradeCloseObservedAt = DateTime.MinValue;
                    if (DateTime.Now > _tradeCloseDeadline)
                    {
                        _autoConfirmCurrentInputOnly = false;
                        _plannedContextMenuItemTrade = false;
                        StatusMessage = "Partner timeout (60s) - stopped.";
                        AddTradeHistory("Timeout", BuildCurrentTradeSummary(), "Trade window stayed open for 60 seconds");
                        IsRunning = false;
                        _step = Step.Idle;
                        CompletedRunSerial++;
                    }
                }
                break;

            case Step.WaitRetryTradeClosed:
                if (TradeWindowOpen)
                {
                    StatusMessage = "Waiting for failed trade window to close before retry...";
                    _nextActionAt = DateTime.Now.AddMilliseconds(250);
                    break;
                }

                ResetContextApiState();
                _visibleInventoryDispatchAttempts = 0;
                _visibleInventoryDispatchMatched = -1;
                _inputNumericReady = false;
                _plannedContextMenuItemTrade = false;
                _contextMenuTradeAfterReady = false;
                ResetPendingDirectPlannedQuantity();
                _pendingPlannedChunkQuantity = 0;
                AddTradeHistory("Retrying", BuildCurrentTradeSummary(), "Previous attempt did not verify; retrying same trade");

                StatusMessage = "Retrying same trade after verification failure...";
                TrackImportant("[AutoTrade] Retrying same trade after verification failure.");
                if (Mode == AutoTradeMode.Gil)
                {
                    if (string.IsNullOrWhiteSpace(TargetName))
                        Delay(_plugin.Configuration.ActionDelayMs, Step.WaitManualTradeReopen);
                    else
                        Delay(_plugin.Configuration.ActionDelayMs, Step.SendTarget);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(TargetName))
                        Delay(_plugin.Configuration.ActionDelayMs, Step.WaitManualTradeReopen);
                    else
                        Delay(_plugin.Configuration.ActionDelayMs, Step.SendTarget);
                }
                break;

            case Step.WaitManualTradeReopen:
                if (TradeWindowOpen)
                {
                    TrackVerbose("[ManualFullAuto] Next Trade window detected; continuing remaining batch.");
                    StatusMessage = "Next Trade window open - continuing item batch...";
                    Delay(_plugin.Configuration.TradeOpenDelayMs, Step.WaitItemSlots);
                }
                else
                {
                    StatusMessage = $"{TotalTrades - TradesRemaining}/{TotalTrades} done - open the next Trade window to continue.";
                    _nextActionAt = DateTime.Now.AddMilliseconds(250);
                }
                break;
        }
    }

    private static unsafe bool DoFireCallback(string addonName, int callbackIdx, int val0, int val1)
    {
        try
        {
            var ptr = BackstabTheTrade.GameGui.GetAddonByName(addonName);
            if (ptr.Address == nint.Zero) return false;
            var addon = (AtkUnitBase*)ptr.Address;
            if (!addon->IsVisible) return false;

            if (val1 >= 0)
            {
                var v = stackalloc AtkValue[2];
                v[0].SetInt(val0);
                v[1].SetInt(val1);
                addon->FireCallback((uint)callbackIdx, v, false);
            }
            else
            {
                var v = stackalloc AtkValue[1];
                v[0].SetInt(val0);
                addon->FireCallback((uint)callbackIdx, v, false);
            }
            return true;
        }
        catch (Exception ex)
        {
            BackstabTheTrade.Log.Error(ex, $"DoFireCallback({addonName})");
            return false;
        }
    }

    private unsafe bool DoSetValueAndOk(long value)
    {
        try
        {
            var ptr = BackstabTheTrade.GameGui.GetAddonByName("InputNumeric");
            if (ptr.Address == nint.Zero) return false;
            var addon = (AtkUnitBase*)ptr.Address;
            if (!addon->IsVisible) return false;

            bool isItemQuantity = IsItemQuantityInputOpen();
            var node = addon->GetNodeById(3);
            if (node != null)
            {
                var num = node->GetAsAtkComponentNumericInput();
                if (num != null)
                    num->SetValue((int)value);
            }

            var verifiedValue = GetVisibleInputNumericValue();
            if (verifiedValue != (int)value)
            {
                TrackVerbose(
                    $"[ManualFullAuto] Numeric value not ready: target={value:N0}, visible={verifiedValue:N0}; retrying before OK.");
                return false;
            }

            if (!isItemQuantity)
            {
                var state = addon->AtkValues;
                if (state != null && addon->AtkValuesCount > 3)
                    state[3].SetInt((int)value);
                if (state != null && addon->AtkValuesCount > 4)
                    state[4].SetInt((int)value);
            }

            var okButton = addon->GetComponentButtonById(4);
            if (okButton == null)
            {
                BackstabTheTrade.Log.Warning("InputNumeric: N4 OK button not found.");
                return false;
            }

            var okNode = (AtkResNode*)okButton->AtkComponentBase.OwnerNode;
            if (okNode == null)
            {
                BackstabTheTrade.Log.Warning("InputNumeric: N4 owner node missing.");
                return false;
            }

            bool hasReplay = _plugin.EventTracker.TryGetOkReplaySequence(out var pressReplay, out var releaseReplay, out var clickReplay);
            if (!hasReplay)
            {
                pressReplay = CreateSyntheticReplay(23, 0);
                releaseReplay = CreateSyntheticReplay(24, 1);
                clickReplay = CreateSyntheticReplay(25, 2);
                BackstabTheTrade.Log.Debug("InputNumeric: no captured N4 replay context yet, using synthetic button sequence.");
            }

            const string modeLabel = "component-node DispatchEvent + replay-state + replay-data";
            bool handled = TryComponentNodeDispatch(
                addon,
                (AtkComponentBase*)okButton,
                (AtkComponentNode*)okNode,
                pressReplay,
                releaseReplay,
                clickReplay,
                useReplayData: true);

            if (!handled)
                return false;

            BackstabTheTrade.Log.Information($"InputNumeric: fixed N4 replay path -> {modeLabel} for {value:N0} (itemQuantity={isItemQuantity}).");
            return true;
        }
        catch (Exception ex)
        {
            BackstabTheTrade.Log.Error(ex, "DoSetValueAndOk");
            return false;
        }
    }

    private static unsafe bool IsAddonVisible(string name)
    {
        var ptr = BackstabTheTrade.GameGui.GetAddonByName(name);
        if (ptr.Address == nint.Zero) return false;
        return ((AtkUnitBase*)ptr.Address)->IsVisible;
    }

    private static unsafe bool IsTradeAmountInputOpen()
    {
        var ptr = BackstabTheTrade.GameGui.GetAddonByName("InputNumeric");
        if (ptr.Address == nint.Zero)
            return false;

        var addon = (AtkUnitBase*)ptr.Address;
        if (!addon->IsVisible)
            return false;

        var state = addon->AtkValues;
        if (state != null && addon->AtkValuesCount > 2)
        {
            // InputNumeric mode flag observed from tracker:
            // 0 = trade gil amount, 1 = item quantity.
            if (state[2].Int == 0)
                return true;
            if (state[2].Int == 1)
                return false;
        }

        var textNode = addon->GetTextNodeById(2);
        if (textNode == null)
            return false;

        var prompt = textNode->NodeText.ToString();
        return string.Equals(prompt, "Select amount.", StringComparison.Ordinal);
    }

    private static unsafe bool IsItemQuantityInputOpen()
    {
        var ptr = BackstabTheTrade.GameGui.GetAddonByName("InputNumeric");
        if (ptr.Address == nint.Zero)
            return false;

        var addon = (AtkUnitBase*)ptr.Address;
        if (!addon->IsVisible)
            return false;

        var state = addon->AtkValues;
        if (state != null && addon->AtkValuesCount > 2)
        {
            if (state[2].Int == 1)
                return true;
            if (state[2].Int == 0)
                return false;
        }

        var textNode = addon->GetTextNodeById(2);
        if (textNode == null)
            return false;

        var prompt = textNode->NodeText.ToString();
        return ClientTextMap.IsSpecifyQuantityPrompt(prompt) ||
               prompt.Contains("quantity", StringComparison.OrdinalIgnoreCase);
    }

    private static unsafe int GetVisibleInputNumericValue()
    {
        var ptr = BackstabTheTrade.GameGui.GetAddonByName("InputNumeric");
        if (ptr.Address == nint.Zero)
            return 0;

        var addon = (AtkUnitBase*)ptr.Address;
        if (!addon->IsVisible)
            return 0;

        var node = addon->GetNodeById(3);
        if (node == null)
            return 0;

        var numericInput = node->GetAsAtkComponentNumericInput();
        return numericInput == null ? 0 : numericInput->Value;
    }

    private void Delay(int ms, Step next)
    {
        _nextActionAt = DateTime.Now.AddMilliseconds(ms);
        _step = next;
    }

    private void ArmYesConfirm(YesConfirmOwner owner)
    {
        if (_yesConfirmOwner == owner)
            return;

        _yesConfirmOwner = owner;
        _yesConfirmClicked = false;
        _yesConfirmVisibleSince = DateTime.MinValue;
        _yesConfirmNextRetryAt = DateTime.MinValue;
        _yesConfirmLoggedWaiting = false;
        TrackVerbose($"[YesGuard] Armed {owner}.");
    }

    private bool TryTickYesConfirm(YesConfirmOwner owner)
    {
        if (_yesConfirmOwner != owner || _yesConfirmClicked)
            return false;

        if (!IsCompleteTradeConfirmWindowVisible())
        {
            if (_yesConfirmVisibleSince != DateTime.MinValue)
                TrackVerbose($"[YesGuard] Complete trade window not visible for {owner}; waiting.");

            _yesConfirmVisibleSince = DateTime.MinValue;
            _yesConfirmNextRetryAt = DateTime.MinValue;
            _yesConfirmLoggedWaiting = false;
            return false;
        }

        if (_yesConfirmVisibleSince == DateTime.MinValue)
        {
            _yesConfirmVisibleSince = DateTime.Now;
            _yesConfirmNextRetryAt = DateTime.Now.AddMilliseconds(Math.Max(0, _plugin.Configuration.YesButtonDelayMs));
            TrackVerbose($"[YesGuard] Complete trade window visible for {owner}; waiting {_plugin.Configuration.YesButtonDelayMs} ms before Yes.");
            return false;
        }

        if (DateTime.Now < _yesConfirmNextRetryAt)
        {
            if (!_yesConfirmLoggedWaiting)
            {
                _yesConfirmLoggedWaiting = true;
                var remainingMs = Math.Max(0, (int)(_yesConfirmNextRetryAt - DateTime.Now).TotalMilliseconds);
                TrackVerbose($"[YesGuard] Waiting Before Yes button for {owner}; remaining about {remainingMs} ms.");
            }

            return false;
        }

        if (!TryConfirmTradeYes())
        {
            var retryMs = Math.Max(50, _plugin.Configuration.YesConfirmRetryDelayMs);
            _yesConfirmNextRetryAt = DateTime.Now.AddMilliseconds(retryMs);
            TrackVerbose($"[YesGuard] Retry Yes for {owner} failed; retrying in {retryMs} ms.");
            return false;
        }

        _yesConfirmClicked = true;
        TrackVerbose($"[YesGuard] Yes click fired for {owner}.");
        return true;
    }

    private bool IsYesConfirmPending(YesConfirmOwner owner)
    {
        return _yesConfirmOwner == owner && !_yesConfirmClicked;
    }

    private bool IsReceiverYesConfirmWindowPending()
    {
        return IsYesConfirmPending(YesConfirmOwner.Receiver) && IsTradeConfirmWindowVisible();
    }

    private void ResetYesConfirm(YesConfirmOwner owner)
    {
        if (_yesConfirmOwner != owner)
            return;

        TrackVerbose($"[YesGuard] Reset {owner}.");
        _yesConfirmOwner = YesConfirmOwner.None;
        _yesConfirmClicked = false;
        _yesConfirmVisibleSince = DateTime.MinValue;
        _yesConfirmNextRetryAt = DateTime.MinValue;
        _yesConfirmLoggedWaiting = false;
    }

    private void MarkReceiverYesConfirmed(string logMessage)
    {
        _receiverConfirmedForWindow = true;
        _receiverTradeClickedForWindow = true;
        _receiverConfirmedAt = DateTime.Now;
        CaptureReceiverOfferSnapshot();
        AddTradeHistory("Confirmed", GetReceiverTradeSummary(), "Receiver mode confirmed Complete trade?", "Receiver", 0);
        TrackVerbose(logMessage);
        StatusMessage = "Receiver mode: Complete trade? detected - confirming Yes...";
    }

    private bool TryConfirmReceiverYes(string logMessage)
    {
        if (!TryTickYesConfirm(YesConfirmOwner.Receiver))
            return false;

        MarkReceiverYesConfirmed(logMessage);
        return true;
    }

    private void TickReceiverMode()
    {
        var receiverPollMs = Math.Max(10, _plugin.Configuration.ReceiverPollDelayMs);
        var receiverChangedPollMs = Math.Max(10, _plugin.Configuration.ReceiverChangedPollDelayMs);

        if (!_plugin.Configuration.ReceiverModeAutoConfirm)
        {
            ResetReceiverWindowState();
            return;
        }

        if (DateTime.Now < _receiverNextActionAt)
            return;

        bool confirmWindowOpen = IsTradeConfirmWindowVisible();
        if (confirmWindowOpen)
        {
            if (_receiverConfirmedForWindow)
            {
                StatusMessage = "Receiver mode: confirmed; waiting for trade to close...";
                _receiverNextActionAt = DateTime.Now.AddMilliseconds(receiverPollMs);
                return;
            }

            if (!ShouldAutoConfirmReceiverYesNo())
            {
                _receiverNextActionAt = DateTime.Now.AddMilliseconds(receiverPollMs);
                return;
            }

            ArmYesConfirm(YesConfirmOwner.Receiver);
            StatusMessage = "Receiver mode: Complete trade? detected - waiting before Yes...";
            TryConfirmReceiverYes("[ReceiverMode] Complete trade? detected; Yes/OK fired.");

            _receiverNextActionAt = DateTime.Now.AddMilliseconds(receiverPollMs);

            return;
        }

        if (_receiverConfirmedForWindow)
        {
            if (!TradeWindowOpen)
            {
                if (_receiverSawTradeWindow)
                    AddTradeHistory("Success", GetReceiverTradeSummary(), "Receiver trade window closed after confirmation", "Receiver", 0);
                ResetReceiverWindowState();
                return;
            }

            StatusMessage = "Receiver mode: confirmed; waiting for trade to close...";
            _receiverNextActionAt = DateTime.Now.AddMilliseconds(receiverPollMs);
            return;
        }

        if (!TradeWindowOpen)
        {
            if (_receiverSawTradeWindow)
            {
                if (_receiverConfirmedForWindow)
                    AddTradeHistory("Success", GetReceiverTradeSummary(), "Receiver trade window closed after confirmation", "Receiver", 0);
                else if (_receiverOfferSeenForWindow)
                    AddTradeHistory("Closed", GetReceiverTradeSummary(), "Receiver trade window closed before confirmation", "Receiver", 0);
            }
            ResetReceiverWindowState();
            return;
        }

        if (!_receiverSawTradeWindow)
        {
            _receiverSawTradeWindow = true;
            _receiverTradeClickedForWindow = false;
            _receiverOfferSeenForWindow = false;
            _receiverConfirmedForWindow = false;
            _receiverOfferFirstSeenAt = DateTime.MinValue;
            _receiverNextActionAt = DateTime.Now.AddMilliseconds(receiverPollMs);
            return;
        }

        if (!HasIncomingTradeOffer())
        {
            if (IsYesConfirmPending(YesConfirmOwner.Receiver))
            {
                StatusMessage = "Receiver mode: waiting for Complete trade? confirmation...";
                TryConfirmReceiverYes("[ReceiverMode] Complete trade? detected; Yes/OK fired.");

                _receiverNextActionAt = DateTime.Now.AddMilliseconds(receiverPollMs);
                return;
            }

            _receiverOfferSeenForWindow = false;
            _receiverOfferFirstSeenAt = DateTime.MinValue;
            _receiverOfferLastChangedAt = DateTime.MinValue;
            _receiverOfferStableSummary = string.Empty;
            ResetYesConfirm(YesConfirmOwner.Receiver);
            StatusMessage = "Receiver mode: waiting for partner offer...";
            _receiverNextActionAt = DateTime.Now.AddMilliseconds(receiverPollMs);
            return;
        }

        var currentOfferFingerprint = BuildIncomingTradeFingerprint();
        var currentOfferSummary = BuildIncomingTradeSummary();

        if (_receiverTradeClickedForWindow)
        {
            var retryDelay = Math.Max(250, _plugin.Configuration.ReceiverTradeRetryDelayMs);
            var elapsedSinceTradeClickMs = (DateTime.Now - _receiverTradeClickedAt).TotalMilliseconds;
            if (elapsedSinceTradeClickMs < retryDelay + ReceiverTradeConfirmGraceMs)
            {
                StatusMessage = "Receiver mode: waiting for Complete trade? confirmation...";
                _receiverNextActionAt = DateTime.Now.AddMilliseconds(receiverPollMs);
                return;
            }

            if (!string.IsNullOrEmpty(_receiverTradeClickOfferSummary) &&
                !string.Equals(_receiverTradeClickOfferSummary, currentOfferFingerprint, StringComparison.Ordinal))
            {
                if (IsReceiverYesConfirmWindowPending())
                {
                    StatusMessage = "Receiver mode: Complete trade? confirmation open; holding Yes guard...";
                    TryConfirmReceiverYes("[ReceiverMode] Complete trade? detected; Yes/OK fired.");

                    _receiverNextActionAt = DateTime.Now.AddMilliseconds(receiverPollMs);
                    return;
                }

                _receiverTradeClickedForWindow = false;
                _receiverOfferStableSummary = currentOfferFingerprint;
                _receiverOfferLastChangedAt = DateTime.Now;
                _receiverWindowSummary = currentOfferSummary;
                ResetYesConfirm(YesConfirmOwner.Receiver);
                StatusMessage = "Receiver mode: partner offer changed after Trade; waiting to settle...";
                TrackVerbose($"[ReceiverMode] Partner offer changed after Trade click; waiting to settle again. {_receiverTradeClickOfferSummary} -> {currentOfferFingerprint}");
                _receiverNextActionAt = DateTime.Now.AddMilliseconds(receiverChangedPollMs);
                return;
            }

            _receiverTradeClickedForWindow = false;
            StatusMessage = "Receiver mode: Trade not confirmed yet; retrying Trade click...";
            TrackVerbose("[ReceiverMode] Trade still pending; retrying Trade callback.");
        }

        if (!_receiverOfferSeenForWindow)
        {
            _receiverOfferSeenForWindow = true;
            _receiverOfferFirstSeenAt = DateTime.Now;
            _receiverOfferLastChangedAt = DateTime.Now;
            _receiverOfferStableSummary = currentOfferFingerprint;
            _receiverWindowSummary = currentOfferSummary;
            if (!_receiverHistoryStartedForWindow)
            {
                AddTradeHistory("Started", GetReceiverTradeSummary(), "Receiver detected incoming trade offer", "Receiver", 0);
                _receiverHistoryStartedForWindow = true;
            }
            StatusMessage = "Receiver mode: partner offer detected; waiting to settle...";
            TrackVerbose("[ReceiverMode] Partner offer detected; waiting to settle.");
            _receiverNextActionAt = DateTime.Now.AddMilliseconds(receiverPollMs);
            return;
        }

        if (!string.Equals(_receiverOfferStableSummary, currentOfferFingerprint, StringComparison.Ordinal))
        {
            var previousOfferFingerprint = _receiverOfferStableSummary;
            _receiverOfferStableSummary = currentOfferFingerprint;
            _receiverOfferLastChangedAt = DateTime.Now;
            _receiverWindowSummary = currentOfferSummary;
            StatusMessage = "Receiver mode: partner offer changed; waiting to settle...";
            TrackVerbose($"[ReceiverMode] Partner offer changed; waiting to settle again. {previousOfferFingerprint} -> {currentOfferFingerprint}");
            _receiverNextActionAt = DateTime.Now.AddMilliseconds(receiverChangedPollMs);
            return;
        }

        var offerStableSince = _receiverOfferLastChangedAt == DateTime.MinValue
            ? _receiverOfferFirstSeenAt
            : _receiverOfferLastChangedAt;

        if ((DateTime.Now - offerStableSince).TotalMilliseconds < Math.Max(0, _plugin.Configuration.ReceiverOfferSettleDelayMs))
        {
            StatusMessage = "Receiver mode: partner offer detected; waiting to settle...";
            _receiverNextActionAt = DateTime.Now.AddMilliseconds(receiverPollMs);
            return;
        }

        if (DoFireCallback("Trade", 2, 0, -1))
        {
            _receiverTradeClickedForWindow = true;
            _receiverTradeClickedAt = DateTime.Now;
            _receiverTradeClickOfferSummary = currentOfferFingerprint;
            ArmYesConfirm(YesConfirmOwner.Receiver);
            CaptureReceiverOfferSnapshot();
            AddTradeHistory("TradeClicked", GetReceiverTradeSummary(), "Receiver mode clicked Trade", "Receiver", 0);
            TrackVerbose("[ReceiverMode] Trade callback fired.");
            StatusMessage = "Receiver mode: Trade clicked.";
            _receiverNextActionAt = DateTime.Now.AddMilliseconds(Math.Max(250, _plugin.Configuration.ReceiverTradeRetryDelayMs));
        }
    }

    private void ResetReceiverWindowState()
    {
        _receiverSawTradeWindow = false;
        _receiverTradeClickedForWindow = false;
        _receiverOfferSeenForWindow = false;
        _receiverConfirmedForWindow = false;
        _receiverConfirmedAt = DateTime.MinValue;
        _receiverHistoryStartedForWindow = false;
        _receiverWindowSummary = string.Empty;
        _receiverOfferStableSummary = string.Empty;
        _receiverOfferFirstSeenAt = DateTime.MinValue;
        _receiverOfferLastChangedAt = DateTime.MinValue;
        _receiverTradeClickedAt = DateTime.MinValue;
        _receiverTradeClickOfferSummary = string.Empty;
        ResetYesConfirm(YesConfirmOwner.Receiver);
    }

    private void TickPassiveTradeHistory(bool tradeWindowVisible)
    {
        if (!TradeHistoryTrackingEnabled)
            return;

        if (IsRunning)
            return;

        if (tradeWindowVisible)
        {
            if (!_passiveTradeTrackingActive)
            {
                _passiveTradeTrackingActive = true;
                _passiveTradeFinalizeAt = DateTime.MinValue;
                _passiveBeforeGil = _plugin.InventoryWealth.CapturePlayerGil();
                _passiveBeforeItems = _plugin.InventoryWealth.CaptureItemQuantities();
                _passiveTradeMode = _plugin.Configuration.ReceiverModeAutoConfirm ? "Receiver" : "Manual";
            }

            return;
        }

        if (!_passiveTradeTrackingActive)
            return;

        if (_passiveTradeFinalizeAt == DateTime.MinValue)
        {
            _passiveTradeFinalizeAt = DateTime.Now.AddMilliseconds(500);
            return;
        }

        if (DateTime.Now < _passiveTradeFinalizeAt)
            return;

        var afterGil = _plugin.InventoryWealth.CapturePlayerGil();
        var afterItems = _plugin.InventoryWealth.CaptureItemQuantities();
        var (gilDelta, itemDeltas) = BuildTradeDeltas(_passiveBeforeGil, afterGil, _passiveBeforeItems, afterItems);

        if (gilDelta != 0 || itemDeltas.Length > 0)
        {
            AddTradeHistory(
                "Completed",
                BuildDeltaSummary(gilDelta, itemDeltas),
                "Detected from trade window close",
                _passiveTradeMode,
                0,
                gilDelta,
                itemDeltas);
        }

        _passiveTradeTrackingActive = false;
        _passiveTradeFinalizeAt = DateTime.MinValue;
        _passiveBeforeGil = 0;
        _passiveBeforeItems.Clear();
        _passiveTradeMode = "Manual";
    }

    private void OnInputNumericSetup(AddonEvent t, AddonArgs a)
    {
        HandleInputNumericReady("PostSetup");
    }

    private void OnInputNumericRequestedUpdate(AddonEvent t, AddonArgs a)
    {
        HandleInputNumericReady("PostRequestedUpdate");
    }

    private void HandleInputNumericReady(string source)
    {
        if (!IsRunning)
            return;

        if (_plannedContextMenuItemTrade)
        {
            var plannedChunkQuantity = GetPendingPlannedChunkQuantity();
            if (plannedChunkQuantity > 0)
            {
                _pendingDirectPlannedQuantity = plannedChunkQuantity;
                _pendingDirectPlannedQuantityDeadline = DateTime.Now.AddSeconds(3);
                _pendingDirectPlannedQuantityReadyAt = DateTime.Now.AddMilliseconds(_plugin.Configuration.PlannedQuantityReadyDelayMs);
                TrackVerbose($"[ManualFullAuto] InputNumeric ready via {source}; planned quantity {plannedChunkQuantity:N0} queued.");
                _inputNumericReady = false;
                if (!TryRunPendingDirectPlannedQuantity())
                {
                    TrackVerbose("[ManualFullAuto] Planned quantity not applied yet; waiting for InputNumeric visibility.");
                    _step = Step.EnterAndOk;
                    _nextActionAt = DateTime.Now.AddMilliseconds(Math.Max(10, _plugin.Configuration.InputNumericRetryDelayMs));
                }

                return;
            }
        }

        if (Mode == AutoTradeMode.Gil && !IsTradeAmountInputOpen())
            return;

        if (Mode != AutoTradeMode.Gil && !IsItemQuantityInputOpen())
            return;

        _inputNumericReady = true;

        bool shouldEnterAndOk =
            _step == Step.WaitInputNumeric ||
            _step == Step.WaitItemSlots ||
            _step == Step.WaitContextMenuInputNumeric ||
            (_plannedContextMenuItemTrade &&
             _step != Step.EnterAndOk &&
             _step != Step.WaitPopupClose &&
             _step != Step.WaitContextMenuTradeReady &&
             _step != Step.ClickTrade &&
             _step != Step.WaitTradeClose &&
             _step != Step.Idle);

        if (shouldEnterAndOk)
        {
            StatusMessage = Mode == AutoTradeMode.Gil ? "InputNumeric open." : "Specify quantity open.";
            if (_plannedContextMenuItemTrade)
            {
                var plannedChunkQuantity = GetPendingPlannedChunkQuantity();
                if (plannedChunkQuantity > 0)
                {
                    _pendingDirectPlannedQuantity = plannedChunkQuantity;
                    _pendingDirectPlannedQuantityDeadline = DateTime.Now.AddSeconds(3);
                    _pendingDirectPlannedQuantityReadyAt = DateTime.Now.AddMilliseconds(_plugin.Configuration.PlannedQuantityReadyDelayMs);
                }

                TrackVerbose($"[ManualFullAuto] InputNumeric ready via {source}; entering quantity step.");
            }

            _step = Step.EnterAndOk;
            _nextActionAt = DateTime.Now;
        }
    }

    private bool TryRunPendingDirectPlannedQuantity()
    {
        if (_pendingDirectPlannedQuantity <= 0)
            return false;

        if (!_plannedContextMenuItemTrade)
        {
            ResetPendingDirectPlannedQuantity();
            return false;
        }

        if (!IsAddonVisible("InputNumeric"))
        {
            if (DateTime.Now > _pendingDirectPlannedQuantityDeadline)
                ResetPendingDirectPlannedQuantity();
            return false;
        }

        if (!IsItemQuantityInputOpen())
        {
            if (DateTime.Now > _pendingDirectPlannedQuantityDeadline)
                ResetPendingDirectPlannedQuantity();
            else
                _nextActionAt = DateTime.Now.AddMilliseconds(_plugin.Configuration.PlannedTradeSlotPollMs);
            return true;
        }

        if (DateTime.Now < _pendingDirectPlannedQuantityReadyAt)
        {
            _nextActionAt = _pendingDirectPlannedQuantityReadyAt;
            return true;
        }

        var quantity = _pendingDirectPlannedQuantity;
        TrackVerbose($"[ManualFullAuto] Applying pending planned quantity {quantity:N0}.");
        if (!DoSetValueAndOk(quantity))
        {
            if (DateTime.Now > _pendingDirectPlannedQuantityDeadline)
                ResetPendingDirectPlannedQuantity();
            else
                _nextActionAt = DateTime.Now.AddMilliseconds(Math.Max(10, _plugin.Configuration.InputNumericRetryDelayMs));
            return true;
        }

        ResetPendingDirectPlannedQuantity();
        _inputNumericReady = false;
        _step = Step.WaitPopupClose;
        _nextActionAt = DateTime.Now.AddMilliseconds(_plugin.Configuration.PlannedPopupCloseWaitMs);
        StatusMessage = $"Set planned item quantity {quantity:N0} - waiting for popup close...";
        return true;
    }

    private void CaptureTradeVerificationBaseline()
    {
        _tradeVerifyBeforeGil = _plugin.InventoryWealth.CapturePlayerGil();
        _tradeVerifyObservedIncomingGil = 0;
        _tradeVerifyDeadline = DateTime.Now.AddSeconds(2);

        if (Mode == AutoTradeMode.Gil)
        {
            _tradeVerifyBatch = null;
            _tradeVerifyBefore.Clear();
            AddTradeHistory("Started", BuildCurrentTradeSummary(), $"Trade attempt opened; gil baseline {_tradeVerifyBeforeGil:N0} captured");
            TrackVerbose($"[AutoTrade] Trade verify baseline captured for gil: {_tradeVerifyBeforeGil:N0}.");
            return;
        }

        var batch = GetCurrentBatch();
        if (batch == null)
        {
            _tradeVerifyBatch = null;
            _tradeVerifyBefore.Clear();
            AddTradeHistory("Started", BuildCurrentTradeSummary(), "Trade attempt opened; inventory baseline unavailable");
            return;
        }

        _tradeVerifyBatch = batch;
        _tradeVerifyBefore = _plugin.InventoryWealth.CaptureItemQuantities();
        AddTradeHistory("Started", BuildCurrentTradeSummary(), $"Trade attempt opened; captured inventory baseline for batch {batch.Value.BatchNumber}");
        TrackVerbose($"[AutoTrade] Trade verify baseline captured for batch {batch.Value.BatchNumber}, gil={_tradeVerifyBeforeGil:N0}.");
    }

    private bool VerifyCompletedTrade(bool logFailure = true)
    {
        _plugin.InventoryWealth.Invalidate();

        if (Mode == AutoTradeMode.Gil)
        {
            var expectedGilDecrease = GetCurrentGilTradeAmount();
            var afterGil = _plugin.InventoryWealth.CapturePlayerGil();
            long netGilDecrease = _tradeVerifyBeforeGil - afterGil;
            long givenGil = netGilDecrease + _tradeVerifyObservedIncomingGil;
            if (givenGil + GilVerifyTolerance < expectedGilDecrease)
            {
                if (logFailure)
                {
                    AddTradeHistory(
                        "Failed",
                        BuildCurrentTradeSummary(),
                        $"Expected given gil {expectedGilDecrease:N0}, actual {givenGil:N0} (net decrease {netGilDecrease:N0}, received {_tradeVerifyObservedIncomingGil:N0}, tolerance {GilVerifyTolerance:N0})");
                    TrackImportant(
                        $"[AutoTrade] Trade verify failed: gil expected given {expectedGilDecrease:N0}, actual {givenGil:N0}, net decrease {netGilDecrease:N0}, received {_tradeVerifyObservedIncomingGil:N0}, tolerance {GilVerifyTolerance:N0} (before={_tradeVerifyBeforeGil:N0}, after={afterGil:N0}).");
                }
                return false;
            }

            AddTradeHistory(
                "Success",
                BuildCurrentTradeSummary(),
                $"Gil given {givenGil:N0} (net decrease {netGilDecrease:N0}, received {_tradeVerifyObservedIncomingGil:N0})",
                gilDelta: -givenGil);
            TrackImportant($"[AutoTrade] Trade verify success: gil given {givenGil:N0} (net decrease {netGilDecrease:N0}, received {_tradeVerifyObservedIncomingGil:N0}).");
            return true;
        }

        if (_tradeVerifyBatch == null || _tradeVerifyBefore.Count == 0)
        {
            AddTradeHistory(
                "Unverified",
                BuildCurrentTradeSummary(),
                "Trade closed, but no inventory baseline was available for verification");
            TrackVerbose("[AutoTrade] Trade verify skipped: no item baseline captured.");
            return true;
        }

        var after = _plugin.InventoryWealth.CaptureItemQuantities();
        var expected = _tradeVerifyBatch.Value.Chunks
            .GroupBy(chunk => chunk.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(chunk => chunk.Quantity));

        var successDeltas = new List<TradeItemDelta>();

        foreach (var pair in expected)
        {
            _tradeVerifyBefore.TryGetValue(pair.Key, out var beforeQuantity);
            after.TryGetValue(pair.Key, out var afterQuantity);
            long decreased = beforeQuantity - afterQuantity;
            var name = _tradeVerifyBatch.Value.Chunks.First(chunk => chunk.ItemId == pair.Key).Name;
            if (decreased < pair.Value)
            {
                if (logFailure)
                {
                    AddTradeHistory(
                        "Failed",
                        BuildCurrentTradeSummary(),
                        $"{name} expected decrease {pair.Value:N0}, actual {decreased:N0}");
                    TrackImportant(
                        $"[AutoTrade] Trade verify failed: {name} expected decrease {pair.Value:N0}, actual {decreased:N0} (before={beforeQuantity:N0}, after={afterQuantity:N0}).");
                }
                return false;
            }

            successDeltas.Add(new TradeItemDelta(pair.Key, name, -decreased));
        }

        AddTradeHistory(
            "Success",
            BuildCurrentTradeSummary(),
            $"Inventory batch {_tradeVerifyBatch.Value.BatchNumber} decreased as planned",
            itemDeltas: successDeltas.ToArray());
        TrackImportant(
            $"[AutoTrade] Trade verify success: batch {_tradeVerifyBatch.Value.BatchNumber} inventory decreased as planned.");
        return true;
    }

    private void AddTradeHistory(
        string result,
        string summary,
        string detail,
        string? modeOverride = null,
        int? attemptOverride = null,
        long gilDelta = 0,
        TradeItemDelta[]? itemDeltas = null)
    {
        if (!TradeHistoryTrackingEnabled)
            return;

        _tradeHistory.Insert(0, new TradeHistoryEntry(
            DateTime.Now,
            modeOverride ?? Mode.ToString(),
            result,
            summary,
            detail,
            attemptOverride ?? GetCurrentAttemptNumber(),
            gilDelta,
            itemDeltas ?? Array.Empty<TradeItemDelta>()));

        if (_tradeHistory.Count > MaxTradeHistory)
            _tradeHistory.RemoveRange(MaxTradeHistory, _tradeHistory.Count - MaxTradeHistory);
    }

    private int GetCurrentAttemptNumber()
    {
        if (TotalTrades <= 0)
            return 0;

        return Math.Clamp(TotalTrades - TradesRemaining + 1, 1, TotalTrades);
    }

    private string BuildCurrentTradeSummary()
    {
        if (Mode == AutoTradeMode.Gil)
            return $"Gil x{GetCurrentGilTradeAmount():N0}";

        var batch = _tradeVerifyBatch ?? GetCurrentBatch();
        if (batch == null)
            return "Item batch";

        var groups = batch.Value.Chunks
            .GroupBy(chunk => chunk.Name)
            .Select(group => $"{group.Key} x{group.Sum(chunk => chunk.Quantity):N0}")
            .ToArray();

        return groups.Length == 0
            ? $"Batch {batch.Value.BatchNumber}"
            : $"Batch {batch.Value.BatchNumber}: {string.Join(", ", groups)}";
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

    private long GetCurrentGilTradeAmount()
    {
        if (Mode != AutoTradeMode.Gil || _gilTradePlan.Count == 0)
            return GilPerTrade;

        int index = Math.Clamp(TotalTrades - TradesRemaining, 0, _gilTradePlan.Count - 1);
        return _gilTradePlan[index];
    }

    private static (long GilDelta, TradeItemDelta[] ItemDeltas) BuildTradeDeltas(
        long beforeGil,
        long afterGil,
        IReadOnlyDictionary<uint, long> beforeItems,
        IReadOnlyDictionary<uint, long> afterItems)
    {
        long gilDelta = afterGil - beforeGil;
        var itemIds = beforeItems.Keys.Concat(afterItems.Keys).Distinct().OrderBy(id => id).ToArray();
        var deltas = new List<TradeItemDelta>();
        foreach (var itemId in itemIds)
        {
            beforeItems.TryGetValue(itemId, out var beforeQuantity);
            afterItems.TryGetValue(itemId, out var afterQuantity);
            long delta = afterQuantity - beforeQuantity;
            if (delta == 0)
                continue;

            deltas.Add(new TradeItemDelta(itemId, ResolveItemName(itemId), delta));
        }

        return (gilDelta, deltas.ToArray());
    }

    private static string BuildDeltaSummary(long gilDelta, IReadOnlyList<TradeItemDelta> itemDeltas)
    {
        var parts = new List<string>();
        if (gilDelta != 0)
            parts.Add($"Gil {(gilDelta > 0 ? "+" : string.Empty)}{gilDelta:N0}");

        foreach (var delta in itemDeltas)
            parts.Add($"{delta.Name} {(delta.QuantityDelta > 0 ? "+" : string.Empty)}{delta.QuantityDelta:N0}");

        return parts.Count == 0 ? "No change" : string.Join(", ", parts);
    }

    private static string ResolveItemName(uint itemId)
    {
        var itemSheet = BackstabTheTrade.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        if (itemSheet != null && itemSheet.TryGetRow(itemId, out var row))
        {
            var name = row.Name.ToString();
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }

        return $"Item {itemId}";
    }

    private void CaptureReceiverOfferSnapshot()
    {
        var summary = BuildIncomingTradeSummary();
        if (!string.IsNullOrWhiteSpace(summary))
            _receiverWindowSummary = summary;
    }

    private string GetReceiverTradeSummary()
    {
        return string.IsNullOrWhiteSpace(_receiverWindowSummary)
            ? BuildIncomingTradeSummary()
            : _receiverWindowSummary;
    }

    private void ResetTradeVerification()
    {
        _tradeVerifyBefore.Clear();
        _tradeVerifyBeforeGil = 0;
        _tradeVerifyObservedIncomingGil = 0;
        _tradeVerifyBatch = null;
        _tradeVerifyDeadline = DateTime.MinValue;
        _tradePostCloseGraceApplied = false;
    }

    private void ResetPendingDirectPlannedQuantity()
    {
        _pendingDirectPlannedQuantity = 0;
        _pendingDirectPlannedQuantityDeadline = DateTime.MinValue;
        _pendingDirectPlannedQuantityReadyAt = DateTime.MinValue;
    }

    private void OnInputNumericFinalize(AddonEvent t, AddonArgs a)
    {
        if (!IsRunning)
            return;

        if (_autoConfirmCurrentInputOnly && _step == Step.WaitPopupClose)
        {
            if (_contextMenuTradeAfterReady && HasAnyTradeItemSelected())
            {
                StatusMessage = "Specify quantity confirmed - clicking Trade...";
                TrackVerbose("[AutoTrade2] Popup closed and trade slot already ready.");
                _step = Step.ClickTrade;
                _nextActionAt = DateTime.Now.AddMilliseconds(Math.Min(Math.Max(1, _plugin.Configuration.ConfirmDelayMs), _plugin.Configuration.PlannedPopupCloseWaitMs));
                return;
            }

            StatusMessage = "Specify quantity confirmed - waiting for trade slot...";
            _contextMenuTradeReadyDeadline = DateTime.Now.AddSeconds(3);
            _step = Step.WaitContextMenuTradeReady;
            _nextActionAt = DateTime.Now.AddMilliseconds(Math.Min(Math.Max(1, _plugin.Configuration.ConfirmDelayMs), _plugin.Configuration.PlannedPopupCloseWaitMs));
            return;
        }

        if (_step == Step.WaitPopupClose)
        {
            StatusMessage = "Popup closed.";
            _step = Mode == AutoTradeMode.Gil ? Step.ClickTrade : Step.WaitItemSlots;
            _nextActionAt = DateTime.Now.AddMilliseconds(Mode == AutoTradeMode.Gil ? Math.Max(1, _plugin.Configuration.ConfirmDelayMs) : 0);
        }
    }

    private void ResetContextMenuAutoConfirm(string status)
    {
        _autoConfirmCurrentInputOnly = false;
        _plannedContextMenuItemTrade = false;
        _contextMenuTradeAfterReady = false;
        ResetPendingDirectPlannedQuantity();
        _pendingPlannedChunkQuantity = 0;
        IsRunning = false;
        _step = Step.Idle;
        ActiveItemPlan = InventoryTradePlan.Empty;
        _itemTradeBatches = Array.Empty<InventoryTradeBatch>();
        TotalTrades = 0;
        TradesRemaining = 0;
        StatusMessage = status;
    }

    private void AbortPlannedContextMenuTrade(string status)
    {
        _plannedContextMenuItemTrade = false;
        _autoConfirmCurrentInputOnly = false;
        _contextMenuTradeAfterReady = false;
        _inputNumericReady = false;
        ResetPendingDirectPlannedQuantity();
        _pendingPlannedChunkQuantity = 0;
        _step = Step.WaitItemSlots;
        _nextActionAt = DateTime.Now.AddMilliseconds(Math.Max(_plugin.Configuration.PlannedNextItemWaitMs, _plugin.Configuration.InputNumericRetryDelayMs));
        StatusMessage = status;
    }

    private void FinishContextMenuFillOnly(string status)
    {
        _plannedContextMenuItemTrade = false;
        _autoConfirmCurrentInputOnly = false;
        _contextMenuTradeAfterReady = false;
        _inputNumericReady = false;
        ResetPendingDirectPlannedQuantity();
        _pendingPlannedChunkQuantity = 0;
        IsRunning = false;
        _step = Step.Idle;
        ActiveItemPlan = InventoryTradePlan.Empty;
        _itemTradeBatches = Array.Empty<InventoryTradeBatch>();
        TotalTrades = 0;
        TradesRemaining = 0;
        StatusMessage = status;
    }

    private void OnSelectYesnoSetup(AddonEvent t, AddonArgs a)
    {
        _tradeConfirmSeenForWindow = true;

        if (IsRunning && _step == Step.WaitTradeClose)
        {
            ArmYesConfirm(YesConfirmOwner.Sender);
            StatusMessage = "Trade confirm popup open.";
            _nextActionAt = DateTime.Now;
            return;
        }

        if (!_plugin.Configuration.ReceiverModeAutoConfirm)
            return;

        if (_receiverConfirmedForWindow)
            return;

        if (!ShouldAutoConfirmReceiverYesNo())
            return;

        ArmYesConfirm(YesConfirmOwner.Receiver);
        StatusMessage = "Receiver mode: Complete trade? setup hook detected - waiting before Yes...";
        _receiverNextActionAt = DateTime.Now;
    }

    private unsafe bool ShouldAutoConfirmReceiverYesNo()
    {
        return _plugin.Configuration.ReceiverModeAutoConfirm &&
               (TradeWindowOpen ||
                _receiverSawTradeWindow ||
                _receiverOfferSeenForWindow ||
                _receiverTradeClickedForWindow ||
                HasIncomingTradeOffer());
    }

    private string GetTradeCommandVariant()
    {
        return (_tradeSendAttempt % 2) switch
        {
            0 => "/trade <t>",
            _ => "trade <t>",
        };
    }

    private Step GetNextAutoTradeStartStep()
    {
        if (HasCurrentTargetForAutoTrade())
            return Step.SendTrade;

        return Step.SendTarget;
    }

    private bool HasCurrentTargetForAutoTrade()
    {
        var trimmed = TargetName.Trim();
        if (trimmed.Length == 0)
            return false;

        var target = BackstabTheTrade.TargetManager.Target;
        if (target == null)
            return false;

        return string.Equals(target.Name.TextValue, trimmed, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveTradeTargetName(string targetName)
    {
        var trimmed = targetName.Trim();
        if (trimmed.Length > 0)
            return trimmed;

        var target = BackstabTheTrade.TargetManager.Target;
        if (target == null || (int)target.ObjectKind != 1)
            return string.Empty;

        return target.Name.TextValue?.Trim() ?? string.Empty;
    }

    private bool ShouldStopBecauseTargetChanged()
    {
        if (!IsRunning)
            return false;

        if (string.IsNullOrWhiteSpace(TargetName))
            return false;

        return !HasCurrentTargetForAutoTrade();
    }

    private void StopBecauseTargetChanged()
    {
        var expectedTarget = TargetName.Trim();
        var actualTarget = BackstabTheTrade.TargetManager.Target?.Name.TextValue?.Trim() ?? string.Empty;
        var detail = string.IsNullOrWhiteSpace(actualTarget)
            ? $"Target player no longer matched {expectedTarget}."
            : $"Target player changed from {expectedTarget} to {actualTarget}.";

        AddTradeHistory("Stopped", BuildCurrentTradeSummary(), detail);
        TrackImportant($"[TargetMonitor] {detail} Auto trade stopped immediately.");
        StopTradeRun("Target player changed - trade stopped.");
    }

    public void NotifyTooFarAwaySignal(string source, string message)
    {
        TrackImportant($"[TargetMonitor] {source} reported: {message}");
        if (!IsRunning)
            return;

        var detail = $"Game reported target player {TargetName.Trim()} is too far away to continue trading.";
        AddTradeHistory("Stopped", BuildCurrentTradeSummary(), detail);
        StopTradeRun("Game reported target too far away - trade stopped.");
    }

    private void StopTradeRun(string statusMessage)
    {
        _autoConfirmCurrentInputOnly = false;
        _plannedContextMenuItemTrade = false;
        _contextMenuTradeAfterReady = false;
        _pendingPlannedContextMenuTrigger = false;
        ResetPendingDirectPlannedQuantity();
        _pendingPlannedChunkQuantity = 0;
        _pendingPlannedItemId = 0;
        _pendingPlannedItemName = string.Empty;
        ResetTradeVerification();
        IsRunning = false;
        _step = Step.Idle;
        ActiveItemPlan = InventoryTradePlan.Empty;
        _itemTradeBatches = Array.Empty<InventoryTradeBatch>();
        _gilTradePlan = Array.Empty<long>();
        _visibleInventoryDispatchAttempts = 0;
        _visibleInventoryDispatchMatched = -1;
        ResetContextApiState();
        _activeInventoryBlockNumber = 0;
        StatusMessage = statusMessage;
        CompletedRunSerial++;
    }

    private static string QuoteTradeTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return target;

        if (target.Contains('"'))
            target = target.Replace("\"", string.Empty);

        return target.Contains(' ') ? $"\"{target}\"" : target;
    }

    private bool TryConfirmTradeYes()
    {
        bool handled = DoSelectYesnoDispatch(_plugin.EventTracker, "SelectYesno") ||
                       DoSelectYesnoDispatch(_plugin.EventTracker, "SelectYesNo") ||
                       DoSelectYesno("SelectYesno") ||
                       DoSelectYesno("SelectYesNo");
        if (handled)
            _tradeConfirmSeenForWindow = true;

        return handled;
    }

    private static bool IsTradeConfirmWindowVisible()
    {
        return IsAddonVisible("SelectYesno") || IsAddonVisible("SelectYesNo");
    }

    private static bool IsCompleteTradeConfirmWindowVisible()
    {
        // The addon can be allocated before the Yes button is ready; only click once node 8 is usable.
        return IsSelectYesnoReady("SelectYesno") || IsSelectYesnoReady("SelectYesNo");
    }

    private static unsafe bool IsSelectYesnoReady(string addonName)
    {
        var ptr = BackstabTheTrade.GameGui.GetAddonByName(addonName);
        if (ptr.Address == nint.Zero)
            return false;

        var addon = (AtkUnitBase*)ptr.Address;
        if (!addon->IsVisible)
            return false;

        var yesNode = addon->GetNodeById(8);
        if (yesNode == null || !yesNode->IsVisible())
            return false;

        return yesNode->GetAsAtkComponentButton() != null;
    }

    private InventoryTradeBatch? GetCurrentBatch()
    {
        int batchIndex = TotalTrades - TradesRemaining;
        if (batchIndex < 0 || batchIndex >= _itemTradeBatches.Count)
            return null;

        return _itemTradeBatches[batchIndex];
    }

    private long GetPendingBatchChunkQuantity()
    {
        var batch = GetCurrentBatch();
        if (batch == null)
            return 0;

        int matched = GetMatchedChunkCount(batch.Value);
        if (matched < 0 || matched >= batch.Value.Chunks.Count)
            return 0;

        return batch.Value.Chunks[matched].Quantity;
    }

    private long GetPendingPlannedChunkQuantity()
    {
        if (_pendingPlannedChunkQuantity > 0)
            return _pendingPlannedChunkQuantity;

        return GetPendingBatchChunkQuantity();
    }

    private static string DescribeInventorySourceHint(InventoryTradeBatchChunk chunk)
    {
        if (chunk.StackSources.Count == 0)
            return "Searching inventory stacks...";

        return $"Inventory fill plan: {string.Join(", ", chunk.StackSources.Select(source => source.Label))}";
    }

    private void ResetContextApiState()
    {
        _contextApiOpenMatched = -1;
        _contextApiTraceMatched = -1;
        _contextApiSkipLoggedMatched = -1;
        _contextApiOpenRetryAt = DateTime.MinValue;
    }

    private void LogContextApiSkipOnce(int matchedChunkCount, string message)
    {
        if (_contextApiSkipLoggedMatched == matchedChunkCount)
            return;

        _contextApiSkipLoggedMatched = matchedChunkCount;
        TrackVerbose(message);
    }

    private void TrackVerbose(string message)
    {
        _plugin.EventTracker.LogVerbose(message);
    }

    private void TrackImportant(string message)
    {
        _plugin.EventTracker.LogExternal(message);
    }

    private static string DescribeFirstBatchChunkForLog(IReadOnlyList<InventoryTradeBatch> batches)
    {
        if (batches.Count == 0)
            return "<none>";

        var firstBatch = batches[0];
        if (firstBatch.Chunks.Count == 0)
            return $"batch {firstBatch.BatchNumber} has no chunks";

        return DescribeChunkForLog(firstBatch.Chunks[0]);
    }

    private static string DescribeChunkForLog(InventoryTradeBatchChunk chunk)
    {
        var sources = chunk.StackSources.Count == 0
            ? "sources=<none>"
            : $"sources={string.Join(" | ", chunk.StackSources.Select(source => $"{source.ContainerType}:{source.SlotIndex}x{source.Quantity}"))}";

        return $"{chunk.Name} ({chunk.ItemId}) x{chunk.Quantity}; {sources}";
    }

    private unsafe bool TryFillVisibleInventoryNode(InventoryTradeBatchChunk chunk, int matchedChunkCount, out string status)
    {
        status = string.Empty;

        if (_visibleInventoryDispatchMatched != matchedChunkCount)
        {
            _visibleInventoryDispatchMatched = matchedChunkCount;
            _visibleInventoryDispatchAttempts = 0;
        }

        if (_visibleInventoryDispatchAttempts >= 8 || chunk.StackSources.Count == 0)
            return false;

        var primarySource = chunk.StackSources[0];
        if (primarySource.BlockNumber <= 0 || primarySource.SlotIndex < 0 || primarySource.SlotIndex >= 35)
        {
            status = $"Visible node dispatch pending page/container nav. First source: {primarySource.Label}";
            return false;
        }

        var ptr = BackstabTheTrade.GameGui.GetAddonByName("Inventory");
        if (ptr.Address == nint.Zero)
            return false;

        var addon = (AtkUnitBase*)ptr.Address;
        if (!addon->IsVisible || !IsTradeInventorySelectionOpen(addon))
            return false;

        if (_activeInventoryBlockNumber != primarySource.BlockNumber)
        {
            uint? tabNodeId = GetInventoryBlockTabNodeId(primarySource.BlockNumber);
            if (tabNodeId == null)
            {
                status = $"Unsupported inventory block for {primarySource.Label}.";
                return false;
            }

            if (!TryInventoryNodeDispatch(_plugin.EventTracker, addon, tabNodeId.Value))
                return false;

            _activeInventoryBlockNumber = primarySource.BlockNumber;
            _visibleInventoryDispatchAttempts++;
            status = $"Switched inventory view to block {primarySource.BlockNumber} for {primarySource.Label}.";
            return true;
        }

        const uint inventoryGridNodeId = 13;
        uint slotParam = (uint)primarySource.DisplaySlotNumber;
        if (!TryInventoryNodeDispatch(_plugin.EventTracker, addon, inventoryGridNodeId, slotParam))
            return false;

        _visibleInventoryDispatchAttempts++;
        status =
            $"Visible inventory dispatch fired for {chunk.Name} x{chunk.Quantity:N0} using {primarySource.Label} " +
            $"(row {primarySource.VisualRow}, col {primarySource.VisualColumn}).";
        return true;
    }

    private unsafe bool TryOpenInventoryContextForChunk(InventoryTradeBatchChunk chunk, int matchedChunkCount, out string status)
    {
        status = string.Empty;

        if (chunk.StackSources.Count == 0)
        {
            LogContextApiSkipOnce(matchedChunkCount, $"[ManualFullAuto] ContextAPI skipped for {chunk.Name} ({chunk.ItemId}): no recorded stack source.");
            return false;
        }

        if (_contextApiOpenMatched == matchedChunkCount && DateTime.Now < _contextApiOpenRetryAt)
        {
            status = $"Opened context API for {chunk.Name} x{chunk.Quantity:N0}; waiting for native inventory context menu...";
            _nextActionAt = DateTime.Now.AddMilliseconds(50);
            return true;
        }

        var source = chunk.StackSources.FirstOrDefault(source => source.Quantity > 0);
        if (source.Quantity <= 0)
        {
            LogContextApiSkipOnce(matchedChunkCount, $"[ManualFullAuto] ContextAPI skipped for {chunk.Name} ({chunk.ItemId}): recorded sources have no quantity.");
            return false;
        }

        try
        {
            var inventory = InventoryManager.Instance();
            if (inventory == null)
            {
                status = "InventoryManager unavailable; waiting for manual right-click.";
                LogContextApiSkipOnce(matchedChunkCount, $"[ManualFullAuto] ContextAPI skipped for {chunk.Name} ({chunk.ItemId}): InventoryManager unavailable.");
                return false;
            }

            var slot = inventory->GetInventorySlot(source.ContainerType, source.SlotIndex);
            if (slot == null || slot->ItemId == 0)
            {
                status = $"Source slot empty for {chunk.Name}; waiting for manual right-click.";
                LogContextApiSkipOnce(matchedChunkCount, $"[ManualFullAuto] ContextAPI skipped for {chunk.Name} ({chunk.ItemId}): source {source.ContainerType}:{source.SlotIndex} empty.");
                return false;
            }

            var slotItemId = slot->GetItemId();
            if (slotItemId != chunk.ItemId)
            {
                status = $"Source slot changed for {chunk.Name}; waiting for manual right-click.";
                LogContextApiSkipOnce(matchedChunkCount, $"[ManualFullAuto] ContextAPI skipped for {chunk.Name} ({chunk.ItemId}): source {source.ContainerType}:{source.SlotIndex} now itemId={slot->ItemId}.");
                return false;
            }

            var context = AgentInventoryContext.Instance();
            if (context == null)
            {
                status = "AgentInventoryContext unavailable; waiting for manual right-click.";
                LogContextApiSkipOnce(matchedChunkCount, $"[ManualFullAuto] ContextAPI skipped for {chunk.Name} ({chunk.ItemId}): AgentInventoryContext unavailable.");
                return false;
            }

            context->OpenForItemSlot(source.ContainerType, source.SlotIndex, 0, 0);
            _contextApiOpenMatched = matchedChunkCount;
            _contextApiOpenRetryAt = DateTime.Now.AddSeconds(2);
            _nextActionAt = DateTime.Now.AddMilliseconds(120);
            status = $"Opening native item context for {chunk.Name} x{chunk.Quantity:N0} from {source.ContainerType}:{source.SlotIndex + 1}...";
            TrackVerbose($"[ManualFullAuto] OpenForItemSlot source={source.ContainerType}:{source.SlotIndex}, itemId={chunk.ItemId}, qty={chunk.Quantity}");
            return true;
        }
        catch (Exception ex)
        {
            status = $"Context API open failed ({ex.GetType().Name}); waiting for manual right-click.";
            TrackImportant($"[ManualFullAuto] OpenForItemSlot failed: {ex.Message}");
            return false;
        }
    }

    private static uint? GetInventoryBlockTabNodeId(int blockNumber) => blockNumber switch
    {
        // N8 appears to be the key-item style tab in the trade inventory selector.
        // Normal inventory blocks line up one node later.
        1 => 9,
        2 => 10,
        3 => 11,
        4 => 12,
        _ => null,
    };

    private static unsafe bool IsTradeInventorySelectionOpen(AtkUnitBase* addon)
    {
        var textNode = addon->GetTextNodeById(3);
        if (textNode == null)
            return false;

        return string.Equals(textNode->NodeText.ToString(), "Select an item to trade.", StringComparison.Ordinal);
    }

    private static unsafe bool HasAnyTradeItemSelected()
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
            return false;

        var slots = manager->TradeItemsLocal;
        for (var i = 0; i < 5; i++)
        {
            if (slots[i].ItemId != 0 && slots[i].Quantity > 0)
                return true;
        }

        return false;
    }

    private static unsafe bool HasIncomingTradeOffer()
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
            return false;

        var partnerSlots = manager->TradeItemsRemote;
        for (var i = 0; i < 5; i++)
        {
            if (partnerSlots[i].ItemId != 0 && partnerSlots[i].Quantity > 0)
                return true;
        }

        // TradeItemsRemote 6th slot is gil.
        if (partnerSlots[5].ItemId != 0 || partnerSlots[5].Quantity > 0)
            return true;

        return false;
    }

    private static unsafe long GetIncomingTradeGilAmount()
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
            return 0;

        return Math.Max(0, manager->TradeItemsRemote[5].Quantity);
    }

    private static unsafe string BuildIncomingTradeSummary()
    {
        var manager = InventoryManager.Instance();
        var itemSheet = BackstabTheTrade.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        if (manager == null || itemSheet == null)
            return "Incoming trade";

        var parts = new List<string>();
        var partnerSlots = manager->TradeItemsRemote;
        for (var i = 0; i < 5; i++)
        {
            var slot = partnerSlots[i];
            var normalizedItemId = InventoryWealthService.NormalizeItemId(slot.ItemId);
            if (normalizedItemId == 0 || slot.Quantity <= 0)
                continue;

            string name = itemSheet.TryGetRow(normalizedItemId, out var itemRow)
                ? itemRow.Name.ToString()
                : $"Item {normalizedItemId}";

            parts.Add($"{name} x{slot.Quantity:N0}");
        }

        if (partnerSlots[5].ItemId != 0 || partnerSlots[5].Quantity > 0)
            parts.Add($"Gil x{partnerSlots[5].Quantity:N0}");

        return parts.Count == 0
            ? "Incoming trade"
            : string.Join(", ", parts);
    }

    private static unsafe string BuildIncomingTradeFingerprint()
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
            return "remote:<unavailable>";

        var partnerSlots = manager->TradeItemsRemote;
        var parts = new List<string>(6);
        for (var i = 0; i < 6; i++)
        {
            var slot = partnerSlots[i];
            parts.Add($"{i}:{slot.ItemId}:{slot.Quantity}");
        }

        return string.Join("|", parts);
    }

    private static unsafe bool TryInventoryNodeDispatch(
        EventTracker tracker,
        AtkUnitBase* addon,
        uint nodeId,
        uint? syntheticBaseParamOverride = null)
    {
        try
        {
            var node = addon->GetNodeById(nodeId);
            if (node == null)
                return false;

            var componentNode = (AtkComponentNode*)node;
            if (componentNode == null)
                return false;

            var componentBase = componentNode->Component;
            if (componentBase == null)
                return false;

            EventTracker.ReplayButtonEventContext pressReplay;
            EventTracker.ReplayButtonEventContext releaseReplay;
            EventTracker.ReplayButtonEventContext clickReplay;
            bool useReplayData = tracker.TryGetInventoryReplaySequence(nodeId, out pressReplay, out releaseReplay, out clickReplay);

            if (!useReplayData)
            {
                uint baseParam = syntheticBaseParamOverride ?? (4 * nodeId - 3);
                pressReplay = CreateSyntheticReplay(23, baseParam + 1);
                releaseReplay = CreateSyntheticReplay(24, baseParam + 2);
                clickReplay = CreateSyntheticReplay(25, baseParam);
                BackstabTheTrade.Log.Warning($"Inventory node {nodeId}: no captured replay context yet, using synthetic node sequence.");
            }

            return TryComponentNodeDispatch(
                addon,
                componentBase,
                componentNode,
                pressReplay,
                releaseReplay,
                clickReplay,
                useReplayData);
        }
        catch (Exception ex)
        {
            BackstabTheTrade.Log.Warning(ex, $"Inventory node dispatch failed for node {nodeId}.");
            return false;
        }
    }

    private static unsafe int GetMatchedChunkCount(InventoryTradeBatch batch)
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
            return 0;

        var slots = manager->TradeItemsLocal;

        int matched = 0;
        for (int i = 0; i < batch.Chunks.Count && i < 5; i++)
        {
            var slot = slots[i];
            var chunk = batch.Chunks[i];
            if (slot.GetItemId() == chunk.ItemId && slot.Quantity == chunk.Quantity)
                matched++;
            else
                break;
        }

        return matched;
    }

    private static unsafe bool DoSelectYesnoDispatch(EventTracker tracker, string addonName)
    {
        try
        {
            var ptr = BackstabTheTrade.GameGui.GetAddonByName(addonName);
            if (ptr.Address == nint.Zero) return false;

            var addon = (AtkUnitBase*)ptr.Address;
            if (!addon->IsVisible) return false;

            var yesNode = addon->GetNodeById(8);
            if (yesNode == null)
                return false;

            var yesButton = yesNode->GetAsAtkComponentButton();
            if (yesButton == null)
                return false;

            var yesComponentNode = (AtkComponentNode*)yesNode;
            if (yesComponentNode == null)
                return false;

            if (!tracker.TryGetYesReplaySequence(out var pressReplay, out var releaseReplay, out var clickReplay))
            {
                pressReplay = CreateSyntheticReplay(23, 20);
                releaseReplay = CreateSyntheticReplay(24, 21);
                clickReplay = CreateSyntheticReplay(25, 22);
                BackstabTheTrade.Log.Debug($"{addonName}: no captured N8 replay context yet, using synthetic yes button sequence.");
            }

            return TryComponentNodeDispatch(
                addon,
                (AtkComponentBase*)yesButton,
                yesComponentNode,
                pressReplay,
                releaseReplay,
                clickReplay,
                useReplayData: true);
        }
        catch (Exception ex)
        {
            BackstabTheTrade.Log.Error(ex, $"DoSelectYesnoDispatch({addonName})");
            return false;
        }
    }

    private static bool DoSelectYesno(string addonName)
    {
        if (!IsAddonVisible(addonName))
            return false;

        return DoFireCallback(addonName, 0, 0, -1);
    }

    public void Dispose()
    {
        BackstabTheTrade.Framework.Update -= Tick;
        BackstabTheTrade.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "InputNumeric", OnInputNumericSetup);
        BackstabTheTrade.AddonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, "InputNumeric", OnInputNumericRequestedUpdate);
        BackstabTheTrade.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "InputNumeric", OnInputNumericFinalize);
        BackstabTheTrade.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesnoSetup);
        BackstabTheTrade.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesNo", OnSelectYesnoSetup);
    }

    private static unsafe bool TryComponentNodeDispatch(
        AtkUnitBase* addon,
        AtkComponentBase* componentBase,
        AtkComponentNode* okComponentNode,
        EventTracker.ReplayButtonEventContext pressReplay,
        EventTracker.ReplayButtonEventContext releaseReplay,
        EventTracker.ReplayButtonEventContext clickReplay,
        bool useReplayData)
    {
        addon->SetComponentFocusNode(componentBase);
        return DispatchReplaySequence(
            evt => okComponentNode->DispatchEvent(evt),
            pressReplay,
            releaseReplay,
            clickReplay,
            useReplayData);
    }

    private unsafe delegate bool DispatchEventInvoker(AtkEventDispatcher.Event* evt);

    private static unsafe bool DispatchReplaySequence(
        DispatchEventInvoker invoker,
        EventTracker.ReplayButtonEventContext pressReplay,
        EventTracker.ReplayButtonEventContext releaseReplay,
        EventTracker.ReplayButtonEventContext clickReplay,
        bool useReplayData)
    {
        var pressEvt = BuildDispatchEvent(pressReplay, useReplayData);
        var releaseEvt = BuildDispatchEvent(releaseReplay, useReplayData);
        var clickEvt = BuildDispatchEvent(clickReplay, useReplayData);

        bool handled = invoker(&pressEvt);
        handled |= invoker(&releaseEvt);
        handled |= invoker(&clickEvt);
        return handled;
    }

    private static unsafe AtkEventDispatcher.Event BuildDispatchEvent(
        EventTracker.ReplayButtonEventContext replay,
        bool useReplayData)
    {
        AtkEventDispatcher.Event evt = default;

        if (replay.HasAtkEvent)
        {
            evt.State = replay.AtkEvent.State;
            evt.ReturnFlags = replay.AtkEvent.State.ReturnFlags;
        }
        else
        {
            evt.State.EventType = (AtkEventType)replay.AtkEventType;
            evt.State.ReturnFlags = 0;
            evt.State.StateFlags = 0;
            evt.ReturnFlags = 0;
        }

        if (useReplayData && replay.HasAtkEventData)
            evt.EventData = replay.AtkEventData;

        return evt;
    }

    private static EventTracker.ReplayButtonEventContext CreateSyntheticReplay(int atkEventType, uint param)
    {
        return new EventTracker.ReplayButtonEventContext
        {
            Captured = true,
            AtkEventType = atkEventType,
            Param = param,
            HasAtkEvent = false,
            HasAtkEventData = false,
        };
    }
}

public readonly record struct TradeHistoryEntry(
    DateTime At,
    string Mode,
    string Result,
    string Summary,
    string Detail,
    int AttemptNumber,
    long GilDelta,
    TradeItemDelta[] ItemDeltas);

public readonly record struct TradeItemDelta(
    uint ItemId,
    string Name,
    long QuantityDelta);

