using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.GameContent.UI.Elements;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.Localization;
using Terraria.UI;
using TerraStorage.Common;
using TerraStorage.Content.Items;
using TerraStorage.Systems;

namespace TerraStorage.Content.UI
{
    // UIState for the Disk Recovery sub-panel opened from the Drive Bay UI.
    // Displays all known <see cref="DiskData"/> entries from <see cref="StorageWorldSystem"/>,
    // lets the player select a lost disk, insert a blank replacement disk of matching tier,
    // and restore the lost disk's GUID onto the replacement item so its stored items are
    // accessible again.
    public class DiskRecoveryUIState : UIState
    {
        // Layout constants (relative to panel inner area)
        private const int ListWidth = 255;
        private const int DetailX = 265;
        private const int MainAreaTop = 30;
        private const int MainAreaHeight = 360;
        private const int EntryHeight = 48;
        private const int ItemRowHeight = 26;

        private UIPanel _panel;
        private UIText _title;
        private UITextPanel<string> _restoreBtn;
        private UIText _statusText;

        private List<DiskData> _diskList = new();
        private int _diskScrollOffset;
        private DiskData _selectedDisk;
        private int _itemScrollOffset;

        // The blank replacement disk the player places into the recovery slot
        private Item _replacementDisk = new Item();

        private bool _prevMouseLeft;
        private long _lastStorageVersion = -1;
        private bool _dragging;
        private Vector2 _dragOffset;

        public override void OnInitialize()
        {
            _panel = new UIPanel();
            _panel.Width.Set(720, 0f);
            _panel.Height.Set(510, 0f);
            _panel.HAlign = 0.5f;
            _panel.VAlign = 0.4f;
            _panel.SetPadding(10);
            Append(_panel);

            _title = new UIText("");
            _title.HAlign = 0.5f;
            _panel.Append(_title);

            var closeBtn = new UITextPanel<string>("X", 0.7f);
            closeBtn.Width.Set(30, 0f);
            closeBtn.Height.Set(24, 0f);
            closeBtn.Left.Set(-30, 1f);
            closeBtn.Top.Set(-6, 0f);
            closeBtn.OnLeftClick += (_, _) =>
                ModContent.GetInstance<DriveBayUISystem>().CloseDiskRecovery();
            _panel.Append(closeBtn);

            // Restore button positioned in the bottom section
            _restoreBtn = new UITextPanel<string>("", 0.75f);
            _restoreBtn.Width.Set(90, 0f);
            _restoreBtn.Height.Set(30, 0f);
            _restoreBtn.Left.Set(60, 0f);
            _restoreBtn.Top.Set(MainAreaTop + MainAreaHeight + 32, 0f);
            _restoreBtn.OnLeftClick += OnRestoreClicked;
            _panel.Append(_restoreBtn);

            _statusText = new UIText("", 0.72f);
            _statusText.Left.Set(0, 0f);
            _statusText.Top.Set(MainAreaTop + MainAreaHeight + 72, 0f);
            _panel.Append(_statusText);
        }

        //Called when the recovery UI is opened. Resets all state.
        public bool IsMouseOverPanel() => _panel?.ContainsPoint(Main.MouseScreen) == true;

        public void Open()
        {
            _title?.SetText(Language.GetTextValue("Mods.TerraStorage.UI.DiskRecovery.Title"));
            _restoreBtn?.SetText(Language.GetTextValue("Mods.TerraStorage.UI.DiskRecovery.Restore"));
            RefreshDiskList();
            _selectedDisk = null;
            _diskScrollOffset = 0;
            _itemScrollOffset = 0;
            if (_replacementDisk == null) _replacementDisk = new Item();
            _replacementDisk.TurnToAir();
            _statusText?.SetText("");
        }

