namespace TerraStorage.Common
{
    // What became of one deposit into the network.
    //
    // The count that went in has to be read BEFORE the item's stack is overwritten with the
    // leftover. Comparing the two afterwards reads leftover < leftover, which is never true, so a
    // partial deposit reported failure and its delta was never broadcast - other clients went
    // stale until the next resync. Holding both numbers in one value makes that mistake unspellable.
    public readonly struct DepositOutcome
    {
        public int Offered { get; }
        public int Leftover { get; }

        public DepositOutcome(int offered, int leftover)
        {
            Offered = offered < 0 ? 0 : offered;

            int clamped = leftover < 0 ? 0 : leftover;
            Leftover = clamped > Offered ? Offered : clamped;
        }

        public int Deposited => Offered - Leftover;

        // Anything at all landed, so storage changed and the delta must go out.
        public bool AnyDeposited => Deposited > 0;

        // Something bounced and has to go back to the player.
        public bool NeedsReturn => Leftover > 0;
    }
}
