using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Events.EventDataTypes;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BackstabTheTrade;

public sealed class EventTracker : IDisposable
{
    private static readonly string[] InventoryTrackedAddons =
    {
        "Inventory",
        "InventoryLarge",
        "InventoryExpansion",
        "InventoryEvent",
        "InventoryContext",
        "ContextMenu",
        "ContextIconMenu",
        "ContextMenuSub",
        "SelectIconString",
        "SelectIconString2",
        "SelectString",
        "ActionMenu",
    };

    public readonly record struct SigResult(string Label, nint Address, bool Resolved);
    public unsafe struct ReplayButtonEventContext
    {
        public bool Captured;
        public int AtkEventType;
        public uint Param;
        public AtkEvent AtkEvent;
        public bool HasAtkEvent;
        public global::FFXIVClientStructs.FFXIV.Component.GUI.AtkEventData AtkEventData;
        public bool HasAtkEventData;
    }

    public bool IsTracking { get; private set; }
    public readonly List<string> Log = new();
    public IReadOnlyList<SigResult> SigResults { get; } = Array.Empty<SigResult>();
    private const int MaxLog = 800;
    private readonly List<IAddonEventHandle> _buttonEventHandles = new();
    private readonly List<IAddonEventHandle> _inventoryNodeEventHandles = new();
    private readonly List<IAddonEventHandle> _contextMenuNodeEventHandles = new();
    private readonly List<IAddonEventHandle> _dragDropEventHandles = new();
    private readonly HashSet<string> _seenInventoryAddons = new(StringComparer.Ordinal);
    private readonly List<RecentAddonEvent> _recentAddonEvents = new();
    private int _lastContextMenuTradeIndex = -1;
    private string _lastContextMenuLabelsSummary = string.Empty;
    private bool _inventoryTrackingRegistered;

    public bool InputNumericOpen { get; private set; }

    private ReplayButtonEventContext _okPressReplay;
    private ReplayButtonEventContext _okReleaseReplay;
    private ReplayButtonEventContext _okClickReplay;
    private ReplayButtonEventContext _yesPressReplay;
    private ReplayButtonEventContext _yesReleaseReplay;
    private ReplayButtonEventContext _yesClickReplay;
    private readonly Dictionary<uint, InventoryReplaySequence> _inventoryReplaySequences = new();

    private unsafe struct InventoryReplaySequence
    {
        public ReplayButtonEventContext Press;
        public ReplayButtonEventContext Release;
        public ReplayButtonEventContext Click;
    }

    private readonly record struct RecentAddonEvent(DateTime At, string Message);

