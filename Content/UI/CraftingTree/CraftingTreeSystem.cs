using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using TerraStorage.Systems;

namespace TerraStorage.Content.UI.CraftingTree
{
    public class CraftingTreeSystem : ModSystem
    {
        private CraftingTreeState _state;
        private bool _isOpen;

        public static ModKeybind OpenTreeKeybind { get; private set; }

        public bool IsTreeOpen => _isOpen;

        public override void Load()
        {
            if (Main.dedServ) return;

            OpenTreeKeybind = KeybindLoader.RegisterKeybind(Mod, "CraftingTree", "X");
            _state = new CraftingTreeState();
            _state.Activate();

            // No UserInterface of its own -- the state is driven directly -- so the arbiter's
            // click suppression is applied by RequisitionUISystem around this update callback.
            RequisitionWindows.Register(
                "TerraStorage: Crafting Tree",
                isOpen: () => _isOpen,
                isMouseOver: () => _state.IsMouseOverPanel(),
                update: gameTime => _state.Update(gameTime),
                draw: DrawTree);
        }

        private bool DrawTree()
        {
            if (_state.IsMouseOverPanel())
            {
                Main.HoverItem = new Item();
                Main.hoverItemName = string.Empty;
            }
            _state.DrawTree(Main.spriteBatch);
            return true;
        }

        public override void Unload()
        {
            OpenTreeKeybind = null;
        }

        public void OpenTree(int itemType)
        {
            if (Main.dedServ) return;

            if (UIPositionStore.TryGetSize("craftingtree", out float w, out float h))
                _state.SetSize(w, h);

            if (UIPositionStore.TryGet("craftingtree", out float x, out float y))
            {
                _state.SetPosition(x, y);
            }
            else
            {
                _state.SetPosition(
                    (UIDrawHelpers.UiWidth - 900) / 2f,
                    (UIDrawHelpers.UiHeight - 550) / 2f);
            }

            _state.OpenForItem(itemType);
            _isOpen = true;
        }

        public void CloseTree()
        {
            if (!_isOpen) return;
            var (x, y) = _state.GetPosition();
            var (w, h) = _state.GetSize();
            UIPositionStore.SaveWithSize("craftingtree", x, y, w, h);
            _isOpen = false;
        }

        public override void UpdateUI(GameTime gameTime)
        {
            if (Main.dedServ) return;

            if (OpenTreeKeybind?.JustPressed == true)
            {
                int hoveredItem = GetHoveredItemType();
                if (hoveredItem > 0)
                {
                    if (_isOpen)
                        CloseTree();
                    OpenTree(hoveredItem);
                }
                else if (_isOpen)
                {
                    CloseTree();
                }
            }
        }

        private static int GetHoveredItemType()
        {
            int encItem = ModContent.GetInstance<Encyclopedia.EncyclopediaSystem>()?.GetGridHoveredItemType() ?? 0;
            if (encItem > 0) return encItem;

            var player = Main.LocalPlayer;
            for (int i = 0; i < 50; i++)
            {
                if (DriveBayUIState.IsMouseOverInventorySlot(i) && !player.inventory[i].IsAir)
                    return player.inventory[i].type;
            }

            if (Main.mouseItem != null && !Main.mouseItem.IsAir)
                return Main.mouseItem.type;

            if (Main.HoverItem != null && !Main.HoverItem.IsAir && Main.HoverItem.type > ItemID.None)
                return Main.HoverItem.type;

            return 0;
        }

    }
}
