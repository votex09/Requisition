using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.GameContent.UI.Elements;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.Localization;
using Terraria.UI;
using TerraStorage.Content.Items;
using TerraStorage.Content.Tiles;
using TerraStorage.Content.UI.Elements;
using TerraStorage.Systems;

namespace TerraStorage.Content.UI
{
    // UIState for the Drive Bay (Storage Block) disk-management panel.
    // Renders a grid of disk slots and handles left-click insertion/extraction
    // and shift+click quick-transfer from the player's inventory.
    public class DriveBayUIState : UIState
    {
        private const int Columns = 10;
        private const int Rows = 4;
        private const int SlotSize = 48;
        private const int SlotPadding = 4;

        private TSWindowElement _panel;
        private DriveBayEntity _entity;

        private bool _prevMouseLeft;

        public bool IsMouseOverPanel() => _panel?.ContainsPoint(Main.MouseScreen) == true;

        public DriveBayEntity Entity => _entity;

        public void SetEntity(DriveBayEntity entity)
        {
            _entity = entity;
        }

        public override void OnInitialize()
        {
            _panel = new TSWindowElement
            {
                StoreKey    = "drivebay",
                HasTitleBar = false,
                Resizable   = false,
            };
            _panel.Width.Set(Columns * (SlotSize + SlotPadding) + 28, 0f);
            _panel.Height.Set(Rows * (SlotSize + SlotPadding) + 60, 0f);
            _panel.HAlign = 0.5f;
            _panel.VAlign = 0.4f;
            _panel.GetDragZone = mouse =>
            {
                var d = _panel.GetDimensions();
                return new Rectangle((int)d.X, (int)d.Y, (int)d.Width, 26).Contains(mouse.ToPoint());
            };
            _panel.SetPadding(10);
            Append(_panel);

            var title = new UIText(Language.GetText("Mods.TerraStorage.UI.DriveBay.Title"));            
            title.HAlign = 0.5f;
            _panel.Append(title);

            var closeBtn = new TSCloseButton(() => ModContent.GetInstance<DriveBayUISystem>().CloseDriveBay());
            closeBtn.Left.Set(-26, 1f);
            closeBtn.Top.Set(-4, 0f);
            _panel.Append(closeBtn);

            var recoverBtn = new UITextPanel<Terraria.Localization.LocalizedText>(Language.GetText("Mods.TerraStorage.UI.DriveBay.Recover"), 0.65f);
            recoverBtn.Width.Set(80, 0f);
            recoverBtn.Height.Set(22, 0f);
            recoverBtn.Left.Set(0, 0f);
            recoverBtn.Top.Set(-4, 0f);
            recoverBtn.OnLeftClick += (evt, el) =>
                ModContent.GetInstance<DriveBayUISystem>().OpenDiskRecovery();
            _panel.Append(recoverBtn);
        }

        public override void Draw(SpriteBatch spriteBatch)
        {
            base.Draw(spriteBatch);

            if (_entity == null || _panel == null)
                return;

            _entity.EnsureSlotsInitialized();
            var panelDims = _panel.GetInnerDimensions();
            float startX = panelDims.X + 4;
            float startY = panelDims.Y + 40;

            for (int i = 0; i < DriveBayEntity.DiskSlotCount; i++)
            {
                int col = i % Columns;
                int row = i / Columns;
                var pos = new Vector2(startX + col * (SlotSize + SlotPadding), startY + row * (SlotSize + SlotPadding));

                var slotRect = new Rectangle((int)pos.X, (int)pos.Y, SlotSize, SlotSize);
                Utils.DrawInvBG(spriteBatch, slotRect, new Color(63, 82, 151) * 0.7f);

                var item = _entity.DiskSlots[i];
                if (item != null && !item.IsAir)
                    DrawItemInSlot(spriteBatch, item, slotRect);

                if (slotRect.Contains(Main.MouseScreen.ToPoint()))
                {
                    Main.LocalPlayer.mouseInterface = true;
                    if (item != null && !item.IsAir)
                    {
                        Main.HoverItem = item.Clone();
                        Main.hoverItemName = item.Name;
                    }
                    else
                    {
                        Main.hoverItemName = "Empty Disk Slot";
                    }
                }
            }
        }

