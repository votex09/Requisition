namespace TerraStorage.Common
{
    // Whether a stored stack is its own item or just so many units of a type. Everything that
    // reorganises stacks - the terminal grid, deposits, withdrawals, defragmenting - asks this
    // first, so the rule lives here, away from the NBT, where it can be exercised without Terraria.
    public static class StackIdentity
    {
        // Carrying bytes some mod wrote is not the test. In a modded world every item carries some,
        // so reading them as identity made every stack its own item and nothing ever pooled.
        public static bool IsUnique(bool hasModItemData, bool gameRefusesToStackWithPlainItem)
            => hasModItemData || gameRefusesToStackWithPlainItem;

        // Whether the full serialized tag has to be kept so extraction hands the item back with its
        // mod state intact. A stack can need this and still pool freely.
        public static bool MustPreserveFullTag(bool hasModItemData, bool carriesModWrittenData)
            => hasModItemData || carriesModWrittenData;
    }
}