        //Returns the replacement disk to the player if they close without restoring.
        public void ReturnDisk()
        {
            if (_replacementDisk == null || _replacementDisk.IsAir)
                return;

            Main.LocalPlayer.GetItem(Main.myPlayer, _replacementDisk,
                GetItemSettings.InventoryEntityToPlayerInventorySettings);

            // GetItem modifies _replacementDisk in-place; if it is still non-air the
            // inventory was full and the item wasn't placed — put it in the cursor instead.
            if (!_replacementDisk.IsAir)
            {
                if (Main.mouseItem == null || Main.mouseItem.IsAir)
                    Main.mouseItem = _replacementDisk.Clone();
                else
                    Main.LocalPlayer.QuickSpawnItem(
                        Main.LocalPlayer.GetSource_FromThis(), _replacementDisk, _replacementDisk.stack);
            }

            _replacementDisk.TurnToAir();
        }

        // Reloads the disk list from <see cref="StorageWorldSystem"/>, sorted by fullest
        // first so the most likely candidates for recovery appear at the top. 
        private void RefreshDiskList()
        {
            var sys = StorageWorldSystem.Instance;
            // Only show disks that actually contain items — empty entries have nothing to
            // recover and are purged from world data on load anyway.
            // Primary sort: most-used stacks first (lost disks are usually non-empty).
            // Secondary sort: ascending tier so lower-tier disks appear before higher ones
            // when stack usage is equal.
            _diskList = sys?.GetAllDiskData()
                .Where(d => d.UsedStacks > 0)
                .OrderByDescending(d => d.UsedStacks)
                .ThenBy(d => (int)d.Tier)
                .ToList() ?? new List<DiskData>();
        }

        private void OnRestoreClicked(UIMouseEvent evt, UIElement el)
        {
            if (_selectedDisk == null)
            {
                _statusText.SetText($"[c/FF8888:{Language.GetTextValue("Mods.TerraStorage.UI.DiskRecovery.SelectFirst")}]");
                return;
            }
            if (_replacementDisk == null || _replacementDisk.IsAir ||
                _replacementDisk.ModItem is not StorageDiskBase repDisk)
            {
                _statusText.SetText($"[c/FF8888:{Language.GetTextValue("Mods.TerraStorage.UI.DiskRecovery.NeedReplacementDisk")}]");
                return;
            }
            if (repDisk.Tier != _selectedDisk.Tier)
            {
                _statusText.SetText($"[c/FF8888:{Language.GetText("Mods.TerraStorage.UI.DiskRecovery.TierMismatch").Format(_selectedDisk.Tier.GetName())}]");
                return;
            }

            var newId = Guid.NewGuid();

            if (Main.netMode == NetmodeID.MultiplayerClient)
            {
                // Server will remap the data and broadcast to all clients.
                var mod = ModLoader.GetMod("TerraStorage");
                NetworkHandler.SendRestoreDiskRequest(mod, _selectedDisk.DiskId, repDisk.DiskId, newId);
            }
            else
            {
                var sys = StorageWorldSystem.Instance;
                if (sys == null) return;

                // Clean up the replacement disk's old entry if it is empty.
                if (repDisk.DiskId != Guid.Empty)
                {
                    var existingData = sys.GetDiskData(repDisk.DiskId);
                    if (existingData == null || existingData.UsedStacks == 0)
                        sys.RemoveDiskData(repDisk.DiskId);
                }

                sys.RemapDiskData(_selectedDisk.DiskId, newId);
            }

            // Assign the new GUID to the replacement disk item and give it to the player.
            // (Done client-side in both SP and MP — the item is purely local at this point.)
            repDisk.AssignDiskId(newId);

            // Hand the recovered disk back to the player
            var restored = _replacementDisk.Clone();
            Main.LocalPlayer.GetItem(Main.myPlayer, restored,
                GetItemSettings.InventoryEntityToPlayerInventorySettings);
            _replacementDisk.TurnToAir();

            _selectedDisk = null;
            _itemScrollOffset = 0;
            RefreshDiskList();
            _statusText.SetText($"[c/88FF88:{Language.GetTextValue("Mods.TerraStorage.UI.DiskRecovery.DiskRestored")}]");
            SoundEngine.PlaySound(SoundID.Item37);
        }

        // -------------------------------------------------------------------------
        // Drawing
        // -------------------------------------------------------------------------

