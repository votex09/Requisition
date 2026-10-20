using System;
using System.Collections.Generic;
using TerraStorage.Common;

namespace TerraStorage.Tests
{
    // A stored stack with no Terraria in it. ModState stands in for the mod-written bytes
    // DiskData.ModStateMatches byte-compares: two stacks of one item carrying different state are
    // the same item but must not be folded, because folding would discard one of them.
    public sealed class FakeStack
    {
        public int ItemType;
        public int PrefixId;
        public int Count;
        public int MaxStack = 99;
        public bool IsUnique;
        public string ModState = "plain";

        public FakeStack Copy(int count) => new FakeStack
        {
            ItemType = ItemType,
            PrefixId = PrefixId,
            Count    = count,
            MaxStack = MaxStack,
            IsUnique = IsUnique,
            ModState = ModState
        };
    }

    // The Terraria-free half of what DiskData tells the defragment sweep.
    //
    // CanMerge mirrors DiskData.CanMergeStacks - StacksWith AND ModStateMatches - because the sweep
    // must never be able to reach the same verdict from the (ItemType, PrefixId) it can see. That is
    // what makes DG-06 and DG-18 able to express issues 04 and 24: the state lives here, out of the
    // sweep's reach, exactly as a TagCompound does in the live game.
    public sealed class FakeDefragmentRules : IDefragmentRules<FakeStack>
    {
        // Every pair the sweep asked about, in order. The falsifiable form of "the index only
        // narrows which stacks the rule is asked about, and never answers for it".
        public List<(FakeStack Target, FakeStack Donor)> AskedPairs { get; } = new();

        public int CopyWithCountCalls;

        private List<FakeStack> _removeFromList;
        private int _removeAtSlot = -1;
        private FakeStack _removeWhenTouched;

        // Drops a stack out of a disk the moment the sweep reads the given stack's count, so a slot
        // the merge index recorded goes stale mid-sweep. Nothing does this today; the guards exist
        // so that if anything ever does, the cost is a missed merge and never a miscredit.
        //
        // The trigger is GetCount rather than CanMerge on purpose. Hanging it off the merge rule
        // made the test vacuous against the one mutation it exists to catch: deleting the rule's
        // say also deleted the call that sprang the trap, so the disk never shrank and the
        // assertion passed. GetCount is read for every candidate whatever the rule answers.
        public void RemoveSlotWhenStackIsWeighed(List<FakeStack> from, int slotIndex, FakeStack trigger)
        {
            _removeFromList = from;
            _removeAtSlot = slotIndex;
            _removeWhenTouched = trigger;
        }

        public int GetItemType(FakeStack stack) => stack.ItemType;

        public int GetPrefixId(FakeStack stack) => stack.PrefixId;

        public int GetCount(FakeStack stack)
        {
            if (ReferenceEquals(stack, _removeWhenTouched))
                ApplyPendingRemoval();

            return stack.Count;
        }

        public void SetCount(FakeStack stack, int count) => stack.Count = count;

        public bool IsUnique(FakeStack stack) => stack.IsUnique;

        public int GetMaxStack(FakeStack stack) => stack.MaxStack;

        public FakeStack CopyWithCount(FakeStack source, int count)
        {
            CopyWithCountCalls++;
            return source.Copy(count);
        }

        public bool CanMerge(FakeStack target, FakeStack donor)
        {
            AskedPairs.Add((target, donor));

            if (target.ItemType != donor.ItemType || target.PrefixId != donor.PrefixId)
                return false;

            if (target.IsUnique || donor.IsUnique)
                return false;

            return target.ModState == donor.ModState;
        }

        private void ApplyPendingRemoval()
        {
            if (_removeAtSlot < 0)
                return;

            List<FakeStack> from = _removeFromList;
            int slot = _removeAtSlot;

            _removeFromList = null;
            _removeAtSlot = -1;
            _removeWhenTouched = null;

            if (slot < from.Count)
                from.RemoveAt(slot);
        }
    }

    // Fixture helpers, so a scenario reads as the disk layout it is rather than as list plumbing.
    public static class FakeDisks
    {
        public static DefragmentDisk<FakeStack> Disk(int capacity, params FakeStack[] stacks)
            => new DefragmentDisk<FakeStack>(new List<FakeStack>(stacks), capacity);

        // Two disk entries over ONE backing list - a disk list that names the same disk twice.
        public static DefragmentDisk<FakeStack> Alias(DefragmentDisk<FakeStack> disk)
            => new DefragmentDisk<FakeStack>(disk.Items, disk.Capacity);

        public static FakeStack Stack(int itemType, int count, string modState = "plain",
            int maxStack = 99, int prefixId = 0)
            => new FakeStack
            {
                ItemType = itemType,
                Count    = count,
                ModState = modState,
                MaxStack = maxStack,
                PrefixId = prefixId
            };

        public static FakeStack Unique(int itemType, int count, int maxStack = 99, int prefixId = 0)
            => new FakeStack
            {
                ItemType = itemType,
                Count    = count,
                MaxStack = maxStack,
                PrefixId = prefixId,
                IsUnique = true,
                ModState = "unique" + Guid.NewGuid()
            };

        public static int TotalUnits(params DefragmentDisk<FakeStack>[] disks)
        {
            int total = 0;
            foreach (DefragmentDisk<FakeStack> disk in disks)
            {
                foreach (FakeStack stack in disk.Items)
                    total += stack.Count;
            }
            return total;
        }

        public static string Layout(DefragmentDisk<FakeStack> disk)
        {
            var parts = new List<string>();
            foreach (FakeStack stack in disk.Items)
                parts.Add($"t{stack.ItemType}x{stack.Count}");
            return string.Join(",", parts);
        }
    }
}
