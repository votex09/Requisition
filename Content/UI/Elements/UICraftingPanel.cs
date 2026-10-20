using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.GameContent.UI.Elements;
using Terraria.GameInput;
using Terraria.Localization;
using Terraria.UI;
using TerraStorage.Common;
using TerraStorage.Content.Tiles;
using TerraStorage.Content.UI;
using TerraStorage.Content.UI.CraftingTree;
using TerraStorage.Helpers;
using TerraStorage.Helpers.Resolver;
using TerraStorage.Systems;

namespace TerraStorage.Content.UI.Elements
{
    // Split-panel crafting UI: a scrollable recipe grid on the left and a detail view
    // on the right. Supports recipe variant cycling, ingredient right-click navigation
    // with a history stack, a craft-amount input field, and a craft/extract button row.
    // Recipe lists are filtered by search text, category, and craftability; favorited
    // recipes are pinned to the top. Storage-version polling keeps the craftability
    // flags live without explicit callbacks.
    public class UICraftingPanel : UIElement
    {
        private const int CellSize = 44;
        private const int GridHeaderHeight = 25;
        private const int GridScrollbarReservedWidth = 25;
        private static readonly RasterizerState ScissorRasterizer = new() { ScissorTestEnable = true };

        private List<Guid> _diskIds = new();
        private TerminalEntity _terminal;
        private HashSet<int> _availableStations = new();
        private HashSet<CraftingCondition> _availableConditions = new();
        private List<(Recipe recipe, bool canCraft)> _allRecipes = new();
        private List<(Recipe recipe, bool canCraft)> _filteredRecipes = new();
        private Recipe _selectedRecipe;
        private bool _selectedCanCraft;
        private CraftingPlan _currentPlan;
        private UICategoryFilterBar _categoryFilter;

        private UIPanel _recipeGridPanel;
        private UIPanel _detailPanel;
        private UIScrollbar _recipeScrollbar;
        private int _craftAmount = 1;
        private bool _showUncraftable = false;
        private bool _recursiveCraft = true;
        private int _recursionDepth = 10;
        private const int MaxRecursionDepth = 10;

        // Largest amount the craft field accepts, and so the ceiling MAX searches up to.
        private const int MaxCraftAmountCap = 9999;

        // MAX is asked for on every frame the button is hovered, and answering it recursively costs
        // a storage snapshot plus a handful of resolves. The answer only moves when storage, the
        // selection or the recursion depth does; a null recipe means "not computed yet", which is
        // how the disk/station/condition setters invalidate it.
        private Recipe _maxCraftAmountRecipe;
        private long _maxCraftAmountVersion;
        private int _maxCraftAmountDepth;
        private int _maxCraftAmount;

        // Recursion depth drag state (right-drag on Recursive checkbox)
        private bool _recursionDragActive;
        private float _recursionDragStartY;
        private int _recursionDragBaseDepth;
        private Rectangle _recursiveCheckRect; // updated each draw
        private string _searchText = "";
        private SortMode _sortMode = SortMode.ID;
        private bool _sortAscending = true;

        // Amount input field
        private bool _amountFieldFocused;
        private string _amountFieldText = "1";
        private readonly TextInputHelper _amountInput = new();

        // Amount field drag state (right-drag = parabolic slider)
        private bool _amountFieldMouseDown;
        private bool _amountDragActive;
        private float _amountDragStartX;
        private int _amountDragBaseAmount;
        private bool _prevMouseRight;
        private Rectangle _amountFieldRect; // screen-space rect, updated each draw
        private bool _cleanCraft; // When true, skip vanilla prefix rolling and mod craft hooks
        private Rectangle _cleanCraftCheckRect; // screen-space rect for the checkbox
        private bool _craftToInventory; // When true, craft result goes to player inventory instead of storage
        private Rectangle _craftToInvCheckRect; // screen-space rect for the checkbox
        private bool _lockRecipe; // Global: when on, craft uses exactly the selected recipe variant (persists across recipes)
        private Rectangle _lockCheckRect; // screen-space rect for the lock checkbox
        // True when the selected item is only available as existing stock and cannot actually be
        // crafted from your other materials (e.g. a recipe that loops back into itself). Clicking
        // craft would be a no-op, so the button says so in red instead of looking craftable.
        private bool _craftIsNoOp;

        // Detail panel scroll
        private float _detailScrollOffset = 0f;
        private float _detailScrollTarget = 0f;
        private float _detailMaxScroll = 0f;

        // Recipe list scroll (pixel units)
        private float _recipeScrollPixels;
        private float _recipeScrollTarget;
        private float _recipeScrollBarLastPos;

        // Recipe navigation
        private readonly Stack<(Recipe recipe, bool canCraft, float scrollOffset)> _recipeHistory = new();
        private List<(Recipe recipe, bool canCraft)> _currentVariants = new();
        private int _currentVariantIndex = 0;

        // Shared scratch item to avoid per-frame allocations in draw methods
        private readonly Item _scratchItem = new();

        public event Action OnFavoritesPanelToggled;

        // Hit-test rects for scrollable content (screen-space, rebuilt each frame)
        private readonly List<(Rectangle rect, int itemType)> _ingredientHitRects = new();

        // Header nav button rects (screen-space, rebuilt each draw)
        private Rectangle _backBtnRect;
        private Rectangle _prevVariantRect;
        private Rectangle _nextVariantRect;

        // Change detection: every stamp that says whether derived state has gone stale.
        private readonly PanelRefreshCache _refresh = new();
        private bool _needsRecipeRefresh;
        private uint _lastRecipeRefreshTick;
        private const uint RecipeRefreshIntervalTicks = 6; // ~50ms at 120 fps, ~100ms at 60 fps

        // Cached item counts — updated incrementally instead of re-scanning all disks
        private Dictionary<int, int> _cachedAvailable = new();
        // Reverse index: itemType → indices into _allRecipes that use it as an ingredient
        private Dictionary<int, List<int>> _ingredientToRecipeIndex = new();
        // Reverse index: output item type -> indices into _allRecipes that produce it. Used to
        // re-check the result recipe(s) after a craft without scanning the whole list.
        private Dictionary<int, List<int>> _outputToRecipeIndex = new();
        // Per-recipe: whether stations+conditions are satisfied (set during full RefreshRecipes, stable between refreshes)
        private bool[] _stationsConditionsMet = Array.Empty<bool>();
        // Debounce for UpdatePlan (heavy recursive resolve) — only run after stability
        private uint _planDirtyTick;
        private bool _planDirty;
        private const uint PlanDebounceFrames = 10; // ~167ms at 60fps

        // Deferred recursive craftability pass — runs across multiple frames after initial direct-check load
        private bool _deferredRecursiveActive;
        private int _deferredRecursiveIndex;
        private HashSet<int> _deferredReachable;
        private Dictionary<int, int> _deferredAvailable;
        private Dictionary<(int ctx, int group, int type, int stack), bool> _deferredIngCache;
        private const int RecursiveBatchSize = 200;

        // Single-owner model: while recursive mode is on, the deferred pass owns every list
        // canCraft flag (promotions and demotions). A storage change requests a debounced HARD
        // restart of the pass (fresh snapshot) rather than letting the direct-only update clobber
        // recursive flags. Debounce coalesces bursts of storage mutations into one restart.
        private bool _recursiveRestartPending;
        private uint _recursiveRestartTick;
        private const uint RecursiveRestartDebounceFrames = 6; // ~100ms @60fps
        // Throttle for surfacing in-progress recursive results to the visible list mid-pass.
        private uint _lastRecursiveSyncTick;
        private const uint RecursiveSyncThrottleFrames = 12; // ~200ms @60fps

        // Precomputed per-ingredient data for draw (avoids per-frame CountItem + Any calls).
        // text is the "have/needed" display string, rebuilt in RebuildIngredientCache so the
        // draw loop never interpolates per frame.
        private readonly Dictionary<int, (int totalHave, int needed, bool hasRecipe, bool isGroup, bool satisfiable, string text)> _ingredientCache = new();

        // Scratch for the detail draw loop, so one square is drawn per distinct ingredient type.
        private readonly HashSet<int> _drawnIngredientTypes = new();

        // Display strings rebuilt in SetPlan (the seam every selection/amount/plan/storage
        // change already flows through) so DrawDetailPanel never allocates them per frame.
        private string _selectedOutputName = "";
        private string _headerText = "";
        private string _craftBtnText = "";
        private readonly List<(int itemType, string label)> _chainLabels = new();

        // Scratch lookup for SyncFilteredRecipesIncremental, kept alive so its backing arrays are
        // allocated once instead of on every incremental sync. See the comment at its use site.
        private readonly Dictionary<Recipe, bool> _desiredScratch = new();

        // Output-slot stock, recounted lazily in draw only when _refresh says it is stale —
        // replaces a per-frame CountItem over every disk.
        private int _outputInStorage;

        // Checkbox labels, rebuilt only when the underlying toggle/depth changes.
        private string _uncraftableLabel;
        private bool _uncraftableLabelState;
        private string _recursiveLabel;
        private bool _recursiveLabelState;
        private int _recursiveLabelDepth = -1;

        // Recomputes, for every recipe, whether its stations and conditions are met. Stations only
        // change on a full refresh, but conditions are live world state (night, Blood Moon, downed
        // bosses, biome), so this is also re-run on a slow tick. Sets _needsCanCraftRefresh when a
        // flag actually flips, so the list is only re-filtered when something changed.
        private void RefreshStationConditionFlags()
        {
            if (_allRecipes == null) return;

            bool anyChanged = PanelRefreshCache.ApplyFlags(_stationsConditionsMet, _allRecipes.Count, IsStationConditionMet);

            if (anyChanged)
            {
                // Conditions are world state, not storage, so nightfall moves what MAX should offer
                // without touching StorageVersion. This is the panel's only notice that it did.
                _maxCraftAmountRecipe = null;
                UpdateCanCraftFlags();
            }
        }

        private bool IsStationConditionMet(int recipeIndex)
        {
            var recipe = _allRecipes[recipeIndex].recipe;

            foreach (int t in recipe.requiredTile)
            {
                if (t >= 0 && !RecipeResolver.IsStationSatisfied(t, _availableStations))
                    return false;
            }

            return RecipeResolver.CheckRecipeConditionsPublic(recipe, _availableConditions);
        }

        // The Terminal this panel is operating. Its packets name it rather than the disk list they
        // used to carry, because the server cannot tell whose disks a client-supplied GUID names.
        public void SetTerminal(TerminalEntity terminal)
        {
            _terminal = terminal;
        }

        public void SetDiskIds(List<Guid> diskIds)
        {
            diskIds ??= new List<Guid>();
            if (_diskIds.Count == diskIds.Count && _diskIds.SequenceEqual(diskIds))
                return;
            _diskIds = diskIds;
            _needsRecipeRefresh = true;
            _maxCraftAmountRecipe = null;

            // StorageVersion tracks what is IN storage, not which disks are connected, so the
            // output-slot count would survive a walk to a different Terminal and show phantom stock
            // with a dead "click to take". Force the next draw to recount.
            _refresh.InvalidateOutputStock();
        }

        public void SetAvailableStations(HashSet<int> stations)
        {
            stations ??= new HashSet<int>();
            if (_availableStations.SetEquals(stations)) return;
            _availableStations = stations;
            _needsRecipeRefresh = true;
            _maxCraftAmountRecipe = null;
        }

        public void SetConditions(HashSet<CraftingCondition> conditions)
        {
            conditions ??= new HashSet<CraftingCondition>();
            if (_availableConditions.SetEquals(conditions)) return;
            _availableConditions = conditions;
            _needsRecipeRefresh = true;
            _maxCraftAmountRecipe = null;
        }

        public void SetCategoryFilter(UICategoryFilterBar filterBar)
        {
            _categoryFilter = filterBar;
        }

        public void SetSearchText(string text)
        {
            _searchText = text ?? "";
            FilterRecipes(resetScroll: true);
        }

        public void SetSortMode(SortMode sortMode, bool ascending)
        {
            _sortMode = sortMode;
            _sortAscending = ascending;
            FilterRecipes(resetScroll: true);
        }

        public void RefreshFilteredRecipes()
        {
            FilterRecipes();
        }

        public override void OnInitialize()
        {
            // Recipe grid panel (left side)
            _recipeGridPanel = new UIPanel();
            _recipeGridPanel.Width.Set(0, 0.55f);
            _recipeGridPanel.Height.Set(0, 1f);
            _recipeGridPanel.Left.Set(0, 0f);
            _recipeGridPanel.Top.Set(0, 0f);
            _recipeGridPanel.BackgroundColor = new Color(23, 33, 69) * 0.8f;
            Append(_recipeGridPanel);

            // Recipe scrollbar
            _recipeScrollbar = new UIScrollbar();
            _recipeScrollbar.Height.Set(-10, 1f);
            _recipeScrollbar.Left.Set(-20, 1f);
            _recipeScrollbar.Top.Set(5, 0f);
            _recipeGridPanel.Append(_recipeScrollbar);

            // Detail panel (right side)
            _detailPanel = new UIPanel();
            _detailPanel.Width.Set(-10, 0.45f);
            _detailPanel.Height.Set(0, 1f);
            _detailPanel.Left.Set(10, 0.55f);
            _detailPanel.Top.Set(0, 0f);
            _detailPanel.BackgroundColor = new Color(33, 43, 89) * 0.8f;
            Append(_detailPanel);
        }

        private static bool PlayerHasRoomFor(Player player, Item item)
        {
            for (int i = 0; i < 50; i++)
            {
                var slot = player.inventory[i];
                if (slot.IsAir) return true;
                if (slot.type == item.type && slot.prefix == item.prefix && slot.stack < item.maxStack)
                    return true;
            }
            return false;
        }

