using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Terraria;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace TerraStorage.Common
{
    // Holds all persistent data for a single Storage Disk: its unique identity,
    // capacity tier, and the list of item stacks currently stored on it.
    // 
    public class DiskData
    {
        //Unique identifier used to look up this disk in <see cref="TerraStorage.Systems.StorageWorldSystem"/>.
        public Guid DiskId { get; set; }
        public DiskTier Tier { get; set; }
        public List<StoredItemStack> Items { get; set; } = new();

        //Maximum number of distinct item stacks this disk can hold, determined by its tier.
        public int MaxStacks => Tier.GetCapacity();
        //Number of item stacks currently occupying slots on this disk.
        public int UsedStacks => Items.Count;
        public bool IsFull => UsedStacks >= MaxStacks;

        // Try to insert an item into this disk. Returns the leftover count (0 if fully inserted).
        // 
        // Optional full ItemIO tag captured from the original item (before any
        // Clone() call) by the caller. Supplying it ensures that GlobalItem data from
        // other mods (e.g. enchantment mods using GlobalItem.SaveData) is preserved,
        // because Item.Clone() may not deep-copy per-instance GlobalItem state.
                public int InsertItem(Item item, long insertionOrder = 0, TagCompound preSerializedTag = null)
        {
            if (item == null || item.IsAir)
                return 0;

            int remaining = item.stack;

            // Capture mod item NBT up front — we need it before the merge step to decide
            // whether merging is safe. For most items this will be null; for mod items with
            // custom per-instance data (e.g. a disk's GUID) it preserves that data so it
            // can be restored on extraction.
            TagCompound modData = null;
            if (item.ModItem != null)
            {
                var tempTag = new TagCompound();
                item.ModItem.SaveData(tempTag);
                if (tempTag.Count > 0)
                    modData = tempTag;
            }

            // Always capture the full serialized tag so globalData (enchantments from mods,
            // e.g. CalamityGlobalItem, Entropy enchantments) is preserved on extraction.
            var fullSave = preSerializedTag ?? ItemIO.Save(item);
            bool carriesModWrittenData = fullSave.ContainsKey("globalData");
            TagCompound fullItemTag = StackIdentity.MustPreserveFullTag(modData != null, carriesModWrittenData)
                ? fullSave : null;
            bool incomingIsUnique = IsUniqueItem(item, modData);
            bool anyFold = false;

            // Merge on the game's own stacking rule, the one a chest and the player inventory use.
            // Mod-written bytes ride along on every item in a modded world and say nothing about
            // whether two of them are the same thing.
            foreach (var stored in Items)
            {
                if (stored.Matches(item.type, item.prefix) && stored.Stack < item.maxStack
                    && stored.StacksWith(item, incomingIsUnique))
                {
                    int canAdd = Math.Min(remaining, item.maxStack - stored.Stack);
                    if (!ModStateMatches(stored.FullItemTag, fullItemTag))
                    {
                        stored.FoldInModState(item, canAdd);
                        anyFold = true;
                    }

                    stored.Stack += canAdd;
                    if (insertionOrder > 0)
                        stored.InsertionOrder = insertionOrder;
                    remaining -= canAdd;
                    if (remaining <= 0)
                        return 0;
                }
            }

            // A fold hands the incoming item to the mod, which may have drained state out of it.
            // Whatever is left over has to be stored as it is now, not as it arrived.
            if (anyFold)
                fullItemTag = StackIdentity.MustPreserveFullTag(modData != null, carriesModWrittenData)
                    ? ItemIO.Save(item) : null;

            // Add new stacks
            while (remaining > 0 && !IsFull)
            {
                int stackSize = Math.Min(remaining, item.maxStack);
                Items.Add(new StoredItemStack
                {
                    ItemType = item.type,
                    Stack = stackSize,
                    PrefixId = item.prefix,
                    InsertionOrder = insertionOrder,
                    ModData = modData,
                    FullItemTag = fullItemTag
                });
                remaining -= stackSize;
            }

            return remaining;
        }

        public Item ExtractItem(int itemType, int count, int prefixId = -1)
            => ExtractItem(itemType, count, prefixId, true, out _, out _);

        public Item ExtractItem(int itemType, int count, int prefixId, bool allowUniqueFallback, out bool uniqueStack)
            => ExtractItem(itemType, count, prefixId, allowUniqueFallback, out uniqueStack, out _);

        // Extract up to 'count' of the given item type. Returns the items extracted.
        // uniqueStack reports that the result carries per-instance mod data and therefore stands for
        // exactly the one stack it came from — callers must not fold any other count into it.
        // allowUniqueFallback lets a caller that is already carrying plain items refuse the fallback,
        // so a unique stack is never pulled out only to be mixed in with something else.
        // modState is the tag the result carries, so a caller drawing from several disks can tell
        // whether folding this into what it already holds would discard anything.
        public Item ExtractItem(int itemType, int count, int prefixId, bool allowUniqueFallback,
            out bool uniqueStack, out TagCompound modState)
        {
            int extracted = 0;
            var toRemove = new List<StoredItemStack>();
            TagCompound extractedModData = null;
            TagCompound extractedFullTag = null;
            int extractedPrefixId = prefixId;

            // Which stacks to draw from, and whether the unique fallback applies, is decided by
            // StackSelection so the rule can be asserted without NBT. Everything below just carries
            // that plan out.
            var matching = MatchingSlots(itemType, prefixId);
            var draws = StackSelection.PlanWithdrawal(matching, count, allowUniqueFallback, out uniqueStack);

            // Every stack in the plan merges with the one that opened it - PlanWithdrawal ends its
            // pass at the first that does not - so the opening stack speaks for all of them. There
            // used to be an after-the-fact check here that dropped the state when the draws
            // disagreed, which handed a mixed withdrawal back with NO state at all: issue 05's harm
            // inverted. The prefix comes off the same stack, because a request asking by "any
            // prefix" cannot say which one the units it is getting actually carry.
            if (draws.Count > 0)
            {
                var runOpener = Items[draws[0].Index];
                extractedModData = runOpener.ModData;
                extractedFullTag = runOpener.FullItemTag;
                extractedPrefixId = runOpener.PrefixId;
            }

            foreach (var draw in draws)
            {
                var stored = Items[draw.Index];
                stored.Stack -= draw.Count;
                extracted += draw.Count;

                if (stored.Stack <= 0)
                    toRemove.Add(stored);
            }

            foreach (var r in toRemove)
                Items.Remove(r);

            modState = extractedFullTag;

            if (extracted == 0)
                return new Item();

            Item result;
            if (extractedFullTag != null)
            {
                // Restore full item including GlobalItem data from other mods.
                result = ItemIO.Load(extractedFullTag);
                result.stack = extracted;
            }
            else
            {
                result = new Item();
                result.SetDefaults(itemType);
                result.stack = extracted;
                if (extractedPrefixId > 0)
                    result.Prefix(extractedPrefixId);

                // Restore mod item data (e.g. the DiskId GUID).
                if (extractedModData != null && result.ModItem != null)
                    result.ModItem.LoadData(extractedModData);
            }

            return result;
        }

        // The stacks a withdrawal of this item type may draw from, in storage order, reduced to
        // what the selection rules need.
        //
        // StateGroup numbers the RUNS of consecutive stacks that merge into one another, so equal
        // numbers mean CanMergeStacks - the same rule defragmenting asks, prefix and mod state
        // together. Numbering runs rather than interning every distinct state keeps this to one
        // comparison per stack: a request naming no prefix matches every stack of the type, and a
        // drive bay's worth of them would otherwise each be weighed against every state seen so far.
        //
        // Only pooled stacks are weighed at all. One that stands for itself is taken alone, so it
        // belongs to no run and is transparent to the stacks either side of it.
        private List<StackSlot> MatchingSlots(int itemType, int prefixId)
        {
            var matching = new List<StackSlot>();
            StoredItemStack previousPooled = null;
            int runIndex = 0;

            for (int index = 0; index < Items.Count; index++)
            {
                var stored = Items[index];
                if (!stored.Matches(itemType, prefixId))
                    continue;

                bool standsForItself = HasPerInstanceData(stored);
                if (!standsForItself)
                {
                    bool opensANewRun = previousPooled != null && !CanMergeStacks(previousPooled, stored);
                    if (opensANewRun)
                        runIndex++;

                    previousPooled = stored;
                }

                matching.Add(new StackSlot
                {
                    Index = index,
                    Stack = stored.Stack,
                    IsUnique = standsForItself,
                    StateGroup = runIndex
                });
            }

            return matching;
        }

        // Extract the stack a crafting run stored, identified by everything that makes it that stack
        // rather than one like it: its type and prefix, the mod item data it carries, and the
        // mod-written state riding on it.
        //
        // Matching on ModData alone is not enough for this. It carries no type, so a different item
        // whose mod wrote the same bytes matches - StorageDiskBase writes {"archived": true} and
        // nothing else for an archived empty disk, which is every tier of them. And it says nothing
        // about globalData, so the player's enchanted copy answers for the plain one this run made,
        // which is the whole of what recovering by handle exists to avoid.
        //
        // refuseIfLargerThan skips a match holding more units than the caller can account for: a
        // stack can be born larger than a later partial recovery asks for, and taking the difference
        // destroys units the player owned.
        public Item ExtractStoredStack(int itemType, int prefixId, TagCompound modData,
            TagCompound fullItemTag, int refuseIfLargerThan)
        {
            StoredItemStack match = null;

            foreach (var stored in Items)
            {
                // Cheapest first: type and prefix are two integer compares and reject almost
                // everything, where the two below deserialize and walk tag trees.
                if (!stored.Matches(itemType, prefixId))
                    continue;

                if (stored.Stack > refuseIfLargerThan)
                    continue;

                if (!ModItemDataMatches(stored.ModData, modData))
                    continue;

                if (!ModStateMatches(stored.FullItemTag, fullItemTag))
                    continue;

                match = stored;
                break;
            }

            if (match == null)
                return new Item();

            Items.Remove(match);
            return BuildExtractedItem(match);
        }

        public static bool ModItemDataMatches(TagCompound stored, TagCompound target)
        {
            if (stored == null && target == null)
                return true;
            if (stored == null || target == null)
                return false;

            return TagCompoundEquals(stored, target);
        }

        private static Item BuildExtractedItem(StoredItemStack stack)
        {
            if (stack.FullItemTag != null)
            {
                var restored = ItemIO.Load(stack.FullItemTag);
                restored.stack = stack.Stack;
                return restored;
            }

            var result = new Item();
            result.SetDefaults(stack.ItemType);
            result.stack = stack.Stack;
            if (stack.PrefixId > 0)
                result.Prefix(stack.PrefixId);
            if (stack.ModData != null && result.ModItem != null)
                result.ModItem.LoadData(stack.ModData);

            return result;
        }

        // Extract the specific per-instance stack whose ModData matches <paramref name="targetModData"/>
        // byte-for-byte. Used to pull the exact UnloadedItem (or other unique item) the user clicked.
        public Item ExtractItemWithModData(TagCompound targetModData)
        {
            StoredItemStack match = null;
            foreach (var stored in Items)
            {
                if (stored.ModData != null && TagCompoundEquals(stored.ModData, targetModData))
                {
                    match = stored;
                    break;
                }
            }

            if (match == null)
                return new Item();

            Items.Remove(match);

            // Through the shared builder: constructing the item here by hand skipped the
            // FullItemTag branch, so a stack carrying BOTH mod item data and mod-written state came
            // back with the state stripped. Three encodings of "turn a stored stack into an Item"
            // is how one of them keeps an old rule - the shape of 23a, 23b and 23c.
            return BuildExtractedItem(match);
        }

        // Extract the specific per-instance stack whose FullItemTag matches <paramref name="targetFullTag"/>
        // byte-for-byte. Used for items with GlobalItem data (e.g. Entropy enchantments) that have no ModData.
        // 
        public Item ExtractItemWithFullItemTag(TagCompound targetFullTag)
        {
            StoredItemStack match = null;
            foreach (var stored in Items)
            {
                if (stored.FullItemTag != null && TagCompoundEquals(stored.FullItemTag, targetFullTag))
                {
                    match = stored;
                    break;
                }
            }

            if (match == null)
                return new Item();

            Items.Remove(match);
            return BuildExtractedItem(match);
        }

        private static bool TagCompoundEquals(TagCompound a, TagCompound b)
        {
            if (a.Count != b.Count) return false;
            foreach (var kv in a)
            {
                if (!b.ContainsKey(kv.Key)) return false;
                if (!TagValueEquals(kv.Value, b[kv.Key])) return false;
            }
            return true;
        }

        private static bool TagValueEquals(object a, object b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.GetType() != b.GetType()) return false;
            if (a is TagCompound ta && b is TagCompound tb)
                return TagCompoundEquals(ta, tb);
            if (a is IList<TagCompound> la && b is IList<TagCompound> lb)
            {
                if (la.Count != lb.Count) return false;
                for (int i = 0; i < la.Count; i++)
                    if (!TagCompoundEquals(la[i], lb[i])) return false;
                return true;
            }
            if (a is byte[] ba && b is byte[] bb)
                return ba.SequenceEqual(bb);
            if (a is int[] ia && b is int[] ib)
                return ia.SequenceEqual(ib);
            return a.Equals(b);
        }

        // True if this stack stands for one particular item and may never be merged with a plain
        // one; anything that reorganises stacks must ask this first.
        public static bool HasPerInstanceData(StoredItemStack stack) => stack.IsUnique;

        // True if two stored stacks are the same item identity and may be merged. Defragmenting
        // asks this for every pair it considers, so stacks carrying different mod state are kept
        // apart rather than folded: folding them would need the game's OnStack hook, and paying for
        // that inside an O(n^2) sweep is not worth what it buys.
        public static bool CanMergeStacks(StoredItemStack a, StoredItemStack b)
            => a.StacksWith(b) && ModStateMatches(a.FullItemTag, b.FullItemTag);

        // Whether two stacks carry the same mod-written state, so folding one into the other loses
        // nothing. Not an identity test - that is the game's job - but a "would anything be
        // discarded" test, and byte equality is exactly the right answer to that.
        public static bool ModStateMatches(TagCompound first, TagCompound second)
        {
            // One tag cannot differ from itself. This catches two stacks with no tag at all, which
            // is every comparison in an unmodded world and none in a heavily modded one, and two
            // stacks sharing a tag object - a split copy weighed against its source, which only
            // meet on a second defragment before the world is next saved.
            if (ReferenceEquals(first, second))
                return true;

            bool firstHas = first?.ContainsKey("globalData") == true;
            bool secondHas = second?.ContainsKey("globalData") == true;
            if (firstHas != secondHas)
                return false;
            if (!firstHas)
                return true;

            try
            {
                // Wrapping each blob in a TagCompound only to compare the pair costs two dictionary
                // allocations, and for a single key TagCompoundEquals reduces to exactly this.
                // Defragmenting asks this question hundreds of thousands of times in one press.
                return TagValueEquals(first["globalData"], second["globalData"]);
            }
            catch
            {
                return false;
            }
        }

        // Whether a live item stands for one particular item rather than so many units of a type.
        public static bool IsUniqueItem(Item item, TagCompound modData)
        {
            var plain = PlainItemCache.Get(item.type, item.prefix);
            return StackIdentity.IsUnique(modData != null, !StoredItemStack.GameAllowsStacking(plain, item));
        }

        public static bool IsUniqueItem(Item item)
        {
            TagCompound modData = null;
            if (item.ModItem != null)
            {
                var tag = new TagCompound();
                item.ModItem.SaveData(tag);
                if (tag.Count > 0)
                    modData = tag;
            }

            return IsUniqueItem(item, modData);
        }

        // Count how many of a given item type are stored.
        public int CountItem(int itemType, int prefixId = -1)
        {
            int total = 0;
            foreach (var s in Items)
            {
                if (s.Matches(itemType, prefixId))
                    total += s.Stack;
            }
            return total;
        }

        // Compact binary serialization for network packets. ~18 bytes/stack vs ~373 bytes
        // with the TagCompound world-save format.
        public void WriteNet(BinaryWriter writer)
        {
            writer.Write(DiskId.ToByteArray());
            writer.Write((byte)Tier);
            writer.Write(Items.Count);
            foreach (var item in Items)
                item.WriteNet(writer);
        }

        // Deserializes a compact network-format disk written by <see cref="WriteNet"/>.
        // Null when the packet cannot be describing a real disk. Unlike the archived-items list this
        // reads straight from the packet rather than from a bounded sub-stream, so carrying on after
        // a bad count would read every following disk in the packet from a meaningless offset.
        public static DiskData ReadNet(BinaryReader reader)
        {
            var diskId = new Guid(reader.ReadBytes(16));
            byte tierValue = reader.ReadByte();

            // The tier indexes a fixed capacity table, so a byte outside the enum throws on the
            // first read of MaxStacks. An unrecognised tier is treated as the smallest one.
            bool tierIsKnown = Enum.IsDefined(typeof(DiskTier), (int)tierValue);
            var tier = tierIsKnown ? (DiskTier)tierValue : DiskTier.Tier1;

            int count = reader.ReadInt32();

            // Bounded by the largest tier rather than by this packet's own tier: a disk whose tier
            // was wrongly lowered still legitimately reports the stacks it already holds, and
            // refusing those would blank an honest disk on every client.
            if (!WireCount.FitsDiskCapacity(count, LargestDiskCapacity))
                return null;

            var data = new DiskData
            {
                DiskId = diskId,
                Tier = tier,
                Items = new List<StoredItemStack>(count)
            };
            for (int i = 0; i < count; i++)
                data.Items.Add(StoredItemStack.ReadNet(reader));
            return data;
        }

        // The most stacks any disk can hold, whatever its tier.
        private static int LargestDiskCapacity => DiskTier.Tier6.GetCapacity();

        // Serializes this disk's GUID, tier, and all stored item stacks to a
        // see "TagCompound" for world-save persistence.
        public TagCompound Save()
        {
            return new TagCompound
            {
                ["guid"] = DiskId.ToByteArray(),
                ["tier"] = (int)Tier,
                ["items"] = Items.Select(i => i.Save()).ToList()
            };
        }

        // Deserializes a DiskData from a TagCompound,
        // reconstructing the GUID, tier, and item stacks.
        public static DiskData Load(TagCompound tag)
        {
            var data = new DiskData
            {
                // GUIDs are stored as 16-byte arrays to avoid string parsing overhead
                DiskId = new Guid(tag.GetByteArray("guid")),
                Tier = (DiskTier)tag.GetInt("tier")
            };

            if (tag.ContainsKey("items"))
            {
                var itemTags = tag.GetList<TagCompound>("items");
                data.Items = itemTags.Select(StoredItemStack.Load).ToList();
            }

            return data;
        }
    }
}
