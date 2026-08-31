using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using Terraria.DataStructures;
using Terraria.ModLoader;
using Terraria.Map;
using TerraStorage.Common;
using TerraStorage.Content.Items;
using TerraStorage.Helpers.Resolver;
using TerraStorage.Systems;

namespace TerraStorage.Helpers
{
    // Represents a single intermediate crafting operation within a <see cref="CraftingPlan"/>.
    // Each step records what recipe is used, how many times it must be crafted, which items are
    // consumed and produced, and what stations that step requires.
    public class CraftingStep
    {
        public Recipe Recipe { get; set; }
        //Number of times the recipe must be executed to satisfy demand.
        public int CraftCount { get; set; }
        //Maps item type → total quantity consumed across all <see cref="CraftCount"/> executions.
        public Dictionary<int, int> Consumed { get; set; } = new();
        public int ProducedType { get; set; }
        public int ProducedCount { get; set; }
        public List<int> RequiredStations { get; set; } = new();
    }

    // The full resolved crafting plan for producing a target item. Contains all intermediate
    // <see cref="CraftingStep"/>s in bottom-up order, aggregate material requirements, and
    // feasibility information including missing stations or materials. 
    public class CraftingPlan
    {
        //Ordered list of intermediate crafting steps (dependencies first, target last).
        public List<CraftingStep> Steps { get; set; } = new();
        //Total raw materials needed across all steps (not counting intermediate products).
        public Dictionary<int, int> BaseMaterialsRequired { get; set; } = new();
        //Subset of <see cref="BaseMaterialsRequired"/> items that are not fully available.
        public Dictionary<int, int> BaseMaterialsMissing { get; set; } = new();
        public HashSet<int> RequiredStations { get; set; } = new();
        public HashSet<int> MissingStations { get; set; } = new();
        public bool IsFeasible { get; set; }
        public int FinalItemType { get; set; }
        public int FinalItemCount { get; set; }
        //Item was already in storage/inventory - no crafting steps needed.
        public bool IsDirectExtract => IsFeasible && Steps.Count == 0;
    }

    // Resolves multi-step crafting plans by recursively traversing the recipe tree,
    // checking material availability across storage and the player's inventory,
    // and verifying station/condition requirements against the connected network. 
    public static class RecipeResolver
    {
        // Limits recursive ingredient expansion to prevent infinite loops in circular recipe graphs
        public static int MaxDepth { get; set; } = 10;

        // Builds the pure resolver bound to the live recipe world for this request. All recursive
        // decisions are delegated here; the same CoreResolver is exercised directly by the unit tests.
        private static CoreResolver Core(HashSet<int> availableStations, HashSet<CraftingCondition> availableConditions)
            => new CoreResolver(new TerrariaRecipeEnvironment(availableStations, availableConditions)) { MaxDepth = MaxDepth };

        // Maps a resolved core step back to the Terraria-facing step (re-attaching the concrete Recipe).
        private static CraftingStep MapStep(CoreStep s) => new CraftingStep
        {
            Recipe = (Recipe)s.Recipe.Source,
            CraftCount = s.CraftCount,
            Consumed = s.Consumed,
            ProducedType = s.ProducedType,
            ProducedCount = s.ProducedCount,
            RequiredStations = s.RequiredStations
        };

        // Cache for tile name lookups (populated lazily, or pre-warmed via WarmTileCaches)
        private static readonly Dictionary<int, string> _tileNameCache = new();

        // Checks if a required tile type is satisfied by any station in the set,
        // accounting for adjTile equivalences (e.g. Mythril Anvil satisfies Anvil).
        public static bool IsStationSatisfied(int requiredTile, HashSet<int> availableStations)
        {
            if (availableStations.Contains(requiredTile))
                return true;

            // Check if any available station counts as the required tile via adjTile
            foreach (int station in availableStations)
            {
                foreach (int adj in AdjTileHelper.GetAdjTiles(station))
                {
                    if (adj == requiredTile)
                        return true;
                }
            }

            return false;
        }

