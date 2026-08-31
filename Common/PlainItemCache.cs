using System.Collections.Concurrent;
using Terraria;

namespace TerraStorage.Common
{
    // A plain item of a given type and prefix, to compare a stored stack against. Reached from
    // packet handling as well as the UI, so the map is concurrent; the items themselves are only
    // ever read.
    public static class PlainItemCache
    {
        private static readonly ConcurrentDictionary<(int type, int prefix), Item> Items = new();

        public static Item Get(int itemType, int prefixId) => Items.GetOrAdd((itemType, prefixId), Build);

        private static Item Build((int type, int prefix) key)
        {
            var plain = new Item();
            plain.SetDefaults(key.type);

            // Rolled so the comparison item has a real prefix's stats, then forced, because a
            // prefix the item cannot roll would otherwise leave it at none and look like a
            // different item to anything comparing prefixes.
            plain.Prefix(key.prefix);
            plain.prefix = key.prefix;
            return plain;
        }

        public static void Clear() => Items.Clear();
    }
}
