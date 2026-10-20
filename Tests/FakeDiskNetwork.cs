using System.Collections.Generic;
using TerraStorage.Common;

namespace TerraStorage.Tests
{
    // A disk network with no Terraria in it. Each disk is a list of stacks; a stack carries the mod
    // state that decides whether two draws may share one returned item, and a stack that stands for
    // itself is never pooled.
    //
    // PooledDraws and StandaloneDraws count how many times a disk was asked, which is the falsifiable
    // form of "a step needing N units walks every disk N times".
    public sealed class FakeDiskNetwork : IWithdrawalNetwork
    {
        private sealed class Stack
        {
            public int Units;
            public string State;
            public bool IsStandalone;
        }

        private sealed class Draw
        {
            public int DiskIndex;
            public List<Stack> From = new();
            public List<int> Units = new();
        }

        private readonly List<List<Stack>> _disks = new();
        private readonly List<Draw> _draws = new();
        private readonly List<string> _stateGroups = new();

        public int PooledDraws;
        public int StandaloneDraws;

        public int TotalDraws => PooledDraws + StandaloneDraws;

        public FakeDiskNetwork WithDisk() { _disks.Add(new List<Stack>()); return this; }

        // Pooled stock carrying one mod state, as a single stack.
        public FakeDiskNetwork WithPooled(int diskIndex, int units, string state)
        {
            _disks[diskIndex].Add(new Stack { Units = units, State = state });
            return this;
        }

        // Stacks that each stand for themselves - armour, a storage disk, anything a mod refuses to
        // stack. One unit each unless told otherwise.
        public FakeDiskNetwork WithStandalone(int diskIndex, int stackCount, int unitsEach = 1)
        {
            for (int stack = 0; stack < stackCount; stack++)
                _disks[diskIndex].Add(new Stack { Units = unitsEach, IsStandalone = true, State = "standalone" + _disks[diskIndex].Count });
            return this;
        }

        public int DiskCount => _disks.Count;

        public int UnitsOn(int diskIndex)
        {
            int total = 0;
            foreach (Stack stack in _disks[diskIndex])
                total += stack.Units;
            return total;
        }

        public int SlotsOn(int diskIndex) => _disks[diskIndex].Count;

        // The mod state the item built from this handle would carry. Null means the withdrawal came
        // back with none, which for a draw off stacks that had state is the loss issue 05 is about.
        public string StateOfHandle(WithdrawalHandle handle) => _stateGroups[handle.StateGroup];

        // Every handle's state, in draw order, as one comparable string.
        public string StatesOf(List<WithdrawalHandle> handles)
            => string.Join(",", handles.ConvertAll(handle => StateOfHandle(handle) ?? "none"));

        public int TotalUnits
        {
            get
            {
                int total = 0;
                for (int disk = 0; disk < _disks.Count; disk++)
                    total += UnitsOn(disk);
                return total;
            }
        }

        public DrawnUnits DrawPooled(int diskIndex, int amount)
        {
            PooledDraws++;
            return TakeFrom(diskIndex, amount, standalone: false);
        }

        public DrawnUnits DrawStandalone(int diskIndex, int amount)
        {
            StandaloneDraws++;

            // "Pooled stock is drained first, and a stack that stands for itself comes out only
            // when nothing pooled matched" is StackSelection.PlanWithdrawal's own rule, which
            // TakeFrom already applies - a second hand-written copy of it here would be exactly the
            // drift 23a/23b/23c are each an instance of.
            return TakeFrom(diskIndex, amount, standalone: true);
        }

        public void PutBack(DrawnUnits draw)
        {
            Draw record = _draws[draw.DrawIndex];
            List<Stack> stacks = _disks[record.DiskIndex];

            for (int index = 0; index < record.From.Count; index++)
            {
                Stack stack = record.From[index];
                stack.Units += record.Units[index];

                if (!stacks.Contains(stack))
                    stacks.Add(stack);
            }
        }

        // Which stacks a draw comes from is StackSelection.PlanWithdrawal's decision, the same one
        // DiskData.ExtractItem carries out. Deciding it a second time here would be a second
        // encoding of the rule NW-* exists to test.
        private DrawnUnits TakeFrom(int diskIndex, int amount, bool standalone)
        {
            if (amount <= 0)
                return DrawnUnits.Nothing(diskIndex);

            List<Stack> stacks = _disks[diskIndex];
            var matching = MatchingSlots(stacks);

            var draws = StackSelection.PlanWithdrawal(matching, amount, standalone, out bool standaloneStack);
            if (draws.Count == 0 || (standalone && !standaloneStack))
                return DrawnUnits.Nothing(diskIndex);

            var record = new Draw { DiskIndex = diskIndex };
            int taken = 0;

            // Mirrors DiskData.ExtractItem: the plan cannot span a state boundary, so the stack that
            // opened it speaks for every unit drawn.
            string state = stacks[draws[0].Index].State;

            foreach (var draw in draws)
            {
                Stack stack = stacks[draw.Index];
                stack.Units -= draw.Count;
                record.From.Add(stack);
                record.Units.Add(draw.Count);
                taken += draw.Count;
            }

            // Mirrors DiskData.ExtractItem: a stack drained to nothing gives up its slot, so what the
            // next withdrawal sees - and what a put-back has to restore - is a shorter disk.
            stacks.RemoveAll(s => s.Units <= 0);

            if (taken <= 0)
                return DrawnUnits.Nothing(diskIndex);

            _draws.Add(record);
            return new DrawnUnits(diskIndex, _draws.Count - 1, taken, DrawnRunIndexOf(state));
        }

        // Mirrors DiskData.MatchingSlots: StateGroup numbers the runs of consecutive pooled stacks
        // that merge into one another, and a stack standing for itself belongs to no run.
        private static List<StackSlot> MatchingSlots(List<Stack> stacks)
        {
            var matching = new List<StackSlot>();
            Stack previousPooled = null;
            int runIndex = 0;

            for (int index = 0; index < stacks.Count; index++)
            {
                Stack stack = stacks[index];
                if (!stack.IsStandalone)
                {
                    if (previousPooled != null && previousPooled.State != stack.State)
                        runIndex++;

                    previousPooled = stack;
                }

                matching.Add(new StackSlot
                {
                    Index = index,
                    Stack = stack.Units,
                    IsUnique = stack.IsStandalone,
                    StateGroup = runIndex
                });
            }

            return matching;
        }

        // Mirrors StorageWorldSystem.DiskWithdrawal.RunIndexOf: consecutive draws sharing a state
        // share a group, and a state that comes back later gets one of its own.
        private int DrawnRunIndexOf(string state)
        {
            if (_stateGroups.Count == 0 || _stateGroups[_stateGroups.Count - 1] != state)
                _stateGroups.Add(state);

            return _stateGroups.Count - 1;
        }
    }
}