        // Resolve a recipe recursively, determining what base materials are needed
        // and what intermediate crafting steps must be performed.
        // Checks crafting station requirements against available stations.
        public static CraftingPlan Resolve(int targetItemType, int quantity, IEnumerable<Guid> diskIds, HashSet<int> availableStations, HashSet<CraftingCondition> availableConditions = null)
        {
            var available = GetAvailableItems(diskIds);
            availableConditions ??= new HashSet<CraftingCondition>();
            var plan = new CraftingPlan
            {
                FinalItemType = targetItemType,
                FinalItemCount = quantity
            };

            var coreSteps = new List<CoreStep>();
            var resolving = new HashSet<int>(); // cycle detection
            bool feasible = Core(availableStations, availableConditions)
                .ResolveRecursive(targetItemType, quantity, available, coreSteps, resolving, 0);
            foreach (var s in coreSteps)
                plan.Steps.Add(MapStep(s));

            // Collect required stations from successful steps only
            foreach (var step in plan.Steps)
            {
                foreach (int s in step.RequiredStations)
                    plan.RequiredStations.Add(s);
            }

            // Determine which stations are missing
            foreach (int stationType in plan.RequiredStations)
            {
                if (!IsStationSatisfied(stationType, availableStations))
                    plan.MissingStations.Add(stationType);
            }

            plan.IsFeasible = feasible && plan.MissingStations.Count == 0;

            // Calculate base materials
            CalculateBaseMaterials(plan, diskIds);

            return plan;
        }

        // Get the display name for a tile type (for UI display of station requirements).
        // Caches results for performance. 
        public static string GetTileName(int tileType)
        {
            if (_tileNameCache.TryGetValue(tileType, out string cached))
                return cached;

            // Reuse GetTileItemType to avoid a second full item scan
            int itemType = GetTileItemType(tileType);
            if (itemType > 0)
            {
                var tempItem = new Item();
                tempItem.SetDefaults(itemType);
                _tileNameCache[tileType] = tempItem.Name;
                return tempItem.Name;
            }

            // No item places this tile (e.g. Demon Altar). Use Terraria's map legend name.
            string mapName = Lang.GetMapObjectName(MapHelper.TileToLookup(tileType, 0));
            if (!string.IsNullOrWhiteSpace(mapName))
            {
                _tileNameCache[tileType] = mapName;
                return mapName;
            }

            string fallback = $"Tile #{tileType}";
            _tileNameCache[tileType] = fallback;
            return fallback;
        }

        // Get all available recipes that can be crafted with items in storage.
        // Filters by available crafting stations.
        public static List<Recipe> GetAvailableRecipes(IEnumerable<Guid> diskIds, HashSet<int> availableStations, HashSet<CraftingCondition> availableConditions = null)
        {
            var available = GetAvailableItems(diskIds);
            availableConditions ??= new HashSet<CraftingCondition>();
            var results = new List<Recipe>();

            for (int i = 0; i < Recipe.numRecipes; i++)
            {
                var recipe = Main.recipe[i];
                if (recipe?.createItem == null || recipe.createItem.type <= ItemID.None)
                    continue;
                if (recipe.createItem.Name == "Recipe Not Found")
                    continue;

                // Check crafting station requirements
                bool stationsMet = true;
                foreach (int tileType in recipe.requiredTile)
                {
                    if (tileType >= 0 && !IsStationSatisfied(tileType, availableStations))
                    {
                        stationsMet = false;
                        break;
                    }
                }
                if (!stationsMet)
                    continue;

                // Check ingredient availability
                bool canCraft = true;
                foreach (var ingredient in recipe.requiredItem)
                {
                    if (ingredient.type <= ItemID.None)
                        continue;

                    int needed = ingredient.stack;
                    bool found = false;

                    if (available.TryGetValue(ingredient.type, out int have) && have >= needed)
                        found = true;

                    // Check recipe groups
                    if (!found)
                    {
                        foreach (int groupId in recipe.acceptedGroups)
                        {
                            var group = RecipeGroup.recipeGroups[groupId];
                            if (group.ContainsItem(ingredient.type))
                            {
                                foreach (int validItem in group.ValidItems)
                                {
                                    if (available.TryGetValue(validItem, out int groupHave) && groupHave >= needed)
                                    {
                                        found = true;
                                        break;
                                    }
                                }
                            }
                            if (found)
                                break;
                        }
                    }

                    if (!found)
                    {
                        canCraft = false;
                        break;
                    }
                }

                bool conditionsMet = CheckRecipeConditions(recipe, availableConditions);
                if (canCraft && conditionsMet)
                    results.Add(recipe);
            }

            return results;
        }

        // Checks if the player or the network meets the special conditions for a recipe. 
        //Public accessor for lightweight canCraft updates.
        public static bool CheckRecipeConditionsPublic(Recipe recipe, HashSet<CraftingCondition> conditions)
            => CheckRecipeConditions(recipe, conditions);

