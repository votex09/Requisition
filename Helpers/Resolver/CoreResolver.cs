using System;
using System.Collections.Generic;
using System.Linq;

namespace TerraStorage.Helpers.Resolver
{
    // The pure crafting-resolution algorithm. Operates entirely through <see cref="IRecipeEnvironment"/>
    // and plain dictionaries/sets, so it carries no Terraria dependency and can be unit-tested with
    // synthetic recipe fixtures. The Terraria-facing RecipeResolver builds an environment adapter and
    // delegates every recursive decision here; the unit tests build a fake environment and call the
    // same methods, so the tested code is the shipped code.
    public sealed class CoreResolver
    {
        private readonly IRecipeEnvironment _env;
        public int MaxDepth = 10;

        // Reusable scratch for the allocation-free feasibility path (CanProduce). Each feasibility
        // query mutates the caller's snapshot and rolls it back fully before returning, recording
        // every change here — so one instance is reused across all recipes in a pass instead of
        // cloning a dictionary (and building throwaway step lists) on every recursive call.
        private readonly List<(int type, int amount)> _undo = new();
        private readonly HashSet<int> _feasibilityResolving = new();

        // Depth at which a query about one of a recipe's INGREDIENTS starts. ResolveRecursive enters
        // a recipe at depth 0 and resolves its ingredients at depth + 1, so a feasibility query that
        // begins at the ingredient is already one level in. Starting such a query at 0 hands it an
        // extra level of budget, and the list flag then accepts chains the craft button refuses —
        // exactly at MaxDepth == chainLength - 1. Item-level queries still start at 0.
        private const int IngredientDepth = 1;

        // How many craft counts past the bisected boundary ComputeMaxCraftAmount checks one by one,
        // covering the short dips that sub-craft rounding produces.
        private const int RoundingDipProbeWindow = 4;

        // How far ComputeMaxCraftAmount will step down looking for an amount the planner confirms.
        private const int PlannerConfirmSteps = 8;

        public CoreResolver(IRecipeEnvironment env)
        {
            _env = env;
        }

        public bool IsStationSatisfied(int tile) => _env.IsStationSatisfied(tile);

        // True if every station tile this recipe needs is available.
        public bool StationsAllSatisfied(CoreRecipe recipe)
        {
            foreach (int t in recipe.RequiredTiles)
                if (!_env.IsStationSatisfied(t))
                    return false;
            return true;
        }

        // True if a single ingredient is satisfied directly from stock — own type or a recipe-group
        // substitute — ignoring sub-crafting. viaGroup is set when satisfaction relied on a substitute.
        private bool IngredientSatisfiedDirectly(CoreRecipe recipe, int ingredientType, int needed,
            Dictionary<int, int> available, out bool viaGroup)
        {
            viaGroup = false;
            if (available.TryGetValue(ingredientType, out int have) && have >= needed)
                return true;

            foreach (int groupId in recipe.AcceptedGroups)
            {
                if (!_env.GroupContains(groupId, ingredientType)) continue;
                foreach (int validItem in _env.GroupValidItems(groupId))
                {
                    if (available.TryGetValue(validItem, out int groupHave) && groupHave >= needed)
                    {
                        viaGroup = true;
                        return true;
                    }
                }
            }
            return false;
        }

        // Recursively satisfies a demand for `needed` units of `itemType` against the mutable
        // `available` pool, appending the required steps. Deducts consumed quantities from the pool
        // so siblings cannot each claim the same stock. Returns false if it cannot be met.
        public bool ResolveRecursive(int itemType, int needed, Dictionary<int, int> available,
            List<CoreStep> steps, HashSet<int> resolving, int depth)
        {
            if (depth > MaxDepth)
                return false;

            if (available.TryGetValue(itemType, out int have) && have >= needed)
            {
                available[itemType] -= needed;
                return true;
            }

            int deficit = needed;
            if (have > 0)
            {
                deficit -= have;
                available[itemType] = 0;
            }

            // Partial stock is spent above, before we know a plan exists. Every false path from here
            // has to hand it back: the contract is "returns false if it cannot be met", and a caller
            // that reads its pool afterwards must not find it quietly drained.
            if (!resolving.Add(itemType))
            {
                if (have > 0) available[itemType] = have;
                return false;
            }

            var recipes = _env.RecipesProducing(itemType);

            IEnumerable<CoreRecipe> ordered = recipes;
            if (recipes.Count > 1)
                ordered = recipes.OrderByDescending(r => StationsAllSatisfied(r));

            List<CoreStep> fallbackSteps = null;
            Dictionary<int, int> fallbackAvailable = null;

            foreach (var recipe in ordered)
            {
                if (!_env.ConditionsMet(recipe))
                    continue;

                int stepsBefore = steps.Count;
                var availSnapshot = new Dictionary<int, int>(available);

                if (!TryResolveRecipe(recipe, itemType, deficit, available, steps, resolving, depth))
                    continue;

                bool stationsComplete = true;
                for (int si = stepsBefore; si < steps.Count && stationsComplete; si++)
                    foreach (int st in steps[si].RequiredStations)
                        if (!_env.IsStationSatisfied(st)) { stationsComplete = false; break; }

                if (stationsComplete)
                {
                    resolving.Remove(itemType);
                    return true;
                }

                if (fallbackSteps == null)
                {
                    fallbackSteps = steps.GetRange(stepsBefore, steps.Count - stepsBefore);
                    fallbackAvailable = new Dictionary<int, int>(available);
                }
                steps.RemoveRange(stepsBefore, steps.Count - stepsBefore);
                available.Clear();
                foreach (var kvp in availSnapshot)
                    available[kvp.Key] = kvp.Value;
            }

            if (fallbackSteps != null)
            {
                steps.AddRange(fallbackSteps);
                available.Clear();
                foreach (var kvp in fallbackAvailable)
                    available[kvp.Key] = kvp.Value;
                resolving.Remove(itemType);
                return true;
            }

            resolving.Remove(itemType);
            if (have > 0) available[itemType] = have;
            return false;
        }

