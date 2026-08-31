using System.Collections.Generic;
using TerraStorage.Common;

namespace TerraStorage.Tests
{
    // StorageWorldSystem.ExtractItem's withdrawal rule as it stood at f37f33e, kept so the rewritten
    // sweep can be held against it rather than against a reading of it. Every UI and network caller
    // takes exactly one item, so this is the behaviour that must not move; NW-12 sweeps a matrix of
    // disk layouts asserting Drain(..., handleLimit: 1) still agrees with it everywhere.
    //
    // Kept for the same reason BuggyPreview is: a committed old implementation is the only thing that
    // makes "unchanged" checkable once the new one has replaced it.
    public static class LegacySingleHandleDrain
    {
        public static List<WithdrawalHandle> Drain(IWithdrawalNetwork network, int count)
        {
            var handles = new List<WithdrawalHandle>();
            if (count <= 0)
                return handles;

            int taken = 0;
            WithdrawalHandle result = null;

            for (int diskIndex = 0; diskIndex < network.DiskCount; diskIndex++)
            {
                DrawnUnits draw = network.DrawPooled(diskIndex, count - taken);
                if (draw.Units <= 0)
                    continue;

                if (result != null && result.StateGroup != draw.StateGroup)
                {
                    network.PutBack(draw);
                    break;
                }

                taken += draw.Units;
                if (result == null)
                {
                    result = new WithdrawalHandle { StateGroup = draw.StateGroup, Units = draw.Units };
                    result.Draws.Add(draw);
                    handles.Add(result);
                }
                else
                {
                    result.Draws.Add(draw);
                    result.Units = taken;
                }

                if (taken >= count)
                    break;
            }

            if (result != null)
                return handles;

            for (int diskIndex = 0; diskIndex < network.DiskCount; diskIndex++)
            {
                DrawnUnits draw = network.DrawStandalone(diskIndex, count);
                if (draw.Units <= 0)
                    continue;

                var standalone = new WithdrawalHandle { StateGroup = draw.StateGroup, Units = draw.Units };
                standalone.Draws.Add(draw);
                handles.Add(standalone);
                return handles;
            }

            return handles;
        }
    }
}
