using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.GameContent;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.UI;
using TerraStorage.Content.UI;
using TerraStorage.Content.UI.Elements;
using TerraStorage.Helpers;
using TerraStorage.Systems;

namespace TerraStorage.Content.UI.CraftingTree
{
    // UIState for the Crafting Tree window. Displays a pannable, zoomable graph showing
    // all recipes an item is used in, expanding outward recursively. Nodes are colored
    // by item category. Right-click a node to select its recipe in the Terminal.
    public class CraftingTreeState : UIState
    {
        // Window constraints
        private const float PanelMinWidth = 500f;
        private const float PanelMaxWidth = 1600f;
        private const float PanelMinHeight = 350f;
        private const float PanelMaxHeight = 1000f;
        private const float TitleBarHeight = 30f;
        private const float ResizeHandleSize = 16f;
        private const float MinimapSize = 150f;
        private const float MinimapPadding = 8f;
        private const float InfoPanelWidth = 200f;

        // Zoom constraints
        private const float MinZoom = 0.3f;
        private const float MaxZoom = 2.0f;
        private const float ZoomStep = 0.1f;

        // Window state
        private float _panelWidth = 900f;
        private float _panelHeight = 550f;
        private float _panelX;
        private float _panelY;
        private bool _minimized;
        private bool _autoMinimize = true;

        // Button hover tracking (for hover sound)
        private bool _prevBtnHovered;

        // Drag state
        private bool _dragging;
        private Vector2 _dragOffset;
        private bool _prevMouseLeft;
        private bool _prevMouseRight;
        private bool _prevMouseMiddle;

        // Selection
        private CraftingTreeNode _selectedNode;

        // Resize state
        private bool _resizing;
        private Vector2 _resizeStart;
        private float _resizeOrigWidth;
        private float _resizeOrigHeight;

        // Pan/zoom state
        private Vector2 _panOffset;
        private float _zoom = 1.0f;
        private bool _panning;
        private Vector2 _panStart;
        private Vector2 _panOffsetStart;

        // Minimap drag state
        private bool _minimapDragging;
        private Rectangle _minimapRect;
        private float _minimapScale;
        private float _minimapGraphMinX;
        private float _minimapGraphMinY;

        // Animation
        private const float AnimationSpeed = 8f; // lerp speed per second
        private readonly List<CraftingTreeNode> _collapsingNodes = new();

        // Tree data
        private CraftingTreeNode _root;
        private int _rootItemType;
        private readonly List<CraftingTreeNode> _allNodes = new();

        // Reverse ingredient index: itemType → list of recipes that USE it as an ingredient
        private static Dictionary<int, List<Recipe>> _recipesByIngredient;
        private static bool _indexBuilt;

        // Info panel
        private float _infoPanelScroll;
        private float _infoPanelScrollTarget;
        private float _infoPanelContentHeight;
        private float _infoPanelSlide; // 0 = hidden (off-screen left), 1 = fully visible
        private bool _infoPanelVisible;
        private int _infoPanelHoveredItemType; // item type hovered in info panel this frame
        private int _infoPanelHoveredNpcType;  // NPC type hovered in info panel this frame

        // Hover/tooltip
        private CraftingTreeNode _hoveredNode;
        private Item _tooltipItem;

        // Per-open / per-selection display caches: rebuilt on state change, read per frame.
        private string _titleText = "";
        private int _infoRowsForType = -1;
        private readonly List<string> _dropRowText = new();

        // Colors per category
        private static readonly Dictionary<ItemCategory, Color> CategoryColors = new()
        {
            { ItemCategory.MeleeWeapons, new Color(255, 100, 100) },
            { ItemCategory.RangedWeapons, new Color(100, 255, 100) },
            { ItemCategory.MagicWeapons, new Color(100, 150, 255) },
            { ItemCategory.SummonerWeapons, new Color(200, 100, 255) },
            { ItemCategory.ThrowingWeapons, new Color(255, 200, 100) },
            { ItemCategory.OtherWeapons, new Color(255, 150, 150) },
            { ItemCategory.Ammo, new Color(180, 180, 100) },
            { ItemCategory.Tools, new Color(200, 200, 200) },
            { ItemCategory.Armor, new Color(100, 200, 220) },
            { ItemCategory.Accessories, new Color(220, 180, 100) },
            { ItemCategory.Vanity, new Color(255, 150, 200) },
            { ItemCategory.Potions, new Color(100, 255, 200) },
            { ItemCategory.Placables, new Color(180, 140, 100) },
            { ItemCategory.BossSummoners, new Color(255, 80, 80) },
            { ItemCategory.Materials, new Color(170, 170, 170) },
            { ItemCategory.Miscellaneous, new Color(140, 140, 140) },
        };

        // Builds the global reverse ingredient index once per session.
        // Maps each item type to all recipes that require it as an ingredient.
        public static void EnsureIndexBuilt()
        {
            if (_indexBuilt) return;
            _indexBuilt = true;
            _recipesByIngredient = new Dictionary<int, List<Recipe>>();

            for (int i = 0; i < Recipe.numRecipes; i++)
            {
                var recipe = Main.recipe[i];
                if (recipe?.createItem == null || recipe.createItem.type <= ItemID.None)
                    continue;

                foreach (var ing in recipe.requiredItem)
                {
                    if (ing.type <= ItemID.None) continue;

                    if (!_recipesByIngredient.TryGetValue(ing.type, out var list))
                    {
                        list = new List<Recipe>();
                        _recipesByIngredient[ing.type] = list;
                    }
                    list.Add(recipe);

                    // Also index recipe group substitutes
                    foreach (int gid in recipe.acceptedGroups)
                    {
                        var grp = RecipeGroup.recipeGroups[gid];
                        if (!grp.ContainsItem(ing.type)) continue;
                        foreach (int v in grp.ValidItems)
                        {
                            if (v == ing.type) continue;
                            if (!_recipesByIngredient.TryGetValue(v, out var gList))
                            {
                                gList = new List<Recipe>();
                                _recipesByIngredient[v] = gList;
                            }
                            if (!gList.Contains(recipe))
                                gList.Add(recipe);
                        }
                    }
                }
            }
        }

        // Opens the tree for the given item type, building the root node. 
        public void OpenForItem(int itemType)
        {
            EnsureIndexBuilt();

            _rootItemType = itemType;
            _titleText = "Crafting Tree: " + Lang.GetItemNameValue(itemType);
            _root = new CraftingTreeNode(itemType);
            _allNodes.Clear();
            _collapsingNodes.Clear();
            _allNodes.Add(_root);
            _selectedNode = null;
            _zoom = RequisitionClientConfig.Instance?.CraftingTreeDefaultZoom ?? 1.0f;

            // Auto-expand root in both directions
            ExpandNode(_root);
            _root.IsExpanded = true;
            ExpandSourceNode(_root);
            _root.IsSourceExpanded = true;

            // Layout and snap all nodes to final position (no animation on initial open)
            LayoutTree();
            foreach (var n in _allNodes)
            {
                n.AnimatedPosition = n.Position;
                n.AnimationProgress = 1f;
            }
            CenterOnRoot();

            _hoveredNode = null;
            _tooltipItem = null;

            // Load auto-minimize preference from player data
            _autoMinimize = StoragePlayerSystem.Local.CraftingTreeAutoMinimize;
        }

        // Expands a node by finding all recipes that use this item as an ingredient.
        private void ExpandNode(CraftingTreeNode node)
        {
            if (node.ChildrenLoaded || node.IsCycleNode) return;
            node.ChildrenLoaded = true;

            if (!_recipesByIngredient.TryGetValue(node.ItemType, out var recipes))
                return;

            // Track result types we've already added to avoid duplicate children
            var addedResults = new HashSet<int>();

            foreach (var recipe in recipes)
            {
                int resultType = recipe.createItem.type;
                if (addedResults.Contains(resultType)) continue;
                addedResults.Add(resultType);

                var child = new CraftingTreeNode(resultType, recipe);
                child.Parent = node;

                // Cycle detection
                if (node.IsAncestor(resultType) || resultType == _rootItemType)
                {
                    child.IsCycleNode = true;
                }

                // Start animated from parent position
                child.AnimatedPosition = node.AnimatedPosition;
                child.AnimationProgress = 0f;

                node.Children.Add(child);
                _allNodes.Add(child);
            }
        }

        // Collapses a node, starting collapse animations for all descendants.
        private void CollapseNode(CraftingTreeNode node)
        {
            StartCollapseAnimation(node, node.Children);
            node.Children.Clear();
            node.ChildrenLoaded = false;
            node.IsExpanded = false;
        }

        private void RemoveDescendants(CraftingTreeNode node)
        {
            foreach (var child in node.Children)
                RemoveDescendants(child);
            foreach (var child in node.SourceChildren)
                RemoveDescendants(child);
            _allNodes.Remove(node);
        }

        // Moves children (and their descendants) into _collapsingNodes for animated removal.
        // They animate back toward their parent's position and fade out.
        private void StartCollapseAnimation(CraftingTreeNode parent, List<CraftingTreeNode> children)
        {
            foreach (var child in children)
            {
                CollectAllDescendants(child, parent.AnimatedPosition);
            }
        }

        private void CollectAllDescendants(CraftingTreeNode node, Vector2 collapseTarget)
        {
            // Recurse first so descendants also collapse
            foreach (var child in node.Children)
                CollectAllDescendants(child, collapseTarget);
            foreach (var child in node.SourceChildren)
                CollectAllDescendants(child, collapseTarget);

            // Move from _allNodes to _collapsingNodes
            _allNodes.Remove(node);
            node.IsCollapsing = true;
            node.Position = collapseTarget; // target = parent position
            node.AnimationProgress = 0f;
            node.Opacity = 1f;
            _collapsingNodes.Add(node);
        }