        // Satisfies `deficit` units of `itemType` via one specific recipe. On success appends the
        // sub-steps and this recipe's step, credits overproduction back to the pool, returns true.
        // On ingredient failure rolls the pool back and returns false. Caller owns the `resolving`
        // entry for itemType and the per-recipe condition check.
        // Fills ONE ingredient slot, recording what it actually costs into `consumed`.
        //
        // A recipe-group slot draws from every accepted member, not just one: vanilla counts the
        // group in aggregate, so 3 gold bars plus 7 platinum bars fill a 10-bar slot. Committing the
        // whole slot to a single concrete type made holding a few of the named item turn a craftable
        // recipe uncraftable. Stock is taken first, cheapest to reason about, and only the remainder
        // is sub-crafted — through the named type where possible, otherwise through a substitute.
        //
        // `consumed` accumulates because a recipe may name one item in two slots, and it drives how
        // much ExecutePlan extracts; overwriting recorded only the last slot.
        private bool ResolveIngredientSlot(CoreRecipe recipe, int ingredientType, int needed,
            Dictionary<int, int> available, List<CoreStep> steps, HashSet<int> resolving, int depth,
            Dictionary<int, int> consumed)
        {
            int remaining = needed;

            foreach (int candidate in IngredientCandidates(recipe, ingredientType))
            {
                if (remaining <= 0) break;
                if (!available.TryGetValue(candidate, out int have) || have <= 0) continue;

                int taken = Math.Min(remaining, have);
                available[candidate] = have - taken;
                consumed.TryGetValue(candidate, out int already);
                consumed[candidate] = already + taken;
                remaining -= taken;
            }

            if (remaining <= 0)
                return true;

            foreach (int candidate in IngredientCandidates(recipe, ingredientType))
            {
                if (!ResolveRecursive(candidate, remaining, available, steps, resolving, depth + 1))
                    continue;

                consumed.TryGetValue(candidate, out int already);
                consumed[candidate] = already + remaining;
                return true;
            }

            return false;
        }

        // The item types that may fill a slot for `ingredientType`: the named type first, then the
        // members of whichever accepted recipe group contains it.
        private IEnumerable<int> IngredientCandidates(CoreRecipe recipe, int ingredientType)
        {
            yield return ingredientType;

            foreach (int groupId in recipe.AcceptedGroups)
            {
                if (!_env.GroupContains(groupId, ingredientType)) continue;
                foreach (int validItem in _env.GroupValidItems(groupId))
                    if (validItem != ingredientType)
                        yield return validItem;
                yield break;
            }
        }

        public bool TryResolveRecipe(CoreRecipe recipe, int itemType, int deficit,
            Dictionary<int, int> available, List<CoreStep> steps, HashSet<int> resolving, int depth)
        {
            var stepStations = new List<int>(recipe.RequiredTiles);

            int craftsNeeded = (int)Math.Ceiling((double)deficit / recipe.OutputStack);

            var availBackup = new Dictionary<int, int>(available);
            var tempSteps = new List<CoreStep>();
            var consumed = new Dictionary<int, int>();

            foreach (var ingredient in recipe.Ingredients)
            {
                // Each level of the chain multiplies the demand again. Wrapped into a negative it
                // costs nothing to fill, so a plan reads as built when it cannot be.
                long slotDemand = (long)ingredient.Stack * craftsNeeded;
                int ingredientNeeded = slotDemand > int.MaxValue ? int.MaxValue : (int)slotDemand;

                if (!ResolveIngredientSlot(recipe, ingredient.Type, ingredientNeeded,
                        available, tempSteps, resolving, depth, consumed))
                {
                    available.Clear();
                    foreach (var kvp in availBackup)
                        available[kvp.Key] = kvp.Value;
                    return false;
                }
            }

            steps.AddRange(tempSteps);

            int produced = craftsNeeded * recipe.OutputStack;
            steps.Add(new CoreStep
            {
                Recipe = recipe,
                CraftCount = craftsNeeded,
                Consumed = consumed,
                ProducedType = itemType,
                ProducedCount = produced,
                RequiredStations = stepStations
            });

            int excess = produced - deficit;
            if (excess > 0)
            {
                if (!available.ContainsKey(itemType))
                    available[itemType] = 0;
                available[itemType] += excess;
            }

            return true;
        }

