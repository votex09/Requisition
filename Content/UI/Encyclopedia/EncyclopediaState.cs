using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.GameContent;
using Terraria.GameContent.UI.Elements;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.UI;
using TerraStorage.Content.UI.Elements;
using TerraStorage.Helpers;
using TerraStorage.Systems;

namespace TerraStorage.Content.UI.Encyclopedia
{
    public class EncyclopediaState : UIState
    {
        private const float PanelMinWidth = 250;
        private const float PanelMaxWidth = 1400f;
        private const float PanelMinHeight = 300f;

        // Layout constants
        private const float SearchBarY = 30f;
        private const float SearchBarHeight = 30f;
        private const float FilterBarY = 65f;
        private const float FilterBarHeight = 26f;
        private const float SortBarY = 91f;
        private const float SortBarHeight = 26f;
        private const float ContentY = 122f;
        private const int SlotSize = 38;

        private TSWindowElement _mainPanel;
        private StorageSearchBar _searchBar;
        private UICategoryFilterBar _filterBar;
        private UISortBar _sortBar;
        private UIItemGrid _itemGrid;
        private UINpcGrid _npcGrid;
        private UIScrollbar _gridScrollbar;
        private bool _npcMode;

        // Detail panel
        private UIPanel _detailPanel;
        private UIScrollbar _detailScrollbar;
        private SmoothUIList _detailList;
        private int _selectedItemType;
        private int _selectedNpcType;
        private readonly Stack<(int id, bool isNpc)> _history = new();

        // All items/NPCs catalog
        private List<ConsolidatedItem> _allItems;
        private List<ConsolidatedItem> _filteredItems = new();
        private List<int> _allNpcTypes;

        // Sorting
        private SortMode _currentSort = SortMode.ID;
        private bool _sortAscending = true;

        private float _panelWidth = PanelMinWidth;
        private float _panelHeight;

        // Browse-mode animation (0 = browse pane hidden, 1 = browse pane fully visible)
        private bool  _browseModeTarget = false;
        private float _browseLerp       = 0f;
        private UIElement _browsePane;
        private UIElement _browseToggleBtn;

        // Sort caches
        private static readonly Dictionary<int, string> _nameCache = new();
        private static readonly Dictionary<int, int> _valueCache = new();
        private static readonly Dictionary<int, int> _damageDefenseCache = new();

        public bool IsMouseOverPanel() => _mainPanel?.ContainsPoint(Main.MouseScreen) == true;
        public void DeactivateSearch() => _searchBar?.Unfocus();

        public int GetGridHoveredItemType() => _itemGrid?.GetHoveredItemType() ?? 0;
        public bool IsBrowsePaneVisible() => _browseLerp > 0.001f;

        public override void OnInitialize()
        {
            _panelHeight = Math.Min(Math.Max(500, Main.screenHeight - 200), Main.screenHeight * 0.75f);

            _mainPanel = new TSWindowElement
            {
                StoreKey     = "encyclopedia",
                HasTitleBar  = false,
                Resizable    = true,
                WinMinWidth  = PanelMinWidth,
                WinMaxWidth  = PanelMaxWidth,
                WinMinHeight = PanelMinHeight,
                WinMaxHeight = (float)(Main.screenHeight * 0.85f),
            };
            _mainPanel.Width.Set(_panelWidth, 0f);
            _mainPanel.Height.Set(_panelHeight, 0f);
            _mainPanel.Left.Set((Main.screenWidth - _panelWidth) / 2f, 0f);
            _mainPanel.Top.Set((Main.screenHeight - _panelHeight) / 2f, 0f);
            _mainPanel.GetDragZone = mouse =>
            {
                var d = _mainPanel.GetDimensions();
                var id = _mainPanel.GetInnerDimensions();
                return mouse.X >= d.X && mouse.X <= d.X + d.Width - 50
                    && mouse.Y >= d.Y && mouse.Y <= id.Y + SearchBarY;
            };
            _mainPanel.OnResized += (w, h) =>
            {
                _panelWidth  = w;
                _panelHeight = h;
                RecalculateLayout();
            };
            _mainPanel.LoadSavedBounds();
            _panelWidth  = _mainPanel.Width.Pixels;
            _panelHeight = _mainPanel.Height.Pixels;
            _mainPanel.SetPadding(12);
            Append(_mainPanel);

            // Title
            var title = new UIText(Language.GetText("Mods.TerraStorage.UI.Encyclopedia.Title"), 0.9f);
            title.Left.Set(10, 0f);
            title.Top.Set(2, 0f);
            _mainPanel.Append(title);

            // Close button
            var closeBtn = new TSCloseButton(() => ModContent.GetInstance<EncyclopediaSystem>()?.CloseEncyclopedia());
            closeBtn.Left.Set(-26, 1f);
            closeBtn.Top.Set(-4, 0f);
            _mainPanel.Append(closeBtn);

            // Content area dimensions
            // -24 accounts for _mainPanel's 12px top+bottom padding so content fits without clipping
            float contentHeight    = _panelHeight - ContentY - 24;       // item grid height
            float browseAreaHeight = _panelHeight - FilterBarY - 24;     // browse pane + detail panel height
            float innerWidth       = _panelWidth - 24;
            float gridW            = innerWidth - 46; // 20 left + 4 gap + 16 scrollbar + 6 right

            // Search bar — always visible, never part of the lerping browse pane
            _searchBar = new StorageSearchBar();
            _searchBar.SetPlaceholder("Search...  #tooltip  @mod  !npc");
            _searchBar.Width.Set(-30, 1f);
            _searchBar.Height.Set(SearchBarHeight, 0f);
            _searchBar.Left.Set(10, 0f);
            _searchBar.Top.Set(SearchBarY, 0f);
            _searchBar.OnTextChanged += _ => FilterAndDisplayItems();
            _searchBar.OnFocused     += () => SetBrowseMode(true);
            _mainPanel.Append(_searchBar);

            // Detail panel — inset by toggle strip width + 4px padding
            _detailPanel = new UIPanel();
            _detailPanel.Width.Set(-20, 1f);
            _detailPanel.Height.Set(browseAreaHeight, 0f);
            _detailPanel.Left.Set(20, 0f);
            _detailPanel.Top.Set(FilterBarY, 0f);
            _detailPanel.SetPadding(8);
            _detailPanel.BackgroundColor = new Color(35, 43, 79) * 0.9f;
            _mainPanel.Append(_detailPanel);

            // Browse pane — clipped container that slides in from the left to cover the detail panel.
            // Starts at FilterBarY (below search bar); slides from Left=-innerWidth to Left=0.
            _browsePane = new BrowsePane(_mainPanel);
            _browsePane.Left.Set(-innerWidth, 0f);
            _browsePane.Top.Set(FilterBarY, 0f);
            _browsePane.Width.Set(innerWidth, 0f);
            _browsePane.Height.Set(browseAreaHeight, 0f);

            // Filter bar (Top=0 relative to browse pane, Left=20 to clear toggle strip + 4px gap)
            _filterBar = new UICategoryFilterBar();
            _filterBar.Width.Set(-30, 1f);
            _filterBar.Height.Set(FilterBarHeight, 0f);
            _filterBar.Left.Set(20, 0f);
            _filterBar.Top.Set(0, 0f);
            _filterBar.OnFilterChanged += FilterAndDisplayItems;
            _browsePane.Append(_filterBar);

            // Sort bar (Top relative to browse pane)
            _sortBar = new UISortBar();
            _sortBar.Width.Set(-30, 1f);
            _sortBar.Height.Set(SortBarHeight, 0f);
            _sortBar.Left.Set(20, 0f);
            _sortBar.Top.Set(SortBarY - FilterBarY, 0f);
            _sortBar.OnSortChanged += () =>
            {
                _currentSort = (SortMode)_sortBar.Selected;
                _sortAscending = _sortBar.Ascending;
                FilterAndDisplayItems();
            };
            _browsePane.Append(_sortBar);

            // Item grid (inside browse pane; Top relative to pane)
            _itemGrid = new UIItemGrid();
            _itemGrid.Width.Set(gridW, 0f);
            _itemGrid.Height.Set(contentHeight, 0f);
            _itemGrid.Left.Set(20, 0f);
            _itemGrid.Top.Set(ContentY - FilterBarY, 0f);
            _itemGrid.SetShowFavoriteHint(true);
            _itemGrid.SetFavoriteChecker((type, _) =>
            {
                var recipes = EncyclopediaItemData.GetRecipesFor(type);
                if (recipes.Count == 0) return false;
                return StoragePlayerSystem.Local.IsRecipeFavorited(Main.recipe[recipes[0].RecipeIndex]);
            });
            _browsePane.Append(_itemGrid);

            // Grid scrollbar (inside browse pane)
            _gridScrollbar = new UIScrollbar();
            _gridScrollbar.Height.Set(contentHeight, 0f);
            _gridScrollbar.Left.Set(gridW + 26, 0f);
            _gridScrollbar.Top.Set(ContentY - FilterBarY, 0f);
            _browsePane.Append(_gridScrollbar);
            _itemGrid.SetScrollbar(_gridScrollbar);

            _itemGrid.OnItemClicked += OnItemClicked;
            _itemGrid.OnItemRightClicked += OnItemRightClicked;
            _itemGrid.OnItemMiddleClicked += OnItemMiddleClicked;
            _itemGrid.OnItemAltClicked += OnGridItemAltClicked;

            // NPC grid (same position inside browse pane, swapped in when searching with "!")
            _npcGrid = new UINpcGrid();
            _npcGrid.Width.Set(gridW, 0f);
            _npcGrid.Height.Set(contentHeight, 0f);
            _npcGrid.Left.Set(20, 0f);
            _npcGrid.Top.Set(ContentY - FilterBarY, 0f);
            _npcGrid.SetColumns(Math.Max(1, (int)(gridW / 48)));
            _npcGrid.OnNpcClicked += OnNpcGridClicked;

            _mainPanel.Append(_browsePane);

            // Detail scrollbar
            _detailScrollbar = new UIScrollbar();
            _detailScrollbar.Height.Set(-16, 1f);
            _detailScrollbar.Left.Set(-20, 1f);
            _detailScrollbar.Top.Set(4, 0f);
            _detailPanel.Append(_detailScrollbar);

            // Detail list
            _detailList = new SmoothUIList();
            _detailList.Width.Set(-28, 1f);
            _detailList.Height.Set(-10, 1f);
            _detailList.Left.Set(0, 0f);
            _detailList.Top.Set(0, 0f);
            _detailList.SetScrollbar(_detailScrollbar);
            _detailPanel.Append(_detailList);

            _detailList.Add(new UIText(Language.GetText("Mods.TerraStorage.UI.Encyclopedia.ClickToView"), 0.85f) { TextColor = Color.Gray });

            // Toggle strip — permanent left-edge anchor, always on top, never moves
            _browseToggleBtn = new BrowseToggleButton(
                () => SetBrowseMode(!_browseModeTarget),
                () => _browseModeTarget,
                _detailPanel);
            _browseToggleBtn.Width.Set(16, 0f);
            _browseToggleBtn.Height.Set(browseAreaHeight, 0f);
            _browseToggleBtn.Left.Set(0, 0f);
            _browseToggleBtn.Top.Set(FilterBarY, 0f);
            _mainPanel.Append(_browseToggleBtn);

            RecalculateColumns();
        }

