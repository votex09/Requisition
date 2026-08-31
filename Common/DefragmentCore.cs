using System;
using System.Collections.Generic;

namespace TerraStorage.Common
{
    // One disk as the defragment sweep sees it: the stacks it holds, and how many it may hold.
    // The list is the caller's own, so the sweep moves the real stacks rather than a copy of them.
    public readonly struct DefragmentDisk<TStack> where TStack : class
    {
        public readonly List<TStack> Items;
        public readonly int Capacity;

        public DefragmentDisk(List<TStack> items, int capacity)
        {
            Items = items;
            Capacity = capacity;
        }

        public bool IsFull => Items.Count >= Capacity;

        public int FreeSlots => Capacity - Items.Count;
    }

    // Everything about a stack that needs Terraria. Whether a stack carries per-instance data, and
    // whether two of them are the same item, needs NBT and a live Item and stays on DiskData;
    // deciding what to DO about that verdict does not, so the sweep itself lives here where it can
    // be exercised without Terraria.
    public interface IDefragmentRules<TStack> where TStack : class
    {
        int GetItemType(TStack stack);

        int GetPrefixId(TStack stack);

        int GetCount(TStack stack);

        void SetCount(TStack stack, int count);

        // Whether this stack stands for one particular item, so it may never be merged or split.
        bool IsUnique(TStack stack);

        int GetMaxStack(TStack stack);

        // The single authority on whether two stacks are the same item and may be folded together.
        // Issues 04 and 24 are both what happens when something other than this rule gets to answer.
        bool CanMerge(TStack target, TStack donor);

        // A stack carrying everything that identifies the source, holding `count` units. A
        // defragment that dropped per-instance data here is issue 04.
        TStack CopyWithCount(TStack source, int count);
    }

    public static class DefragmentCore
    {
        // Moves stacks from later disks into free space on earlier ones, returning the indices of
        // every disk whose contents changed, ascending.
        //
        // A stack that stands for itself moves whole into a free slot or stays put; a plain stack
        // tops up partial stacks of the same identity first and then takes fresh slots.
        // TRules is a type parameter rather than the interface so that a struct implementation is
        // specialised by the JIT and its calls inlined: the sweep asks it several times per candidate
        // stack, tens of millions of times at the supported maximum, and shared interface dispatch
        // there costs more than the merge rule it is calling.
        public static List<int> Sweep<TStack, TRules>(IReadOnlyList<DefragmentDisk<TStack>> disks,
            TRules rules) where TStack : class where TRules : IDefragmentRules<TStack>
        {
            // No null check on rules: TRules is usually a struct, where the comparison is constant
            // false and the guard would read as protection it does not give.
            if (disks == null)
                throw new ArgumentNullException(nameof(disks));

            // A flag per disk rather than a set: the sweep marks a disk on every single move, which at
            // the supported maximum is tens of thousands of marks, and a set pays a lookup for each.
            var diskWasModified = new bool[disks.Count];

            // One plan is built per donor stack, which at the supported maximum (40 disks x 2048
            // stacks) is tens of thousands. Allocating the buffers per stack churned hundreds of
            // megabytes through a single defrag, so they are hoisted and reused.
            var mergeTargets = new List<MergeTarget>();
            var movePlan = new DonorMovePlan();

            // Asking the merge rule about every stack on the target for every donor stack is
            // O(donors x target stacks), and at the supported maximum that froze the game thread for
            // a third of a second - issue 23i. The index keeps the target's stacks under the type
            // and prefix a merge needs anyway, so a donor is only asked about stacks that could
            // say yes.
            var mergeCandidates = new MergeCandidateIndex();

            for (int targetIndex = 0; targetIndex < disks.Count - 1; targetIndex++)
            {
                DefragmentDisk<TStack> target = disks[targetIndex];
                if (target.IsFull) continue;

                // Rebuilt per target, because a disk that donated to an earlier target arrives here
                // with its own stacks already moved.
                IndexTargetStacks(target, rules, mergeCandidates);

                for (int donorIndex = targetIndex + 1;
                     donorIndex < disks.Count && !target.IsFull;
                     donorIndex++)
                {
                    DefragmentDisk<TStack> donor = disks[donorIndex];

                    // The disk list arrives off the wire (NetworkHandler.HandleDefragRequest). A
                    // list naming one disk twice makes it its own donor, and then removing a stack
                    // from the donor shifts every slot the target just recorded - counts land on a
                    // stack of another item type. One disk yields one Items list, so comparing the
                    // lists catches an alias the disk indices cannot.
                    if (ReferenceEquals(target.Items, donor.Items)) continue;

                    // Descending, because moving a stack off the donor removes it and shifts
                    // everything above it down.
                    for (int donorSlot = donor.Items.Count - 1;
                         donorSlot >= 0 && !target.IsFull;
                         donorSlot--)
                    {
                        MoveOneStack(target, targetIndex, donor, donorIndex, donorSlot, rules,
                            mergeCandidates, mergeTargets, movePlan, diskWasModified);
                    }
                }
            }

            var modifiedDisks = new List<int>();
            for (int diskIndex = 0; diskIndex < diskWasModified.Length; diskIndex++)
            {
                if (diskWasModified[diskIndex])
                    modifiedDisks.Add(diskIndex);
            }

            return modifiedDisks;
        }

