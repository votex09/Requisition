using System;
using System.Collections.Generic;
using System.Linq;
using TerraStorage.Helpers.Resolver;

namespace TerraStorage.Tests
{
    // A hand-built recipe world for exercising CoreResolver without Terraria. Recipes, recipe
    // groups, stations and conditions are all explicit, so each scenario controls exactly what the
    // resolver sees — this is how the copper/tin and demonite/crimtane shapes are reproduced faithfully.
    public sealed class FakeEnvironment : IRecipeEnvironment
    {
        private readonly List<CoreRecipe> _all = new();
        private readonly Dictionary<int, List<CoreRecipe>> _producing = new();
        private readonly Dictionary<int, List<int>> _groups = new();
        private readonly HashSet<int> _stations = new();

        public FakeEnvironment WithStations(params int[] tiles)
        {
            foreach (var t in tiles) _stations.Add(t);
            return this;
        }

        public FakeEnvironment WithGroup(int id, params int[] items)
        {
            _groups[id] = items.ToList();
            return this;
        }

        public CoreRecipe AddRecipe(int outType, int outStack, (int type, int stack)[] ingredients,
            int[] tiles = null, int[] groups = null)
        {
            var r = new CoreRecipe { OutputType = outType, OutputStack = outStack };
            foreach (var (type, stack) in ingredients) r.Ingredients.Add(new CoreIngredient(type, stack));
            if (tiles != null) r.RequiredTiles.AddRange(tiles);
            if (groups != null) r.AcceptedGroups.AddRange(groups);
            _all.Add(r);
            if (!_producing.TryGetValue(outType, out var list)) { list = new List<CoreRecipe>(); _producing[outType] = list; }
            list.Add(r);
            return r;
        }

        public IReadOnlyList<CoreRecipe> RecipesProducing(int itemType)
            => _producing.TryGetValue(itemType, out var l) ? l : (IReadOnlyList<CoreRecipe>)Array.Empty<CoreRecipe>();

        public IReadOnlyList<CoreRecipe> AllRecipes => _all;

        public bool GroupContains(int groupId, int itemType)
            => _groups.TryGetValue(groupId, out var l) && l.Contains(itemType);

        public IReadOnlyList<int> GroupValidItems(int groupId)
            => _groups.TryGetValue(groupId, out var l) ? l : (IReadOnlyList<int>)Array.Empty<int>();

        public bool IsStationSatisfied(int tile) => _stations.Contains(tile);

        // Recipe conditions are live world state in the real environment (night, biome, downed
        // bosses). Tests that care about them supply a predicate; everything else sees them met.
        private Func<CoreRecipe, bool> _conditions = _ => true;

        public FakeEnvironment WithConditions(Func<CoreRecipe, bool> conditions)
        {
            _conditions = conditions;
            return this;
        }

        public bool ConditionsMet(CoreRecipe recipe) => _conditions(recipe);
    }
}