        private void RecalculateColumns()
        {
            float innerWidth = _panelWidth - 24;
            float gridW      = innerWidth - 46;
            int cols = Math.Max(1, (int)(gridW / 48));
            _itemGrid.SetColumns(cols);
            _npcGrid.SetColumns(cols);
        }

        private float GetDetailContentWidth()
        {
            // Available width inside the detail list (panel width minus padding, scrollbar, margins)
            var dims = _detailPanel.GetDimensions();
            return dims.Width - 44; // padding + scrollbar
        }

        private void EnsureItemCatalog()
        {
            if (_allItems != null) return;
            // Pre-built by EncyclopediaItemData.PostSetupRecipes — no stall here
            _allItems = EncyclopediaItemData.AllItems ?? new List<ConsolidatedItem>();
        }

        public void OpenForItem(int itemType)
        {
            EnsureItemCatalog();
            FilterAndDisplayItems();

            if (itemType > 0)
            {
                _selectedItemType = itemType;
                PopulateDetailForItem(itemType);
            }
        }

        public void Open()
        {
            EnsureItemCatalog();
            FilterAndDisplayItems();
        }

        private void OnItemClicked(ConsolidatedItem item)
        {
            if (item == null) return;
            _history.Clear();
            _selectedItemType = item.ItemType;
            _selectedNpcType = 0;
            SetBrowseMode(false);
            PopulateDetailForItem(item.ItemType);
        }

        private void OnItemRightClicked(ConsolidatedItem item)
        {
            if (item == null) return;
            ModContent.GetInstance<CraftingTree.CraftingTreeSystem>()?.OpenTree(item.ItemType);
        }

        private void OnItemMiddleClicked(ConsolidatedItem item)
        {
            if (item == null) return;
            TrySendRecipeToTerminal(item.ItemType);
        }

        private void OnGridItemAltClicked(ConsolidatedItem item)
        {
            if (item == null) return;
            var recipes = EncyclopediaItemData.GetRecipesFor(item.ItemType);
            if (recipes.Count == 0) return;
            StoragePlayerSystem.Local.ToggleRecipeFavorite(Main.recipe[recipes[0].RecipeIndex]);
            Terraria.Audio.SoundEngine.PlaySound(Terraria.ID.SoundID.MenuTick);
        }

        //Sends the first recipe for itemType to the crafting terminal, if one exists.
        private static void TrySendRecipeToTerminal(int itemType)
            => EncyclopediaItemData.TrySendRecipeToTerminal(itemType);

        private void FilterAndDisplayItems()
        {
            string search = _searchBar?.SearchText ?? "";

            // "!" prefix switches to NPC browsing mode
            if (search.StartsWith("!"))
            {
                EnterNpcMode(search.Substring(1).Trim());
                return;
            }

            if (_npcMode) LeaveNpcMode();
            if (_allItems == null) return;

            bool hasSearch = !string.IsNullOrEmpty(search);
            // Parsed once: the query is the same for every item in the sweep below.
            var (searchMode, searchQuery) = ItemSearchHelper.Parse(search);

            _filteredItems.Clear();
            foreach (var ci in _allItems)
            {
                if (hasSearch && !ItemSearchHelper.Matches(ci.ItemType, searchMode, searchQuery))
                    continue;
                if (_filterBar != null && !_filterBar.PassesFilter(ci.ItemType))
                    continue;
                _filteredItems.Add(ci);
            }

            SortItems(_filteredItems);
            _itemGrid?.SetItems(new List<ConsolidatedItem>(_filteredItems));
        }

