using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using TerraStorage.Common;

namespace TerraStorage.Systems
{
    // World-level system that owns the authoritative storage of all disk data.
    // Provides insert, extract, and query operations across one or more disks,
    // manages the world-save lifecycle, and exposes a <see cref="StorageVersion"/>
    // counter so UI components can poll for changes without per-frame full refreshes.
    public class StorageWorldSystem : ModSystem
    {
        // Keyed by DiskId GUID for O(1) disk lookups
        private Dictionary<Guid, DiskData> _allDiskData = new();
        // Monotonically increasing counter stamped on every insert; used for "recently added" sort
        private long _insertionCounter;
        // When non-null, InsertItem/ExtractItem record which disk GUIDs they touch.
        // Use BeginModificationTracking / EndModificationTracking around server operations
        // so BroadcastDiskData only sends the actually-modified disks.
        private HashSet<Guid> _modifiedTracker;

        // Per-disk sequence numbers for delta sync. Incremented each time a disk is modified.
        // Clients track their own copy; a mismatch triggers a full resync for that disk.
        private readonly Dictionary<Guid, int> _diskSeqNums = new();

        // Snapshot of disk item states captured at BeginModificationTracking.
        // Used to compute item-level deltas (what actually changed) in EndModificationTrackingWithDeltas.
        private Dictionary<Guid, List<StoredItemStack>> _preModificationSnapshot;

        // Incremented on every insert or extract. UI can poll this to detect changes.
        public long StorageVersion { get; private set; }

        public static StorageWorldSystem Instance => ModContent.GetInstance<StorageWorldSystem>();

        public long InsertionCounter => _insertionCounter;

        //Increment StorageVersion to trigger UI refresh (used by delta sync on client).
        public void BumpStorageVersion() => StorageVersion++;

        //Get the current sequence number for a disk (0 if untracked).
        public int GetDiskSeqNum(Guid diskId) =>
            _diskSeqNums.TryGetValue(diskId, out int seq) ? seq : 0;

        //Increment and return the new sequence number for a disk.
        public int IncrementDiskSeqNum(Guid diskId)
        {
            _diskSeqNums.TryGetValue(diskId, out int seq);
            seq++;
            _diskSeqNums[diskId] = seq;
            return seq;
        }

        //Set the client-side sequence number baseline for a disk (used after full sync).
        public void SetDiskSeqNum(Guid diskId, int seq) => _diskSeqNums[diskId] = seq;

        //Remove sequence tracking for a disk (used when disk is removed).
        public void RemoveDiskSeqNum(Guid diskId) => _diskSeqNums.Remove(diskId);

        // Snapshots the disks this operation can touch, so the deltas afterwards have a before-state
        // to compare against.
        //
        // Scoped, not the whole registry. Snapshotting every entry made registry size a multiplier on
        // EVERY deposit, withdrawal, craft and quick-stack - and the registry grows on inserts nobody
        // can bound (issue 27). An operation reaches the disks of the Terminal it was issued from and
        // no others, so that network is the honest scope.
        //
        // Under-scoping cannot corrupt a client: a disk modified without a snapshot is reported for a
        // full resync rather than given an empty before-state, which would have read as "everything
        // on this disk is new".
        public void BeginModificationTracking(IEnumerable<Guid> operationDiskIds)
        {
            _modifiedTracker = new HashSet<Guid>();
            _preModificationSnapshot = new Dictionary<Guid, List<StoredItemStack>>();

            if (operationDiskIds == null)
                return;

            foreach (var diskId in operationDiskIds)
            {
                if (_preModificationSnapshot.ContainsKey(diskId))
                    continue;

                if (_allDiskData.TryGetValue(diskId, out var disk))
                    _preModificationSnapshot[diskId] = SnapshotItems(disk.Items);
            }
        }

        public List<Guid> EndModificationTracking()
        {
            var result = _modifiedTracker?.ToList() ?? new List<Guid>();
            _modifiedTracker = null;
            _preModificationSnapshot = null;
            return result;
        }

        // Ends modification tracking and computes item-level deltas for each modified disk.
        // Returns (modifiedDiskIds, deltas) where deltas maps diskGuid → list of changed items.
        // Each delta entry is the NEW state of that item stack on the disk (or stack=0 if removed). 
        // needsFullSync names disks that changed without a snapshot to compare against, so no delta
        // describes them. Treating a missing snapshot as "the disk was empty before" would tell the
        // client every stack on it had just appeared, which for a disk it already knows is a
        // duplicated view of the whole disk. The caller sends those in full instead.
        public (List<Guid> modified, Dictionary<Guid, DiskDelta> deltas, List<Guid> needsFullSync)
            EndModificationTrackingWithDeltas()
        {
            var modifiedIds = _modifiedTracker?.ToList() ?? new List<Guid>();
            var deltas = new Dictionary<Guid, DiskDelta>();
            var needsFullSync = new List<Guid>();

            if (_preModificationSnapshot != null)
            {
                foreach (var diskId in modifiedIds)
                {
                    if (!_preModificationSnapshot.TryGetValue(diskId, out var before))
                    {
                        // Bumped here, like a delta's, so the full state that goes out instead is not
                        // read as older than deltas the client has already applied.
                        IncrementDiskSeqNum(diskId);
                        needsFullSync.Add(diskId);
                        continue;
                    }

                    var after = _allDiskData.TryGetValue(diskId, out var disk)
                        ? disk.Items : new List<StoredItemStack>();

                    var delta = ComputeDelta(before, after);
                    delta.SeqNum = IncrementDiskSeqNum(diskId);
                    deltas[diskId] = delta;
                }
            }

            _modifiedTracker = null;
            _preModificationSnapshot = null;
            return (modifiedIds, deltas, needsFullSync);
        }