        private static void MoveOneStack<TStack, TRules>(DefragmentDisk<TStack> target, int targetIndex,
            DefragmentDisk<TStack> donor, int donorIndex, int donorSlot,
            TRules rules, MergeCandidateIndex mergeCandidates,
            List<MergeTarget> mergeTargets, DonorMovePlan movePlan, bool[] diskWasModified)
            where TStack : class where TRules : IDefragmentRules<TStack>
        {
            TStack stack = donor.Items[donorSlot];

            // What may merge with what is the identity rule's question, which needs NBT; what that
            // verdict then means for stacks and slots is StackSelection's, which does not. "Unique"
            // covers a mod's GlobalItem state as well as ModItem data: merging on type and prefix
            // alone destroyed enchantments one way and duplicated them the other.
            bool donorIsUnique = rules.IsUnique(stack);
            int maxStack = rules.GetMaxStack(stack);
            int donorCount = rules.GetCount(stack);

            BuildMergeTargets(target, stack, donorIsUnique, maxStack, rules, mergeCandidates,
                mergeTargets);

            DonorMovePlan plan = StackSelection.PlanDonorMove(mergeTargets, donorCount, maxStack,
                target.FreeSlots, donorIsUnique, movePlan);

            if (plan.MoveWholeStack)
            {
                // The stack object itself moves, so everything identifying it travels with it.
                mergeCandidates.Add(rules.GetItemType(stack), rules.GetPrefixId(stack),
                    target.Items.Count);
                target.Items.Add(stack);
                donor.Items.RemoveAt(donorSlot);
                diskWasModified[targetIndex] = true;
                diskWasModified[donorIndex] = true;
                return;
            }

            foreach (StackDraw merge in plan.Merges)
            {
                TStack existing = target.Items[merge.Index];
                rules.SetCount(existing, rules.GetCount(existing) + merge.Count);
            }

            foreach (int addAmount in plan.NewSlots)
            {
                mergeCandidates.Add(rules.GetItemType(stack), rules.GetPrefixId(stack),
                    target.Items.Count);
                target.Items.Add(rules.CopyWithCount(stack, addAmount));
            }

            if (plan.LeftOnDonor < donorCount)
            {
                diskWasModified[targetIndex] = true;
                diskWasModified[donorIndex] = true;
            }

            if (plan.LeftOnDonor == 0)
                donor.Items.RemoveAt(donorSlot);
            else
                rules.SetCount(stack, plan.LeftOnDonor);
        }

        // Every stack on the target this donor could merge into, with the identity verdict attached.
        // Fills a caller-owned buffer: this runs once per donor stack inside the defrag sweep.
        //
        // The index narrows the field to stacks sharing the donor's type and prefix, and CanMerge
        // still decides every one of them. That split is deliberate: a merge refuses a different
        // type or prefix before it tests anything else, so sharing them is a necessary condition of
        // merging and never a sufficient one. Letting the index answer instead of the rule is
        // issues 04 and 24 all over again.
        private static void BuildMergeTargets<TStack, TRules>(DefragmentDisk<TStack> target,
            TStack donorStack, bool donorIsUnique, int maxStack, TRules rules,
            MergeCandidateIndex mergeCandidates, List<MergeTarget> into)
            where TStack : class where TRules : IDefragmentRules<TStack>
        {
            into.Clear();

            // A stack that stands for itself moves whole into a free slot or stays put, so
            // PlanDonorMove never reads the targets for one.
            if (donorIsUnique)
                return;

            IReadOnlyList<int> candidates = mergeCandidates.GetCandidates(
                rules.GetItemType(donorStack), rules.GetPrefixId(donorStack));

            for (int candidate = 0; candidate < candidates.Count; candidate++)
            {
                int index = candidates[candidate];

                // The index records slots, and it is only correct while nothing removes from the
                // target mid-sweep - which nothing does today. Should that ever change, a slot past
                // the end must cost a merge, never credit whatever moved into its place. A slot
                // still in bounds but now holding another item is refused by CanMerge below, which
                // is re-asked for every candidate on every donor stack. The count is re-read rather
                // than hoisted so that a disk shrinking mid-loop cannot be read past its end.
                if (index >= target.Items.Count)
                    continue;

                TStack existing = target.Items[index];
                int existingCount = rules.GetCount(existing);

                // A stack already at capacity has no room, and PlanDonorMove would pass over it
                // anyway. Skipping it here spares the identity comparison, which is the expensive
                // one - a bulk-storage disk holds hundreds of full stacks under a single identity.
                if (existingCount >= maxStack)
                    continue;

                into.Add(new MergeTarget
                {
                    Index = index,
                    Stack = existingCount,
                    Accepts = rules.CanMerge(existing, donorStack)
                });
            }
        }

        // The target's stacks under the identity a merge needs, in ascending slot order so a donor
        // still tops up the earliest partial stack first. Deliberately does not ask whether a stack
        // stands for itself: that verdict can cost a full item deserialization, and a target stack
        // is never asked for one.
        private static void IndexTargetStacks<TStack, TRules>(DefragmentDisk<TStack> target,
            TRules rules, MergeCandidateIndex mergeCandidates)
            where TStack : class where TRules : IDefragmentRules<TStack>
        {
            mergeCandidates.Clear();

            for (int index = 0; index < target.Items.Count; index++)
            {
                TStack stack = target.Items[index];
                mergeCandidates.Add(rules.GetItemType(stack), rules.GetPrefixId(stack), index);
            }
        }
    }
}