        private void EnterNpcMode(string npcSearch)
        {
            if (!_npcMode)
            {
                _npcMode = true;
                _browsePane.RemoveChild(_itemGrid);
                _browsePane.Append(_npcGrid);
                _npcGrid.SetScrollbar(_gridScrollbar);
            }

            EnsureNpcCatalog();

            var filtered = new List<int>();
            bool hasSearch = !string.IsNullOrEmpty(npcSearch);
            foreach (int npcType in _allNpcTypes)
            {
                if (hasSearch)
                {
                    string name = Lang.GetNPCNameValue(npcType);
                    if (!name.Contains(npcSearch, StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                filtered.Add(npcType);
            }

            _npcGrid.SetNpcs(filtered);
        }

        private void LeaveNpcMode()
        {
            _npcMode = false;
            _browsePane.RemoveChild(_npcGrid);
            _browsePane.Append(_itemGrid);
            _itemGrid.SetScrollbar(_gridScrollbar);
        }

        private void EnsureNpcCatalog()
        {
            if (_allNpcTypes != null) return;
            _allNpcTypes = new List<int>();

            int maxNpc = NPCLoader.NPCCount;
            for (int npcId = 1; npcId < maxNpc; npcId++)
            {
                string name = Lang.GetNPCNameValue(npcId);
                if (!string.IsNullOrEmpty(name))
                    _allNpcTypes.Add(npcId);
            }
        }

        private void SortItems(List<ConsolidatedItem> items)
        {
            int dir = _sortAscending ? 1 : -1;
            items.Sort((a, b) =>
            {
                int result = _currentSort switch
                {
                    SortMode.ID => a.ItemType.CompareTo(b.ItemType),
                    SortMode.Name => string.Compare(
                        GetCachedName(a.ItemType), GetCachedName(b.ItemType),
                        StringComparison.OrdinalIgnoreCase),
                    SortMode.Value => GetCachedValue(a.ItemType).CompareTo(GetCachedValue(b.ItemType)),
                    SortMode.DamageDefense => GetCachedDamageDefense(a.ItemType).CompareTo(
                        GetCachedDamageDefense(b.ItemType)),
                    SortMode.Rarity => TerminalUIState.GetCachedRarity(a.ItemType).CompareTo(
                        TerminalUIState.GetCachedRarity(b.ItemType)),
                    _ => a.ItemType.CompareTo(b.ItemType)
                };
                return result * dir;
            });
        }

        private static string GetCachedName(int itemType)
        {
            if (!_nameCache.TryGetValue(itemType, out var name))
            {
                var item = new Item();
                item.SetDefaults(itemType);
                name = item.Name;
                _nameCache[itemType] = name;
            }
            return name;
        }

        private static int GetCachedValue(int itemType)
        {
            if (!_valueCache.TryGetValue(itemType, out var value))
            {
                var item = new Item();
                item.SetDefaults(itemType);
                value = item.value;
                _valueCache[itemType] = value;
            }
            return value;
        }

        private static int GetCachedDamageDefense(int itemType)
        {
            if (!_damageDefenseCache.TryGetValue(itemType, out var val))
            {
                var item = new Item();
                item.SetDefaults(itemType);
                val = item.damage > 0 ? item.damage : item.defense;
                _damageDefenseCache[itemType] = val;
            }
            return val;
        }

        // ────────────────────────────────────────────────────────
        //  Detail panel: Item view
        // ────────────────────────────────────────────────────────

        private void PopulateDetailForItem(int itemType)
        {
            _detailList.Clear();
            var cache = ItemSourceCache.Instance;
            float contentW = GetDetailContentWidth();
            float sectionInnerW = contentW - 15; // account for section border + padding
            bool hasAny = false;

            // Back button
            AddBackButton();

            // Header: large item icon + name
            int headerSize = SlotSize + 16;
            var headerRow = new UIEncyclopediaHeaderRow(contentW, headerSize, itemType);
            _detailList.Add(headerRow);

            // ── Crafted From ──
            var recipes = EncyclopediaItemData.GetRecipesFor(itemType);
            if (recipes.Count > 0)
            {
                hasAny = true;
                int stationSize = SlotSize + 8;
                var carousel = new UIRecipeCarousel(sectionInnerW, stationSize, SlotSize, OnDetailItemClicked);
                foreach (var recipe in recipes)
                    carousel.AddRecipe(recipe);

                var section = new UIEncyclopediaSection(
                    $"Crafted From ({recipes.Count})", new Color(100, 200, 100), carousel);
                _detailList.Add(section);
                _detailList.Add(new UISpacer(4));
            }

            // ── Dropped By ──
            var drops = cache?.GetDropSources(itemType);
            if (drops != null && drops.Count > 0)
            {
                hasAny = true;
                var npcRow = new UIEncyclopediaNpcFlowRow(sectionInnerW, SlotSize, OnDetailNpcClicked);
                foreach (var drop in drops)
                {
                    string pct = drop.DropRate >= 1f ? "100%" : $"{drop.DropRate * 100:0.#}%";
                    string stack = drop.StackMin == drop.StackMax
                        ? (drop.StackMin > 1 ? $"x{drop.StackMin}" : "")
                        : $"x{drop.StackMin}-{drop.StackMax}";
                    npcRow.AddNpcSlot(drop.NpcType, pct, stack);
                }

                var section = new UIEncyclopediaSection(
                    $"{Language.GetTextValue("Mods.TerraStorage.UI.Encyclopedia.DroppedBy")} ({drops.Count})", new Color(255, 180, 80), npcRow);
                _detailList.Add(section);
                _detailList.Add(new UISpacer(4));
            }

            // ── Sold By ──
            var shops = cache?.GetShopSources(itemType);
            if (shops != null && shops.Count > 0)
            {
                hasAny = true;
                var npcRow = new UIEncyclopediaNpcFlowRow(sectionInnerW, SlotSize, OnDetailNpcClicked);
                foreach (var shop in shops)
                    npcRow.AddNpcSlot(shop.NpcType, null, null, shop.Price);

                var section = new UIEncyclopediaSection(
                    $"{Language.GetTextValue("Mods.TerraStorage.UI.Encyclopedia.SoldBy")} ({shops.Count})", new Color(100, 200, 255), npcRow);
                _detailList.Add(section);
                _detailList.Add(new UISpacer(4));
            }

            // ── Shimmers Into ──
            int shimmerTo = cache?.GetShimmerResult(itemType) ?? -1;
            if (shimmerTo > 0)
            {
                hasAny = true;
                var row = new UIEncyclopediaFlowRow(sectionInnerW, SlotSize, OnDetailItemClicked);
                row.AddItemSlot(shimmerTo, 1);

                var section = new UIEncyclopediaSection(
                    Language.GetTextValue("Mods.TerraStorage.UI.Encyclopedia.ShimmersInto"), new Color(200, 150, 255), row);
                _detailList.Add(section);
                _detailList.Add(new UISpacer(4));
            }

            // ── Used In ──
            var usages = EncyclopediaItemData.GetUsagesFor(itemType);
            if (usages.Count > 0)
            {
                hasAny = true;
                var row = new UIEncyclopediaFlowRow(sectionInnerW, SlotSize, OnDetailItemClicked);
                foreach (var recipe in usages)
                    row.AddItemSlot(recipe.ResultType, recipe.ResultStack);

                var section = new UIEncyclopediaSection(
                    $"Used In ({usages.Count})", new Color(100, 150, 255), row);
                _detailList.Add(section);
            }

            if (!hasAny)
            {
                _detailList.Add(new UISpacer(6));
                _detailList.Add(new UIText(Language.GetTextValue("Mods.TerraStorage.UI.Encyclopedia.NoSourceData"), 0.8f) { TextColor = Color.Gray });
            }
        }

        // ────────────────────────────────────────────────────────
        //  Detail panel: NPC view (shows everything an NPC drops)
        // ────────────────────────────────────────────────────────

        private void PopulateDetailForNpc(int npcType)
        {
            _detailList.Clear();
            float contentW = GetDetailContentWidth();
            float sectionInnerW = contentW - 15;

            // Back button
            AddBackButton();

            // NPC header
            var headerRow = new UIEncyclopediaNpcFlowRow(contentW, SlotSize + 8, null);
            headerRow.AddNpcSlot(npcType, null);
            _detailList.Add(headerRow);

            string npcName = Lang.GetNPCNameValue(npcType);
            var nameText = new UIText(npcName, 0.9f);
            nameText.TextColor = new Color(255, 200, 100);
            _detailList.Add(nameText);

            bool hasAny = false;

            // Build drop list by querying Main.ItemDropsDB directly
            var npcDrops = new List<(int itemType, float rate, int stackMin, int stackMax)>();
            try
            {
                var rules = Main.ItemDropsDB.GetRulesForNPCID(npcType, false);
                if (rules != null)
                {
                    foreach (var rule in rules)
                        ExtractNpcDrops(rule, npcDrops, 1f);
                }
            }
            catch { }

            if (npcDrops.Count > 0)
            {
                hasAny = true;
                var row = new UIEncyclopediaFlowRow(sectionInnerW, SlotSize, OnDetailItemClicked);
                foreach (var (itemType, rate, stackMin, stackMax) in npcDrops)
                    row.AddItemSlot(itemType, stackMax > 1 ? stackMax : 1);

                var section = new UIEncyclopediaSection(
                    $"Drops ({npcDrops.Count})", new Color(255, 180, 80), row);
                _detailList.Add(section);
                _detailList.Add(new UISpacer(4));
            }

            // Shops
            var shopItems = new List<int>();
            foreach (var shop in NPCShopDatabase.AllShops)
            {
                if (shop is not NPCShop npcShop || npcShop.NpcType != npcType) continue;
                foreach (var entry in npcShop.ActiveEntries)
                {
                    if (entry.Item.type > ItemID.None && !shopItems.Contains(entry.Item.type))
                        shopItems.Add(entry.Item.type);
                }
            }

            if (shopItems.Count > 0)
            {
                hasAny = true;
                var row = new UIEncyclopediaFlowRow(sectionInnerW, SlotSize, OnDetailItemClicked);
                foreach (int itemType in shopItems)
                    row.AddItemSlot(itemType, 1);

                var section = new UIEncyclopediaSection(
                    $"Sells ({shopItems.Count})", new Color(100, 200, 255), row);
                _detailList.Add(section);
            }

            if (!hasAny)
            {
                _detailList.Add(new UISpacer(6));
                _detailList.Add(new UIText(Language.GetTextValue("Mods.TerraStorage.UI.Encyclopedia.NoDropOrShop"), 0.8f) { TextColor = Color.Gray });
            }
        }

        private static void ExtractNpcDrops(Terraria.GameContent.ItemDropRules.IItemDropRule rule,
            List<(int itemType, float rate, int stackMin, int stackMax)> results, float parentChance)
        {
            if (rule is Terraria.GameContent.ItemDropRules.CommonDrop cd)
            {
                float rate = parentChance * cd.chanceNumerator / (float)cd.chanceDenominator;
                if (!results.Exists(r => r.itemType == cd.itemId))
                    results.Add((cd.itemId, rate, cd.amountDroppedMinimum, cd.amountDroppedMaximum));
            }
            else if (rule is Terraria.GameContent.ItemDropRules.OneFromOptionsDropRule ofr)
            {
                float rate = parentChance * ofr.chanceNumerator / (float)ofr.chanceDenominator / ofr.dropIds.Length;
                foreach (int id in ofr.dropIds)
                    if (!results.Exists(r => r.itemType == id))
                        results.Add((id, rate, 1, 1));
            }
            else if (rule is Terraria.GameContent.ItemDropRules.DropBasedOnExpertMode ebm)
            {
                ExtractNpcDrops(ebm.ruleForNormalMode, results, parentChance);
            }
            else if (rule is Terraria.GameContent.ItemDropRules.DropBasedOnMasterMode mbm)
            {
                ExtractNpcDrops(mbm.ruleForDefault, results, parentChance);
            }

            if (rule.ChainedRules != null)
                foreach (var chain in rule.ChainedRules)
                    ExtractNpcDrops(chain.RuleToChain, results, parentChance);
        }

        // ────────────────────────────────────────────────────────
        //  Callbacks
        // ────────────────────────────────────────────────────────

        private void OnNpcGridClicked(int npcType)
        {
            if (npcType != 0)
            {
                _history.Clear();
                _selectedNpcType = npcType;
                _selectedItemType = 0;
                PopulateDetailForNpc(npcType);
            }
        }

        private void OnDetailItemClicked(int itemType)
        {
            PushHistory();
            _selectedItemType = itemType;
            _selectedNpcType = 0;
            PopulateDetailForItem(itemType);
        }

        private void OnDetailNpcClicked(int npcType)
        {
            PushHistory();
            _selectedNpcType = npcType;
            _selectedItemType = 0;
            PopulateDetailForNpc(npcType);
        }

        private void PushHistory()
        {
            if (_selectedItemType > 0)
                _history.Push((_selectedItemType, false));
            else if (_selectedNpcType != 0)
                _history.Push((_selectedNpcType, true));
        }

        private void NavigateBack()
        {
            if (_history.Count == 0) return;
            var (id, isNpc) = _history.Pop();
            if (isNpc)
            {
                _selectedNpcType = id;
                _selectedItemType = 0;
                PopulateDetailForNpc(id);
            }
            else
            {
                _selectedItemType = id;
                _selectedNpcType = 0;
                PopulateDetailForItem(id);
            }
        }

        private void AddBackButton()
        {
            if (_history.Count == 0) return;
            var btn = new UITextPanel<string>(Language.GetTextValue("Mods.TerraStorage.UI.Encyclopedia.Back"), 0.7f);
            btn.Width.Set(70, 0f);
            btn.Height.Set(22, 0f);
            btn.OnLeftClick += (_, _) => NavigateBack();
            _detailList.Add(btn);
        }


        // ────────────────────────────────────────────────────────
        //  Draw / Update / Resize / Drag
        // ────────────────────────────────────────────────────────

        public override void Draw(SpriteBatch spriteBatch)
        {
            base.Draw(spriteBatch);
        }

        public override void Update(GameTime gameTime)
        {
            base.Update(gameTime);

            // Claim the frame's click whatever the button. Without this the Encyclopedia only ever
            // stood down for windows above it and never stopped the ones below, so a click in an
            // overlap was acted on by both -- over the Terminal's grid that withdrew an item.
            UIClickBlocker.ClaimIfPressed(IsMouseOverPanel(),
                Main.mouseLeft, Main.mouseRight, Main.mouseMiddle);

            if (_mainPanel.ContainsPoint(Main.MouseScreen))
            {
                Main.LocalPlayer.mouseInterface = true;
                PlayerInput.LockVanillaMouseScroll("TerraStorage/Encyclopedia");
            }

            if (Main.keyState.IsKeyDown(Keys.Escape) && Main.oldKeyState.IsKeyUp(Keys.Escape))
            {
                ModContent.GetInstance<EncyclopediaSystem>()?.CloseEncyclopedia();
                return;
            }

            // Browse-mode lerp
            float target = _browseModeTarget ? 1f : 0f;
            float prev   = _browseLerp;
            _browseLerp += (target - _browseLerp) * 0.18f;
            if (Math.Abs(_browseLerp - target) < 0.005f)
                _browseLerp = target;
            if (Math.Abs(_browseLerp - prev) > 0.001f)
                RecalculateLayout();
        }

        private void SetBrowseMode(bool browse)
        {
            _browseModeTarget = browse;
        }

        private void RecalculateLayout()
        {
            float contentHeight    = _panelHeight - ContentY - 24;
            float browseAreaHeight = _panelHeight - FilterBarY - 24;
            float innerWidth       = _panelWidth - 24;
            float gridW            = innerWidth - 46;

            // Browse pane slides in from the left (lerp=0: fully hidden, lerp=1: fully visible)
            float browsePaneLeft = -(innerWidth * (1f - _browseLerp));
            _browsePane.Left.Set(browsePaneLeft, 0f);
            _browsePane.Width.Set(innerWidth, 0f);
            _browsePane.Height.Set(browseAreaHeight, 0f);

            // Elements inside browse pane (sizes only; positions are fixed relative to pane)
            _itemGrid.Width.Set(gridW, 0f);
            _itemGrid.Height.Set(contentHeight, 0f);
            _npcGrid.Width.Set(gridW, 0f);
            _npcGrid.Height.Set(contentHeight, 0f);
            _gridScrollbar.Height.Set(contentHeight, 0f);
            _gridScrollbar.Left.Set(gridW + 26, 0f);

            // Detail panel — inset by toggle strip width + 4px padding
            _detailPanel.Width.Set(-20, 1f);
            _detailPanel.Height.Set(browseAreaHeight, 0f);
            _detailPanel.Left.Set(20, 0f);

            _detailScrollbar.Height.Set(-16, 1f);
            _detailList.Height.Set(-10, 1f);

            // Toggle strip — permanent anchor, height tracks panel resize
            _browseToggleBtn.Height.Set(browseAreaHeight, 0f);

            RecalculateColumns();
        }
    }

    // ────────────────────────────────────────────────────────────
    //  Flow row: wrapping horizontal layout of item icon slots
    // ────────────────────────────────────────────────────────────

    internal class UIEncyclopediaFlowRow : UIElement
    {
        private readonly float _contentWidth;
        private readonly int _slotSize;
        private readonly Action<int> _onItemClicked;
        private readonly List<SlotEntry> _entries = new();
        private const int Padding = 3;

        private struct SlotEntry
        {
            public int ItemType;
            public int Stack;
            public string Label; // for text-only fallback
        }

        public UIEncyclopediaFlowRow(float contentWidth, int slotSize, Action<int> onItemClicked)
        {
            _contentWidth = contentWidth;
            _slotSize = slotSize;
            _onItemClicked = onItemClicked;
            Width.Set(0, 1f);
        }

        public void AddItemSlot(int itemType, int stack)
        {
            _entries.Add(new SlotEntry { ItemType = itemType, Stack = stack });
            RecalcHeight();
        }

        public void AddLabel(string text)
        {
            _entries.Add(new SlotEntry { ItemType = 0, Label = text });
            RecalcHeight();
        }

        private string _suffix;
        public void SetSuffix(string suffix) => _suffix = suffix;

        private void RecalcHeight()
        {
            int perRow = Math.Max(1, (int)(_contentWidth / (_slotSize + Padding)));
            int rows = (_entries.Count + perRow - 1) / perRow;
            Height.Set(rows * (_slotSize + Padding) + Padding, 0f);
        }

        protected override void DrawSelf(SpriteBatch spriteBatch)
        {
            var dims = GetDimensions();
            int perRow = Math.Max(1, (int)(dims.Width / (_slotSize + Padding)));

            for (int i = 0; i < _entries.Count; i++)
            {
                int col = i % perRow;
                int row = i / perRow;
                float x = dims.X + col * (_slotSize + Padding);
                float y = dims.Y + row * (_slotSize + Padding);
                var cellRect = new Rectangle((int)x, (int)y, _slotSize, _slotSize);

                var entry = _entries[i];

                if (entry.ItemType > 0)
                {
                    // Cell background
                    Utils.DrawInvBG(spriteBatch, cellRect, new Color(63, 82, 151) * 0.4f);

                    UIDrawHelpers.DrawItemInCell(spriteBatch, entry.ItemType, entry.Stack, cellRect);

                    // Hover: vanilla tooltip
                    if (cellRect.Contains(Main.MouseScreen.ToPoint()))
                    {
                        var hoverItem = new Item();
                        hoverItem.SetDefaults(entry.ItemType);
                        hoverItem.stack = entry.Stack;
                        Main.HoverItem = hoverItem;
                        Main.hoverItemName = hoverItem.Name;
                    }
                }
                else if (!string.IsNullOrEmpty(entry.Label))
                {
                    Utils.DrawBorderString(spriteBatch, entry.Label,
                        new Vector2(x + 2, y + (_slotSize - 16) / 2f),
                        Color.Gray, 0.7f);
                }
            }

            // Suffix text after last slot
            if (!string.IsNullOrEmpty(_suffix) && _entries.Count > 0)
            {
                int lastCol = (_entries.Count - 1) % Math.Max(1, (int)(dims.Width / (_slotSize + Padding)));
                int lastRow = (_entries.Count - 1) / Math.Max(1, (int)(dims.Width / (_slotSize + Padding)));
                float sx = dims.X + (lastCol + 1) * (_slotSize + Padding) + 4;
                float sy = dims.Y + lastRow * (_slotSize + Padding) + (_slotSize - 16) / 2f;
                Utils.DrawBorderString(spriteBatch, _suffix, new Vector2(sx, sy), Color.LightGray, 0.7f);
            }
        }

        public override void LeftClick(UIMouseEvent evt)
        {
            base.LeftClick(evt);
            if (UIClickBlocker.IsConsumed) return;
            int type = GetEntryAtMouse(evt.MousePosition);
            if (type <= 0) return;
            bool alt = Keyboard.GetState().IsKeyDown(Keys.LeftAlt)
                    || Keyboard.GetState().IsKeyDown(Keys.RightAlt);
            if (alt)
            {
                var recipes = EncyclopediaItemData.GetRecipesFor(type);
                if (recipes.Count > 0)
                {
                    StoragePlayerSystem.Local.ToggleRecipeFavorite(Main.recipe[recipes[0].RecipeIndex]);
                    Terraria.Audio.SoundEngine.PlaySound(Terraria.ID.SoundID.MenuTick);
                }
            }
            else
            {
                _onItemClicked?.Invoke(type);
            }
        }

        public override void RightClick(UIMouseEvent evt)
        {
            base.RightClick(evt);
            if (UIClickBlocker.IsConsumed) return;
            int type = GetEntryAtMouse(evt.MousePosition);
            if (type > 0)
                ModContent.GetInstance<CraftingTree.CraftingTreeSystem>()?.OpenTree(type);
        }

        public override void MiddleClick(UIMouseEvent evt)
        {
            base.MiddleClick(evt);
            if (UIClickBlocker.IsConsumed) return;
            int type = GetEntryAtMouse(evt.MousePosition);
            if (type <= 0) return;
            EncyclopediaItemData.TrySendRecipeToTerminal(type);
        }

        private int GetEntryAtMouse(Vector2 mouse)
        {
            var dims = GetDimensions();
            int perRow = Math.Max(1, (int)(dims.Width / (_slotSize + Padding)));
            for (int i = 0; i < _entries.Count; i++)
            {
                int col = i % perRow;
                int row = i / perRow;
                float x = dims.X + col * (_slotSize + Padding);
                float y = dims.Y + row * (_slotSize + Padding);
                if (new Rectangle((int)x, (int)y, _slotSize, _slotSize).Contains(mouse.ToPoint()))
                    return _entries[i].ItemType;
            }
            return 0;
        }
    }

    // ────────────────────────────────────────────────────────────
    //  Flow row: wrapping horizontal layout of NPC icon slots
    // ────────────────────────────────────────────────────────────

    // ────────────────────────────────────────────────────────────
    //  Header row: large item icon + item name text
    // ────────────────────────────────────────────────────────────

    internal class UIEncyclopediaHeaderRow : UIElement
    {
        private readonly int _itemType;
        private readonly int _iconSize;
        private readonly string _itemName;
        private readonly Item _cachedItem;

        public UIEncyclopediaHeaderRow(float contentWidth, int iconSize, int itemType)
        {
            _itemType = itemType;
            _iconSize = iconSize;
            _itemName = Lang.GetItemNameValue(itemType);
            _cachedItem = new Item();
            _cachedItem.SetDefaults(itemType);
            Width.Set(0, 1f);
            Height.Set(iconSize + 4, 0f);
        }

        protected override void DrawSelf(SpriteBatch spriteBatch)
        {
            var dims = GetDimensions();
            var cellRect = new Rectangle((int)dims.X, (int)dims.Y, _iconSize, _iconSize);

            // Background
            Utils.DrawInvBG(spriteBatch, cellRect, new Color(63, 82, 151) * 0.4f);

            UIDrawHelpers.DrawItemInCell(spriteBatch, _itemType, 1, cellRect);

            // Item name next to icon
            Utils.DrawBorderString(spriteBatch, _itemName,
                new Vector2(dims.X + _iconSize + 8, dims.Y + (_iconSize - 20) / 2f),
                Color.White, 0.9f);

            // Hover: vanilla tooltip
            if (cellRect.Contains(Main.MouseScreen.ToPoint()))
            {
                Main.HoverItem = _cachedItem;
                Main.hoverItemName = _itemName;
            }
        }

        public override void LeftClick(UIMouseEvent evt)
        {
            base.LeftClick(evt);
            if (UIClickBlocker.IsConsumed) return;
            var dims = GetDimensions();
            var cellRect = new Rectangle((int)dims.X, (int)dims.Y, _iconSize, _iconSize);
            if (!cellRect.Contains(evt.MousePosition.ToPoint())) return;
            bool alt = Keyboard.GetState().IsKeyDown(Keys.LeftAlt)
                    || Keyboard.GetState().IsKeyDown(Keys.RightAlt);
            if (!alt) return;
            var recipes = EncyclopediaItemData.GetRecipesFor(_itemType);
            if (recipes.Count == 0) return;
            StoragePlayerSystem.Local.ToggleRecipeFavorite(Main.recipe[recipes[0].RecipeIndex]);
            Terraria.Audio.SoundEngine.PlaySound(Terraria.ID.SoundID.MenuTick);
        }

        public override void MiddleClick(UIMouseEvent evt)
        {
            base.MiddleClick(evt);
            if (UIClickBlocker.IsConsumed) return;
            var dims = GetDimensions();
            var cellRect = new Rectangle((int)dims.X, (int)dims.Y, _iconSize, _iconSize);
            if (!cellRect.Contains(evt.MousePosition.ToPoint())) return;
            EncyclopediaItemData.TrySendRecipeToTerminal(_itemType);
        }
    }

    // ────────────────────────────────────────────────────────────
    //  NPC flow row: % on top, stack amount on bottom
    // ────────────────────────────────────────────────────────────

    internal class UIEncyclopediaNpcFlowRow : UIElement
    {
        private readonly float _contentWidth;
        private readonly int _slotSize;
        private readonly Action<int> _onNpcClicked;
        private readonly List<NpcSlotEntry> _entries = new();
        private const int Padding = 3;

        private struct NpcSlotEntry
        {
            public int NpcType;
            public string TopLabel;
            public string BottomLabel;
            public int Price; // in copper coins, 0 = no price
        }

        public UIEncyclopediaNpcFlowRow(float contentWidth, int slotSize, Action<int> onNpcClicked)
        {
            _contentWidth = contentWidth;
            _slotSize = slotSize;
            _onNpcClicked = onNpcClicked;
            Width.Set(0, 1f);
        }

        public void AddNpcSlot(int npcType, string topLabel, string bottomLabel = null, int price = 0)
        {
            _entries.Add(new NpcSlotEntry
            {
                NpcType = npcType,
                TopLabel = topLabel,
                BottomLabel = bottomLabel,
                Price = price
            });
            RecalcHeight();
        }

        private void RecalcHeight()
        {
            int perRow = Math.Max(1, (int)(_contentWidth / (_slotSize + Padding)));
            int rows = (_entries.Count + perRow - 1) / perRow;
            Height.Set(rows * (_slotSize + Padding) + Padding, 0f);
        }

        protected override void DrawSelf(SpriteBatch spriteBatch)
        {
            var dims = GetDimensions();
            int perRow = Math.Max(1, (int)(dims.Width / (_slotSize + Padding)));

            NpcSlotEntry? hoveredEntry = null;

            for (int i = 0; i < _entries.Count; i++)
            {
                int col = i % perRow;
                int row = i / perRow;
                float x = dims.X + col * (_slotSize + Padding);
                float y = dims.Y + row * (_slotSize + Padding);
                var cellRect = new Rectangle((int)x, (int)y, _slotSize, _slotSize);

                var entry = _entries[i];

                // Cell background
                Utils.DrawInvBG(spriteBatch, cellRect, new Color(63, 82, 151) * 0.4f);

                // NPC icon
                UIDrawHelpers.DrawNpcInSlot(spriteBatch, entry.NpcType, cellRect);

                // Top label (drop rate %)
                if (!string.IsNullOrEmpty(entry.TopLabel))
                    Utils.DrawBorderString(spriteBatch, entry.TopLabel,
                        new Vector2(cellRect.X + cellRect.Width / 2f, cellRect.Y + 1),
                        new Color(255, 255, 100), 0.45f, 0.5f, 0f);

                // Bottom text label (stack amount for drops)
                if (!string.IsNullOrEmpty(entry.BottomLabel))
                    Utils.DrawBorderString(spriteBatch, entry.BottomLabel,
                        new Vector2(cellRect.X + cellRect.Width / 2f, cellRect.Bottom - 2),
                        Color.White, 0.45f, 0.5f, 1f);

                if (cellRect.Contains(Main.MouseScreen.ToPoint()))
                    hoveredEntry = entry;
            }

            // Custom tooltip — suppresses vanilla tooltip, allows colored price text
            if (hoveredEntry.HasValue)
            {
                Main.hoverItemName = "";
                Main.HoverItem = new Item();
                DrawNpcTooltip(spriteBatch, hoveredEntry.Value);
            }
        }

        private static void DrawNpcTooltip(SpriteBatch sb, NpcSlotEntry entry)
        {
            string npcName = Lang.GetNPCNameValue(entry.NpcType);
            bool hasPrice = entry.Price > 0;

            var font = FontAssets.MouseText.Value;
            const float scale = 0.75f;
            float lineH = font.MeasureString("A").Y * scale + 2;

            // Build price segments
            var segments = new List<(string text, Color color)>();
            if (hasPrice)
            {
                int plat   = entry.Price / 1000000;
                int gold   = (entry.Price % 1000000) / 10000;
                int silver = (entry.Price % 10000) / 100;
                int copper = entry.Price % 100;
                if (plat   > 0) segments.Add(($"{plat}p ",   new Color(220, 220, 198)));
                if (gold   > 0) segments.Add(($"{gold}g ",   new Color(224, 201, 92)));
                if (silver > 0) segments.Add(($"{silver}s ", new Color(181, 192, 193)));
                if (copper > 0) segments.Add(($"{copper}c",  new Color(246, 138, 96)));
            }

            // Measure
            string buyLabel = Language.GetTextValue("Mods.TerraStorage.UI.Encyclopedia.BuyLabel");
            float nameW     = font.MeasureString(npcName).X * scale;
            float buyLabelW = hasPrice ? font.MeasureString(buyLabel).X * scale : 0f;
            float priceW    = 0f;
            foreach (var (text, _) in segments)
                priceW += font.MeasureString(text).X * scale;

            float tooltipW = Math.Max(nameW, buyLabelW + priceW) + 16f;
            float tooltipH = lineH * (hasPrice ? 2 : 1) + 12f;

            float tx = Main.MouseScreen.X + 14;
            float ty = Main.MouseScreen.Y + 14;
            if (tx + tooltipW > Main.screenWidth)  tx = Main.MouseScreen.X - tooltipW - 4;
            if (ty + tooltipH > Main.screenHeight) ty = Main.MouseScreen.Y - tooltipH - 4;

            Utils.DrawInvBG(sb,
                new Rectangle((int)tx - 6, (int)ty - 4, (int)tooltipW, (int)tooltipH),
                new Color(23, 25, 81, 220));

            // NPC name
            Utils.DrawBorderString(sb, npcName, new Vector2(tx, ty), Color.White, scale);

            // Price line
            if (hasPrice)
            {
                ty += lineH;
                Utils.DrawBorderString(sb, buyLabel, new Vector2(tx, ty), new Color(180, 180, 180), scale);
                float px = tx + buyLabelW;
                foreach (var (text, color) in segments)
                {
                    var sz = Utils.DrawBorderString(sb, text, new Vector2(px, ty), color, scale);
                    px += sz.X;
                }
            }
        }

        public override void LeftClick(UIMouseEvent evt)
        {
            base.LeftClick(evt);
            if (UIClickBlocker.IsConsumed) return;
            int npcType = GetEntryAtMouse(evt.MousePosition);
            if (npcType != 0) _onNpcClicked?.Invoke(npcType);
        }

        private int GetEntryAtMouse(Vector2 mouse)
        {
            var dims = GetDimensions();
            int perRow = Math.Max(1, (int)(dims.Width / (_slotSize + Padding)));
            for (int i = 0; i < _entries.Count; i++)
            {
                int col = i % perRow;
                int row = i / perRow;
                float x = dims.X + col * (_slotSize + Padding);
                float y = dims.Y + row * (_slotSize + Padding);
                if (new Rectangle((int)x, (int)y, _slotSize, _slotSize).Contains(mouse.ToPoint()))
                    return _entries[i].NpcType;
            }
            return 0;
        }
    }

    // ────────────────────────────────────────────────────────────
    //  Scrollable NPC grid (mirrors UIItemGrid but for NPCs)
    // ────────────────────────────────────────────────────────────

    internal class UINpcGrid : UIElement
    {
        private List<int> _npcs = new();
        private UIScrollbar _scrollbar;
        private int _columns = 8;
        private int _cellSize = 48;

        public event Action<int> OnNpcClicked;

        public void SetNpcs(List<int> npcs)
        {
            _npcs = npcs ?? new List<int>();
            UpdateScrollbar();
        }

        public void SetColumns(int columns)
        {
            _columns = Math.Max(1, columns);
            UpdateScrollbar();
        }

        public void SetScrollbar(UIScrollbar scrollbar)
        {
            _scrollbar = scrollbar;
        }

        private void UpdateScrollbar()
        {
            if (_scrollbar == null) return;
            int totalRows = (_npcs.Count + _columns - 1) / _columns;
            var dims = GetDimensions();
            int visibleRows = Math.Max(1, (int)(dims.Height / _cellSize));
            _scrollbar.SetView(visibleRows, totalRows);
        }

        public override void ScrollWheel(UIScrollWheelEvent evt)
        {
            base.ScrollWheel(evt);
            if (_scrollbar != null)
            {
                int gridScrollRows = RequisitionClientConfig.GetGridScrollRows();
                _scrollbar.ViewPosition -= evt.ScrollWheelValue / 120f * gridScrollRows;
            }
        }

        public override void LeftClick(UIMouseEvent evt)
        {
            base.LeftClick(evt);
            if (UIClickBlocker.IsConsumed) return;
            int npcType = GetNpcAtMouse(evt.MousePosition);
            if (npcType != 0) OnNpcClicked?.Invoke(npcType);
        }

        private int GetNpcAtMouse(Vector2 mousePos)
        {
            var dims = GetDimensions();
            float relX = mousePos.X - dims.X;
            float relY = mousePos.Y - dims.Y;
            int col = (int)(relX / _cellSize);
            int scrollRow = _scrollbar != null ? (int)_scrollbar.ViewPosition : 0;
            int row = (int)(relY / _cellSize) + scrollRow;
            int index = row * _columns + col;
            if (index >= 0 && index < _npcs.Count && col >= 0 && col < _columns)
                return _npcs[index];
            return 0;
        }

        protected override void DrawSelf(SpriteBatch spriteBatch)
        {
            var dims = GetDimensions();
            int visibleRows = Math.Max(1, (int)(dims.Height / _cellSize));
            int scrollRow = _scrollbar != null ? (int)_scrollbar.ViewPosition : 0;

            Utils.DrawInvBG(spriteBatch, dims.ToRectangle(), new Color(23, 33, 69) * 0.8f);

            for (int row = 0; row < visibleRows; row++)
            {
                for (int col = 0; col < _columns; col++)
                {
                    int index = (scrollRow + row) * _columns + col;
                    float x = dims.X + col * _cellSize;
                    float y = dims.Y + row * _cellSize;
                    var cellRect = new Rectangle((int)x, (int)y, _cellSize - 2, _cellSize - 2);

                    Utils.DrawInvBG(spriteBatch, cellRect, new Color(63, 82, 151) * 0.4f);

                    if (index >= 0 && index < _npcs.Count)
                    {
                        int npcType = _npcs[index];
                        UIDrawHelpers.DrawNpcInSlot(spriteBatch, npcType, cellRect);

                        if (cellRect.Contains(Main.MouseScreen.ToPoint()))
                        {
                            string npcName = Lang.GetNPCNameValue(npcType);
                            Main.hoverItemName = npcName;
                            Main.HoverItem = new Item();
                        }
                    }
                }
            }
        }
    }

    // ────────────────────────────────────────────────────────────
    //  Section wrapper: colored left border + title + content
    // ────────────────────────────────────────────────────────────

    internal class UIEncyclopediaSection : UIElement
    {
        private readonly string _title;
        private readonly Color _borderColor;
        private const float TitleHeight = 22f;
        private const float BorderWidth = 3f;
        private const float Pad = 6f;

        public UIEncyclopediaSection(string title, Color borderColor, UIElement content)
        {
            _title = title;
            _borderColor = borderColor;
            Width.Set(0, 1f);

            content.Left.Set(BorderWidth + Pad, 0f);
            content.Top.Set(TitleHeight, 0f);
            Append(content);

            Height.Set(TitleHeight + content.Height.Pixels + Pad * 2, 0f);
        }

        protected override void DrawSelf(SpriteBatch spriteBatch)
        {
            var dims = GetDimensions();

            // Background
            Utils.DrawInvBG(spriteBatch, dims.ToRectangle(), new Color(25, 30, 55) * 0.5f);

            // Left border bar
            UIDrawHelpers.DrawSolidRect(spriteBatch,
                new Rectangle((int)dims.X, (int)dims.Y, (int)BorderWidth, (int)dims.Height),
                _borderColor * 0.7f);

            // Title
            Utils.DrawBorderString(spriteBatch, _title,
                new Vector2(dims.X + BorderWidth + Pad + 2, dims.Y + 3),
                _borderColor, 0.8f);
        }
    }

    // ────────────────────────────────────────────────────────────
    //  Recipe carousel: cycle through recipes with < > buttons
    // ────────────────────────────────────────────────────────────

    internal class UIRecipeCarousel : UIElement
    {
        private readonly float _contentWidth;
        private readonly int _stationSize;
        private readonly int _ingredientSize;
        private readonly Action<int> _onItemClicked;
        private readonly List<RecipeDisplay> _recipes = new();
        private int _currentIndex;
        private const int Padding = 3;
        private const float NavHeight = 22f;

        private struct RecipeDisplay
        {
            public List<int> StationItems;
            public List<(int type, int stack, int? groupId)> Ingredients;
            public int ResultStack;
            public string[] Conditions;
            public int RecipeIndex;
            // Precomputed in AddRecipe; DrawSelf runs every frame and must not build strings.
            public string ConditionsText;
            public string MakesText;
        }

        // Nav label rebuilt only when the shown index or recipe count changes.
        private string _navText;
        private int _navTextIndex = -1;
        private int _navTextCount = -1;

        public UIRecipeCarousel(float contentWidth, int stationSize, int ingredientSize, Action<int> onItemClicked)
        {
            _contentWidth = contentWidth;
            _stationSize = stationSize;
            _ingredientSize = ingredientSize;
            _onItemClicked = onItemClicked;
            Width.Set(contentWidth, 0f);
        }

        public void AddRecipe(EncyclopediaItemData.RecipeInfo recipe)
        {
            var display = new RecipeDisplay
            {
                StationItems = new List<int>(),
                Ingredients = new List<(int, int, int?)>(),
                ResultStack = recipe.ResultStack,
                Conditions = recipe.ConditionDescriptions,
                RecipeIndex = recipe.RecipeIndex,
                ConditionsText = recipe.ConditionDescriptions.Length > 0
                    ? "? " + string.Join(", ", recipe.ConditionDescriptions)
                    : null,
                MakesText = recipe.ResultStack > 1
                    ? Language.GetText("Mods.TerraStorage.UI.Encyclopedia.Makes").Format(recipe.ResultStack)
                    : null,
            };

            foreach (int tileId in recipe.RequiredTiles)
            {
                int itemType = UIDrawHelpers.GetItemForTile(tileId);
                if (itemType > 0) display.StationItems.Add(itemType);
            }

            for (int i = 0; i < recipe.Ingredients.Length; i++)
            {
                var (type, stack) = recipe.Ingredients[i];
                int? gid = recipe.IngredientGroupIds != null && i < recipe.IngredientGroupIds.Length
                    ? recipe.IngredientGroupIds[i]
                    : null;
                display.Ingredients.Add((type, stack, gid));
            }

            _recipes.Add(display);
            RecalcHeight();
        }

        private void RecalcHeight()
        {
            // Use max height across all recipes for stable layout
            float maxH = 0;
            foreach (var recipe in _recipes)
            {
                float h = CalcRecipeHeight(recipe);
                if (h > maxH) maxH = h;
            }
            float navH = _recipes.Count > 1 ? NavHeight : 0;
            Height.Set(navH + maxH + 8, 0f);
        }

        private float CalcRecipeHeight(RecipeDisplay recipe)
        {
            float h = 0;
            if (recipe.StationItems.Count > 0)
                h += _stationSize + Padding;

            int perRow = Math.Max(1, (int)(_contentWidth / (_ingredientSize + Padding)));
            int rows = (recipe.Ingredients.Count + perRow - 1) / perRow;
            h += rows * (_ingredientSize + Padding);

            if (recipe.Conditions.Length > 0)
                h += 18;

            return h;
        }

        protected override void DrawSelf(SpriteBatch spriteBatch)
        {
            if (_recipes.Count == 0) return;
            var dims = GetDimensions();
            float navH = _recipes.Count > 1 ? NavHeight : 0;

            // Nav bar
            if (_recipes.Count > 1)
            {
                if (_navTextIndex != _currentIndex || _navTextCount != _recipes.Count)
                {
                    _navTextIndex = _currentIndex;
                    _navTextCount = _recipes.Count;
                    _navText = $"{_currentIndex + 1} / {_recipes.Count}";
                }
                string navText = _navText;
                float navCenterX = dims.X + dims.Width / 2f;
                Utils.DrawBorderString(spriteBatch, navText,
                    new Vector2(navCenterX, dims.Y + 3), Color.White, 0.75f, 0.5f, 0f);

                // < arrow (highlight on hover)
                var leftRect = new Rectangle((int)dims.X, (int)dims.Y, 30, (int)NavHeight);
                Color leftColor = leftRect.Contains(Main.MouseScreen.ToPoint()) ? Color.White : Color.Gray;
                Utils.DrawBorderString(spriteBatch, "<",
                    new Vector2(dims.X + 8, dims.Y + 3), leftColor, 0.8f);

                // > arrow
                var rightRect = new Rectangle((int)(dims.X + dims.Width - 30), (int)dims.Y, 30, (int)NavHeight);
                Color rightColor = rightRect.Contains(Main.MouseScreen.ToPoint()) ? Color.White : Color.Gray;
                Utils.DrawBorderString(spriteBatch, ">",
                    new Vector2(dims.X + dims.Width - 16, dims.Y + 3), rightColor, 0.8f);
            }

            var recipe = _recipes[_currentIndex];

            // Recipe background
            float contentY = dims.Y + navH;
            float recipeH = CalcRecipeHeight(recipe);
            var bgRect = new Rectangle((int)dims.X - 2, (int)contentY - 1,
                (int)dims.Width + 4, (int)recipeH + 6);
            Utils.DrawInvBG(spriteBatch, bgRect, new Color(40, 50, 80) * 0.5f);

            float y = contentY + 2;

            // Stations (larger icons)
            if (recipe.StationItems.Count > 0)
            {
                float x = dims.X + 2;
                foreach (int type in recipe.StationItems)
                {
                    var cellRect = new Rectangle((int)x, (int)y, _stationSize, _stationSize);
                    Utils.DrawInvBG(spriteBatch, cellRect, new Color(80, 60, 40) * 0.5f);
                    UIDrawHelpers.DrawItemInCell(spriteBatch, type, 1, cellRect);

                    if (cellRect.Contains(Main.MouseScreen.ToPoint()))
                    {
                        var hoverItem = new Item();
                        hoverItem.SetDefaults(type);
                        Main.HoverItem = hoverItem;
                        Main.hoverItemName = hoverItem.Name;
                    }
                    x += _stationSize + Padding;
                }
                y += _stationSize + Padding;
            }

            // Ingredients
            if (recipe.Ingredients.Count > 0)
            {
                int perRow = Math.Max(1, (int)(dims.Width / (_ingredientSize + Padding)));
                for (int i = 0; i < recipe.Ingredients.Count; i++)
                {
                    int col = i % perRow;
                    int row = i / perRow;
                    float ix = dims.X + 2 + col * (_ingredientSize + Padding);
                    float iy = y + row * (_ingredientSize + Padding);
                    var cellRect = new Rectangle((int)ix, (int)iy, _ingredientSize, _ingredientSize);

                    var (type, stack, groupId) = recipe.Ingredients[i];
                    Utils.DrawInvBG(spriteBatch, cellRect, new Color(63, 82, 151) * 0.4f);
                    UIDrawHelpers.DrawItemInCell(spriteBatch, type, stack, cellRect);

                    if (cellRect.Contains(Main.MouseScreen.ToPoint()))
                    {
                        var hoverItem = new Item();
                        hoverItem.SetDefaults(type);
                        hoverItem.stack = stack;
                        Main.HoverItem = hoverItem;

                        int foundGid = -1;
                        if (recipe.RecipeIndex >= 0 && recipe.RecipeIndex < Recipe.numRecipes)
                        {
                            foreach (int gid in Main.recipe[recipe.RecipeIndex].acceptedGroups)
                            {
                                if (RecipeGroup.recipeGroups[gid].ContainsItem(type)) { foundGid = gid; break; }
                            }
                        }
                        if (foundGid >= 0)
                        {
                            hoverItem.SetNameOverride(RecipeResolver.GetGroupName(foundGid));
                            Main.hoverItemName = RecipeResolver.GetGroupItemNames(foundGid);
                        }
                        else
                        {
                            Main.hoverItemName = hoverItem.Name;
                        }
                    }
                }

                int ingredientRows = (recipe.Ingredients.Count + Math.Max(1, (int)(dims.Width / (_ingredientSize + Padding))) - 1)
                    / Math.Max(1, (int)(dims.Width / (_ingredientSize + Padding)));
                y += ingredientRows * (_ingredientSize + Padding);
            }

            // Conditions (inside the recipe area)
            if (recipe.ConditionsText != null)
            {
                Utils.DrawBorderString(spriteBatch,
                    recipe.ConditionsText,
                    new Vector2(dims.X + 4, y + 2),
                    new Color(200, 200, 100), 0.65f);
            }

            // Result stack indicator
            if (recipe.MakesText != null)
            {
                Utils.DrawBorderString(spriteBatch, recipe.MakesText,
                    new Vector2(dims.X + dims.Width - 4, contentY + 4),
                    new Color(200, 200, 100), 0.6f, 1f, 0f);
            }
        }

        public override void LeftClick(UIMouseEvent evt)
        {
            base.LeftClick(evt);
            if (UIClickBlocker.IsConsumed) return;

            var dims = GetDimensions();

            // Check nav buttons
            if (_recipes.Count > 1 && evt.MousePosition.Y < dims.Y + NavHeight)
            {
                if (evt.MousePosition.X < dims.X + 40)
                {
                    _currentIndex = (_currentIndex - 1 + _recipes.Count) % _recipes.Count;
                    return;
                }
                if (evt.MousePosition.X > dims.X + dims.Width - 40)
                {
                    _currentIndex = (_currentIndex + 1) % _recipes.Count;
                    return;
                }
                return;
            }

            // Check item clicks
            int type = GetItemAtMouse(evt.MousePosition);
            if (type > 0) _onItemClicked?.Invoke(type);
        }

        public override void RightClick(UIMouseEvent evt)
        {
            base.RightClick(evt);
            if (UIClickBlocker.IsConsumed) return;
            int type = GetItemAtMouse(evt.MousePosition);
            if (type > 0)
                ModContent.GetInstance<CraftingTree.CraftingTreeSystem>()?.OpenTree(type);
        }

        public override void MiddleClick(UIMouseEvent evt)
        {
            base.MiddleClick(evt);
            if (UIClickBlocker.IsConsumed || _recipes.Count == 0) return;

            // Only respond within the actual recipe background rect, not the full (padded) element height
            var dims = GetDimensions();
            float navH = _recipes.Count > 1 ? NavHeight : 0;
            float contentY = dims.Y + navH;
            float recipeH = CalcRecipeHeight(_recipes[_currentIndex]);
            var contentRect = new Rectangle((int)dims.X - 2, (int)contentY - 1, (int)dims.Width + 4, (int)recipeH + 6);
            if (!contentRect.Contains(evt.MousePosition.ToPoint())) return;

            // If hovering a specific item slot, send that item's recipe; otherwise send this recipe
            int hoveredItem = GetItemAtMouse(evt.MousePosition);
            if (hoveredItem > 0)
            {
                EncyclopediaItemData.TrySendRecipeToTerminal(hoveredItem);
            }
            else
            {
                int idx = _recipes[_currentIndex].RecipeIndex;
                if (idx >= 0 && idx < Recipe.numRecipes)
                    CraftingTree.CraftingTreeState.PendingRecipeSelection = Main.recipe[idx];
            }
        }

        private int GetItemAtMouse(Vector2 mouse)
        {
            if (_recipes.Count == 0) return 0;
            var dims = GetDimensions();
            float navH = _recipes.Count > 1 ? NavHeight : 0;
            float y = dims.Y + navH + 2;
            var recipe = _recipes[_currentIndex];

            // Check stations
            if (recipe.StationItems.Count > 0)
            {
                float x = dims.X + 2;
                foreach (int type in recipe.StationItems)
                {
                    if (new Rectangle((int)x, (int)y, _stationSize, _stationSize).Contains(mouse.ToPoint()))
                        return type;
                    x += _stationSize + Padding;
                }
                y += _stationSize + Padding;
            }

            // Check ingredients
            if (recipe.Ingredients.Count > 0)
            {
                int perRow = Math.Max(1, (int)(dims.Width / (_ingredientSize + Padding)));
                for (int i = 0; i < recipe.Ingredients.Count; i++)
                {
                    int col = i % perRow;
                    int row = i / perRow;
                    float ix = dims.X + 2 + col * (_ingredientSize + Padding);
                    float iy = y + row * (_ingredientSize + Padding);
                    if (new Rectangle((int)ix, (int)iy, _ingredientSize, _ingredientSize).Contains(mouse.ToPoint()))
                        return recipe.Ingredients[i].type;  // 3-tuple: (type, stack, groupId)
                }
            }

            return 0;
        }
    }

    internal class UISpacer : UIElement
    {
        public UISpacer(float height)
        {
            Height.Set(height, 0f);
            Width.Set(0, 1f);
        }
    }

    internal class BrowsePane : UIElement
    {
        private readonly TSWindowElement _panel;
        private static readonly RasterizerState _scissorRaster =
            new RasterizerState { ScissorTestEnable = true };

        public BrowsePane(TSWindowElement panel) => _panel = panel;

        protected override void DrawSelf(SpriteBatch spriteBatch)
        {
            // Solid background so the detail panel doesn't show through
            Utils.DrawInvBG(spriteBatch, GetDimensions().ToRectangle(), new Color(22, 26, 50) * 0.97f);
        }

        public override void Draw(SpriteBatch spriteBatch)
        {
            // Scissor = intersection of this pane's own bounds with the panel's inner area.
            // When the pane is fully off-screen (lerp=0) the intersection is zero → early return,
            // which prevents UIItemGrid from applying its own (negative-X) scissor and bleeding.
            var inner  = _panel.GetInnerDimensions();
            var myDims = GetDimensions();

            float clipL = Math.Max(myDims.X,              inner.X);
            float clipT = Math.Max(myDims.Y,              inner.Y);
            float clipR = Math.Min(myDims.X + myDims.Width,  inner.X + inner.Width);
            float clipB = Math.Min(myDims.Y + myDims.Height, inner.Y + inner.Height);
            if (clipR <= clipL || clipB <= clipT) return;

            int x1 = (int)(clipL * Main.UIScale);
            int y1 = (int)(clipT * Main.UIScale);
            int x2 = (int)(clipR * Main.UIScale);
            int y2 = (int)(clipB * Main.UIScale);

            var scissor = new Rectangle(x1, y1, x2 - x1, y2 - y1);
            var saved   = spriteBatch.GraphicsDevice.ScissorRectangle;

            // DrawSelf with scissor
            spriteBatch.End();
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
                SamplerState.AnisotropicClamp, DepthStencilState.None,
                _scissorRaster, null, Main.UIScaleMatrix);
            spriteBatch.GraphicsDevice.ScissorRectangle = scissor;
            DrawSelf(spriteBatch);

            // Suppress tooltips set by detail panel elements that are covered by this pane.
            // Children will set their own hover state for items within the browse pane.
            var mouse = Main.MouseScreen;
            if (mouse.X >= clipL && mouse.X < clipR && mouse.Y >= clipT && mouse.Y < clipB)
            {
                Main.HoverItem = new Item();
                Main.hoverItemName = string.Empty;
            }

            // Re-apply scissor before each child — children (e.g. UIItemGrid) may do End/Begin
            foreach (var child in Elements)
            {
                spriteBatch.End();
                spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
                    SamplerState.AnisotropicClamp, DepthStencilState.None,
                    _scissorRaster, null, Main.UIScaleMatrix);
                spriteBatch.GraphicsDevice.ScissorRectangle = scissor;
                child.Draw(spriteBatch);
            }

            // Restore normal rasterizer
            spriteBatch.End();
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
                SamplerState.AnisotropicClamp, DepthStencilState.None,
                RasterizerState.CullCounterClockwise, null, Main.UIScaleMatrix);
            spriteBatch.GraphicsDevice.ScissorRectangle = saved;
        }
    }

