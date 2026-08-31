using System;
using System.Collections.Generic;

namespace TerraStorage.Helpers.Resolver
{
    // Storage as the crafting transaction sees it: a handful of operations over opaque item handles.
    // Terraria binds TItem to Item; the tests bind it to a plain class, which is what makes the
    // consume/execute bookkeeping assertable without Terraria, TagCompound or a live world.
    //
    // Insert must NOT mutate the handle it is given - it reports how many units did not fit and
    // leaves the caller holding the original stack, so a partial insert can be undone.
    public interface ICraftingStorage<TItem>
    {
        TItem Nothing { get; }

        int CountItem(int itemType);

        // Drains up to `amount` in ONE sweep of the network, handing back one handle per run of
        // consecutive draws that share state, in draw order. Best-effort: an empty list, never null,
        // when nothing came out. A type held as stacks that each stand for themselves comes back as
        // one handle per stack, because folding them would stamp one stack's state onto another's.
        List<TItem> ExtractStacks(int itemType, int amount);

        // Units recovered carrying exactly the state `stored` was inserted with, or 0 when nothing
        // matches. Drains up to `count` in ONE sweep - an insert too big for a single slot is
        // several stacks sharing one state, and asking once per stack made the caller rebuild this
        // handle's tags for every one of them. Bounded by `count`, so a stack holding units this run
        // did not store is left alone.
        int ExtractStored(TItem stored, int count);

        // Returns the number of units that did not fit.
        int Insert(TItem item);

        // 0 for an empty handle.
        int StackOf(TItem item);

        // Splits `count` units off, returning a handle for them and leaving `item` describing the
        // rest. Only called with 0 < count < StackOf(item).
        TItem SplitOff(TItem item, int count);

        // Whether two handles describe units in the same state, so withholding one in place of the
        // other loses nothing. NOT object identity: a step's product is inserted into storage and a
        // later step draws it back out as a different handle, and that drawn handle is the one the
        // refund has to recognise. Units with no state to compare are interchangeable, so a plain
        // handle matches any other plain handle of its type.
        bool SameStoredState(TItem first, TItem second);
    }

    // Undoing an insert, for both transactions.
    internal static class StorageRecovery
    {
        // Recovering by type alone draws in storage order, which for a type whose stacks each stand
        // for themselves is whichever sorts first - the player's own as readily as the one this run
        // conjured. The units balance either way; the identity does not. So each handle this run put
        // in is asked for its own units first, and only what no handle accounts for falls back to a
        // plain draw by type, which is right there because plain units are interchangeable.
        public static void TakeBack<TItem>(ICraftingStorage<TItem> storage, TItem conjuredHandle,
            int itemType, int count)
            => TakeBack(storage, new[] { conjuredHandle }, itemType, count);

        public static void TakeBack<TItem>(ICraftingStorage<TItem> storage,
            IReadOnlyList<TItem> conjuredHandles, int itemType, int count)
        {
            int remaining = count;

            foreach (TItem handle in conjuredHandles)
            {
                if (remaining <= 0)
                    break;

                int recovered = storage.ExtractStored(handle, remaining);
                if (recovered > 0)
                    remaining -= recovered;
            }

            if (remaining <= 0)
                return;

            foreach (TItem returned in storage.ExtractStacks(itemType, remaining))
                remaining -= storage.StackOf(returned);
        }
    }

    // One crafting step's material bookkeeping, free of Terraria types.
    public class ExecutionStep
    {
        public List<(int itemType, int count)> Consumed { get; set; } = new();
        public int ProducedType { get; set; }
        public int ProducedCount { get; set; }
    }

    // Everything one transaction has pulled out of storage, so a failure at any point can put it
    // all back. The items came out of these disks moments ago, so the space is there; a leftover
    // would mean storage shrank underneath us, and dropping it is still better than consuming it
    // for nothing.
    public class RefundLedger<TItem>
    {
        private readonly ICraftingStorage<TItem> _storage;
        private readonly List<(TItem item, int itemType)> _taken = new();
        private readonly Dictionary<int, int> _conjured = new();
        private readonly List<(TItem handle, int itemType, int count)> _conjuredHandles = new();