        //Shallow-clone item list for snapshotting (clones each StoredItemStack's mutable fields).
        private static List<StoredItemStack> SnapshotItems(List<StoredItemStack> items)
        {
            var snapshot = new List<StoredItemStack>(items.Count);
            foreach (var s in items)
            {
                var copy = new StoredItemStack
                {
                    ItemType = s.ItemType,
                    Stack = s.Stack,
                    PrefixId = s.PrefixId,
                    InsertionOrder = s.InsertionOrder,
                    ModData = s.ModData,
                    FullItemTag = s.FullItemTag
                };

                // Every server operation snapshots every disk. Rebuilding each copy's item to
                // re-decide what the original already knows would put a full deserialization per
                // stack on the netcode path.
                copy.CopyIdentityVerdictFrom(s);
                snapshot.Add(copy);
            }
            return snapshot;
        }

        // Computes the item-level differences between a before and after snapshot of a disk. 
        private static DiskDelta ComputeDelta(List<StoredItemStack> before, List<StoredItemStack> after)
        {
            var delta = new DiskDelta();

            // Build lookup: (itemType, prefixId) → total stack for items that pool.
            // Items that stand for themselves are tracked individually.
            var beforeCounts = new Dictionary<(int type, int prefix), int>();
            var afterCounts = new Dictionary<(int type, int prefix), int>();
            var beforeUnique = new List<StoredItemStack>();
            var afterUnique = new List<StoredItemStack>();

            foreach (var s in before)
            {
                if (s.IsUnique) { beforeUnique.Add(s); continue; }
                var key = (s.ItemType, s.PrefixId);
                beforeCounts.TryGetValue(key, out int existing);
                beforeCounts[key] = existing + s.Stack;
            }
            foreach (var s in after)
            {
                if (s.IsUnique) { afterUnique.Add(s); continue; }
                var key = (s.ItemType, s.PrefixId);
                afterCounts.TryGetValue(key, out int existing);
                afterCounts[key] = existing + s.Stack;
            }

            // Detect changes in stackable items
            var allKeys = new HashSet<(int type, int prefix)>(beforeCounts.Keys);
            allKeys.UnionWith(afterCounts.Keys);
            foreach (var key in allKeys)
            {
                beforeCounts.TryGetValue(key, out int bCount);
                afterCounts.TryGetValue(key, out int aCount);
                if (bCount != aCount)
                {
                    delta.ChangedItems.Add(new DeltaItemEntry
                    {
                        ItemType = key.type,
                        PrefixId = key.prefix,
                        NewStack = aCount // 0 means item fully removed from disk
                    });
                }
            }

            // For unique (mod data) items: send the full after-state as part of the delta.
            // This is simpler than diffing individual mod data blobs and covers all cases.
            delta.UniqueItemsAfter = afterUnique;

            return delta;
        }

        // Get or create disk data for a given ID and tier.
        public DiskData GetOrCreateDiskData(Guid diskId, DiskTier tier)
        {
            if (!_allDiskData.TryGetValue(diskId, out var data))
            {
                data = new DiskData
                {
                    DiskId = diskId,
                    Tier = tier,
                    Items = new List<StoredItemStack>()
                };
                _allDiskData[diskId] = data;
            }
            return data;
        }

        // Get disk data by ID. Returns null if not found.
        public DiskData GetDiskData(Guid diskId)
        {
            return _allDiskData.TryGetValue(diskId, out var data) ? data : null;
        }

        // Check if a disk ID exists in world data.
        public bool HasDiskData(Guid diskId) => _allDiskData.ContainsKey(diskId);

        // Fast item count lookup: returns itemType → total count across all given disks.
        // No object allocation beyond the dictionary. Use this instead of GetConsolidatedItems
        // when you only need counts (e.g. canCraft checks).
        public Dictionary<int, int> GetItemCounts(IEnumerable<Guid> diskIds)
        {
            var counts = new Dictionary<int, int>();
            foreach (var diskId in diskIds)
            {
                if (!_allDiskData.TryGetValue(diskId, out var disk))
                    continue;
                foreach (var stored in disk.Items)
                {
                    counts.TryGetValue(stored.ItemType, out int existing);
                    counts[stored.ItemType] = existing + stored.Stack;
                }
            }
            return counts;
        }

