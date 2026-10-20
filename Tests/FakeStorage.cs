using System;
using System.Collections.Generic;
using System.Linq;
using TerraStorage.Common;
using TerraStorage.Helpers.Resolver;

namespace TerraStorage.Tests
{
    // An item handle with no Terraria in it. Mark stands in for per-instance state (prefix, mod
    // data), so a refund can be shown to put back the same units rather than equivalent ones.
    public sealed class FakeItem
    {
        public int Type;
        public int Stack;
        public string Mark;

        public override string ToString() => Mark == null ? $"{Type}x{Stack}" : $"{Type}x{Stack}[{Mark}]";
    }

    // A stack-level storage network. Capacity is a total unit count, which is all the transaction
    // core ever needs to know about "storage is full".
    //
    // Withdrawals go through StackSelection.PlanWithdrawal, the same rule DiskData.ExtractItem
    // carries out, so a type held as several stacks that each stand for themselves behaves here
    // exactly as it does in a real network: the count says one thing, the withdrawal another.
    public sealed class FakeStorage : ICraftingStorage<FakeItem>
    {
        private sealed class Stack
        {
            public int Type;
            public int Count;
            public bool IsUnique;
            public string Mark;
            public int Disk;
        }

        private readonly List<Stack> _stacks = new();
        private readonly HashSet<int> _uniqueTypes = new();
        private readonly List<int> _diskSlotLimits = new();

        public int Capacity = int.MaxValue;
        public readonly List<string> Log = new();

        public FakeStorage With(int itemType, int count)
        {
            AddStacks(itemType, count);
            return this;
        }

        // Every stack of this type stands for itself and holds a single unit - a storage disk, an
        // unloaded item, anything a mod refuses to stack.
        public FakeStorage WithUniqueType(int itemType)
        {
            _uniqueTypes.Add(itemType);
            return this;
        }

        // Stock laid out as given rather than as one stack: armour holds one unit per stack, so 18
        // pieces are 18 stacks whether or not any of them stands for itself.
        public FakeStorage WithStacks(int itemType, params int[] sizes)
        {
            foreach (int size in sizes)
                PlaceStack(new Stack { Type = itemType, Count = size, IsUnique = _uniqueTypes.Contains(itemType) });
            return this;
        }

        // One stack carrying per-instance state, so a refund can be shown to put back the stack it
        // took rather than an equivalent count.
        public FakeStorage WithUniqueStack(int itemType, int count, string mark)
        {
            _uniqueTypes.Add(itemType);
            PlaceStack(new Stack { Type = itemType, Count = count, IsUnique = true, Mark = mark });
            return this;
        }

        // How many stacks each disk holds, in the order a withdrawal walks them. Without this the
        // network is one unbounded disk and everything inserted lands at the end - which is the
        // layout in which withholding a refund from the end of the ledger happens to be right.
        public FakeStorage WithDiskSlots(params int[] slotsPerDisk)
        {
            _diskSlotLimits.AddRange(slotsPerDisk);
            return this;
        }

        // Seeds a stack onto a named disk rather than the first with room, so a test can leave an
        // early disk holding stock AND a free slot - the layout an insert fills ahead of a later
        // disk's stock.
        public FakeStorage WithUniqueStackOn(int disk, int itemType, int count, string mark)
        {
            _uniqueTypes.Add(itemType);
            InsertOnDisk(new Stack { Type = itemType, Count = count, IsUnique = true, Mark = mark }, disk);
            return this;
        }

        public FakeStorage WithOn(int disk, int itemType, int count)
        {
            InsertOnDisk(new Stack { Type = itemType, Count = count }, disk);
            return this;
        }

        private void InsertOnDisk(Stack stack, int disk)
        {
            stack.Disk = disk;
            _stacks.Insert(GetSlotAfterDisk(disk), stack);
        }

        public List<string> MarksOf(int itemType)
        {
            var marks = _stacks.Where(s => s.Type == itemType && s.Mark != null).Select(s => s.Mark).ToList();
            marks.Sort(StringComparer.Ordinal);
            return marks;
        }

        public int TotalUnits => _stacks.Sum(s => s.Count);

        public FakeItem Nothing => null;

        public int CountItem(int itemType)
            => _stacks.Where(s => s.Type == itemType).Sum(s => s.Count);

        // Goes through NetworkWithdrawal.Drain, the same rule StorageWorldSystem carries out, so the
        // crafting tests exercise the shipped sweep rather than a second hand-written copy of it.
        // One disk, because what a network of them does is NW-*'s job.
        public List<FakeItem> ExtractStacks(int itemType, int amount)
        {
            var withdrawal = new StackWithdrawal(this, itemType);
            var handles = NetworkWithdrawal.Drain(withdrawal, amount, int.MaxValue);
            Log.Add($"extract {itemType}x{amount}->{withdrawal.TotalUnitsOf(handles)}");
            return withdrawal.BuildItems(handles);
        }