        //Lerps all node positions and handles collapsing node removal.
        private void UpdateAnimations(float dt)
        {
            float t = Math.Min(1f, AnimationSpeed * dt);

            // Animate expanding/existing nodes toward layout positions
            foreach (var node in _allNodes)
            {
                if (node.AnimationProgress < 1f)
                {
                    node.AnimatedPosition = Vector2.Lerp(node.AnimatedPosition, node.Position, t);
                    node.AnimationProgress = Math.Min(1f, node.AnimationProgress + AnimationSpeed * dt);

                    // Snap when close enough
                    if (Vector2.DistanceSquared(node.AnimatedPosition, node.Position) < 1f)
                    {
                        node.AnimatedPosition = node.Position;
                        node.AnimationProgress = 1f;
                    }
                }
                else if (node.AnimatedPosition != node.Position)
                {
                    // Layout changed (other nodes shifted) — animate existing nodes too
                    node.AnimatedPosition = Vector2.Lerp(node.AnimatedPosition, node.Position, t);
                    if (Vector2.DistanceSquared(node.AnimatedPosition, node.Position) < 1f)
                        node.AnimatedPosition = node.Position;
                }
            }

            // Animate collapsing nodes
            for (int i = _collapsingNodes.Count - 1; i >= 0; i--)
            {
                var node = _collapsingNodes[i];
                node.AnimatedPosition = Vector2.Lerp(node.AnimatedPosition, node.Position, t);
                node.Opacity = Math.Max(0f, node.Opacity - AnimationSpeed * dt);

                if (node.Opacity <= 0.01f || Vector2.DistanceSquared(node.AnimatedPosition, node.Position) < 1f)
                {
                    _collapsingNodes.RemoveAt(i);
                }
            }

            // Animate info panel slide
            bool shouldShow = _selectedNode != null && HasInfoData(_selectedNode.ItemType);
            _infoPanelVisible = shouldShow;
            float slideTarget = shouldShow ? 1f : 0f;
            _infoPanelSlide = MathHelper.Lerp(_infoPanelSlide, slideTarget, Math.Min(1f, 10f * dt));
            if (Math.Abs(_infoPanelSlide - slideTarget) < 0.01f)
                _infoPanelSlide = slideTarget;
        }

        private bool HasInfoData(int itemType)
        {
            var cache = ItemSourceCache.Instance;
            if (cache == null) return false;
            return cache.GetDropSources(itemType) != null
                || cache.GetShopSources(itemType) != null
                || cache.GetShimmerSources(itemType) != null
                || cache.GetShimmerResult(itemType) > 0;
        }

        // Expands source nodes: finds recipes that CREATE this item and adds their
        // ingredients as child nodes on the left side.
        private void ExpandSourceNode(CraftingTreeNode node)
        {
            if (node.SourceChildrenLoaded || node.IsCycleNode) return;
            node.SourceChildrenLoaded = true;

            var cache = RecipeCacheSystem.Instance;
            var recipes = cache.GetRecipesFor(node.ItemType);
            if (recipes.Count == 0) return;

            // For each recipe that produces this item, add all its ingredients as source children
            // If multiple recipes exist, show all of them (each ingredient set)
            var addedTypes = new HashSet<int>();

            foreach (var recipe in recipes)
            {
                foreach (var ing in recipe.requiredItem)
                {
                    if (ing.type <= ItemID.None) continue;
                    if (addedTypes.Contains(ing.type)) continue;
                    addedTypes.Add(ing.type);

                    var child = new CraftingTreeNode(ing.type, recipe);
                    child.Parent = node;
                    child.IsSourceSide = true;

                    // Check if this ingredient is part of a recipe group
                    foreach (int gid in recipe.acceptedGroups)
                    {
                        var grp = RecipeGroup.recipeGroups[gid];
                        if (grp.ContainsItem(ing.type)) { child.IsGroupIngredient = true; child.GroupId = gid; break; }
                    }

                    // Cycle detection
                    if (node.IsAncestor(ing.type) || ing.type == _rootItemType)
                    {
                        child.IsCycleNode = true;
                    }

                    // Start animated from parent position
                    child.AnimatedPosition = node.AnimatedPosition;
                    child.AnimationProgress = 0f;

                    node.SourceChildren.Add(child);
                    _allNodes.Add(child);
                }
            }
        }

        // Collapses source nodes, starting collapse animations for all left-side descendants.
        private void CollapseSourceNode(CraftingTreeNode node)
        {
            StartCollapseAnimation(node, node.SourceChildren);
            node.SourceChildren.Clear();
            node.SourceChildrenLoaded = false;
            node.IsSourceExpanded = false;
        }

        // Assigns positions to all visible nodes using a bidirectional tree layout.
        // Right side: items this item crafts into (positive X).
        // Left side: ingredients needed to create this item (negative X).
        private void LayoutTree()
        {
            if (_root == null) return;

            // Layout right side (uses) starting from depth 0
            float rightY = 0;
            LayoutRightSubtree(_root, 0, ref rightY);

            // Layout left side (sources) — these go to negative depths
            if (_root.IsSourceExpanded && _root.SourceChildren.Count > 0)
            {
                float leftY = 0;
                float firstChildY = float.MaxValue;
                float lastChildY = float.MinValue;

                foreach (var child in _root.SourceChildren)
                {
                    float childY = LayoutLeftSubtree(child, -1, ref leftY);
                    firstChildY = Math.Min(firstChildY, childY);
                    lastChildY = Math.Max(lastChildY, childY);
                }

                // If the root has both sides, center it vertically between the
                // max vertical extent of both sides
                float rootY = _root.Position.Y;
                float leftCenter = (firstChildY + lastChildY) / 2f;
                // Nudge root toward the average of both sides
                float rightCenter = rootY;
                float combinedCenter = (leftCenter + rightCenter) / 2f;
                float shift = combinedCenter - rootY;
                if (Math.Abs(shift) > 1f)
                    ShiftSubtree(_root, shift);
            }
        }

        private float LayoutRightSubtree(CraftingTreeNode node, int depth, ref float y)
        {
            float x = depth * (CraftingTreeNode.NodeSize.X + CraftingTreeNode.LevelSpacing);

            if (!node.IsExpanded || node.Children.Count == 0)
            {
                node.Position = new Vector2(x, y);
                y += CraftingTreeNode.NodeSize.Y + CraftingTreeNode.SiblingSpacing;
                return node.Position.Y;
            }

            float firstChildY = float.MaxValue;
            float lastChildY = float.MinValue;

            foreach (var child in node.Children)
            {
                float childY = LayoutRightSubtree(child, depth + 1, ref y);
                firstChildY = Math.Min(firstChildY, childY);
                lastChildY = Math.Max(lastChildY, childY);
            }

            float centerY = (firstChildY + lastChildY) / 2f;
            node.Position = new Vector2(x, centerY);
            return centerY;
        }

        private float LayoutLeftSubtree(CraftingTreeNode node, int depth, ref float y)
        {
            // depth is negative for left side — further left = more negative
            float x = depth * (CraftingTreeNode.NodeSize.X + CraftingTreeNode.LevelSpacing);

            if (!node.IsSourceExpanded || node.SourceChildren.Count == 0)
            {
                node.Position = new Vector2(x, y);
                y += CraftingTreeNode.NodeSize.Y + CraftingTreeNode.SiblingSpacing;
                return node.Position.Y;
            }

            float firstChildY = float.MaxValue;
            float lastChildY = float.MinValue;

            foreach (var child in node.SourceChildren)
            {
                float childY = LayoutLeftSubtree(child, depth - 1, ref y);
                firstChildY = Math.Min(firstChildY, childY);
                lastChildY = Math.Max(lastChildY, childY);
            }

            float centerY = (firstChildY + lastChildY) / 2f;
            node.Position = new Vector2(x, centerY);
            return centerY;
        }

        private void ShiftSubtree(CraftingTreeNode node, float dy)
        {
            node.Position = new Vector2(node.Position.X, node.Position.Y + dy);
            foreach (var child in node.Children)
                ShiftSubtree(child, dy);
            // Don't shift source children — they were laid out independently
        }

        private void CenterOnRoot()
        {
            if (_root == null) return;
            // Center the root node in the viewport
            float viewW = _panelWidth - 24; // padding
            float viewH = _panelHeight - TitleBarHeight - 24;
            _panOffset = new Vector2(
                viewW / 2f / _zoom - _root.Position.X - CraftingTreeNode.NodeSize.X / 2f,
                viewH / 2f / _zoom - _root.Position.Y - CraftingTreeNode.NodeSize.Y / 2f
            );
        }

        public void SetPosition(float x, float y)
        {
            _panelX = x;
            _panelY = y;
        }

        public void SetSize(float w, float h)
        {
            _panelWidth = w;
            _panelHeight = h;
        }

        public (float x, float y) GetPosition() => (_panelX, _panelY);
        public (float w, float h) GetSize()     => (_panelWidth, _panelHeight);

        public bool IsMinimized => _minimized;

        // ─── Coordinate Helpers ─────────────────────────────────────────

        //Converts graph-space position to screen-space position.
        private Vector2 GraphToScreen(Vector2 graphPos)
        {
            float contentX = _panelX + 12;
            float contentY = _panelY + TitleBarHeight + 12;
            return new Vector2(
                contentX + (graphPos.X + _panOffset.X) * _zoom,
                contentY + (graphPos.Y + _panOffset.Y) * _zoom
            );
        }

        //Converts screen-space position to graph-space position.
        private Vector2 ScreenToGraph(Vector2 screenPos)
        {
            float contentX = _panelX + 12;
            float contentY = _panelY + TitleBarHeight + 12;
            return new Vector2(
                (screenPos.X - contentX) / _zoom - _panOffset.X,
                (screenPos.Y - contentY) / _zoom - _panOffset.Y
            );
        }