        // Get all items across multiple disks, consolidated by type+prefix.
        public List<ConsolidatedItem> GetConsolidatedItems(IEnumerable<Guid> diskIds)
        {
            var consolidated = new Dictionary<(int type, int prefix), ConsolidatedItem>();
            // Items that stand for themselves (UnloadedItems, disks, anything the game refuses to
            // stack) must never be merged — each gets its own grid slot.
            var uniqueEntries = new List<ConsolidatedItem>();

            foreach (var diskId in diskIds)
            {
                if (!_allDiskData.TryGetValue(diskId, out var disk))
                    continue;

                foreach (var stored in disk.Items)
                {
                    if (stored.IsUnique)
                    {
                        uniqueEntries.Add(new ConsolidatedItem
                        {
                            ItemType = stored.ItemType,
                            PrefixId = stored.PrefixId,
                            TotalCount = stored.Stack,
                            LatestInsertionOrder = stored.InsertionOrder,
                            SourceDisks = new HashSet<Guid> { diskId },
                            ModData = stored.ModData,
                            FullItemTag = stored.FullItemTag
                        });
                        continue;
                    }

                    var key = (stored.ItemType, stored.PrefixId);
                    if (!consolidated.TryGetValue(key, out var entry))
                    {
                        entry = new ConsolidatedItem
                        {
                            ItemType = stored.ItemType,
                            PrefixId = stored.PrefixId,
                            TotalCount = 0,
                            SourceDisks = new HashSet<Guid>()
                        };
                        consolidated[key] = entry;
                    }

                    entry.TotalCount += stored.Stack;
                    if (stored.InsertionOrder > entry.LatestInsertionOrder)
                        entry.LatestInsertionOrder = stored.InsertionOrder;
                    entry.SourceDisks.Add(diskId);
                }
            }

            return consolidated.Values.Concat(uniqueEntries).ToList();
        }

        // Insert an item across the given disks (tries each until inserted).
        // Returns leftover count.
        public int InsertItem(IEnumerable<Guid> diskIds, Item item)
        {
            if (item == null || item.IsAir)
                return 0;

            // Bump counters before insertion so the new InsertionOrder is strictly greater than any prior one
            _insertionCounter++;
            StorageVersion++;
            BackupSystem.MarkDirty();
            int remaining = item.stack;

            // Serialize the original item BEFORE any Clone() so GlobalItem data from
            // other mods (enchantments etc.) is captured intact. Clone() may not deep-copy
            // per-instance GlobalItem state, so serializing the clone can lose that data.
            var originalTag = ItemIO.Save(item);

            // Always pass the pre-serialized tag so the stack keeps every byte the original
            // item carried, whatever it later pools with.
            TagCompound tagToPreserve = originalTag;

            foreach (var diskId in diskIds)
            {
                if (!_allDiskData.TryGetValue(diskId, out var disk))
                    continue;

                // Clone with only the current remaining count so DiskData.InsertItem sees the right stack
                var tempItem = item.Clone();
                tempItem.stack = remaining;
                int before = remaining;
                remaining = disk.InsertItem(tempItem, _insertionCounter, tagToPreserve);
                if (remaining < before)
                    _modifiedTracker?.Add(diskId);

                if (remaining <= 0)
                    return 0;
            }

            return remaining;
        }

        // Returns true if the given item can be fully inserted across the given disks
        // without actually modifying them. Used to pre-check capacity before crafting.
        public bool HasRoomFor(IEnumerable<Guid> diskIds, Item item)
        {
            if (item == null || item.IsAir) return true;
            int remaining = item.stack;

            // An item that stands for itself never pools — only free slots can take it.
            bool isUnique = DiskData.IsUniqueItem(item);

            foreach (var diskId in diskIds)
            {
                if (!_allDiskData.TryGetValue(diskId, out var disk)) continue;

                if (!isUnique)
                {
                    foreach (var stored in disk.Items)
                    {
                        if (stored.ItemType == item.type && stored.PrefixId == item.prefix
                            && !stored.IsUnique && stored.Stack < item.maxStack)
                        {
                            remaining -= item.maxStack - stored.Stack;
                            if (remaining <= 0) return true;
                        }
                    }
                }

                int freeSlots = disk.MaxStacks - disk.UsedStacks;
                if (freeSlots > 0)
                {
                    remaining -= freeSlots * item.maxStack;
                    if (remaining <= 0) return true;
                }
            }
            return remaining <= 0;
        }

        // Extract an item from across multiple disks. A withdrawal onto the cursor or into the
        // player's inventory holds exactly one item, so the sweep stops at the first state boundary
        // and hands that draw straight back - what every UI and network caller has always seen.
        public Item ExtractItem(IEnumerable<Guid> diskIds, int itemType, int count, int prefixId = -1)
        {
            const int oneItemHandle = 1;
            List<Item> drawn = ExtractItemStacks(diskIds, itemType, count, prefixId, oneItemHandle);
            return drawn.Count == 0 ? new Item() : drawn[0];
        }

        // Drains up to `count` across the network in ONE sweep, one item per run of consecutive draws that
        // share mod state. A caller that can hold several items - a crafting step paying for itself
        // out of stacks that each stand for themselves - no longer walks every disk once per stack.
        public List<Item> ExtractItemStacks(IEnumerable<Guid> diskIds, int itemType, int count, int prefixId = -1)
            => ExtractItemStacks(diskIds, itemType, count, prefixId, int.MaxValue);