        // Confirms a recipe by simulating ALL its ingredients against ONE shared, deducting pool — so
        // two ingredients drawing on the same base material cannot each be counted against the full
        // stock. Allocation-free: deducts directly from the caller's snapshot and rolls it back before
        // returning, instead of cloning it and building a throwaway step list.
        private bool IsRecipeFeasibleShared(CoreRecipe recipe, Dictionary<int, int> availableSnapshot)
        {
            int mark = _undo.Count;
            _feasibilityResolving.Clear();
            _feasibilityResolving.Add(recipe.OutputType);

            bool ok = true;
            foreach (var ingredient in recipe.Ingredients)
            {
                if (!CanFillSlot(recipe, ingredient.Type, ingredient.Stack, availableSnapshot, IngredientDepth))
                {
                    ok = false;
                    break;
                }
            }

            Rollback(availableSnapshot, mark);
            return ok;
        }

        // Feasibility of producing `quantity` of `targetItemType`. Allocation-free: deducts directly
        // from the caller's snapshot and rolls it back before returning (no clone, no step list).
        // CanProduce only ever uses station-satisfied recipes, so a feasible result is automatically
        // fully station-satisfied — no separate post-check needed.
        public bool IsFeasibleFromSnapshot(int targetItemType, int quantity, Dictionary<int, int> availableSnapshot)
        {
            int mark = _undo.Count;
            _feasibilityResolving.Clear();
            bool ok = CanProduce(targetItemType, quantity, availableSnapshot, 0);
            Rollback(availableSnapshot, mark);
            return ok;
        }

        // Allocation-free recursive feasibility: can `needed` units of `itemType` be obtained from
        // `avail` (directly, or by sub-crafting through station-satisfied recipes)? Deducts what it
        // consumes from `avail` and records every change in _undo so a caller can roll back to a mark.
        // Returns true/false only — no steps. Mirrors ResolveRecursive's feasibility decisions
        // (cycle guard, deficit handling, overproduction credit) with zero allocation.
        private bool CanProduce(int itemType, int needed, Dictionary<int, int> avail, int depth)
        {
            avail.TryGetValue(itemType, out int have);
            if (have >= needed)
            {
                avail[itemType] = have - needed;
                _undo.Add((itemType, needed));
                return true;
            }

            // Mirrors ResolveRecursive's depth cut, which bounds RECIPE EXPANSION, not stock lookups:
            // both sides take what is already in the pool for free (ResolveIngredientSlot draws stock
            // before it ever recurses), so both must allow expansion at exactly depth <= MaxDepth.
            // Without this the list flag and the preview accept chains the craft button refuses, and
            // the recursion-depth slider changes nothing.
            if (depth > MaxDepth)
                return false;

            int deficit = needed;
            if (have > 0)
            {
                avail[itemType] = 0;
                _undo.Add((itemType, have));
                deficit -= have;
            }

            if (!_feasibilityResolving.Add(itemType))
                return false; // cycle

            foreach (var recipe in _env.RecipesProducing(itemType))
            {
                if (!StationsAllSatisfied(recipe)) continue;   // feasibility requires available stations
                if (!_env.ConditionsMet(recipe)) continue;

                int mark = _undo.Count;
                int craftsNeeded = (int)Math.Ceiling((double)deficit / recipe.OutputStack);

                bool ok = true;
                foreach (var ingredient in recipe.Ingredients)
                {
                    // Each level multiplies the demand again, so a deep chain of x100 recipes wraps
                    // int negative — and an empty pool satisfies a negative demand. Refuse instead.
                    long slotDemand = (long)ingredient.Stack * craftsNeeded;
                    if (slotDemand > int.MaxValue)
                    {
                        ok = false;
                        break;
                    }

                    // CanFillSlot, not ResolveIngredientType: a recipe-group slot may be filled
                    // from a MIX of members, and committing to one concrete type here loses that.
                    // ResolveIngredientSlot (the plan side) mixes at every level, so picking one
                    // type here made feasibility disagree with the plan for any group slot below
                    // the top — hiding a craftable recipe from the grid.
                    if (!CanFillSlot(recipe, ingredient.Type, (int)slotDemand, avail, depth + 1))
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok)
                {
                    int excess = craftsNeeded * recipe.OutputStack - deficit;
                    if (excess > 0)
                    {
                        avail.TryGetValue(itemType, out int cur);
                        avail[itemType] = cur + excess;
                        _undo.Add((itemType, -excess)); // rollback subtracts the credited overproduction
                    }
                    _feasibilityResolving.Remove(itemType);
                    return true;
                }

                Rollback(avail, mark); // this recipe failed — undo its sub-deductions and try the next
            }

            _feasibilityResolving.Remove(itemType);
            return false;
        }