        public override void Draw(SpriteBatch spriteBatch)
        {
            UIDrawHelpers.DrawPanelUnderlay(spriteBatch, _panel);
            base.Draw(spriteBatch);
            if (_panel == null) return;

            var inner = _panel.GetInnerDimensions();
            float px = inner.X;
            float py = inner.Y;

            DrawDiskList(spriteBatch, px, py + MainAreaTop);
            DrawDiskDetail(spriteBatch, px + DetailX, py + MainAreaTop, inner.Width - DetailX);
            DrawBottomSection(spriteBatch, px, py + MainAreaTop + MainAreaHeight + 8);
        }

        private void DrawDiskList(SpriteBatch spriteBatch, float lx, float ly)
        {
            var bgRect = new Rectangle((int)lx, (int)ly, ListWidth, MainAreaHeight);
            Utils.DrawInvBG(spriteBatch, bgRect, new Color(23, 33, 69) * 0.85f);
            Utils.DrawBorderString(spriteBatch, Language.GetTextValue("Mods.TerraStorage.UI.DiskRecovery.KnownDisks"), new Vector2(lx + 6, ly + 4), Color.White, 0.8f);

            int entryAreaTop = (int)(ly + 22);
            int entryAreaH = MainAreaHeight - 22;
            int maxVisible = entryAreaH / EntryHeight;
            int maxOffset = Math.Max(0, _diskList.Count - maxVisible);
            _diskScrollOffset = Math.Clamp(_diskScrollOffset, 0, maxOffset);

            for (int i = 0; i < maxVisible && i + _diskScrollOffset < _diskList.Count; i++)
            {
                var disk = _diskList[i + _diskScrollOffset];
                int ey = entryAreaTop + i * EntryHeight;
                bool selected = _selectedDisk?.DiskId == disk.DiskId;

                var entryRect = new Rectangle((int)lx + 2, ey + 1, ListWidth - 4, EntryHeight - 2);
                Utils.DrawInvBG(spriteBatch, entryRect,
                    selected ? new Color(100, 150, 210) * 0.7f : new Color(40, 55, 100) * 0.5f);

                // Tier color bar
                var tc = disk.Tier.GetColor();
                spriteBatch.Draw(TextureAssets.MagicPixel.Value,
                    new Rectangle((int)lx + 5, ey + 6, 8, EntryHeight - 12), tc);

                // Short ID
                string sid = disk.DiskId.ToString()[..8] + "...";
                Utils.DrawBorderString(spriteBatch, sid, new Vector2(lx + 18, ey + 4), Color.White, 0.72f);

                // Tier name
                Utils.DrawBorderString(spriteBatch, disk.Tier.GetName(),
                    new Vector2(lx + 18, ey + 20), tc, 0.68f);

                // Stack usage
                string usage = $"{disk.UsedStacks}/{disk.MaxStacks} stacks";
                var usageColor = disk.IsFull ? Color.OrangeRed : (disk.UsedStacks > 0 ? Color.LightGreen : Color.Gray);
                Utils.DrawBorderString(spriteBatch, usage, new Vector2(lx + 18, ey + 34), usageColor, 0.62f);

                if (entryRect.Contains(Main.MouseScreen.ToPoint()))
                    Main.LocalPlayer.mouseInterface = true;
            }

            if (_diskList.Count == 0)
            {
                Utils.DrawBorderString(spriteBatch, Language.GetTextValue("Mods.TerraStorage.UI.DiskRecovery.NoDiskData"),
                    new Vector2(lx + 6, ly + MainAreaHeight / 2f - 8), Color.Gray, 0.75f);
            }
            else if (_diskList.Count > maxVisible)
            {
                int lo = _diskScrollOffset + 1;
                int hi = Math.Min(_diskScrollOffset + maxVisible, _diskList.Count);
                Utils.DrawBorderString(spriteBatch, Language.GetText("Mods.TerraStorage.UI.DiskRecovery.SlotRange").Format(lo, hi, _diskList.Count),
                    new Vector2(lx + 4, ly + MainAreaHeight - 14), Color.Gray, 0.6f);
            }
        }

