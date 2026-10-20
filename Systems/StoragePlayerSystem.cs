using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.Audio;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using Terraria.UI;
using TerraStorage.Content.Items;
using TerraStorage.Content.Tiles;
using TerraStorage.Content.UI;

namespace TerraStorage.Systems
{
    // Per-player mod data that persists favorited items and recipes across sessions.
    // Favorites affect how items are sorted and displayed in the Terminal UI
    // (always appear first regardless of other sort settings). 
    public class StoragePlayerSystem : ModPlayer
    {
        // (itemType, prefixId) tuple used as key so the same item with different modifiers
        // can be favorited independently
        private readonly HashSet<(int type, int prefix)> _favoritedItems = new();
        private readonly HashSet<int> _favoritedRecipes = new();
        private bool _starterGiven;

        // Reverse lookup: Recipe reference → recipe index. Built lazily, avoids O(n)
        // Array.IndexOf scans in IsRecipeFavorited/ToggleRecipeFavorite.
        private static Dictionary<Recipe, int> _recipeIndexLookup;

        private static Dictionary<Recipe, int> RecipeIndexLookup
        {
            get
            {
                if (_recipeIndexLookup == null || _recipeIndexLookup.Count == 0)
                {
                    _recipeIndexLookup = new Dictionary<Recipe, int>(Recipe.numRecipes);
                    for (int i = 0; i < Recipe.numRecipes; i++)
                    {
                        if (Main.recipe[i] != null)
                            _recipeIndexLookup[Main.recipe[i]] = i;
                    }
                }
                return _recipeIndexLookup;
            }
        }

        // Last-opened terminal's disk IDs — used by tooltip overlay to show storage counts.
        // Session-only, not persisted.
        private List<Guid> _lastOpenedDiskIds = new();

        public IReadOnlyList<Guid> LastOpenedDiskIds => _lastOpenedDiskIds;

        // The Terminal those disks came from. Deposits name it instead of the disk list, because a
        // client-supplied GUID tells the server nothing about whose disk it is. The list stays for
        // the tooltip counts and the singleplayer path, which never cross the network.
        public int LastOpenedTerminalId { get; private set; } = -1;

        public void SetLastOpenedDiskIds(List<Guid> diskIds, int terminalEntityId)
        {
            _lastOpenedDiskIds = diskIds ?? new List<Guid>();
            LastOpenedTerminalId = terminalEntityId;
        }

        public static StoragePlayerSystem Local => Main.LocalPlayer.GetModPlayer<StoragePlayerSystem>();

        public bool IsItemFavorited(int type, int prefix) => _favoritedItems.Contains((type, prefix));

        //Returns true if this specific recipe variant is favorited.
        public bool IsRecipeFavorited(Recipe recipe)
        {
            if (_favoritedRecipes.Count == 0) return false;
            return RecipeIndexLookup.TryGetValue(recipe, out int idx) && _favoritedRecipes.Contains(idx);
        }

        public IReadOnlyCollection<int> FavoritedRecipes => _favoritedRecipes;

        // Changes whenever the favorited-recipes set changes. UI caches (the Favorited Recipes
        // panel's row cache) poll this instead of re-walking the set every frame. Drawn from a
        // process-global counter, never a per-instance one: the consuming cache outlives the
        // player instance, and a per-instance counter would hand every character's LoadData the
        // same value — a character switch would then never invalidate the cache.
        public long FavoritesVersion { get; private set; }

        private static long _favoritesVersionCounter;

        // Toggles the favorite state of an item. Uses a remove-first pattern:
        // if the key was already present it gets removed, otherwise it is added.
        public void ToggleItemFavorite(int type, int prefix)
        {
            var key = (type, prefix);
            if (!_favoritedItems.Remove(key))
                _favoritedItems.Add(key);
        }

        //Toggles the favorite state of this specific recipe variant.
        public void ToggleRecipeFavorite(Recipe recipe)
        {
            if (!RecipeIndexLookup.TryGetValue(recipe, out int idx)) return;
            if (!_favoritedRecipes.Remove(idx))
                _favoritedRecipes.Add(idx);
            FavoritesVersion = System.Threading.Interlocked.Increment(ref _favoritesVersionCounter);
        }

        public override void OnEnterWorld()
        {
            if (_starterGiven || !RequisitionConfig.Instance.QuickStarterPack)
                return;

            _starterGiven = true;

            Player.QuickSpawnItem(Player.GetSource_GiftOrReward(), ModContent.ItemType<TerminalItem>());
            Player.QuickSpawnItem(Player.GetSource_GiftOrReward(), ModContent.ItemType<CraftingCoreItem>());
            Player.QuickSpawnItem(Player.GetSource_GiftOrReward(), ModContent.ItemType<DriveBayItem>());
            Player.QuickSpawnItem(Player.GetSource_GiftOrReward(), ModContent.ItemType<StorageDiskTier2>());
        }