        // Restores `avail` by replaying _undo back to `mark` (each entry's amount is added back:
        // positive un-deducts a consumption, negative un-credits an overproduction), then trims the log.
        private void Rollback(Dictionary<int, int> avail, int mark)
        {
            for (int i = _undo.Count - 1; i >= mark; i--)
            {
                var (type, amount) = _undo[i];
                avail.TryGetValue(type, out int cur);
                avail[type] = cur + amount;
            }
            _undo.RemoveRange(mark, _undo.Count - mark);
        }

        // Item types transitively producible from current stock (least fixpoint, quantities ignored).
        //
        // Worklist propagation, not a re-scan-everything fixpoint: each recipe's unmet ingredient
        // slots register a reverse edge from every item type that could fill them (own type plus
        // recipe-group substitutes). When a type becomes reachable, only the slots waiting on it are
        // revisited; a recipe's output is published the moment its last slot is filled. Each ingredient
        // edge is processed at most once, so the cost is linear in the number of edges rather than the
        // naive O(passes × recipes) — which degraded to quadratic on long dependency chains (one new
        // item per pass, every pass re-scanning every recipe) and was the source of the open-terminal hitch.
        public HashSet<int> ComputeReachableTypes(Dictionary<int, int> available)
        {
            // Station/condition gate — only these recipes can ever contribute.
            var eligible = new List<CoreRecipe>();
            foreach (var r in _env.AllRecipes)
            {
                bool ok = true;
                foreach (int t in r.RequiredTiles)
                    if (!_env.IsStationSatisfied(t)) { ok = false; break; }
                if (!ok) continue;
                if (!_env.ConditionsMet(r)) continue;
                eligible.Add(r);
            }

            var reachable = new HashSet<int>();
            foreach (var kvp in available)
                if (kvp.Value > 0) reachable.Add(kvp.Key);

            int n = eligible.Count;
            var remaining = new int[n];                       // unmet ingredient slots per recipe (-1 = retired)
            var slotSatisfied = new bool[n][];                // per recipe, which slots are already met
            var triggers = new Dictionary<int, List<(int recipe, int slot)>>();
            var queue = new Queue<int>();

            for (int ri = 0; ri < n; ri++)
            {
                var recipe = eligible[ri];

                // Output already in stock: downstream recipes see it via the seed set, and there is
                // nothing to derive — skip building its edges.
                if (reachable.Contains(recipe.OutputType))
                {
                    remaining[ri] = -1;
                    slotSatisfied[ri] = Array.Empty<bool>();
                    continue;
                }

                var ings = recipe.Ingredients;
                var sat = new bool[ings.Count];
                slotSatisfied[ri] = sat;
                int unmet = 0;

                for (int si = 0; si < ings.Count; si++)
                {
                    int type = ings[si].Type;
                    if (SlotMetBySeed(recipe, type, reachable))
                    {
                        sat[si] = true;
                        continue;
                    }

                    unmet++;
                    AddTrigger(triggers, type, ri, si);
                    foreach (int gid in recipe.AcceptedGroups)
                    {
                        if (!_env.GroupContains(gid, type)) continue;
                        foreach (int v in _env.GroupValidItems(gid))
                            AddTrigger(triggers, v, ri, si);
                    }
                }

                remaining[ri] = unmet;
                if (unmet == 0 && reachable.Add(recipe.OutputType))
                    queue.Enqueue(recipe.OutputType);
            }

            while (queue.Count > 0)
            {
                int type = queue.Dequeue();
                if (!triggers.TryGetValue(type, out var slots)) continue;
                foreach (var (ri, si) in slots)
                {
                    if (remaining[ri] <= 0) continue;          // recipe complete or retired
                    var sat = slotSatisfied[ri];
                    if (sat[si]) continue;                     // slot already filled by another type
                    sat[si] = true;
                    if (--remaining[ri] == 0 && reachable.Add(eligible[ri].OutputType))
                        queue.Enqueue(eligible[ri].OutputType);
                }
            }

            return reachable;
        }