        public RefundLedger(ICraftingStorage<TItem> storage)
        {
            _storage = storage;
        }

        // Extracts exactly `amount`, or reports failure. Extract is a best-effort partial
        // extractor, so an unchecked call lets a step consume less than its recipe listed and
        // still produce the output. Whatever did come out is recorded either way.
        //
        // One handle is not enough: a stack that stands for itself comes out alone, so a material
        // held as twenty such stacks answered a request for twenty with one, and every recipe
        // needing it was offered and then quietly refused. The sweep hands back a handle per stack,
        // so no stack's state is folded into another's - the rule that made the withdrawal come up
        // short in the first place - and it does it in one walk of the network rather than twenty.
        public bool TryTakeExact(int itemType, int amount)
        {
            int taken = 0;

            foreach (TItem extracted in _storage.ExtractStacks(itemType, amount))
            {
                int extractedStack = _storage.StackOf(extracted);
                if (extractedStack <= 0)
                    continue;

                _taken.Add((extracted, itemType));
                taken += extractedStack;
            }

            return taken >= amount;
        }

        // Units of this type that this run created rather than the player owning them. A later
        // step extracts them back out of storage mixed in with the player's own stock, so the
        // refund has to know how many of what it is holding must NOT be handed back.
        //
        // Takes the handle the run produced, not just the count. Position cannot stand in for it:
        // a product lands in the first disk with room, which is ahead of stock the player holds on
        // a later disk, so the conjured handle is not reliably the trailing one.
        public void MarkConjured(TItem handle, int itemType, int count)
        {
            _conjured.TryGetValue(itemType, out int already);
            _conjured[itemType] = already + count;
            _conjuredHandles.Add((handle, itemType, count));
        }

        // Puts back what the player owned, withholding conjured units as it goes. Withholding
        // during the refund rather than re-extracting afterwards is what keeps it correct on a
        // network with no spare room: the end state is the start state, which fits by definition,
        // whereas inserting everything first overflows by exactly the conjured amount and drops
        // real materials as leftover.
        public void Refund()
        {
            // By handle first, and only then by position. A step's product goes into the first disk
            // with room, which is ahead of any stock the player holds on a later disk, so the
            // conjured handle is NOT reliably the trailing one - withholding purely from the end
            // drops a player's stack and re-inserts the run's copy in its place. The count balances
            // either way; for a type whose stacks stand for themselves the state does not.
            var withheld = new int[_taken.Count];

            WithholdMatchingHandles(withheld);

            // Whatever no handle claimed is units with no state to tell apart - interchangeable, so
            // any of them will do. The end of the list is still the best guess for those, because a
            // product with nothing to distinguish it merged into stock that was already there.
            for (int index = _taken.Count - 1; index >= 0; index--)
            {
                var (item, itemType) = _taken[index];

                _conjured.TryGetValue(itemType, out int outstanding);
                if (outstanding <= 0)
                    continue;

                int alreadyWithheld = withheld[index];
                int room = _storage.StackOf(item) - alreadyWithheld;
                if (room <= 0)
                    continue;

                int drop = Math.Min(outstanding, room);
                withheld[index] = alreadyWithheld + drop;
                _conjured[itemType] = outstanding - drop;
            }

            for (int index = 0; index < _taken.Count; index++)
            {
                TItem item = _taken[index].item;
                int stack = _storage.StackOf(item);
                int drop = withheld[index];

                if (drop >= stack)
                    continue; // the whole handle was conjured

                _storage.Insert(drop > 0 ? _storage.SplitOff(item, stack - drop) : item);
            }

            _taken.Clear();
        }

        // Each handle the run produced claims the drawn handles carrying its state, oldest draw
        // first. Bounded by what that step actually made, so a stack the player grew past it keeps
        // the units they owned.
        private void WithholdMatchingHandles(int[] withheld)
        {
            foreach (var (handle, itemType, count) in _conjuredHandles)
            {
                int outstanding = Math.Min(count, GetOutstandingConjured(itemType));

                for (int index = 0; index < _taken.Count && outstanding > 0; index++)
                {
                    var (drawn, drawnType) = _taken[index];
                    if (drawnType != itemType)
                        continue;

                    int room = _storage.StackOf(drawn) - withheld[index];
                    if (room <= 0)
                        continue;

                    if (!_storage.SameStoredState(handle, drawn))
                        continue;

                    int drop = Math.Min(outstanding, room);
                    withheld[index] += drop;
                    outstanding -= drop;
                    _conjured[itemType] = GetOutstandingConjured(itemType) - drop;
                }
            }
        }