        private List<Item> ExtractItemStacks(IEnumerable<Guid> diskIds, int itemType, int count,
            int prefixId, int handleLimit)
        {
            var diskList = diskIds as List<Guid> ?? diskIds.ToList();
            var withdrawal = new DiskWithdrawal(this, diskList, itemType, prefixId);

            List<WithdrawalHandle> handles = NetworkWithdrawal.Drain(withdrawal, count, handleLimit);
            List<Item> drawn = withdrawal.BuildItems(handles);
            if (drawn.Count == 0)
                return drawn;

            StorageVersion++;
            BackupSystem.MarkDirty();
            return drawn;
        }

        // Binds the withdrawal sweep to real disks. Everything Terraria-shaped lives here - building
        // the item, reading NBT to decide whether two draws may share one - so the rule itself stays
        // in NetworkWithdrawal where it can be exercised without a live world.
        private sealed class DiskWithdrawal : IWithdrawalNetwork
        {
            private readonly StorageWorldSystem _storage;
            private readonly List<Guid>         _diskIds;
            private readonly int                _itemType;
            private readonly int                _prefixId;

            private readonly List<Item> _drawnItems = new();

            private Item        _previousDraw;
            private TagCompound _previousDrawState;
            private int         _drawnRunIndex;

            public DiskWithdrawal(StorageWorldSystem storage, List<Guid> diskIds, int itemType, int prefixId)
            {
                _storage = storage;
                _diskIds = diskIds;
                _itemType = itemType;
                _prefixId = prefixId;
            }

            public int DiskCount => _diskIds.Count;

            public DrawnUnits DrawPooled(int diskIndex, int amount)
            {
                if (!TryGetDisk(diskIndex, out DiskData disk))
                    return DrawnUnits.Nothing(diskIndex);

                Item extracted = disk.ExtractItem(_itemType, amount, _prefixId,
                    allowUniqueFallback: false, out _, out TagCompound modState);
                return Record(diskIndex, extracted, modState);
            }

            public DrawnUnits DrawStandalone(int diskIndex, int amount)
            {
                if (!TryGetDisk(diskIndex, out DiskData disk))
                    return DrawnUnits.Nothing(diskIndex);

                Item extracted = disk.ExtractItem(_itemType, amount, _prefixId,
                    allowUniqueFallback: true, out bool standaloneStack, out TagCompound modState);

                // Pooled stock is exhausted network-wide before this pass runs, so anything that is
                // not a stack standing for itself means the disk had nothing left to give. Should
                // that ever stop holding, the draw goes back to the network rather than to the one
                // disk, whose slot layout this draw may not match - dropping it would destroy it.
                if (!standaloneStack)
                {
                    if (!extracted.IsAir)
                        _storage.InsertItem(_diskIds, extracted);
                    return DrawnUnits.Nothing(diskIndex);
                }

                return Record(diskIndex, extracted, modState);
            }

            public void PutBack(DrawnUnits draw)
            {
                if (!TryGetDisk(draw.DiskIndex, out DiskData disk))
                    return;

                // Back into the disk whose slots this same draw just freed, so the insert cannot
                // come up short.
                disk.InsertItem(_drawnItems[draw.DrawIndex], ++_storage._insertionCounter);
                _drawnItems[draw.DrawIndex] = null;
            }

            // One item per handle, carrying the state of the draw that opened it and the units of
            // every draw folded into it.
            public List<Item> BuildItems(List<WithdrawalHandle> handles)
            {
                var items = new List<Item>(handles.Count);

                foreach (WithdrawalHandle handle in handles)
                {
                    Item item = _drawnItems[handle.Draws[0].DrawIndex];
                    item.stack = handle.Units;
                    items.Add(item);

                    // Only disks behind draws the sweep kept changed; one that was put back left its
                    // disk as it found it.
                    foreach (DrawnUnits draw in handle.Draws)
                        _storage._modifiedTracker?.Add(_diskIds[draw.DiskIndex]);
                }

                return items;
            }

            private bool TryGetDisk(int diskIndex, out DiskData disk)
                => _storage._allDiskData.TryGetValue(_diskIds[diskIndex], out disk);

            private DrawnUnits Record(int diskIndex, Item extracted, TagCompound modState)
            {
                if (extracted.IsAir)
                    return DrawnUnits.Nothing(diskIndex);

                int stateGroup = RunIndexOf(extracted, modState);
                _drawnItems.Add(extracted);
                return new DrawnUnits(diskIndex, _drawnItems.Count - 1, extracted.stack, stateGroup);
            }