        // True if ingredient `type` is satisfied by the seed reachable set — directly or via a
        // recipe-group substitute this recipe accepts. Mirrors the per-ingredient test below.
        private bool SlotMetBySeed(CoreRecipe recipe, int type, HashSet<int> reachable)
        {
            if (reachable.Contains(type)) return true;
            foreach (int gid in recipe.AcceptedGroups)
            {
                if (!_env.GroupContains(gid, type)) continue;
                foreach (int v in _env.GroupValidItems(gid))
                    if (reachable.Contains(v)) return true;
            }
            return false;
        }

        private static void AddTrigger(Dictionary<int, List<(int recipe, int slot)>> triggers, int type, int ri, int si)
        {
            if (!triggers.TryGetValue(type, out var list))
            {
                list = new List<(int recipe, int slot)>();
                triggers[type] = list;
            }
            list.Add((ri, si));
        }

        // Authoritative craftability of one recipe — direct OR recursive. Mirrors the list-flag pass:
        // a cheap per-ingredient pre-filter (memoised in ingCache), then a single shared-pool confirm
        // only when 2+ ingredients could contend for the same base material.
        public bool IsRecipeCraftable(CoreRecipe recipe, HashSet<int> reachable,
            Dictionary<int, int> available, Dictionary<(int ctx, int group, int type, int stack), bool> ingCache)
        {
            // Fast reject using the precomputed reachable set — worthwhile when sweeping ALL recipes.
            // `reachable` is built from the full snapshot, so it stays a valid superset under the
            // force-craft exclusion below: excluding the output can only ever remove craftability.
            if (!reachable.Contains(recipe.OutputType)) return false;
            return RecheckRecipeCraftable(recipe, available, ingCache);
        }

        // Authoritative craftability of one recipe (direct OR recursive) WITHOUT the reachable
        // pre-filter. For targeted revalidation of a small set of recipes after a storage change,
        // where iterating all recipes (so the reachable fast-reject would pay off) is unnecessary.
        // Result is identical to IsRecipeCraftable: a recipe whose output is not reachable is not
        // craftable, so the omitted fast-reject only changes speed, never the answer.
        // The first accepted group of this recipe that contains `itemType`, or 0 if none does.
        // Identifies which substitutes may fill the slot, so an ingredient verdict can be cached.
        private int AcceptedGroupFor(CoreRecipe recipe, int itemType)
        {
            foreach (int groupId in recipe.AcceptedGroups)
                if (_env.GroupContains(groupId, itemType))
                    return groupId;
            return 0;
        }

        private static bool HasRepeatedIngredientType(CoreRecipe recipe)
        {
            var ingredients = recipe.Ingredients;
            for (int i = 1; i < ingredients.Count; i++)
                for (int j = 0; j < i; j++)
                    if (ingredients[i].Type == ingredients[j].Type)
                        return true;
            return false;
        }

        // Feasibility of ONE ingredient slot, under the same rules the craft button plans by:
        // the recipe's own output may not be produced inside its own subtree (ResolveRecursive holds
        // it in `resolving`), and a recipe-group slot may be filled by any accepted substitute.
        // IsFeasibleFromSnapshot alone does neither, which is why it cannot serve as the prefilter.
        private bool IsIngredientFeasible(CoreRecipe recipe, int ingredientType, int stack, Dictionary<int, int> available)
        {
            int mark = _undo.Count;
            _feasibilityResolving.Clear();
            _feasibilityResolving.Add(recipe.OutputType);
            bool ok = CanFillSlot(recipe, ingredientType, stack, available, IngredientDepth);
            Rollback(available, mark);
            return ok;
        }

        // Feasibility mirror of ResolveIngredientSlot: take stock across every accepted group member,
        // then sub-craft whatever is left through any of them. Deducts into the caller's snapshot and
        // records the changes in _undo, so the caller rolls back to its own mark.
        private bool CanFillSlot(CoreRecipe recipe, int ingredientType, int needed,
            Dictionary<int, int> avail, int depth)
        {
            int remaining = needed;

            foreach (int candidate in IngredientCandidates(recipe, ingredientType))
            {
                if (remaining <= 0) break;
                if (!avail.TryGetValue(candidate, out int have) || have <= 0) continue;

                int taken = Math.Min(remaining, have);
                avail[candidate] = have - taken;
                _undo.Add((candidate, taken));
                remaining -= taken;
            }

            if (remaining <= 0)
                return true;

            foreach (int candidate in IngredientCandidates(recipe, ingredientType))
            {
                int mark = _undo.Count;
                if (CanProduce(candidate, remaining, avail, depth))
                    return true;
                Rollback(avail, mark);
            }

            return false;
        }

        // CanProduce with the recipe's output seeded into the cycle guard and the snapshot restored.
        private bool CanProduceForRecipe(CoreRecipe recipe, int itemType, int quantity, Dictionary<int, int> available)
        {
            int mark = _undo.Count;
            _feasibilityResolving.Clear();
            _feasibilityResolving.Add(recipe.OutputType);
            bool ok = CanProduce(itemType, quantity, available, IngredientDepth);
            Rollback(available, mark);
            return ok;
        }