        // Takes back the stack this handle was stored as, and only when it can account for the whole
        // of it: one that grew past `count` holds units this run did not store. A handle with no
        // per-instance state has no identity to match on, so it recovers nothing here and falls back
        // to a plain draw by type - which is right, because plain units are interchangeable.
        public int ExtractStored(FakeItem stored, int count)
        {
            if (stored == null || stored.Mark == null || count <= 0)
                return 0;

            int recovered = 0;

            // Drains in one call, like StorageWorldSystem.ExtractStoredItem: an insert too big for
            // one slot is several stacks sharing a state, and every one of them matched the same
            // handle, so folding them takes nothing from anyone else.
            while (recovered < count)
            {
                int stillWanted = count - recovered;
                Stack match = _stacks.FirstOrDefault(s => s.Type == stored.Type && s.Mark == stored.Mark
                    && s.Count <= stillWanted);
                if (match == null)
                    break;

                _stacks.Remove(match);
                Log.Add($"take back stored {stored.Type}[{stored.Mark}]->{match.Count}");
                recovered += match.Count;
            }

            return recovered;
        }

        // One disk's worth of stacks as the withdrawal sweep sees them.
        private sealed class StackWithdrawal : IWithdrawalNetwork
        {
            private readonly FakeStorage _storage;
            private readonly int         _itemType;

            private readonly List<FakeItem>      _drawnItems = new();
            private readonly List<List<Stack>>   _drawnFrom = new();
            private readonly List<List<int>>     _drawnUnits = new();
            private readonly List<string>        _stateGroups = new();

            public StackWithdrawal(FakeStorage storage, int itemType)
            {
                _storage = storage;
                _itemType = itemType;
            }

            public int DiskCount => 1;

            public DrawnUnits DrawPooled(int diskIndex, int amount) => Draw(diskIndex, amount, allowStandalone: false);

            public DrawnUnits DrawStandalone(int diskIndex, int amount) => Draw(diskIndex, amount, allowStandalone: true);

            public void PutBack(DrawnUnits draw)
            {
                List<Stack> from = _drawnFrom[draw.DrawIndex];
                for (int index = 0; index < from.Count; index++)
                    from[index].Count += _drawnUnits[draw.DrawIndex][index];

                foreach (Stack stack in from)
                {
                    if (!_storage._stacks.Contains(stack))
                        _storage._stacks.Add(stack);
                }
            }

            public List<FakeItem> BuildItems(List<WithdrawalHandle> handles)
            {
                var items = new List<FakeItem>(handles.Count);

                foreach (WithdrawalHandle handle in handles)
                {
                    FakeItem item = _drawnItems[handle.Draws[0].DrawIndex];
                    item.Stack = handle.Units;
                    items.Add(item);
                }

                return items;
            }

            public int TotalUnitsOf(List<WithdrawalHandle> handles)
            {
                int total = 0;
                foreach (WithdrawalHandle handle in handles)
                    total += handle.Units;
                return total;
            }

            private DrawnUnits Draw(int diskIndex, int amount, bool allowStandalone)
            {
                var matching = _storage.MatchingSlots(_itemType);
                var draws = StackSelection.PlanWithdrawal(matching, amount, allowStandalone, out bool standaloneStack);

                // Pooled stock is drained network-wide before the standalone pass, so a draw that is
                // not a stack standing for itself means there was nothing left to take.
                if (draws.Count == 0 || (allowStandalone && !standaloneStack))
                    return DrawnUnits.Nothing(diskIndex);

                // Mirrors DiskData.ExtractItem: the plan cannot span a state boundary, so the stack
                // that opened it speaks for every unit drawn.
                string mark = _storage._stacks[draws[0].Index].Mark;

                var from = new List<Stack>();
                var units = new List<int>();
                int taken = 0;

                foreach (var draw in draws)
                {
                    Stack stack = _storage._stacks[draw.Index];
                    stack.Count -= draw.Count;
                    from.Add(stack);
                    units.Add(draw.Count);
                    taken += draw.Count;
                }

                _storage._stacks.RemoveAll(s => s.Count <= 0);

                if (taken <= 0)
                    return DrawnUnits.Nothing(diskIndex);

                _drawnItems.Add(new FakeItem { Type = _itemType, Stack = taken, Mark = mark });
                _drawnFrom.Add(from);
                _drawnUnits.Add(units);
                return new DrawnUnits(diskIndex, _drawnItems.Count - 1, taken, StateGroupOf(mark));
            }

            // Mirrors StorageWorldSystem.DiskWithdrawal.RunIndexOf: consecutive draws sharing a mark
            // share a group, and a mark that comes back later gets one of its own.
            private int StateGroupOf(string mark)
            {
                if (_stateGroups.Count == 0 || _stateGroups[_stateGroups.Count - 1] != mark)
                    _stateGroups.Add(mark);

                return _stateGroups.Count - 1;
            }
        }

