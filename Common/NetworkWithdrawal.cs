using System.Collections.Generic;

namespace TerraStorage.Common
{
    // One disk's answer to one request. Units of 0 means the disk had nothing to give: DiskIndex is
    // still valid, DrawIndex and StateGroup are not, and the sweep skips the draw entirely.
    //
    // DrawIndex is minted by the network and handed back only to it, so the rule below can be
    // exercised without Terraria, an Item or a live world.
    public readonly struct DrawnUnits
    {
        public readonly int DiskIndex;
        public readonly int DrawIndex;
        public readonly int Units;
        public readonly int StateGroup;

        public DrawnUnits(int diskIndex, int drawIndex, int units, int stateGroup)
        {
            DiskIndex = diskIndex;
            DrawIndex = drawIndex;
            Units = units;
            StateGroup = stateGroup;
        }

        public static DrawnUnits Nothing(int diskIndex) => new DrawnUnits(diskIndex, -1, 0, -1);
    }

    // The draws one returned item is built from, in the order they were taken. Draws[0] owns the
    // item; the rest contribute their units to it.
    public class WithdrawalHandle
    {
        public int StateGroup;
        public int Units;
        public List<DrawnUnits> Draws { get; } = new();
    }

    // The disks a withdrawal may draw from, reduced to what the sweep needs.
    public interface IWithdrawalNetwork
    {
        int DiskCount { get; }

        // Up to `amount` units of pooled stock, never a stack that stands for itself.
        DrawnUnits DrawPooled(int diskIndex, int amount);

        // One stack that stands for itself, for a type with no pooled stock left.
        DrawnUnits DrawStandalone(int diskIndex, int amount);

        // Returns a draw to the disk it came from, whose slots it just freed.
        void PutBack(DrawnUnits draw);
    }

    public static class NetworkWithdrawal
    {
        // Drains up to `count` units across the whole network in ONE sweep, returning one handle per
        // run of consecutive draws that share mod state - a state that comes back after another
        // opens a handle of its own rather than rejoining the earlier one, because `_taken`'s order
        // is what RefundLedger.Refund withholds from.
        //
        // handleLimit is how many separate items the caller can hold. A withdrawal onto the mouse
        // cursor holds one, so it stops at the first state boundary and hands that draw straight
        // back; a crafting step's ledger holds as many as the step needs, so a boundary opens
        // another handle instead. Asking again per handle is what made a step needing twenty units
        // walk every disk twenty times.
        public static List<WithdrawalHandle> Drain(IWithdrawalNetwork network, int count, int handleLimit)
        {
            var handles = new List<WithdrawalHandle>();
            if (count <= 0 || handleLimit <= 0)
                return handles;

            int taken = DrainPooledStock(network, count, handleLimit, handles);
            DrainStandaloneStacks(network, count - taken, handleLimit, handles);
            return handles;
        }

        private static int DrainPooledStock(IWithdrawalNetwork network, int count, int handleLimit,
            List<WithdrawalHandle> handles)
        {
            int taken = 0;

            for (int diskIndex = 0; diskIndex < network.DiskCount; diskIndex++)
            {
                // A disk is asked until it stops yielding, not once: one draw carries units of ONE
                // mod state, so a disk holding the type under two of them answers twice. Asking
                // once would abandon everything behind the first state boundary on that disk - and
                // a caller that reads the whole amount from a single sweep, as a crafting step's
                // ledger does, would be told the network was short.
                while (taken < count)
                {
                    DrawnUnits draw = network.DrawPooled(diskIndex, count - taken);

                    // A disk holding none of the type is not a state boundary. Reading its state
                    // group would end the sweep here and abandon every disk behind it.
                    if (draw.Units <= 0)
                        break;

                    WithdrawalHandle openHandle = handles.Count == 0 ? null : handles[handles.Count - 1];
                    bool foldsIntoOpenHandle = openHandle != null && openHandle.StateGroup == draw.StateGroup;

                    if (!foldsIntoOpenHandle && handles.Count >= handleLimit)
                    {
                        network.PutBack(draw);
                        return taken;
                    }

                    if (foldsIntoOpenHandle)
                        AddDraw(openHandle, draw);
                    else
                        handles.Add(NewHandle(draw));

                    taken += draw.Units;
                }

                if (taken >= count)
                    break;
            }

            return taken;
        }

        // Stacks that each stand for themselves are refused a place in a pooled withdrawal only
        // because folding them into a count would stamp one stack's state onto units from another.
        // A caller with handle budget left is not folding anything, so the refusal does not apply.
        private static void DrainStandaloneStacks(IWithdrawalNetwork network, int stillNeeded,
            int handleLimit, List<WithdrawalHandle> handles)
        {
            if (stillNeeded <= 0)
                return;

            int taken = 0;

            for (int diskIndex = 0; diskIndex < network.DiskCount; diskIndex++)
            {
                // One stack per draw, each kept as its own handle and never folded into another. A
                // disk holding several is asked again until it stops yielding.
                while (taken < stillNeeded && handles.Count < handleLimit)
                {
                    DrawnUnits draw = network.DrawStandalone(diskIndex, stillNeeded - taken);
                    if (draw.Units <= 0)
                        break;

                    handles.Add(NewHandle(draw));
                    taken += draw.Units;
                }

                bool nothingLeftToDrawFor = taken >= stillNeeded || handles.Count >= handleLimit;
                if (nothingLeftToDrawFor)
                    return;
            }
        }

        private static WithdrawalHandle NewHandle(DrawnUnits draw)
        {
            var handle = new WithdrawalHandle { StateGroup = draw.StateGroup, Units = draw.Units };
            handle.Draws.Add(draw);
            return handle;
        }

        private static void AddDraw(WithdrawalHandle handle, DrawnUnits draw)
        {
            handle.Draws.Add(draw);
            handle.Units += draw.Units;
        }
    }
}