    public EventTracker()
    {
        BackstabTheTrade.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Trade", OnTradeSetup);
        BackstabTheTrade.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "Trade", OnTradeFinalize);
        BackstabTheTrade.AddonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, "Trade", OnTradeEvent);
        BackstabTheTrade.AddonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, "Trade", OnTradeEvent);

        BackstabTheTrade.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "InputNumeric", OnInputSetup);
        BackstabTheTrade.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "InputNumeric", OnInputFinalize);
        BackstabTheTrade.AddonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, "InputNumeric", OnInputEvent);
        BackstabTheTrade.AddonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, "InputNumeric", OnInputEvent);
        BackstabTheTrade.AddonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, "InputNumeric", OnInputUpdate);
        BackstabTheTrade.AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "InputNumeric", OnInputUpdate);

        BackstabTheTrade.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesnoSetup);
        BackstabTheTrade.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "SelectYesno", OnSelectYesnoFinalize);
        BackstabTheTrade.AddonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, "SelectYesno", OnSelectYesnoEvent);
        BackstabTheTrade.AddonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, "SelectYesno", OnSelectYesnoEvent);

        BackstabTheTrade.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesNo", OnSelectYesNoSetup);
        BackstabTheTrade.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "SelectYesNo", OnSelectYesNoFinalize);
        BackstabTheTrade.AddonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, "SelectYesNo", OnSelectYesNoEvent);
        BackstabTheTrade.AddonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, "SelectYesNo", OnSelectYesNoEvent);

        BackstabTheTrade.Framework.Update += OnTick;
    }

    public void StartTracking()
    {
        IsTracking = true;
        RegisterInventoryTrackingListeners();
        Write("=== TRACKING STARTED ===");
        RefreshInventoryNodeEvents();
        RefreshDragDropNodeEvents();
    }
    public void StopTracking()
    {
        IsTracking = false;
        UnregisterDragDropNodeEvents();
        UnregisterContextMenuNodeEvents();
        UnregisterInventoryNodeEvents();
        UnregisterInventoryTrackingListeners();
        Write("=== TRACKING STOPPED ===");
    }
    public void ClearLog() { lock (Log) Log.Clear(); }
    public void LogExternal(string msg) => Write(msg);
    public void LogVerbose(string msg)
    {
        if (IsTracking)
            Write(msg);
    }

    public unsafe bool TryGetOkReplaySequence(
        out ReplayButtonEventContext press,
        out ReplayButtonEventContext release,
        out ReplayButtonEventContext click)
    {
        press = _okPressReplay;
        release = _okReleaseReplay;
        click = _okClickReplay;
        return press.Captured && release.Captured && click.Captured;
    }

    public unsafe bool TryGetYesReplaySequence(
        out ReplayButtonEventContext press,
        out ReplayButtonEventContext release,
        out ReplayButtonEventContext click)
    {
        press = _yesPressReplay;
        release = _yesReleaseReplay;
        click = _yesClickReplay;
        return press.Captured && release.Captured && click.Captured;
    }

    public unsafe bool TryGetInventoryReplaySequence(
        uint nodeId,
        out ReplayButtonEventContext press,
        out ReplayButtonEventContext release,
        out ReplayButtonEventContext click)
    {
        if (_inventoryReplaySequences.TryGetValue(nodeId, out var sequence))
        {
            press = sequence.Press;
            release = sequence.Release;
            click = sequence.Click;
            return press.Captured && release.Captured && click.Captured;
        }

        press = default;
        release = default;
        click = default;
        return false;
    }

    private DateTime _lastPoll = DateTime.MinValue;
    private unsafe void OnTick(IFramework fw)
    {
        var inPtr = BackstabTheTrade.GameGui.GetAddonByName("InputNumeric");
        InputNumericOpen = inPtr.Address != nint.Zero && ((AtkUnitBase*)inPtr.Address)->IsVisible;

        if (!IsTracking) return;
        ProbeVisibleInventoryAddons();
        if (DateTime.Now - _lastPoll < TimeSpan.FromMilliseconds(500)) return;
        _lastPoll = DateTime.Now;

        if (!InputNumericOpen) return;

        var addon = (AtkUnitBase*)inPtr.Address;
        var sb = new StringBuilder("[InputNumeric POLL] ");
        for (uint i = 1; i <= 6; i++)
        {
            var node = addon->GetNodeById(i);
            if (node == null) continue;
            sb.Append($"N{i}={node->Type}");
            if (node->Type == NodeType.Text)
            {
                var t = node->GetAsAtkTextNode();
                if (t != null)
                    try { sb.Append($"(\"{t->NodeText}\") "); } catch { }
            }
            if ((int)node->Type == 1003)
            {
                var n = node->GetAsAtkComponentNumericInput();
                if (n != null) sb.Append($"(val={n->Value}) ");
            }
        }
        Write(sb.ToString());
    }

    private void OnTradeSetup(AddonEvent t, AddonArgs a)
    {
        if (IsTracking)
        {
            Write("[Trade] OPENED");
            RefreshDragDropNodeEvents();
        }
    }

    private void OnTradeFinalize(AddonEvent t, AddonArgs a)
    {
        if (IsTracking)
        {
            RefreshDragDropNodeEvents();
            Write("[Trade] CLOSED");
        }
    }

    private void OnTradeEvent(AddonEvent t, AddonArgs a)
    {
        if (!IsTracking) return;
        if (a is not AddonReceiveEventArgs ev) return;
        var message = $"[Trade] {t}  Type={ev.AtkEventType}({(int)ev.AtkEventType})  Param={ev.EventParam}" +
                      (ev.AtkEvent != nint.Zero ? $"  AtkEvent.Param={ReadAtkEventParam(ev.AtkEvent)}" : "");
        Write(message);
        RememberAddonEvent(message);
    }

    private void OnInputSetup(AddonEvent t, AddonArgs a)
    {
        InputNumericOpen = true;
        if (IsTracking)
        {
            Write("[InputNumeric] OPENED");
            DumpNumericDetails();
        }
        RegisterInputButtonEvents();
    }

    private void OnInputFinalize(AddonEvent t, AddonArgs a)
    {
        InputNumericOpen = false;
        UnregisterInputButtonEvents();
        if (IsTracking)
            Write("[InputNumeric] CLOSED");
    }

    private unsafe void OnInputEvent(AddonEvent t, AddonArgs a)
    {
        if (!IsTracking) return;
        if (a is not AddonReceiveEventArgs ev) return;

        var sb = new StringBuilder($"[InputNumeric] {t}");
        sb.Append($"  Type={ev.AtkEventType}({(int)ev.AtkEventType})");
        sb.Append($"  Param={ev.EventParam}");

        if (ev.AtkEvent != nint.Zero)
        {
            sb.Append($"  AtkEvent.Param={ReadAtkEventParam(ev.AtkEvent)}");
        }

        var ptr = BackstabTheTrade.GameGui.GetAddonByName("InputNumeric");
        if (ptr.Address != nint.Zero)
            AppendInputState(sb, (AtkUnitBase*)ptr.Address);

        Write(sb.ToString());
    }

    private void OnSelectYesnoSetup(AddonEvent t, AddonArgs a)
    {
        RegisterSelectYesnoButtonEvents();
        if (IsTracking) Write("[SelectYesno] OPENED");
    }

    private void OnSelectYesnoFinalize(AddonEvent t, AddonArgs a)
    {
        UnregisterInputButtonEvents();
        if (IsTracking) Write("[SelectYesno] CLOSED");
    }
    private void OnSelectYesNoSetup(AddonEvent t, AddonArgs a) { if (IsTracking) Write("[SelectYesNo] OPENED"); }
    private void OnSelectYesNoFinalize(AddonEvent t, AddonArgs a) { if (IsTracking) Write("[SelectYesNo] CLOSED"); }

    private void OnSelectYesnoEvent(AddonEvent t, AddonArgs a) => LogYesNoEvent("SelectYesno", t, a);
    private void OnSelectYesNoEvent(AddonEvent t, AddonArgs a) => LogYesNoEvent("SelectYesNo", t, a);

    private void LogYesNoEvent(string addonName, AddonEvent t, AddonArgs a)
    {
        if (!IsTracking) return;
        if (a is not AddonReceiveEventArgs ev) return;
        Write($"[{addonName}] {t}  Type={ev.AtkEventType}({(int)ev.AtkEventType})  Param={ev.EventParam}" +
              (ev.AtkEvent != nint.Zero ? $"  AtkEvent.Param={ReadAtkEventParam(ev.AtkEvent)}" : ""));
    }

    private void OnInputUpdate(AddonEvent t, AddonArgs a)
    {
        if (!IsTracking) return;
        if (a is not AddonRequestedUpdateArgs upd) return;
        var sb = new StringBuilder($"[InputNumeric] {t}");
        AppendNumberArrayState(sb, upd.NumberArrayData);
        unsafe
        {
            var ptr = BackstabTheTrade.GameGui.GetAddonByName("InputNumeric");
            if (ptr.Address != nint.Zero)
                AppendInputState(sb, (AtkUnitBase*)ptr.Address);
        }
        Write(sb.ToString());
    }

    private void OnInventoryAddonSetup(AddonEvent t, AddonArgs a)
    {
        var addonName = TryGetAddonName(a, "InventoryAddon");
        MarkInventoryAddonSeen(addonName);
        RememberAddonEvent($"[{addonName}] PostSetup");
        if (IsTracking && IsContextPathAddon(addonName))
            RegisterContextMenuNodeEvents(addonName);
        if (IsTracking && IsDragDropTrackedAddon(addonName))
            RefreshDragDropNodeEvents();
    }

    private void OnInventoryAddonFinalize(AddonEvent t, AddonArgs a)
    {
        var addonName = TryGetAddonName(a, "InventoryAddon");
        _seenInventoryAddons.Remove(addonName);
        RememberAddonEvent($"[{addonName}] PreFinalize");
        if (IsContextPathAddon(addonName))
            UnregisterContextMenuNodeEvents();
        if (IsDragDropTrackedAddon(addonName))
            RefreshDragDropNodeEvents();
        if (IsTracking)
            Write($"[{addonName}] CLOSED");
    }

    private void OnInventoryAddonEvent(AddonEvent t, AddonArgs a)
    {
        if (!IsTracking) return;
        if (a is not AddonReceiveEventArgs ev) return;

        string addonName = TryGetAddonName(a, "InventoryAddon");
        var message = $"[{addonName}] {t}  Type={ev.AtkEventType}({(int)ev.AtkEventType})  Param={ev.EventParam}" +
                      (ev.AtkEvent != nint.Zero ? $"  AtkEvent.Param={ReadAtkEventParam(ev.AtkEvent)}" : "");
        Write(message);
        RememberAddonEvent(message);
    }

    private void OnInventoryAddonUpdate(AddonEvent t, AddonArgs a)
    {
        if (!IsTracking) return;
        if (a is not AddonRequestedUpdateArgs upd) return;

        string addonName = TryGetAddonName(a, "InventoryAddon");
        if (!IsContextPathAddon(addonName))
            return;

        unsafe
        {
            var ptr = BackstabTheTrade.GameGui.GetAddonByName(addonName);
            if (ptr.Address == nint.Zero)
                return;

            var addon = (AtkUnitBase*)ptr.Address;
            if (!addon->IsVisible)
                return;

            if (string.Equals(addonName, "ContextMenu", StringComparison.Ordinal))
                WriteContextMenuListSnapshot(addon, $"{addonName}:List");
        }
    }

    private static bool IsContextPathAddon(string addonName) =>
        string.Equals(addonName, "ContextMenu", StringComparison.Ordinal) ||
        string.Equals(addonName, "InventoryContext", StringComparison.Ordinal) ||
        string.Equals(addonName, "ContextIconMenu", StringComparison.Ordinal) ||
        string.Equals(addonName, "ContextMenuSub", StringComparison.Ordinal) ||
        string.Equals(addonName, "SelectIconString", StringComparison.Ordinal) ||
        string.Equals(addonName, "SelectIconString2", StringComparison.Ordinal) ||
        string.Equals(addonName, "SelectString", StringComparison.Ordinal) ||
        string.Equals(addonName, "ActionMenu", StringComparison.Ordinal);

    private static bool IsInventoryContextStateAddon(string addonName) =>
        string.Equals(addonName, "Inventory", StringComparison.Ordinal) ||
        string.Equals(addonName, "InventoryEvent", StringComparison.Ordinal) ||
        string.Equals(addonName, "InventoryContext", StringComparison.Ordinal) ||
        string.Equals(addonName, "ContextMenu", StringComparison.Ordinal) ||
        string.Equals(addonName, "ContextIconMenu", StringComparison.Ordinal) ||
        string.Equals(addonName, "SelectIconString", StringComparison.Ordinal) ||
        string.Equals(addonName, "SelectIconString2", StringComparison.Ordinal) ||
        string.Equals(addonName, "SelectString", StringComparison.Ordinal) ||
        string.Equals(addonName, "ActionMenu", StringComparison.Ordinal) ||
        string.Equals(addonName, "ItemDetail", StringComparison.Ordinal) ||
        string.Equals(addonName, "ItemDetailCompare", StringComparison.Ordinal);

    private static bool IsDragDropTrackedAddon(string addonName) =>
        string.Equals(addonName, "Inventory", StringComparison.Ordinal) ||
        string.Equals(addonName, "InventoryLarge", StringComparison.Ordinal) ||
        string.Equals(addonName, "InventoryExpansion", StringComparison.Ordinal) ||
        string.Equals(addonName, "Trade", StringComparison.Ordinal);

    private unsafe void DumpAllNodes(string addonName, uint maxNodeId = 10)
    {
        try
        {
            var ptr = BackstabTheTrade.GameGui.GetAddonByName(addonName);
            if (ptr.Address == nint.Zero) return;
            var addon = (AtkUnitBase*)ptr.Address;
            Write($"  [{addonName}] nodeCount={addon->UldManager.NodeListCount}");
            for (uint i = 1; i <= maxNodeId; i++)
            {
                var node = addon->GetNodeById(i);
                if (node == null) continue;
                var line = $"    N{i} Type={node->Type}({(int)node->Type})";
                if (node->Type == NodeType.Text)
                {
                    var tx = node->GetAsAtkTextNode();
                    if (tx != null) try { line += $" Text=\"{tx->NodeText}\""; } catch { }
                }
                if ((int)node->Type == 1003)
                {
                    var n = node->GetAsAtkComponentNumericInput();
                    if (n != null) line += $" NumericVal={n->Value}";
                }
                Write(line);
            }
        }
        catch (Exception ex)
        {
            Write($"  DumpNodes error: {ex.Message}");
        }
    }

    private unsafe void DumpAddonSnapshot(string addonName, string header, uint maxNodeId)
    {
        try
        {
            var ptr = BackstabTheTrade.GameGui.GetAddonByName(addonName);
            if (ptr.Address == nint.Zero)
                return;

            var addon = (AtkUnitBase*)ptr.Address;
            if (!addon->IsVisible)
                return;

            Write(header);
            WriteAtkValueSnapshot(addonName, addon);
            DumpAllNodes(addonName, maxNodeId);
        }
        catch (Exception ex)
        {
            Write($"{header} failed: {ex.Message}");
        }
    }

    private void RememberAddonEvent(string message)
    {
        if (!IsTracking) return;

        var now = DateTime.UtcNow;
        _recentAddonEvents.Add(new RecentAddonEvent(now, message));
        _recentAddonEvents.RemoveAll(x => (now - x.At) > TimeSpan.FromSeconds(4));
    }

    private void DumpRecentAddonPath(TimeSpan lookback)
    {
        if (!IsTracking) return;

        var cutoff = DateTime.UtcNow - lookback;
        var recent = _recentAddonEvents.Where(x => x.At >= cutoff).ToList();
        if (recent.Count == 0)
        {
            Write($"[MenuPath] No tracked addon events in the last {lookback.TotalMilliseconds:0}ms");
            return;
        }

        Write($"[MenuPath] Recent addon path ({lookback.TotalMilliseconds:0}ms)");
        foreach (var entry in recent)
            Write($"[MenuPath] {entry.At:HH:mm:ss.fff} {entry.Message}");
    }

    private void DumpVisibleMenuPathAddons()
    {
        if (!IsTracking) return;

        bool any = false;
        foreach (var addonName in InventoryTrackedAddons)
        {
            if (!IsContextPathAddon(addonName) &&
                !string.Equals(addonName, "ItemDetail", StringComparison.Ordinal) &&
                !string.Equals(addonName, "ItemDetailCompare", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var ptr = BackstabTheTrade.GameGui.GetAddonByName(addonName);
                if (ptr.Address == nint.Zero)
                    continue;

                unsafe
                {
                    var addon = (AtkUnitBase*)ptr.Address;
                    if (!addon->IsVisible)
                        continue;
                }

                any = true;
                DumpAddonSnapshot(addonName, $"[{addonName}] MENU-PATH SNAPSHOT", 80);
            }
            catch (Exception ex)
            {
                Write($"[{addonName}] MENU-PATH SNAPSHOT failed: {ex.Message}");
            }
        }

        if (!any)
            Write("[MenuPath] No visible menu-path addons at InputNumeric open");
    }

    private void DumpInventoryContextActionState(string reason)
    {
        if (!IsTracking) return;

        Write($"[InventoryContextAction] trigger={reason}");
        foreach (var addonName in InventoryTrackedAddons)
        {
            if (!IsInventoryContextStateAddon(addonName))
                continue;

            try
            {
                var ptr = BackstabTheTrade.GameGui.GetAddonByName(addonName);
                if (ptr.Address == nint.Zero)
                    continue;

                unsafe
                {
                    var addon = (AtkUnitBase*)ptr.Address;
                    if (!addon->IsVisible)
                        continue;

                    Write($"[InventoryContextAction] {addonName} visible");
                    WriteAtkValueSnapshot($"InventoryContextAction:{addonName}", addon);

                    if (string.Equals(addonName, "Inventory", StringComparison.Ordinal))
                    {
                        DumpInventoryFocusSnapshot(addon, "InventoryContextAction");
                        DumpInventoryCollisionSnapshot(addon, "InventoryContextAction");
                    }
                }

                DumpAllNodes(addonName, 20);
            }
            catch (Exception ex)
            {
                Write($"[InventoryContextAction] {addonName} dump failed: {ex.Message}");
            }
        }
    }

    private unsafe void DumpNumericDetails()
    {
        try
        {
            var ptr = BackstabTheTrade.GameGui.GetAddonByName("InputNumeric");
            if (ptr.Address == nint.Zero) return;
            var addon = (AtkUnitBase*)ptr.Address;
            for (uint nid = 4; nid <= 5; nid++)
            {
                var node = addon->GetNodeById(nid);
                if (node == null) continue;
                if (node->Type == NodeType.Component)
                {
                    var btn = node->GetAsAtkComponentButton();
                    if (btn != null)
                        Write($"  N{nid} ButtonFlags=0x{btn->Flags:X}");
                }
            }
        }
        catch { }
    }

    private static unsafe void AppendButtonState(StringBuilder sb, AtkUnitBase* addon, uint nodeId, string label)
    {
        try
        {
            var node = addon->GetNodeById(nodeId);
            if (node == null) return;
            sb.Append($"  {label}Node={nodeId}");
            sb.Append($" Visible={node->IsVisible()}");
            if (node->Type != NodeType.Component) return;
            var btn = node->GetAsAtkComponentButton();
            if (btn == null) return;
            sb.Append($" BtnFlags=0x{btn->Flags:X}");
        }
        catch { }
    }

    private static unsafe void AppendInputState(StringBuilder sb, AtkUnitBase* addon)
    {
        var node = addon->GetNodeById(3);
        if (node != null)
        {
            var n = node->GetAsAtkComponentNumericInput();
            if (n != null) sb.Append($"  NumVal={n->Value}");
        }

        AppendButtonState(sb, addon, 4, "OK");
        AppendButtonState(sb, addon, 5, "Cancel");
        AppendAtkValueState(sb, addon);
    }

    private static unsafe void AppendAtkValueState(StringBuilder sb, AtkUnitBase* addon)
    {
        try
        {
            if (addon->AtkValues == null || addon->AtkValuesCount <= 0) return;

            int count = Math.Min((int)addon->AtkValuesCount, 10);
            sb.Append("  AtkValues=[");
            for (int i = 0; i < count; i++)
            {
                var value = addon->AtkValues[i];
                sb.Append($"{i}:{(int)value.Type}=");
                switch (value.Type)
                {
                    case (AtkValueType)3:
                    case (AtkValueType)5:
                    case (AtkValueType)7:
                        sb.Append(value.Int);
                        break;
                    default:
                        sb.Append("?");
                        break;
                }
                sb.Append(',');
            }
            sb.Append(']');
        }
        catch
        {
            sb.Append("  AtkValues=<err>");
        }
    }

    private static void AppendNumberArrayState(StringBuilder sb, nint numberArrayPtr)
    {
        if (numberArrayPtr == nint.Zero)
        {
            sb.Append(" NumberArrayData=null");
            return;
        }

        sb.Append(" NumberArrayData=<present>");
    }

    private unsafe void RegisterInputButtonEvents()
    {
        UnregisterInputButtonEvents();

        try
        {
            var ptr = BackstabTheTrade.GameGui.GetAddonByName("InputNumeric");
            if (ptr.Address == nint.Zero) return;

            var addon = (AtkUnitBase*)ptr.Address;
            RegisterNodeEvents(addon, 4, "OK");
            RegisterNodeEvents(addon, 5, "Cancel");
        }
        catch (Exception ex)
        {
            Write($"[InputNumeric] register button events failed: {ex.Message}");
        }
    }

    private unsafe void RegisterSelectYesnoButtonEvents()
    {
        UnregisterInputButtonEvents();

        try
        {
            var ptr = BackstabTheTrade.GameGui.GetAddonByName("SelectYesno");
            if (ptr.Address == nint.Zero) return;

            var addon = (AtkUnitBase*)ptr.Address;
            for (uint nodeId = 3; nodeId <= 10; nodeId++)
            {
                var node = addon->GetNodeById(nodeId);
                if (node == null) continue;
                RegisterNodeEvents(addon, nodeId, $"SelectYesno:N{nodeId}");
            }
        }
        catch (Exception ex)
        {
            Write($"[SelectYesno] register button events failed: {ex.Message}");
        }
    }

    private unsafe void RegisterInventoryNodeEvents(string addonName)
    {
        try
        {
            var ptr = BackstabTheTrade.GameGui.GetAddonByName(addonName);
            if (ptr.Address == nint.Zero) return;

            var addon = (AtkUnitBase*)ptr.Address;
            uint maxNodeId = Math.Min(40u, addon->UldManager.NodeListCount);
            for (uint nodeId = 1; nodeId <= maxNodeId; nodeId++)
            {
                var node = addon->GetNodeById(nodeId);
                if (node == null) continue;
                RegisterInventoryNode(addonName, addon, nodeId);
            }
        }
        catch (Exception ex)
        {
            Write($"[{addonName}] register inventory node events failed: {ex.Message}");
        }
    }

    private unsafe void RegisterContextMenuNodeEvents(string addonName)
    {
        UnregisterContextMenuNodeEvents();

        try
        {
            var ptr = BackstabTheTrade.GameGui.GetAddonByName(addonName);
            if (ptr.Address == nint.Zero) return;

            var addon = (AtkUnitBase*)ptr.Address;
            uint maxNodeId = Math.Min(80u, addon->UldManager.NodeListCount);
            for (uint nodeId = 1; nodeId <= maxNodeId; nodeId++)
            {
                var node = addon->GetNodeById(nodeId);
                if (node == null) continue;
                RegisterContextMenuNode(addonName, addon, nodeId);
            }
        }
        catch (Exception ex)
        {
            Write($"[{addonName}] register context menu node events failed: {ex.Message}");
        }
    }

    private unsafe void RegisterDragDropNodeEvents(string addonName)
    {
        try
        {
            var ptr = BackstabTheTrade.GameGui.GetAddonByName(addonName);
            if (ptr.Address == nint.Zero)
                return;

            var addon = (AtkUnitBase*)ptr.Address;
            uint maxNodeId = Math.Min(80u, addon->UldManager.NodeListCount);
            for (uint nodeId = 1; nodeId <= maxNodeId; nodeId++)
            {
                var node = addon->GetNodeById(nodeId);
                if (node == null)
                    continue;

                RegisterDragDropNode(addonName, addon, nodeId);
            }
        }
        catch (Exception ex)
        {
            Write($"[{addonName}] register drag-drop node events failed: {ex.Message}");
        }
    }

    private void RefreshDragDropNodeEvents()
    {
        UnregisterDragDropNodeEvents();

        foreach (var addonName in InventoryTrackedAddons)
        {
            if (!IsDragDropTrackedAddon(addonName))
                continue;

            RegisterDragDropNodeEvents(addonName);
        }

        RegisterDragDropNodeEvents("Trade");
    }

    private void RefreshInventoryNodeEvents()
    {
        UnregisterInventoryNodeEvents();

        foreach (var addonName in InventoryTrackedAddons)
            MarkInventoryAddonSeen(addonName);

        if (IsTracking)
            Write("[Inventory] refreshed visible node hooks.");
    }

    private void ProbeVisibleInventoryAddons()
    {
        foreach (var addonName in InventoryTrackedAddons)
            MarkInventoryAddonSeen(addonName);
    }

    private void DumpAggressiveInventorySources(string reason)
    {
        if (!IsTracking) return;

        Write($"[InventorySource] trigger={reason}");
        foreach (var addonName in InventoryTrackedAddons)
        {
            try
            {
                var ptr = BackstabTheTrade.GameGui.GetAddonByName(addonName);
                if (ptr.Address == nint.Zero)
                    continue;

                unsafe
                {
                    var addon = (AtkUnitBase*)ptr.Address;
                    if (!addon->IsVisible)
                        continue;

                    Write($"[InventorySource] {addonName} visible");
                    WriteAtkValueSnapshot($"InventorySource:{addonName}", addon);
                    if (string.Equals(addonName, "Inventory", StringComparison.Ordinal))
                    {
                        DumpInventoryFocusSnapshot(addon, "InventoryFocus");
                        DumpInventoryCollisionSnapshot(addon, "InventoryFocus");
                        DumpInventoryTreeSnapshot(addon, "InventoryFocus");
                    }
                    DumpAllNodes(addonName, 40);
                }
            }
            catch (Exception ex)
            {
                Write($"[InventorySource] {addonName} dump failed: {ex.Message}");
            }
        }
    }

    private unsafe void MarkInventoryAddonSeen(string addonName)
    {
        try
        {
            var ptr = BackstabTheTrade.GameGui.GetAddonByName(addonName);
            if (ptr.Address == nint.Zero) return;

            var addon = (AtkUnitBase*)ptr.Address;
            if (!addon->IsVisible) return;

            if (_seenInventoryAddons.Add(addonName))
            {
                RegisterInventoryNodeEvents(addonName);
                if (IsTracking)
                {
                    Write($"[{addonName}] VISIBLE");
                }
            }
        }
        catch
        {
        }
    }

    private unsafe void RegisterInventoryNode(string addonName, AtkUnitBase* addon, uint nodeId)
    {
        var node = addon->GetNodeById(nodeId);
        if (node == null) return;

        AddTrackedInventoryNodeEvent(addon, node, AddonEventType.MouseClick, $"{addonName}:N{nodeId}");
        AddTrackedInventoryNodeEvent(addon, node, AddonEventType.ButtonClick, $"{addonName}:N{nodeId}");
        AddTrackedInventoryNodeEvent(addon, node, AddonEventType.ButtonPress, $"{addonName}:N{nodeId}");
        AddTrackedInventoryNodeEvent(addon, node, AddonEventType.ButtonRelease, $"{addonName}:N{nodeId}");
    }

    private unsafe void RegisterContextMenuNode(string addonName, AtkUnitBase* addon, uint nodeId)
    {
        var node = addon->GetNodeById(nodeId);
        if (node == null) return;

        AddTrackedContextMenuNodeEvent(addon, node, AddonEventType.MouseClick, $"{addonName}:N{nodeId}");
        AddTrackedContextMenuNodeEvent(addon, node, AddonEventType.ButtonClick, $"{addonName}:N{nodeId}");
        AddTrackedContextMenuNodeEvent(addon, node, AddonEventType.ButtonPress, $"{addonName}:N{nodeId}");
        AddTrackedContextMenuNodeEvent(addon, node, AddonEventType.ButtonRelease, $"{addonName}:N{nodeId}");
    }

    private unsafe void RegisterDragDropNode(string addonName, AtkUnitBase* addon, uint nodeId)
    {
        var node = addon->GetNodeById(nodeId);
        if (node == null)
            return;

        var label = $"DragPath:{addonName}:N{nodeId}";
        AddTrackedDragDropNodeEvent(addon, node, AddonEventType.DragDropBegin, label);
        AddTrackedDragDropNodeEvent(addon, node, AddonEventType.DragDropEnd, label);
        AddTrackedDragDropNodeEvent(addon, node, AddonEventType.DragDropInsertAttempt, label);
        AddTrackedDragDropNodeEvent(addon, node, AddonEventType.DragDropInsert, label);
        AddTrackedDragDropNodeEvent(addon, node, AddonEventType.DragDropCanAcceptCheck, label);
        AddTrackedDragDropNodeEvent(addon, node, AddonEventType.DragDropRollOver, label);
        AddTrackedDragDropNodeEvent(addon, node, AddonEventType.DragDropRollOut, label);
        AddTrackedDragDropNodeEvent(addon, node, AddonEventType.DragDropClick, label);
    }

    private unsafe void AddTrackedInventoryNodeEvent(AtkUnitBase* addon, AtkResNode* node, AddonEventType eventType, string label)
    {
        var handle = BackstabTheTrade.AddonEventManager.AddEvent(
            (nint)addon,
            (nint)node,
            eventType,
            (atkEventType, data) => OnTrackedInventoryNodeEvent(label, atkEventType, data));

        if (handle != null)
            _inventoryNodeEventHandles.Add(handle);
    }

    private unsafe void AddTrackedContextMenuNodeEvent(AtkUnitBase* addon, AtkResNode* node, AddonEventType eventType, string label)
    {
        var handle = BackstabTheTrade.AddonEventManager.AddEvent(
            (nint)addon,
            (nint)node,
            eventType,
            (atkEventType, data) => OnTrackedContextMenuNodeEvent(label, atkEventType, data));

        if (handle != null)
            _contextMenuNodeEventHandles.Add(handle);
    }

    private unsafe void AddTrackedDragDropNodeEvent(AtkUnitBase* addon, AtkResNode* node, AddonEventType eventType, string label)
    {
        var handle = BackstabTheTrade.AddonEventManager.AddEvent(
            (nint)addon,
            (nint)node,
            eventType,
            (atkEventType, data) => OnTrackedDragDropNodeEvent(label, atkEventType, data));

        if (handle != null)
            _dragDropEventHandles.Add(handle);
    }

    private unsafe void RegisterNodeEvents(AtkUnitBase* addon, uint nodeId, string label)
    {
        var node = addon->GetNodeById(nodeId);
        if (node == null) return;

        AddTrackedNodeEvent(addon, node, AddonEventType.ButtonPress, label);
        AddTrackedNodeEvent(addon, node, AddonEventType.ButtonRelease, label);
        AddTrackedNodeEvent(addon, node, AddonEventType.ButtonClick, label);
        AddTrackedNodeEvent(addon, node, AddonEventType.MouseClick, label);
    }

    private unsafe void AddTrackedNodeEvent(AtkUnitBase* addon, AtkResNode* node, AddonEventType eventType, string label)
    {
        var handle = BackstabTheTrade.AddonEventManager.AddEvent(
            (nint)addon,
            (nint)node,
            eventType,
            (atkEventType, data) => OnTrackedButtonEvent(label, atkEventType, data));

        if (handle != null)
            _buttonEventHandles.Add(handle);
    }

    private unsafe void OnTrackedInventoryNodeEvent(string label, AddonEventType addonEventType, AddonEventData data)
    {
        if (TryParseInventoryNodeId(label, out var inventoryNodeId))
            CaptureInventoryReplayContext(inventoryNodeId, addonEventType, data);

        if (!IsTracking) return;

        var sb = new StringBuilder($"[{label}] {addonEventType}");
        sb.Append($"  AtkEventType={(int)data.AtkEventType}");
        sb.Append($"  Param={data.Param}");
        if (data.NodeTargetPointer != nint.Zero)
        {
            try
            {
                var node = (AtkResNode*)data.NodeTargetPointer;
                sb.Append($"  NodeId={node->NodeId}");
                sb.Append($"  NodeType={(int)node->Type}");
            }
            catch { }
        }

        Write(sb.ToString());

        if (label.StartsWith("Inventory:", StringComparison.Ordinal) &&
            addonEventType == AddonEventType.ButtonClick &&
            TryParseInventoryNodeId(label, out var parsedNodeId) &&
            !IsInventoryTabNodeId(parsedNodeId))
            DumpInventoryMappingSnapshot(label, data);
    }

    private unsafe void OnTrackedContextMenuNodeEvent(string label, AddonEventType addonEventType, AddonEventData data)
    {
        if (!IsTracking) return;

        var addonName = label.Split(':')[0];
        try
        {
            var ptr = BackstabTheTrade.GameGui.GetAddonByName(addonName);
            if (ptr.Address != nint.Zero)
            {
                var addon = (AtkUnitBase*)ptr.Address;
                if (string.Equals(addonName, "ContextMenu", StringComparison.Ordinal))
                    WriteContextMenuListSnapshot(addon, $"{addonName}:List");
            }
        }
        catch
        {
        }
    }

    private unsafe void OnTrackedDragDropNodeEvent(string label, AddonEventType addonEventType, AddonEventData data)
    {
        // Drag/drop rollover events are very noisy and are not needed for the
        // agent-context based manual item flow.
    }

    private static bool IsInventoryTabNodeId(uint nodeId) => nodeId is >= 8 and <= 11;

    private static bool TryParseInventoryNodeId(string label, out uint nodeId)
    {
        nodeId = 0;
        const string prefix = "Inventory:N";
        if (!label.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        return uint.TryParse(label.AsSpan(prefix.Length), out nodeId);
    }

    private unsafe void DumpInventoryMappingSnapshot(string label, AddonEventData data)
    {
        try
        {
            var ptr = BackstabTheTrade.GameGui.GetAddonByName("Inventory");
            if (ptr.Address == nint.Zero)
                return;

            var addon = (AtkUnitBase*)ptr.Address;
            Write($"[InventoryMap] source={label} param={data.Param} atkType={(int)data.AtkEventType}");
            WriteAtkValueSnapshot("InventoryMap", addon);
            DumpInventoryFocusSnapshot(addon, "InventoryGrid");
            DumpInventoryCollisionSnapshot(addon, "InventoryGrid");
            DumpInventoryTreeSnapshot(addon, "InventoryGrid");

            uint maxNodeId = Math.Min(40u, addon->UldManager.NodeListCount);
            for (uint nodeId = 1; nodeId <= maxNodeId; nodeId++)
            {
                var node = addon->GetNodeById(nodeId);
                if (node == null || !node->IsVisible()) continue;

                var line = new StringBuilder($"[InventoryMap] N{nodeId} Type={(int)node->Type}");
                if (node->Type == NodeType.Text)
                {
                    var tx = node->GetAsAtkTextNode();
                    if (tx != null)
                    {
                        try { line.Append($" Text=\"{tx->NodeText}\""); } catch { }
                    }
                }

                if (node->Type == NodeType.Component)
                {
                    var btn = node->GetAsAtkComponentButton();
                    if (btn != null)
                        line.Append($" BtnFlags=0x{btn->Flags:X}");
                }

                Write(line.ToString());
            }
        }
        catch (Exception ex)
        {
            Write($"[InventoryMap] snapshot failed: {ex.Message}");
        }
    }

    private unsafe void DumpInventoryFocusSnapshot(AtkUnitBase* addon, string prefix)
    {
        Write($"[{prefix}] CursorTarget={DescribeInventoryNode(addon->CursorTarget)}");
        Write($"[{prefix}] FocusNode={DescribeInventoryNode(addon->FocusNode)}");
        Write($"[{prefix}] ComponentFocusNode={DescribeInventoryNode(addon->ComponentFocusNode)}");
    }

    private unsafe void DumpInventoryCollisionSnapshot(AtkUnitBase* addon, string prefix)
    {
        try
        {
            int count = Math.Min((int)addon->CollisionNodeListCount, 24);
            Write($"[{prefix}] CollisionNodeListCount={addon->CollisionNodeListCount}");
            for (int i = 0; i < count; i++)
            {
                var node = addon->CollisionNodeList[i];
                if (node == null)
                    continue;

                Write($"[{prefix}] Collision[{i}]={DescribeInventoryNode(node)}");
            }
        }
        catch (Exception ex)
        {
            Write($"[{prefix}] collision snapshot failed: {ex.Message}");
        }
    }

    private unsafe void DumpInventoryTreeSnapshot(AtkUnitBase* addon, string prefix)
    {
        try
        {
            int budget = 80;
            Write($"[{prefix}] RootTree:");
            DumpNodeTreeRecursive(addon->RootNode, 0, ref budget, prefix);
        }
        catch (Exception ex)
        {
            Write($"[{prefix}] tree snapshot failed: {ex.Message}");
        }
    }

    private unsafe void DumpAddonTreeSnapshot(AtkUnitBase* addon, string prefix, int budget)
    {
        try
        {
            Write($"[{prefix}] RootTree:");
            DumpNodeTreeRecursive(addon->RootNode, 0, ref budget, prefix);
        }
        catch (Exception ex)
        {
            Write($"[{prefix}] tree snapshot failed: {ex.Message}");
        }
    }

    private unsafe void DumpNodeTreeRecursive(AtkResNode* node, int depth, ref int budget, string prefix)
    {
        if (node == null || budget <= 0 || depth > 4)
            return;

        budget--;
        var indent = new string(' ', depth * 2);
        Write($"[{prefix}] {indent}{DescribeInventoryNode(node)}");

        DumpNodeTreeRecursive(node->ChildNode, depth + 1, ref budget, prefix);
        DumpNodeTreeRecursive(node->NextSiblingNode, depth, ref budget, prefix);
    }

    private static unsafe string DescribeInventoryNode(AtkResNode* node)
    {
        if (node == null)
            return "<null>";

        var sb = new StringBuilder();
        sb.Append($"Node Id={node->NodeId}");
        sb.Append($" Type={(int)node->Type}");
        sb.Append($" Visible={node->IsVisible()}");

        if (node->Type == NodeType.Text)
        {
            var tx = node->GetAsAtkTextNode();
            if (tx != null)
            {
                try { sb.Append($" Text=\"{tx->NodeText}\""); } catch { }
            }
        }

        return sb.ToString();
    }

    private unsafe void WriteAtkValueSnapshot(string prefix, AtkUnitBase* addon)
    {
        try
        {
            if (addon->AtkValues == null || addon->AtkValuesCount <= 0)
                return;

            var sb = new StringBuilder($"[{prefix}] AtkValues=[");
            int count = Math.Min((int)addon->AtkValuesCount, 20);
            for (int i = 0; i < count; i++)
            {
                var value = addon->AtkValues[i];
                sb.Append($"{i}:{(int)value.Type}=");
                switch (value.Type)
                {
                    case (AtkValueType)3:
                    case (AtkValueType)5:
                    case (AtkValueType)7:
                        sb.Append(value.Int);
                        break;
                    default:
                        sb.Append('?');
                        break;
                }

                sb.Append(',');
            }

            sb.Append(']');
            Write(sb.ToString());
        }
        catch
        {
        }
    }

    private unsafe void WriteContextMenuListSnapshot(AtkUnitBase* addon, string prefix)
    {
        try
        {
            var listNode = addon->GetNodeById(2);
            if (listNode == null)
            {
                Write($"[{prefix}] node2=<null>");
                return;
            }

            var list = listNode->GetAsAtkComponentList();
            if (list == null)
            {
                Write($"[{prefix}] node2Type={(int)listNode->Type}, component=<null>");
                return;
            }

            Write($"[{prefix}] ListLength={list->ListLength} FirstVisible={list->FirstVisibleItemIndex} Selected={list->SelectedItemIndex} Hovered={list->HoveredItemIndex} Hovered2={list->HoveredItemIndex2} Hovered3={list->HoveredItemIndex3} Held={list->HeldItemIndex} DragDrop={list->DragDropItemIndex} VisibleRows={list->VisibleRowCount} VisibleItems={list->NumVisibleItems}");

            var count = Math.Min(list->ListLength, 20);
            for (var i = 0; i < count; i++)
            {
                string label = TryReadContextMenuItemLabel(list, i);
                bool visible = false;
                try { visible = list->IsItemVisible(i, true); } catch { }
                Write($"[{prefix}] Item[{i}] Visible={visible} Label=\"{label}\"");
            }

            CacheContextMenuTradeInfo(list, count);
        }
        catch (Exception ex)
        {
            Write($"[{prefix}] snapshot failed: {ex.Message}");
        }
    }

    private unsafe void CacheContextMenuTradeInfo(AtkComponentList* list, int count)
    {
        try
        {
            var labels = new List<string>();
            var tradeIndex = -1;
            for (var i = 0; i < count; i++)
            {
                var label = TryReadContextMenuItemLabel(list, i);
                labels.Add($"{i}:{label}");
                if (tradeIndex < 0 && ClientTextMap.IsTradeContextMenuLabel(label))
                    tradeIndex = i;
            }

            _lastContextMenuTradeIndex = tradeIndex;
            _lastContextMenuLabelsSummary = string.Join(" | ", labels);
        }
        catch
        {
        }
    }

    private static unsafe string TryReadContextMenuItemLabel(AtkComponentList* list, int index)
    {
        try
        {
            var ptr = list->GetItemLabel(index);
            return ptr.ToString() ?? string.Empty;
        }
        catch
        {
            return "<err>";
        }
    }

    private unsafe void OnTrackedButtonEvent(string label, AddonEventType addonEventType, AddonEventData data)
    {
        if (label == "OK")
            CaptureOkReplayContext(addonEventType, data);
        else if (label == "SelectYesno:N8")
            CaptureYesReplayContext(addonEventType, data);

        if (!IsTracking) return;

        var prefix = label.StartsWith("SelectYesno:", StringComparison.Ordinal)
            ? $"[{label}]"
            : $"[InputNumeric:{label}]";

        var sb = new StringBuilder($"{prefix} {addonEventType}");
        sb.Append($"  AtkEventType={(int)data.AtkEventType}");
        sb.Append($"  Param={data.Param}");
        if (data.NodeTargetPointer != nint.Zero)
        {
            try
            {
                var node = (AtkResNode*)data.NodeTargetPointer;
                sb.Append($"  NodeId={node->NodeId}");
                sb.Append($"  NodeType={(int)node->Type}");
            }
            catch { }
        }

        if (!label.StartsWith("SelectYesno:", StringComparison.Ordinal))
        {
            var ptr = BackstabTheTrade.GameGui.GetAddonByName("InputNumeric");
            if (ptr.Address != nint.Zero)
                AppendInputState(sb, (AtkUnitBase*)ptr.Address);
        }

        Write(sb.ToString());
    }

    private unsafe void CaptureOkReplayContext(AddonEventType addonEventType, AddonEventData data)
    {
        ReplayButtonEventContext ctx = default;
        ctx.Captured = true;
        ctx.AtkEventType = (int)data.AtkEventType;
        ctx.Param = data.Param;

        if (data.AtkEventPointer != nint.Zero)
        {
            try
            {
                ctx.AtkEvent = *(AtkEvent*)data.AtkEventPointer;
                ctx.HasAtkEvent = true;
            }
            catch { }
        }

        if (data.AtkEventDataPointer != nint.Zero)
        {
            try
            {
                ctx.AtkEventData = *(global::FFXIVClientStructs.FFXIV.Component.GUI.AtkEventData*)data.AtkEventDataPointer;
                ctx.HasAtkEventData = true;
            }
            catch { }
        }

        switch (addonEventType)
        {
            case AddonEventType.ButtonPress:
                _okPressReplay = ctx;
                break;
            case AddonEventType.ButtonRelease:
                _okReleaseReplay = ctx;
                break;
            case AddonEventType.ButtonClick:
                _okClickReplay = ctx;
                break;
        }
    }

    private unsafe void CaptureYesReplayContext(AddonEventType addonEventType, AddonEventData data)
    {
        ReplayButtonEventContext ctx = default;
        ctx.Captured = true;
        ctx.AtkEventType = (int)data.AtkEventType;
        ctx.Param = data.Param;

        if (data.AtkEventPointer != nint.Zero)
        {
            try
            {
                ctx.AtkEvent = *(AtkEvent*)data.AtkEventPointer;
                ctx.HasAtkEvent = true;
            }
            catch { }
        }

        if (data.AtkEventDataPointer != nint.Zero)
        {
            try
            {
                ctx.AtkEventData = *(global::FFXIVClientStructs.FFXIV.Component.GUI.AtkEventData*)data.AtkEventDataPointer;
                ctx.HasAtkEventData = true;
            }
            catch { }
        }

        switch (addonEventType)
        {
            case AddonEventType.ButtonPress:
                _yesPressReplay = ctx;
                break;
            case AddonEventType.ButtonRelease:
                _yesReleaseReplay = ctx;
                break;
            case AddonEventType.ButtonClick:
                _yesClickReplay = ctx;
                break;
        }
    }

    private unsafe void CaptureInventoryReplayContext(uint nodeId, AddonEventType addonEventType, AddonEventData data)
    {
        ReplayButtonEventContext ctx = default;
        ctx.Captured = true;
        ctx.AtkEventType = (int)data.AtkEventType;
        ctx.Param = data.Param;

        if (data.AtkEventPointer != nint.Zero)
        {
            try
            {
                ctx.AtkEvent = *(AtkEvent*)data.AtkEventPointer;
                ctx.HasAtkEvent = true;
            }
            catch { }
        }

        if (data.AtkEventDataPointer != nint.Zero)
        {
            try
            {
                ctx.AtkEventData = *(global::FFXIVClientStructs.FFXIV.Component.GUI.AtkEventData*)data.AtkEventDataPointer;
                ctx.HasAtkEventData = true;
            }
            catch { }
        }

        _inventoryReplaySequences.TryGetValue(nodeId, out var sequence);
        switch (addonEventType)
        {
            case AddonEventType.ButtonPress:
                sequence.Press = ctx;
                break;
            case AddonEventType.ButtonRelease:
                sequence.Release = ctx;
                break;
            case AddonEventType.ButtonClick:
                sequence.Click = ctx;
                break;
            default:
                return;
        }

        _inventoryReplaySequences[nodeId] = sequence;
    }

    private void UnregisterInputButtonEvents()
    {
        foreach (var handle in _buttonEventHandles)
            BackstabTheTrade.AddonEventManager.RemoveEvent(handle);

        _buttonEventHandles.Clear();
    }

    private void UnregisterContextMenuNodeEvents()
    {
        foreach (var handle in _contextMenuNodeEventHandles)
            BackstabTheTrade.AddonEventManager.RemoveEvent(handle);

        _contextMenuNodeEventHandles.Clear();
    }

    private void UnregisterDragDropNodeEvents()
    {
        foreach (var handle in _dragDropEventHandles)
            BackstabTheTrade.AddonEventManager.RemoveEvent(handle);

        _dragDropEventHandles.Clear();
    }

    private void UnregisterInventoryNodeEvents()
    {
        foreach (var handle in _inventoryNodeEventHandles)
            BackstabTheTrade.AddonEventManager.RemoveEvent(handle);

        _inventoryNodeEventHandles.Clear();
        _seenInventoryAddons.Clear();
    }

    private static string TryGetAddonName(AddonArgs args, string fallback)
    {
        try
        {
            var type = args.GetType();
            var prop = type.GetProperty("AddonName");
            if (prop?.GetValue(args) is string name && !string.IsNullOrWhiteSpace(name))
                return name;
        }
        catch
        {
        }

        return fallback;
    }

    public unsafe string TestFireCallback(string addonName, int callbackType, int val0, int val1, bool requireVisible = true)
    {
        try
        {
            var ptr = BackstabTheTrade.GameGui.GetAddonByName(addonName);
            if (ptr.Address == nint.Zero) return "Addon not found";

            var addon = (AtkUnitBase*)ptr.Address;
            if (requireVisible && !addon->IsVisible) return "Addon not visible";

            if (val1 >= 0)
            {
                var values = stackalloc AtkValue[2];
                values[0].SetInt(val0);
                values[1].SetInt(val1);
                addon->FireCallback((uint)callbackType, values, false);
            }
            else
            {
                var values = stackalloc AtkValue[1];
                values[0].SetInt(val0);
                addon->FireCallback((uint)callbackType, values, false);
            }

            return $"FireCallback sent visible={addon->IsVisible}";
        }
        catch (Exception ex)
        {
            return $"FireCallback failed: {ex.Message}";
        }
    }

    public unsafe string TraceContextMenuComponent()
    {
        try
        {
            var ptr = BackstabTheTrade.GameGui.GetAddonByName("ContextMenu");
            if (ptr.Address == nint.Zero)
                return "ContextMenu addon not found";

            var addon = (AtkUnitBase*)ptr.Address;
            if (!addon->IsVisible)
                return "ContextMenu addon not visible";

            WriteAtkValueSnapshot("ContextMenuTrace", addon);
            DumpAddonTreeSnapshot(addon, "ContextMenuTrace:Tree", 60);
            WriteContextMenuListSnapshot(addon, "ContextMenuTrace:List");
            return "ContextMenu component traced";
        }
        catch (Exception ex)
        {
            return $"ContextMenu trace failed: {ex.Message}";
        }
    }

    public unsafe string TryTriggerContextMenuTrade()
    {
        try
        {
            if (!TryGetContextMenuTradeInfo(out var tradeIndex, out var labelsSummary, out var error))
                return error;

            var result = TestFireCallback("ContextMenu", 2, tradeIndex, 0, requireVisible: false);
            return $"callbackType=2, tradeIndex={tradeIndex}, firstValue={tradeIndex}, secondValue=0, result={result}, labels={labelsSummary}";
        }
        catch (Exception ex)
        {
            return $"Backstab The Trade trigger failed: {ex.Message}";
        }
    }

    private unsafe bool TryGetContextMenuTradeInfo(out int tradeIndex, out string labelsSummary, out string error)
    {
        tradeIndex = _lastContextMenuTradeIndex;
        labelsSummary = _lastContextMenuLabelsSummary;
        error = string.Empty;

        var ptr = BackstabTheTrade.GameGui.GetAddonByName("ContextMenu");
        if (ptr.Address == nint.Zero)
        {
            if (tradeIndex >= 0 && !string.IsNullOrWhiteSpace(labelsSummary))
                return true;

            error = "ContextMenu addon not found";
            return false;
        }

        var addon = (AtkUnitBase*)ptr.Address;
        if (!addon->IsVisible)
        {
            if (tradeIndex >= 0 && !string.IsNullOrWhiteSpace(labelsSummary))
                return true;

            error = "ContextMenu addon not visible";
            return false;
        }

        var listNode = addon->GetNodeById(2);
        if (listNode == null)
        {
            error = "ContextMenu node2 not found";
            return false;
        }

        var list = listNode->GetAsAtkComponentList();
        if (list == null)
        {
            error = $"ContextMenu node2 type={(int)listNode->Type}, list=<null>";
            return false;
        }

        var count = Math.Min(list->ListLength, 32);
        CacheContextMenuTradeInfo(list, count);
        tradeIndex = _lastContextMenuTradeIndex;
        labelsSummary = _lastContextMenuLabelsSummary;
        if (tradeIndex < 0)
        {
            error = $"Trade label not found, labels={labelsSummary}";
            return false;
        }

        return true;
    }

    public unsafe string TestSetNumericValue(string addonName, uint nodeId, int value)
    {
        try
        {
            var ptr = BackstabTheTrade.GameGui.GetAddonByName(addonName);
            if (ptr.Address == nint.Zero) return "Addon not found";

            var addon = (AtkUnitBase*)ptr.Address;
            var node = addon->GetNodeById(nodeId);
            if (node == null) return $"Node {nodeId} not found";

            var numeric = node->GetAsAtkComponentNumericInput();
            if (numeric == null) return $"Node {nodeId} is not a numeric input";

            numeric->SetValue(value);
            return $"Set node {nodeId} to {value}";
        }
        catch (Exception ex)
        {
            return $"SetValue failed: {ex.Message}";
        }
    }

    public string DumpAddon(string addonName)
    {
        try
        {
            DumpAllNodes(addonName);
            return $"Dumped {addonName}";
        }
        catch (Exception ex)
        {
            return $"Dump failed: {ex.Message}";
        }
    }

    private static unsafe int ReadAtkEventParam(nint atkEventPtr)
    {
        try { return (int)((AtkEvent*)atkEventPtr)->Param; }
        catch { return -1; }
    }

    private void Write(string msg)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        BackstabTheTrade.Log.Information("[Tracker] " + msg);
        lock (Log) { Log.Add(entry); if (Log.Count > MaxLog) Log.RemoveAt(0); }
    }

    private void RegisterInventoryTrackingListeners()
    {
        if (_inventoryTrackingRegistered)
            return;

        foreach (var addonName in InventoryTrackedAddons)
        {
            BackstabTheTrade.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, addonName, OnInventoryAddonSetup);
            BackstabTheTrade.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, addonName, OnInventoryAddonFinalize);
            BackstabTheTrade.AddonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, addonName, OnInventoryAddonEvent);
            BackstabTheTrade.AddonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, addonName, OnInventoryAddonEvent);
        }

        _inventoryTrackingRegistered = true;
    }

    private void UnregisterInventoryTrackingListeners()
    {
        if (!_inventoryTrackingRegistered)
            return;

        foreach (var addonName in InventoryTrackedAddons)
        {
            BackstabTheTrade.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, addonName, OnInventoryAddonSetup);
            BackstabTheTrade.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, addonName, OnInventoryAddonFinalize);
            BackstabTheTrade.AddonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, addonName, OnInventoryAddonEvent);
            BackstabTheTrade.AddonLifecycle.UnregisterListener(AddonEvent.PostReceiveEvent, addonName, OnInventoryAddonEvent);
        }

        _inventoryTrackingRegistered = false;
    }

    public void Dispose()
    {
        BackstabTheTrade.Framework.Update -= OnTick;
        UnregisterInputButtonEvents();
        UnregisterContextMenuNodeEvents();
        UnregisterDragDropNodeEvents();
        UnregisterInventoryNodeEvents();
        UnregisterInventoryTrackingListeners();
        BackstabTheTrade.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "Trade", OnTradeSetup);
        BackstabTheTrade.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "Trade", OnTradeFinalize);
        BackstabTheTrade.AddonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, "Trade", OnTradeEvent);
        BackstabTheTrade.AddonLifecycle.UnregisterListener(AddonEvent.PostReceiveEvent, "Trade", OnTradeEvent);
        BackstabTheTrade.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "InputNumeric", OnInputSetup);
        BackstabTheTrade.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "InputNumeric", OnInputFinalize);
        BackstabTheTrade.AddonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, "InputNumeric", OnInputEvent);
        BackstabTheTrade.AddonLifecycle.UnregisterListener(AddonEvent.PostReceiveEvent, "InputNumeric", OnInputEvent);
        BackstabTheTrade.AddonLifecycle.UnregisterListener(AddonEvent.PreRequestedUpdate, "InputNumeric", OnInputUpdate);
        BackstabTheTrade.AddonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, "InputNumeric", OnInputUpdate);
        BackstabTheTrade.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", OnSelectYesnoSetup);
        BackstabTheTrade.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "SelectYesno", OnSelectYesnoFinalize);
        BackstabTheTrade.AddonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, "SelectYesno", OnSelectYesnoEvent);
        BackstabTheTrade.AddonLifecycle.UnregisterListener(AddonEvent.PostReceiveEvent, "SelectYesno", OnSelectYesnoEvent);
        BackstabTheTrade.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesNo", OnSelectYesNoSetup);
        BackstabTheTrade.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "SelectYesNo", OnSelectYesNoFinalize);
        BackstabTheTrade.AddonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, "SelectYesNo", OnSelectYesNoEvent);
        BackstabTheTrade.AddonLifecycle.UnregisterListener(AddonEvent.PostReceiveEvent, "SelectYesNo", OnSelectYesNoEvent);
    }
}