        private void RefreshRecipes()
        {
            if (_diskIds.Count == 0)
            {
                _allRecipes.Clear();
                _filteredRecipes.Clear();
                _cachedAvailable.Clear();
                _ingredientToRecipeIndex.Clear();
                _outputToRecipeIndex.Clear();
                return;
            }

            // Phase 1: fast direct-check only — no BFS or recursive feasibility
            _cachedAvailable = StorageWorldSystem.Instance.GetItemCounts(_diskIds);
            _allRecipes = RecipeResolver.GetAllRecipesDirect(_diskIds, _availableStations, _availableConditions);

            RebuildIngredientIndex();

            _stationsConditionsMet = new bool[_allRecipes.Count];
            RefreshStationConditionFlags();

            FilterRecipes(resetScroll: true);

            // Refresh variant list for selected recipe in case craftability changed
            if (_selectedRecipe != null)
            {
                _currentVariants = _allRecipes
                    .Where(r => r.recipe.createItem.type == _selectedRecipe.createItem.type)
                    .ToList();
                var match = _currentVariants.FirstOrDefault(r => r.recipe == _selectedRecipe);
                if (match != default)
                    _selectedCanCraft = match.canCraft;
            }

            // Phase 2: kick off deferred recursive craftability pass (if enabled)
            if (_recursiveCraft)
            {
                StartRecursivePass();
            }
            else
            {
                _deferredRecursiveActive = false;
                _recursiveRestartPending = false;
            }
        }

        // (Re)starts the deferred recursive craftability pass from a fresh storage snapshot.
        // A hard restart, not a resume: snapshot, reachable set, ingredient cache and resume
        // index are all reset so the pass recomputes authoritative flags against current storage.
        // This is the single writer of list canCraft flags while recursive mode is on.
        private void StartRecursivePass()
        {
            RecipeResolver.MaxDepth = _recursionDepth;
            _deferredAvailable = new Dictionary<int, int>(_cachedAvailable);
            _deferredReachable = null; // computed on the first deferred frame
            _deferredIngCache = new Dictionary<(int ctx, int group, int type, int stack), bool>();
            _deferredRecursiveIndex = 0;
            _deferredRecursiveActive = true;
            _recursiveRestartPending = false;
        }

        // Syncs the selected recipe's cached craftability from the master list. Used after the
        // recursive pass updates flags, since that pass owns them while recursive mode is on.
        private void RefreshSelectedCanCraftFromList()
        {
            if (_selectedRecipe == null) return;
            foreach (var entry in _allRecipes)
            {
                if (entry.recipe == _selectedRecipe)
                {
                    _selectedCanCraft = entry.canCraft;
                    return;
                }
            }
        }

        // Reverts all recipes to direct-craftability only (strips recursive canCraft flags).
        // Used when the Recursive checkbox is toggled off.
        private void StripRecursiveCraftFlags()
        {
            var available = _cachedAvailable;
            for (int i = 0; i < _allRecipes.Count; i++)
            {
                var (recipe, canCraft) = _allRecipes[i];
                if (!canCraft) continue;

                // Re-check direct ingredient availability — if it fails, this was a recursive flag.
                // Force-craft semantics: the output's own stock is not a material.
                int outputType = recipe.createItem.type;

                bool directMet = true;
                foreach (var ing in recipe.requiredItem)
                {
                    if (ing.type <= ItemID.None) continue;
                    int needed = ing.stack;
                    bool found = ing.type != outputType
                        && available.TryGetValue(ing.type, out int have) && have >= needed;
                    if (!found)
                    {
                        foreach (int gid in recipe.acceptedGroups)
                        {
                            var grp = RecipeGroup.recipeGroups[gid];
                            if (!grp.ContainsItem(ing.type)) continue;
                            foreach (int v in grp.ValidItems)
                            {
                                if (v != outputType && available.TryGetValue(v, out int vh) && vh >= needed)
                                { found = true; break; }
                            }
                            if (found) break;
                        }
                    }
                    if (!found) { directMet = false; break; }
                }

                if (!directMet)
                    _allRecipes[i] = (recipe, false);
            }
        }

        // Builds the reverse index: itemType → list of indices into _allRecipes
        // that use that item as an ingredient. Built once after RefreshRecipes,
        // used for targeted canCraft updates when specific items change.
        private void RebuildIngredientIndex()
        {
            _ingredientToRecipeIndex.Clear();
            _outputToRecipeIndex.Clear();
            for (int i = 0; i < _allRecipes.Count; i++)
            {
                var recipe = _allRecipes[i].recipe;

                // Index this recipe under the item it produces (for re-checking result recipes).
                if (!_outputToRecipeIndex.TryGetValue(recipe.createItem.type, out var outList))
                {
                    outList = new List<int>();
                    _outputToRecipeIndex[recipe.createItem.type] = outList;
                }
                outList.Add(i);

                foreach (var ing in recipe.requiredItem)
                {
                    if (ing.type <= ItemID.None) continue;
                    if (!_ingredientToRecipeIndex.TryGetValue(ing.type, out var list))
                    {
                        list = new List<int>();
                        _ingredientToRecipeIndex[ing.type] = list;
                    }
                    list.Add(i);

                    // Also index recipe group substitutes so those recipes update too
                    foreach (int gid in recipe.acceptedGroups)
                    {
                        var grp = RecipeGroup.recipeGroups[gid];
                        if (!grp.ContainsItem(ing.type)) continue;
                        foreach (int v in grp.ValidItems)
                        {
                            if (v == ing.type) continue;
                            if (!_ingredientToRecipeIndex.TryGetValue(v, out var gList))
                            {
                                gList = new List<int>();
                                _ingredientToRecipeIndex[v] = gList;
                            }
                            gList.Add(i);
                        }
                    }
                }
            }
        }

        // Targeted update: diffs cached item counts against current state, finds which
        // items actually changed, and only re-checks the specific recipes that use those
        // items as ingredients. No full disk scan, no iteration of all recipes.
        private void UpdateCanCraftFlags()
        {
            if (_diskIds.Count == 0 || _allRecipes.Count == 0) return;

            // Fast count-only lookup — no ConsolidatedItem allocation
            var current = StorageWorldSystem.Instance.GetItemCounts(_diskIds);
            var changedTypes = new HashSet<int>();

            foreach (var kvp in current)
            {
                _cachedAvailable.TryGetValue(kvp.Key, out int oldCount);
                if (kvp.Value != oldCount)
                    changedTypes.Add(kvp.Key);
            }
            // Check for items that were removed entirely
            foreach (var kvp in _cachedAvailable)
            {
                if (!current.ContainsKey(kvp.Key))
                    changedTypes.Add(kvp.Key);
            }

            _cachedAvailable = current;

            if (changedTypes.Count == 0) return;

            // Find recipe indices affected by changed items
            var affectedIndices = new HashSet<int>();
            foreach (int itemType in changedTypes)
            {
                if (_ingredientToRecipeIndex.TryGetValue(itemType, out var indices))
                    affectedIndices.UnionWith(indices);
            }

            if (affectedIndices.Count == 0) return;

            // Only re-check ingredient availability for affected recipes.
            // Stations and conditions don't change between frames — skip those checks.
            bool anyChanged = false;
            foreach (int i in affectedIndices)
            {
                var (recipe, oldCanCraft) = _allRecipes[i];

                // If the recipe was previously uncraftable due to stations/conditions
                // (not ingredients), it stays uncraftable — ingredient changes can't fix that.
                // We only flip recipes that were limited by ingredient availability.
                // Force-craft semantics: the output's own stock is not a material (see
                // CoreResolver.RecheckRecipeCraftable).
                int outputType = recipe.createItem.type;

                bool ingredientsMet = true;
                foreach (var ing in recipe.requiredItem)
                {
                    if (ing.type <= ItemID.None) continue;
                    if (ing.type != outputType
                        && current.TryGetValue(ing.type, out int have) && have >= ing.stack) continue;

                    bool groupOk = false;
                    foreach (int gid in recipe.acceptedGroups)
                    {
                        var grp = RecipeGroup.recipeGroups[gid];
                        if (!grp.ContainsItem(ing.type)) continue;
                        foreach (int v in grp.ValidItems)
                        {
                            if (v != outputType && current.TryGetValue(v, out int vh) && vh >= ing.stack)
                            { groupOk = true; break; }
                        }
                        if (groupOk) break;
                    }
                    if (!groupOk) { ingredientsMet = false; break; }
                }

                bool canCraft = ingredientsMet && _stationsConditionsMet[i];

                if (canCraft != oldCanCraft)
                {
                    _allRecipes[i] = (recipe, canCraft);
                    anyChanged = true;
                }
            }

            if (anyChanged)
            {
                SyncFilteredRecipesIncremental();
                if (_selectedRecipe != null)
                {
                    var match = _allRecipes.FirstOrDefault(r => r.recipe == _selectedRecipe);
                    if (match != default)
                        _selectedCanCraft = match.canCraft;
                }
            }
        }

        // Recursive mode, storage content changed (a craft/deposit/withdraw): re-validate ONLY the
        // recipes that the change can actually affect, instead of restarting a full re-validation of
        // every recipe. If a full deferred pass is still in flight (rare — e.g. crafting during the
        // initial pass after opening the Terminal), fall back to the existing hard-restart, since it
        // owns the flags until it finishes.
        private void OnStorageChangedRecursive()
        {
            if (_deferredRecursiveActive)
            {
                _cachedAvailable = StorageWorldSystem.Instance.GetItemCounts(_diskIds);
                _recursiveRestartPending = true;
                _recursiveRestartTick = Main.GameUpdateCount;
                return;
            }

            RecursiveTargetedUpdate();
        }

        // Re-checks recursive craftability for only the recipes a storage change can flip:
        //   • a CONSUMED item can only DEMOTE a currently-craftable recipe that uses it;
        //   • a PRODUCED item can only PROMOTE a currently-uncraftable recipe that uses it;
        //   • the result recipe(s) that produce a produced item.
        // Everything else keeps its flag. This is the recursive analogue of UpdateCanCraftFlags.
        private void RecursiveTargetedUpdate()
        {
            var current = StorageWorldSystem.Instance.GetItemCounts(_diskIds);
            if (_diskIds.Count == 0 || _allRecipes.Count == 0) { _cachedAvailable = current; return; }

            var increased = new HashSet<int>();
            var decreased = new HashSet<int>();
            foreach (var kvp in current)
            {
                _cachedAvailable.TryGetValue(kvp.Key, out int old);
                if (kvp.Value > old) increased.Add(kvp.Key);
                else if (kvp.Value < old) decreased.Add(kvp.Key);
            }
            foreach (var kvp in _cachedAvailable)
                if (!current.ContainsKey(kvp.Key)) decreased.Add(kvp.Key);

            _cachedAvailable = current;
            if (increased.Count == 0 && decreased.Count == 0) return;

            var affected = new HashSet<int>();
            foreach (int t in decreased)
                if (_ingredientToRecipeIndex.TryGetValue(t, out var users))
                    foreach (int i in users) if (_allRecipes[i].canCraft) affected.Add(i);
            foreach (int t in increased)
                if (_ingredientToRecipeIndex.TryGetValue(t, out var users))
                    foreach (int i in users) if (!_allRecipes[i].canCraft) affected.Add(i);
            foreach (int t in increased)
                if (_outputToRecipeIndex.TryGetValue(t, out var producers))
                    affected.UnionWith(producers);

            if (affected.Count == 0) return;

            var core = new CoreResolver(new TerrariaRecipeEnvironment(_availableStations, _availableConditions)) { MaxDepth = _recursionDepth };
            var reachable = core.ComputeReachableTypes(current);
            var ingCache = new Dictionary<(int ctx, int group, int type, int stack), bool>();
            bool anyChanged = false;
            foreach (int i in affected)
            {
                var (recipe, was) = _allRecipes[i];
                bool now = core.IsRecipeCraftable(TerrariaRecipeEnvironment.ToCore(recipe), reachable, current, ingCache);
                if (now != was)
                {
                    _allRecipes[i] = (recipe, now);
                    anyChanged = true;
                }
            }

            if (anyChanged)
            {
                SyncFilteredRecipesIncremental();
                RefreshSelectedCanCraftFromList();
            }
        }

        // Applies search text, category filter, and the show-uncraftable toggle to
        // <see cref="_allRecipes"/>, then sorts each partition (favorited/regular)
        // so craftable entries appear before uncraftable ones within the same group.
        private void FilterRecipes(bool resetScroll = false)
        {
            var player = StoragePlayerSystem.Local;
            bool hasSearch = !string.IsNullOrEmpty(_searchText);
            // Parsed once: the query is the same for every recipe in the sweep below.
            var (searchMode, searchQuery) = ItemSearchHelper.Parse(_searchText);
            bool hasCategory = _categoryFilter != null;

            _filteredRecipes.Clear();
            var regular = new List<(Recipe recipe, bool canCraft)>();

            foreach (var entry in _allRecipes)
            {
                int itemType = entry.recipe.createItem.type;
                bool isFav = player.IsRecipeFavorited(entry.recipe);

                if (isFav)
                {
                    if (hasSearch && !ItemSearchHelper.Matches(itemType, searchMode, searchQuery))
                        continue;
                    if (hasCategory && !_categoryFilter.PassesFilter(itemType))
                        continue;
                    _filteredRecipes.Add(entry);
                }
                else
                {
                    if (!_showUncraftable && !entry.canCraft)
                        continue;
                    if (hasSearch && !ItemSearchHelper.Matches(itemType, searchMode, searchQuery))
                        continue;
                    if (hasCategory && !_categoryFilter.PassesFilter(itemType))
                        continue;
                    regular.Add(entry);
                }
            }

            int dir = _sortAscending ? 1 : -1;
            Comparison<(Recipe recipe, bool canCraft)> sort = (a, b) =>
            {
                if (a.canCraft != b.canCraft) return a.canCraft ? -1 : 1;
                int typeA = a.recipe.createItem.type;
                int typeB = b.recipe.createItem.type;
                int result = _sortMode switch
                {
                    SortMode.Name => string.Compare(TerminalUIState.GetCachedName(typeA), TerminalUIState.GetCachedName(typeB), StringComparison.OrdinalIgnoreCase),
                    SortMode.Value => TerminalUIState.GetCachedValue(typeA).CompareTo(TerminalUIState.GetCachedValue(typeB)),
                    SortMode.DamageDefense => TerminalUIState.GetCachedDamageDefense(typeA).CompareTo(TerminalUIState.GetCachedDamageDefense(typeB)),
                    _ => typeA.CompareTo(typeB)
                };
                return result * dir;
            };
            _filteredRecipes.Sort(sort);
            regular.Sort(sort);

            _filteredRecipes.AddRange(regular);

            if (resetScroll)
                ResetRecipeScroll();
        }

