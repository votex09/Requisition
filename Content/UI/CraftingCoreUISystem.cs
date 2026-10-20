using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.DataStructures;
using Terraria.ModLoader;
using Terraria.UI;
using TerraStorage.Content.Tiles;

namespace TerraStorage.Content.UI
{
    public class CraftingCoreUISystem : ModSystem
    {
        private const float MaxInteractDistance = 15f; // tiles

        private UserInterface _userInterface;
        private CraftingCoreUIState _uiState;
        private bool _isOpen;
        private Point16 _entityTilePos;

        public bool IsOpen => _isOpen;
        public CraftingCoreEntity OpenEntity => _isOpen ? _uiState?.Entity : null;

        public bool IsMouseOverPanel() => _isOpen && _uiState != null && _uiState.IsMouseOverPanel();

        public override void Load()
        {
            if (!Main.dedServ)
            {
                _userInterface = new UserInterface();
                _uiState = new CraftingCoreUIState();
                _uiState.Activate();

                RequisitionWindows.Register(
                    "TerraStorage: Crafting Core UI",
                    isOpen: () => _isOpen,
                    isMouseOver: () => _uiState.IsMouseOverPanel(),
                    update: gameTime => _userInterface.Update(gameTime),
                    draw: DrawPanel,
                    hidesVanillaCraftingMenu: true);
            }
        }

        private bool DrawPanel()
        {
            if (_uiState.IsMouseOverPanel())
            {
                Main.HoverItem = new Item();
                Main.hoverItemName = string.Empty;
            }
            _userInterface.Draw(Main.spriteBatch, new GameTime());
            return true;
        }

        public void OpenCraftingCore(CraftingCoreEntity entity)
        {
            if (Main.dedServ)
                return;

            ModContent.GetInstance<DriveBayUISystem>()?.CloseDriveBay();
            ModContent.GetInstance<TerminalUISystem>()?.CloseTerminal();

            _uiState.SetEntity(entity);
            _entityTilePos = entity.Position;
            _userInterface.SetState(_uiState);
            _isOpen = true;
            Main.playerInventory = true;
        }

        public void CloseCraftingCore()
        {
            _userInterface.SetState(null);
            _isOpen = false;
        }

        public override void UpdateUI(GameTime gameTime)
        {
            if (_isOpen)
            {
                if (!Main.playerInventory)
                {
                    CloseCraftingCore();
                    return;
                }

                var playerTilePos = Main.LocalPlayer.Center / 16f;
                float dist = Vector2.Distance(playerTilePos, _entityTilePos.ToVector2());
                if (dist > MaxInteractDistance)
                {
                    CloseCraftingCore();
                    return;
                }
            }
        }
    }
}