        private int GetOutstandingConjured(int itemType)
        {
            _conjured.TryGetValue(itemType, out int outstanding);
            return outstanding;
        }

        // Conjured units the refund never saw, because no later step consumed them - they are
        // still sitting in storage where the step that made them put them.
        public List<(int itemType, int count)> DrainRemainingConjured()
        {
            var remaining = new List<(int itemType, int count)>();

            foreach (var pair in _conjured)
            {
                if (pair.Value > 0)
                    remaining.Add((pair.Key, pair.Value));
            }

            _conjured.Clear();
            return remaining;
        }
    }

    // The transaction behind both disk-upgrade paths (the panel in single player, the packet
    // handler on a server): either the whole material list is taken and true is returned, or
    // nothing is consumed and everything already taken is put back.
    public class MaterialConsumer<TItem>
    {
        private readonly ICraftingStorage<TItem> _storage;
        private readonly Func<int, int, TItem> _craftShortfall;

        // craftShortfall(itemType, totalNeeded) crafts the material and returns what was produced,
        // or Nothing when the craft is impossible. It is asked for the FULL need, not the
        // shortfall: a resolver asked for `need - have` rebuilds its pool from all of storage,
        // sees the stock the caller already subtracted, and reports a direct extract with no
        // steps - feasible, free, and wrong.
        public MaterialConsumer(ICraftingStorage<TItem> storage, Func<int, int, TItem> craftShortfall)
        {
            _storage = storage;
            _craftShortfall = craftShortfall;
        }

        public bool TryConsume(IEnumerable<(int itemType, int count)> materials)
        {
            var ledger = new RefundLedger<TItem>(_storage);

            foreach (var (itemType, needed) in materials)
            {
                if (needed <= 0)
                    continue;

                if (!TryStockUp(itemType, needed))
                {
                    ledger.Refund();
                    return false;
                }

                if (ledger.TryTakeExact(itemType, needed))
                    continue;

                ledger.Refund();
                return false;
            }

            return true;
        }

        // Brings storage up to `needed` of this material, crafting the shortfall if there is one.
        private bool TryStockUp(int itemType, int needed)
        {
            int have = _storage.CountItem(itemType);
            if (have >= needed)
                return true;

            TItem crafted = _craftShortfall(itemType, needed);
            if (_storage.StackOf(crafted) <= 0)
                return false;

            // A leftover means storage is full, so the units that did not fit are gone and the
            // extract that follows would come up short anyway. Fail here instead, while the ledger
            // can still put back everything earlier materials cost.
            int craftedStack = _storage.StackOf(crafted);
            int leftover = _storage.Insert(crafted);
            if (leftover <= 0)
                return true;

            // Take back the part that did land, so the refund cannot be blocked by a product the
            // caller is about to abandon anyway.
            int stored = craftedStack - leftover;
            if (stored > 0)
                StorageRecovery.TakeBack(_storage, crafted, itemType, stored);

            return false;
        }
    }

    // The Terraria-only half of executing a plan. Building an item, transferring a disk GUID and
    // rolling a prefix all need the real world, so they live behind this; everything that decides
    // whether materials move stays in PlanExecutor.
    public interface IStepProducer<TItem>
    {
        // Runs before a step's materials are taken, while storage still holds them - a disk
        // upgrade reads the source disk's GUID here, since extraction is about to remove it.
        void PrepareStep(int stepIndex);

        // Runs once the step is fully paid for.
        TItem ProduceStep(int stepIndex);
    }

    // The material bookkeeping of a crafting plan: pay for every step up front, store each
    // intermediate so the next step can consume it, and hand back the final product. Any shortfall
    // aborts with everything this run took already put back, so a failed craft never eats materials.
    public class PlanExecutor<TItem>
    {
        private readonly ICraftingStorage<TItem> _storage;

