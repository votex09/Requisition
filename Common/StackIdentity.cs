namespace TerraStorage.Common
{
    // Whether a stored stack is its own item or just so many units of a type. Everything that
    // reorganises stacks - the terminal grid, deposits, withdrawals, defragmenting - asks this
    // first, so the rule lives here, away from the NBT, where it can be exercised without Terraria.
    public static class StackIdentity
    {
        // Carrying bytes some mod wrote is not the test on its own. In a modded world every item
        // carries some, so reading them as identity made every stack its own item and nothing ever
        // pooled. The test is whether the stack differs from a plain copy of the same item: either
        // the game refuses to stack the two (CanStack), or a mod wrote state into it that a fresh
        // copy lacks (per-instance globalData - WeaponPerks perks, which vary items without
        // overriding CanStack). Either way it is not "so many units of a type".
        public static bool IsUnique(bool hasModItemData, bool differsFromPlainItem)
            => hasModItemData || differsFromPlainItem;

        // Whether the full serialized tag has to be kept so extraction hands the item back with its
        // mod state intact. A stack can need this and still pool freely.
        public static bool MustPreserveFullTag(bool hasModItemData, bool carriesModWrittenData)
            => hasModItemData || carriesModWrittenData;
    }
}
