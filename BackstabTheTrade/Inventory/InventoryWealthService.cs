using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace BackstabTheTrade;

public sealed class InventoryWealthService
{
    private const int SnapshotRefreshIntervalMs = 1500;
    private const int SnapshotMinBuildIntervalMs = 1000;
    private const int MaxTradeSlotsPerWindow = 5;
    private static readonly InventoryType[] PersonalInventoryTypes =
    {
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    };

    private static readonly Dictionary<string, int> PriceOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Salvaged Ring"] = 8_000,
        ["Salvaged Bracelet"] = 9_000,
        ["Salvaged Earring"] = 10_000,
        ["Salvaged Necklace"] = 13_000,
        ["Extravagant Salvaged Ring"] = 27_000,
        ["Extravagant Salvaged Bracelet"] = 28_500,
        ["Extravagant Salvaged Earring"] = 30_000,
        ["Extravagant Salvaged Necklace"] = 34_500,
    };

    private static readonly HashSet<string> SalvagedTradeItemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Salvaged Necklace",
        "Salvaged Earring",
        "Salvaged Bracelet",
        "Salvaged Ring",
        "Extravagant Salvaged Necklace",
        "Extravagant Salvaged Earring",
        "Extravagant Salvaged Bracelet",
        "Extravagant Salvaged Ring",
    };

    private InventoryWealthSnapshot _cached = InventoryWealthSnapshot.Empty;
    private DateTime _nextRefreshAt = DateTime.MinValue;
    private DateTime _lastBuildAt = DateTime.MinValue;
    private bool _hasSnapshot;
    private bool _refreshRequested = true;
    private bool _activeDemand;

    public InventoryWealthSnapshot GetSnapshot()
    {
        if (!_hasSnapshot)
        {
            _cached = BuildSnapshot();
            _hasSnapshot = true;
            _refreshRequested = false;
            _lastBuildAt = DateTime.Now;
            _nextRefreshAt = _lastBuildAt.AddMilliseconds(SnapshotRefreshIntervalMs);
            return _cached;
        }

        if (_activeDemand && DateTime.Now >= _nextRefreshAt)
        {
            _refreshRequested = true;
            _nextRefreshAt = DateTime.Now.AddMilliseconds(SnapshotRefreshIntervalMs);
        }

        return _cached;
    }

    public void SetActiveDemand(bool activeDemand)
    {
        _activeDemand = activeDemand;
        if (activeDemand && _nextRefreshAt == DateTime.MinValue)
            _nextRefreshAt = DateTime.Now.AddMilliseconds(SnapshotRefreshIntervalMs);
    }

    public void Tick()
    {
        if (!_activeDemand || !_refreshRequested)
            return;

        var now = DateTime.Now;
        if (_hasSnapshot && now < _nextRefreshAt)
            return;

        if (_hasSnapshot && now < _lastBuildAt.AddMilliseconds(SnapshotMinBuildIntervalMs))
            return;

        _cached = BuildSnapshot();
        _hasSnapshot = true;
        _refreshRequested = false;
        _lastBuildAt = now;
        _nextRefreshAt = now.AddMilliseconds(SnapshotRefreshIntervalMs);
    }

    public void Invalidate()
    {
        _nextRefreshAt = DateTime.MinValue;
        _refreshRequested = true;
    }

    public Dictionary<uint, long> CaptureItemQuantities()
    {
        var snapshot = BuildSnapshot();
        return snapshot.Entries.ToDictionary(entry => entry.ItemId, entry => entry.Quantity);
    }

    public long CapturePlayerGil()
    {
        return BuildSnapshot().PlayerGil;
    }

    public unsafe long GetCurrentPlayerGil()
    {
        var manager = InventoryManager.Instance();
        return manager == null ? 0 : GetPlayerGil(manager);
    }

    public static uint NormalizeItemId(uint itemId)
    {
        return itemId >= 1_000_000 ? itemId - 1_000_000 : itemId;
    }

    public InventoryTradePlan BuildTradePlan(long targetGil)
    {
        return BuildTradePlan(targetGil, static _ => true);
    }

    public InventoryTradePlan BuildSalvagedTradePlan(long targetGil)
    {
        return BuildTradePlan(targetGil, static entry => IsSalvagedTradeItem(entry.Name));
    }

    public InventoryTradePlan BuildAllSalvagedTradePlan()
    {
        var snapshot = GetSnapshot();
        var entries = snapshot.Entries
            .Where(entry => IsSalvagedTradeItem(entry.Name) && entry.UnitPrice > 0 && entry.Quantity > 0)
            .OrderByDescending(entry => entry.TotalValue)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new InventoryTradePlanEntry(
                entry.ItemId,
                entry.DisplayName,
                entry.UnitPrice,
                entry.Quantity,
                entry.TotalValue,
                entry.MaxTradeQuantity,
                BuildTradeChunks(entry.Quantity, entry.MaxTradeQuantity),
                entry.StackSources))
            .ToArray();

        long plannedValue = entries.Sum(entry => entry.TotalValue);
        return new InventoryTradePlan(
            plannedValue,
            plannedValue,
            0,
            entries.Length > 0,
            entries);
    }

    private InventoryTradePlan BuildTradePlan(long targetGil, Func<InventoryWealthEntry, bool> candidateFilter)
    {
        var snapshot = GetSnapshot();
        if (targetGil <= 0)
            return InventoryTradePlan.Empty with { TargetValue = targetGil };

        var candidates = snapshot.Entries
            .Where(entry => entry.UnitPrice > 0 && entry.Quantity > 0 && candidateFilter(entry))
            .OrderByDescending(entry => entry.UnitPrice)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (candidates.Length == 0)
            return new InventoryTradePlan(targetGil, 0, targetGil, false, Array.Empty<InventoryTradePlanEntry>());

        long gcd = candidates[0].UnitPrice;
        foreach (var entry in candidates)
            gcd = Gcd(gcd, entry.UnitPrice);

        if (gcd <= 0)
            gcd = 1;

        var scaledTargetLong = targetGil / gcd;
        if (scaledTargetLong <= 0)
            return new InventoryTradePlan(targetGil, 0, targetGil, false, Array.Empty<InventoryTradePlanEntry>());

        if (scaledTargetLong > 600_000)
            return BuildGreedyPlan(targetGil, candidates);

        int scaledTarget = (int)scaledTargetLong;
        var packs = new List<TradePack>();
        for (int i = 0; i < candidates.Length; i++)
        {
            long remaining = candidates[i].Quantity;
            int chunk = 1;
            while (remaining > 0)
            {
                int take = (int)Math.Min(chunk, remaining);
                packs.Add(new TradePack(i, take, (int)((candidates[i].UnitPrice / gcd) * take)));
                remaining -= take;
                chunk <<= 1;
            }
        }

        var reachable = new bool[scaledTarget + 1];
        var prevSum = new int[scaledTarget + 1];
        var prevPack = new int[scaledTarget + 1];
        Array.Fill(prevSum, -1);
        Array.Fill(prevPack, -1);
        reachable[0] = true;

        for (int packIndex = 0; packIndex < packs.Count; packIndex++)
        {
            var pack = packs[packIndex];
            for (int sum = scaledTarget; sum >= pack.ScaledValue; sum--)
            {
                if (!reachable[sum] && reachable[sum - pack.ScaledValue])
                {
                    reachable[sum] = true;
                    prevSum[sum] = sum - pack.ScaledValue;
                    prevPack[sum] = packIndex;
                }
            }
        }

        int bestSum = scaledTarget;
        while (bestSum > 0 && !reachable[bestSum])
            bestSum--;

        var quantities = new long[candidates.Length];
        int cursor = bestSum;
        while (cursor > 0 && prevPack[cursor] >= 0)
        {
            var pack = packs[prevPack[cursor]];
            quantities[pack.EntryIndex] += pack.Quantity;
            cursor = prevSum[cursor];
        }

        long plannedValue = bestSum * gcd;
        var entries = new List<InventoryTradePlanEntry>();
        for (int i = 0; i < candidates.Length; i++)
        {
            if (quantities[i] <= 0)
                continue;

            var entry = candidates[i];
            entries.Add(new InventoryTradePlanEntry(
                entry.ItemId,
                entry.DisplayName,
                entry.UnitPrice,
                quantities[i],
                quantities[i] * entry.UnitPrice,
                entry.MaxTradeQuantity,
                BuildTradeChunks(quantities[i], entry.MaxTradeQuantity),
                entry.StackSources));
        }

        return new InventoryTradePlan(
            targetGil,
            plannedValue,
            targetGil - plannedValue,
            plannedValue == targetGil,
            entries);
    }

    public InventoryTradePlan BuildTradePlanFromSelections(IReadOnlyDictionary<uint, long> requestedQuantities)
    {
        var snapshot = GetSnapshot();
        if (requestedQuantities.Count == 0)
            return InventoryTradePlan.Empty;

        var entryMap = snapshot.Entries.ToDictionary(entry => entry.ItemId);
        var entries = new List<InventoryTradePlanEntry>();
        long plannedValue = 0;

        foreach (var pair in requestedQuantities)
        {
            if (pair.Value <= 0)
                continue;

            if (!entryMap.TryGetValue(pair.Key, out var entry))
                continue;

            long quantity = Math.Min(pair.Value, entry.Quantity);
            if (quantity <= 0)
                continue;

            long totalValue = quantity * entry.UnitPrice;
            entries.Add(new InventoryTradePlanEntry(
                entry.ItemId,
                entry.DisplayName,
                entry.UnitPrice,
                quantity,
                totalValue,
                entry.MaxTradeQuantity,
                BuildTradeChunks(quantity, entry.MaxTradeQuantity),
                entry.StackSources));
            plannedValue += totalValue;
        }

        return new InventoryTradePlan(
            plannedValue,
            plannedValue,
            0,
            entries.Count > 0,
            entries
                .OrderByDescending(entry => entry.TotalValue)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    public IReadOnlyList<InventoryTradeBatch> SplitIntoTradeBatches(InventoryTradePlan plan)
    {
        if (plan.Entries.Count == 0)
            return Array.Empty<InventoryTradeBatch>();

        var flatChunks = new List<InventoryTradeBatchChunk>();
        foreach (var entry in plan.Entries)
        {
            var remainingSources = entry.StackSources
                .Select(source => new MutableStackSource(source.ContainerType, source.SlotIndex, source.Quantity))
                .ToList();

            foreach (var chunk in entry.TradeChunks)
            {
                if (chunk <= 0)
                    continue;

                var chunkSources = TakeStackSources(remainingSources, chunk);
                if (chunkSources.Count == 0)
                {
                    flatChunks.Add(new InventoryTradeBatchChunk(
                        entry.ItemId,
                        entry.Name,
                        chunk,
                        entry.UnitPrice,
                        chunk * entry.UnitPrice,
                        Array.Empty<InventoryStackSource>()));
                    continue;
                }

                // A native inventory context menu can only trade from one stack at a time.
                // If a requested chunk spans multiple stacks (for example 64 + 35 = 99),
                // split it into separate trade-window slots so full-auto manual mode does
                // not final-trade after only the first partial stack.
                foreach (var source in chunkSources)
                {
                    flatChunks.Add(new InventoryTradeBatchChunk(
                        entry.ItemId,
                        entry.Name,
                        source.Quantity,
                        entry.UnitPrice,
                        source.Quantity * entry.UnitPrice,
                        new[] { source }));
                }
            }
        }

        if (flatChunks.Count == 0)
            return Array.Empty<InventoryTradeBatch>();

        var batches = new List<InventoryTradeBatch>();
        for (int i = 0; i < flatChunks.Count; i += MaxTradeSlotsPerWindow)
        {
            var batchChunks = flatChunks
                .Skip(i)
                .Take(MaxTradeSlotsPerWindow)
                .ToArray();

            batches.Add(new InventoryTradeBatch(
                batches.Count + 1,
                batchChunks.Sum(chunk => chunk.TotalValue),
                batchChunks));
        }

        return batches;
    }

    private unsafe InventoryWealthSnapshot BuildSnapshot()
    {
        var entries = new Dictionary<uint, InventoryWealthAccumulator>();
        var itemSheet = BackstabTheTrade.DataManager.GetExcelSheet<Item>();
        var manager = InventoryManager.Instance();
        if (manager == null || itemSheet == null)
            return InventoryWealthSnapshot.Empty;

        long playerGil = GetPlayerGil(manager);

        foreach (var containerType in PersonalInventoryTypes)
        {
            var container = manager->GetInventoryContainer(containerType);
            if (container == null)
                continue;

            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var slot = container->GetInventorySlot(slotIndex);
                if (slot == null)
                    continue;

                var rawItemId = slot->ItemId;
                var itemId = NormalizeItemId(rawItemId);
                var quantity = (long)slot->Quantity;
                if (itemId == 0 || quantity <= 0)
                    continue;

                if (!itemSheet.TryGetRow(itemId, out var itemRow))
                    continue;

                var itemName = itemRow.Name.ToString();
                if (string.IsNullOrWhiteSpace(itemName))
                    continue;

                if (!IsEligibleTradeItem(itemRow, slot, itemName))
                    continue;

                var unitPrice = GetUnitPrice(itemName, itemRow);
                var maxTradeQuantity = GetMaxTradeQuantity(itemRow);
                bool isHighQuality = rawItemId >= 1_000_000;
                if (!entries.TryGetValue(rawItemId, out var entry))
                {
                    entry = new InventoryWealthAccumulator(rawItemId, itemId, itemName, isHighQuality, unitPrice, maxTradeQuantity);
                    entries[rawItemId] = entry;
                }

                entry.Quantity += quantity;
                var sourceContainerType = slot->Container;
                // `slot->Slot` can be a raw game slot id that doesn't map cleanly to the
                // visible 1..35 row-major grid inside each inventory block. For manual trade
                // routing we want the local container index so block/slot labels stay stable.
                int sourceSlotIndex = slotIndex;

                entry.StackSources.Add(new InventoryStackSource(sourceContainerType, sourceSlotIndex, quantity));
            }
        }

        var entryList = new InventoryWealthEntry[entries.Count];
        int entryIndex = 0;
        long totalItemValue = 0;

        foreach (var accumulator in entries.Values)
        {
            if (accumulator.StackSources.Count > 1)
                accumulator.StackSources.Sort(InventoryStackSourceComparer.Instance);

            var stackSources = accumulator.StackSources.ToArray();
            long totalValue = accumulator.Quantity * accumulator.UnitPrice;
            totalItemValue += totalValue;

            entryList[entryIndex++] = new InventoryWealthEntry(
                accumulator.ItemId,
                accumulator.BaseItemId,
                accumulator.Name,
                accumulator.DisplayName,
                accumulator.QualityLabel,
                accumulator.IsHighQuality,
                accumulator.UnitPrice,
                accumulator.Quantity,
                totalValue,
                accumulator.MaxTradeQuantity,
                BuildTradeChunks(accumulator.Quantity, accumulator.MaxTradeQuantity),
                stackSources);
        }

        if (entryList.Length > 1)
            Array.Sort(entryList, InventoryWealthEntryComparer.Instance);

        return new InventoryWealthSnapshot(
            playerGil,
            totalItemValue,
            playerGil + totalItemValue,
            entryList);
    }

    private static long GetUnitPrice(string itemName, Item itemRow)
    {
        if (PriceOverrides.TryGetValue(itemName, out var overridePrice))
            return overridePrice;

        return itemRow.PriceLow;
    }

    private static unsafe bool IsEligibleTradeItem(Item itemRow, InventoryItem* slot, string itemName)
    {
        return IsEligibleTradeItemSheet(itemRow, itemName) &&
               IsEligibleTradeItemSlot(slot);
    }

    private static bool IsEligibleTradeItemSheet(Item itemRow, string itemName)
    {
        if (itemRow.IsUntradable)
            return false;

        if (GetOptionalBoolProperty(itemRow, "IsIndisposable") ||
            GetOptionalBoolProperty(itemRow, "IsUniqueUntradable") ||
            GetOptionalBoolProperty(itemRow, "IsCollectable"))
        {
            return false;
        }

        // Housing permits and similar ownership documents can still slip through
        // `IsUntradable` in some data revisions, but they cannot be traded player-to-player.
        if (itemName.Contains("Permit", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static unsafe bool IsEligibleTradeItemSlot(InventoryItem* slot)
    {
        if (slot == null)
            return false;

        // Items showing "BINDING" in the inventory UI are already bound to the
        // character and cannot be traded. In client inventory data that state is
        // reflected through the spiritbond/collectability field becoming non-zero.
        if (slot->SpiritbondOrCollectability > 0)
            return false;

        return true;
    }

    private static bool GetOptionalBoolProperty(Item itemRow, string propertyName)
    {
        try
        {
            var property = typeof(Item).GetProperty(propertyName);
            if (property == null || property.PropertyType != typeof(bool))
                return false;

            return property.GetValue(itemRow) is true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsSalvagedTradeItem(string itemName)
    {
        return SalvagedTradeItemNames.Contains(itemName);
    }

    private static InventoryTradePlan BuildGreedyPlan(long targetGil, InventoryWealthEntry[] candidates)
    {
        long remaining = targetGil;
        var entries = new List<InventoryTradePlanEntry>();

        foreach (var entry in candidates)
        {
            long maxCount = Math.Min(entry.Quantity, remaining / entry.UnitPrice);
            if (maxCount <= 0)
                continue;

            long total = maxCount * entry.UnitPrice;
            entries.Add(new InventoryTradePlanEntry(
                entry.ItemId,
                entry.DisplayName,
                entry.UnitPrice,
                maxCount,
                total,
                entry.MaxTradeQuantity,
                BuildTradeChunks(maxCount, entry.MaxTradeQuantity),
                entry.StackSources));
            remaining -= total;

            if (remaining <= 0)
                break;
        }

        long planned = targetGil - remaining;
        return new InventoryTradePlan(targetGil, planned, remaining, planned == targetGil, entries);
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return Math.Abs(a);
    }

    private static long GetMaxTradeQuantity(Item itemRow)
    {
        return Math.Max(1, (long)itemRow.StackSize);
    }

    private static IReadOnlyList<InventoryStackSource> TakeStackSources(List<MutableStackSource> sources, long neededQuantity)
    {
        if (neededQuantity <= 0 || sources.Count == 0)
            return Array.Empty<InventoryStackSource>();

        var singleStack = sources
            .Where(source => source.Quantity >= neededQuantity)
            .OrderBy(source => source.Quantity)
            .ThenBy(source => source.PageSortKey)
            .ThenBy(source => source.SlotIndex)
            .FirstOrDefault();

        if (singleStack != null)
        {
            singleStack.Quantity -= neededQuantity;
            return new[]
            {
                new InventoryStackSource(singleStack.ContainerType, singleStack.SlotIndex, neededQuantity),
            };
        }

        var result = new List<InventoryStackSource>();
        long remaining = neededQuantity;

        foreach (var source in sources
                     .OrderByDescending(source => source.Quantity)
                     .ThenBy(source => source.PageSortKey)
                     .ThenBy(source => source.SlotIndex))
        {
            if (remaining <= 0)
                break;

            if (source.Quantity <= 0)
                continue;

            long taken = Math.Min(source.Quantity, remaining);
            if (taken <= 0)
                continue;

            result.Add(new InventoryStackSource(source.ContainerType, source.SlotIndex, taken));
            source.Quantity -= taken;
            remaining -= taken;
        }

        return result;
    }

    private static IReadOnlyList<long> BuildTradeChunks(long quantity, long maxTradeQuantity)
    {
        if (quantity <= 0)
            return Array.Empty<long>();

        maxTradeQuantity = Math.Max(1, maxTradeQuantity);

        var chunks = new List<long>();
        long remaining = quantity;
        while (remaining > 0)
        {
            long chunk = Math.Min(maxTradeQuantity, remaining);
            chunks.Add(chunk);
            remaining -= chunk;
        }

        return chunks;
    }

    private static unsafe long GetPlayerGil(InventoryManager* manager)
    {
        var currency = manager->GetInventoryContainer(InventoryType.Currency);
        if (currency == null)
            return 0;

        for (var slotIndex = 0; slotIndex < currency->Size; slotIndex++)
        {
            var slot = currency->GetInventorySlot(slotIndex);
            if (slot == null)
                continue;

            if (slot->ItemId == 1)
                return slot->Quantity;
        }

        return 0;
    }
}

internal sealed class InventoryWealthAccumulator(uint itemId, string name, long unitPrice, long maxTradeQuantity)
{
    public InventoryWealthAccumulator(uint itemId, uint baseItemId, string name, bool isHighQuality, long unitPrice, long maxTradeQuantity)
        : this(itemId, name, unitPrice, maxTradeQuantity)
    {
        BaseItemId = baseItemId;
        IsHighQuality = isHighQuality;
    }

    public uint ItemId { get; } = itemId;
    public uint BaseItemId { get; }
    public string Name { get; } = name;
    public string DisplayName => IsHighQuality ? $"{Name} (HQ)" : $"{Name} (NQ)";
    public string QualityLabel => IsHighQuality ? "HQ" : "NQ";
    public bool IsHighQuality { get; }
    public long UnitPrice { get; } = unitPrice;
    public long MaxTradeQuantity { get; } = maxTradeQuantity;
    public long Quantity { get; set; }
    public List<InventoryStackSource> StackSources { get; } = new();
}

internal sealed class InventoryWealthEntryComparer : IComparer<InventoryWealthEntry>
{
    public static InventoryWealthEntryComparer Instance { get; } = new();

    public int Compare(InventoryWealthEntry left, InventoryWealthEntry right)
    {
        int valueCompare = right.TotalValue.CompareTo(left.TotalValue);
        if (valueCompare != 0)
            return valueCompare;

        int nameCompare = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        if (nameCompare != 0)
            return nameCompare;

        return right.IsHighQuality.CompareTo(left.IsHighQuality);
    }
}

internal sealed class InventoryStackSourceComparer : IComparer<InventoryStackSource>
{
    public static InventoryStackSourceComparer Instance { get; } = new();

    public int Compare(InventoryStackSource left, InventoryStackSource right)
    {
        int pageCompare = left.PageSortKey.CompareTo(right.PageSortKey);
        if (pageCompare != 0)
            return pageCompare;

        return left.SlotIndex.CompareTo(right.SlotIndex);
    }
}

internal readonly record struct TradePack(int EntryIndex, int Quantity, int ScaledValue);

public readonly record struct InventoryWealthEntry(
    uint ItemId,
    uint BaseItemId,
    string Name,
    string DisplayName,
    string QualityLabel,
    bool IsHighQuality,
    long UnitPrice,
    long Quantity,
    long TotalValue,
    long MaxTradeQuantity,
    IReadOnlyList<long> TradeChunks,
    IReadOnlyList<InventoryStackSource> StackSources);

public readonly record struct InventoryWealthSnapshot(
    long PlayerGil,
    long TotalItemValue,
    long TotalWealth,
    IReadOnlyList<InventoryWealthEntry> Entries)
{
    public static InventoryWealthSnapshot Empty { get; } =
        new(0, 0, 0, Array.Empty<InventoryWealthEntry>());
}

public readonly record struct InventoryTradePlanEntry(
    uint ItemId,
    string Name,
    long UnitPrice,
    long Quantity,
    long TotalValue,
    long MaxTradeQuantity,
    IReadOnlyList<long> TradeChunks,
    IReadOnlyList<InventoryStackSource> StackSources);

public readonly record struct InventoryTradePlan(
    long TargetValue,
    long PlannedValue,
    long RemainingValue,
    bool IsExact,
    IReadOnlyList<InventoryTradePlanEntry> Entries)
{
    public static InventoryTradePlan Empty { get; } =
        new(0, 0, 0, false, Array.Empty<InventoryTradePlanEntry>());
}

public readonly record struct InventoryTradeBatchChunk(
    uint ItemId,
    string Name,
    long Quantity,
    long UnitPrice,
    long TotalValue,
    IReadOnlyList<InventoryStackSource> StackSources);

public readonly record struct InventoryTradeBatch(
    int BatchNumber,
    long TotalValue,
    IReadOnlyList<InventoryTradeBatchChunk> Chunks);

public readonly record struct InventoryStackSource(
    InventoryType ContainerType,
    int SlotIndex,
    long Quantity)
{
    private const int SlotsPerRow = 5;

    public int BlockNumber => ContainerType switch
    {
        InventoryType.Inventory1 => 1,
        InventoryType.Inventory2 => 2,
        InventoryType.Inventory3 => 3,
        InventoryType.Inventory4 => 4,
        _ => 0,
    };

    public int PageSortKey => BlockNumber > 0 ? BlockNumber : int.MaxValue;

    public int DisplaySlotNumber => SlotIndex + 1;

    public int VisualRow => SlotIndex >= 0 ? (SlotIndex / SlotsPerRow) + 1 : 0;

    public int VisualColumn => SlotIndex >= 0 ? (SlotIndex % SlotsPerRow) + 1 : 0;

    public string Label => BlockNumber > 0
        ? $"block {BlockNumber} slot {DisplaySlotNumber} x{Quantity:N0}"
        : $"{ContainerType} slot {DisplaySlotNumber} x{Quantity:N0}";
}

internal sealed class MutableStackSource(InventoryType containerType, int slotIndex, long quantity)
{
    public InventoryType ContainerType { get; } = containerType;
    public int SlotIndex { get; } = slotIndex;
    public long Quantity { get; set; } = quantity;

    public int PageSortKey => ContainerType switch
    {
        InventoryType.Inventory1 => 1,
        InventoryType.Inventory2 => 2,
        InventoryType.Inventory3 => 3,
        InventoryType.Inventory4 => 4,
        _ => int.MaxValue,
    };
}

