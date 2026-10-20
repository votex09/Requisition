using System.Collections.Generic;
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.UI;
using TerraStorage.Systems;

namespace TerraStorage.Content.UI
{
    public class FavoritedRecipesPanelSystem : ModSystem
    {
        private UserInterface _ui;
        private UIFavoritedRecipesPanel _panel;
        private bool _visible;
        private bool _wasVisibleBeforeClose;

        // Toggle button state
        private float _btnX = 420f;
        private float _btnY = 36f;
        private bool _btnMiddleDragging;
        private Vector2 _btnDragOffset;
        private bool _prevMiddle;
        private bool _prevLeft;
        private bool _prevHovered;
        private const float BtnSize = 26f;
        private const string BtnKey = "favbutton";

        public static FavoritedRecipesPanelSystem Instance => ModContent.GetInstance<FavoritedRecipesPanelSystem>();

        public override void Load()
        {
            if (Main.dedServ) return;

            _panel = new UIFavoritedRecipesPanel();
            _panel.Activate();
            _ui    = new UserInterface();

            if (UIPositionStore.TryGet(BtnKey, out float bx, out float by))
            {
                _btnX = bx;
                _btnY = by;
            }

            if (UIPositionStore.TryGet("favpanel", out float px, out float py))
            {
                _panel.PanelLeft = px;
                _panel.PanelTop  = py;
            }

            RequisitionWindows.Register(
                "TerraStorage: Favorited Recipes Panel",
                isOpen: () => _visible,
                isMouseOver: () => _panel.IsMouseOverPanel(),
                update: gameTime => _ui.Update(gameTime),
                draw: DrawPanel);

            // A free-floating button, not part of any panel, and live on every frame the inventory
            // is open. It gets its own z-entry so clicks on it stop at Requisition -- but note it
            // must NOT hide the vanilla crafting menu, or that menu would vanish permanently.
            RequisitionWindows.Register(
                "TerraStorage: Favorites Toggle Button",
                isOpen: () => Main.playerInventory,
                isMouseOver: IsMouseOverButton,
                update: _ => UpdateButton(),
                draw: DrawToggleButton);
        }

        private bool IsMouseOverButton()
        {
            var btnRect = new Rectangle((int)_btnX, (int)_btnY, (int)BtnSize, (int)BtnSize);
            return btnRect.Contains(Main.MouseScreen.ToPoint()) || _btnMiddleDragging;
        }

        private bool DrawPanel()
        {
            if (_panel.IsMouseOverPanel())
            {
                Main.HoverItem = new Item();
                Main.hoverItemName = string.Empty;
            }
            _ui.Draw(Main.spriteBatch, new GameTime());
            return true;
        }

        public void SetDiskIds(List<Guid> ids) => _panel?.SetDiskIds(ids);

        public void ShowPanel()
        {
            _visible = true;
            _ui.SetState(_panel);
        }

        public void HidePanel()
        {
            _visible = false;
            _ui.SetState(null);
        }

        public void TogglePanel()
        {
            if (_visible)
            {
                _wasVisibleBeforeClose = false;
                HidePanel();
            }
            else
            {
                ShowPanel();
            }
            SoundEngine.PlaySound(SoundID.MenuTick);
        }

        public override void OnWorldUnload()
        {
            // The panel and its caches live for the whole session; the next world/character can
            // present the same version stamps, so staleness must be forced out here.
            _panel?.ResetCaches();
        }

        public override void OnWorldLoad()
        {
            // Symmetric reset: an abrupt multiplayer disconnect may skip OnWorldUnload, and
            // world-load is the one hook guaranteed to run before anything draws in the new world.
            _panel?.ResetCaches();
        }

        public override void UpdateUI(GameTime gameTime)
        {
            if (Main.dedServ) return;

            bool inventoryOpen = Main.playerInventory;
            bool pinned = _panel?.IsPinned ?? false;

            if (!inventoryOpen && !pinned && _visible)
            {
                _wasVisibleBeforeClose = true;
                HidePanel();
            }
            else if (inventoryOpen && _wasVisibleBeforeClose && !_visible && !pinned)
            {
                _wasVisibleBeforeClose = false;
                ShowPanel();
            }
        }

