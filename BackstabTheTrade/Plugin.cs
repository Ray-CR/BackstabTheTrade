using Dalamud.Game.Command;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BackstabTheTrade;

public sealed class BackstabTheTrade : IDalamudPlugin
{
    private const int PlayerObjectKindValue = 1;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager        CommandManager  { get; private set; } = null!;
    [PluginService] internal static IPluginLog             Log             { get; private set; } = null!;
    [PluginService] internal static IFramework             Framework       { get; private set; } = null!;
    [PluginService] internal static IGameGui               GameGui         { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle        AddonLifecycle  { get; private set; } = null!;
    [PluginService] internal static IAddonEventManager     AddonEventManager { get; private set; } = null!;
    [PluginService] internal static IContextMenu           ContextMenu     { get; private set; } = null!;
    [PluginService] internal static IObjectTable           ObjectTable     { get; private set; } = null!;
    [PluginService] internal static ITargetManager         TargetManager   { get; private set; } = null!;
    [PluginService] internal static ISigScanner            SigScanner      { get; private set; } = null!;
    [PluginService] internal static IDataManager           DataManager     { get; private set; } = null!;

    private const string CommandName = "/autotrade";

    public Configuration  Configuration { get; init; }
    public EventTracker   EventTracker  { get; init; }
    public ChatSender     ChatSender    { get; init; }
    public InventoryWealthService InventoryWealth { get; init; }
    public string LastAgentInventoryContextSummary { get; private set; } = string.Empty;
    private readonly PluginUI     _ui;
    private readonly TradeManager _tradeManager;

    public BackstabTheTrade()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        ChatSender    = new ChatSender();
        EventTracker  = new EventTracker();
        InventoryWealth = new InventoryWealthService();
        _tradeManager = new TradeManager(this, ChatSender);
        _ui           = new PluginUI(this, _tradeManager);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Backstab The Trade window."
        });

        ContextMenu.OnMenuOpened += OnMenuOpened;
        Framework.Update += OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw         += _ui.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += _ui.Toggle;
        PluginInterface.UiBuilder.OpenMainUi   += _ui.Toggle;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        InventoryWealth.Tick();
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.Target is MenuTargetInventory inventoryTarget)
        {
            var summary = BuildAgentInventoryContextSummary(inventoryTarget);
            LastAgentInventoryContextSummary = summary;
            EventTracker.LogExternal($"[AgentInventoryContext] MenuOpened {summary}");
            var manualFullAutoHandled = false;

            if (TryExtractInventorySource(inventoryTarget, out var containerType, out var slotIndex) &&
                TryExtractInventoryItemId(inventoryTarget, out var itemId, out var itemName))
            {
                if (_tradeManager.IsRunning &&
                    _tradeManager.Mode != AutoTradeMode.Gil &&
                    _tradeManager.ActiveItemPlan.Entries.Count > 0)
                {
                    var result = _tradeManager.SchedulePlannedContextMenuTrade(itemId, itemName, true);
                    EventTracker.LogExternal($"[ManualFullAuto] AgentInventoryContext auto-trigger: itemId={itemId}, source={containerType}:{slotIndex}; {result}");
                    _tradeManager.NoteAgentInventoryContext($"ManualFullAuto auto-trigger: {result}");
                    manualFullAutoHandled = result.Contains("Scheduled", StringComparison.Ordinal) ||
                                            result.Contains("FireCallback sent", StringComparison.Ordinal);
                }
            }
            else if (_tradeManager.IsRunning && _tradeManager.Mode == AutoTradeMode.ItemManual)
            {
                EventTracker.LogExternal("[ManualFullAuto] AgentInventoryContext extract failed; cannot read item source or item id.");
            }

            if (_tradeManager.TradeWindowOpen)
            {
                if (!manualFullAutoHandled)
                    _tradeManager.NoteAgentInventoryContext(summary);
                args.AddMenuItem(CreatePlainMenuItem("Backstab The Trade 1", _ =>
                {
                    var usePlannedPath = TryExtractInventoryItemId(inventoryTarget, out var itemId, out var itemName) &&
                                         _tradeManager.Mode != AutoTradeMode.Gil &&
                                         _tradeManager.IsRunning &&
                                         _tradeManager.ActiveItemPlan.Entries.Count > 0;
                    var result = usePlannedPath
                        ? _tradeManager.TryRunPlannedContextMenuTrade(itemId, itemName, false)
                        : _tradeManager.TryRunContextMenuTradeAutoConfirm(false);
                    EventTracker.LogExternal($"[AutoTrade1] {result}");
                    _tradeManager.NoteAgentInventoryContext(result);
                }));
                args.AddMenuItem(CreatePlainMenuItem("Backstab The Trade 2", _ =>
                {
                    var usePlannedPath = TryExtractInventoryItemId(inventoryTarget, out var itemId, out var itemName) &&
                                         _tradeManager.Mode != AutoTradeMode.Gil &&
                                         _tradeManager.IsRunning &&
                                         _tradeManager.ActiveItemPlan.Entries.Count > 0;
                    var result = usePlannedPath
                        ? _tradeManager.TryRunPlannedContextMenuTrade(itemId, itemName, true)
                        : _tradeManager.TryRunContextMenuTradeAutoConfirm(true);
                    EventTracker.LogExternal($"[AutoTrade2] {result}");
                    _tradeManager.NoteAgentInventoryContext(result);
                }));
            }

            return;
        }

        if (args.Target is not MenuTargetDefault t)
            return;
        if (string.IsNullOrEmpty(t.TargetName)) return;
        if (t.TargetObjectId == 0xE0000000) return;
        var obj = ObjectTable.SearchById(t.TargetObjectId);
        if (obj == null) return;
        if ((int)obj.ObjectKind != PlayerObjectKindValue) return;

        args.AddMenuItem(CreatePlainMenuItem("Backstab The Trade", menuArgs =>
        {
            if (menuArgs.Target is MenuTargetDefault target)
                _ui.OpenWithTarget(target.TargetName);
        }));
    }

    private static MenuItem CreatePlainMenuItem(string name, Action<IMenuItemClickedArgs> onClicked)
    {
        return new MenuItem
        {
            Name = name,
            UseDefaultPrefix = true,
            OnClicked = onClicked,
        };
    }

    private static string BuildAgentInventoryContextSummary(MenuTargetInventory target)
    {
        try
        {
            var parts = new List<string> { "target=inventory" };
            object? targetItem = target.TargetItem;
            if (targetItem == null)
                return string.Join(", ", parts.Append("item=<null>"));

            var itemType = targetItem.GetType();
            parts.Add($"itemType={itemType.Name}");

            foreach (var name in new[]
                     {
                         "ItemId", "ItemName", "Name", "Quantity", "Count",
                         "Container", "ContainerType", "InventoryType",
                         "Slot", "SlotIndex", "Page", "PageIndex", "Block", "BlockIndex",
                     })
            {
                var value = TryReadProperty(targetItem, name);
                if (value != null)
                    parts.Add($"{name}={value}");
            }

            var inventorySlot = TryReadProperty(targetItem, "InventorySlot");
            if (inventorySlot != null)
            {
                var slotIndex = TryExtractInventorySlotIndex(inventorySlot);
                if (slotIndex != null)
                {
                    parts.Add($"InventorySlotIndex={slotIndex.Value}");
                    parts.Add($"DisplaySlot={slotIndex.Value + 1}");
                }

                parts.Add("InventorySlot{" + BuildPublicObjectSummary(inventorySlot, 10) + "}");
                parts.Add("InventorySlotPrivate{" + BuildNonPublicObjectSummary(inventorySlot) + "}");
            }

            var visibleProps = itemType
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.CanRead)
                .Select(p => p.Name)
                .Take(12)
                .ToArray();
            if (visibleProps.Length > 0)
                parts.Add("props=" + string.Join("/", visibleProps));

            return string.Join(", ", parts);
        }
        catch (Exception ex)
        {
            return $"target=inventory, summary-failed={ex.Message}";
        }
    }

    private static int? TryExtractInventorySlotIndex(object inventorySlot)
    {
        try
        {
            if (inventorySlot is int directInt)
                return directInt;

            var publicValue = TryReadAnyProperty(inventorySlot, "Value");
            if (TryConvertSlotIndex(publicValue, out var publicInt))
                return publicInt;

            var field = inventorySlot.GetType().GetField("m_value", BindingFlags.Instance | BindingFlags.NonPublic);
            var raw = field?.GetValue(inventorySlot);
            return TryConvertSlotIndex(raw, out var privateInt) ? privateInt : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryConvertSlotIndex(object? raw, out int slotIndex)
    {
        slotIndex = -1;
        switch (raw)
        {
            case int i:
                slotIndex = i;
                return true;
            case uint u when u <= int.MaxValue:
                slotIndex = (int)u;
                return true;
            case long l when l is >= 0 and <= int.MaxValue:
                slotIndex = (int)l;
                return true;
            case ulong ul when ul <= int.MaxValue:
                slotIndex = (int)ul;
                return true;
            case short s:
                slotIndex = s;
                return true;
            case ushort us:
                slotIndex = us;
                return true;
            case byte b:
                slotIndex = b;
                return true;
            case sbyte sb when sb >= 0:
                slotIndex = sb;
                return true;
            case Enum e:
                slotIndex = Convert.ToInt32(e);
                return slotIndex >= 0;
            default:
                return false;
        }
    }

    private static bool TryExtractInventoryItemId(MenuTargetInventory target, out uint itemId, out string itemName)
    {
        itemId = 0;
        itemName = "Unknown item";

        try
        {
            object? targetItem = target.TargetItem;
            if (targetItem == null)
                return false;

            var rawItemId = TryReadProperty(targetItem, "BaseItemId") ?? TryReadProperty(targetItem, "ItemId");
            switch (rawItemId)
            {
                case uint u:
                    itemId = InventoryWealthService.NormalizeItemId(u);
                    break;
                case int i when i > 0:
                    itemId = InventoryWealthService.NormalizeItemId((uint)i);
                    break;
                case long l when l > 0:
                    itemId = InventoryWealthService.NormalizeItemId((uint)l);
                    break;
            }

            var rawName = TryReadProperty(targetItem, "ItemName") ?? TryReadProperty(targetItem, "Name");
            if (rawName != null)
                itemName = rawName.ToString() ?? itemName;

            return itemId != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractInventorySource(MenuTargetInventory target, out InventoryType containerType, out int slotIndex)
    {
        containerType = 0;
        slotIndex = -1;

        try
        {
            object? targetItem = target.TargetItem;
            if (targetItem == null)
                return false;

            var hasContainer = false;
            var rawContainer = TryReadProperty(targetItem, "ContainerType")
                               ?? TryReadProperty(targetItem, "Container")
                               ?? TryReadProperty(targetItem, "InventoryType");
            switch (rawContainer)
            {
                case InventoryType typed:
                    containerType = typed;
                    hasContainer = true;
                    break;
                case int i:
                    containerType = (InventoryType)i;
                    hasContainer = true;
                    break;
                case uint u:
                    containerType = (InventoryType)u;
                    hasContainer = true;
                    break;
                case long l:
                    containerType = (InventoryType)l;
                    hasContainer = true;
                    break;
                case Enum e:
                    containerType = (InventoryType)Convert.ToInt32(e);
                    hasContainer = true;
                    break;
            }

            var inventorySlot = TryReadProperty(targetItem, "InventorySlot");
            if (inventorySlot != null)
            {
                var extracted = TryExtractInventorySlotIndex(inventorySlot);
                if (extracted != null)
                    slotIndex = extracted.Value;
            }

            if (slotIndex < 0)
            {
                var rawSlot = TryReadProperty(targetItem, "SlotIndex") ?? TryReadProperty(targetItem, "Slot");
                switch (rawSlot)
                {
                    case int i:
                        slotIndex = i;
                        break;
                    case uint u:
                        slotIndex = (int)u;
                        break;
                    case long l:
                        slotIndex = (int)l;
                        break;
                    case short s:
                        slotIndex = s;
                        break;
                    case byte b:
                        slotIndex = b;
                        break;
                }
            }

            return hasContainer && slotIndex >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static object? TryReadProperty(object target, string name)
    {
        try
        {
            return target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    private static object? TryReadAnyProperty(object target, string name)
    {
        try
        {
            return target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildPublicObjectSummary(object obj, int maxMembers)
    {
        try
        {
            var parts = new List<string>();
            foreach (var prop in obj.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.CanRead).Take(maxMembers))
            {
                try
                {
                    parts.Add($"{prop.Name}={FormatValue(prop.GetValue(obj))}");
                }
                catch
                {
                    parts.Add($"{prop.Name}=<err>");
                }
            }

            return string.Join(", ", parts);
        }
        catch (Exception ex)
        {
            return $"summary-failed={ex.Message}";
        }
    }

    private static string BuildNonPublicObjectSummary(object obj)
    {
        try
        {
            var sb = new StringBuilder();
            bool first = true;

            foreach (var field in obj.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Take(16))
            {
                if (!first)
                    sb.Append(", ");
                first = false;
                object? value;
                try
                {
                    value = field.GetValue(obj);
                }
                catch
                {
                    value = "<err>";
                }

                sb.Append(field.Name).Append('=').Append(FormatValue(value));
            }

            foreach (var prop in obj.GetType().GetProperties(BindingFlags.Instance | BindingFlags.NonPublic).Where(p => p.CanRead).Take(16))
            {
                if (!first)
                    sb.Append(", ");
                first = false;
                object? value;
                try
                {
                    value = prop.GetValue(obj);
                }
                catch
                {
                    value = "<err>";
                }

                sb.Append(prop.Name).Append('=').Append(FormatValue(value));
            }

            return sb.Length == 0 ? "<none>" : sb.ToString();
        }
        catch (Exception ex)
        {
            return $"summary-failed={ex.Message}";
        }
    }

    private static string BuildPointerSummary(object? pointerObject)
    {
        if (pointerObject == null)
            return "<null>";

        try
        {
            unsafe
            {
                var raw = (nint)Pointer.Unbox(pointerObject);
                return raw == nint.Zero ? "0x0" : $"0x{raw:X}";
            }
        }
        catch (Exception ex)
        {
            return $"pointer-failed={ex.Message}";
        }
    }

    private static string FormatValue(object? value)
    {
        if (value == null)
            return "<null>";

        return value switch
        {
            string s => s,
            IntPtr p => $"0x{p.ToInt64():X}",
            UIntPtr p => $"0x{p.ToUInt64():X}",
            _ when value.GetType() == typeof(Pointer) => BuildPointerSummary(value),
            _ => value.ToString() ?? value.GetType().Name,
        };
    }

    private void OnCommand(string command, string args) => _ui.Toggle();

    public void Dispose()
    {
        ContextMenu.OnMenuOpened               -= OnMenuOpened;
        Framework.Update                       -= OnFrameworkUpdate;
        CommandManager.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw         -= _ui.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= _ui.Toggle;
        PluginInterface.UiBuilder.OpenMainUi   -= _ui.Toggle;
        ChatSender.Dispose();
        EventTracker.Dispose();
        _tradeManager.Dispose();
        _ui.Dispose();
    }
}

