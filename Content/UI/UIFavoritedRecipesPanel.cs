using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.Localization;
using Terraria.UI;
using TerraStorage.Systems;

namespace TerraStorage.Content.UI
{
    // A floating, collapsible, pinnable panel that shows every recipe the player has
    // favorited via <see cref="StoragePlayerSystem"/>. For each recipe it renders the
    // output item and each ingredient with a live have/need count drawn from storage
    // and the player's inventory. Supports drag-to-move, scroll wheel, and a ghost
    // (semi-transparent) mode when pinned and the inventory is closed.
    public class UIFavoritedRecipesPanel : UIState
    {
        private const float PanelWidth     = 290f;
        private const float HeaderHeight   = 26f;
        private const float MaxBodyH       = 380f;

        // Slot sizes
        private const float OutputSlotSize = 36f;
        private const float IngSlotSize    = 28f;
        private const float SlotGap        = 2f;

        // Position (screen-space, persisted between sessions)
        public float PanelLeft = 460f;
        public float PanelTop  = 200f;

        public bool IsCollapsed { get; private set; } = false;
        public bool IsPinned    { get; private set; } = false;

        //Checks if the mouse is over the panel bounds.
        public bool IsMouseOverPanel()
        {
            EnsureCaches();
            var m = Main.MouseScreen;
            float bodyH = IsCollapsed ? 0f : Math.Min(_rowCache.BodyHeight, MaxBodyH);
            float totalH = HeaderHeight + bodyH;
            return m.X >= PanelLeft && m.X <= PanelLeft + PanelWidth
                && m.Y >= PanelTop && m.Y <= PanelTop + totalH;
        }

        private bool _dragging;
        private Vector2 _dragOffset;
        private bool _prevMouseLeft;
        private float _scrollOffset;
        private float _maxScroll;

        private List<Guid> _diskIds = new();

        // Hit-test rects rebuilt each frame
        private Rectangle _headerRect;
        private Rectangle _collapseBtnRect;
        private Rectangle _pinBtnRect;

        // Output slot rects per recipe — built during DrawBody, checked during next Update
        private readonly List<(int recipeIdx, Rectangle rect)> _recipeOutputRects = new();

        // Hover tooltip state (set during DrawBody, read after draw)
        private string _hoveredTooltip;

        // The panel draws every frame while pinned, so nothing storage-derived may be computed
        // per frame: rows rebuild on FavoritesVersion, the storage snapshot on StorageVersion /
        // disk-set change, and slot strings only when their count changed. The inventory has no
        // version counter, so its counts are the one per-frame input — a single 50-slot pass
        // into a reused dictionary.
        private readonly FavoritesRowCache _rowCache = new();
        private Dictionary<int, int> _storageCounts = new();
        private readonly Dictionary<int, int> _invCounts = new();
        private int _diskIdsToken;

        // Hover item cached per type; Main.HoverItem always receives a Clone because tooltip
        // code mutates HoverItem fields across frames (see UIItemGrid.GetOrCreateDrawItem).
        private Item _hoverItem = new();
        private int _hoverItemType = -1;

        public void SetDiskIds(List<Guid> ids)
        {
            _diskIds = ids ?? new();
            _diskIdsToken++;
        }

        // Called on world unload: the panel outlives worlds and characters, and neither
        // FavoritesVersion nor StorageVersion is guaranteed to differ in the next session,
        // so everything derived from them must be dropped here (see FavoritesRowCache.
        // ResetVersionStamps). Without this, a pinned panel entering another world shows the
        // previous session's rows and counts — and alt-click on a stale row would toggle the
        // wrong recipe for the new character.
        public void ResetCaches()
        {
            _rowCache.ResetVersionStamps();
            _rowCache.Rows.Clear();
            _recipeOutputRects.Clear();
            _diskIds = new List<Guid>();
            _diskIdsToken++;
            _storageCounts = new Dictionary<int, int>();
            _invCounts.Clear();
            _hoverItemType = -1;
        }