        // Converts a screen-space point on the minimap to graph-space,
        // then adjusts pan offset to center the viewport on that point.
        private void CenterViewOnMinimapPoint(Vector2 screenPos)
        {
            if (_minimapScale <= 0) return;

            // Convert minimap screen position to graph position
            float graphX = (screenPos.X - _minimapRect.X - 4) / _minimapScale + _minimapGraphMinX;
            float graphY = (screenPos.Y - _minimapRect.Y - 4) / _minimapScale + _minimapGraphMinY;

            // Center the viewport on this graph point
            float contentW = _panelWidth - 24;
            float contentH = _panelHeight - TitleBarHeight - 24;
            _panOffset = new Vector2(
                contentW / 2f / _zoom - graphX,
                contentH / 2f / _zoom - graphY
            );
        }

        // ─── Update ─────────────────────────────────────────────────────

        public override void Update(GameTime gameTime)
        {
            base.Update(gameTime);
            if (_root == null) return;

            float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
            UpdateAnimations(dt);

            var mouse = Main.MouseScreen;
            bool justClicked = Main.mouseLeft && !_prevMouseLeft && !UIClickBlocker.IsConsumed;
            bool mouseInPanel = IsMouseInPanel(mouse);

            // Block input passthrough when mouse is over panel or during drag/resize
            if (mouseInPanel || _resizing || _dragging || _panning || _minimapDragging)
            {
                Main.LocalPlayer.mouseInterface = true;
                if (mouseInPanel)
                {
                    PlayerInput.LockVanillaMouseScroll("TerraStorage/CraftingTree");
                    if (Main.mouseLeft || Main.mouseRight || Main.mouseMiddle)
                        UIClickBlocker.Consume();
                }
            }

            // Handle window resize
            if (_resizing)
            {
                UIClickBlocker.MarkGesture();
                if (!UIClickBlocker.RealMouseLeft)
                {
                    _resizing = false;
                    UIPositionStore.SaveWithSize("craftingtree", _panelX, _panelY, _panelWidth, _panelHeight);
                }
                else
                {
                    _panelWidth = Math.Clamp(_resizeOrigWidth + (mouse.X - _resizeStart.X), PanelMinWidth, PanelMaxWidth);
                    float maxH = Math.Min(Main.screenHeight - _panelY - 10, PanelMaxHeight);
                    _panelHeight = Math.Clamp(_resizeOrigHeight + (mouse.Y - _resizeStart.Y), PanelMinHeight, maxH);
                }
                _prevMouseLeft = UIClickBlocker.RealMouseLeft;
                return;
            }

            // Handle window drag
            if (_dragging)
            {
                UIClickBlocker.MarkGesture();
                if (!UIClickBlocker.RealMouseLeft)
                {
                    _dragging = false;
                    UIPositionStore.SaveWithSize("craftingtree", _panelX, _panelY, _panelWidth, _panelHeight);
                }
                else
                {
                    _panelX = mouse.X - _dragOffset.X;
                    _panelY = mouse.Y - _dragOffset.Y;
                }
                _prevMouseLeft = UIClickBlocker.RealMouseLeft;
                return;
            }

            // Handle panning
            if (_panning)
            {
                UIClickBlocker.MarkGesture();
                if (!UIClickBlocker.RealMouseLeft)
                {
                    _panning = false;
                }
                else
                {
                    _panOffset = _panOffsetStart + (mouse - _panStart) / _zoom;
                }
                _prevMouseLeft = UIClickBlocker.RealMouseLeft;
                return;
            }

            // Handle minimap dragging
            if (_minimapDragging)
            {
                UIClickBlocker.MarkGesture();
                if (!UIClickBlocker.RealMouseLeft)
                {
                    _minimapDragging = false;
                }
                else
                {
                    CenterViewOnMinimapPoint(mouse);
                }
                _prevMouseLeft = UIClickBlocker.RealMouseLeft;
                return;
            }

            // Zoom with scroll wheel (only when mouse is in content area)
            if (mouseInPanel && !_minimized)
            {
                int scrollDelta = PlayerInput.ScrollWheelDeltaForUI;
                if (scrollDelta != 0)
                {
                    // Check if mouse is over the info panel — scroll it instead of zooming
                    if (_selectedNode != null && IsInInfoPanel(mouse))
                    {
                        _infoPanelScrollTarget -= scrollDelta > 0 ? 30f : -30f;
                        float panelH = _panelHeight - TitleBarHeight - 12 - 8;
                        float visibleH = panelH - 26f; // subtract header
                        float maxScroll = Math.Max(0, _infoPanelContentHeight - visibleH);
                        _infoPanelScrollTarget = Math.Clamp(_infoPanelScrollTarget, 0, maxScroll);
                    }
                    else
                    {
                        float oldZoom = _zoom;
                        _zoom = Math.Clamp(_zoom + (scrollDelta > 0 ? ZoomStep : -ZoomStep), MinZoom, MaxZoom);

                        // Zoom toward mouse position
                        if (Math.Abs(oldZoom - _zoom) > 0.001f)
                        {
                            Vector2 graphUnderMouse = ScreenToGraph(mouse);
                            float contentX = _panelX + 12;
                            float contentY = _panelY + TitleBarHeight + 12;
                            _panOffset = new Vector2(
                                (mouse.X - contentX) / _zoom - graphUnderMouse.X,
                                (mouse.Y - contentY) / _zoom - graphUnderMouse.Y
                            );
                        }
                    }
                }
            }

            // Update hovered node. _tooltipItem is a per-type cache (SetDefaults runs mod hooks,
            // so once per hovered type, not once per frame); tooltip drawing is gated on
            // _hoveredNode, so the stale cached item is inert while nothing is hovered.
            _hoveredNode = null;
            if (mouseInPanel && !_minimized)
            {
                Vector2 graphMouse = ScreenToGraph(mouse);
                foreach (var node in _allNodes)
                {
                    var nodeRect = new Rectangle(
                        (int)node.AnimatedPosition.X, (int)node.AnimatedPosition.Y,
                        (int)CraftingTreeNode.NodeSize.X, (int)CraftingTreeNode.NodeSize.Y);
                    if (nodeRect.Contains((int)graphMouse.X, (int)graphMouse.Y))
                    {
                        _hoveredNode = node;
                        if (_tooltipItem == null || _tooltipItem.type != node.ItemType)
                        {
                            _tooltipItem = new Item();
                            _tooltipItem.SetDefaults(node.ItemType);
                        }
                        break;
                    }
                }
            }

            bool justRightClicked = Main.mouseRight && !_prevMouseRight;
            bool justMiddleClicked = Main.mouseMiddle && !_prevMouseMiddle;

            // Hover sound for title bar buttons
            bool btnHovered = mouseInPanel && (IsInCloseButton(mouse) || IsInMinimizeButton(mouse) || IsInAutoMinimizeButton(mouse));
            if (btnHovered && !_prevBtnHovered)
                Terraria.Audio.SoundEngine.PlaySound(Terraria.ID.SoundID.MenuTick);
            _prevBtnHovered = btnHovered;

            // Left-click: select node, window controls, or start panning
            if (justClicked && mouseInPanel)
            {
                UIClickBlocker.Consume();

                if (_minimized)
                {
                    _minimized = false;
                    _prevMouseLeft = UIClickBlocker.RealMouseLeft;
                    _prevMouseRight = UIClickBlocker.RealMouseRight;
                    _prevMouseMiddle = UIClickBlocker.RealMouseMiddle;
                    return;
                }

                // Close button
                if (IsInCloseButton(mouse))
                {
                    Terraria.Audio.SoundEngine.PlaySound(Terraria.ID.SoundID.MenuTick);
                    ModContent.GetInstance<CraftingTreeSystem>()?.CloseTree();
                    _prevMouseLeft = UIClickBlocker.RealMouseLeft;
                    _prevMouseRight = UIClickBlocker.RealMouseRight;
                    _prevMouseMiddle = UIClickBlocker.RealMouseMiddle;
                    return;
                }

                // Minimize button
                if (IsInMinimizeButton(mouse))
                {
                    Terraria.Audio.SoundEngine.PlaySound(Terraria.ID.SoundID.MenuTick);
                    _minimized = true;
                    _prevMouseLeft = UIClickBlocker.RealMouseLeft;
                    _prevMouseRight = UIClickBlocker.RealMouseRight;
                    _prevMouseMiddle = UIClickBlocker.RealMouseMiddle;
                    return;
                }

                // Auto-minimize toggle
                if (IsInAutoMinimizeButton(mouse))
                {
                    Terraria.Audio.SoundEngine.PlaySound(Terraria.ID.SoundID.MenuTick);
                    _autoMinimize = !_autoMinimize;
                    StoragePlayerSystem.Local.CraftingTreeAutoMinimize = _autoMinimize;
                    _prevMouseLeft = UIClickBlocker.RealMouseLeft;
                    _prevMouseRight = UIClickBlocker.RealMouseRight;
                    _prevMouseMiddle = UIClickBlocker.RealMouseMiddle;
                    return;
                }

                // Resize handle
                if (IsInResizeHandle(mouse))
                {
                    _resizing = true;
                    UIClickBlocker.MarkGesture();
                    _resizeStart = mouse;
                    _resizeOrigWidth = _panelWidth;
                    _resizeOrigHeight = _panelHeight;
                    _prevMouseLeft = UIClickBlocker.RealMouseLeft;
                    _prevMouseRight = UIClickBlocker.RealMouseRight;
                    _prevMouseMiddle = UIClickBlocker.RealMouseMiddle;
                    return;
                }

                // Title bar drag
                if (IsInTitleBar(mouse))
                {
                    _dragging = true;
                    UIClickBlocker.MarkGesture();
                    _dragOffset = mouse - new Vector2(_panelX, _panelY);
                    _prevMouseLeft = UIClickBlocker.RealMouseLeft;
                    _prevMouseRight = UIClickBlocker.RealMouseRight;
                    _prevMouseMiddle = UIClickBlocker.RealMouseMiddle;
                    return;
                }

                // Left-click node: alt-click favorites, plain click selects
                if (_hoveredNode != null)
                {
                    bool alt = Main.keyState.IsKeyDown(Keys.LeftAlt) || Main.keyState.IsKeyDown(Keys.RightAlt);
                    if (alt && _hoveredNode.Recipe != null)
                    {
                        StoragePlayerSystem.Local.ToggleRecipeFavorite(_hoveredNode.Recipe);
                        Terraria.Audio.SoundEngine.PlaySound(Terraria.ID.SoundID.MenuTick);
                    }
                    else
                    {
                        _selectedNode = _selectedNode == _hoveredNode ? null : _hoveredNode;
                        _infoPanelScroll = 0;
                        _infoPanelScrollTarget = 0;
                    }
                }
                else if (IsInInfoPanel(mouse))
                {
                    // Click on info panel: absorb click (don't pan)
                }
                else if (_minimapRect.Width > 0 && _minimapRect.Contains((int)mouse.X, (int)mouse.Y))
                {
                    // Click on minimap: start minimap drag
                    _minimapDragging = true;
                    UIClickBlocker.MarkGesture();
                    CenterViewOnMinimapPoint(mouse);
                }
                else if (IsInContentArea(mouse))
                {
                    // Click empty space: start panning
                    _panning = true;
                    UIClickBlocker.MarkGesture();
                    _panStart = mouse;
                    _panOffsetStart = _panOffset;
                }
            }

            // Right-click: expand/collapse node
            // Source-side nodes expand their sources (left), right-side nodes expand their uses (right)
            if (justRightClicked && mouseInPanel && !_minimized && _hoveredNode != null && !_hoveredNode.IsCycleNode)
            {
                if (_hoveredNode.IsSourceSide)
                {
                    // Source-side node: expand/collapse sources (go further left)
                    if (_hoveredNode.IsSourceExpanded)
                    {
                        CollapseSourceNode(_hoveredNode);
                    }
                    else
                    {
                        ExpandSourceNode(_hoveredNode);
                        _hoveredNode.IsSourceExpanded = true;
                    }
                }
                else
                {
                    // Right-side node: expand/collapse uses (go further right)
                    if (_hoveredNode.IsExpanded)
                    {
                        CollapseNode(_hoveredNode);
                    }
                    else
                    {
                        ExpandNode(_hoveredNode);
                        _hoveredNode.IsExpanded = true;
                    }
                }
                LayoutTree();
            }

            // Middle-click: send recipe to Terminal
            if (justMiddleClicked && mouseInPanel && !_minimized && _hoveredNode != null && _hoveredNode.Recipe != null)
            {
                var terminalSystem = ModContent.GetInstance<TerminalUISystem>();
                if (terminalSystem?.IsTerminalOpen == true)
                {
                    SelectRecipeInTerminal(_hoveredNode.Recipe);
                    if (_autoMinimize)
                        _minimized = true;
                }
            }

            _prevMouseLeft = UIClickBlocker.RealMouseLeft;
            _prevMouseRight = UIClickBlocker.RealMouseRight;
            _prevMouseMiddle = UIClickBlocker.RealMouseMiddle;
        }

