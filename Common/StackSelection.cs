using System;
using System.Collections.Generic;

namespace TerraStorage.Common
{
    // A stored stack reduced to what the selection rules actually need: how much is on it, whether
    // it carries per-instance state that makes it a distinct item, and which run of mergeable
    // stacks it belongs to. Deciding THOSE is DiskData's job and needs NBT; deciding what to do
    // about them does not, so these rules stay here where they can be exercised without Terraria.
    public struct StackSlot
    {
        public int Index;
        public int Stack;
        public bool IsUnique;

        // Which run of consecutive stacks this one merges into. Equal numbers mean folding the two
        // into a single returned item discards nothing - the caller mints them, because answering
        // that means comparing prefixes and serialized mod state.
        public int StateGroup;
    }

    // Take `Count` units off the stack at `Index`.
    public struct StackDraw
    {
        public int Index;
        public int Count;
    }

    // A stack a donor might merge into. Accepts is the caller's identity verdict.
    public struct MergeTarget
    {
        public int Index;
        public int Stack;
        public bool Accepts;
    }

    // What moving one donor stack into a target disk comes to.
    public class DonorMovePlan
    {
        // A unique stack stands for itself: it moves whole into a free slot or not at all, so the
        // caller relocates the stack object rather than copying counts out of it.
        public bool MoveWholeStack;

        public List<StackDraw> Merges { get; } = new();

        // Counts for stacks that have to occupy fresh slots, already split at maxStack.
        public List<int> NewSlots { get; } = new();

        public int LeftOnDonor;

        // Defragmenting plans one move per donor stack, which at the supported maximum is tens of
        // thousands of plans. Reusing one instance keeps that out of the allocator entirely.
        public void Reset()
        {
            MoveWholeStack = false;
            LeftOnDonor = 0;
            Merges.Clear();
            NewSlots.Clear();
        }
    }

    public static class StackSelection
    {
        // Plans a withdrawal of `count` units from the stacks matching an item type.
        //
        // Per-instance data belongs to ONE stack, and a withdrawal returns ONE item. So plain
        // stacks are drained first and a unique stack is only ever taken alone, as a fallback when
        // nothing plain matched - that is how a disk, always unique, still comes out. Folding a
        // unique stack into a bulk withdrawal stamps its mod state onto every unit returned,
        // duplicating enchantments one way and erasing the unique cell the other.
        //
        // Plain stacks pool, but only with the ones they merge with: the pass ends at the first
        // stack whose StateGroup differs, so a plan never spans a state boundary. One returned item
        // carries one state, and both ways of pretending otherwise lose an item - stamping the
        // opening stack's state onto units drawn from another, or handing every unit back with none.
        // The caller asks again for what is left, and its handle budget decides whether that opens
        // a second item or ends the withdrawal.
        //
        // allowUniqueFallback lets a caller already carrying plain items refuse the fallback, so a
        // unique stack is never pulled out only to be mixed into a count it does not describe.
        public static List<StackDraw> PlanWithdrawal(IReadOnlyList<StackSlot> matching, int count,
            bool allowUniqueFallback, out bool uniqueStack)
        {
            uniqueStack = false;
            var draws = new List<StackDraw>();

            if (count <= 0)
                return draws;

            int taken = 0;
            int openStateGroup = 0;

            foreach (StackSlot slot in matching)
            {
                if (slot.IsUnique)
                    continue;

                // Read the group only for a stack actually drawn from. A slot with nothing on it is
                // not a state boundary, and ending the pass over one would abandon every stack
                // behind it.
                int canTake = Math.Min(count - taken, slot.Stack);
                if (canTake <= 0)
                    continue;

                bool leavesTheOpenRun = draws.Count > 0 && slot.StateGroup != openStateGroup;
                if (leavesTheOpenRun)
                    break;

                draws.Add(new StackDraw { Index = slot.Index, Count = canTake });
                openStateGroup = slot.StateGroup;
                taken += canTake;

                if (taken >= count)
                    break;
            }

            bool nothingPlainMatched = taken == 0;
            if (!nothingPlainMatched || !allowUniqueFallback)
                return draws;

            foreach (StackSlot slot in matching)
            {
                if (!slot.IsUnique)
                    continue;

                int canTake = Math.Min(count, slot.Stack);
                if (canTake <= 0)
                    continue;

                draws.Add(new StackDraw { Index = slot.Index, Count = canTake });
                uniqueStack = true;
                break;
            }

            return draws;
        }

        // Plans moving one donor stack onto a target disk during a defragment: fill partial stacks
        // of the same identity first, then take fresh slots while any remain free.
        public static DonorMovePlan PlanDonorMove(IReadOnlyList<MergeTarget> targets, int donorStack,
            int maxStack, int freeSlots, bool donorIsUnique)
            => PlanDonorMove(targets, donorStack, maxStack, freeSlots, donorIsUnique, new DonorMovePlan());

        // Fills a caller-owned plan, so a defragment sweep can reuse one instead of allocating per
        // donor stack.
        public static DonorMovePlan PlanDonorMove(IReadOnlyList<MergeTarget> targets, int donorStack,
            int maxStack, int freeSlots, bool donorIsUnique, DonorMovePlan plan)
        {
            plan.Reset();
            plan.LeftOnDonor = donorStack;

            if (donorStack <= 0 || maxStack <= 0)
                return plan;

            if (donorIsUnique)
            {
                // Never merged, never split: a unique stack either gets a slot of its own or stays
                // where it is. Merging it on type and prefix alone destroys the state that makes
                // it unique, and splitting it would copy that state onto units it does not describe.
                if (freeSlots <= 0)
                    return plan;

                plan.MoveWholeStack = true;
                plan.LeftOnDonor = 0;
                return plan;
            }

            int toMove = donorStack;

            foreach (MergeTarget target in targets)
            {
                if (!target.Accepts)
                    continue;

                int room = maxStack - target.Stack;
                if (room <= 0)
                    continue;

                int canAdd = Math.Min(toMove, room);
                plan.Merges.Add(new StackDraw { Index = target.Index, Count = canAdd });
                toMove -= canAdd;

                if (toMove == 0)
                    break;
            }

            int slotsLeft = freeSlots;
            while (toMove > 0 && slotsLeft > 0)
            {
                int addAmount = Math.Min(toMove, maxStack);
                plan.NewSlots.Add(addAmount);
                toMove -= addAmount;
                slotsLeft--;
            }

            plan.LeftOnDonor = toMove;
            return plan;
        }
    }
}