        //Whether the Crafting Tree auto-minimizes on middle-click recipe selection.
        public bool CraftingTreeAutoMinimize { get; set; } = true;

        public override bool ShiftClickSlot(Item[] inventory, int context, int slot)
        {
            if (context != ItemSlot.Context.InventoryItem)
                return false;

            // Terminal: deposit item into storage
            if (ModContent.GetInstance<TerminalUISystem>().IsTerminalOpen && _lastOpenedDiskIds.Count > 0)
                return DepositToStorage(inventory, slot);

            // Crafting Core: insert station item
            var coreEntity = ModContent.GetInstance<CraftingCoreUISystem>()?.OpenEntity;
            if (coreEntity != null)
                return InsertStation(inventory, slot, coreEntity);

            // Drive Bay: insert disk
            var bayEntity = ModContent.GetInstance<DriveBayUISystem>()?.OpenEntity;
            if (bayEntity != null)
                return InsertDisk(inventory, slot, bayEntity);

            return false;
        }

        private bool DepositToStorage(Item[] inventory, int slot)
        {
            var item = inventory[slot];
            if (item.IsAir)
                return true;

            if (Main.netMode == NetmodeID.MultiplayerClient)
            {
                var mod = ModLoader.GetMod("TerraStorage");
                NetworkHandler.SendDepositItem(mod, LastOpenedTerminalId, item);
                inventory[slot].TurnToAir();
            }
            else
            {
                int leftover = StorageWorldSystem.Instance.InsertItem(_lastOpenedDiskIds, item);
                if (leftover <= 0)
                    inventory[slot].TurnToAir();
                else
                    inventory[slot].stack = leftover;
            }

            SoundEngine.PlaySound(SoundID.Grab);
            return true;
        }

        private static bool InsertStation(Item[] inventory, int slot, CraftingCoreEntity entity)
        {
            var item = inventory[slot];
            if (item.IsAir || !CraftingCoreEntity.IsValidStation(item))
                return true;

            entity.EnsureSlotsInitialized();
            for (int s = 0; s < CraftingCoreEntity.StationSlotCount; s++)
            {
                if (entity.StationSlots[s].IsAir)
                {
                    entity.StationSlots[s] = item.Clone();
                    entity.StationSlots[s].stack = 1;
                    item.stack--;
                    if (item.stack <= 0)
                        inventory[slot].TurnToAir();
                    SoundEngine.PlaySound(SoundID.Grab);
                    var mod = ModLoader.GetMod("TerraStorage");
                    NetworkHandler.SendSyncStationInsert(mod, entity.ID, s, entity.StationSlots[s]);
                    return true;
                }
            }
            return true;
        }

        private static bool InsertDisk(Item[] inventory, int slot, DriveBayEntity entity)
        {
            var item = inventory[slot];
            if (item.IsAir || item.ModItem is not StorageDiskBase)
                return true;

            entity.EnsureSlotsInitialized();
            for (int s = 0; s < DriveBayEntity.DiskSlotCount; s++)
            {
                if (entity.DiskSlots[s].IsAir)
                {
                    if (!entity.InsertDisk(item, s))
                        return true;
                    inventory[slot].TurnToAir();
                    SoundEngine.PlaySound(SoundID.Grab);
                    var mod = ModLoader.GetMod("TerraStorage");
                    NetworkHandler.SendSyncDiskInsert(mod, entity.ID, s, entity.DiskSlots[s]);
                    return true;
                }
            }
            return true;
        }

        public override void SaveData(TagCompound tag)
        {
            // Serialize each favorited item as a small TagCompound with "t" (type) and "p" (prefix)
            tag["favItems"] = _favoritedItems
                .Select(f => new TagCompound { ["t"] = f.type, ["p"] = f.prefix })
                .ToList();
            tag["favRecipeIdx"] = _favoritedRecipes.ToList();
            tag["starterGiven"] = _starterGiven;
            tag["craftingTreeAutoMin"] = CraftingTreeAutoMinimize;
        }

        public override void LoadData(TagCompound tag)
        {
            _favoritedItems.Clear();
            _favoritedRecipes.Clear();

            if (tag.ContainsKey("favItems"))
                foreach (var e in tag.GetList<TagCompound>("favItems"))
                    _favoritedItems.Add((e.GetInt("t"), e.GetInt("p")));

            if (tag.ContainsKey("favRecipeIdx"))
                foreach (int recipeIdx in tag.GetList<int>("favRecipeIdx"))
                    _favoritedRecipes.Add(recipeIdx);

            _starterGiven = tag.GetBool("starterGiven");
            CraftingTreeAutoMinimize = !tag.ContainsKey("craftingTreeAutoMin") || tag.GetBool("craftingTreeAutoMin");
            FavoritesVersion = System.Threading.Interlocked.Increment(ref _favoritesVersionCounter);
        }
    }
}