        // Updates <see cref="_filteredRecipes"/> incrementally: updates canCraft flags in-place,
        // removes recipes that no longer pass filters, and appends newly visible recipes.
        // Does not re-sort or reset scroll — preserves item positions and scroll offset.
        // Use this for craftability-only changes (after craft, deferred recursive pass). 
        private void SyncFilteredRecipesIncremental()
        {
            var player = StoragePlayerSystem.Local;
            bool hasSearch = !string.IsNullOrEmpty(_searchText);
            // Parsed once: the query is the same for every recipe in the sweep below.
            var (searchMode, searchQuery) = ItemSearchHelper.Parse(_searchText);
            bool hasCategory = _categoryFilter != null;

            // Build a lookup of which recipes should be visible and their current canCraft state.
            // Reused rather than reallocated: on a 14 000-recipe world a fresh dictionary is ~400 KB,
            // over the 85 KB Large Object Heap threshold, and the deferred recursive pass calls this
            // several times a second — which was buying a gen2 collection every ten calls. Nothing
            // here escapes the method, so clearing and refilling one instance is safe.
            var desired = _desiredScratch;
            desired.Clear();
            foreach (var entry in _allRecipes)
            {
                int itemType = entry.recipe.createItem.type;
                bool isFav = player.IsRecipeFavorited(entry.recipe);
                if (!isFav && !_showUncraftable && !entry.canCraft) continue;
                if (hasSearch && !ItemSearchHelper.Matches(itemType, searchMode, searchQuery)) continue;
                if (hasCategory && !_categoryFilter.PassesFilter(itemType)) continue;
                desired[entry.recipe] = entry.canCraft;
            }

            // Pass 1: walk existing list — update flags in-place, remove items that dropped out.
            for (int i = _filteredRecipes.Count - 1; i >= 0; i--)
            {
                var entry = _filteredRecipes[i];
                if (!desired.TryGetValue(entry.recipe, out bool cc))
                {
                    _filteredRecipes.RemoveAt(i);
                }
                else
                {
                    if (cc != entry.canCraft)
                        _filteredRecipes[i] = (entry.recipe, cc);
                    desired.Remove(entry.recipe); // already present — don't add again
                }
            }

            // Pass 2: append recipes that became newly visible.
            foreach (var (recipe, cc) in desired)
                _filteredRecipes.Add((recipe, cc));
        }

        private void ResetRecipeScroll()
        {
            if (_recipeScrollbar == null || _recipeGridPanel == null)
                return;

            _recipeScrollPixels = 0;
            _recipeScrollTarget = 0;
            _recipeScrollbar.ViewPosition = 0;
            _recipeScrollBarLastPos = 0;
        }

        private int GetGridColumns(float width)
        {
            float gridWidth = width - GridScrollbarReservedWidth;

            return Math.Max(1, (int)(gridWidth / CellSize));
        }

        private int GetGridVisibleRows(float panelHeight)
        {
            float gridHeight = panelHeight - GridHeaderHeight;

            return Math.Max(1, (int)(gridHeight / CellSize));
        }

        //Select from the recipe grid — clears navigation history.
        private void DeselectRecipe()
        {
            _selectedRecipe = null;
            _selectedCanCraft = false;
            _currentVariants = new List<(Recipe, bool)>();
            _currentVariantIndex = 0;
            _recipeHistory.Clear();
            _detailScrollOffset = 0f;

            // The plan describes the recipe that was selected, and the craft button is gated on the
            // plan alone. Leaving it behind meant clicking the selected recipe a second time and
            // then CRAFT ran ExecuteCraft with no recipe: a null dereference in single player, and a
            // silent no-op on a client, where the send path happens to check.
            _currentPlan = null;
            _craftIsNoOp = false;
            _ingredientCache.Clear();
        }

        private void SelectRecipe(Recipe recipe, bool canCraft)
        {
            _recipeHistory.Clear();
            _detailScrollOffset = 0f;
            SelectRecipeCore(recipe, canCraft);
        }

        //Core selection logic shared by all navigation paths.
        private void SelectRecipeCore(Recipe recipe, bool canCraft)
        {
            _selectedRecipe = recipe;
            _selectedCanCraft = canCraft;
            _craftAmount = 1;
            _amountFieldText = "1";
            _amountFieldFocused = false;
            _amountInput.Deactivate();

            // Find all recipe variants for this item type
            _currentVariants = recipe != null
                ? _allRecipes.Where(r => r.recipe.createItem.type == recipe.createItem.type).ToList()
                : new List<(Recipe, bool)>();
            _currentVariantIndex = _currentVariants.FindIndex(r => r.recipe == recipe);
            if (_currentVariantIndex < 0) _currentVariantIndex = 0;

            if (recipe != null)
            {
                SetPlan(recipe);
            }
            else
            {
                _currentPlan = null;
                _ingredientCache.Clear();
            }
        }

        //Right-click ingredient navigation — navigate to the recipe that produces itemType.
        private void NavigateToItem(int itemType)
        {
            var variants = _allRecipes.Where(r => r.recipe.createItem.type == itemType).ToList();
            if (variants.Count == 0) return;

            if (_selectedRecipe != null)
                _recipeHistory.Push((_selectedRecipe, _selectedCanCraft, _detailScrollOffset));

            _detailScrollOffset = 0f;
            _currentVariants = variants;
            _currentVariantIndex = 0;
            SelectRecipeCore(variants[0].recipe, variants[0].canCraft);
            // Re-set the variant list explicitly; SelectRecipeCore re-queries it but this
            // ensures the correct count is visible immediately without an extra frame delay.
            _currentVariants = variants;
            _currentVariantIndex = 0;
        }

        private void NavigateBack()
        {
            if (_recipeHistory.Count == 0) return;
            var (recipe, canCraft, scrollOffset) = _recipeHistory.Pop();
            SelectRecipeCore(recipe, canCraft);
            _detailScrollOffset = scrollOffset;
        }

        private void CycleVariant(int dir)
        {
            if (_currentVariants.Count <= 1) return;
            _currentVariantIndex = (_currentVariantIndex + dir + _currentVariants.Count) % _currentVariants.Count;
            var (recipe, canCraft) = _currentVariants[_currentVariantIndex];
            _selectedRecipe = recipe;
            _selectedCanCraft = canCraft;
            _craftAmount = 1;
            _amountFieldText = "1";
            _amountFieldFocused = false;
            _amountInput.Deactivate();
            _detailScrollOffset = 0f;
            SetPlan(recipe);
        }

        private void SetCraftAmount(int amount)
        {
            _craftAmount = Math.Max(1, Math.Min(MaxCraftAmountCap, amount));
            _amountFieldText = _craftAmount.ToString();
            UpdatePlan();
        }

        // What the MAX button offers. This has to be the resolver's answer, not a stock tally: an
        // ingredient that is out of stock but sub-craftable (no torches, but wood and gel to make
        // them from) otherwise reads as a hard zero and pins MAX to one, while the craft button
        // happily plans the dozen crafts the materials allow. The Recursive checkbox is not a
        // condition here — it governs the recipe LIST's craftability flags; the craft path resolves
        // recursively either way, so MAX must too or the two disagree the moment it is unticked.
        private int ComputeMaxCraftAmount()
        {
            if (_selectedRecipe == null) return 1;

            long storageVersion = StorageWorldSystem.Instance.StorageVersion;
            bool cached = _maxCraftAmountRecipe == _selectedRecipe
                && _maxCraftAmountVersion == storageVersion
                && _maxCraftAmountDepth == _recursionDepth;
            if (cached)
                return _maxCraftAmount;

            var available = StorageWorldSystem.Instance.GetItemCounts(_diskIds);
            var core = new CoreResolver(new TerrariaRecipeEnvironment(_availableStations, _availableConditions))
                { MaxDepth = _recursionDepth };
            var coreRecipe = TerrariaRecipeEnvironment.ToCore(_selectedRecipe);

            _maxCraftAmount = Math.Max(1, core.ComputeMaxCraftAmount(coreRecipe, available, MaxCraftAmountCap));
            _maxCraftAmountRecipe = _selectedRecipe;
            _maxCraftAmountVersion = storageVersion;
            _maxCraftAmountDepth = _recursionDepth;
            return _maxCraftAmount;
        }

        // Resolves the plan for a recipe, honoring the global recipe-lock: when locked, forces the
        // exact selected recipe variant; otherwise auto-selects the best recipe for the item (default).
        private CraftingPlan ResolvePlan(Recipe recipe, int quantity)
            => _lockRecipe
                ? RecipeResolver.ResolveRecipe(recipe, quantity, _diskIds, _availableStations, _availableConditions)
                : RecipeResolver.Resolve(recipe.createItem.type, quantity, _diskIds, _availableStations, _availableConditions);

        // Resolves the plan for a recipe and refreshes the ingredient preview. Also flags the
        // no-op case: the craft button force-crafts, so when the item is only available as existing
        // stock (direct extract) and cannot actually be crafted from your other materials, clicking
        // would do nothing — the button shows that instead of looking craftable.
        private void SetPlan(Recipe recipe)
        {
            int quantity = _craftAmount * recipe.createItem.stack;
            _currentPlan = ResolvePlan(recipe, quantity);

            _craftIsNoOp = false;
            if (!_lockRecipe && _currentPlan is { IsDirectExtract: true })
            {
                // We have it in stock; clicking force-crafts. If no recipe can actually produce it
                // from the other materials (e.g. it loops back into itself), the craft is a no-op.
                _craftIsNoOp = RecipeResolver.ResolveForceCraft(
                    recipe.createItem.type, quantity, _diskIds, _availableStations, _availableConditions) == null;
            }

            RebuildIngredientCache();
            RebuildDetailStrings(recipe, quantity);
        }

        // Every input of these strings (selection, amount, plan, no-op flag) changes only
        // through SetPlan, so building them here keeps DrawDetailPanel allocation-free.
        private void RebuildDetailStrings(Recipe recipe, int quantity)
        {
            _selectedOutputName = Lang.GetItemNameValue(recipe.createItem.type);
            _headerText = $"{_selectedOutputName} x{quantity}";

            if (_craftIsNoOp)
                _craftBtnText = "Nothing to Craft";
            else if (_currentPlan is { IsDirectExtract: true })
                _craftBtnText = $"CRAFT x{_craftAmount} ({quantity} items)";
            else if (_currentPlan != null && _currentPlan.MissingStations.Count > 0)
                _craftBtnText = "Missing Stations";
            else if (_currentPlan is { IsFeasible: true })
                _craftBtnText = $"CRAFT x{_craftAmount} ({quantity} items)";
            else
                _craftBtnText = "Missing Materials";

            _chainLabels.Clear();
            if (_currentPlan != null && _currentPlan.Steps.Count > 1)
            {
                foreach (var step in _currentPlan.Steps)
                    _chainLabels.Add((step.ProducedType,
                        $"{Lang.GetItemNameValue(step.ProducedType)} x{step.ProducedCount}"));
            }
        }

        private void UpdatePlan()
        {
            if (_selectedRecipe != null)
                SetPlan(_selectedRecipe);
        }

        // Precomputes per-ingredient storage counts and recipe-existence flags so
        // that <see cref="DrawScrollableDetailContent"/> can read them without
        // calling CountItem or scanning _allRecipes every frame.
        private void RebuildIngredientCache()
        {
            _ingredientCache.Clear();
            if (_selectedRecipe == null) return;

            // Ingredient availability is computed by the unit-tested resolver core against ONE shared,
            // deducting pool — so a base material usable for two slots (a recipe-group substitute, or
            // an ore behind two sub-crafts) is never counted twice, and the preview cannot show an
            // ingredient as satisfied when the recipe as a whole is not craftable. Whether a shortfall
            // is coverable by sub-crafting is conveyed by Satisfiable, NOT by inflating the stock
            // count. The core also folds duplicate slots of one item into a single view, so the
            // needed figure here is the recipe's real total for that item.
            var available = StorageWorldSystem.Instance.GetItemCounts(_diskIds);
            var core = new CoreResolver(new TerrariaRecipeEnvironment(_availableStations, _availableConditions))
                { MaxDepth = _recursionDepth };
            var coreRecipe = TerrariaRecipeEnvironment.ToCore(_selectedRecipe);

            // The "have/needed" string is built here so the draw loop reads instead of interpolating.
            foreach (var view in core.ComputeIngredientPreview(coreRecipe, available, _craftAmount))
            {
                bool craftableShortfall = view.TotalHave < view.Needed && view.Satisfiable;
                string text = craftableShortfall
                    ? $"{view.Needed}/{view.Needed}"
                    : $"{view.TotalHave}/{view.Needed}";
                _ingredientCache[view.Type] =
                    (view.TotalHave, view.Needed, view.HasRecipe, view.IsGroup, view.Satisfiable, text);
            }
        }

        public override void LeftClick(UIMouseEvent evt)
        {
            base.LeftClick(evt);

            if (UIClickBlocker.IsConsumed) return;

            if (_recipeGridPanel != null && _recipeGridPanel.ContainsPoint(evt.MousePosition))
                HandleRecipeGridClick(evt.MousePosition);

            if (_detailPanel != null && _detailPanel.ContainsPoint(evt.MousePosition))
                HandleDetailPanelClick(evt.MousePosition);
        }

        public override void RightClick(UIMouseEvent evt)
        {
            base.RightClick(evt);

            if (UIClickBlocker.IsConsumed) return;

            if (_detailPanel == null || !_detailPanel.ContainsPoint(evt.MousePosition))
                return;

            // Ingredient slot right-click: navigate to that item's recipe
            // (Amount field drag is handled via raw mouse state in Update, not here)
            var mousePoint = evt.MousePosition.ToPoint();
            foreach (var (rect, itemType) in _ingredientHitRects)
            {
                if (rect.Contains(mousePoint))
                {
                    NavigateToItem(itemType);
                    return;
                }
            }

            if (_selectedRecipe == null) return;

            var dims = _detailPanel.GetInnerDimensions();
            float relY = evt.MousePosition.Y - dims.Y;
            float relX = evt.MousePosition.X - dims.X;

            float btnWidth = 40;
            float startX = 5;
            float amtY = dims.Height - 100;

            if (relY >= amtY && relY < amtY + 25)
            {
                int[] amounts = { 1, 5, 25, 100 };
                for (int b = 0; b < 4; b++)
                {
                    float bx = startX + b * (btnWidth + 4);
                    if (relX >= bx && relX < bx + btnWidth)
                    {
                        SetCraftAmount(_craftAmount - amounts[b]);
                        break;
                    }
                }
            }
        }