        private void DrawDiskDetail(SpriteBatch spriteBatch, float dx, float dy, float dw)
        {
            var bgRect = new Rectangle((int)dx, (int)dy, (int)dw, MainAreaHeight);
            Utils.DrawInvBG(spriteBatch, bgRect, new Color(23, 33, 69) * 0.85f);

            if (_selectedDisk == null)
            {
                Utils.DrawBorderString(spriteBatch, Language.GetTextValue("Mods.TerraStorage.UI.DiskRecovery.SelectDisk"),
                    new Vector2(dx + 8, dy + 8), Color.Gray, 0.8f);
                return;
            }

            // Header
            var tc = _selectedDisk.Tier.GetColor();
            string idStr = "ID: " + _selectedDisk.DiskId.ToString()[..18] + "...";
            string infoStr = $"Tier: {_selectedDisk.Tier.GetName()}   {_selectedDisk.UsedStacks}/{_selectedDisk.MaxStacks} stacks";
            Utils.DrawBorderString(spriteBatch, idStr, new Vector2(dx + 6, dy + 4), Color.White, 0.7f);
            Utils.DrawBorderString(spriteBatch, infoStr, new Vector2(dx + 6, dy + 20), tc, 0.7f);

            int itemAreaTop = (int)(dy + 40);
            int itemAreaH = MainAreaHeight - 40;
            int maxVisible = itemAreaH / ItemRowHeight;
            int maxOffset = Math.Max(0, _selectedDisk.Items.Count - maxVisible);
            _itemScrollOffset = Math.Clamp(_itemScrollOffset, 0, maxOffset);

            if (_selectedDisk.Items.Count == 0)
            {
                Utils.DrawBorderString(spriteBatch, Language.GetTextValue("Mods.TerraStorage.UI.DiskRecovery.NoItemsOnDisk"),
                    new Vector2(dx + 8, itemAreaTop + 6), Color.Gray, 0.75f);
                return;
            }

            for (int i = 0; i < maxVisible && i + _itemScrollOffset < _selectedDisk.Items.Count; i++)
            {
                var stored = _selectedDisk.Items[i + _itemScrollOffset];
                int iy = itemAreaTop + i * ItemRowHeight;

                if (i % 2 == 0)
                    spriteBatch.Draw(TextureAssets.MagicPixel.Value,
                        new Rectangle((int)dx + 2, iy, (int)dw - 4, ItemRowHeight - 1),
                        new Color(35, 50, 90) * 0.5f);

                // Item icon
                Main.instance.LoadItem(stored.ItemType);
                var tex = TextureAssets.Item[stored.ItemType].Value;
                var srcRect = Main.itemAnimations[stored.ItemType] != null
                    ? Main.itemAnimations[stored.ItemType].GetFrame(tex)
                    : tex.Frame();
                float iconScale = Math.Min(1f, 20f / Math.Max(srcRect.Width, srcRect.Height));
                spriteBatch.Draw(tex, new Vector2(dx + 14, iy + ItemRowHeight / 2f),
                    srcRect, Color.White, 0f,
                    new Vector2(srcRect.Width / 2f, srcRect.Height / 2f),
                    iconScale, SpriteEffects.None, 0f);

                // Item name + count
                var tempItem = new Item();
                tempItem.SetDefaults(stored.ItemType);
                string name = tempItem.Name;
                if (stored.PrefixId > 0)
                {
                    tempItem.Prefix(stored.PrefixId);
                    name = tempItem.AffixName();
                }
                Utils.DrawBorderString(spriteBatch, $"{name}  \xd7{stored.Stack}",
                    new Vector2(dx + 28, iy + 4), Color.White, 0.7f);
            }

            if (_selectedDisk.Items.Count > maxVisible)
            {
                int lo = _itemScrollOffset + 1;
                int hi = Math.Min(_itemScrollOffset + maxVisible, _selectedDisk.Items.Count);
                Utils.DrawBorderString(spriteBatch,
                    Language.GetText("Mods.TerraStorage.UI.DiskRecovery.SlotRange").Format(lo, hi, _selectedDisk.Items.Count),
                    new Vector2(dx + 4, dy + MainAreaHeight - 14), Color.Gray, 0.6f);
            }
        }

