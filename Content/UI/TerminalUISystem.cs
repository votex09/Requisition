using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.DataStructures;
using Terraria.ModLoader;
using Terraria.UI;
using TerraStorage.Common;
using TerraStorage.Content.Tiles;

namespace TerraStorage.Content.UI
{
    public class TerminalUISystem : ModSystem
    {
        private UserInterface _userInterface;
        private TerminalUIState _uiState;
        private bool _isOpen;
        private bool _remoteOpen;
        private Point16 _entityTilePos;

        public bool IsTerminalOpen => _isOpen;

        public bool IsMouseOverPanel() => _isOpen && _uiState != null && _uiState.IsMouseOverPanel();

        public override void Load()
        {
            if (!Main.dedServ)
            {
                _userInterface = new UserInterface();
                _uiState = new TerminalUIState();
                _uiState.Activate();

                RequisitionWindows.Register(
                    "TerraStorage: Terminal UI",
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

        public void OpenTerminal(TerminalEntity entity)
        {
            if (Main.dedServ) return;
            ModContent.GetInstance<DriveBayUISystem>()?.CloseDriveBay();
            ModContent.GetInstance<CraftingCoreUISystem>()?.CloseCraftingCore();
            _uiState.SetTerminal(entity);
            _entityTilePos = entity.Position;
            _userInterface.SetState(_uiState);
            _isOpen = true;
            _remoteOpen = false;
            Main.playerInventory = true;
        }

        public void OpenTerminalRemote(TerminalEntity entity)
        {
            OpenTerminal(entity);
            _remoteOpen = true;
        }

        public void CloseTerminal()
        {
            if (!(RequisitionClientConfig.Instance?.RememberSearchQuery ?? true))
                _uiState.ClearSearch();
            else
                _uiState.DeactivateSearch();
            _userInterface.SetState(null);
            _isOpen = false;
        }

        // The same rule the server applies to every packet this panel sends. Sharing it is the
        // point: when the panel and the server disagreed about where a block is measured from,
        // there was a band where this stayed open and the server refused everything it sent.
        private bool SenderIsAtTerminal()
            => TerminalReach.IsWithinRange(Main.LocalPlayer.Center.X, Main.LocalPlayer.Center.Y,
                _entityTilePos.X, _entityTilePos.Y);

        public override void UpdateUI(GameTime gameTime)
        {
            if (_isOpen)
            {
                if (!Main.playerInventory)
                {
                    CloseTerminal();
                    return;
                }

                if (!_remoteOpen && !SenderIsAtTerminal())
                {
                    CloseTerminal();
                    return;
                }
            }
        }
    }
}