        private void HandleRecipeGridClick(Vector2 mousePos)
        {
            var dims = _recipeGridPanel.GetInnerDimensions();
            float relX = mousePos.X - dims.X;
            float relY = mousePos.Y - dims.Y;

            // Header row: show-uncraftable toggle (left) + recursive toggle (middle) + favorites button (right)
            if (relY < 22)
            {
                float starBtnX = dims.Width - 50;
                if (relX >= starBtnX && relX < starBtnX + 24)
                {
                    OnFavoritesPanelToggled?.Invoke();
                }
                else
                {
                    float halfW = (dims.Width - 52) * 0.5f;
                    if (relX < halfW)
                    {
                        _showUncraftable = !_showUncraftable;
                        FilterRecipes(resetScroll: true);
                    }
                    else if (relX < halfW * 2)
                    {
                        _recursiveCraft = !_recursiveCraft;
                        if (_recursiveCraft)
                        {
                            StartRecursivePass();
                        }
                        else
                        {
                            // Cancel any in-progress deferred pass and strip recursive flags
                            _deferredRecursiveActive = false;
                            _recursiveRestartPending = false;
                            StripRecursiveCraftFlags();
                            FilterRecipes();
                        }
                    }
                }
                return;
            }

            int columns = GetGridColumns(dims.Width);
            float gridStartY = GridHeaderHeight;
            int col = (int)(relX / CellSize);
            int row = (int)((relY - gridStartY + _recipeScrollPixels) / CellSize);
            int index = row * columns + col;

            if (index >= 0 && index < _filteredRecipes.Count && col >= 0 && col < columns)
            {
                bool alt = Main.keyState.IsKeyDown(Keys.LeftAlt) || Main.keyState.IsKeyDown(Keys.RightAlt);
                if (alt)
                {
                    StoragePlayerSystem.Local.ToggleRecipeFavorite(_filteredRecipes[index].recipe);
                    Terraria.Audio.SoundEngine.PlaySound(Terraria.ID.SoundID.MenuTick);
                    FilterRecipes();
                }
                else
                {
                    if (_selectedRecipe == _filteredRecipes[index].recipe)
                        DeselectRecipe();
                    else
                        SelectRecipe(_filteredRecipes[index].recipe, _filteredRecipes[index].canCraft);
                }
            }
        }

        private void HandleDetailPanelClick(Vector2 mousePos)
        {
            if (_selectedRecipe == null)
                return;

            var mousePoint = mousePos.ToPoint();

            // Navigation header buttons
            if (_backBtnRect.Contains(mousePoint))    { NavigateBack();      return; }
            if (_prevVariantRect.Contains(mousePoint)) { CycleVariant(-1);   return; }
            if (_nextVariantRect.Contains(mousePoint)) { CycleVariant(+1);   return; }

            if (_currentPlan == null) return;

            var dims = _detailPanel.GetInnerDimensions();
            float relY = mousePos.Y - dims.Y;
            float relX = mousePos.X - dims.X;

            float btnWidth = 40;
            float startX = 5;

            // Amount buttons row (left-click increments)
            float amtY = dims.Height - 100;
            if (relY >= amtY && relY < amtY + 25)
            {
                int[] amounts = { 1, 5, 25, 100 };
                for (int b = 0; b < 4; b++)
                {
                    float bx = startX + b * (btnWidth + 4);
                    if (relX >= bx && relX < bx + btnWidth)
                    {
                        SetCraftAmount(_craftAmount + amounts[b]);
                        break;
                    }
                }
            }

            // Craft Max button (right of +1/+5/+25/+100 on the same row)
            float craftMaxStartX = startX + 4 * (btnWidth + 4) + 4;
            if (relX >= craftMaxStartX && relX < craftMaxStartX + btnWidth && relY >= amtY && relY < amtY + 25 && _currentPlan is { IsFeasible: true })
            {
                SetCraftAmount(ComputeMaxCraftAmount());
                return;
            }

            // Clean Craft checkbox (on the middle row, right of amount field)
            if (_cleanCraftCheckRect.Contains(mousePoint))
            {
                _cleanCraft = !_cleanCraft;
                Terraria.Audio.SoundEngine.PlaySound(Terraria.ID.SoundID.MenuTick);
                return;
            }

            // Craft to Inventory checkbox (right of Clean Craft)
            if (_craftToInvCheckRect.Contains(mousePoint))
            {
                _craftToInventory = !_craftToInventory;
                Terraria.Audio.SoundEngine.PlaySound(Terraria.ID.SoundID.MenuTick);
                return;
            }

            // Lock Recipe checkbox (global toggle — recompute the plan so the craft button updates)
            if (_lockCheckRect.Contains(mousePoint))
            {
                _lockRecipe = !_lockRecipe;
                UpdatePlan();
                Terraria.Audio.SoundEngine.PlaySound(Terraria.ID.SoundID.MenuTick);
                return;
            }

            // Input field row (middle row) — left-click focuses text input
            if (relY >= dims.Height - 70 && relY < dims.Height - 45)
            {
                _amountFieldFocused = true;
                _amountFieldText    = _craftAmount.ToString();
                _amountInput.Reset();
                return;
            }

            // Craft button + output slot row
            if (relY >= dims.Height - 35)
            {
                float craftWidth = dims.Width - 30 - 24; // same as craftRect width calc
                // Output slot (right of craft button)
                if (relX >= 5 + craftWidth + 14 && relX < 5 + craftWidth + 14 + 30)
                {
                    TakeFromStorage();
                    return;
                }
                // Craft button (left side)
                if (relX >= 5 && relX < 5 + craftWidth && _currentPlan is { IsFeasible: true })
                    ExecuteCraft();
            }

            // Unfocus amount field if clicking elsewhere
            if (_amountFieldFocused)
            {
                _amountFieldFocused = false;
                _amountInput.Deactivate();
                ApplyAmountFieldText();
            }
        }

        private void ApplyAmountFieldText()
        {
            if (int.TryParse(_amountFieldText, out int val) && val > 0)
                SetCraftAmount(val);
            else
                _amountFieldText = _craftAmount.ToString();
        }

        private void TakeFromStorage()
        {
            if (_selectedRecipe == null || _diskIds.Count == 0) return;

            int inStorage = StorageWorldSystem.Instance.CountItem(_diskIds, _selectedRecipe.createItem.type);
            if (inStorage <= 0) return;

            var tempItem = new Item();
            tempItem.SetDefaults(_selectedRecipe.createItem.type);
            int takeCount = Math.Min(tempItem.maxStack, inStorage);

            bool shift = Keyboard.GetState().IsKeyDown(Keys.LeftShift)
                || Keyboard.GetState().IsKeyDown(Keys.RightShift);

            if (Main.netMode == Terraria.ID.NetmodeID.MultiplayerClient)
            {
                if (!Main.mouseItem.IsAir && !shift)
                    return;
                var mod = Terraria.ModLoader.ModLoader.GetMod("TerraStorage");
                NetworkHandler.SendWithdrawItem(mod, _terminal.ID, _selectedRecipe.createItem.type, takeCount, -1, shift);

                Terraria.Audio.SoundEngine.PlaySound(Terraria.ID.SoundID.Grab);
                return;
            }

            if (!Main.mouseItem.IsAir)
            {
                // If cursor holds same item, try to stack
                if (Main.mouseItem.type == _selectedRecipe.createItem.type
                    && Main.mouseItem.stack < Main.mouseItem.maxStack)
                {
                    int canTake = Math.Min(Main.mouseItem.maxStack - Main.mouseItem.stack, inStorage);
                    var extra = StorageWorldSystem.Instance.ExtractItem(_diskIds, _selectedRecipe.createItem.type, canTake);
                    if (!extra.IsAir)
                    {
                        Main.mouseItem.stack += extra.stack;
                        Terraria.Audio.SoundEngine.PlaySound(Terraria.ID.SoundID.Grab);
                    }
                }
                return;
            }

            var taken = StorageWorldSystem.Instance.ExtractItem(_diskIds, _selectedRecipe.createItem.type, takeCount);
            if (!taken.IsAir)
            {
                if (shift)
                {
                    var player = Main.LocalPlayer;
                    taken = player.GetItem(player.whoAmI, taken, GetItemSettings.InventoryEntityToPlayerInventorySettings);
                    if (!taken.IsAir)
                        Main.mouseItem = taken; // inventory full fallback
                }
                else
                {
                    Main.mouseItem = taken;
                }
                Terraria.Audio.SoundEngine.PlaySound(Terraria.ID.SoundID.Grab);
            }

            if (_selectedRecipe != null)
                UpdatePlan();
        }

        // Executes the current crafting plan. If the plan resolved as a direct
        // extract (item already in storage), forces a proper craft so materials are
        // consumed and the recipe chain runs as intended. The crafted result is
        // inserted back into storage.
        private void ExecuteCraft()
        {
            if (_currentPlan == null || !_currentPlan.IsFeasible || _craftIsNoOp) return;

            if (Main.netMode == Terraria.ID.NetmodeID.MultiplayerClient)
            {
                // In MP, send a craft request to the server instead of executing locally
                if (_selectedRecipe != null)
                {
                    var mod = Terraria.ModLoader.ModLoader.GetMod("TerraStorage");
                    NetworkHandler.SendCraftRequest(mod, _terminal.ID, _selectedRecipe.createItem.type,
                        _craftAmount * _selectedRecipe.createItem.stack, _cleanCraft, _craftToInventory,
                        _lockRecipe ? _selectedRecipe.RecipeIndex : -1);
                    // No pickup sound here: the server has not answered yet, and playing the sound
                    // of a successful craft on SEND told the player a refused craft had worked.
                    Terraria.Audio.SoundEngine.PlaySound(Terraria.ID.SoundID.MenuTick);
                }
                return;
            }

            // Always force-craft: the user explicitly wants to produce new items, not extract existing
            // stock. When the recipe is locked, force exactly the selected variant; otherwise
            // ResolveForceCraft auto-selects the best recipe for the item.
            int craftQty = _craftAmount * _selectedRecipe.createItem.stack;
            var planToUse = _lockRecipe
                ? RecipeResolver.ResolveRecipe(_selectedRecipe, craftQty, _diskIds, _availableStations, _availableConditions)
                : RecipeResolver.ResolveForceCraft(_selectedRecipe.createItem.type, craftQty, _diskIds, _availableStations, _availableConditions);

            // Pre-check: block the craft if neither storage nor player inventory
            // has room. This prevents consuming ingredients with nowhere to put the result.
            // The verdict comes from GetCraftFailure so the server's copy of these guards, which
            // decides the same thing for a multiplayer client, cannot drift from this one.
            bool planIsFeasible = planToUse != null && planToUse.IsFeasible;

            var resultPreview = new Item();
            if (planIsFeasible)
            {
                resultPreview.SetDefaults(planToUse.FinalItemType);
                resultPreview.stack = planToUse.FinalItemCount;
            }

            bool playerHasRoomForResult = planIsFeasible && PlayerHasRoomFor(Main.LocalPlayer, resultPreview);
            bool storageHasRoomForResult = planIsFeasible && !_craftToInventory
                && StorageWorldSystem.Instance.HasRoomFor(_diskIds, resultPreview);

            var craftFailure = StorageOperationFailures.GetCraftFailure(planIsFeasible,
                _craftToInventory, playerHasRoomForResult, storageHasRoomForResult);

            if (!StorageOperationFailures.IsSuccess(craftFailure))
            {
                StorageOperationReporter.ReportFailure(craftFailure);
                return;
            }

            var result = RecipeResolver.ExecutePlan(planToUse, _diskIds, _cleanCraft);
            if (result.IsAir)
                StorageOperationReporter.ReportFailure(StorageOperationFailure.CraftCostingNoLongerHolds);
            else
            {
                if (_craftToInventory)
                {
                    // Send crafted item directly to player inventory.
                    Main.LocalPlayer.GetItem(Main.myPlayer, result, GetItemSettings.GetItemInDropItemCheck);
                }
                else
                {
                    int leftover = StorageWorldSystem.Instance.InsertItem(_diskIds, result);
                    if (leftover > 0)
                    {
                        // Storage is full — give remainder to player.
                        result.stack = leftover;
                        Main.LocalPlayer.GetItem(Main.myPlayer, result, GetItemSettings.GetItemInDropItemCheck);
                    }
                }
                Terraria.Audio.SoundEngine.PlaySound(Terraria.ID.SoundID.Grab);
            }

            // Refresh craftability after the craft, without resetting scroll or resorting.
            // While recursive mode is on, the deferred pass owns the list flags — request a hard
            // restart rather than running the direct-only update (which would clobber recursive
            // flags). When off, the targeted direct update maintains them (it diffs against the
            // pre-craft snapshot, so do not refresh _cachedAvailable first).
            if (_recursiveCraft)
                OnStorageChangedRecursive();
            else
                UpdateCanCraftFlags();
            if (_selectedRecipe != null)
                UpdatePlan();
        }

        public override void ScrollWheel(UIScrollWheelEvent evt)
        {
            base.ScrollWheel(evt);
            if (_recipeGridPanel != null && _recipeGridPanel.ContainsPoint(Main.MouseScreen))
            {
                int gridScrollRows = RequisitionClientConfig.GetGridScrollRows();
                _recipeScrollTarget -= evt.ScrollWheelValue / 120f * CellSize * gridScrollRows;
            }
            else if (_detailPanel != null && _detailPanel.ContainsPoint(Main.MouseScreen))
            {
                _detailScrollTarget -= evt.ScrollWheelValue / 120f * 20f;
            }
        }