        private void SelectRecipeInTerminal(Recipe recipe)
        {
            // The terminal's crafting panel will need a method to select a recipe externally.
            // For now, we'll use a static pending recipe that the crafting panel checks.
            PendingRecipeSelection = recipe;
        }

        // Set by the Crafting Tree when the user right-clicks a node.
        // The crafting panel checks this each frame and selects the recipe if set.
        public static Recipe PendingRecipeSelection { get; set; }

        // ─── Hit Testing ────────────────────────────────────────────────

        private bool IsMouseInPanel(Vector2 mouse)
        {
            float h = _minimized ? TitleBarHeight : _panelHeight;
            return mouse.X >= _panelX && mouse.X <= _panelX + _panelWidth
                && mouse.Y >= _panelY && mouse.Y <= _panelY + h;
        }

        //Public wrapper for interface layer input blocking.
        public bool IsMouseOverPanel() => IsMouseInPanel(Main.MouseScreen);

        private bool IsInTitleBar(Vector2 mouse)
        {
            return mouse.X >= _panelX && mouse.X <= _panelX + _panelWidth - 96
                && mouse.Y >= _panelY && mouse.Y <= _panelY + TitleBarHeight;
        }

        private bool IsInCloseButton(Vector2 mouse)
        {
            float bx = _panelX + _panelWidth - 30;
            float by = _panelY + 3;
            return mouse.X >= bx && mouse.X <= bx + 24 && mouse.Y >= by && mouse.Y <= by + 24;
        }

        private bool IsInMinimizeButton(Vector2 mouse)
        {
            float bx = _panelX + _panelWidth - 58;
            float by = _panelY + 3;
            return mouse.X >= bx && mouse.X <= bx + 24 && mouse.Y >= by && mouse.Y <= by + 24;
        }

        private bool IsInAutoMinimizeButton(Vector2 mouse)
        {
            float bx = _panelX + _panelWidth - 86;
            float by = _panelY + 3;
            return mouse.X >= bx && mouse.X <= bx + 24 && mouse.Y >= by && mouse.Y <= by + 24;
        }

        private bool IsInResizeHandle(Vector2 mouse)
        {
            if (_minimized) return false;
            return mouse.X > _panelX + _panelWidth - ResizeHandleSize
                && mouse.Y > _panelY + _panelHeight - ResizeHandleSize;
        }

        private bool IsInContentArea(Vector2 mouse)
        {
            if (_minimized) return false;
            return mouse.X >= _panelX + 12 && mouse.X <= _panelX + _panelWidth - 12
                && mouse.Y >= _panelY + TitleBarHeight && mouse.Y <= _panelY + _panelHeight - 12;
        }

        private bool IsInInfoPanel(Vector2 mouse)
        {
            if (_minimized || _infoPanelSlide <= 0.01f) return false;
            float contentX = _panelX + 12;
            float contentY = _panelY + TitleBarHeight + 12;
            float contentH = _panelHeight - TitleBarHeight - 24;
            float panelXTarget = contentX + 4;
            float panelXHidden = contentX - InfoPanelWidth - 4;
            float panelX = MathHelper.Lerp(panelXHidden, panelXTarget, _infoPanelSlide);
            float panelY2 = contentY + 4;
            return mouse.X >= panelX && mouse.X <= panelX + InfoPanelWidth
                && mouse.Y >= panelY2 && mouse.Y <= panelY2 + contentH - 8;
        }

        // ─── Drawing ────────────────────────────────────────────────────

        public void DrawTree(SpriteBatch spriteBatch)
        {
            if (_root == null) return;

            float drawHeight = _minimized ? TitleBarHeight : _panelHeight;

            // Dark underlay for visibility, then vanilla panel on top
            UIDrawHelpers.DrawUnderlay(spriteBatch, _panelX, _panelY, _panelWidth, drawHeight);
            Utils.DrawInvBG(spriteBatch,
                new Rectangle((int)_panelX, (int)_panelY, (int)_panelWidth, (int)drawHeight),
                new Color(63, 82, 151) * 0.7f);

            // Title bar
            Utils.DrawInvBG(spriteBatch,
                new Rectangle((int)_panelX, (int)_panelY, (int)_panelWidth, (int)TitleBarHeight),
                new Color(63, 82, 151) * 0.85f);

            // Title text (built once per OpenForItem — the root type is fixed per open)
            var titleSize = FontAssets.MouseText.Value.MeasureString(_titleText) * 0.6f;
            Utils.DrawBorderString(spriteBatch, _titleText,
                new Vector2(_panelX + 10, _panelY + (TitleBarHeight - titleSize.Y) / 2f), Color.White, 0.6f);

            // Close button [X]
            DrawButton(spriteBatch, _panelX + _panelWidth - 30, _panelY + 3, 24, 24, "X",
                IsInCloseButton(Main.MouseScreen), new Color(200, 60, 60));

            // Minimize button [_]
            DrawButton(spriteBatch, _panelX + _panelWidth - 58, _panelY + 3, 24, 24, _minimized ? "+" : "_",
                IsInMinimizeButton(Main.MouseScreen));

            // Auto-minimize toggle
            float amX = _panelX + _panelWidth - 86;
            float amY = _panelY + 3;
            bool amHov = IsInAutoMinimizeButton(Main.MouseScreen);
            Color amBg = _autoMinimize
                ? (amHov ? new Color(56, 136, 56) * 0.95f : new Color(46, 106, 46) * 0.8f)
                : (amHov ? new Color(136, 56, 56) * 0.95f : new Color(106, 46, 46) * 0.8f);
            Utils.DrawInvBG(spriteBatch, new Rectangle((int)amX, (int)amY, 24, 24), amBg);
            var amTextSize = FontAssets.MouseText.Value.MeasureString("A") * 0.35f;
            Utils.DrawBorderString(spriteBatch, "A",
                new Vector2(amX + (24 - amTextSize.X) / 2f, amY + (24 - amTextSize.Y) / 2f),
                _autoMinimize ? new Color(100, 255, 100) : new Color(255, 100, 100), 0.35f);

            // Tooltip for auto-minimize button
            if (Main.MouseScreen.X >= amX && Main.MouseScreen.X <= amX + 24
                && Main.MouseScreen.Y >= amY && Main.MouseScreen.Y <= amY + 24)
            {
                string tip = _autoMinimize
                    ? Language.GetTextValue("Mods.TerraStorage.UI.CraftingTree.AutoMinimizeOn")
                    : Language.GetTextValue("Mods.TerraStorage.UI.CraftingTree.AutoMinimizeOff");
                Main.instance.MouseText(tip);
            }

            if (_minimized) return;

            // Content area with scissor clipping
            float contentX = _panelX + 12;
            float contentY = _panelY + TitleBarHeight + 12;
            float contentW = _panelWidth - 24;
            float contentH = _panelHeight - TitleBarHeight - 24;

            var clipRect = new Rectangle(
                (int)(contentX * Main.UIScale),
                (int)(contentY * Main.UIScale),
                (int)(contentW * Main.UIScale),
                (int)(contentH * Main.UIScale));

            var savedScissor = spriteBatch.GraphicsDevice.ScissorRectangle;
            spriteBatch.End();
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
                SamplerState.AnisotropicClamp, DepthStencilState.None,
                UIDrawHelpers.ScissorRasterizer,
                null, Main.UIScaleMatrix);
            spriteBatch.GraphicsDevice.ScissorRectangle = clipRect;