            // Reduces "would folding these two discard anything" to an integer the sweep can
            // compare. It numbers RUNS of consecutive draws rather than distinct states, because
            // the sweep only ever weighs a draw against the handle it is holding open - a state
            // that comes back later opens a new handle either way (NW-09).
            //
            // Prefix counts as much as mod state: a withdrawal naming no prefix matches every stack
            // of the type, and one returned Item carries one prefix, so folding two would stamp the
            // first draw's prefix onto units that never had it. The tag comes from the disk that
            // just built it, so this costs no serialization.
            private int RunIndexOf(Item drawn, TagCompound modState)
            {
                if (_previousDraw != null)
                {
                    bool prefixChanged = _previousDraw.prefix != drawn.prefix;
                    bool modStateChanged = !DiskData.ModStateMatches(_previousDrawState, modState);

                    if (prefixChanged || modStateChanged)
                        _drawnRunIndex++;
                }

                _previousDraw = drawn;
                _previousDrawState = modState;
                return _drawnRunIndex;
            }
        }

        // Takes back units carrying exactly the state `stored` was inserted with, so recovering what
        // a crafting run conjured does not take the player's stack of the same type instead. A
        // matching stack larger than `refuseIfLargerThan` also holds units this run did not store,
        // so it is refused rather than taken whole.
        public Item ExtractStoredItem(IEnumerable<Guid> diskIds, Item stored, int refuseIfLargerThan)
        {
            if (stored == null || stored.IsAir || refuseIfLargerThan <= 0)
                return new Item();

            TagCompound modItemData = ModItemDataOf(stored);
            var fullItemTag = ItemIO.Save(stored);

            bool hasModItemData = modItemData != null;
            bool carriesModWrittenData = fullItemTag.ContainsKey("globalData");

            // Nothing to recognise it by, so there is nothing to be precise about: one plain unit is
            // as good as another and the caller's draw by type is already right.
            if (!StackIdentity.MustPreserveFullTag(hasModItemData, carriesModWrittenData))
                return new Item();

            // One sweep for the whole amount. The tags above cost a serialization each to build, and
            // asking per stored stack rebuilt them every time.
            Item recovered = new Item();
            int stillWanted = refuseIfLargerThan;

            foreach (var diskId in diskIds)
            {
                if (!_allDiskData.TryGetValue(diskId, out var disk))
                    continue;

                // A disk can hold several stacks of one state when the insert outgrew a slot, so it
                // is asked until it stops yielding. Folding them is safe here in a way it is not for
                // a general withdrawal: every stack matched the SAME tags, so no stack's state is
                // being stamped onto units from another.
                while (stillWanted > 0)
                {
                    var extracted = disk.ExtractStoredStack(stored.type, stored.prefix, modItemData,
                        fullItemTag, stillWanted);
                    if (extracted.IsAir)
                        break;

                    if (recovered.IsAir)
                        recovered = extracted;
                    else
                        recovered.stack += extracted.stack;

                    stillWanted -= extracted.stack;

                    StorageVersion++;
                    BackupSystem.MarkDirty();
                    _modifiedTracker?.Add(diskId);
                }

                if (stillWanted <= 0)
                    break;
            }

            return recovered;
        }

        private static TagCompound ModItemDataOf(Item item)
        {
            if (item.ModItem == null)
                return null;

            var tag = new TagCompound();
            item.ModItem.SaveData(tag);
            return tag.Count > 0 ? tag : null;
        }

        // Whether two live items are in the same state, on the same terms ExtractStoredStack matches
        // a stored stack on. Used by the refund to tell the product a run conjured from the player's
        // own stock of that type, which position alone cannot do: a product lands in the first disk
        // with room, ahead of stock the player holds on a later disk.
        public static bool ItemsShareStoredState(Item first, Item second)
        {
            if (first == null || second == null || first.IsAir || second.IsAir)
                return false;

            if (first.type != second.type || first.prefix != second.prefix)
                return false;

            TagCompound firstModItemData = ModItemDataOf(first);
            TagCompound secondModItemData = ModItemDataOf(second);
            if (!DiskData.ModItemDataMatches(firstModItemData, secondModItemData))
                return false;

            return DiskData.ModStateMatches(ItemIO.Save(first), ItemIO.Save(second));
        }

        // Extract a specific per-instance item (e.g. UnloadedItem) identified by its exact
        // ModData. Searches disks in order and returns the first matching stack.
        public Item ExtractItemWithModData(IEnumerable<Guid> diskIds, TagCompound modData)
        {
            foreach (var diskId in diskIds)
            {
                if (!_allDiskData.TryGetValue(diskId, out var disk))
                    continue;

                var extracted = disk.ExtractItemWithModData(modData);
                if (!extracted.IsAir)
                {
                    StorageVersion++;
                    BackupSystem.MarkDirty();
                    _modifiedTracker?.Add(diskId);
                    return extracted;
                }
            }
            return new Item();
        }

        // Extract a specific per-instance item identified by its exact FullItemTag.
        // Used for GlobalItem-backed items (e.g. Entropy enchantments) that have no ModData.
        public Item ExtractItemWithFullItemTag(IEnumerable<Guid> diskIds, TagCompound fullItemTag)
        {
            foreach (var diskId in diskIds)
            {
                if (!_allDiskData.TryGetValue(diskId, out var disk))
                    continue;

                var extracted = disk.ExtractItemWithFullItemTag(fullItemTag);
                if (!extracted.IsAir)
                {
                    StorageVersion++;
                    BackupSystem.MarkDirty();
                    _modifiedTracker?.Add(diskId);
                    return extracted;
                }
            }
            return new Item();
        }