        public override void Update(GameTime gameTime)
        {
            base.Update(gameTime);

            if (_entity == null || _panel == null)
                return;

            if (_panel.ContainsPoint(Main.MouseScreen))
                Main.LocalPlayer.mouseInterface = true;

            bool shift = Main.keyState.IsKeyDown(Keys.LeftShift) || Main.keyState.IsKeyDown(Keys.RightShift);

            bool justClicked = Main.mouseLeft && !_prevMouseLeft && !UIClickBlocker.IsConsumed;
            _prevMouseLeft = UIClickBlocker.RealMouseLeft;

            // Claim the frame's click whatever the button was: a right- or middle-click must stop
            // at this window too, or every window beneath it acts on the same press.
            UIClickBlocker.ClaimIfPressed(_panel.ContainsPoint(Main.MouseScreen),
                Main.mouseLeft, Main.mouseRight, Main.mouseMiddle);

            if (justClicked)
            {
                if (_panel.ContainsPoint(Main.MouseScreen))
                {
                    UIClickBlocker.Consume();
                    var dims = _panel.GetDimensions();
                    if (Main.MouseScreen.Y >= dims.Y + 26)
                        HandleSlotClick(Main.MouseScreen, shift);
                }
            }
        }

        private void HandleSlotClick(Vector2 mousePos, bool shift)
        {
            if (_entity == null)
                return;

            _entity.EnsureSlotsInitialized();
            var panelDims = _panel.GetInnerDimensions();
            float startX = panelDims.X + 4;
            float startY = panelDims.Y + 40;

            for (int i = 0; i < DriveBayEntity.DiskSlotCount; i++)
            {
                int col = i % Columns;
                int row = i / Columns;
                var slotRect = new Rectangle(
                    (int)(startX + col * (SlotSize + SlotPadding)),
                    (int)(startY + row * (SlotSize + SlotPadding)),
                    SlotSize, SlotSize);

                if (!slotRect.Contains(mousePos.ToPoint()))
                    continue;

                var cursorItem = Main.mouseItem;

                var mod = ModLoader.GetMod("TerraStorage");

                if (cursorItem != null && !cursorItem.IsAir)
                {
                    if (cursorItem.ModItem is StorageDiskBase && _entity.DiskSlots[i].IsAir)
                    {
                        if (!_entity.InsertDisk(cursorItem, i))
                            return;
                        Main.mouseItem.TurnToAir();
                        SoundEngine.PlaySound(SoundID.Grab);
                        NetworkHandler.SendSyncDiskInsert(mod, _entity.ID, i, _entity.DiskSlots[i]);
                    }
                }
                else if (!shift)
                {
                    if (!_entity.DiskSlots[i].IsAir)
                    {
                        // The server may refuse this, and taking the disk before it answers would
                        // leave the refusal with nothing safe to do: correcting the bay would put a
                        // second item carrying the same DiskId into the world, and not correcting it
                        // would show a slot the server still considers full. So the server hands the
                        // disk over, the way it already does for a withdrawal.
                        if (Main.netMode == NetmodeID.MultiplayerClient)
                        {
                            NetworkHandler.SendSyncDiskRemove(mod, _entity.ID, i, routeToInventory: false);
                            SoundEngine.PlaySound(SoundID.Grab);
                            return;
                        }

                        var takenDiskId = GetDiskIdOf(_entity.DiskSlots[i]);
                        Main.mouseItem = _entity.DiskSlots[i].Clone();
                        _entity.DiskSlots[i].TurnToAir();
                        _entity.RefreshVisualState(_entity.IsConnected);
                        NetworkHandler.DropOrphanedDiskData(takenDiskId);
                        SoundEngine.PlaySound(SoundID.Grab);
                    }
                }
                else
                {
                    if (!_entity.DiskSlots[i].IsAir)
                    {
                        // Same reason as the cursor path above; the server routes it into the
                        // inventory and falls back to the cursor when that is full.
                        if (Main.netMode == NetmodeID.MultiplayerClient)
                        {
                            NetworkHandler.SendSyncDiskRemove(mod, _entity.ID, i, routeToInventory: true);
                            SoundEngine.PlaySound(SoundID.Grab);
                            return;
                        }

                        var takenDiskId = GetDiskIdOf(_entity.DiskSlots[i]);
                        var item = _entity.DiskSlots[i].Clone();
                        // Directly place item in inventory instead of using ground pickup logic
                        bool placedInInventory = false;
                        var player = Main.LocalPlayer;
                        for (int j = 0; j < 50; j++)
                        {
                            if (player.inventory[j].IsAir)
                            {
                                player.inventory[j] = item.Clone();
                                placedInInventory = true;
                                break;
                            }
                            else if (player.inventory[j].type == item.type && 
                                     player.inventory[j].prefix == item.prefix &&
                                     player.inventory[j].stack < player.inventory[j].maxStack)
                            {
                                int spaceAvailable = player.inventory[j].maxStack - player.inventory[j].stack;
                                if (spaceAvailable >= item.stack)
                                {
                                    player.inventory[j].stack += item.stack;
                                    placedInInventory = true;
                                    break;
                                }
                                else
                                {
                                    // Partially fill the stack and put remaining in next empty slot
                                    item.stack -= spaceAvailable;
                                    player.inventory[j].stack = player.inventory[j].maxStack;
                                }
                            }
                        }
                        
                        if (!placedInInventory)
                        {
                            // If no inventory space, re-insert into storage
                            _entity.DiskSlots[i] = item;
                        }
                        else
                        {
                            _entity.DiskSlots[i].TurnToAir();
                            _entity.RefreshVisualState(_entity.IsConnected);
                            NetworkHandler.DropOrphanedDiskData(takenDiskId);
                        }
                        SoundEngine.PlaySound(SoundID.Grab);
                    }
                }
                return;
            }
        }

