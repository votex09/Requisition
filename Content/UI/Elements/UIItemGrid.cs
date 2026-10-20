using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.GameContent;
using Terraria.GameContent.UI.Elements;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using Terraria.UI;
using TerraStorage.Content.UI;
using TerraStorage.Systems;

namespace TerraStorage.Content.UI.Elements
{
    // Scrollable grid that displays <see cref="ConsolidatedItem"/> entries from storage.
    // Supports configurable column count, an external <see cref="UIScrollbar"/>, left/right/
    // alt+click events, and per-item favorite highlighting via an injected checker delegate.
    // Smooth scrolling: pixel-offset rendering with scissor clipping.
    // The UIScrollbar is kept in sync for display and drag interaction.
    public class UIItemGrid : UIElement
    {
        private static readonly RasterizerState ScissorRasterizer = new() { ScissorTestEnable = true };

        private List<ConsolidatedItem> _items = new();
        private Dictionary<int, Item> _drawItemCache = new();
        // Count-badge strings per index; same lifecycle as _drawItemCache (items are immutable
        // between SetItems calls, so the text never changes within one list generation).
        private readonly Dictionary<int, string> _countTextCache = new();
        // Hover tooltip rebuilt only when the hovered cell / favorite state / alt state changes.
        private int _lastHoverIndex = -1;
        private bool _lastHoverFavorited;
        private bool _lastHoverAlt;
        private string _hoverText;
        private UIScrollbar _scrollbar;
        private int _columns = 8;
        private int _cellSize = 48;
        private Func<int, int, bool> _isFavorited;
        private bool _showFavoriteHint = true;

        // Smooth scroll state (pixel units)
        private float _scrollPixels;     // current rendered position (lerped)
        private float _scrollTarget;     // target position in pixels
        private float _scrollBarLastPos; // last known scrollbar value in pixels, for drag detection

        public event Action<ConsolidatedItem> OnItemClicked;
        public event Action<ConsolidatedItem> OnItemRightClicked;
        public event Action<ConsolidatedItem> OnItemMiddleClicked;
        public event Action<ConsolidatedItem> OnItemAltClicked;

        public void SetShowFavoriteHint(bool show) => _showFavoriteHint = show;

        // Returns the item currently under the mouse cursor, or null if none.
        // Safe to call from Update (does not depend on Draw state).
        public int GetHoveredItemType()
        {
            var ci = GetItemAtMouse(Main.MouseScreen);
            return ci?.ItemType ?? 0;
        }

        public ConsolidatedItem GetHoveredItem()
        {
            return GetItemAtMouse(Main.MouseScreen);
        }