    internal class BrowseToggleButton : UIElement
    {
        private readonly Action     _onClick;
        private readonly Func<bool> _isBrowse;
        private readonly UIPanel    _detailPanel; // color source — adapts to texture packs
        private bool _hovered;

        public BrowseToggleButton(Action onClick, Func<bool> isBrowse, UIPanel detailPanel)
        {
            _onClick     = onClick;
            _isBrowse    = isBrowse;
            _detailPanel = detailPanel;
        }

        public override void MouseOver(UIMouseEvent evt)
        {
            base.MouseOver(evt);
            _hovered = true;
            Terraria.Audio.SoundEngine.PlaySound(Terraria.ID.SoundID.MenuTick);
        }

        public override void MouseOut(UIMouseEvent evt)  { base.MouseOut(evt);  _hovered = false; }
        public override void LeftClick(UIMouseEvent evt) { base.LeftClick(evt); _onClick?.Invoke(); }

        protected override void DrawSelf(SpriteBatch sb)
        {
            var dims = GetDimensions();
            bool isOpen = _isBrowse?.Invoke() == true;

            // Derive colors from the adjacent detail panel so texture packs stay consistent.
            // DrawInvBG uses the same replaceable texture as the rest of the panel chrome.
            Color panelColor = _detailPanel?.BackgroundColor ?? new Color(35, 43, 79) * 0.9f;
            Color bg = _hovered
                ? Color.Lerp(panelColor, new Color(89, 116, 213), 0.15f)
                : panelColor * 0.88f;

            Utils.DrawInvBG(sb,
                new Rectangle((int)dims.X, (int)dims.Y, (int)dims.Width, (int)dims.Height), bg);

            // Arrow glyph — muted, brightens on hover
            string arrow = isOpen ? "‹" : "›";
            var font = FontAssets.MouseText.Value;
            float scale = 0.8f;
            var size = font.MeasureString(arrow) * scale;
            Utils.DrawBorderString(sb, arrow,
                new Vector2(dims.X + (dims.Width  - size.X) / 2f,
                            dims.Y + (dims.Height - size.Y) / 2f),
                Color.White * (_hovered ? 0.75f : 0.35f), scale);

            if (_hovered)
            {
                Main.LocalPlayer.mouseInterface = true;
                Main.hoverItemName = isOpen
                    ? Language.GetTextValue("Mods.TerraStorage.UI.Encyclopedia.CloseBrowser")
                    : Language.GetTextValue("Mods.TerraStorage.UI.Encyclopedia.OpenBrowser");
            }
        }
    }
}