        public bool RecheckRecipeCraftable(CoreRecipe recipe,
            Dictionary<int, int> available, Dictionary<(int ctx, int group, int type, int stack), bool> ingCache)
        {
            foreach (int t in recipe.RequiredTiles)
                if (!_env.IsStationSatisfied(t)) return false;
            if (!_env.ConditionsMet(recipe)) return false;

            // Force-craft semantics, matching what the craft button actually does: existing stock of
            // the OUTPUT is not a material. Without this, a recipe whose ingredients are only
            // satisfiable by sub-crafting back through its own output (a cycle) reads as craftable in
            // the list, but the button — which resolves via ResolveForceCraft — refuses it as
            // "Nothing to Craft". Excluding the output here makes the two agree: such a recipe is
            // uncraftable, and an item whose every recipe is a no-op is uncraftable outright.
            bool hasOutputStock = available.TryGetValue(recipe.OutputType, out int outputStock) && outputStock > 0;
            if (hasOutputStock) available.Remove(recipe.OutputType);
            try
            {
                // Cached ingredient verdicts are scoped by the recipe's OUTPUT, always - not only
                // when its stock is excluded. IsIngredientFeasible seeds the cycle guard with
                // OutputType unconditionally, so the verdict depends on which item may not be
                // routed through. Keying that only when the output happened to be in stock let one
                // recipe's answer decide another's: whichever was evaluated first won, hiding a
                // craftable recipe or offering an uncraftable one, depending on the order.
                int ctx = recipe.OutputType;

                bool allDirect = true;
                bool usedGroupSubstitute = false;
                int realIngredients = 0;
                foreach (var ing in recipe.Ingredients)
                {
                    realIngredients++;

                    if (IngredientSatisfiedDirectly(recipe, ing.Type, ing.Stack, available, out bool viaGroup))
                    {
                        if (viaGroup) usedGroupSubstitute = true;
                        continue;
                    }

                    allDirect = false;

                    // The verdict depends on which recipe group may fill this slot, so the group is
                    // part of the key — without it two recipes naming the same item with different
                    // accepted groups would share one answer.
                    int groupId = AcceptedGroupFor(recipe, ing.Type);
                    var key = (ctx, groupId, ing.Type, ing.Stack);
                    if (!ingCache.TryGetValue(key, out bool ok))
                    {
                        ok = IsIngredientFeasible(recipe, ing.Type, ing.Stack, available);
                        ingCache[key] = ok;
                    }
                    if (!ok) return false;
                }

                // The shared confirm is the only check that DEDUCTS, so it is also the only one that
                // catches two slots drawing on the same stock. Slots of distinct types that are each
                // directly satisfied cannot contend — unless a group substitute is in play, or the
                // recipe names one item in two slots, which the per-slot checks both measure against
                // the full stock.
                bool needsSharedConfirm = realIngredients >= 2
                    && (!allDirect || usedGroupSubstitute || HasRepeatedIngredientType(recipe));
                if (needsSharedConfirm && !IsRecipeFeasibleShared(recipe, available))
                    return false;

                return true;
            }
            finally
            {
                if (hasOutputStock) available[recipe.OutputType] = outputStock;
            }
        }