        public void SetItems(List<ConsolidatedItem> items)
        {
            _items = items ?? new List<ConsolidatedItem>();
            _drawItemCache.Clear();
            _countTextCache.Clear();
            _lastHoverIndex = -1;
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

        public void SetFavoriteChecker(Func<int, int, bool> checker)
        {
            _isFavorited = checker;
        }

        private void UpdateScrollbar()
        {
            if (_scrollbar == null)
                return;

            int totalRows = (_items.Count + _columns - 1) / _columns;
            var dims = GetDimensions();
            int visibleRows = (int)(dims.Height / _cellSize);
            _scrollbar.SetView(visibleRows, totalRows);
        }

        public override void ScrollWheel(UIScrollWheelEvent evt)
        {
            base.ScrollWheel(evt);
            if (_scrollbar != null)
            {
                int gridScrollRows = RequisitionClientConfig.GetGridScrollRows();
                _scrollTarget -= evt.ScrollWheelValue / 120f * _cellSize * gridScrollRows;
            }
        }

        public override void Update(GameTime gameTime)
        {
            base.Update(gameTime);
            if (_scrollbar == null) return;

            UpdateScrollbar();

            float barPixels = _scrollbar.ViewPosition * _cellSize;

            // If the scrollbar thumb was dragged externally, snap to it immediately
            if (Math.Abs(barPixels - _scrollBarLastPos) > 0.5f)
            {
                _scrollPixels = barPixels;
                _scrollTarget = barPixels;
            }
            else
            {
                // Clamp target to valid range
                int totalRows = (_items.Count + _columns - 1) / _columns;
                var dims = GetDimensions();
                int visibleRows = (int)(dims.Height / _cellSize);
                float maxPixels = Math.Max(0, (totalRows - visibleRows) * _cellSize);
                _scrollTarget = Math.Clamp(_scrollTarget, 0, maxPixels);

                // Lerp rendered position toward target
                float diff = _scrollTarget - _scrollPixels;
                if (Math.Abs(diff) < 0.5f) _scrollPixels = _scrollTarget;
                else _scrollPixels += diff * 0.15f;
                _scrollPixels = Math.Clamp(_scrollPixels, 0, maxPixels);

                // Sync UIScrollbar so the thumb tracks the rendered position
                _scrollbar.ViewPosition = _scrollPixels / _cellSize;
            }

            _scrollBarLastPos = _scrollbar.ViewPosition * _cellSize;
        }

        public override void LeftClick(UIMouseEvent evt)
        {
            base.LeftClick(evt);
            if (UIClickBlocker.IsConsumed) return;
            var item = GetItemAtMouse(evt.MousePosition);
            if (item == null) return;

            bool alt = Main.keyState.IsKeyDown(Keys.LeftAlt) || Main.keyState.IsKeyDown(Keys.RightAlt);
            if (alt)
                OnItemAltClicked?.Invoke(item);
            else
                OnItemClicked?.Invoke(item);
        }

        public override void RightClick(UIMouseEvent evt)
        {
            base.RightClick(evt);
            if (UIClickBlocker.IsConsumed) return;
            var item = GetItemAtMouse(evt.MousePosition);
            if (item != null)
                OnItemRightClicked?.Invoke(item);
        }

        public override void MiddleClick(UIMouseEvent evt)
        {
            base.MiddleClick(evt);
            if (UIClickBlocker.IsConsumed) return;
            var item = GetItemAtMouse(evt.MousePosition);
            if (item != null)
                OnItemMiddleClicked?.Invoke(item);
        }

        private ConsolidatedItem GetItemAtMouse(Vector2 mousePos)
        {
            var dims = GetDimensions();
            if (!dims.ToRectangle().Contains(mousePos.ToPoint())) return null;

            float relX = mousePos.X - dims.X;
            float relY = mousePos.Y - dims.Y + _scrollPixels;

            int col = (int)(relX / _cellSize);
            int row = (int)(relY / _cellSize);
            int index = row * _columns + col;

            if (index >= 0 && index < _items.Count && col >= 0 && col < _columns)
                return _items[index];

            return null;
        }

        protected override void DrawSelf(SpriteBatch spriteBatch)
        {
            var dims = GetDimensions();

            // Background
            Utils.DrawInvBG(spriteBatch, dims.ToRectangle(), new Color(23, 33, 69) * 0.8f);

            int startRow = (int)(_scrollPixels / _cellSize);
            float yOffset = _scrollPixels % _cellSize;        // sub-row pixel offset
            int rowsToDraw = (int)(dims.Height / _cellSize) + 2; // +2 to fill partial rows

            // Scissor to grid bounds so partial rows at top/bottom clip cleanly
            var savedScissor = spriteBatch.GraphicsDevice.ScissorRectangle;
            spriteBatch.End();
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
                SamplerState.AnisotropicClamp, DepthStencilState.None,
                ScissorRasterizer, null, Main.UIScaleMatrix);
            // Intersect own bounds with the saved (outer) scissor so parent clipping is honoured.
            // Without this, a negative-X grid rect gets GPU-clamped to X=0 and bleeds on screen.
            var ownRect = new Rectangle(
                (int)(dims.X * Main.UIScale), (int)(dims.Y * Main.UIScale),
                (int)(dims.Width * Main.UIScale), (int)(dims.Height * Main.UIScale));
            int ix  = Math.Max(ownRect.X,      savedScissor.X);
            int iy  = Math.Max(ownRect.Y,      savedScissor.Y);
            int ix2 = Math.Min(ownRect.Right,  savedScissor.Right);
            int iy2 = Math.Min(ownRect.Bottom, savedScissor.Bottom);
            spriteBatch.GraphicsDevice.ScissorRectangle = ix2 > ix && iy2 > iy
                ? new Rectangle(ix, iy, ix2 - ix, iy2 - iy)
                : new Rectangle(0, 0, 0, 0);

            for (int row = 0; row < rowsToDraw; row++)
            {
                for (int col = 0; col < _columns; col++)
                {
                    int index = (startRow + row) * _columns + col;
                    float x = dims.X + col * _cellSize;
                    float y = dims.Y + row * _cellSize - yOffset;

                    var cellRect = new Rectangle((int)x, (int)y, _cellSize - 2, _cellSize - 2);

                    Utils.DrawInvBG(spriteBatch, cellRect, new Color(63, 82, 151) * 0.4f);

                    if (index >= 0 && index < _items.Count)
                    {
                        var consolidatedItem = _items[index];
                        bool favorited = _isFavorited != null && _isFavorited(consolidatedItem.ItemType, consolidatedItem.PrefixId);

                        if (favorited)
                            Utils.DrawInvBG(spriteBatch,
                                new Rectangle(cellRect.X - 1, cellRect.Y - 1, cellRect.Width + 2, cellRect.Height + 2),
                                Color.Gold * 0.35f);

                        DrawItem(spriteBatch, consolidatedItem, cellRect, index);

                        if (favorited)
                            Utils.DrawBorderString(spriteBatch, "★",
                                new Vector2(cellRect.X + 2, cellRect.Y + 1), Color.Gold, 0.5f);

                        if (cellRect.Contains(Main.MouseScreen.ToPoint()))
                        {
                            // Tooltip/stat display code can mutate Main.HoverItem's fields.
                            // We must not point Terraria at our cached instance, otherwise
                            // values (e.g. knockback) can accumulate across frames.
                            var hoverItem = GetOrCreateDrawItem(index, consolidatedItem);
                            Main.HoverItem = hoverItem.Clone();

                            bool altHeld = Main.keyState.IsKeyDown(Keys.LeftAlt)
                                        || Main.keyState.IsKeyDown(Keys.RightAlt);
                            // The tooltip string only depends on (cell, favorite, alt); rebuild
                            // it on change instead of concatenating every hovered frame.
                            if (index != _lastHoverIndex || favorited != _lastHoverFavorited || altHeld != _lastHoverAlt)
                            {
                                _lastHoverIndex = index;
                                _lastHoverFavorited = favorited;
                                _lastHoverAlt = altHeld;
                                _hoverText = BuildHoverText(hoverItem, consolidatedItem, favorited, altHeld);
                            }
                            Main.hoverItemName = _hoverText;
                        }
                    }
                }
            }

            // Restore spriteBatch state
            spriteBatch.End();
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
                SamplerState.AnisotropicClamp, DepthStencilState.None,
                RasterizerState.CullNone, null, Main.UIScaleMatrix);
            spriteBatch.GraphicsDevice.ScissorRectangle = savedScissor;
        }