        protected override void DrawChildren(SpriteBatch spriteBatch)
        {
            base.DrawChildren(spriteBatch);

            if (_recipeGridPanel != null)
                DrawRecipeGrid(spriteBatch);

            if (_detailPanel != null && _selectedRecipe != null)
                DrawDetailPanel(spriteBatch);
            else if (_detailPanel != null)
                DrawEmptyDetail(spriteBatch);
        }

        private void DrawRecipeGrid(SpriteBatch spriteBatch)
        {
            var dims = _recipeGridPanel.GetInnerDimensions();
            int columns = GetGridColumns(dims.Width);

            // Checkboxes: Show Uncraftable (left) | Recursive (middle-right)
            float checkScale = 0.55f;
            float halfW = (dims.Width - 52) * 0.5f; // leave room for the star button

            if (_uncraftableLabel == null || _uncraftableLabelState != _showUncraftable)
            {
                _uncraftableLabelState = _showUncraftable;
                string uncraftBox = _showUncraftable ? "[X]" : "[  ]";
                _uncraftableLabel = $"{uncraftBox} {Language.GetTextValue("Mods.TerraStorage.UI.CraftingPanel.ShowUncraftable")}";
            }
            var uncraftRect = new Rectangle((int)dims.X, (int)dims.Y, (int)halfW, 20);
            bool uncraftHover = uncraftRect.Contains(Main.MouseScreen.ToPoint());
            Color uncraftColor = uncraftHover ? Color.White : Color.LightGray;
            Utils.DrawBorderString(spriteBatch, _uncraftableLabel,
                new Vector2(dims.X + 4, dims.Y + 2), uncraftColor, checkScale);
            if (uncraftHover && !_recursionDragActive)
                Main.hoverItemName = "Show recipes you can't currently craft";

            if (_recursiveLabel == null || _recursiveLabelState != _recursiveCraft || _recursiveLabelDepth != _recursionDepth)
            {
                _recursiveLabelState = _recursiveCraft;
                _recursiveLabelDepth = _recursionDepth;
                string recursiveBox = _recursiveCraft ? "[X]" : "[  ]";
                _recursiveLabel = _recursiveCraft
                    ? $"{recursiveBox} {Language.GetText("Mods.TerraStorage.UI.CraftingPanel.RecursiveWithDepth").Format(_recursionDepth)}"
                    : $"{recursiveBox} {Language.GetTextValue("Mods.TerraStorage.UI.CraftingPanel.Recursive")}";
            }
            string depthLabel = _recursiveLabel;
            var recursiveRect = new Rectangle((int)(dims.X + halfW), (int)dims.Y, (int)halfW, 20);
            _recursiveCheckRect = recursiveRect;
            bool recursiveHover = recursiveRect.Contains(Main.MouseScreen.ToPoint());
            Color recursiveColor = _recursionDragActive ? Color.Yellow
                                 : recursiveHover ? Color.White : Color.LightGray;
            Utils.DrawBorderString(spriteBatch, depthLabel,
                new Vector2(dims.X + halfW + 4, dims.Y + 2), recursiveColor, checkScale);
            if (recursiveHover && !_recursionDragActive)
                Main.hoverItemName = "Show recursively craftable recipes\nRight-drag to set depth (1-10)";

            // Vertical slider popup while dragging recursion depth
            if (_recursionDragActive)
                DrawRecursionDepthSlider(spriteBatch);

            // Favorites panel toggle button (★)
            var starBtnRect = new Rectangle((int)(dims.X + dims.Width - 50), (int)dims.Y, 24, 20);
            bool starBtnHover = starBtnRect.Contains(Main.MouseScreen.ToPoint());
            Utils.DrawInvBG(spriteBatch, starBtnRect,
                starBtnHover ? new Color(83, 74, 20) : new Color(53, 44, 10));
            Utils.DrawBorderString(spriteBatch, "★",
                new Vector2(starBtnRect.X + 5, starBtnRect.Y + 1), Color.Gold, 0.7f);
            if (starBtnHover) Main.hoverItemName = "Toggle favorites panel";

            float gridStartY = dims.Y + GridHeaderHeight;

            if (_filteredRecipes.Count == 0)
            {
                string msg = _availableStations.Count == 0
                    ? Language.GetTextValue("Mods.TerraStorage.UI.CraftingPanel.NoCraftingCore")
                    : Language.GetTextValue("Mods.TerraStorage.UI.CraftingPanel.NoRecipesMatch");
                Utils.DrawBorderString(spriteBatch, msg,
                    new Vector2(dims.X + 5, gridStartY + 5), Color.Gray, 0.7f);
                return;
            }

            int startRow = (int)(_recipeScrollPixels / CellSize);
            float yOffset = _recipeScrollPixels % CellSize;
            int rowsToDraw = GetGridVisibleRows(dims.Height) + 2;

            var savedScissor = spriteBatch.GraphicsDevice.ScissorRectangle;
            spriteBatch.End();
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
                SamplerState.AnisotropicClamp, DepthStencilState.None,
                ScissorRasterizer, null, Main.UIScaleMatrix);
            spriteBatch.GraphicsDevice.ScissorRectangle = new Rectangle(
                (int)(dims.X * Main.UIScale), (int)(gridStartY * Main.UIScale),
                (int)(dims.Width * Main.UIScale), (int)((dims.Height - GridHeaderHeight) * Main.UIScale));

            for (int row = 0; row < rowsToDraw; row++)
            {
                for (int col = 0; col < columns; col++)
                {
                    int index = (startRow + row) * columns + col;
                    float x = dims.X + col * CellSize;
                    float y = gridStartY + row * CellSize - yOffset;

                    var cellRect = new Rectangle((int)x, (int)y, CellSize - 2, CellSize - 2);

                    if (index >= 0 && index < _filteredRecipes.Count)
                    {
                        var (recipe, canCraft) = _filteredRecipes[index];
                        bool isSelected = recipe == _selectedRecipe;
                        bool isFavorited = StoragePlayerSystem.Local.IsRecipeFavorited(recipe);

                        // Cell background: bright gold for selected, red for uncraftable, blue for craftable
                        Color cellBg;
                        if (isSelected)
                            cellBg = new Color(120, 140, 220);
                        else if (!canCraft)
                            cellBg = new Color(100, 30, 30) * 0.7f;
                        else
                            cellBg = new Color(63, 82, 151) * 0.4f;

                        if (isFavorited)
                            Utils.DrawInvBG(spriteBatch,
                                new Rectangle(cellRect.X - 1, cellRect.Y - 1, cellRect.Width + 2, cellRect.Height + 2),
                                Color.Gold * 0.35f);

                        Utils.DrawInvBG(spriteBatch, cellRect, cellBg);
                        DrawCellItem(spriteBatch, recipe.createItem.type, recipe.createItem.stack, cellRect);

                        if (isFavorited)
                            Utils.DrawBorderString(spriteBatch, "★",
                                new Vector2(cellRect.X + 2, cellRect.Y + 1), Color.Gold, 0.45f);

                        // Hover tooltip
                        if (cellRect.Contains(Main.MouseScreen.ToPoint()))
                        {
                            _scratchItem.SetDefaults(recipe.createItem.type);
                            _scratchItem.stack = recipe.createItem.stack;
                            Main.HoverItem = _scratchItem.Clone();
                            Main.hoverItemName = isFavorited
                                ? _scratchItem.Name + "\nAlt+Click to unfavorite"
                                : _scratchItem.Name + "\nAlt+Click to favorite";
                        }
                    }
                    else
                    {
                        Utils.DrawInvBG(spriteBatch, cellRect, new Color(63, 82, 151) * 0.2f);
                    }
                }
            }

