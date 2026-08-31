namespace TerraStorage.Common
{
    // Two rules the packet handlers need and cannot be compiled to check, so they live here where
    // the test runner really executes them.
    public static class DiskAccess
    {
        // Whether a player may operate a placed Terminal. Standing at it is one way; carrying a
        // Remote Terminal is the other, and that item exists precisely to lift the range rule
        // (TerminalUISystem.OpenTerminalRemote skips the distance close). Written as an OR for that
        // reason: an AND here is not a stricter rule, it is the Remote Terminal not working - which
        // is what Defragment shipped with, because the only range-scoped handler had no second arm.
        //
        // Deliberately NOT "a Remote Terminal bound to this Terminal". The bound id is item mod data
        // and the server's copy of an inventory slot is written by the client that owns it, so the
        // extra condition is forgeable at the same cost as the item itself and buys nothing.
        public static bool MayOperateTerminal(bool senderWithinRange, bool senderHoldsRemoteTerminal)
        {
            return senderWithinRange || senderHoldsRemoteTerminal;
        }

        // Whether a disk's world entry may be dropped once the disk has left its Drive Bay. Both
        // arms are needed and the conjunction is the whole safety argument: an entry still holding
        // items is a player's storage, and an entry another bay still references is in use. An
        // empty, unreferenced entry carries nothing - DiskData is its id, its tier and its items,
        // and the tier is re-read off the disk item every time it is inserted.
        //
        // The tempting weaker rule is "no disk in the world carries this id". That cannot be
        // answered: a disk in a chest, a piggy bank, on the ground or in an offline player's
        // inventory is invisible to the server, so that rule deletes their storage.
        public static bool MayPruneDiskData(int usedStacks, bool anotherBayHoldsDisk)
        {
            return usedStacks == 0 && !anotherBayHoldsDisk;
        }
    }
}