        private static bool CheckRecipeConditions(Recipe recipe, HashSet<CraftingCondition> availableConditions)
        {
            foreach (var condition in recipe.Conditions)
            {
                // First, check the original predicate (player's environment)
                if (condition.Predicate())
                    continue;

                // If that fails, check if a network provider satisfies it
                bool networkSatisfied = false;
                if (condition == Condition.NearWater && availableConditions.Contains(CraftingCondition.NearWater))
                    networkSatisfied = true;
                else if (condition == Condition.NearLava && availableConditions.Contains(CraftingCondition.NearLava))
                    networkSatisfied = true;
                else if (condition == Condition.NearHoney && availableConditions.Contains(CraftingCondition.NearHoney))
                    networkSatisfied = true;
                else if (condition == Condition.InGraveyard && availableConditions.Contains(CraftingCondition.InGraveyard))
                    networkSatisfied = true;
                else if (condition == Condition.InSnow && availableConditions.Contains(CraftingCondition.InSnow))
                    networkSatisfied = true;
                if (!networkSatisfied)
                    return false; // Condition not met by player or network
            }

            return true;
        }

        // Get all recipes. Returns (recipe, canCraft) where canCraft is true only if
        // both station requirements AND ingredient requirements are met.
        // Includes recursive craftability (expensive). Use <see cref="GetAllRecipesDirect"/>
        // for just the direct-check pass.
        public static List<(Recipe recipe, bool canCraft)> GetAllRecipesWithStations(
            IEnumerable<Guid> diskIds, HashSet<int> availableStations, HashSet<CraftingCondition> availableConditions = null)
        {
            var diskList = diskIds as List<Guid> ?? diskIds.ToList();
            var available = GetAvailableItems(diskList);
            availableConditions ??= new HashSet<CraftingCondition>();

            var results = GetAllRecipesDirect(available, availableStations, availableConditions);
            ApplyRecursiveCraftability(results, available, availableStations, availableConditions);
            return results;
        }

        // Fast first pass: returns all valid recipes with direct ingredient availability only.
        // No BFS reachability or recursive feasibility checks — O(n) single pass.
        public static List<(Recipe recipe, bool canCraft)> GetAllRecipesDirect(
            IEnumerable<Guid> diskIds, HashSet<int> availableStations, HashSet<CraftingCondition> availableConditions = null)
        {
            var diskList = diskIds as List<Guid> ?? diskIds.ToList();
            var available = GetAvailableItems(diskList);
            availableConditions ??= new HashSet<CraftingCondition>();
            return GetAllRecipesDirect(available, availableStations, availableConditions);
        }

        private static List<(Recipe recipe, bool canCraft)> GetAllRecipesDirect(
            Dictionary<int, int> available, HashSet<int> availableStations, HashSet<CraftingCondition> availableConditions)
        {
            var results = new List<(Recipe, bool)>();

            for (int i = 0; i < Recipe.numRecipes; i++)
            {
                var recipe = Main.recipe[i];
                if (recipe?.createItem == null || recipe.createItem.type <= ItemID.None)
                    continue;
                if (recipe.createItem.Name == "Recipe Not Found")
                    continue;

                bool stationsMet = true;
                foreach (int tileType in recipe.requiredTile)
                {
                    if (tileType >= 0 && !IsStationSatisfied(tileType, availableStations))
                    {
                        stationsMet = false;
                        break;
                    }
                }

                // Force-craft semantics (see CoreResolver.RecheckRecipeCraftable): the craft button
                // ignores existing stock of the output, so the output cannot be its own material.
                int outputType = recipe.createItem.type;

                bool ingredientsMet = true;
                foreach (var ingredient in recipe.requiredItem)
                {
                    if (ingredient.type <= ItemID.None)
                        continue;

                    int needed = ingredient.stack;
                    bool found = false;

                    if (ingredient.type != outputType
                        && available.TryGetValue(ingredient.type, out int have) && have >= needed)
                        found = true;

                    if (!found)
                    {
                        foreach (int groupId in recipe.acceptedGroups)
                        {
                            var group = RecipeGroup.recipeGroups[groupId];
                            if (group.ContainsItem(ingredient.type))
                            {
                                foreach (int validItem in group.ValidItems)
                                {
                                    if (validItem != outputType
                                        && available.TryGetValue(validItem, out int groupHave) && groupHave >= needed)
                                    {
                                        found = true;
                                        break;
                                    }
                                }
                            }
                            if (found)
                                break;
                        }
                    }

                    if (!found)
                    {
                        ingredientsMet = false;
                        break;
                    }
                }

                bool conditionsMet = CheckRecipeConditions(recipe, availableConditions);
                bool canCraft = stationsMet && ingredientsMet && conditionsMet;
                results.Add((recipe, canCraft));
            }

            return results;
        }

