using System.Collections.Generic;

namespace TerraStorage.Common
{
    // Which stacks already on a disk a donor stack could possibly merge into.
    //
    // Defragmenting used to put the merge question to every stack on the target disk for every
    // donor stack it moved. At the supported maximum that is tens of millions of questions whose
    // answer was already settled by the first two integers compared. Bucketing the target's stacks
    // under exactly those two integers means a donor is only ever asked about stacks that could
    // say yes.
    //
    // The key is deliberately WEAKER than the merge rule, and that is the whole safety argument.
    // StoredItemStack.StacksWith refuses two stacks of a different type or prefix before it tests
    // anything else, so sharing a key is a NECESSARY condition of merging and never a sufficient
    // one: this can only ever withhold a pair the rule would have refused anyway.
    // DiskData.CanMergeStacks still decides every candidate handed back. Issues 04 and 24 are both
    // what happens when something other than that rule gets to say two stacks are the same item.
    public class MergeCandidateIndex
    {
        // An array rather than a List: this is handed out to every caller that asks about an
        // identity no stack carries, and a List behind IReadOnlyList can be cast back and mutated.
        private static readonly IReadOnlyList<int> NoCandidates = System.Array.Empty<int>();

        private readonly Dictionary<(int itemType, int prefixId), List<int>> _stackIndicesByIdentity = new();

        // Keeps the buckets themselves, so sweeping a whole drive bay does not hand the allocator a
        // fresh list per disk per identity.
        public void Clear()
        {
            foreach (var bucket in _stackIndicesByIdentity.Values)
                bucket.Clear();
        }

        public void Add(int itemType, int prefixId, int stackIndex)
        {
            var identity = (itemType, prefixId);
            if (!_stackIndicesByIdentity.TryGetValue(identity, out List<int> bucket))
            {
                bucket = new List<int>();
                _stackIndicesByIdentity[identity] = bucket;
            }

            bucket.Add(stackIndex);
        }

        // In the order they were added, which for a defragment sweep is ascending stack index: a
        // donor tops up the earliest partial stack first, exactly as it did when the caller walked
        // the whole disk in order. An identity no stack carries gets a shared empty list rather
        // than null or a new one, so a miss needs no guard and costs nothing.
        public IReadOnlyList<int> GetCandidates(int itemType, int prefixId)
            => _stackIndicesByIdentity.TryGetValue((itemType, prefixId), out List<int> bucket)
                ? bucket
                : NoCandidates;
    }
}
