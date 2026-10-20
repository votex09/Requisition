using System.Collections.Generic;

namespace TerraStorage.Helpers.Resolver
{
    // A single recipe ingredient: a quantity of one item type.
    public sealed class CoreIngredient
    {
        public int Type;
        public int Stack;

        public CoreIngredient(int type, int stack)
        {
            Type = type;
            Stack = stack;
        }
    }

    // A recipe in the resolver's own terms, decoupled from Terraria's Recipe so the resolution
    // algorithm can be exercised with synthetic fixtures in a plain unit test. The engine adapter
    // converts each Terraria Recipe into one of these; <see cref="Source"/> holds the original so
    // resolved steps can be mapped back to the concrete recipe for execution.
    public sealed class CoreRecipe
    {
        public int OutputType;
        public int OutputStack;
        public List<CoreIngredient> Ingredients = new();
        // Station tile types this recipe needs. Only real tiles (>= 0); sentinels are dropped on conversion.
        public List<int> RequiredTiles = new();
        public List<int> AcceptedGroups = new();
        // Back-reference to the engine recipe (Terraria Recipe) — null in tests.
        public object Source;
    }

    // One intermediate crafting operation produced by the resolver.
    public sealed class CoreStep
    {
        public CoreRecipe Recipe;
        public int CraftCount;
        public Dictionary<int, int> Consumed = new();
        public int ProducedType;
        public int ProducedCount;
        public List<int> RequiredStations = new();
    }

    // Per-ingredient availability for the crafting detail preview. Pure data so the UI is a thin
    // renderer over it and the "X/Y available" numbers can be unit-tested directly.
    public struct IngredientView
    {
        public int Type;
        // Units drawn for this slot from the shared pool (own type plus recipe-group substitutes),
        // capped at Needed. Never inflated by what could be sub-crafted — that is Satisfiable's job.
        // Slots are filled in recipe order, so an earlier slot's sub-craft can legitimately claim
        // base material a later slot would otherwise have drawn; a later slot then reports what is
        // still free for it rather than the raw stack count.
        public int TotalHave;
        // Sum of every slot naming this item type, times the craft amount.
        public int Needed;
        public bool HasRecipe;
        public bool IsGroup;
        // True if this slot can be filled from the shared pool - directly, or by sub-crafting the
        // remainder. False marks the slot as one actually blocking the recipe, which is what the
        // panel colours red; a short-but-satisfiable slot is not the player's problem.
        public bool Satisfiable;
    }

    // Everything the resolution algorithm needs to know about the recipe world, abstracted away
    // from Terraria's statics (Main.recipe, RecipeGroup, RecipeCacheSystem, tile adjacency,
    // recipe conditions) so the same code runs against real game data and against test fixtures.
    public interface IRecipeEnvironment
    {
        // Recipes whose output is itemType, in registration order.
        IReadOnlyList<CoreRecipe> RecipesProducing(int itemType);
        // Every craftable recipe (used for reachability BFS).
        IReadOnlyList<CoreRecipe> AllRecipes { get; }
        bool GroupContains(int groupId, int itemType);
        IReadOnlyList<int> GroupValidItems(int groupId);
        // True if the required tile is met by the available stations (incl. adjacency equivalences).
        bool IsStationSatisfied(int tile);
        // True if the recipe's special conditions are met by the player/network.
        bool ConditionsMet(CoreRecipe recipe);
    }
}