            // Draw connection lines using grouped bracket style
            foreach (var node in _allNodes)
            {
                if (node.IsExpanded && node.Children.Count > 0)
                    DrawBracketConnections(spriteBatch, node, node.Children, false);
                if (node.IsSourceExpanded && node.SourceChildren.Count > 0)
                    DrawBracketConnections(spriteBatch, node, node.SourceChildren, true);
            }

            // Draw collapsing nodes (fading out)
            foreach (var node in _collapsingNodes)
                DrawNode(spriteBatch, node);

            // Draw active nodes
            foreach (var node in _allNodes)
                DrawNode(spriteBatch, node);

            // Restore scissor
            spriteBatch.End();
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
                SamplerState.AnisotropicClamp, DepthStencilState.None,
                RasterizerState.CullCounterClockwise, null, Main.UIScaleMatrix);
            spriteBatch.GraphicsDevice.ScissorRectangle = savedScissor;

            // Draw minimap
            DrawMinimap(spriteBatch, contentX, contentY, contentW, contentH);

            // Draw info panel for selected node
            DrawInfoPanel(spriteBatch, contentX, contentY, contentW, contentH);

            // Resize handle indicator
            bool resizeHov = IsInResizeHandle(Main.MouseScreen);
            var handleColor = resizeHov ? new Color(140, 160, 220, 220) : new Color(100, 120, 180, 180);
            float hx = _panelX + _panelWidth - ResizeHandleSize;
            float hy = _panelY + _panelHeight - ResizeHandleSize;
            UIDrawHelpers.DrawResizeHandle(spriteBatch,
                new Rectangle((int)hx, (int)hy, (int)ResizeHandleSize, (int)ResizeHandleSize), handleColor);

            // Tooltip for hovered node — draw hints ABOVE the node
            if (_hoveredNode != null && _tooltipItem != null)
            {
                Vector2 nodeScreen = GraphToScreen(_hoveredNode.AnimatedPosition);
                float nodeSize = CraftingTreeNode.NodeSize.X * _zoom;
                const float hintScale = 0.45f;

                // Build hint lines bottom-up (we stack them above the node)
                var hints = new List<(string text, Color color)>();

                if (!_hoveredNode.IsCycleNode)
                {
                    string expandHint;
                    if (_hoveredNode.IsSourceSide)
                    {
                        bool hasSourceRecipes = RecipeCacheSystem.Instance.HasRecipesFor(_hoveredNode.ItemType);
                        if (!hasSourceRecipes)
                            expandHint = "No crafting sources";
                        else if (_hoveredNode.IsSourceExpanded)
                            expandHint = "Right-Click: Collapse sources";
                        else
                            expandHint = "Right-Click: Show sources";
                    }
                    else
                    {
                        bool hasUses = _recipesByIngredient.ContainsKey(_hoveredNode.ItemType);
                        if (_hoveredNode.ChildrenLoaded && _hoveredNode.Children.Count == 0)
                            expandHint = "No further recipes";
                        else if (!_hoveredNode.ChildrenLoaded && !hasUses)
                            expandHint = "No further recipes";
                        else if (_hoveredNode.IsExpanded)
                            expandHint = "Right-Click: Collapse";
                        else
                            expandHint = "Right-Click: Expand";
                    }
                    bool isNoAction = expandHint.StartsWith("No ");
                    hints.Add((expandHint, isNoAction ? new Color(150, 150, 150) : new Color(200, 255, 200)));
                }

                if (_hoveredNode.IsGroupIngredient)
                {
                    hints.Add((RecipeResolver.GetGroupItemNames(_hoveredNode.GroupId), new Color(80, 200, 220)));
                    hints.Add((RecipeResolver.GetGroupName(_hoveredNode.GroupId), new Color(80, 200, 220)));
                }

                if (_hoveredNode.Recipe != null)
                {
                    var terminalSystem = ModContent.GetInstance<TerminalUISystem>();
                    string hint = terminalSystem?.IsTerminalOpen == true
                        ? "Middle-Click: Select recipe"
                        : "Middle-Click: Select recipe (open Terminal first)";
                    hints.Add((hint, new Color(200, 200, 255)));

                    bool isFav = StoragePlayerSystem.Local.IsRecipeFavorited(_hoveredNode.Recipe);
                    hints.Add((isFav ? "Alt+Click: Unfavorite" : "Alt+Click: Favorite", new Color(255, 220, 100)));
                }

                // Draw hints stacking upward from above the node
                float hintX = nodeScreen.X;
                float hintY = nodeScreen.Y - 4;
                for (int i = hints.Count - 1; i >= 0; i--)
                {
                    var (text, color) = hints[i];
                    var sz = FontAssets.MouseText.Value.MeasureString(text) * hintScale;
                    hintY -= sz.Y + 2;
                    DrawRect(spriteBatch, hintX - 3, hintY - 1, sz.X + 6, sz.Y + 2, new Color(0, 0, 0, 200));
                    Utils.DrawBorderString(spriteBatch, text, new Vector2(hintX, hintY), color, hintScale);
                }

                // Show vanilla tooltip
                Main.HoverItem = _tooltipItem.Clone();
                Main.instance.MouseText(string.Empty);
            }