        // Mirrors StorageWorldSystem.InsertItem: reports what did not fit and leaves the caller's
        // handle untouched, so a partial insert stays undoable.
        public int Insert(FakeItem item)
        {
            if (item == null || item.Stack <= 0)
                return 0;

            int space = Capacity - TotalUnits;
            int stored = Math.Min(space, item.Stack);
            Log.Add($"insert {item}->{stored}");

            if (stored > 0)
                AddStacks(item.Type, stored, item.Mark);

            return item.Stack - stored;
        }

        public int StackOf(FakeItem item) => item == null ? 0 : item.Stack;

        public FakeItem SplitOff(FakeItem item, int count)
        {
            var part = new FakeItem { Type = item.Type, Stack = count, Mark = item.Mark };
            item.Stack -= count;
            return part;
        }

        // Mark stands in for everything a real handle's state comparison reads, so two handles match
        // when their type and mark agree. Two plain handles of a type match because units with no
        // state are interchangeable.
        public bool SameStoredState(FakeItem first, FakeItem second)
        {
            if (first == null || second == null)
                return false;

            return first.Type == second.Type && first.Mark == second.Mark;
        }

        private void AddStacks(int itemType, int count, string mark = null)
        {
            if (!_uniqueTypes.Contains(itemType))
            {
                PlaceStack(new Stack { Type = itemType, Count = count, Mark = mark });
                return;
            }

            for (int unit = 0; unit < count; unit++)
                PlaceStack(new Stack { Type = itemType, Count = 1, IsUnique = true, Mark = mark });
        }

        // With no slot limits set, one unbounded disk: a new stack lands at the end, which is what
        // every test that does not care about disk layout wants. With limits, this mirrors
        // StorageWorldSystem.InsertItem walking the disks in order and filling the first with room -
        // so a stack can land AHEAD of stock the player holds on a later disk, which is the layout
        // that tells a refund by handle apart from a refund by position.
        private void PlaceStack(Stack stack)
        {
            if (_diskSlotLimits.Count == 0)
            {
                _stacks.Add(stack);
                return;
            }

            for (int disk = 0; disk < _diskSlotLimits.Count; disk++)
            {
                if (CountStacksOnDisk(disk) >= _diskSlotLimits[disk])
                    continue;

                stack.Disk = disk;
                _stacks.Insert(GetSlotAfterDisk(disk), stack);
                return;
            }
        }

        private int CountStacksOnDisk(int disk)
        {
            int used = 0;
            foreach (Stack stack in _stacks)
            {
                if (stack.Disk == disk)
                    used++;
            }
            return used;
        }

        // Stacks are held in disk order, so a disk's own stacks are the run ending here.
        private int GetSlotAfterDisk(int disk)
        {
            int slot = 0;
            for (int index = 0; index < _stacks.Count; index++)
            {
                if (_stacks[index].Disk <= disk)
                    slot = index + 1;
            }
            return slot;
        }

        // Mirrors DiskData.MatchingSlots: StateGroup numbers the runs of consecutive pooled stacks
        // that merge into one another, and a stack standing for itself belongs to no run.
        private List<StackSlot> MatchingSlots(int itemType)
        {
            var matching = new List<StackSlot>();
            Stack previousPooled = null;
            int runIndex = 0;

            for (int index = 0; index < _stacks.Count; index++)
            {
                Stack stack = _stacks[index];
                if (stack.Type != itemType)
                    continue;

                if (!stack.IsUnique)
                {
                    if (previousPooled != null && previousPooled.Mark != stack.Mark)
                        runIndex++;

                    previousPooled = stack;
                }

                matching.Add(new StackSlot
                {
                    Index = index,
                    Stack = stack.Count,
                    IsUnique = stack.IsUnique,
                    StateGroup = runIndex
                });
            }

            return matching;
        }
    }

    // Produces each step's output from a fixed table, with no Terraria item construction.
    public sealed class FakeStepProducer : IStepProducer<FakeItem>
    {
        private readonly IReadOnlyList<ExecutionStep> _steps;
        private readonly string                       _mark;

        public readonly List<int> Prepared = new();

        // A mark stands in for the per-instance state a real conjured item carries. Without one a
        // recovery cannot tell the run's own product from the player's stack of the same type, which
        // is the whole of what the handle-precise take-back exists to do.
        public FakeStepProducer(IReadOnlyList<ExecutionStep> steps, string mark = null)
        {
            _steps = steps;
            _mark = mark;
        }

        public void PrepareStep(int stepIndex) => Prepared.Add(stepIndex);

        public FakeItem ProduceStep(int stepIndex)
            => new FakeItem
            {
                Type = _steps[stepIndex].ProducedType,
                Stack = _steps[stepIndex].ProducedCount,
                Mark = _mark
            };
    }
}