        // Count total of a given item across multiple disks.
        public int CountItem(IEnumerable<Guid> diskIds, int itemType, int prefixId = -1)
        {
            int total = 0;
            foreach (var diskId in diskIds)
            {
                if (_allDiskData.TryGetValue(diskId, out var disk))
                    total += disk.CountItem(itemType, prefixId);
            }
            return total;
        }

        // Register a disk ID with a given tier (ensures data exists).
        public void RegisterDisk(Guid diskId, DiskTier tier)
        {
            GetOrCreateDiskData(diskId, tier);
        }

        // Get all disk data in the world.
        public IReadOnlyCollection<DiskData> GetAllDiskData() => _allDiskData.Values;

        // Drop the entry for a disk that has just left its Drive Bay, but only when it is empty and
        // no other bay still holds that GUID. Both arms are the safety argument: an entry with items
        // is somebody's storage, and DiskAccess.MayPruneDiskData explains why the weaker "no disk in
        // the world carries this id" rule cannot be used. Returns whether anything was dropped.
        public bool PruneEmptyDiskData(Guid diskId, bool anotherBayHoldsDisk)
        {
            if (!_allDiskData.TryGetValue(diskId, out var data))
                return false;

            if (!DiskAccess.MayPruneDiskData(data.UsedStacks, anotherBayHoldsDisk))
                return false;

            RemoveDiskData(diskId);
            RemoveDiskSeqNum(diskId);
            return true;
        }

        // Remove a disk's data entry (used when reassigning a blank disk's GUID during recovery).
        public void RemoveDiskData(Guid diskId)
        {
            if (_allDiskData.Remove(diskId))
                StorageVersion++;
        }

        // Move a disk's data from <paramref name="oldId"/> to <paramref name="newId"/>,
        // then delete the old entry.  Used by Disk Recovery so the original physical disk
        // (if it still exists) is left pointing at nothing and becomes empty.
        public void RemapDiskData(Guid oldId, Guid newId)
        {
            if (!_allDiskData.TryGetValue(oldId, out var data)) return;
            data.DiskId = newId;
            _allDiskData[newId] = data;
            _allDiskData.Remove(oldId);
            StorageVersion++;
            BackupSystem.MarkDirty();
        }

        // Archive a disk: removes its data from the world system and returns the stored items
        // so they can be embedded in the disk item's own NBT for cross-world transport.
        // After this call, the disk's GUID no longer exists in world storage.
        public List<StoredItemStack> ArchiveDisk(Guid diskId)
        {
            DBG($"ArchiveDisk: diskId={diskId.ToString()[..8]} found={_allDiskData.ContainsKey(diskId)} allDiskData=[{string.Join(", ", _allDiskData.Keys.Select(g => g.ToString()[..8]))}]");
            if (!_allDiskData.TryGetValue(diskId, out var data))
                return new List<StoredItemStack>();

            var items = new List<StoredItemStack>(data.Items);
            _allDiskData.Remove(diskId);
            DBG($"ArchiveDisk: removed {diskId.ToString()[..8]}, returning {items.Count} item stacks");
            StorageVersion++;
            BackupSystem.MarkDirty();
            return items;
        }

        // Defragments the given disks, in the order provided, by moving stacks from later disks into
        // free space on earlier ones. The sweep is DefragmentCore.Sweep; this resolves the GUIDs it
        // works over and supplies the rules that need Terraria.
        // Returns the GUIDs of every disk whose Items list was modified.
        public List<Guid> Defragment(List<Guid> orderedDiskIds)
        {
            var diskData = orderedDiskIds
                .Select(id => _allDiskData.TryGetValue(id, out var d) ? d : null)
                .Where(d => d != null)
                .ToList();

            var disks = new List<DefragmentDisk<StoredItemStack>>(diskData.Count);
            foreach (var data in diskData)
                disks.Add(new DefragmentDisk<StoredItemStack>(data.Items, data.MaxStacks));

            List<int> movedDiskIndices = DefragmentCore.Sweep(disks,
                new StoredStackRules(new Dictionary<int, int>()));

            // A disk named twice in the request is two indices for one GUID, so the ids are
            // deduplicated rather than the indices.
            var modified = new HashSet<Guid>();
            foreach (int diskIndex in movedDiskIndices)
                modified.Add(diskData[diskIndex].DiskId);

            if (modified.Count > 0)
            {
                StorageVersion++;
                BackupSystem.MarkDirty();
            }

            return modified.ToList();
        }