        // Second pass: recomputes each recipe's craftability authoritatively (direct OR recursive),
        // promoting recipes whose ingredients are recursively feasible and demoting ones that are
        // no longer craftable. Call this after <see cref="GetAllRecipesDirect"/>; re-running it over
        // a fresh snapshot fully corrects stale flags. Can be driven incrementally across frames via
        // <see cref="ApplyRecursiveCraftabilityBatch"/>.
        public static void ApplyRecursiveCraftability(
            List<(Recipe recipe, bool canCraft)> results,
            Dictionary<int, int> available,
            HashSet<int> availableStations,
            HashSet<CraftingCondition> availableConditions)
        {
            var core = Core(availableStations, availableConditions);
            var reachable = core.ComputeReachableTypes(available);
            var ingCache = new Dictionary<(int ctx, int group, int type, int stack), bool>();
            for (int i = 0; i < results.Count; i++)
                results[i] = (results[i].recipe,
                    core.IsRecipeCraftable(TerrariaRecipeEnvironment.ToCore(results[i].recipe), reachable, available, ingCache));
        }

        // Processes a batch of the recursive craftability pass. Returns the index to resume from
        // next frame, or -1 if complete. Caller should pass startIndex=0 on first call, then
        // feed the returned value back on subsequent frames.
        // <param name="reachable">Pre-computed reachable set from <see cref="ComputeReachableTypesPublic"/>.</param>
        // <param name="ingCache">Shared ingredient cache — pass the same instance across batches.</param>
        // <param name="anyFlipped">Set to true if any recipe's canCraft changed in this batch.</param>
        public static int ApplyRecursiveCraftabilityBatch(
            List<(Recipe recipe, bool canCraft)> results,
            int startIndex, int batchSize,
            HashSet<int> reachable,
            Dictionary<int, int> available,
            HashSet<int> availableStations,
            HashSet<CraftingCondition> availableConditions,
            Dictionary<(int ctx, int group, int type, int stack), bool> ingCache,
            out bool anyFlipped)
        {
            anyFlipped = false;
            int end = Math.Min(startIndex + batchSize, results.Count);
            var core = Core(availableStations, availableConditions);
            for (int i = startIndex; i < end; i++)
            {
                bool wasCraftable = results[i].canCraft;
                bool nowCraftable = core.IsRecipeCraftable(
                    TerrariaRecipeEnvironment.ToCore(results[i].recipe), reachable, available, ingCache);
                results[i] = (results[i].recipe, nowCraftable);
                // The check is authoritative (it can demote as well as promote), so surface any
                // change — a recipe that became uncraftable must leave the list too.
                if (wasCraftable != nowCraftable)
                    anyFlipped = true;
            }
            return end >= results.Count ? -1 : end;
        }

        //Expose BFS reachability computation for deferred use.
        public static HashSet<int> ComputeReachableTypesPublic(
            Dictionary<int, int> available, HashSet<int> availableStations, HashSet<CraftingCondition> availableConditions)
            => Core(availableStations, availableConditions).ComputeReachableTypes(available);

        // Get the item type that places a given tile type. Returns -1 if not found.
        // Cached for performance. Use <see cref="RegisterTileDisplay"/> to map vanilla
        // non-placeable tile types (e.g. Demon Altar) to a representative item.
        private static readonly Dictionary<int, int> _tileToItemCache = new();
        private static readonly Dictionary<int, int> _tileDisplayOverrides = new();

        // Registers an explicit item to display for a tile type that has no directly
        // placeable item (e.g. TileID.DemonAltar → DemonAltarItem).
        // Call from PostSetupContent after ModContent types are resolved. 
        public static void RegisterTileDisplay(int tileType, int itemType)
        {
            _tileDisplayOverrides[tileType] = itemType;
        }

        // Builds the full tile→item mapping in a single pass over all items.
        // Call once after PostSetupContent (e.g. on world load) to avoid per-tile
        // linear scans that cause hitching on first hover.
        public static void WarmTileCaches()
        {
            if (_tileToItemCache.Count > 0) return; // already warmed

            // Apply overrides first so they take priority
            foreach (var kvp in _tileDisplayOverrides)
                _tileToItemCache[kvp.Key] = kvp.Value;

            // Single pass: map every tile type to the first item that places it
            for (int i = 1; i < ItemLoader.ItemCount; i++)
            {
                var tempItem = new Item();
                tempItem.SetDefaults(i);
                int tile = tempItem.createTile;
                if (tile >= 0 && !_tileToItemCache.ContainsKey(tile))
                    _tileToItemCache[tile] = i;
            }

            // Also pre-warm tile name cache from the results
            foreach (var kvp in _tileToItemCache)
            {
                if (kvp.Value > 0 && !_tileNameCache.ContainsKey(kvp.Key))
                {
                    var tempItem = new Item();
                    tempItem.SetDefaults(kvp.Value);
                    _tileNameCache[kvp.Key] = tempItem.Name;
                }
            }
        }