        private void EnsureCaches()
        {
            var player = StoragePlayerSystem.Local;
            if (_rowCache.NeedsRowRebuild(player.FavoritesVersion))
            {
                _rowCache.Rows.Clear();
                foreach (int recipeIdx in player.FavoritedRecipes)
                {
                    if (recipeIdx < 0 || recipeIdx >= Recipe.numRecipes) continue;
                    var recipe = Main.recipe[recipeIdx];

                    var row = new FavoritesRowCache.Row
                    {
                        RecipeIndex = recipeIdx,
                        OutputType = recipe.createItem.type,
                    };
                    foreach (var ingredient in recipe.requiredItem)
                    {
                        if (ingredient.type <= ItemID.None) continue;
                        row.Slots.Add(new FavoritesRowCache.Slot
                        {
                            ItemType = ingredient.type,
                            Needed = ingredient.stack,
                        });
                    }
                    _rowCache.Rows.Add(row);
                }
                _rowCache.MarkRowsRebuilt(player.FavoritesVersion);
            }

            long storageVersion = StorageWorldSystem.Instance?.StorageVersion ?? 0;
            if (_rowCache.NeedsStorageRefresh(storageVersion, _diskIdsToken))
            {
                _storageCounts = _diskIds.Count > 0
                    ? StorageWorldSystem.Instance.GetItemCounts(_diskIds)
                    : new Dictionary<int, int>();
                _rowCache.MarkStorageRefreshed(storageVersion, _diskIdsToken);
            }
        }

        private void RefreshInventoryCounts()
        {
            _invCounts.Clear();
            var inventory = Main.LocalPlayer.inventory;
            for (int i = 0; i < 50; i++)
            {
                var item = inventory[i];
                if (item == null || item.IsAir) continue;
                _invCounts.TryGetValue(item.type, out int count);
                _invCounts[item.type] = count + item.stack;
            }
        }

        private Item GetHoverItem(int itemType)
        {
            if (_hoverItemType != itemType)
            {
                _hoverItem = new Item();
                _hoverItem.SetDefaults(itemType);
                _hoverItemType = itemType;
            }
            return _hoverItem;
        }

        public void TogglePinned()
        {
            IsPinned = !IsPinned;
            SoundEngine.PlaySound(SoundID.MenuTick);
        }

        public override void Update(GameTime gameTime)
        {
            base.Update(gameTime);

            bool justClicked = Main.mouseLeft && !_prevMouseLeft && !UIClickBlocker.IsConsumed;
            _prevMouseLeft = UIClickBlocker.RealMouseLeft;

            // Claimed before the ghost-mode return below: a ghost panel is still visible and still
            // occludes whatever is under it, and ghost mode only happens with the inventory closed --
            // exactly when the Crafting Tree and Encyclopedia can be open underneath it.
            // IsMouseOverPanel(), not GetPanelRect().Contains(): this is the same hit-test the
            // registry uses, so the region the panel claims and the region it reports hovering are
            // the same one, down to the boundary pixel.
            UIClickBlocker.ClaimIfPressed(IsMouseOverPanel(),
                Main.mouseLeft, Main.mouseRight, Main.mouseMiddle);

            // Ghost mode: pinned but inventory is closed — only the collapse button works.
            bool ghostMode = IsPinned && !Main.playerInventory;

            // In ghost mode only the collapse button is interactive
            if (ghostMode)
            {
                if (_collapseBtnRect.Contains(Main.MouseScreen.ToPoint()))
                {
                    Main.LocalPlayer.mouseInterface = true;
                    if (justClicked)
                    {
                        UIClickBlocker.Consume();
                        IsCollapsed = !IsCollapsed;
                        _scrollOffset = 0f;
                        SoundEngine.PlaySound(SoundID.MenuTick);
                    }
                }
                return;
            }

            var panelRect = GetPanelRect();
            if (panelRect.Contains(Main.MouseScreen.ToPoint()))
            {
                Main.LocalPlayer.mouseInterface = true;
                Terraria.GameInput.PlayerInput.LockVanillaMouseScroll("TerraStorage:FavPanel");
            }

            if (_dragging)
            {
                UIClickBlocker.MarkGesture();
                if (!UIClickBlocker.RealMouseLeft)
                {
                    _dragging = false;
                    UIPositionStore.Save("favpanel", PanelLeft, PanelTop);
                }
                else
                {
                    PanelLeft = Main.MouseScreen.X - _dragOffset.X;
                    PanelTop  = Main.MouseScreen.Y - _dragOffset.Y;
                    PanelLeft = Math.Clamp(PanelLeft, 0, Main.screenWidth  - PanelWidth);
                    PanelTop  = Math.Clamp(PanelTop,  0, Main.screenHeight - HeaderHeight);
                }
                return;
            }

            if (justClicked)
            {
                bool alt = Microsoft.Xna.Framework.Input.Keyboard.GetState()
                    .IsKeyDown(Microsoft.Xna.Framework.Input.Keys.LeftAlt)
                    || Microsoft.Xna.Framework.Input.Keyboard.GetState()
                    .IsKeyDown(Microsoft.Xna.Framework.Input.Keys.RightAlt);

                if (alt && !IsCollapsed)
                {
                    var mouse = Main.MouseScreen.ToPoint();
                    foreach (var (recipeIdx, rect) in _recipeOutputRects)
                    {
                        if (rect.Contains(mouse))
                        {
                            if (recipeIdx >= 0 && recipeIdx < Recipe.numRecipes)
                                StoragePlayerSystem.Local.ToggleRecipeFavorite(Main.recipe[recipeIdx]);
                            SoundEngine.PlaySound(SoundID.MenuTick);
                            UIClickBlocker.Consume();
                            return;
                        }
                    }
                }

                if (_collapseBtnRect.Contains(Main.MouseScreen.ToPoint()))
                {
                    IsCollapsed = !IsCollapsed;
                    _scrollOffset = 0f;
                    SoundEngine.PlaySound(SoundID.MenuTick);
                    return;
                }
                if (_pinBtnRect.Contains(Main.MouseScreen.ToPoint()))
                {
                    IsPinned = !IsPinned;
                    SoundEngine.PlaySound(SoundID.MenuTick);
                    return;
                }
                if (_headerRect.Contains(Main.MouseScreen.ToPoint()))
                {
                    _dragging   = true;
                    UIClickBlocker.MarkGesture();
                    _dragOffset = Main.MouseScreen - new Vector2(PanelLeft, PanelTop);
                }
            }
        }