        // The live bindings for the sweep: every question about a stored stack that needs a
        // TagCompound or an Item, and nothing else. The sweep itself is Terraria-free and lives in
        // Common/DefragmentCore.cs, where it runs under test.
        private readonly struct StoredStackRules : IDefragmentRules<StoredItemStack>
        {
            // maxStack needs an Item to read, which is far too expensive to rebuild per stack.
            private readonly Dictionary<int, int> _maxStackByItemType;

            public StoredStackRules(Dictionary<int, int> maxStackByItemType)
            {
                _maxStackByItemType = maxStackByItemType;
            }

            public int GetItemType(StoredItemStack stack) => stack.ItemType;

            public int GetPrefixId(StoredItemStack stack) => stack.PrefixId;

            public int GetCount(StoredItemStack stack) => stack.Stack;

            public void SetCount(StoredItemStack stack, int count) => stack.Stack = count;

            public bool IsUnique(StoredItemStack stack) => DiskData.HasPerInstanceData(stack);

            public bool CanMerge(StoredItemStack target, StoredItemStack donor)
                => DiskData.CanMergeStacks(target, donor);

            public StoredItemStack CopyWithCount(StoredItemStack source, int count)
                => CopyStackWithCount(source, count);

            public int GetMaxStack(StoredItemStack stack)
            {
                if (_maxStackByItemType.TryGetValue(stack.ItemType, out int cached))
                    return cached;

                var tempItem = new Item();
                tempItem.SetDefaults(stack.ItemType);
                _maxStackByItemType[stack.ItemType] = tempItem.maxStack;
                return tempItem.maxStack;
            }
        }

        // A relocated stack keeps everything that identifies it, or defragmenting would quietly
        // strip the per-instance data the identity rule just protected.
        private static StoredItemStack CopyStackWithCount(StoredItemStack source, int count)
        {
            var copy = new StoredItemStack
            {
                ItemType       = source.ItemType,
                Stack          = count,
                PrefixId       = source.PrefixId,
                InsertionOrder = source.InsertionOrder,
                ModData        = source.ModData,
                FullItemTag    = source.FullItemTag
            };

            copy.CopyIdentityVerdictFrom(source);
            return copy;
        }