        public static int GetTileItemType(int tileType)
        {
            if (_tileToItemCache.TryGetValue(tileType, out int itemType))
                return itemType;

            if (_tileDisplayOverrides.TryGetValue(tileType, out int overrideType))
            {
                _tileToItemCache[tileType] = overrideType;
                return overrideType;
            }

            // Fallback for tiles not found during warm-up (shouldn't happen after WarmTileCaches)
            for (int i = 1; i < ItemLoader.ItemCount; i++)
            {
                var tempItem = new Item();
                tempItem.SetDefaults(i);
                if (tempItem.createTile == tileType)
                {
                    _tileToItemCache[tileType] = i;
                    return i;
                }
            }

            _tileToItemCache[tileType] = -1;
            return -1;
        }

        // Like Resolve but ignores existing stock of the target item, forcing actual crafting.
        // Returns null if the item cannot be crafted with available ingredients.
        public static CraftingPlan ResolveForceCraft(int targetItemType, int quantity, IEnumerable<Guid> diskIds, HashSet<int> availableStations, HashSet<CraftingCondition> availableConditions = null)
        {
            var available = GetAvailableItems(diskIds);
            availableConditions ??= new HashSet<CraftingCondition>();
            // Remove existing target items so resolver must craft them
            available.Remove(targetItemType);

            var plan = new CraftingPlan
            {
                FinalItemType = targetItemType,
                FinalItemCount = quantity
            };

            var coreSteps = new List<CoreStep>();
            var resolving = new HashSet<int>();
            bool feasible = Core(availableStations, availableConditions)
                .ResolveRecursive(targetItemType, quantity, available, coreSteps, resolving, 0);
            foreach (var s in coreSteps)
                plan.Steps.Add(MapStep(s));

            foreach (var step in plan.Steps)
            {
                foreach (int s in step.RequiredStations)
                    plan.RequiredStations.Add(s);
            }

            foreach (int stationType in plan.RequiredStations)
            {
                if (!IsStationSatisfied(stationType, availableStations))
                    plan.MissingStations.Add(stationType);
            }

            plan.IsFeasible = feasible && plan.MissingStations.Count == 0 && plan.Steps.Count > 0;
            if (plan.IsFeasible)
                CalculateBaseMaterials(plan, diskIds);

            return plan.IsFeasible ? plan : null;
        }

        // Like Resolve, but forces a SPECIFIC recipe for the target item instead of auto-selecting the
        // best one. Used by the crafting panel's "lock recipe" option so the plan and the actual craft
        // use exactly the recipe the player selected. Ignores existing stock of the output (force-craft),
        // sub-resolving ingredients via the normal station-preferring ResolveRecursive. Returns the plan
        // ALWAYS (with MissingStations populated), like Resolve — a recipe that cannot be satisfied yields
        // a plan with IsFeasible == false.
        public static CraftingPlan ResolveRecipe(Recipe recipe, int quantity, IEnumerable<Guid> diskIds,
            HashSet<int> availableStations, HashSet<CraftingCondition> availableConditions = null)
        {
            availableConditions ??= new HashSet<CraftingCondition>();
            var plan = new CraftingPlan
            {
                FinalItemType = recipe?.createItem?.type ?? 0,
                FinalItemCount = quantity
            };
            if (recipe?.createItem == null || recipe.createItem.type <= ItemID.None)
                return plan; // null/invalid recipe → infeasible empty plan

            int itemType = recipe.createItem.type;
            var available = GetAvailableItems(diskIds);
            available.Remove(itemType); // force crafting via this recipe; don't direct-extract the output

            var coreRecipe = TerrariaRecipeEnvironment.ToCore(recipe);
            var coreSteps = new List<CoreStep>();
            var resolving = new HashSet<int> { itemType };
            bool feasible = CheckRecipeConditions(recipe, availableConditions)
                && Core(availableStations, availableConditions)
                    .TryResolveRecipe(coreRecipe, itemType, quantity, available, coreSteps, resolving, 0);
            foreach (var s in coreSteps)
                plan.Steps.Add(MapStep(s));

            foreach (var step in plan.Steps)
                foreach (int s in step.RequiredStations)
                    plan.RequiredStations.Add(s);
            foreach (int stationType in plan.RequiredStations)
                if (!IsStationSatisfied(stationType, availableStations))
                    plan.MissingStations.Add(stationType);

            plan.IsFeasible = feasible && plan.MissingStations.Count == 0 && plan.Steps.Count > 0;
            if (feasible)
                CalculateBaseMaterials(plan, diskIds);

            return plan;
        }