        // Read before the slot is cleared: afterwards the item carrying the GUID is gone.
        private static System.Guid GetDiskIdOf(Item slotItem)
        {
            if (slotItem == null || slotItem.IsAir || slotItem.ModItem is not StorageDiskBase disk)
                return System.Guid.Empty;

            return disk.DiskId;
        }

        // Returns true if the mouse is over the given vanilla inventory slot index (0–49).
        // Replicates the slot positions that Terraria uses internally so we can detect
        // shift+clicks on inventory items before Terraria's own inventory handler fires.
        public static bool IsMouseOverInventorySlot(int slot)
        {
            // Vanilla uses 0.85f scale for all 50 inventory slots (10 cols x 5 rows)
            // Hit rect uses InventoryBack texture size (52x52) * scale
            const float scale = 0.85f;
            int col = slot % 10;
            int row = slot / 10;
            int x = (int)(20f + col * 56 * scale);
            int y = (int)(20f + row * 56 * scale);
            int size = (int)(52 * scale);
            return new Rectangle(x, y, size, size).Contains(Main.MouseScreen.ToPoint());
        }

        private void DrawItemInSlot(SpriteBatch spriteBatch, Item item, Rectangle rect)
        {
            Main.instance.LoadItem(item.type);
            var texture = TextureAssets.Item[item.type].Value;
            // Respect animated items (e.g., throwing weapons) by using the current animation frame
            var sourceRect = Main.itemAnimations[item.type] != null
                ? Main.itemAnimations[item.type].GetFrame(texture)
                : texture.Frame();

            float scale = 1f;
            float maxDim = System.Math.Max(sourceRect.Width, sourceRect.Height);
            // Scale down oversized item sprites so they fit within 36px of the slot
            if (maxDim > 36) scale = 36f / maxDim;

            spriteBatch.Draw(texture,
                new Vector2(rect.X + rect.Width / 2, rect.Y + rect.Height / 2),
                sourceRect, Color.White, 0f,
                new Vector2(sourceRect.Width / 2, sourceRect.Height / 2),
                scale, SpriteEffects.None, 0f);
        }
    }
}