        private void UpdateButton()
        {
            bool middleDown = Main.mouseMiddle;
            bool middleJustDown = middleDown && !_prevMiddle;
            bool leftDown = Main.mouseLeft;
            bool leftJustDown = leftDown && !_prevLeft;
            _prevMiddle = UIClickBlocker.RealMouseMiddle;
            _prevLeft   = UIClickBlocker.RealMouseLeft;

            if (_btnMiddleDragging)
            {
                UIClickBlocker.MarkGesture();

                // Claimed on every drag frame, not just the press: this block returns early, so
                // without it the drag frames stopped claiming and a middle-drag across the Terminal
                // grid ended up toggling a favourite on whatever item it was released over.
                UIClickBlocker.ClaimIfPressed(true, Main.mouseLeft, Main.mouseRight, Main.mouseMiddle);

                if (!UIClickBlocker.RealMouseMiddle)
                {
                    _btnMiddleDragging = false;
                    UIPositionStore.Save(BtnKey, _btnX, _btnY);
                }
                else
                {
                    _btnX = Main.MouseScreen.X - _btnDragOffset.X;
                    _btnY = Main.MouseScreen.Y - _btnDragOffset.Y;
                    _btnX = Math.Clamp(_btnX, 0, Main.screenWidth  - BtnSize);
                    _btnY = Math.Clamp(_btnY, 0, Main.screenHeight - BtnSize);
                    Main.LocalPlayer.mouseInterface = true;
                }
                return;
            }

            var btnRect = new Rectangle((int)_btnX, (int)_btnY, (int)BtnSize, (int)BtnSize);
            if (!btnRect.Contains(Main.MouseScreen.ToPoint())) return;

            Main.LocalPlayer.mouseInterface = true;

            if (middleJustDown && !UIClickBlocker.IsConsumed)
            {
                _btnMiddleDragging = true;
                UIClickBlocker.MarkGesture();
                _btnDragOffset = Main.MouseScreen - new Vector2(_btnX, _btnY);
            }
            else if (leftJustDown && !UIClickBlocker.IsConsumed)
            {
                TogglePanel();
            }

            // Whatever the button: the button is the topmost thing under the cursor, so nothing
            // beneath it should also act on this press.
            UIClickBlocker.ClaimIfPressed(true, Main.mouseLeft, Main.mouseRight, Main.mouseMiddle);
        }

        private bool DrawToggleButton()
        {
            var sb = Main.spriteBatch;
            var btnRect = new Rectangle((int)_btnX, (int)_btnY, (int)BtnSize, (int)BtnSize);
            bool hovered = btnRect.Contains(Main.MouseScreen.ToPoint()) || _btnMiddleDragging;

            if (hovered && !_prevHovered)
                SoundEngine.PlaySound(SoundID.MenuTick);
            _prevHovered = hovered;

            if (hovered)
            {
                Color glow = _visible ? new Color(255, 200, 50) : new Color(120, 150, 255);
                UIDrawHelpers.DrawSolidRect(sb, new Rectangle(btnRect.X - 1, btnRect.Y - 1, btnRect.Width + 2, 1), glow);
                UIDrawHelpers.DrawSolidRect(sb, new Rectangle(btnRect.X - 1, btnRect.Bottom,     btnRect.Width + 2, 1), glow);
                UIDrawHelpers.DrawSolidRect(sb, new Rectangle(btnRect.X - 1, btnRect.Y - 1, 1, btnRect.Height + 2), glow);
                UIDrawHelpers.DrawSolidRect(sb, new Rectangle(btnRect.Right,  btnRect.Y - 1, 1, btnRect.Height + 2), glow);
            }

            const string star = "★";
            const float textScale = 0.9f;
            var font = Terraria.GameContent.FontAssets.MouseText.Value;
            var textSize = font.MeasureString(star) * textScale;
            var textPos = new Vector2(
                _btnX + BtnSize / 2f - textSize.X / 2f,
                _btnY + BtnSize / 2f - textSize.Y / 2f + 4f);
            Color starColor = _visible ? Color.Gold : (hovered ? Color.White : Color.White * 0.6f);
            Utils.DrawBorderString(sb, star, textPos, starColor, textScale);

            if (hovered)
                Main.hoverItemName = _visible
                    ? "Close Favorited Recipes\nMiddle-click to move"
                    : "Open Favorited Recipes\nMiddle-click to move";

            return true;
        }
    }
}