        // Builds the per-ingredient availability view for the detail preview. `available` is the
        // real storage snapshot (item type -> count). Ingredients are filled from ONE shared,
        // deducting pool — so a base material usable for two slots (a recipe-group substitute, or
        // the same ore behind two sub-crafts) is not counted twice. TotalHave therefore reflects
        // stock actually claimable for that slot, capped at its need; it is never inflated by what
        // could be sub-crafted (recursive craftability is signalled by HasRecipe instead). This
        // mirrors the resolver's shared-pool accounting, so the preview cannot show an ingredient as
        // satisfied when the recipe as a whole is not craftable.
        public List<IngredientView> ComputeIngredientPreview(CoreRecipe recipe, Dictionary<int, int> available, int craftAmount)
        {
            var views = new List<IngredientView>();
            var pool = new Dictionary<int, int>(available);

            // Force-craft semantics, the same rule RecheckRecipeCraftable and ResolveForceCraft
            // apply: existing stock of the OUTPUT is not a material. Without this the direct draw
            // below fills a slot from the very item being crafted — reachable whenever the output
            // is named as its own ingredient, and far more often when the output is a member of an
            // accepted recipe group (Any Wood, Any Iron Bar). The slot then paints green off stock
            // the craft button refuses to spend.
            bool hasOutputStock = pool.TryGetValue(recipe.OutputType, out int outputStock) && outputStock > 0;
            if (hasOutputStock) pool.Remove(recipe.OutputType);

            // One view per distinct item type, needing the SUM of its slots. A recipe may list the
            // same item twice; taking only the first slot's stack understates the need, and every
            // slot then reads satisfied while the recipe cannot be crafted.
            var neededByType = new Dictionary<int, int>();
            var order = new List<int>();
            foreach (var ing in recipe.Ingredients)
            {
                if (!neededByType.ContainsKey(ing.Type))
                {
                    neededByType[ing.Type] = 0;
                    order.Add(ing.Type);
                }
                neededByType[ing.Type] += ing.Stack * craftAmount;
            }

            foreach (int ingredientType in order)
            {
                int needed = neededByType[ingredientType];
                bool hasRecipe = _env.RecipesProducing(ingredientType).Count > 0;

                bool isGroup = false;
                foreach (int gid in recipe.AcceptedGroups)
                {
                    if (_env.GroupContains(gid, ingredientType)) { isGroup = true; break; }
                }

                // Draw this slot's need from the shared pool: own type first, then group substitutes.
                int have = 0;
                have += DrawFromPool(pool, ingredientType, needed - have);
                if (have < needed)
                {
                    foreach (int gid in recipe.AcceptedGroups)
                    {
                        if (!_env.GroupContains(gid, ingredientType)) continue;
                        foreach (int v in _env.GroupValidItems(gid))
                        {
                            if (v == ingredientType) continue;
                            have += DrawFromPool(pool, v, needed - have);
                            if (have >= needed) break;
                        }
                        break;
                    }
                }

                bool satisfiable = have >= needed
                    || CanSubCraftRemainder(recipe, ingredientType, needed - have, pool);

                views.Add(new IngredientView
                {
                    Type = ingredientType,
                    TotalHave = have,
                    Needed = needed,
                    HasRecipe = hasRecipe,
                    IsGroup = isGroup,
                    Satisfiable = satisfiable
                });
            }

            return views;
        }

        // Sub-crafts the part of a slot that direct stock could not cover, drawing on the SAME
        // deducting pool the direct draws use — so a later slot cannot re-spend a base material an
        // earlier slot already claimed. A successful attempt keeps its deductions (the pool must
        // carry them forward); a failed one rolls back, leaving the pool intact for the next slot.
        // The recipe's own output seeds the cycle guard, matching force-craft semantics: a slot is
        // not satisfiable by looping back through the very item being crafted.
        private bool CanSubCraftRemainder(CoreRecipe recipe, int ingredientType, int remaining, Dictionary<int, int> pool)
        {
            int mark = _undo.Count;
            _feasibilityResolving.Clear();
            _feasibilityResolving.Add(recipe.OutputType);

            // Force-craft semantics, as RecheckRecipeCraftable applies them: existing stock of the
            // output is not a material. Seeding the cycle guard alone is not enough — the stock has
            // to leave the pool too, or a slot reads satisfiable off the very item being crafted.
            bool hasOutputStock = pool.TryGetValue(recipe.OutputType, out int outputStock) && outputStock > 0;
            if (hasOutputStock) pool.Remove(recipe.OutputType);
            try
            {
                // Any accepted group member may cover the remainder, matching what the plan does.
                foreach (int candidate in IngredientCandidates(recipe, ingredientType))
                {
                    int attempt = _undo.Count;
                    if (!CanProduce(candidate, remaining, pool, IngredientDepth))
                    {
                        Rollback(pool, attempt);
                        continue;
                    }

                    _undo.RemoveRange(mark, _undo.Count - mark);
                    return true;
                }

                Rollback(pool, mark);
                return false;
            }
            finally
            {
                if (hasOutputStock) pool[recipe.OutputType] = outputStock;
            }
        }

        // Whether `recipe` could be executed `amount` times against `available`, answered by the
        // allocation-free feasibility mirror: every slot filled from ONE shared, deducting pool —
        // direct stock, a recipe-group substitute, or a sub-craft — so two slots cannot both spend
        // the same base material. Force-craft semantics, as CanSubCraftRemainder applies them:
        // existing stock of the output is not a material. Every count in the snapshot is restored
        // before returning (a type the query touched may be left present at zero, as it is on the
        // other feasibility paths).
        //
        // This is the fast answer, not the last word: the mirror refuses a station-gated recipe and
        // moves on to the next group candidate, where the planner takes the first candidate that
        // resolves at all and keeps a station-incomplete plan. ComputeMaxCraftAmount therefore
        // confirms whatever this leads it to against the planner itself.
        public bool CanCraftAmount(CoreRecipe recipe, Dictionary<int, int> available, int amount)
        {
            if (amount < 1)
                return false;

            if (!StationsAllSatisfied(recipe) || !_env.ConditionsMet(recipe))
                return false;

            // A slot's demand is the stack times the craft count, and every sub-craft multiplies it
            // again. Left in int arithmetic that wraps negative at large amounts, and a negative
            // demand is satisfied by an empty pool — infeasible would read as feasible.
            foreach (var ingredient in recipe.Ingredients)
            {
                long demand = (long)ingredient.Stack * amount;
                if (demand > int.MaxValue)
                    return false;
            }

            bool hasOutputStock = available.TryGetValue(recipe.OutputType, out int outputStock) && outputStock > 0;
            if (hasOutputStock) available.Remove(recipe.OutputType);
            try
            {
                int mark = _undo.Count;
                _feasibilityResolving.Clear();
                _feasibilityResolving.Add(recipe.OutputType);

                bool ok = true;
                foreach (var ingredient in recipe.Ingredients)
                {
                    int needed = ingredient.Stack * amount;
                    if (!CanFillSlot(recipe, ingredient.Type, needed, available, IngredientDepth))
                    {
                        ok = false;
                        break;
                    }
                }

                Rollback(available, mark);
                return ok;
            }
            finally
            {
                if (hasOutputStock) available[recipe.OutputType] = outputStock;
            }
        }