        private void DrawBottomSection(SpriteBatch spriteBatch, float bx, float by)
        {
            Utils.DrawBorderString(spriteBatch,
                Language.GetTextValue("Mods.TerraStorage.UI.DiskRecovery.PlaceBlankDisk"),
                new Vector2(bx, by), Color.White, 0.78f);

            var slotRect = GetSlotRect();
            Utils.DrawInvBG(spriteBatch, slotRect, new Color(63, 82, 151) * 0.7f);

            if (_replacementDisk != null && !_replacementDisk.IsAir)
                DrawItemInSlot(spriteBatch, _replacementDisk, slotRect);

            if (slotRect.Contains(Main.MouseScreen.ToPoint()))
            {
                Main.LocalPlayer.mouseInterface = true;
                if (_replacementDisk != null && !_replacementDisk.IsAir)
                {
                    Main.HoverItem = _replacementDisk.Clone();
                    Main.hoverItemName = _replacementDisk.Name;
                }
                else
                {
                    Main.hoverItemName = "Replacement Disk Slot";
                }
            }
        }

        private Rectangle GetSlotRect()
        {
            if (_panel == null) return Rectangle.Empty;
            var inner = _panel.GetInnerDimensions();
            return new Rectangle(
                (int)inner.X,
                (int)(inner.Y + MainAreaTop + MainAreaHeight + 22),
                48, 48);
        }

        private static void DrawItemInSlot(SpriteBatch spriteBatch, Item item, Rectangle rect)
        {
            Main.instance.LoadItem(item.type);
            var tex = TextureAssets.Item[item.type].Value;
            var srcRect = Main.itemAnimations[item.type] != null
                ? Main.itemAnimations[item.type].GetFrame(tex)
                : tex.Frame();
            float scale = Math.Min(1f, 36f / Math.Max(srcRect.Width, srcRect.Height));
            spriteBatch.Draw(tex,
                new Vector2(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f),
                srcRect, Color.White, 0f,
                new Vector2(srcRect.Width / 2f, srcRect.Height / 2f),
                scale, SpriteEffects.None, 0f);
        }

        // -------------------------------------------------------------------------
        // Input handling
        // -------------------------------------------------------------------------