            spriteBatch.End();
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
                SamplerState.AnisotropicClamp, DepthStencilState.None,
                RasterizerState.CullNone, null, Main.UIScaleMatrix);
            spriteBatch.GraphicsDevice.ScissorRectangle = savedScissor;
        }

        private void DrawDetailPanel(SpriteBatch spriteBatch)
        {
            var dims = _detailPanel.GetInnerDimensions();

            // ── Fixed header ────────────────────────────────────────────────
            float headerY = dims.Y + 5;

            // Back button
            _backBtnRect = default;
            if (_recipeHistory.Count > 0)
            {
                _backBtnRect = new Rectangle((int)dims.X + 5, (int)headerY, 28, 18);
                bool backHover = _backBtnRect.Contains(Main.MouseScreen.ToPoint());
                Utils.DrawInvBG(spriteBatch, _backBtnRect,
                    backHover ? new Color(83, 104, 181) : new Color(53, 74, 141));
                Utils.DrawBorderString(spriteBatch, "←",
                    new Vector2(_backBtnRect.X + 7, _backBtnRect.Y + 1), Color.White, 0.75f);
                if (backHover) Main.hoverItemName = "Go back";
            }

            float nameX = dims.X + (_recipeHistory.Count > 0 ? 38 : 5);
            DrawItemIcon(spriteBatch, _selectedRecipe.createItem.type,
                new Vector2(nameX + 15, headerY + 10), 0.75f);
            Utils.DrawBorderString(spriteBatch, _headerText,
                new Vector2(nameX + 30, headerY), Color.White, 0.85f);

            // Variant cycle buttons moved to the Ingredients: row — see DrawScrollableDetailContent
            _prevVariantRect = default;
            _nextVariantRect = default;

            float contentTop = headerY + 22;

            // ── Fixed bottom controls ────────────────────────────────────────
            float amtY        = dims.Y + dims.Height - 100;
            float craftMaxBtnY = dims.Y + dims.Height - 70;
            float craftBtnY   = dims.Y + dims.Height - 35;
            float contentBottom = amtY - 4;

            // ── Measure scrollable content ───────────────────────────────────
            float measuredHeight = MeasureDetailContent();
            float viewportHeight = contentBottom - contentTop;
            _detailMaxScroll = Math.Max(0f, measuredHeight - viewportHeight);
            _detailScrollOffset = Math.Clamp(_detailScrollOffset, 0f, _detailMaxScroll);

            // ── Scissor clipping for scrollable region ───────────────────────
            var clipRect = new Rectangle(
                (int)(dims.X * Main.UIScale),
                (int)(contentTop * Main.UIScale),
                (int)(dims.Width * Main.UIScale),
                (int)(Math.Max(0, contentBottom - contentTop) * Main.UIScale));

            var savedScissor = spriteBatch.GraphicsDevice.ScissorRectangle;
            spriteBatch.End();
            spriteBatch.Begin(SpriteSortMode.Deferred,
                BlendState.AlphaBlend,
                SamplerState.AnisotropicClamp,
                DepthStencilState.None,
                ScissorRasterizer, null, Main.UIScaleMatrix);
            spriteBatch.GraphicsDevice.ScissorRectangle = clipRect;

            float y = contentTop - _detailScrollOffset;
            DrawScrollableDetailContent(spriteBatch, dims, ref y);

            spriteBatch.End();
            spriteBatch.Begin(SpriteSortMode.Deferred,
                BlendState.AlphaBlend,
                SamplerState.AnisotropicClamp,
                DepthStencilState.None,
                RasterizerState.CullCounterClockwise, null, Main.UIScaleMatrix);
            spriteBatch.GraphicsDevice.ScissorRectangle = savedScissor;

            // ── Scrollbar indicator ──────────────────────────────────────────
            if (_detailMaxScroll > 0f)
            {
                float sbX = dims.X + dims.Width - 6;
                float sbH = contentBottom - contentTop;
                float thumbH = Math.Max(14, sbH * (viewportHeight / (viewportHeight + _detailMaxScroll)));
                float thumbY = contentTop + (_detailScrollOffset / _detailMaxScroll) * (sbH - thumbH);
                Utils.DrawInvBG(spriteBatch,
                    new Rectangle((int)sbX, (int)contentTop, 4, (int)sbH),
                    new Color(30, 40, 80) * 0.6f);
                Utils.DrawInvBG(spriteBatch,
                    new Rectangle((int)sbX, (int)thumbY, 4, (int)thumbH),
                    new Color(89, 116, 213) * 0.8f);
            }

            // ── Amount controls ──────────────────────────────────────────────
            float btnWidth = 40;
            float startX = dims.X + 5;
            int[] amounts = { 1, 5, 25, 100 };

            for (int i = 0; i < 4; i++)
            {
                float btnX = startX + i * (btnWidth + 4);
                var btnRect = new Rectangle((int)btnX, (int)amtY, (int)btnWidth, 25);
                bool hover = btnRect.Contains(Main.MouseScreen.ToPoint());
                Color bgColor = hover ? new Color(83, 104, 181) : new Color(53, 74, 141);
                Utils.DrawInvBG(spriteBatch, btnRect, bgColor);

                string label = amounts[i].ToString();
                var labelSize = FontAssets.MouseText.Value.MeasureString(label) * 0.7f;
                Utils.DrawBorderString(spriteBatch, label,
                    new Vector2(btnX + btnWidth / 2 - labelSize.X / 2, amtY + 3), Color.White, 0.7f);

                if (hover)
                {
                    Main.LocalPlayer.mouseInterface = true;
                    Main.hoverItemName = $"Left-click: +{amounts[i]}  |  Right-click: -{amounts[i]}";
                }
            }

            // Craft Max button (right of +1/+5/+25/+100 on the same row)
            float craftMaxInlineX = startX + 4 * (btnWidth + 4) + 4;
            var craftMaxRect = new Rectangle((int)craftMaxInlineX, (int)amtY, (int)btnWidth, 25);
            bool craftMaxFeasible = _currentPlan is { IsFeasible: true };
            bool craftMaxHover = craftMaxRect.Contains(Main.MouseScreen.ToPoint());
            Color craftMaxColor = craftMaxFeasible
                ? (craftMaxHover ? new Color(70, 170, 70) : new Color(50, 140, 50))
                : new Color(80, 30, 30);
            Utils.DrawInvBG(spriteBatch, craftMaxRect, craftMaxColor);
            string craftMaxLabel = "MAX";
            var craftMaxSize = FontAssets.MouseText.Value.MeasureString(craftMaxLabel) * 0.75f;
            Utils.DrawBorderString(spriteBatch, craftMaxLabel,
                new Vector2(craftMaxRect.X + craftMaxRect.Width / 2f - craftMaxSize.X / 2f, amtY + 4),
                Color.White, 0.75f);
            if (craftMaxHover)
            {
                Main.LocalPlayer.mouseInterface = true;
                Main.hoverItemName = craftMaxFeasible
                    ? $"Set amount to max craftable (x{ComputeMaxCraftAmount()})"
                    : "Not enough materials";
            }

            // Amount input field (middle row)
            string amtLabel = "Amount:";
            var amtLabelSize = FontAssets.MouseText.Value.MeasureString(amtLabel) * 0.75f;
            Utils.DrawBorderString(spriteBatch, amtLabel,
                new Vector2(dims.X + 5, craftMaxBtnY + 4), Color.LightGray, 0.75f);
            float fieldOffsetX = amtLabelSize.X + 10;
            int checkboxSize = 18;
            int checkboxGap = 6;
            int cbY = (int)craftMaxBtnY + (25 - checkboxSize) / 2;
            // Right-anchor the three option checkboxes (Clean Craft, Craft to Inventory, Lock Recipe);
            // the amount field fills the space to their left so they never overlap, even on a narrow panel.
            int lockCbX = (int)(dims.X + dims.Width - 9 - checkboxSize);
            int invCbX = lockCbX - checkboxGap - checkboxSize;
            int cbX = invCbX - checkboxGap - checkboxSize;
            int fieldLeft = (int)(dims.X + 5 + fieldOffsetX);
            var fieldRect = new Rectangle(fieldLeft, (int)craftMaxBtnY, Math.Max(24, cbX - checkboxGap - fieldLeft), 25);
            _amountFieldRect = fieldRect; // expose to Update for raw mouse detection
            Color fieldBg = _amountFieldFocused  ? new Color(45, 55, 100)
                          : _amountDragActive    ? new Color(60, 45, 110)
                          : new Color(35, 43, 79) * 0.9f;
            Utils.DrawInvBG(spriteBatch, fieldRect, fieldBg);
            string fieldDisplay = _amountFieldText;
            if (_amountFieldFocused && (int)(Main.GameUpdateCount / 20) % 2 == 0)
                fieldDisplay += "|";
            Utils.DrawBorderString(spriteBatch, fieldDisplay,
                new Vector2(fieldRect.X + 6, craftMaxBtnY + 2), Color.White, 0.8f);
            if (!_amountFieldFocused && !_amountDragActive && fieldRect.Contains(Main.MouseScreen.ToPoint()))
                Main.hoverItemName = "Left-click to type  |  Right-drag to adjust  |  Middle-click to reset";

            // Clean Craft checkbox (leftmost of the three right-anchored options)
            var cbRect = new Rectangle(cbX, cbY, checkboxSize, checkboxSize);
            _cleanCraftCheckRect = cbRect;
            bool cbHover = cbRect.Contains(Main.MouseScreen.ToPoint());
            Color cbBg = cbHover ? new Color(83, 104, 181)
                       : _cleanCraft ? new Color(80, 130, 60)
                       : new Color(53, 74, 141);
            Utils.DrawInvBG(spriteBatch, cbRect, cbBg);
            if (cbHover)
            {
                Main.LocalPlayer.mouseInterface = true;
                Main.hoverItemName = _cleanCraft
                    ? "Clean Craft: ON\nCrafts items with no prefixes or other modifiers.\nClick to disable."
                    : "Clean Craft: OFF\nItems receive vanilla prefixes and mod modifiers.\nClick to enable.";
            }

            // Craft to Inventory checkbox (middle of the three)
            var invCbRect = new Rectangle(invCbX, cbY, checkboxSize, checkboxSize);
            _craftToInvCheckRect = invCbRect;
            bool invCbHover = invCbRect.Contains(Main.MouseScreen.ToPoint());
            Color invCbBg = invCbHover ? new Color(83, 104, 181)
                          : _craftToInventory ? new Color(80, 130, 60)
                          : new Color(53, 74, 141);
            Utils.DrawInvBG(spriteBatch, invCbRect, invCbBg);
            if (invCbHover)
            {
                Main.LocalPlayer.mouseInterface = true;
                Main.hoverItemName = _craftToInventory
                    ? "Craft to Inventory: ON\nCrafted items go to your inventory.\nClick to disable."
                    : "Craft to Inventory: OFF\nCrafted items go to storage.\nClick to enable.";
            }

            // Lock Recipe checkbox (rightmost) — global toggle: when on, crafting uses exactly the
            // selected recipe variant instead of auto-picking one for the item.
            var lockCbRect = new Rectangle(lockCbX, cbY, checkboxSize, checkboxSize);
            _lockCheckRect = lockCbRect;
            bool lockHover = lockCbRect.Contains(Main.MouseScreen.ToPoint());
            Color lockBg = lockHover ? new Color(83, 104, 181)
                         : _lockRecipe ? new Color(80, 130, 60)
                         : new Color(53, 74, 141);
            Utils.DrawInvBG(spriteBatch, lockCbRect, lockBg);
            if (lockHover)
            {
                Main.LocalPlayer.mouseInterface = true;
                Main.hoverItemName = _lockRecipe
                    ? "Lock Recipe: ON\nCrafting uses exactly the selected recipe variant.\nStays on for every recipe until disabled.\nClick to disable."
                    : "Lock Recipe: OFF\nCrafting auto-picks any working recipe for the item.\nClick to enable.";
            }

            // Craft button + output slot row
            int outSlotSize = 40;

            // Craft button (on the left)
            var craftRect = new Rectangle((int)dims.X + 5, (int)craftBtnY, (int)dims.Width - outSlotSize - 24, 30);

            // Output slot (on the right of craft button): shows current stock in storage, click to
            // take. Recounted only when storage contents or the selected output change — a live
            // per-frame CountItem here scanned every disk stack 60 times a second.
            int outSlotX = craftRect.Right + 14;
            long storageVersion = StorageWorldSystem.Instance.StorageVersion;
            int outputType = _selectedRecipe.createItem.type;
            if (_refresh.NeedsOutputStockRecount(storageVersion, outputType))
            {
                _refresh.MarkOutputStockCounted(storageVersion, outputType);
                _outputInStorage = StorageWorldSystem.Instance.CountItem(_diskIds, outputType);
            }
            int outputInStorage = _outputInStorage;
            var outSlotRect = new Rectangle(outSlotX, (int)craftBtnY, outSlotSize, outSlotSize);
            bool slotHover = outSlotRect.Contains(Main.MouseScreen.ToPoint());
            Utils.DrawInvBG(spriteBatch, outSlotRect, slotHover ? new Color(83, 104, 181) : new Color(43, 54, 101));
            if (outputInStorage > 0)
            {
                DrawCellItem(spriteBatch, outputType, Math.Min(outputInStorage, 9999), outSlotRect);
            }
            if (slotHover)
            {
                Main.LocalPlayer.mouseInterface = true;
                Main.hoverItemName = outputInStorage > 0
                    ? $"{_selectedOutputName} x{outputInStorage} in storage\nClick to take"
                    : $"{_selectedOutputName} - none in storage";
            }
            bool feasible = _currentPlan is { IsFeasible: true };
            bool directExtract = _currentPlan is { IsDirectExtract: true };
            bool noOp = _craftIsNoOp;

            Color craftColor;
            if (noOp)
                craftColor = new Color(150, 50, 50);   // red: clicking would change nothing
            else if (directExtract)
                craftColor = new Color(40, 80, 160);
            else if (feasible)
                craftColor = new Color(50, 150, 50);
            else
                craftColor = new Color(150, 50, 50);
            Utils.DrawInvBG(spriteBatch, craftRect, craftColor);

            var textSize = FontAssets.MouseText.Value.MeasureString(_craftBtnText) * 0.75f;
            float craftTextX = craftRect.X + craftRect.Width / 2f - textSize.X / 2f;
            Utils.DrawBorderString(spriteBatch, _craftBtnText,
                new Vector2(craftTextX, craftBtnY + 6), Color.White, 0.75f);

            // Explain the no-op on hover so it's obvious why the button won't do anything.
            if (noOp && craftRect.Contains(Main.MouseScreen.ToPoint()))
            {
                Main.LocalPlayer.mouseInterface = true;
                Main.hoverItemName = "You already have these in storage, and they can't be made from your\nother items — this recipe just loops back into itself. Nothing would change.";
            }
        }

        // Allocation-free replacement for requiredTile.Any(t => t >= 0) in per-frame paths.
        private static bool HasRequiredTile(Recipe recipe)
        {
            for (int i = 0; i < recipe.requiredTile.Count; i++)
                if (recipe.requiredTile[i] >= 0)
                    return true;
            return false;
        }

        //Calculates total height of the scrollable detail content without drawing it.
        private float MeasureDetailContent()
        {
            if (_selectedRecipe == null) return 0;

            float y = 0;
            const int cellH = 40;
            int ingCellSize = 42;

            // Requires section
            bool hasRequirements = HasRequiredTile(_selectedRecipe)
                                || _selectedRecipe.Conditions.Count > 0;
            if (hasRequirements)
            {
                y += 18; // "Requires:" label

                // Measure station + condition row wrapping using a dummy panel width
                // We can't call GetInnerDimensions here, so use a reasonable estimate
                float panelW = _detailPanel?.GetInnerDimensions().Width ?? 200;
                float maxX = panelW - 10;
                float reqX = 0;

                foreach (int tileType in _selectedRecipe.requiredTile)
                {
                    if (tileType < 0) continue;
                    if (reqX + cellH > maxX && reqX > 0) { reqX = 0; y += cellH + 2; }
                    reqX += cellH + 2;
                }

                foreach (var cond in _selectedRecipe.Conditions)
                {
                    int condItem = GetConditionItemType(cond);
                    if (condItem > 0)
                    {
                        if (reqX + cellH > maxX && reqX > 0) { reqX = 0; y += cellH + 2; }
                        reqX += cellH + 2;
                    }
                    else
                    {
                        string label = cond.Description.Value;
                        float labelW = FontAssets.MouseText.Value.MeasureString(label).X * 0.68f + 12;
                        if (reqX + labelW > maxX && reqX > 0) { reqX = 0; y += cellH + 2; }
                        reqX += labelW + 4;
                    }
                }

                y += cellH + 6;
            }

            // Ingredients
            y += 18; // "Ingredients:" label
            {
                float panelW = _detailPanel?.GetInnerDimensions().Width ?? 200;
                float maxX = panelW - 10;
                float ingX = 0;
                // One cell per DISTINCT type, matching the draw loop. Measuring raw slots would
                // over-measure a recipe that names an item twice and scroll into dead space.
                _drawnIngredientTypes.Clear();
                foreach (var ingredient in _selectedRecipe.requiredItem)
                {
                    if (ingredient.type <= ItemID.None) continue;
                    if (!_drawnIngredientTypes.Add(ingredient.type)) continue;
                    if (ingX + ingCellSize > maxX) { ingX = 0; y += ingCellSize; }
                    ingX += ingCellSize;
                }
                y += ingCellSize + 5;
            }

            // Crafting chain
            if (_currentPlan != null && _currentPlan.Steps.Count > 1)
            {
                y += 16; // header line
                y += _currentPlan.Steps.Count * 16;
            }

            return y;
        }

        private void DrawScrollableDetailContent(SpriteBatch spriteBatch, CalculatedStyle dims, ref float y)
        {
            const int cellH = 40;
            int ingCellSize = 42;
            _ingredientHitRects.Clear();

            // "Requires:" section
            bool hasRequirements = HasRequiredTile(_selectedRecipe)
                                || _selectedRecipe.Conditions.Count > 0;
            if (hasRequirements)
            {
                Utils.DrawBorderString(spriteBatch, Language.GetTextValue("Mods.TerraStorage.UI.CraftingPanel.Requires"),
                    new Vector2(dims.X + 5, y), Color.LightGoldenrodYellow, 0.7f);
                y += 18;

                float reqStartX = dims.X + 5;
                float reqX = reqStartX;

                foreach (int tileType in _selectedRecipe.requiredTile)
                {
                    if (tileType < 0) continue;

                    bool avail = RecipeResolver.IsStationSatisfied(tileType, _availableStations);
                    int stationItemType = RecipeResolver.GetTileItemType(tileType);
                    var cellRect = new Rectangle((int)reqX, (int)y, cellH, cellH);

                    Utils.DrawInvBG(spriteBatch, cellRect,
                        avail ? new Color(40, 120, 40) * 0.4f : new Color(140, 40, 40) * 0.4f);
                    if (stationItemType > 0)
                        DrawCellItem(spriteBatch, stationItemType, 0, cellRect);
                    else
                    {
                        string name = RecipeResolver.GetTileName(tileType);
                        float scale = 0.55f;
                        var size = FontAssets.MouseText.Value.MeasureString(name) * scale;
                        // Shrink further if name is too wide for the cell
                        if (size.X > cellRect.Width - 4)
                            scale *= (cellRect.Width - 4) / size.X;
                        var textPos = new Vector2(
                            cellRect.X + cellRect.Width  / 2f,
                            cellRect.Y + cellRect.Height / 2f - FontAssets.MouseText.Value.MeasureString(name).Y * scale / 2f);
                        Utils.DrawBorderString(spriteBatch, name, textPos, Color.White, scale, 0.5f);
                    }

                    if (cellRect.Contains(Main.MouseScreen.ToPoint()))
                    {
                        if (stationItemType > 0)
                        {
                            _scratchItem.SetDefaults(stationItemType);
                            Main.HoverItem = _scratchItem.Clone();
                            Main.hoverItemName = _scratchItem.Name + (avail ? " (Available)" : " (Missing)");
                        }
                        else
                        {
                            Main.hoverItemName = RecipeResolver.GetTileName(tileType) + (avail ? " (Available)" : " (Missing)");
                        }
                    }

                    reqX += cellH + 2;
                    if (reqX + cellH > dims.X + dims.Width - 10)
                    {
                        reqX = reqStartX;
                        y += cellH + 2;
                    }
                }

                float fontH = FontAssets.MouseText.Value.MeasureString("A").Y * 0.68f;
                foreach (var cond in _selectedRecipe.Conditions)
                {
                    bool met = cond.Predicate() || ConditionMetByNetwork(cond);
                    string label = cond.Description.Value;
                    int condItem = GetConditionItemType(cond);

                    if (condItem > 0)
                    {
                        // Fluid conditions: render as item cell (same style as station slots)
                        if (reqX + cellH > dims.X + dims.Width - 10 && reqX > reqStartX)
                        {
                            reqX = reqStartX;
                            y += cellH + 2;
                        }
                        var cellRect = new Rectangle((int)reqX, (int)y, cellH, cellH);
                        Utils.DrawInvBG(spriteBatch, cellRect,
                            met ? new Color(40, 120, 40) * 0.4f : new Color(140, 40, 40) * 0.4f);
                        DrawCellItem(spriteBatch, condItem, 0, cellRect);
                        if (cellRect.Contains(Main.MouseScreen.ToPoint()))
                            Main.hoverItemName = label + (met ? " (Met)" : " (Not met)");
                        reqX += cellH + 2;
                    }
                    else
                    {
                        // Non-fluid conditions: render as text tag
                        float labelW = FontAssets.MouseText.Value.MeasureString(label).X * 0.68f + 12;
                        if (reqX + labelW > dims.X + dims.Width - 10 && reqX > reqStartX)
                        {
                            reqX = reqStartX;
                            y += cellH + 2;
                        }
                        var tagRect = new Rectangle((int)reqX, (int)y, (int)labelW, cellH);
                        Utils.DrawInvBG(spriteBatch, tagRect,
                            met ? new Color(40, 120, 40) * 0.5f : new Color(140, 40, 40) * 0.5f);
                        Utils.DrawBorderString(spriteBatch, label,
                            new Vector2(tagRect.X + 6, tagRect.Y + (cellH - fontH) / 2f),
                            Color.White, 0.68f);
                        if (tagRect.Contains(Main.MouseScreen.ToPoint()))
                            Main.hoverItemName = label + (met ? " (Met)" : " (Not met)");
                        reqX += (int)labelW + 4;
                    }
                }

                y += cellH + 6;
            }

            // Ingredients label + variant paginator on the same row
            Utils.DrawBorderString(spriteBatch, Language.GetTextValue("Mods.TerraStorage.UI.CraftingPanel.Ingredients"),
                new Vector2(dims.X + 5, y), Color.Yellow, 0.7f);

            if (_currentVariants.Count > 1)
            {
                string variantLabel = $"{_currentVariantIndex + 1}/{_currentVariants.Count}";
                float varW    = FontAssets.MouseText.Value.MeasureString(variantLabel).X * 0.65f;
                float arrowW  = 18f;
                float totalW  = arrowW + 4 + varW + 4 + arrowW;
                float startX  = dims.X + dims.Width - totalW - 8;

                _prevVariantRect = new Rectangle((int)startX, (int)y, (int)arrowW, 18);
                _nextVariantRect = new Rectangle((int)(startX + arrowW + 4 + varW + 4), (int)y, (int)arrowW, 18);

                bool prevHov = _prevVariantRect.Contains(Main.MouseScreen.ToPoint());
                bool nextHov = _nextVariantRect.Contains(Main.MouseScreen.ToPoint());

                Utils.DrawInvBG(spriteBatch, _prevVariantRect, prevHov ? new Color(83, 104, 181) : new Color(53, 74, 141));
                Utils.DrawBorderString(spriteBatch, "<", new Vector2(_prevVariantRect.X + 4, y + 1), Color.White, 0.75f);

                Utils.DrawBorderString(spriteBatch, variantLabel,
                    new Vector2(startX + arrowW + 4, y + 1), Color.LightGray, 0.65f);

                Utils.DrawInvBG(spriteBatch, _nextVariantRect, nextHov ? new Color(83, 104, 181) : new Color(53, 74, 141));
                Utils.DrawBorderString(spriteBatch, ">", new Vector2(_nextVariantRect.X + 5, y + 1), Color.White, 0.75f);

                if (prevHov) Main.hoverItemName = "Previous recipe";
                if (nextHov) Main.hoverItemName = "Next recipe";
            }

            y += 18;

            float ingStartX = dims.X + 5;
            float ingX = ingStartX;

            // One square per distinct item type: a recipe may name the same item in two slots, and the
            // cache holds their combined need. Drawing per raw slot would show that combined figure
            // twice and let the two squares disagree.
            _drawnIngredientTypes.Clear();
            foreach (var ingredient in _selectedRecipe.requiredItem)
            {
                if (ingredient.type <= ItemID.None) continue;
                if (!_drawnIngredientTypes.Add(ingredient.type)) continue;

                // No cache entry means the preview has not run for this selection. Skipping is the
                // safe read: the zeroed tuple would render a satisfied green "0/0".
                if (!_ingredientCache.TryGetValue(ingredient.type, out var cached)) continue;
                int needed = cached.needed;
                int totalHave = cached.totalHave;
                bool hasRecipe = cached.hasRecipe;
                bool isGroup = cached.isGroup;

                // Short on this ingredient, but the shared pool can still sub-craft the rest: show it
                // as satisfiable (orange need/need) instead of a blocking red shortfall. Red is
                // reserved for slots that genuinely cannot be filled, whatever stock they hold — this
                // used to key off the WHOLE plan being feasible, which painted every sub-craftable
                // ingredient red as soon as some OTHER slot blocked the recipe, and left the real
                // blocker looking healthier for merely holding a partial stack.
                bool craftableShortfall = totalHave < needed && cached.satisfiable;

                Color countColor;
                if (totalHave >= needed)      countColor = Color.LightGreen;
                else if (craftableShortfall)  countColor = Color.Orange;
                else                          countColor = Color.IndianRed;

                var ingRect = new Rectangle((int)ingX, (int)y, ingCellSize - 2, ingCellSize - 2);
                _ingredientHitRects.Add((ingRect, ingredient.type));
                Utils.DrawInvBG(spriteBatch, ingRect, new Color(63, 82, 151) * 0.4f);
                DrawCellItem(spriteBatch, ingredient.type, 0, ingRect);

                string countText = cached.text ?? (craftableShortfall ? $"{needed}/{needed}" : $"{totalHave}/{needed}");
                Utils.DrawBorderString(spriteBatch, countText,
                    new Vector2(ingRect.Right - 4, ingRect.Bottom - 4),
                    countColor, 0.6f, 1f, 1f);

                // Recipe indicator (only when not a group ingredient)
                if (!isGroup && hasRecipe)
                    Utils.DrawBorderString(spriteBatch, "»",
                        new Vector2(ingRect.X + 1, ingRect.Y + 1), Color.LightBlue, 0.5f);

                if (ingRect.Contains(Main.MouseScreen.ToPoint()))
                {
                    _scratchItem.SetDefaults(ingredient.type);

                    int foundGid = -1;
                    foreach (int gid in _selectedRecipe.acceptedGroups)
                    {
                        if (RecipeGroup.recipeGroups[gid].ContainsItem(ingredient.type)) { foundGid = gid; break; }
                    }

                    if (foundGid >= 0)
                    {
                        string groupName  = RecipeResolver.GetGroupName(foundGid);
                        string groupItems = RecipeResolver.GetGroupItemNames(foundGid);
                        _scratchItem.SetNameOverride($"{groupName} ({(craftableShortfall ? needed : totalHave)}/{needed})");
                        Main.HoverItem    = _scratchItem.Clone();
                        Main.hoverItemName = $"{groupItems}";
                    }
                    else
                    {
                        // Same counts the square shows: reading totalHave here would contradict the
                        // orange need/need drawn a few pixels away.
                        Main.HoverItem = _scratchItem.Clone();
                        Main.hoverItemName = hasRecipe
                            ? $"{_scratchItem.Name} ({countText})\nRight-click to view recipe"
                            : $"{_scratchItem.Name} ({countText})";
                    }
                }

                ingX += ingCellSize;
                if (ingX + ingCellSize > dims.X + dims.Width - 10)
                {
                    ingX = ingStartX;
                    y += ingCellSize;
                }
            }
            y += ingCellSize + 5;

            // Crafting chain steps
            if (_chainLabels.Count > 0)
            {
                Utils.DrawBorderString(spriteBatch, Language.GetText("Mods.TerraStorage.UI.CraftingPanel.CraftingChain").Format(_chainLabels.Count),
                    new Vector2(dims.X + 5, y), Color.LightGray, 0.65f);
                y += 16;

                foreach (var (stepType, label) in _chainLabels)
                {
                    DrawItemIcon(spriteBatch, stepType,
                        new Vector2(dims.X + 18, y + 8), 0.4f);
                    Utils.DrawBorderString(spriteBatch, label,
                        new Vector2(dims.X + 32, y), Color.LightGray, 0.55f);
                    y += 16;
                }
            }
        }

        private void DrawEmptyDetail(SpriteBatch spriteBatch)
        {
            var dims = _detailPanel.GetInnerDimensions();
            float y = dims.Y + 10;

            Utils.DrawBorderString(spriteBatch, Language.GetTextValue("Mods.TerraStorage.UI.CraftingPanel.SelectRecipe"),
                new Vector2(dims.X + 10, y), Color.Gray, 0.9f);
            y += 30;

            if (_availableStations.Count > 0)
            {
                Utils.DrawBorderString(spriteBatch, Language.GetTextValue("Mods.TerraStorage.UI.CraftingPanel.AvailableStations"),
                    new Vector2(dims.X + 10, y), Color.LightGoldenrodYellow, 0.75f);
                y += 20;

                float sX = dims.X + 5;
                int sCellSize = 42;

                foreach (int tileType in _availableStations)
                {
                    int stationItemType = RecipeResolver.GetTileItemType(tileType);
                    var sRect = new Rectangle((int)sX, (int)y, sCellSize - 2, sCellSize - 2);
                    Utils.DrawInvBG(spriteBatch, sRect, new Color(40, 120, 40) * 0.4f);

                    if (stationItemType > 0)
                        DrawCellItem(spriteBatch, stationItemType, 0, sRect);

                    if (sRect.Contains(Main.MouseScreen.ToPoint()))
                    {
                        if (stationItemType > 0)
                        {
                            _scratchItem.SetDefaults(stationItemType);
                            Main.HoverItem = _scratchItem.Clone();
                            Main.hoverItemName = _scratchItem.Name;
                        }
                        else
                        {
                            Main.hoverItemName = RecipeResolver.GetTileName(tileType);
                        }
                    }

                    sX += sCellSize;
                    if (sX + sCellSize > dims.X + dims.Width - 10)
                    {
                        sX = dims.X + 5;
                        y += sCellSize;
                    }
                }
                y += sCellSize;
            }
            else
            {
                Utils.DrawBorderString(spriteBatch, Language.GetTextValue("Mods.TerraStorage.UI.CraftingPanel.NoCraftingCore"),
                    new Vector2(dims.X + 10, y), Color.IndianRed, 0.75f);
                y += 20;
                Utils.DrawBorderString(spriteBatch, Language.GetTextValue("Mods.TerraStorage.UI.CraftingPanel.PlaceCraftingCore"),
                    new Vector2(dims.X + 10, y), Color.Gray, 0.7f);
                y += 18;
                Utils.DrawBorderString(spriteBatch, Language.GetTextValue("Mods.TerraStorage.UI.CraftingPanel.AndInsertStations"),
                    new Vector2(dims.X + 10, y), Color.Gray, 0.7f);
            }
        }

        private void DrawRecursionDepthSlider(SpriteBatch spriteBatch)
        {
            const int sliderW = 30;
            const int sliderH = 140;
            const int notchCount = MaxRecursionDepth;
            float notchSpacing = (sliderH - 10f) / (notchCount - 1);

            // Position above the recursive checkbox, centered on it
            float sliderX = _recursiveCheckRect.X + _recursiveCheckRect.Width / 2f - sliderW / 2f;
            float sliderY = _recursiveCheckRect.Y - sliderH - 6;

            // Clamp to screen
            if (sliderY < 4) sliderY = 4;

            var bgRect = new Rectangle((int)sliderX - 2, (int)sliderY - 2, sliderW + 4, sliderH + 4);
            Utils.DrawInvBG(spriteBatch, bgRect, new Color(25, 25, 50) * 0.95f);

            // Draw notches and numbers (1 at bottom, 10 at top)
            for (int i = 1; i <= notchCount; i++)
            {
                float ny = sliderY + sliderH - 5 - (i - 1) * notchSpacing;
                bool active = i <= _recursionDepth;
                Color notchColor = active ? Color.LightSkyBlue : new Color(60, 60, 80);
                var notchRect = new Rectangle((int)sliderX + 2, (int)ny - 2, sliderW - 4, 4);
                Utils.DrawInvBG(spriteBatch, notchRect, active ? new Color(40, 80, 160) * 0.8f : new Color(30, 30, 50) * 0.6f);

                if (i == 1 || i == 5 || i == 10)
                {
                    Utils.DrawBorderString(spriteBatch, i.ToString(),
                        new Vector2(sliderX + sliderW + 4, ny - 6), notchColor, 0.5f);
                }
            }

            // Current value label at top
            Utils.DrawBorderString(spriteBatch, Language.GetText("Mods.TerraStorage.UI.CraftingPanel.Depth").Format(_recursionDepth),
                new Vector2(sliderX - 2, sliderY - 16), Color.Yellow, 0.55f);
        }

        // Returns true if condition is satisfied by one of the
        // environmental emitter tiles connected to the network (e.g. WaterSource,
        // LavaSource) rather than the player's physical position.
        private bool ConditionMetByNetwork(Condition condition)
        {
            if (condition == Condition.NearWater   && _availableConditions.Contains(CraftingCondition.NearWater))   return true;
            if (condition == Condition.NearLava    && _availableConditions.Contains(CraftingCondition.NearLava))    return true;
            if (condition == Condition.NearHoney   && _availableConditions.Contains(CraftingCondition.NearHoney))   return true;
            if (condition == Condition.InGraveyard && _availableConditions.Contains(CraftingCondition.InGraveyard)) return true;
            if (condition == Condition.InSnow      && _availableConditions.Contains(CraftingCondition.InSnow))      return true;
            return false;
        }

        // Returns the item type to display as an icon for a recipe condition, or -1 if
        // the condition has no item representation (falls back to text tag rendering).
        private static int GetConditionItemType(Condition cond)
        {
            if (cond == Condition.NearWater)    return ItemID.BottomlessBucket;
            if (cond == Condition.NearLava)     return ItemID.BottomlessLavaBucket;
            if (cond == Condition.NearHoney)    return ItemID.BottomlessHoneyBucket;
            if (cond == Condition.InSnow)       return ItemID.IceMachine;
            if (cond == Condition.InGraveyard)  return ItemID.Gravestone;
            return -1;
        }

        private void DrawCellItem(SpriteBatch spriteBatch, int itemType, int stack, Rectangle cellRect)
        {
            Main.instance.LoadItem(itemType);
            var texture = TextureAssets.Item[itemType].Value;
            var sourceRect = Main.itemAnimations[itemType] != null
                ? Main.itemAnimations[itemType].GetFrame(texture)
                : texture.Frame();

            float scale = 1f;
            float maxDim = Math.Max(sourceRect.Width, sourceRect.Height);
            if (maxDim > 27)
                scale = 27f / maxDim;

            var center = new Vector2(cellRect.X + cellRect.Width / 2, cellRect.Y + cellRect.Height / 2);
            var origin = new Vector2(sourceRect.Width / 2, sourceRect.Height / 2);

            spriteBatch.Draw(texture, center, sourceRect, Color.White, 0f, origin, scale, SpriteEffects.None, 0f);

            if (stack > 1)
            {
                string countText = stack >= 1000 ? $"{stack / 1000f:0.#}k" : stack.ToString();
                Utils.DrawBorderString(spriteBatch, countText,
                    new Vector2(cellRect.Right - 4, cellRect.Bottom - 4),
                    Color.White, 0.6f, 1f, 1f);
            }
        }