        public override void ScrollWheel(UIScrollWheelEvent evt)
        {
            base.ScrollWheel(evt);
            bool ghostMode = IsPinned && !Main.playerInventory;
            if (!ghostMode && !IsCollapsed && GetPanelRect().Contains(Main.MouseScreen.ToPoint()))
            {
                _scrollOffset -= evt.ScrollWheelValue / 120f * 20f;
                _scrollOffset  = Math.Clamp(_scrollOffset, 0f, Math.Max(0f, _maxScroll));
            }
        }

        protected override void DrawSelf(SpriteBatch spriteBatch)
        {
            _hoveredTooltip = null;

            bool ghostMode  = IsPinned && !Main.playerInventory;
            float alpha     = ghostMode ? 0.55f : 1f;  // text/icon alpha
            float bgAlpha   = ghostMode ? 0f    : 1f;  // background alpha — hidden in ghost mode

            EnsureCaches();

            float bodyH  = IsCollapsed ? 0f : Math.Min(_rowCache.BodyHeight, MaxBodyH);
            float totalH = HeaderHeight + bodyH;

            var panelRect = new Rectangle((int)PanelLeft, (int)PanelTop, (int)PanelWidth, (int)totalH);

            // Panel background — hidden in ghost mode
            if (bgAlpha > 0f)
            {
                UIDrawHelpers.DrawUnderlay(spriteBatch, panelRect);
                Utils.DrawInvBG(spriteBatch, panelRect, new Color(15, 20, 50) * 0.82f * bgAlpha);
            }

            // ── Header ──────────────────────────────────────────────────────
            _headerRect = new Rectangle((int)PanelLeft, (int)PanelTop, (int)(PanelWidth - 52), (int)HeaderHeight);

            // Collapse button
            _collapseBtnRect = new Rectangle((int)(PanelLeft + PanelWidth - 50), (int)PanelTop + 3, 22, 20);
            bool colHov = !ghostMode && _collapseBtnRect.Contains(Main.MouseScreen.ToPoint());
            Color colBg = colHov ? new Color(83, 104, 181) : new Color(43, 54, 101);
            Utils.DrawInvBG(spriteBatch, _collapseBtnRect, colBg * (ghostMode ? 0.4f : 1f));
            Utils.DrawBorderString(spriteBatch, IsCollapsed ? "▲" : "▼",
                new Vector2(_collapseBtnRect.X + 4, _collapseBtnRect.Y + 2),
                Color.White * alpha, 0.7f);
            if (colHov) Main.hoverItemName = IsCollapsed ? "Expand" : "Collapse";

            // Pin button — visible only when inventory is open
            _pinBtnRect = new Rectangle((int)(PanelLeft + PanelWidth - 26), (int)PanelTop + 3, 22, 20);
            if (!ghostMode)
            {
                bool pinHov = _pinBtnRect.Contains(Main.MouseScreen.ToPoint());
                Color pinBg = IsPinned
                    ? new Color(100, 80, 20)
                    : (pinHov ? new Color(83, 104, 181) : new Color(43, 54, 101));
                Utils.DrawInvBG(spriteBatch, _pinBtnRect, pinBg);
                Utils.DrawBorderString(spriteBatch, "📌",
                    new Vector2(_pinBtnRect.X + 3, _pinBtnRect.Y + 1),
                    IsPinned ? Color.Gold : Color.White, 0.6f);
                if (pinHov) Main.hoverItemName = IsPinned ? "Unpin (hide when inventory closes)" : "Pin (keep visible)";
            }

            Utils.DrawBorderString(spriteBatch, Language.GetTextValue("Mods.TerraStorage.UI.FavoritedRecipes.Title"),
                new Vector2(PanelLeft + 6, PanelTop + 5), Color.Gold * alpha, 0.75f);

            if (IsCollapsed) return;

            // ── Body ────────────────────────────────────────────────────────
            float viewH   = bodyH;
            _maxScroll    = Math.Max(0f, _rowCache.BodyHeight - viewH);
            _scrollOffset = Math.Clamp(_scrollOffset, 0f, _maxScroll);

            // Scissor clip ensures items scrolled above/below the body area are hidden.
            // Clip rect must be in physical pixels (UIScale applied), not logical UI units.
            var clipRect = new Rectangle(
                (int)(PanelLeft  * Main.UIScale),
                (int)((PanelTop + HeaderHeight) * Main.UIScale),
                (int)(PanelWidth * Main.UIScale),
                (int)(bodyH      * Main.UIScale));

            var savedScissor = spriteBatch.GraphicsDevice.ScissorRectangle;
            spriteBatch.End();
            spriteBatch.Begin(SpriteSortMode.Deferred,
                BlendState.AlphaBlend, SamplerState.AnisotropicClamp,
                DepthStencilState.None, UIDrawHelpers.ScissorRasterizer, null, Main.UIScaleMatrix);
            spriteBatch.GraphicsDevice.ScissorRectangle = clipRect;

            float y = PanelTop + HeaderHeight - _scrollOffset;
            DrawBody(spriteBatch, ref y, ghostMode, alpha);

            spriteBatch.End();
            spriteBatch.Begin(SpriteSortMode.Deferred,
                BlendState.AlphaBlend, SamplerState.AnisotropicClamp,
                DepthStencilState.None, RasterizerState.CullCounterClockwise, null, Main.UIScaleMatrix);
            spriteBatch.GraphicsDevice.ScissorRectangle = savedScissor;

            // Scrollbar strip — thumb height proportional to visible vs. total content height.
            if (!ghostMode && _maxScroll > 0f)
            {
                float sbH    = bodyH;
                float sbX    = PanelLeft + PanelWidth - 5;
                float sbTop  = PanelTop + HeaderHeight;
                float thumbH = Math.Max(12, sbH * (viewH / (viewH + _maxScroll)));
                float thumbY = sbTop + (_scrollOffset / _maxScroll) * (sbH - thumbH);
                Utils.DrawInvBG(spriteBatch, new Rectangle((int)sbX, (int)sbTop, 4, (int)sbH), new Color(20, 28, 60) * 0.7f);
                Utils.DrawInvBG(spriteBatch, new Rectangle((int)sbX, (int)thumbY, 4, (int)thumbH), new Color(89, 116, 213) * 0.9f);
            }

            if (_rowCache.Rows.Count == 0)
            {
                Utils.DrawBorderString(spriteBatch, Language.GetTextValue("Mods.TerraStorage.UI.FavoritedRecipes.NoFavorites"),
                    new Vector2(PanelLeft + 8, PanelTop + HeaderHeight + 6), Color.Gray * alpha, 0.7f);
            }

            // Apply tooltip gathered during body draw
            if (_hoveredTooltip != null)
                Main.hoverItemName = _hoveredTooltip;
        }