        public PlanExecutor(ICraftingStorage<TItem> storage)
        {
            _storage = storage;
        }

        public TItem Run(IReadOnlyList<ExecutionStep> steps, int finalItemCount, IStepProducer<TItem> producer)
        {
            var ledger = new RefundLedger<TItem>(_storage);
            var intermediates = new List<(TItem handle, int itemType, int count)>();
            TItem finalResult = _storage.Nothing;

            for (int stepIndex = 0; stepIndex < steps.Count; stepIndex++)
            {
                ExecutionStep step = steps[stepIndex];
                producer.PrepareStep(stepIndex);

                if (!TryPayFor(step, ledger))
                    return Abort(ledger, intermediates);

                TItem produced = producer.ProduceStep(stepIndex);
                bool isFinalStep = stepIndex == steps.Count - 1;

                if (isFinalStep)
                {
                    finalResult = StoreExcess(produced, finalItemCount);
                    continue;
                }

                int producedStack = _storage.StackOf(produced);
                if (!TryStoreIntermediate(produced, step.ProducedType))
                    return Abort(ledger, intermediates);

                intermediates.Add((produced, step.ProducedType, producedStack));
            }

            return finalResult;
        }

        // Materials go back, but anything this run conjured must not. A later step consumes an
        // earlier step's intermediate, which puts it in the ledger; refunding alone would hand it
        // back alongside the ingredients it was made from and leave the player holding both.
        private TItem Abort(RefundLedger<TItem> ledger, List<(TItem handle, int itemType, int count)> intermediates)
        {
            foreach (var (handle, itemType, count) in intermediates)
                ledger.MarkConjured(handle, itemType, count);

            ledger.Refund();

            // Whatever no later step consumed is still where the step that made it left it. The
            // handles that made it are offered back first, so taking a conjured stack of a type
            // whose stacks stand for themselves cannot take the player's stack of it instead.
            foreach (var (itemType, count) in ledger.DrainRemainingConjured())
                StorageRecovery.TakeBack(_storage, HandlesConjuredAs(intermediates, itemType), itemType, count);

            return _storage.Nothing;
        }

        private static List<TItem> HandlesConjuredAs(List<(TItem handle, int itemType, int count)> intermediates,
            int itemType)
        {
            var handles = new List<TItem>();

            foreach (var intermediate in intermediates)
            {
                if (intermediate.itemType == itemType)
                    handles.Add(intermediate.handle);
            }

            return handles;
        }

        private bool TryPayFor(ExecutionStep step, RefundLedger<TItem> ledger)
        {
            foreach (var (itemType, count) in step.Consumed)
            {
                // Storage no longer holds what the plan was built against. The caller puts back
                // everything this run took and produces nothing, rather than hand over an
                // underpaid item.
                if (!ledger.TryTakeExact(itemType, count))
                    return false;
            }

            return true;
        }

        // Never routes the final item through storage: a full store would swallow the insert, the
        // following extract would return nothing, and the caller would get an empty handle with
        // the ingredients already spent. Only batch-rounded excess is stored, and losing that on a
        // full store is acceptable.
        private TItem StoreExcess(TItem produced, int finalItemCount)
        {
            int producedStack = _storage.StackOf(produced);
            int excess = producedStack - finalItemCount;
            if (excess <= 0)
                return produced;

            TItem excessItem = _storage.SplitOff(produced, excess);
            _storage.Insert(excessItem);
            return produced;
        }

        // An intermediate has to land in storage for the next step to consume it. A leftover means
        // storage is full, so the next step would extract less than it needs.
        private bool TryStoreIntermediate(TItem produced, int producedType)
        {
            int producedStack = _storage.StackOf(produced);
            int leftover = _storage.Insert(produced);
            if (leftover <= 0)
                return true;

            // Take back the part that did land, so refunding the materials cannot be blocked by
            // the very product they were spent on. The intermediate is then discarded, which loses
            // nothing: its ingredients go back untouched.
            int stored = producedStack - leftover;
            if (stored > 0)
                StorageRecovery.TakeBack(_storage, produced, producedType, stored);

            return false;
        }
    }
}