private void DrawItemIcon(SpriteBatch spriteBatch, int itemType, Vector2 center, float baseScale, Color? tint = null)
        {
            Main.instance.LoadItem(itemType);
            var texture = TextureAssets.Item[itemType].Value;
            var sourceRect = Main.itemAnimations[itemType] != null
                ? Main.itemAnimations[itemType].GetFrame(texture)
                : texture.Frame();

            float scale = baseScale;
            float maxDim = Math.Max(sourceRect.Width, sourceRect.Height);
            if (maxDim > 29) scale *= 29f / maxDim;

            spriteBatch.Draw(texture, center, sourceRect, tint ?? Color.White, 0f,
                new Vector2(sourceRect.Width / 2, sourceRect.Height / 2), scale, SpriteEffects.None, 0f);
        }

        // Externally selects a recipe (used by the Crafting Tree right-click).
        public void SelectRecipeExternal(Recipe recipe)
        {
            // Find the recipe in _allRecipes to get its canCraft status
            foreach (var entry in _allRecipes)
            {
                if (entry.recipe == recipe)
                {
                    SelectRecipe(entry.recipe, entry.canCraft);
                    return;
                }
            }
            // Fallback: select it even if not in current list
            SelectRecipe(recipe, false);
        }

        public override void Update(GameTime gameTime)
        {
            base.Update(gameTime);

            // Smooth scroll: recipe list (pixel-offset, same pattern as UIItemGrid)
            if (_recipeScrollbar != null)
            {
                var gdims = _recipeGridPanel?.GetInnerDimensions() ?? default;
                int gcols = gdims.Width > 0 ? GetGridColumns(gdims.Width) : 1;
                int totalRows = (_filteredRecipes.Count + gcols - 1) / gcols;
                int visRows = GetGridVisibleRows(gdims.Height);
                _recipeScrollbar.SetView(visRows, totalRows);

                float barPixels = _recipeScrollbar.ViewPosition * CellSize;
                if (Math.Abs(barPixels - _recipeScrollBarLastPos) > 0.5f)
                {
                    _recipeScrollPixels = barPixels;
                    _recipeScrollTarget = barPixels;
                }
                else
                {
                    float maxPx = Math.Max(0, (totalRows - visRows) * CellSize);
                    _recipeScrollTarget = Math.Clamp(_recipeScrollTarget, 0, maxPx);
                    float diff = _recipeScrollTarget - _recipeScrollPixels;
                    if (Math.Abs(diff) < 0.5f) _recipeScrollPixels = _recipeScrollTarget;
                    else _recipeScrollPixels += diff * 0.15f;
                    _recipeScrollPixels = Math.Clamp(_recipeScrollPixels, 0, maxPx);
                    _recipeScrollbar.ViewPosition = _recipeScrollPixels / CellSize;
                }
                _recipeScrollBarLastPos = _recipeScrollbar.ViewPosition * CellSize;
            }

            // Smooth scroll: detail panel
            _detailScrollTarget = Math.Clamp(_detailScrollTarget, 0f, Math.Max(0f, _detailMaxScroll));
            float dDiff = _detailScrollTarget - _detailScrollOffset;
            if (Math.Abs(dDiff) < 0.5f) _detailScrollOffset = _detailScrollTarget;
            else _detailScrollOffset += dDiff * 0.15f;
            _detailScrollOffset = Math.Clamp(_detailScrollOffset, 0f, Math.Max(0f, _detailMaxScroll));

            // Check for pending recipe selection from Crafting Tree
            if (CraftingTreeState.PendingRecipeSelection != null)
            {
                SelectRecipeExternal(CraftingTreeState.PendingRecipeSelection);
                CraftingTreeState.PendingRecipeSelection = null;
            }

            // Middle-click on amount field: reset to 1
            if (Main.mouseMiddle && _detailPanel != null && _selectedRecipe != null)
            {
                var dims = _detailPanel.GetInnerDimensions();
                float relY = Main.MouseScreen.Y - dims.Y;
                if (relY >= dims.Height - 70 && relY < dims.Height - 45)
                {
                    SetCraftAmount(1);
                    _amountFieldFocused = false;
                    _amountInput.Deactivate();
                    _amountFieldMouseDown = false;
                    _amountDragActive = false;
                }
            }

            // Right-mouse tracking for both amount field drag and recursion depth drag.
            // rightDown starts a drag, so it reads the suppressed button -- a click another window
            // consumed must not start one here. rightHeld continues a drag already in progress, so
            // it reads the real button: a gesture ends when the player releases, not when some other
            // window happens to consume a click.
            bool rightDown = Main.mouseRight;
            bool rightHeld = UIClickBlocker.RealMouseRight;
            bool rightJustPressed = rightDown && !_prevMouseRight;
            _prevMouseRight = UIClickBlocker.RealMouseRight;

            // Recursion depth drag: right-drag on the Recursive checkbox (vertical)
            if (rightJustPressed && _recursiveCheckRect.Contains(Main.MouseScreen.ToPoint()) && !_amountFieldMouseDown)
            {
                _recursionDragActive = true;
                _recursionDragStartY = Main.MouseScreen.Y;
                _recursionDragBaseDepth = _recursionDepth;
            }

            if (_recursionDragActive)
            {
                if (rightHeld)
                {
                    // Drag up = increase depth, drag down = decrease
                    float deltaY = _recursionDragStartY - Main.MouseScreen.Y;
                    int newDepth = _recursionDragBaseDepth + (int)(deltaY / 12f);
                    newDepth = Math.Clamp(newDepth, 1, MaxRecursionDepth);
                    if (newDepth != _recursionDepth)
                    {
                        _recursionDepth = newDepth;
                        RecipeResolver.MaxDepth = _recursionDepth;
                    }
                }
                else
                {
                    // Released — apply and restart deferred pass if recursive is on
                    _recursionDragActive = false;
                    RecipeResolver.MaxDepth = _recursionDepth;
                    if (_recursiveCraft)
                    {
                        StartRecursivePass();
                    }
                    // The ingredient preview resolves at this depth too, so it is stale until the
                    // plan is rebuilt — without this the squares keep the previous depth's colours.
                    UpdatePlan();
                }
            }

            if (rightJustPressed && _amountFieldRect.Contains(Main.MouseScreen.ToPoint()) && !_recursionDragActive)
            {
                _amountFieldMouseDown = true;
                _amountDragActive     = false;
                _amountDragStartX     = Main.MouseScreen.X;
                _amountDragBaseAmount = _craftAmount;
                if (_amountFieldFocused)
                {
                    _amountFieldFocused = false;
                    _amountInput.Deactivate();
                    ApplyAmountFieldText();
                }
            }

            if (_amountFieldMouseDown)
            {
                if (rightHeld)
                {
                    float deltaX = Main.MouseScreen.X - _amountDragStartX;
                    if (!_amountDragActive && Math.Abs(deltaX) > 4f)
                        _amountDragActive = true;

                    if (_amountDragActive)
                    {
                        float absD = Math.Abs(deltaX);
                        int change = (int)(Math.Sign(deltaX) * absD * absD / 150f);
                        SetCraftAmount(_amountDragBaseAmount + change);
                    }
                }
                else
                {
                    _amountFieldMouseDown = false;
                    _amountDragActive     = false;
                }
            }

            // Intercept Terraria's text handling so the amount field gets keyboard input
            // without opening the chat window or triggering other keybindings.
            if (_amountFieldFocused)
            {
                Main.chatRelease = false;
                PlayerInput.WritingText = true;
                Main.CurrentInputTextTakerOverride = this;

                string prev = _amountFieldText;
                string newText = _amountInput.ProcessInput(prev, maxLength: 4, digitsOnly: true);

                if (newText != prev)
                {
                    _amountFieldText = newText;
                    if (int.TryParse(newText, out int val) && val > 0)
                    {
                        _craftAmount = Math.Min(MaxCraftAmountCap, val);
                        UpdatePlan();
                    }
                }

                if (Keyboard.GetState().IsKeyDown(Keys.Escape) || Keyboard.GetState().IsKeyDown(Keys.Enter))
                {
                    _amountFieldFocused = false;
                    _amountInput.Deactivate();
                    ApplyAmountFieldText();
                }
            }

            // Recipe refresh strategy:
            // - Full rebuild (_needsRecipeRefresh): topology changes (stations/disks/conditions).
            //   Expensive — rebuilds the entire recipe list with BFS reachability. Throttled.
            // - Storage content change (StorageVersion bump): when recursive mode is on, the
            //   deferred pass owns all list flags, so request a debounced hard restart of it; when
            //   off, a cheap targeted direct-only update maintains the flags.
            uint tickNow = Main.GameUpdateCount;

            // Fire a pending debounced restart of the recursive pass (storage changed while
            // recursive is on). Skipped when a full refresh is pending — that will restart it anyway.
            if (_recursiveRestartPending && !_needsRecipeRefresh
                && (tickNow - _recursiveRestartTick) >= RecursiveRestartDebounceFrames)
            {
                StartRecursivePass();
            }

            if (_needsRecipeRefresh)
            {
                if ((tickNow - _lastRecipeRefreshTick) >= RecipeRefreshIntervalTicks)
                {
                    _lastRecipeRefreshTick = tickNow;
                    _refresh.MarkStorageReacted(StorageWorldSystem.Instance.StorageVersion);
                    RefreshRecipes();
                    UpdatePlan();
                    _needsRecipeRefresh = false;
                }
                return;
            }

            // Favorites drive the list's partitioning and, with "show uncraftable" off, whether a
            // recipe is listed at all. They can be toggled from the favorites panel, the crafting
            // tree and the encyclopedia, none of which call back into this panel.
            long favoritesVersion = StoragePlayerSystem.Local?.FavoritesVersion ?? 0;
            if (_refresh.NeedsFavoritesRefilter(favoritesVersion))
            {
                _refresh.MarkFavoritesFiltered(favoritesVersion);
                FilterRecipes();
            }

            // Recipe conditions (night, Blood Moon, downed bosses, biome) are live world state, so a
            // terminal left open across nightfall would otherwise keep the flags it was built with.
            if (_refresh.NeedsConditionRecheck(tickNow))
            {
                _refresh.MarkConditionsChecked(tickNow);
                RefreshStationConditionFlags();
            }

            // React to storage content changes.
            long currentVersion = StorageWorldSystem.Instance.StorageVersion;
            if (_refresh.NeedsStorageReact(currentVersion))
            {
                _refresh.MarkStorageReacted(currentVersion);
                if (_recursiveCraft)
                {
                    // Re-check only the recipes this storage change can flip (a consumed item can
                    // only demote, a produced item only promote), instead of restarting a full
                    // re-validation of every recipe.
                    OnStorageChangedRecursive();
                }
                else
                {
                    // Direct-only mode: targeted update — re-checks only recipes whose ingredients changed.
                    UpdateCanCraftFlags();
                }
                // Debounce the heavy plan resolve (authoritative; gates the craft button via
                // _currentPlan.IsFeasible regardless of recursive mode).
                _planDirty = true;
                _planDirtyTick = tickNow;
            }

            // Advance the deferred recursive pass. Skip while a restart is pending — its in-flight
            // results are about to be discarded (they were computed against a now-stale snapshot).
            if (_deferredRecursiveActive && !_recursiveRestartPending)
            {
                // First deferred frame: compute BFS reachability for the current snapshot.
                if (_deferredReachable == null)
                {
                    _deferredReachable = RecipeResolver.ComputeReachableTypesPublic(
                        _deferredAvailable, _availableStations, _availableConditions);
                }

                _deferredRecursiveIndex = RecipeResolver.ApplyRecursiveCraftabilityBatch(
                    _allRecipes, _deferredRecursiveIndex, RecursiveBatchSize,
                    _deferredReachable, _deferredAvailable, _availableStations,
                    _availableConditions, _deferredIngCache, out bool anyFlipped);

                bool complete = _deferredRecursiveIndex < 0;

                // Surface results to the visible list as they are discovered (throttled), and
                // always at completion — so craftable recipes appear during the pass instead of
                // only at the end, and demoted recipes leave the list.
                if (complete || (anyFlipped && (tickNow - _lastRecursiveSyncTick) >= RecursiveSyncThrottleFrames))
                {
                    SyncFilteredRecipesIncremental();
                    RefreshSelectedCanCraftFromList();
                    _lastRecursiveSyncTick = tickNow;
                }

                if (complete)
                {
                    _deferredRecursiveActive = false;
                    _deferredReachable = null;
                    _deferredAvailable = null;
                    _deferredIngCache = null;
                }
            }

            // Debounced heavy plan resolve.
            if (_planDirty && (tickNow - _planDirtyTick) >= PlanDebounceFrames)
            {
                _planDirty = false;
                UpdatePlan();
            }
        }
    }
}
