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
using TerraStorage.Content.Tiles;
using TerraStorage.Content.UI.Elements;
using TerraStorage.Systems;

namespace TerraStorage.Content.UI
{
    // UIState for the Crafting Core station-module panel. Renders a grid of station slots
    // and handles left-click insertion/extraction and shift+click quick-transfer of valid
    // crafting station items from the player's inventory.
    public class CraftingCoreUIState : UIState
    {
        private const int Columns = 10;
        private const int Rows = 4;
        private const int SlotSize = 48;
        private const int SlotPadding = 4;

        private TSWindowElement _panel;
        private CraftingCoreEntity _entity;

        private bool _prevMouseLeft;

        public bool IsMouseOverPanel() => _panel?.ContainsPoint(Main.MouseScreen) == true;

        public CraftingCoreEntity Entity => _entity;

        public void SetEntity(CraftingCoreEntity entity)
        {
            _entity = entity;
        }

        public override void OnInitialize()
        {
            _panel = new TSWindowElement
            {
                StoreKey    = "craftingcore",
                HasTitleBar = false,
                Resizable   = false,
            };
            _panel.Width.Set(Columns * (SlotSize + SlotPadding) + 28, 0f);
            _panel.Height.Set(Rows * (SlotSize + SlotPadding) + 80, 0f);
            _panel.HAlign = 0.5f;
            _panel.VAlign = 0.4f;
            _panel.GetDragZone = mouse =>
            {
                var d = _panel.GetDimensions();
                return new Rectangle((int)d.X, (int)d.Y, (int)d.Width, 26).Contains(mouse.ToPoint());
            };
            _panel.SetPadding(10);
            Append(_panel);

            var title = new UIText(Language.GetText("Mods.TerraStorage.UI.CraftingCore.Title"));
            title.HAlign = 0.5f;
            _panel.Append(title);

            var helpText = new UIText(Language.GetText("Mods.TerraStorage.UI.CraftingCore.HelpText"), 0.7f);
            helpText.HAlign = 0.5f;
            helpText.Top.Set(22, 0f);
            _panel.Append(helpText);

            var closeBtn = new TSCloseButton(() => ModContent.GetInstance<CraftingCoreUISystem>().CloseCraftingCore());
            closeBtn.Left.Set(-26, 1f);
            closeBtn.Top.Set(-4, 0f);
            _panel.Append(closeBtn);
        }

        public override void Draw(SpriteBatch spriteBatch)
        {
            base.Draw(spriteBatch);

            if (_entity == null || _panel == null)
                return;

            _entity.EnsureSlotsInitialized();
            var panelDims = _panel.GetInnerDimensions();
            float startX = panelDims.X + 4;
            float startY = panelDims.Y + 44;

            for (int i = 0; i < CraftingCoreEntity.StationSlotCount; i++)
            {
                int col = i % Columns;
                int row = i / Columns;
                var pos = new Vector2(startX + col * (SlotSize + SlotPadding), startY + row * (SlotSize + SlotPadding));

                var slotRect = new Rectangle((int)pos.X, (int)pos.Y, SlotSize, SlotSize);
                Utils.DrawInvBG(spriteBatch, slotRect, new Color(120, 90, 50) * 0.7f);

                var item = _entity.StationSlots[i];
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
                        Main.hoverItemName = "Empty Station Slot";
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
            float startY = panelDims.Y + 44;

            for (int i = 0; i < CraftingCoreEntity.StationSlotCount; i++)
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
                    if (CraftingCoreEntity.IsValidStation(cursorItem) && _entity.StationSlots[i].IsAir)
                    {
                        _entity.StationSlots[i] = cursorItem.Clone();
                        _entity.StationSlots[i].stack = 1;
                        cursorItem.stack--;
                        if (cursorItem.stack <= 0)
                            Main.mouseItem.TurnToAir();
                        SoundEngine.PlaySound(SoundID.Grab);
                        NetworkHandler.SendSyncStationInsert(mod, _entity.ID, i, _entity.StationSlots[i]);
                    }
                    else if (!CraftingCoreEntity.IsValidStation(cursorItem))
                    {
                        Main.NewText("Only crafting station items can be inserted here!", 255, 100, 100);
                    }
                }
                else if (!shift)
                {
                    if (!_entity.StationSlots[i].IsAir)
                    {
                        // The server hands the station over, as it does for a Drive Bay disk:
                        // taking it here first let two players each keep a copy of one item.
                        if (Main.netMode == NetmodeID.MultiplayerClient)
                        {
                            NetworkHandler.SendSyncStationRemove(mod, _entity.ID, i, routeToInventory: false);
                            SoundEngine.PlaySound(SoundID.Grab);
                            return;
                        }

                        Main.mouseItem = _entity.StationSlots[i].Clone();
                        _entity.StationSlots[i].TurnToAir();
                        SoundEngine.PlaySound(SoundID.Grab);
                        NetworkHandler.SendSyncStationRemove(mod, _entity.ID, i);
                    }
                }
                else
                {
                    if (!_entity.StationSlots[i].IsAir)
                    {
                        // Same reason as the cursor path above; the server routes it into the
                        // inventory and falls back to the cursor when that is full.
                        if (Main.netMode == NetmodeID.MultiplayerClient)
                        {
                            NetworkHandler.SendSyncStationRemove(mod, _entity.ID, i, routeToInventory: true);
                            SoundEngine.PlaySound(SoundID.Grab);
                            return;
                        }

                        var item = _entity.StationSlots[i].Clone();
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
                            _entity.StationSlots[i] = item;
                        }
                        else
                        {
                            _entity.StationSlots[i].TurnToAir();
                            NetworkHandler.SendSyncStationRemove(mod, _entity.ID, i);
                        }
                        SoundEngine.PlaySound(SoundID.Grab);
                    }
                }
                return;
            }
        }


        // Draws a single item centered inside <paramref name="rect"/>, respecting animation
        // frames and scaling oversized sprites down to fit within 36 px.
        private void DrawItemInSlot(SpriteBatch spriteBatch, Item item, Rectangle rect)
        {
            Main.instance.LoadItem(item.type);
            var texture = TextureAssets.Item[item.type].Value;
            // Use the current animation frame for items with sprite animations.
            var sourceRect = Main.itemAnimations[item.type] != null
                ? Main.itemAnimations[item.type].GetFrame(texture)
                : texture.Frame();

            float scale = 1f;
            float maxDim = System.Math.Max(sourceRect.Width, sourceRect.Height);
            // Scale down oversized sprites so they fit within 36 px of the slot.
            if (maxDim > 36) scale = 36f / maxDim;

            spriteBatch.Draw(texture,
                new Vector2(rect.X + rect.Width / 2, rect.Y + rect.Height / 2),
                sourceRect, Color.White, 0f,
                new Vector2(sourceRect.Width / 2, sourceRect.Height / 2),
                scale, SpriteEffects.None, 0f);
        }
    }
}
