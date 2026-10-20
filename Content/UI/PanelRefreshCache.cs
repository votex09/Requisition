using System;

namespace TerraStorage.Content.UI
{
    // When the crafting panel's derived state has gone stale. Every field here is a stamp taken
    // the last time something was recomputed; the panel asks before doing the work again.
    //
    // Terraria-free on purpose, the way FavoritesRowCache is: these are the rules that let a
    // terminal sit open showing numbers that are no longer true, and they are worth asserting.
    public class PanelRefreshCache
    {
        // Recipe conditions are cheap predicates but there are thousands of recipes, so they are
        // re-evaluated on a slow tick rather than every frame. One second is well inside the time a
        // player needs to notice night falling.
        public const uint ConditionRecheckIntervalTicks = 60;

        private long _outputStockVersion = -1;
        private int _outputStockType = -1;

        private long _favoritesVersion = -1;
        private long _storageVersion = -1;

        private uint _lastConditionCheckTick;
        private bool _conditionCheckStarted;

        // The output slot's stock is stamped with what it was counted against. StorageVersion
        // tracks what is IN storage, not which disks are connected, so a walk to a different
        // Terminal has to invalidate this explicitly - otherwise the slot shows phantom stock with
        // a dead "click to take".
        public bool NeedsOutputStockRecount(long storageVersion, int outputType)
            => _outputStockVersion != storageVersion || _outputStockType != outputType;

        public void MarkOutputStockCounted(long storageVersion, int outputType)
        {
            _outputStockVersion = storageVersion;
            _outputStockType = outputType;
        }

        public void InvalidateOutputStock()
        {
            _outputStockVersion = -1;
            _outputStockType = -1;
        }

        // Favorites drive the list's partitioning and, with "show uncraftable" off, whether a
        // recipe is listed at all. They can be toggled from the favorites panel, the crafting tree
        // and the encyclopedia, none of which call back into this panel.
        public bool NeedsFavoritesRefilter(long favoritesVersion) => favoritesVersion != _favoritesVersion;

        public void MarkFavoritesFiltered(long favoritesVersion) => _favoritesVersion = favoritesVersion;

        public bool NeedsStorageReact(long storageVersion) => storageVersion != _storageVersion;

        public void MarkStorageReacted(long storageVersion) => _storageVersion = storageVersion;

        // Conditions (night, Blood Moon, downed bosses, biome) are live world state, so a terminal
        // left open across nightfall would otherwise keep the flags it was built with. The first
        // call is always due: an unstarted panel has flags from whenever it was last filled.
        public bool NeedsConditionRecheck(uint tickNow)
        {
            if (!_conditionCheckStarted)
                return true;

            return tickNow - _lastConditionCheckTick >= ConditionRecheckIntervalTicks;
        }

        public void MarkConditionsChecked(uint tickNow)
        {
            _conditionCheckStarted = true;
            _lastConditionCheckTick = tickNow;
        }

        public void Reset()
        {
            InvalidateOutputStock();
            _favoritesVersion = -1;
            _storageVersion = -1;
            _conditionCheckStarted = false;
        }

        // Writes freshly evaluated station/condition flags over the stored ones and reports whether
        // any actually flipped, so the list is re-filtered only when something changed. A length
        // mismatch means the flags belong to a recipe list that has since been rebuilt - applying
        // them would blame the wrong recipes, so nothing is written.
        public static bool ApplyFlags(bool[] flags, int recipeCount, Func<int, bool> evaluate)
        {
            if (flags == null || flags.Length != recipeCount)
                return false;

            bool anyChanged = false;

            for (int index = 0; index < recipeCount; index++)
            {
                bool met = evaluate(index);
                if (flags[index] == met)
                    continue;

                flags[index] = met;
                anyChanged = true;
            }

            return anyChanged;
        }
    }
}