        private static void DBG(string msg)
        {
            var path = Requisition.DebugLogPath;
            if (path == null) return;
            try
            {
                using var fs = new System.IO.FileStream(path, System.IO.FileMode.Append, System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite);
                using var sw = new System.IO.StreamWriter(fs);
                sw.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}][net={Terraria.Main.netMode}] {msg}");
            }
            catch { }
        }

        // Register a disk with a pre-existing item list (used when an unarchived disk is
        // first inserted into a Drive Bay to restore its items into this world).
        // Returns false when the GUID already names a disk. Restoring replaces everything the GUID
        // held, and the GUID reaching here came off a disk item that crossed the network, so this
        // may only ever create a disk - never overwrite one.
        public bool RegisterDiskWithItems(Guid diskId, DiskTier tier, List<StoredItemStack> items)
        {
            bool worldAlreadyHasDisk = _allDiskData.ContainsKey(diskId);
            if (!DiskClaim.MayRestoreArchivedItems(worldAlreadyHasDisk))
                return false;

            var data = new DiskData
            {
                DiskId = diskId,
                Tier = tier,
                Items = new List<StoredItemStack>(items)
            };
            _allDiskData[diskId] = data;
            StorageVersion++;
            BackupSystem.MarkDirty();
            return true;
        }

        // Applies a DiskData received from the server, replacing any local copy.
        // Used by clients in multiplayer to stay in sync with the authoritative server state.
        // Guarded here rather than at each caller: this replaces a whole disk, so a handler that
        // forgets its own netMode check must not be able to let a client rewrite server storage.
        public void ApplyDiskDataFromNetwork(DiskData data)
        {
            if (Terraria.Main.netMode != Terraria.ID.NetmodeID.MultiplayerClient)
                return;

            if (data == null)
                return;

            _allDiskData[data.DiskId] = data;
            StorageVersion++;
        }

        // Upgrade an existing disk's tier in-place, preserving all stored items.
        public void UpgradeDisk(Guid diskId, DiskTier newTier)
        {
            if (_allDiskData.TryGetValue(diskId, out var data))
            {
                // Upgrades only ever go up. The stale-tier case this exists to correct is a disk
                // whose world entry lags the item, never the reverse - and a disk item claiming a
                // lower tier came off the network, where lowering a disk below what it already
                // holds is a way to break it rather than a way to fix it.
                if (newTier < data.Tier)
                    return;

                // No-op when the tier is unchanged. This is called for every disk on every disk-
                // connection refresh (~every 2s while a Terminal is open) to defensively sync the
                // tier; bumping StorageVersion / marking the backup dirty here forced a full UI
                // refresh every 2s and continuously reset the backup write timer. Only react to a
                // real tier change.
                if (data.Tier == newTier)
                    return;

                data.Tier = newTier;
                StorageVersion++;
                BackupSystem.MarkDirty();
            }
        }

        // Assign an existing disk's data to a new Guid (for disk restoration).
        public bool RestoreDisk(Guid targetDiskId, Guid sourceDiskId)
        {
            if (!_allDiskData.TryGetValue(sourceDiskId, out var data))
                return false;

            // Re-map the source disk's data under the target GUID so the physical item
            // (which carries targetDiskId) now points to the correct stored items
            _allDiskData[targetDiskId] = data;
            data.DiskId = targetDiskId;
            StorageVersion++;
            BackupSystem.MarkDirty();
            return true;
        }

        // Replaces all disk data in-place from a backup tag. Used by the server restore command
        // for immediate restore without a world reload.
        public void RestoreFromTag(TagCompound tag)
        {
            _allDiskData.Clear();
            _insertionCounter = tag.ContainsKey("insertionCounter") ? tag.GetLong("insertionCounter") : 0;

            if (tag.ContainsKey("disks"))
            {
                foreach (var diskTag in tag.GetList<TagCompound>("disks"))
                {
                    var data = DiskData.Load(diskTag);
                    _allDiskData[data.DiskId] = data;
                }
            }

            StorageVersion++;
        }

        public override void SaveWorldData(TagCompound tag)
        {
            var diskList = _allDiskData.Values.Select(d => d.Save()).ToList();
            tag["disks"] = diskList;
            tag["insertionCounter"] = _insertionCounter;

            DumpDiskData();
        }

        private void DumpDiskData()
        {
            try
            {
                string dumpDir = System.IO.Path.Combine(
                    AppContext.BaseDirectory, "tModLoader-Logs", "Requisition-DiskDumps");
                System.IO.Directory.CreateDirectory(dumpDir);

                foreach (var data in _allDiskData.Values)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"GUID:     {data.DiskId}");
                    sb.AppendLine($"Tier:     {data.Tier}");
                    sb.AppendLine($"Capacity: {data.MaxStacks} stacks");
                    sb.AppendLine($"Used:     {data.UsedStacks} / {data.MaxStacks} stacks");
                    sb.AppendLine($"Saved:    {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine();
                    sb.AppendLine("Items:");
                    if (data.Items.Count == 0)
                    {
                        sb.AppendLine("  (empty)");
                    }
                    else
                    {
                        foreach (var item in data.Items)
                        {
                            var name = Terraria.Lang.GetItemNameValue(item.ItemType);
                            sb.AppendLine($"  [{item.Stack,5}x] {name} (id={item.ItemType} prefix={item.PrefixId} order={item.InsertionOrder})");
                        }
                    }

                    string filePath = System.IO.Path.Combine(dumpDir, $"{data.DiskId}.txt");
                    System.IO.File.WriteAllText(filePath, sb.ToString());
                }
            }
            catch (System.Exception ex)
            {
                Terraria.ModLoader.ModContent.GetInstance<Requisition>()?.Logger.Warn($"DumpDiskData failed: {ex.Message}");
            }
        }

        public override void LoadWorldData(TagCompound tag)
        {
            var restoreTag = BackupSystem.TryConsumeRestoreOverride();
            if (restoreTag != null)
            {
                ModContent.GetInstance<Requisition>()?.Logger.Info("[Requisition] Restoring storage from backup.");
                tag = restoreTag;
            }

            _allDiskData.Clear();
            // Restore the insertion counter so newly inserted items always get a higher order value
            _insertionCounter = tag.ContainsKey("insertionCounter") ? tag.GetLong("insertionCounter") : 0;

            if (tag.ContainsKey("disks"))
            {
                var diskTags = tag.GetList<TagCompound>("disks");
                foreach (var diskTag in diskTags)
                {
                    var data = DiskData.Load(diskTag);
                    _allDiskData[data.DiskId] = data;
                }
            }

            // Purge empty disk entries on load. Disks in Drive Bays will re-register
            // themselves via GetInsertedDiskIds the next time they are accessed.
            var emptyKeys = _allDiskData
                .Where(kvp => kvp.Value.UsedStacks == 0)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in emptyKeys)
                _allDiskData.Remove(key);
        }

        public override void OnWorldUnload()
        {
            _allDiskData.Clear();
            _diskSeqNums.Clear();
            _preModificationSnapshot = null;
            _modifiedTracker = null;
            Helpers.AdjTileHelper.ClearCache();
        }
    }

    // Represents the combined totals of a single item type+prefix pair aggregated
    // across all queried disks. Used by the Terminal UI to show one row per unique item.
    public class ConsolidatedItem
    {
        public int ItemType { get; set; }
        public int PrefixId { get; set; }
        //Sum of all stack counts for this item across every source disk.
        public int TotalCount { get; set; }
        // The highest InsertionOrder among all individual stacks of this item.
        // Used for "recently added" sort — a higher value means more recently inserted.
        public long LatestInsertionOrder { get; set; }
        //GUIDs of the disks that contributed to this consolidated entry.
        public HashSet<Guid> SourceDisks { get; set; } = new();
        // For per-instance items (UnloadedItems, items with unique NBT), the exact ModData
        // of the specific stack this entry represents. Used to extract the right instance.
        // Null for regular stackable items.
        public TagCompound ModData { get; set; }
        // Full ItemIO-serialized tag for items whose per-instance data comes from GlobalItem
        // (e.g. enchantment mods). Non-null means this item must be extracted and restored
        // via ItemIO.Load rather than reconstructed from type/prefix alone.
        public TagCompound FullItemTag { get; set; }
    }
}
