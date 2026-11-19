using System.Collections.Concurrent;
using Terraria;
using Terraria.ModLoader.IO;

namespace TerraStorage.Common
{
    // A plain item of a given type and prefix, to compare a stored stack against. Reached from
    // packet handling as well as the UI, so the map is concurrent; the items themselves are only
    // ever read.
    public static class PlainItemCache
    {
        private static readonly ConcurrentDictionary<(int type, int prefix), Item> Items = new();
        private static readonly ConcurrentDictionary<(int type, int prefix), TagCompound> FullTags = new();

        public static Item Get(int itemType, int prefixId) => Items.GetOrAdd((itemType, prefixId), Build);

        // The full ItemIO tag of a plain copy, so a stored stack's mod-written state (globalData)
        // can be compared against what an ordinary copy of the same item carries. Only read.
        public static TagCompound GetFullTag(int itemType, int prefixId)
            => FullTags.GetOrAdd((itemType, prefixId), k => ItemIO.Save(Get(k.Item1, k.Item2)));

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

        public static void Clear() { Items.Clear(); FullTags.Clear(); }
    }
}