        private void DrawBody(SpriteBatch spriteBatch, ref float y, bool ghostMode, float alpha)
        {
            _recipeOutputRects.Clear();
            RefreshInventoryCounts();
            var mouse = Main.MouseScreen.ToPoint();

            const float innerOut  = OutputSlotSize - 6f;
            const float innerIng  = IngSlotSize    - 4f;
            const float ingOffset = (OutputSlotSize - IngSlotSize) / 2f; // center ing slots vertically

            y += FavoritesRowCache.TopPad;

            // Rows past the clipped body are drawn out of sight. Their hit rects must not be
            // registered: alt-click is the vanilla favorite gesture, so an off-panel rect turns an
            // ordinary inventory alt-click into unfavoriting a row the player cannot even see.
            float bodyTop = PanelTop + HeaderHeight;
            float bodyBottom = FavoritesRowCache.GetBodyBottom(bodyTop, _rowCache.BodyHeight, MaxBodyH);

            foreach (var row in _rowCache.Rows)
            {
                float rowY = y;

                // ── Output slot (left) ───────────────────────────────────────
                var outRect = new Rectangle((int)(PanelLeft + 4f), (int)rowY, (int)OutputSlotSize, (int)OutputSlotSize);
                bool rowVisible = FavoritesRowCache.IsHitRectVisible(outRect.Bottom, bodyTop, bodyBottom);
                if (!ghostMode)
                    Utils.DrawInvBG(spriteBatch, outRect, new Color(63, 82, 151) * 0.5f);
                DrawItemInSlot(spriteBatch, row.OutputType, outRect, innerOut, alpha);
                if (rowVisible)
                    _recipeOutputRects.Add((row.RecipeIndex, outRect));

                if (!ghostMode && rowVisible && outRect.Contains(mouse))
                {
                    var hoverItem = GetHoverItem(row.OutputType);
                    Main.HoverItem  = hoverItem.Clone();
                    _hoveredTooltip = hoverItem.Name + "\nAlt+Click to unfavorite";
                }

                // ── Ingredient slots (to the right) ──────────────────────────
                float ingX = PanelLeft + 4f + OutputSlotSize + 4f;
                float ingY = rowY + ingOffset;

                foreach (var slot in row.Slots)
                {
                    // Stop drawing if we'd overflow the panel
                    if (ingX + IngSlotSize > PanelLeft + PanelWidth - 8f) break;

                    _storageCounts.TryGetValue(slot.ItemType, out int inStorage);
                    _invCounts.TryGetValue(slot.ItemType, out int inInv);
                    int have = inStorage + inInv;

                    if (FavoritesRowCache.UpdateSlotCount(slot, have))
                    {
                        var countSize = FontAssets.MouseText.Value.MeasureString(slot.Text) * 0.45f;
                        slot.TextWidth  = countSize.X;
                        slot.TextHeight = countSize.Y;
                    }

                    // Green = enough, yellow = partial, red = none.
                    Color countColor = have >= slot.Needed ? Color.LightGreen
                                     : have > 0            ? Color.Yellow
                                     :                       Color.IndianRed;

                    var slotRect = new Rectangle((int)ingX, (int)ingY, (int)IngSlotSize, (int)IngSlotSize);

                    if (!ghostMode)
                        Utils.DrawInvBG(spriteBatch, slotRect, new Color(43, 56, 110) * 0.5f);
                    DrawItemInSlot(spriteBatch, slot.ItemType, slotRect, innerIng, alpha);

                    Utils.DrawBorderString(spriteBatch, slot.Text,
                        new Vector2(slotRect.Right - slot.TextWidth - 1f, slotRect.Bottom - slot.TextHeight),
                        countColor * alpha, 0.45f);

                    if (!ghostMode && slotRect.Contains(mouse))
                    {
                        var hoverItem = GetHoverItem(slot.ItemType);
                        Main.HoverItem  = hoverItem.Clone();
                        _hoveredTooltip = $"{hoverItem.Name}\n{slot.Text}";
                    }

                    ingX += IngSlotSize + SlotGap;
                }

                y += FavoritesRowCache.RowHeight;
            }
        }

        private Rectangle GetPanelRect()
        {
            EnsureCaches();
            float bodyH = IsCollapsed ? 0f : Math.Min(_rowCache.BodyHeight, MaxBodyH);
            return new Rectangle((int)PanelLeft, (int)PanelTop, (int)PanelWidth, (int)(HeaderHeight + bodyH));
        }

        private static void DrawItemInSlot(SpriteBatch spriteBatch, int itemType, Rectangle slotRect, float maxInner, float alpha)
        {
            Main.instance.LoadItem(itemType);
            var texture = TextureAssets.Item[itemType].Value;
            var srcRect = Main.itemAnimations[itemType] != null
                ? Main.itemAnimations[itemType].GetFrame(texture)
                : texture.Frame();

            float scale   = 1f;
            float maxDim  = Math.Max(srcRect.Width, srcRect.Height);
            if (maxDim > maxInner) scale = maxInner / maxDim;

            var center = new Vector2(slotRect.X + slotRect.Width / 2f, slotRect.Y + slotRect.Height / 2f);
            spriteBatch.Draw(texture, center, srcRect, Color.White * alpha, 0f,
                new Vector2(srcRect.Width / 2f, srcRect.Height / 2f), scale, SpriteEffects.None, 0f);
        }

    }
}