        // Builds a mutable snapshot of all items available for crafting from the given disks.
        // This snapshot is modified in-place by the resolver to simulate consumption.
        // Builds a dictionary of itemType → total count across all given disks.
        // Public so the crafting panel can use it for lightweight canCraft updates. 
        public static Dictionary<int, int> GetAvailableItemsPublic(IEnumerable<Guid> diskIds)
            => GetAvailableItems(diskIds);

        private static Dictionary<int, int> GetAvailableItems(IEnumerable<Guid> diskIds)
        {
            var items = new Dictionary<int, int>();
            var consolidated = StorageWorldSystem.Instance.GetConsolidatedItems(diskIds);

            foreach (var ci in consolidated)
            {
                if (!items.ContainsKey(ci.ItemType))
                    items[ci.ItemType] = 0;
                items[ci.ItemType] += ci.TotalCount;
            }

            return items;
        }

        // Populates <see cref="CraftingPlan.BaseMaterialsRequired"/> and
        // <see cref="CraftingPlan.BaseMaterialsMissing"/> by computing the net material demand
        // after subtracting intermediate products produced by earlier steps.
        private static void CalculateBaseMaterials(CraftingPlan plan, IEnumerable<Guid> diskIds)
        {
            var produced = new Dictionary<int, int>();
            var consumed = new Dictionary<int, int>();

            foreach (var step in plan.Steps)
            {
                foreach (var kvp in step.Consumed)
                {
                    if (!consumed.ContainsKey(kvp.Key))
                        consumed[kvp.Key] = 0;
                    consumed[kvp.Key] += kvp.Value;
                }

                if (!produced.ContainsKey(step.ProducedType))
                    produced[step.ProducedType] = 0;
                produced[step.ProducedType] += step.ProducedCount;
            }

            foreach (var kvp in consumed)
            {
                // Subtract any amount already produced by an earlier step in the chain
                produced.TryGetValue(kvp.Key, out int prod);
                int netRequired = kvp.Value - prod;
                if (netRequired > 0)
                {
                    plan.BaseMaterialsRequired[kvp.Key] = netRequired;

                    int inStorage = StorageWorldSystem.Instance.CountItem(diskIds, kvp.Key);
                    if (inStorage < netRequired)
                        plan.BaseMaterialsMissing[kvp.Key] = netRequired - inStorage;
                }
            }
        }

        // Binds the transaction core to the live storage network. The core does the bookkeeping;
        // this only forwards the four operations it needs.
        private class WorldCraftingStorage : ICraftingStorage<Item>
        {
            private readonly List<Guid> _diskList;

            public WorldCraftingStorage(List<Guid> diskList)
            {
                _diskList = diskList;
            }

            public Item Nothing => new Item();

            public int CountItem(int itemType) => StorageWorldSystem.Instance.CountItem(_diskList, itemType);

            public List<Item> ExtractStacks(int itemType, int amount)
                => StorageWorldSystem.Instance.ExtractItemStacks(_diskList, itemType, amount);

            public int ExtractStored(Item stored, int count)
            {
                Item recovered = StorageWorldSystem.Instance.ExtractStoredItem(_diskList, stored, count);
                return recovered.IsAir ? 0 : recovered.stack;
            }

            public int Insert(Item item) => StorageWorldSystem.Instance.InsertItem(_diskList, item);

            public int StackOf(Item item) => item == null || item.IsAir ? 0 : item.stack;

            public Item SplitOff(Item item, int count)
            {
                var part = item.Clone();
                part.stack = count;
                item.stack -= count;
                return part;
            }

            public bool SameStoredState(Item first, Item second)
                => StorageWorldSystem.ItemsShareStoredState(first, second);
        }

        // Acquires every listed material, crafting shortfalls, and consumes them as one transaction:
        // either the whole list is taken and true is returned, or nothing is consumed and everything
        // already taken is put back. Both disk-upgrade paths (the panel in single player, the packet
        // handler on a server) go through here so neither can install an upgrade it did not pay for.
        //
        // Each ingredient is resolved for its FULL need, not the shortfall. Asking the resolver for
        // `need - have` double-counts: it rebuilds its pool from all of storage, sees the stock the
        // caller already subtracted, and reports a direct extract with no steps — feasible, free, and
        // wrong.
        public static bool TryConsumeMaterials(IEnumerable<Guid> diskIds, IEnumerable<(int itemType, int count)> materials,
            HashSet<int> availableStations, HashSet<CraftingCondition> availableConditions = null)
        {
            var diskList = diskIds.ToList();
            var storage = new WorldCraftingStorage(diskList);

            var consumer = new MaterialConsumer<Item>(storage,
                (itemType, need) => CraftShortfall(itemType, need, diskList, availableStations, availableConditions));

            return consumer.TryConsume(materials);
        }