        private string BuildHoverText(Item tooltipItem, ConsolidatedItem ci, bool favorited, bool altHeld)
        {
            if (altHeld)
            {
                var dbg = new StringBuilder();
                dbg.Append(tooltipItem.Name);
                if (_showFavoriteHint)
                    dbg.Append(favorited ? "\nAlt+Click to unfavorite" : "\nAlt+Click to favorite");
                dbg.Append("\n[DEBUG NBT]");
                if (ci.ModData != null)
                    dbg.Append($"\nModData keys: {string.Join(", ", ci.ModData.Select(kvp => kvp.Key))}");
                else
                    dbg.Append("\nModData: null");
                if (ci.FullItemTag != null)
                    dbg.Append($"\nFullItemTag keys: {string.Join(", ", ci.FullItemTag.Select(kvp => kvp.Key))}");
                else
                    dbg.Append("\nFullItemTag: null");
                return dbg.ToString();
            }

            if (_showFavoriteHint)
                return favorited
                    ? tooltipItem.Name + "\nAlt+Click to unfavorite"
                    : tooltipItem.Name + "\nAlt+Click to favorite";

            return tooltipItem.Name;
        }

        private Item GetOrCreateDrawItem(int index, ConsolidatedItem ci)
        {
            if (_drawItemCache.TryGetValue(index, out var cached))
                return cached;

            Item item;
            if (ci.FullItemTag != null)
            {
                item = ItemIO.Load(ci.FullItemTag);
                item.stack = ci.TotalCount;
            }
            else
            {
                item = new Item();
                item.SetDefaults(ci.ItemType);
                if (ci.PrefixId > 0)
                    item.Prefix(ci.PrefixId);
                item.stack = ci.TotalCount;
                if (ci.ModData != null)
                    item.ModItem?.LoadData(ci.ModData);
            }
            _drawItemCache[index] = item;
            return item;
        }

        private void DrawItem(SpriteBatch spriteBatch, ConsolidatedItem ci, Rectangle cellRect, int index)
        {
            var item = GetOrCreateDrawItem(index, ci);

            Main.instance.LoadItem(item.type);
            var texture = TextureAssets.Item[item.type].Value;
            var sourceRect = Main.itemAnimations[item.type] != null
                ? Main.itemAnimations[item.type].GetFrame(texture)
                : texture.Frame();

            float scale = 1f;
            float maxDim = Math.Max(sourceRect.Width, sourceRect.Height);
            if (maxDim > 29)
                scale = 29f / maxDim;

            var center = new Vector2(cellRect.X + cellRect.Width / 2, cellRect.Y + cellRect.Height / 2);
            var origin = new Vector2(sourceRect.Width / 2, sourceRect.Height / 2);

            var drawColor = item.GetAlpha(Color.White);
            var itemColor = item.GetColor(Color.White);
            if (ItemLoader.PreDrawInInventory(item, spriteBatch, center, sourceRect, drawColor, itemColor, origin, scale))
            {
                spriteBatch.Draw(texture, center, sourceRect, drawColor, 0f, origin, scale, SpriteEffects.None, 0f);
            }
            ItemLoader.PostDrawInInventory(item, spriteBatch, center, sourceRect, drawColor, itemColor, origin, scale);

            if (ci.TotalCount > 1)
            {
                if (!_countTextCache.TryGetValue(index, out string countText))
                {
                    countText = ci.TotalCount >= 1000
                        ? $"{ci.TotalCount / 1000f:0.#}k"
                        : ci.TotalCount.ToString();
                    _countTextCache[index] = countText;
                }

                Utils.DrawBorderString(spriteBatch, countText,
                    new Vector2(cellRect.Right - 4, cellRect.Bottom - 4),
                    Color.White, 0.7f, 1f, 1f);
            }
        }
    }
}
