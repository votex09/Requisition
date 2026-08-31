using System.Collections.Generic;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraStorage.Systems
{
    // Builds and maintains a reverse-lookup index mapping each craftable item type to the
    // list of all <see cref="Recipe"/>s that produce it. Built once in
    // <see cref="PostAddRecipes"/> and then used throughout the session by
    // <see cref="Helpers.RecipeResolver"/> to avoid O(n) scans over all recipes per lookup. 
    public class RecipeCacheSystem : ModSystem
    {
        // Maps item type -> list of recipes that produce that item.
        public Dictionary<int, List<Recipe>> RecipesByResult { get; private set; } = new();

        public static RecipeCacheSystem Instance => ModContent.GetInstance<RecipeCacheSystem>();

        public override void PostAddRecipes()
        {
            BuildCache();
        }

        private void BuildCache()
        {
            RecipesByResult.Clear();

            for (int i = 0; i < Recipe.numRecipes; i++)
            {
                var recipe = Main.recipe[i];
                if (recipe == null || recipe.createItem == null || recipe.createItem.type <= ItemID.None)
                    continue;

                int resultType = recipe.createItem.type;
                if (!RecipesByResult.ContainsKey(resultType))
                    RecipesByResult[resultType] = new List<Recipe>();

                RecipesByResult[resultType].Add(recipe);
            }
        }

        // Shared miss result: GetRecipesFor is called from per-frame draw paths (Crafting Tree
        // node indicators), where allocating a fresh empty list per call is churn. Callers only
        // read the result.
        private static readonly List<Recipe> Empty = new();

        // Get all recipes that produce the given item type. Do not mutate the result.
        public List<Recipe> GetRecipesFor(int itemType)
        {
            return RecipesByResult.TryGetValue(itemType, out var recipes) ? recipes : Empty;
        }

        // Allocation-free existence check for per-frame callers.
        public bool HasRecipesFor(int itemType) => RecipesByResult.ContainsKey(itemType);
    }
}