        // Produces `need` of a material the network is short of. A plan with no steps is a direct
        // extract, which cannot be right here: we are only asked once storage holds less than `need`.
        private static Item CraftShortfall(int itemType, int need, List<Guid> diskList,
            HashSet<int> availableStations, HashSet<CraftingCondition> availableConditions)
        {
            var plan = Resolve(itemType, need, diskList, availableStations, availableConditions);
            if (plan == null || !plan.IsFeasible || plan.Steps.Count == 0)
                return new Item();

            return ExecutePlan(plan, diskList);
        }

        // Execute a crafting plan, consuming items from storage and player inventory,
        // producing the result back into storage. 
        public static Item ExecutePlan(CraftingPlan plan, IEnumerable<Guid> diskIds, bool cleanCraft = true)
        {
            if (!plan.IsFeasible)
                return new Item();

            var diskList = diskIds.ToList();
            var storage = new WorldCraftingStorage(diskList);
            var producer = new PlanStepProducer(plan, diskList);

            Item finalResult = new PlanExecutor<Item>(storage)
                .Run(ToExecutionSteps(plan), plan.FinalItemCount, producer);

            // Apply vanilla crafting simulation (prefix rolling + mod hooks) on the
            // final result, unless Clean Craft is enabled or this is a disk upgrade.
            // Runs after all steps complete so ingredients are safely consumed even if
            // a mod hook throws an exception.
            if (!cleanCraft && !finalResult.IsAir && plan.Steps.Count > 0
                && !(finalResult.ModItem is StorageDiskBase))
            {
                var finalStep = plan.Steps[^1];
                if (finalStep.Recipe != null)
                {
                    int savedStack = finalResult.stack;

                    // Roll a random prefix (no-op for items that can't have prefixes)
                    try { finalResult.Prefix(-1); }
                    catch { /* prefix rolling failed — item stays unmodified */ }
                    finalResult.stack = savedStack;

                    // Build consumed items list for mod hooks
                    var consumedItems = new List<Item>();
                    foreach (var kvp in finalStep.Consumed)
                    {
                        var ci = new Item();
                        ci.SetDefaults(kvp.Key);
                        ci.stack = kvp.Value;
                        consumedItems.Add(ci);
                    }

                    // Fire OnCreated hooks with per-hook exception isolation.
                    // tModLoader's ItemLoader.OnCreated (also called internally by
                    // RecipeLoader.OnCraft) iterates all GlobalItem hooks in a single
                    // loop without try-catch — one mod throwing prevents all subsequent
                    // hooks from running (e.g. TerraCards' CardSystemItem never getting
                    // its card slots initialized). Iterating manually ensures every
                    // GlobalItem.OnCreated is called regardless of earlier failures.
                    var creationContext = new RecipeItemCreationContext(finalStep.Recipe, consumedItems, finalResult);
                    foreach (var gi in finalResult.EntityGlobals)
                    {
                        if (gi == null) continue;
                        try { gi.OnCreated(finalResult, creationContext); }
                        catch { }
                    }
                    if (finalResult.ModItem != null)
                    {
                        try { finalResult.ModItem.OnCreated(creationContext); }
                        catch { }
                    }

                    finalResult.stack = savedStack;
                }
            }

            return finalResult;
        }

        // Strips a plan down to what the transaction core needs: what each step costs and what it
        // makes. The Recipe itself is only needed for the prefix and mod-hook pass afterwards.
        private static List<ExecutionStep> ToExecutionSteps(CraftingPlan plan)
        {
            var steps = new List<ExecutionStep>(plan.Steps.Count);

            foreach (var step in plan.Steps)
            {
                var executionStep = new ExecutionStep
                {
                    ProducedType = step.ProducedType,
                    ProducedCount = step.ProducedCount
                };

                foreach (var kvp in step.Consumed)
                    executionStep.Consumed.Add((kvp.Key, kvp.Value));

                steps.Add(executionStep);
            }

            return steps;
        }

        // The Terraria half of executing a plan: building each step's item and carrying a disk's
        // identity across an upgrade. Material movement is the core's job.
        private class PlanStepProducer : IStepProducer<Item>
        {
            private readonly CraftingPlan _plan;
            private readonly List<Guid> _diskList;
            private Guid _sourceDiskGuid = Guid.Empty;