        // Whether the craft button will actually build `amount` executions of `recipe` — the same
        // TryResolveRecipe it runs, on a copy of the pool, refusing a plan that leans on a station
        // the network does not have (the panel shows those as "Missing Stations" instead of
        // crafting). Force-craft semantics: existing stock of the output is not a material.
        public bool PlannerCanCraftAmount(CoreRecipe recipe, Dictionary<int, int> available, int amount)
        {
            if (amount < 1)
                return false;

            long produced = (long)amount * recipe.OutputStack;
            if (produced > int.MaxValue)
                return false;

            var pool = new Dictionary<int, int>(available);
            pool.Remove(recipe.OutputType);

            var steps = new List<CoreStep>();
            var resolving = new HashSet<int> { recipe.OutputType };
            if (!TryResolveRecipe(recipe, recipe.OutputType, (int)produced, pool, steps, resolving, 0))
                return false;

            foreach (var step in steps)
                foreach (int tile in step.RequiredStations)
                    if (!_env.IsStationSatisfied(tile))
                        return false;

            return true;
        }

        // The largest number of times `recipe` can be executed against `available`, capped at `cap`
        // (0 when not even one craft is possible). This is what the panel's MAX button offers, so it
        // has to agree with what the craft button will actually plan: an ingredient that is out of
        // stock but sub-craftable raises the ceiling instead of pinning it to zero.
        //
        // The search doubles until a craft count fails and bisects the gap — a couple of dozen plans
        // instead of one per candidate amount, which matters because the button's hover tooltip asks
        // for this every frame.
        //
        // Bisecting wants plannability to fall monotonically with the amount, and it very nearly
        // does — but not quite, so the amounts just past the boundary are checked one by one. The
        // dips come from rounding: a sub-craft is planned ceil(deficit / OutputStack) times, so a
        // larger demand can round onto a different sub-recipe that happens to fit where the first
        // one did not. A run of dips longer than that window leaves the answer short — always
        // conservative, never above what the button will build.
        public int ComputeMaxCraftAmount(CoreRecipe recipe, Dictionary<int, int> available, int cap)
        {
            if (cap < 1 || !CanCraftAmount(recipe, available, 1))
                return 0;

            int feasible = 1;
            int probe = 2;
            int high = cap;
            while (probe <= cap)
            {
                if (!CanCraftAmount(recipe, available, probe))
                {
                    high = probe - 1;
                    break;
                }

                feasible = probe;
                if (probe > cap / 2)
                    break;
                probe *= 2;
            }

            int low = feasible;
            while (low < high)
            {
                int candidate = low + (high - low + 1) / 2;
                if (CanCraftAmount(recipe, available, candidate))
                    low = candidate;
                else
                    high = candidate - 1;
            }

            int best = low;
            int lastProbed = Math.Min(cap, low + RoundingDipProbeWindow);
            for (int candidate = low + 1; candidate <= lastProbed; candidate++)
                if (CanCraftAmount(recipe, available, candidate))
                    best = candidate;

            // The mirror can be a shade more permissive than the planner, and an amount the craft
            // button then refuses is worse than no offer at all — it disables the button the click
            // was meant to arm. Step down until the planner agrees; on real recipe data it agrees
            // immediately, and a recipe that argues for longer than this is not worth more plans.
            int floor = Math.Max(0, best - PlannerConfirmSteps);
            for (int candidate = best; candidate > floor; candidate--)
                if (PlannerCanCraftAmount(recipe, available, candidate))
                    return candidate;

            return 0;
        }

        // Takes up to `want` units of `type` from the pool, deducting what it takes. Returns the
        // amount taken (0 if `want` <= 0 or none available).
        private static int DrawFromPool(Dictionary<int, int> pool, int type, int want)
        {
            if (want <= 0) return 0;
            if (!pool.TryGetValue(type, out int have) || have <= 0) return 0;
            int take = Math.Min(want, have);
            pool[type] = have - take;
            return take;
        }
    }
}