            // Info panel hover tooltips (vanilla style)
            if (_hoveredNode == null)
            {
                if (_infoPanelHoveredItemType > 0)
                {
                    var hoverItem = new Item();
                    hoverItem.SetDefaults(_infoPanelHoveredItemType);
                    Main.HoverItem = hoverItem;
                    Main.hoverItemName = hoverItem.Name;
                }
                else if (_infoPanelHoveredNpcType != 0)
                {
                    string npcName = Lang.GetNPCNameValue(_infoPanelHoveredNpcType);
                    Main.instance.MouseText(npcName);
                }
            }
        }

        private void DrawNode(SpriteBatch spriteBatch, CraftingTreeNode node)
        {
            Vector2 screenPos = GraphToScreen(node.AnimatedPosition);
            float opacity = node.Opacity;
            float size = CraftingTreeNode.NodeSize.X * _zoom;

            // Skip if off-screen
            float contentX = _panelX + 12;
            float contentY = _panelY + TitleBarHeight + 12;
            float contentW = _panelWidth - 24;
            float contentH = _panelHeight - TitleBarHeight - 24;
            if (screenPos.X + size < contentX || screenPos.X > contentX + contentW
                || screenPos.Y + size < contentY || screenPos.Y > contentY + contentH)
                return;

            // Node background
            // Category-colored border
            var cat = UICategoryFilterBar.ClassifyItem(node.ItemType);
            Color borderCol = CategoryColors.TryGetValue(cat, out var c) ? c : new Color(140, 140, 140);
            if (node.IsCycleNode)
                borderCol = new Color(255, 255, 0); // Yellow for cycle nodes
            borderCol *= opacity;

            // Node background — vanilla panel style
            var nodeRect = new Rectangle((int)screenPos.X, (int)screenPos.Y, (int)size, (int)size);
            Color nodeBg = new Color(63, 82, 151) * (0.5f * opacity);
            if (node == _selectedNode)
                nodeBg = new Color(63, 82, 151) * (0.7f * opacity);
            else if (node == _hoveredNode)
                nodeBg = new Color(63, 82, 151) * (0.6f * opacity);
            Utils.DrawInvBG(spriteBatch, nodeRect, nodeBg);

            // Category-colored border overlay
            float borderWidth = node == _root ? 3f : 2f;
            int bw = Math.Max(1, (int)(borderWidth * _zoom));
            DrawRectBorder(spriteBatch, screenPos.X, screenPos.Y, size, size, borderCol, bw);

            // Draw item icon
            if (_zoom >= 0.4f)
            {
                float iconScale = _zoom * 0.75f;
                float iconSize = 32 * iconScale;
                float iconX = screenPos.X + (size - iconSize) / 2f;
                float iconY = screenPos.Y + (size - iconSize) / 2f;

                Main.instance.LoadItem(node.ItemType);
                var tex = TextureAssets.Item[node.ItemType].Value;
                var frame = Main.itemAnimations[node.ItemType] != null
                    ? Main.itemAnimations[node.ItemType].GetFrame(tex)
                    : tex.Bounds;

                float drawScale = iconScale;
                if (frame.Width > 32 || frame.Height > 32)
                    drawScale *= 32f / Math.Max(frame.Width, frame.Height);

                float actualW = frame.Width * drawScale;
                float actualH = frame.Height * drawScale;
                var iconPos = new Vector2(
                    screenPos.X + (size - actualW) / 2f,
                    screenPos.Y + (size - actualH) / 2f);

                spriteBatch.Draw(tex, iconPos, frame, Color.White * opacity, 0f, Vector2.Zero, drawScale, SpriteEffects.None, 0f);


            }

            // Expand indicators
            if (!node.IsCycleNode)
            {
                float indScale = _zoom * 0.6f;
                bool isRoot = node == _root;

                // RIGHT indicator (uses — what this item crafts into)
                if (!node.IsSourceSide || isRoot)
                {
                    bool hasUses = _recipesByIngredient.ContainsKey(node.ItemType);
                    if (node.ChildrenLoaded && node.Children.Count > 0)
                    {
                        string ind = node.IsExpanded ? "-" : "+";
                        var indPos = new Vector2(screenPos.X + size - 10 * _zoom, screenPos.Y + 2 * _zoom);
                        Utils.DrawBorderString(spriteBatch, ind, indPos, Color.White, indScale);
                    }
                    else if (!node.ChildrenLoaded && hasUses)
                    {
                        var indPos = new Vector2(screenPos.X + size - 10 * _zoom, screenPos.Y + 2 * _zoom);
                        Utils.DrawBorderString(spriteBatch, "+", indPos, new Color(150, 150, 150), indScale);
                    }
                }

                // LEFT indicator (sources — what creates this item)
                if (node.IsSourceSide || isRoot)
                {
                    bool hasSourceRecipes = RecipeCacheSystem.Instance.HasRecipesFor(node.ItemType);
                    if (node.SourceChildrenLoaded && node.SourceChildren.Count > 0)
                    {
                        string ind = node.IsSourceExpanded ? "-" : "+";
                        var indPos = new Vector2(screenPos.X + 2 * _zoom, screenPos.Y + 2 * _zoom);
                        Utils.DrawBorderString(spriteBatch, ind, indPos, Color.White, indScale);
                    }
                    else if (!node.SourceChildrenLoaded && hasSourceRecipes)
                    {
                        var indPos = new Vector2(screenPos.X + 2 * _zoom, screenPos.Y + 2 * _zoom);
                        Utils.DrawBorderString(spriteBatch, "+", indPos, new Color(150, 150, 150), indScale);
                    }
                }
            }

            // Cycle indicator
            if (node.IsCycleNode)
            {
                float cycleScale = _zoom * 0.25f;
                var cyclePos = new Vector2(screenPos.X + 2 * _zoom, screenPos.Y + size - 12 * _zoom);
                Utils.DrawBorderString(spriteBatch, Language.GetTextValue("Mods.TerraStorage.UI.CraftingTree.Cycle"), cyclePos, Color.Yellow, cycleScale);
            }
        }

        private void DrawInfoPanel(SpriteBatch spriteBatch, float contentX, float contentY, float contentW, float contentH)
        {
            if (_infoPanelSlide <= 0.01f) return;

            var cache = ItemSourceCache.Instance;
            if (cache == null) return;

            int itemType = _selectedNode?.ItemType ?? 0;
            var drops = cache.GetDropSources(itemType);
            var shops = cache.GetShopSources(itemType);
            var shimmerFrom = cache.GetShimmerSources(itemType);
            int shimmerTo = cache.GetShimmerResult(itemType);

            // Drop-rate strings are formatted once per selected item, not per frame.
            if (_infoRowsForType != itemType)
            {
                _infoRowsForType = itemType;
                _dropRowText.Clear();
                if (drops != null)
                {
                    foreach (var drop in drops)
                    {
                        string pct = drop.DropRate >= 1f ? "100%" : $"{drop.DropRate * 100:0.##}%";
                        string stack = drop.StackMax > 1 ? $" ({drop.StackMin}-{drop.StackMax})" : "";
                        _dropRowText.Add(pct + stack);
                    }
                }
            }

            // Panel slides in from left edge
            float panelW = InfoPanelWidth;
            float panelH = contentH;
            float panelXTarget = contentX;
            float panelXHidden = contentX - panelW;
            float panelX = MathHelper.Lerp(panelXHidden, panelXTarget, _infoPanelSlide);
            float panelY = contentY;

            float headerH = 42f;
            int slotSize = 36;
            float rowH = slotSize + 4;
            float sectionGap = 8f;
            float textScale = 0.5f;
            float labelScale = 0.52f;

            // Lerp toward target, then clamp
            float visibleH = panelH - headerH;
            float maxScroll = Math.Max(0, _infoPanelContentHeight - visibleH);
            _infoPanelScrollTarget = Math.Clamp(_infoPanelScrollTarget, 0, maxScroll);
            float sDiff = _infoPanelScrollTarget - _infoPanelScroll;
            if (Math.Abs(sDiff) < 0.5f) _infoPanelScroll = _infoPanelScrollTarget;
            else _infoPanelScroll += sDiff * 0.15f;
            _infoPanelScroll = Math.Clamp(_infoPanelScroll, 0, maxScroll);

            // Scissor: intersection of content area and info panel bounds
            float clipLeft = Math.Max(panelX, contentX);
            float clipTop = panelY;
            float clipRight = Math.Min(panelX + panelW, contentX + contentW);
            float clipBottom = Math.Min(panelY + panelH, contentY + contentH);
            if (clipRight <= clipLeft || clipBottom <= clipTop) return;

            var savedScissor = spriteBatch.GraphicsDevice.ScissorRectangle;
            spriteBatch.End();
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
                SamplerState.AnisotropicClamp, DepthStencilState.None,
                UIDrawHelpers.ScissorRasterizer,
                null, Main.UIScaleMatrix);
            spriteBatch.GraphicsDevice.ScissorRectangle = new Rectangle(
                (int)(clipLeft * Main.UIScale), (int)(clipTop * Main.UIScale),
                (int)((clipRight - clipLeft) * Main.UIScale), (int)((clipBottom - clipTop) * Main.UIScale));

            // Reset hover tracking
            _infoPanelHoveredItemType = 0;
            _infoPanelHoveredNpcType = 0;
            var mouseScreen = Main.MouseScreen;
            bool mouseInPanel = mouseScreen.X >= clipLeft && mouseScreen.X <= clipRight
                             && mouseScreen.Y >= clipTop && mouseScreen.Y <= clipBottom;

            // Dark underlay for visibility, then vanilla panel
            UIDrawHelpers.DrawUnderlay(spriteBatch, panelX, panelY, panelW, panelH);
            Utils.DrawInvBG(spriteBatch,
                new Rectangle((int)panelX, (int)panelY, (int)panelW, (int)panelH),
                new Color(63, 82, 151) * 0.7f);

            // Header background
            Utils.DrawInvBG(spriteBatch,
                new Rectangle((int)panelX, (int)panelY, (int)panelW, (int)headerH),
                new Color(63, 82, 151) * 0.85f);

            // Header — item icon + name
            var headerSlot = new Rectangle((int)(panelX + 6), (int)(panelY + (headerH - slotSize) / 2f), slotSize, slotSize);
            Utils.DrawInvBG(spriteBatch, headerSlot, new Color(63, 82, 151) * 0.7f);
            DrawItemInSlot(spriteBatch, itemType, headerSlot);

            string itemName = Lang.GetItemNameValue(itemType);
            Utils.DrawBorderString(spriteBatch, itemName,
                new Vector2(panelX + slotSize + 14, panelY + (headerH - 16) / 2f), Color.White, 0.48f);

            // Header item hover tooltip
            if (mouseInPanel && headerSlot.Contains(mouseScreen.ToPoint()))
                _infoPanelHoveredItemType = itemType;

            // Flush header draws before narrowing scissor to scroll area
            spriteBatch.End();
            float scrollClipTop = Math.Max(panelY + headerH, contentY);
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
                SamplerState.AnisotropicClamp, DepthStencilState.None,
                UIDrawHelpers.ScissorRasterizer,
                null, Main.UIScaleMatrix);
            spriteBatch.GraphicsDevice.ScissorRectangle = new Rectangle(
                (int)(clipLeft * Main.UIScale), (int)(scrollClipTop * Main.UIScale),
                (int)((clipRight - clipLeft) * Main.UIScale), (int)((clipBottom - scrollClipTop) * Main.UIScale));

            float y = panelY + headerH - _infoPanelScroll;
            float startY = y;

            bool mouseInScroll = mouseScreen.X >= clipLeft && mouseScreen.X <= clipRight
                              && mouseScreen.Y >= scrollClipTop && mouseScreen.Y <= clipBottom;

            // ── Dropped by ──
            if (drops != null && drops.Count > 0)
            {
                Utils.DrawBorderString(spriteBatch, Language.GetTextValue("Mods.TerraStorage.UI.CraftingTree.DroppedBy"), new Vector2(panelX + 8, y + 2), new Color(255, 200, 100), labelScale);
                y += 22;

                for (int i = 0; i < drops.Count; i++)
                {
                    var drop = drops[i];
                    float rowX = panelX + 6;
                    float rowY = y;

                    // NPC slot (vanilla style)
                    var cellRect = new Rectangle((int)rowX, (int)rowY, slotSize, slotSize);
                    Utils.DrawInvBG(spriteBatch, cellRect, new Color(63, 82, 151) * 0.4f);
                    DrawNpcInSlot(spriteBatch, drop.NpcType, cellRect);

                    // Drop text
                    string npcName = Lang.GetNPCNameValue(drop.NpcType);
                    Utils.DrawBorderString(spriteBatch, npcName, new Vector2(rowX + slotSize + 4, rowY + 4), new Color(220, 220, 220), textScale);
                    Utils.DrawBorderString(spriteBatch, _dropRowText[i], new Vector2(rowX + slotSize + 4, rowY + 19), new Color(180, 180, 140), textScale * 0.85f);

                    // Hover → NPC tooltip
                    if (mouseInScroll && cellRect.Contains(mouseScreen.ToPoint()))
                        _infoPanelHoveredNpcType = drop.NpcType;

                    y += rowH;
                }

                y += sectionGap;
            }

            // ── Sold by ──
            if (shops != null && shops.Count > 0)
            {
                Utils.DrawBorderString(spriteBatch, Language.GetTextValue("Mods.TerraStorage.UI.CraftingTree.SoldBy"), new Vector2(panelX + 8, y + 2), new Color(100, 200, 255), labelScale);
                y += 22;

                foreach (var shop in shops)
                {
                    float rowX = panelX + 6;
                    float rowY = y;

                    var cellRect = new Rectangle((int)rowX, (int)rowY, slotSize, slotSize);
                    Utils.DrawInvBG(spriteBatch, cellRect, new Color(63, 82, 151) * 0.4f);
                    DrawNpcInSlot(spriteBatch, shop.NpcType, cellRect);

                    string npcName = Lang.GetNPCNameValue(shop.NpcType);
                    Utils.DrawBorderString(spriteBatch, npcName, new Vector2(rowX + slotSize + 4, rowY + (slotSize - 16) / 2f), new Color(220, 220, 220), textScale);

                    if (mouseInScroll && cellRect.Contains(mouseScreen.ToPoint()))
                        _infoPanelHoveredNpcType = shop.NpcType;

                    y += rowH;
                }

                y += sectionGap;
            }

            // ── Shimmered from ──
            if (shimmerFrom != null && shimmerFrom.Count > 0)
            {
                Utils.DrawBorderString(spriteBatch, Language.GetTextValue("Mods.TerraStorage.UI.CraftingTree.ShimmeredFrom"), new Vector2(panelX + 8, y + 2), new Color(200, 100, 255), labelScale);
                y += 22;

                foreach (int srcType in shimmerFrom)
                {
                    float rowX = panelX + 6;
                    float rowY = y;

                    var cellRect = new Rectangle((int)rowX, (int)rowY, slotSize, slotSize);
                    Utils.DrawInvBG(spriteBatch, cellRect, new Color(63, 82, 151) * 0.4f);
                    DrawItemInSlot(spriteBatch, srcType, cellRect);

                    if (mouseInScroll && cellRect.Contains(mouseScreen.ToPoint()))
                        _infoPanelHoveredItemType = srcType;

                    string srcName = Lang.GetItemNameValue(srcType);
                    Utils.DrawBorderString(spriteBatch, srcName, new Vector2(rowX + slotSize + 4, rowY + (slotSize - 16) / 2f), new Color(220, 220, 220), textScale);

                    y += rowH;
                }

                y += sectionGap;
            }

            // ── Shimmers into ──
            if (shimmerTo > 0)
            {
                Utils.DrawBorderString(spriteBatch, Language.GetTextValue("Mods.TerraStorage.UI.CraftingTree.ShimmersInto"), new Vector2(panelX + 8, y + 2), new Color(200, 100, 255), labelScale);
                y += 22;

                float rowX = panelX + 6;
                float rowY = y;

                var cellRect = new Rectangle((int)rowX, (int)rowY, slotSize, slotSize);
                Utils.DrawInvBG(spriteBatch, cellRect, new Color(63, 82, 151) * 0.4f);
                DrawItemInSlot(spriteBatch, shimmerTo, cellRect);

                if (mouseInScroll && cellRect.Contains(mouseScreen.ToPoint()))
                    _infoPanelHoveredItemType = shimmerTo;

                string targetName = Lang.GetItemNameValue(shimmerTo);
                Utils.DrawBorderString(spriteBatch, targetName, new Vector2(rowX + slotSize + 4, rowY + (slotSize - 16) / 2f), new Color(220, 220, 220), textScale);

                y += rowH;
            }

            _infoPanelContentHeight = y - startY + 4; // bottom padding

            // Restore scissor
            spriteBatch.End();
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
                SamplerState.AnisotropicClamp, DepthStencilState.None,
                RasterizerState.CullCounterClockwise, null, Main.UIScaleMatrix);
            spriteBatch.GraphicsDevice.ScissorRectangle = savedScissor;

            // Scroll indicator
            if (_infoPanelContentHeight > visibleH)
            {
                float scrollBarH = Math.Max(20, visibleH * (visibleH / _infoPanelContentHeight));
                float scrollBarY = scrollClipTop + (_infoPanelScroll / (_infoPanelContentHeight - visibleH)) * (visibleH - scrollBarH);
                DrawRect(spriteBatch, panelX + panelW - 4, scrollBarY, 3, scrollBarH, new Color(100, 100, 160, 150));
            }
        }

        //Draws an item icon inside a slot rectangle, matching the terminal's DrawItem pattern.
        private void DrawItemInSlot(SpriteBatch spriteBatch, int itemType, Rectangle cellRect)
        {
            Main.instance.LoadItem(itemType);
            var tex = TextureAssets.Item[itemType].Value;
            var frame = Main.itemAnimations[itemType] != null
                ? Main.itemAnimations[itemType].GetFrame(tex)
                : tex.Frame();

            float scale = 1f;
            float maxDim = Math.Max(frame.Width, frame.Height);
            if (maxDim > cellRect.Width - 6)
                scale = (cellRect.Width - 6f) / maxDim;

            var center = new Vector2(cellRect.X + cellRect.Width / 2f, cellRect.Y + cellRect.Height / 2f);
            var origin = new Vector2(frame.Width / 2f, frame.Height / 2f);
            spriteBatch.Draw(tex, center, frame, Color.White, 0f, origin, scale, SpriteEffects.None, 0f);
        }

        //Draws an NPC's first animation frame inside a slot rectangle.
        private void DrawNpcInSlot(SpriteBatch spriteBatch, int npcType, Rectangle cellRect)
        {
            int absType = Math.Abs(npcType);
            if (absType <= 0 || absType >= TextureAssets.Npc.Length) return;

            Main.instance.LoadNPC(absType);
            var tex = TextureAssets.Npc[absType].Value;
            int frameCount = Main.npcFrameCount[absType];
            if (frameCount <= 0) frameCount = 1;
            var frame = new Rectangle(0, 0, tex.Width, tex.Height / frameCount);

            float maxDim = Math.Max(frame.Width, frame.Height);
            float scale = (cellRect.Width - 4f) / maxDim;

            var center = new Vector2(cellRect.X + cellRect.Width / 2f, cellRect.Y + cellRect.Height / 2f);
            var origin = new Vector2(frame.Width / 2f, frame.Height / 2f);
            spriteBatch.Draw(tex, center, frame, Color.White, 0f, origin, scale, SpriteEffects.None, 0f);
        }

        // Draws bracket-style connections from a parent to all its children.
        // Uses side (right): parent right edge → short stub → vertical trunk → stubs to each child left edge.
        // Source side (left): parent left edge → short stub → vertical trunk → stubs to each child right edge.
        private void DrawBracketConnections(SpriteBatch spriteBatch, CraftingTreeNode parent, List<CraftingTreeNode> children, bool isSourceSide)
        {
            Vector2 parentScreen = GraphToScreen(parent.AnimatedPosition);
            float nodeSize = CraftingTreeNode.NodeSize.X * _zoom;
            float halfNode = nodeSize / 2f;
            float stubLen = CraftingTreeNode.LevelSpacing * _zoom * 0.4f;
            float lineWidth = Math.Max(1f, 2f * _zoom);

            Color normalColor = isSourceSide
                ? new Color(160, 100, 80, 150)
                : new Color(80, 100, 160, 150);

            // Parent attachment point
            float parentY = parentScreen.Y + halfNode;
            float trunkX;
            if (isSourceSide)
            {
                // Stub goes left from parent's left edge
                trunkX = parentScreen.X - stubLen;
                DrawLine(spriteBatch, new Vector2(parentScreen.X, parentY), new Vector2(trunkX, parentY), normalColor, lineWidth);
            }
            else
            {
                // Stub goes right from parent's right edge
                trunkX = parentScreen.X + nodeSize + stubLen;
                DrawLine(spriteBatch, new Vector2(parentScreen.X + nodeSize, parentY), new Vector2(trunkX, parentY), normalColor, lineWidth);
            }

            // Find vertical extent of children
            float minChildY = float.MaxValue;
            float maxChildY = float.MinValue;
            foreach (var child in children)
            {
                Vector2 cs = GraphToScreen(child.AnimatedPosition);
                float cy = cs.Y + halfNode;
                minChildY = Math.Min(minChildY, cy);
                maxChildY = Math.Max(maxChildY, cy);
            }

            // Vertical trunk (only if more than one child, or child isn't aligned with parent)
            if (children.Count > 1 || Math.Abs(minChildY - parentY) > 1f)
            {
                float trunkTop = Math.Min(parentY, minChildY);
                float trunkBot = Math.Max(parentY, maxChildY);
                DrawLine(spriteBatch, new Vector2(trunkX, trunkTop), new Vector2(trunkX, trunkBot), normalColor, lineWidth);
            }

            // Stubs from trunk to each child
            foreach (var child in children)
            {
                Vector2 childScreen = GraphToScreen(child.AnimatedPosition);
                float childY = childScreen.Y + halfNode;
                Color lc = child.IsCycleNode ? new Color(255, 255, 0, 100) : normalColor;

                if (isSourceSide)
                {
                    // Trunk → child's right edge
                    DrawLine(spriteBatch, new Vector2(trunkX, childY), new Vector2(childScreen.X + nodeSize, childY), lc, lineWidth);
                }
                else
                {
                    // Trunk → child's left edge
                    DrawLine(spriteBatch, new Vector2(trunkX, childY), new Vector2(childScreen.X, childY), lc, lineWidth);
                }
            }
        }

        private void DrawMinimap(SpriteBatch spriteBatch, float contentX, float contentY, float contentW, float contentH)
        {
            if (_allNodes.Count <= 1) return;

            // Compute bounds of all nodes in graph space
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            foreach (var node in _allNodes)
            {
                minX = Math.Min(minX, node.Position.X);
                minY = Math.Min(minY, node.Position.Y);
                maxX = Math.Max(maxX, node.Position.X + CraftingTreeNode.NodeSize.X);
                maxY = Math.Max(maxY, node.Position.Y + CraftingTreeNode.NodeSize.Y);
            }

            float graphW = maxX - minX;
            float graphH = maxY - minY;
            if (graphW <= 0 || graphH <= 0) return;

            // Minimap position (bottom-right corner)
            float mmX = contentX + contentW - MinimapSize - MinimapPadding;
            float mmY = contentY + contentH - MinimapSize - MinimapPadding;
            float mmW = MinimapSize;
            float mmH = MinimapSize;

            // Background
            UIDrawHelpers.DrawUnderlay(spriteBatch, mmX, mmY, mmW, mmH);
            Utils.DrawInvBG(spriteBatch,
                new Rectangle((int)mmX, (int)mmY, (int)mmW, (int)mmH),
                new Color(63, 82, 151) * 0.5f);

            // Scale to fit all nodes
            float scaleX = (mmW - 8) / graphW;
            float scaleY = (mmH - 8) / graphH;
            float scale = Math.Min(scaleX, scaleY);

            // Store minimap bounds for hit testing and drag in Update
            _minimapRect = new Rectangle((int)mmX, (int)mmY, (int)mmW, (int)mmH);
            _minimapScale = scale;
            _minimapGraphMinX = minX;
            _minimapGraphMinY = minY;

            // Draw connection lines on minimap using bracket style
            float dotSize = Math.Max(3, 5 * scale);
            float mmStub = (CraftingTreeNode.NodeSize.X + CraftingTreeNode.LevelSpacing) * scale * 0.35f;
            foreach (var node in _allNodes)
            {
                if (node.IsExpanded && node.Children.Count > 0)
                    DrawMinimapBracket(spriteBatch, node, node.Children, false, mmX, mmY, minX, minY, scale, dotSize, mmStub);
                if (node.IsSourceExpanded && node.SourceChildren.Count > 0)
                    DrawMinimapBracket(spriteBatch, node, node.SourceChildren, true, mmX, mmY, minX, minY, scale, dotSize, mmStub);
            }

            // Draw node dots
            foreach (var node in _allNodes)
            {
                float dotX = mmX + 4 + (node.Position.X - minX) * scale;
                float dotY = mmY + 4 + (node.Position.Y - minY) * scale;

                var cat = UICategoryFilterBar.ClassifyItem(node.ItemType);
                Color dotColor = CategoryColors.TryGetValue(cat, out var dc) ? dc : Color.Gray;
                if (node == _root) dotColor = Color.White;

                DrawRect(spriteBatch, dotX, dotY, dotSize, dotSize, dotColor);
            }

            // Draw viewport rectangle
            Vector2 topLeft = ScreenToGraph(new Vector2(contentX, contentY));
            Vector2 bottomRight = ScreenToGraph(new Vector2(contentX + contentW, contentY + contentH));

            float vpX = mmX + 4 + (topLeft.X - minX) * scale;
            float vpY = mmY + 4 + (topLeft.Y - minY) * scale;
            float vpW = (bottomRight.X - topLeft.X) * scale;
            float vpH = (bottomRight.Y - topLeft.Y) * scale;

            // Clamp viewport rect to minimap bounds
            vpX = Math.Clamp(vpX, mmX, mmX + mmW);
            vpY = Math.Clamp(vpY, mmY, mmY + mmH);
            vpW = Math.Clamp(vpW, 0, mmX + mmW - vpX);
            vpH = Math.Clamp(vpH, 0, mmY + mmH - vpY);

            DrawRectBorder(spriteBatch, vpX, vpY, vpW, vpH, Color.White);
        }

        private void DrawMinimapBracket(SpriteBatch spriteBatch, CraftingTreeNode parent,
            List<CraftingTreeNode> children, bool isSourceSide,
            float mmX, float mmY, float minX, float minY, float scale,
            float dotSize, float mmStub)
        {
            if (children.Count == 0) return;

            Color normalColor = isSourceSide
                ? new Color(160, 100, 80, 120)
                : new Color(80, 100, 160, 120);

            float dotHalf = dotSize / 2f;

            // Parent dot center in minimap space
            float px = mmX + 4 + (parent.Position.X - minX) * scale + dotHalf;
            float py = mmY + 4 + (parent.Position.Y - minY) * scale + dotHalf;

            // Trunk X position — stub extends from dot edge
            float trunkX;
            if (isSourceSide)
            {
                trunkX = px - dotHalf - mmStub;
                DrawLine(spriteBatch, new Vector2(px - dotHalf, py), new Vector2(trunkX, py), normalColor, 1f);
            }
            else
            {
                trunkX = px + dotHalf + mmStub;
                DrawLine(spriteBatch, new Vector2(px + dotHalf, py), new Vector2(trunkX, py), normalColor, 1f);
            }

            // Find vertical extent of children (using dot centers)
            float minChildY = float.MaxValue;
            float maxChildY = float.MinValue;
            foreach (var child in children)
            {
                float cy = mmY + 4 + (child.Position.Y - minY) * scale + dotHalf;
                minChildY = Math.Min(minChildY, cy);
                maxChildY = Math.Max(maxChildY, cy);
            }

            // Vertical trunk
            if (children.Count > 1 || Math.Abs(minChildY - py) > 1f)
            {
                float trunkTop = Math.Min(py, minChildY);
                float trunkBot = Math.Max(py, maxChildY);
                DrawLine(spriteBatch, new Vector2(trunkX, trunkTop), new Vector2(trunkX, trunkBot), normalColor, 1f);
            }

            // Stubs from trunk to each child dot edge
            foreach (var child in children)
            {
                float cx = mmX + 4 + (child.Position.X - minX) * scale + dotHalf;
                float cy = mmY + 4 + (child.Position.Y - minY) * scale + dotHalf;
                Color lc = child.IsCycleNode ? new Color(255, 255, 0, 80) : normalColor;

                if (isSourceSide)
                    DrawLine(spriteBatch, new Vector2(trunkX, cy), new Vector2(cx + dotHalf, cy), lc, 1f);
                else
                    DrawLine(spriteBatch, new Vector2(trunkX, cy), new Vector2(cx - dotHalf, cy), lc, 1f);
            }
        }

        // ─── Drawing Primitives ─────────────────────────────────────────

        private static Texture2D _pixel;

        private static Texture2D GetPixel(SpriteBatch spriteBatch)
        {
            if (_pixel == null || _pixel.IsDisposed)
            {
                _pixel = new Texture2D(spriteBatch.GraphicsDevice, 1, 1);
                _pixel.SetData(new[] { Color.White });
            }
            return _pixel;
        }

        private static void DrawRect(SpriteBatch sb, float x, float y, float w, float h, Color color)
        {
            sb.Draw(GetPixel(sb), new Rectangle((int)x, (int)y, (int)w, (int)h), color);
        }

        private static void DrawRectBorder(SpriteBatch sb, float x, float y, float w, float h, Color color, float thickness = 1f)
        {
            int t = Math.Max(1, (int)thickness);
            var pixel = GetPixel(sb);
            sb.Draw(pixel, new Rectangle((int)x, (int)y, (int)w, t), color);              // top
            sb.Draw(pixel, new Rectangle((int)x, (int)(y + h - t), (int)w, t), color);    // bottom
            sb.Draw(pixel, new Rectangle((int)x, (int)y, t, (int)h), color);              // left
            sb.Draw(pixel, new Rectangle((int)(x + w - t), (int)y, t, (int)h), color);    // right
        }

        private static readonly Dictionary<int, Texture2D> _circles = new();

        //Gets or creates a filled circle texture at the exact pixel radius needed.
        private static Texture2D GetCircle(SpriteBatch sb, int radius)
        {
            if (_circles.TryGetValue(radius, out var tex) && tex != null && !tex.IsDisposed)
                return tex;

            int d = radius * 2;
            tex = new Texture2D(sb.GraphicsDevice, d, d);
            var data = new Color[d * d];
            float rSq = (radius - 0.5f) * (radius - 0.5f);
            for (int py = 0; py < d; py++)
                for (int px = 0; px < d; px++)
                {
                    float dx = px - radius + 0.5f;
                    float dy = py - radius + 0.5f;
                    data[py * d + px] = dx * dx + dy * dy <= rSq ? Color.White : Color.Transparent;
                }
            tex.SetData(data);
            _circles[radius] = tex;
            return tex;
        }

        //Draws a filled rectangle with rounded corners. No scaling — circle textures are generated at exact size.
        private static void DrawRoundedRect(SpriteBatch sb, float x, float y, float w, float h, Color color, int r = 6)
        {
            // Snap to integer grid first to prevent gaps between components
            int ix = (int)Math.Round(x);
            int iy = (int)Math.Round(y);
            int iw = (int)Math.Round(x + w) - ix;
            int ih = (int)Math.Round(y + h) - iy;

            if (r <= 1 || iw < r * 2 || ih < r * 2)
            {
                sb.Draw(GetPixel(sb), new Rectangle(ix, iy, iw, ih), color);
                return;
            }

            var pixel = GetPixel(sb);
            var circle = GetCircle(sb, r);

            // Four corner quadrants drawn at 1:1 pixel scale
            sb.Draw(circle, new Vector2(ix, iy), new Rectangle(0, 0, r, r), color, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0f);
            sb.Draw(circle, new Vector2(ix + iw - r, iy), new Rectangle(r, 0, r, r), color, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0f);
            sb.Draw(circle, new Vector2(ix, iy + ih - r), new Rectangle(0, r, r, r), color, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0f);
            sb.Draw(circle, new Vector2(ix + iw - r, iy + ih - r), new Rectangle(r, r, r, r), color, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0f);

            // Fill: center cross — all derived from integer coords, no gaps
            sb.Draw(pixel, new Rectangle(ix + r, iy, iw - r * 2, ih), color);
            sb.Draw(pixel, new Rectangle(ix, iy + r, r, ih - r * 2), color);
            sb.Draw(pixel, new Rectangle(ix + iw - r, iy + r, r, ih - r * 2), color);
        }

        private static void DrawLine(SpriteBatch sb, Vector2 start, Vector2 end, Color color, float width = 1f)
        {
            Vector2 diff = end - start;
            float length = diff.Length();
            if (length < 0.5f) return;

            float angle = (float)Math.Atan2(diff.Y, diff.X);
            sb.Draw(GetPixel(sb), start, null, color, angle, Vector2.Zero, new Vector2(length, width), SpriteEffects.None, 0f);
        }

        private static void DrawButton(SpriteBatch sb, float x, float y, float w, float h, string text, bool hovered = false, Color? hoverColor = null)
        {
            Color bg = hovered
                ? (hoverColor ?? new Color(93, 116, 201)) * 0.95f
                : new Color(63, 82, 151) * 0.8f;
            Utils.DrawInvBG(sb, new Rectangle((int)x, (int)y, (int)w, (int)h), bg);
            var size = FontAssets.MouseText.Value.MeasureString(text) * 0.35f;
            Utils.DrawBorderString(sb, text,
                new Vector2(x + (w - size.X) / 2f, y + (h - size.Y) / 2f),
                hovered ? Color.White : Color.White * 0.85f, 0.35f);
        }
    }
}