            public PlanStepProducer(CraftingPlan plan, List<Guid> diskList)
            {
                _plan = plan;
                _diskList = diskList;
            }

            // Detect disk upgrade steps (e.g. Tier1 → Tier2) before anything is consumed.
            // ExecutePlan bypasses Terraria's crafting pipeline entirely, so the
            // AddOnCraftCallback registered in DiskRecipes never fires here. We must replicate
            // the same GUID-transfer logic manually, and the GUID has to be read while the source
            // disk is still in storage.
            public void PrepareStep(int stepIndex)
            {
                _sourceDiskGuid = Guid.Empty;

                if (IsDiskUpgradeStep(_plan.Steps[stepIndex], out int sourceDiskItemType))
                    _sourceDiskGuid = FindDiskGuidInStorage(_diskList, sourceDiskItemType);
            }

            public Item ProduceStep(int stepIndex)
            {
                var step = _plan.Steps[stepIndex];

                var produced = new Item();
                produced.SetDefaults(step.ProducedType);
                produced.stack = step.ProducedCount;

                // Transfer the source GUID to the newly produced disk and upgrade its
                // DiskData tier in-place, preserving all previously stored items.
                if (_sourceDiskGuid != Guid.Empty && produced.ModItem is StorageDiskBase resultDisk)
                {
                    resultDisk.AssignDiskId(_sourceDiskGuid);
                    StorageWorldSystem.Instance.UpgradeDisk(_sourceDiskGuid, resultDisk.Tier);
                }

                return produced;
            }
        }

        // Returns true if <paramref name="step"/> produces a Storage Disk by consuming a
        // lower-tier disk as an ingredient. Sets <paramref name="sourceDiskItemType"/> to
        // the consumed disk's item type so the caller can look up its GUID.
        // 
        private static bool IsDiskUpgradeStep(CraftingStep step, out int sourceDiskItemType)
        {
            sourceDiskItemType = -1;

            // Result must be a Storage Disk
            var resultCheck = new Item();
            resultCheck.SetDefaults(step.ProducedType);
            if (resultCheck.ModItem is not StorageDiskBase)
                return false;

            // One of the consumed items must also be a Storage Disk (the source being upgraded)
            foreach (var consumed in step.Consumed)
            {
                var temp = new Item();
                temp.SetDefaults(consumed.Key);
                if (temp.ModItem is not StorageDiskBase)
                    continue;

                // Exactly one, because only one GUID is read and only one result Item is built:
                // a batched step consuming several would stamp that GUID on every disk it made
                // and orphan the rest. Refusing here leaves the step to craft as an ordinary
                // recipe, which is wrong in a way the player can see rather than a duplication.
                if (consumed.Value != 1)
                    return false;

                sourceDiskItemType = consumed.Key;
                return true;
            }

            return false;
        }

        // Scans the storage network for a stored disk item of <paramref name="diskItemType"/>
        // and returns its <see cref="StorageDiskBase.DiskId"/>.
        // Returns <see cref="Guid.Empty"/> if none is found.
        private static Guid FindDiskGuidInStorage(List<Guid> diskList, int diskItemType)
        {
            var sys = StorageWorldSystem.Instance;
            foreach (var diskId in diskList)
            {
                var data = sys.GetDiskData(diskId);
                if (data == null) continue;

                foreach (var stored in data.Items)
                {
                    if (stored.ItemType != diskItemType || stored.ModData == null) continue;

                    // Reconstruct the ModItem temporarily to read the GUID via LoadData
                    var temp = new Item();
                    temp.SetDefaults(diskItemType);
                    if (temp.ModItem is StorageDiskBase tempDisk)
                    {
                        tempDisk.LoadData(stored.ModData);
                        if (tempDisk.DiskId != Guid.Empty)
                            return tempDisk.DiskId;
                    }
                }
            }
            return Guid.Empty;
        }

        public static string GetGroupName(int groupId)
        {
            if (RecipeGroup.recipeGroups.TryGetValue(groupId, out var grp))
                return "Any " + Lang.GetItemNameValue(grp.IconicItemId);
            return "?";
        }

        public static string GetGroupItemNames(int groupId)
        {
            if (!RecipeGroup.recipeGroups.TryGetValue(groupId, out var grp))
                return "?";
            var names = new System.Text.StringBuilder();
            foreach (int v in grp.ValidItems)
            {
                if (names.Length > 0) names.Append(" / ");
                names.Append(Lang.GetItemNameValue(v));
            }
            return names.ToString();
        }
    }
}