        public override void Update(GameTime gameTime)
        {
            base.Update(gameTime);
            if (_panel == null) return;

            var sys = StorageWorldSystem.Instance;
            if (sys != null && sys.StorageVersion != _lastStorageVersion)
            {
                _lastStorageVersion = sys.StorageVersion;
                RefreshDiskList();
            }

            if (_panel.ContainsPoint(Main.MouseScreen))
                Main.LocalPlayer.mouseInterface = true;

            bool justClicked = Main.mouseLeft && !_prevMouseLeft && !UIClickBlocker.IsConsumed;
            _prevMouseLeft = UIClickBlocker.RealMouseLeft;

            // Claim the frame's click whatever the button was: a right- or middle-click must stop
            // at this window too, or every window beneath it acts on the same press.
            UIClickBlocker.ClaimIfPressed(_panel.ContainsPoint(Main.MouseScreen),
                Main.mouseLeft, Main.mouseRight, Main.mouseMiddle);

            if (justClicked && _panel.ContainsPoint(Main.MouseScreen))
                UIClickBlocker.Consume();

            // Dragging
            if (_dragging)
            {
                UIClickBlocker.MarkGesture();
                if (!UIClickBlocker.RealMouseLeft)
                    _dragging = false;
                else
                {
                    _panel.Left.Set(Main.MouseScreen.X - _dragOffset.X, 0f);
                    _panel.Top.Set(Main.MouseScreen.Y - _dragOffset.Y, 0f);
                    Recalculate();
                }
                return;
            }

            var inner = _panel.GetInnerDimensions();
            float px = inner.X;
            float py = inner.Y;
            float lx = px;
            float ly = py + MainAreaTop;
            float dx = px + DetailX;
            float dy = py + MainAreaTop;

            // Mouse wheel scrolling — positive delta = wheel up = scroll list up (offset -1).
            int scrollDelta = PlayerInput.ScrollWheelDeltaForUI;
            if (scrollDelta != 0)
            {
                int dir = scrollDelta > 0 ? -1 : 1;
                int entryAreaH = MainAreaHeight - 22;
                int listMaxVisible = entryAreaH / EntryHeight;
                int itemMaxVisible = (MainAreaHeight - 40) / ItemRowHeight;

                var listRect = new Rectangle((int)lx, (int)ly, ListWidth, MainAreaHeight);
                var detailRect = new Rectangle((int)dx, (int)dy, (int)(inner.Width - DetailX), MainAreaHeight);

                if (listRect.Contains(Main.MouseScreen.ToPoint()))
                {
                    _diskScrollOffset = Math.Clamp(_diskScrollOffset + dir, 0,
                        Math.Max(0, _diskList.Count - listMaxVisible));
                }
                else if (_selectedDisk != null && detailRect.Contains(Main.MouseScreen.ToPoint()))
                {
                    int gridScrollRows = RequisitionClientConfig.GetGridScrollRows();
                    _itemScrollOffset = Math.Clamp(_itemScrollOffset + dir * gridScrollRows, 0,
                        Math.Max(0, _selectedDisk.Items.Count - itemMaxVisible));
                }
            }

            if (justClicked && _panel.ContainsPoint(Main.MouseScreen))
            {
                var dims = _panel.GetDimensions();
                // Drag by title bar
                if (Main.MouseScreen.Y < dims.Y + 30)
                {
                    _dragging = true;
                    UIClickBlocker.MarkGesture();
                    _panel.HAlign = 0f;
                    _panel.VAlign = 0f;
                    _panel.Left.Set(dims.X, 0f);
                    _panel.Top.Set(dims.Y, 0f);
                    _dragOffset = Main.MouseScreen - new Vector2(dims.X, dims.Y);
                    Recalculate();
                }
                else
                {
                    HandleClick(Main.MouseScreen);
                }
            }
        }

        private void HandleClick(Vector2 mousePos)
        {
            var inner = _panel.GetInnerDimensions();
            float lx = inner.X;
            float ly = inner.Y + MainAreaTop;

            // Disk list entry clicks
            int entryAreaTop = (int)(ly + 22);
            int entryAreaH = MainAreaHeight - 22;
            int maxVisible = entryAreaH / EntryHeight;
            var listRect = new Rectangle((int)lx, entryAreaTop, ListWidth, entryAreaH);

            if (listRect.Contains(mousePos.ToPoint()))
            {
                int relY = (int)(mousePos.Y - entryAreaTop);
                int idx = relY / EntryHeight + _diskScrollOffset;
                if (idx >= 0 && idx < _diskList.Count)
                {
                    _selectedDisk = _diskList[idx];
                    _itemScrollOffset = 0;
                    SoundEngine.PlaySound(SoundID.MenuTick);
                }
                return;
            }

            // Replacement disk slot
            var slotRect = GetSlotRect();
            if (slotRect.Contains(mousePos.ToPoint()))
            {
                var cursor = Main.mouseItem;
                if (cursor != null && !cursor.IsAir && cursor.ModItem is StorageDiskBase &&
                    (_replacementDisk == null || _replacementDisk.IsAir))
                {
                    _replacementDisk = cursor.Clone();
                    Main.mouseItem.TurnToAir();
                    SoundEngine.PlaySound(SoundID.Grab);
                }
                else if ((cursor == null || cursor.IsAir) &&
                         _replacementDisk != null && !_replacementDisk.IsAir)
                {
                    Main.mouseItem = _replacementDisk.Clone();
                    _replacementDisk.TurnToAir();
                    SoundEngine.PlaySound(SoundID.Grab);
                }
            }
        }
    }
}
