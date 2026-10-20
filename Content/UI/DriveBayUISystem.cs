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
    public class DriveBayUISystem : ModSystem
    {
        private UserInterface _userInterface;
        private DriveBayUIState _uiState;
        private bool _isOpen;
        private Point16 _entityTilePos;

        private UserInterface _recoveryInterface;
        private DiskRecoveryUIState _recoveryState;
        private bool _recoveryOpen;

        public bool IsOpen => _isOpen;

        public bool IsMouseOverPanel() => _isOpen &&
            ((_uiState != null && _uiState.IsMouseOverPanel()) ||
             (_recoveryOpen && _recoveryState != null && _recoveryState.IsMouseOverPanel()));

        public override void Load()
        {
            if (!Main.dedServ)
            {
                _userInterface = new UserInterface();
                _uiState = new DriveBayUIState();
                _uiState.Activate();

                _recoveryInterface = new UserInterface();
                _recoveryState = new DiskRecoveryUIState();
                _recoveryState.Activate();

                RequisitionWindow driveBay = RequisitionWindows.Register(
                    "TerraStorage: Storage Block UI",
                    isOpen: () => _isOpen,
                    isMouseOver: () => _uiState.IsMouseOverPanel(),
                    update: gameTime => _userInterface.Update(gameTime),
                    draw: DrawDriveBay,
                    hidesVanillaCraftingMenu: true);

                // Its own window, not folded into the Drive Bay's: it stacks above that panel, and
                // the arbiter has to be able to tell a click on one from a click on the other.
                // keepAbove pins it over its parent, so clicking the Drive Bay behind it cannot
                // bury the dialog the Drive Bay itself opened.
                RequisitionWindows.Register(
                    "TerraStorage: Disk Recovery UI",
                    isOpen: () => _isOpen && _recoveryOpen,
                    isMouseOver: () => _recoveryState.IsMouseOverPanel(),
                    update: gameTime => _recoveryInterface.Update(gameTime),
                    draw: DrawDiskRecovery,
                    keepAbove: driveBay.Handle);
            }
        }

        private bool DrawDriveBay()
        {
            if (_uiState.IsMouseOverPanel())
            {
                Main.HoverItem = new Item();
                Main.hoverItemName = string.Empty;
            }
            _userInterface.Draw(Main.spriteBatch, new GameTime());
            return true;
        }

        private bool DrawDiskRecovery()
        {
            if (_recoveryState.IsMouseOverPanel())
            {
                Main.HoverItem = new Item();
                Main.hoverItemName = string.Empty;
            }
            _recoveryInterface.Draw(Main.spriteBatch, new GameTime());
            return true;
        }

        public void OpenDriveBay(DriveBayEntity entity)
        {
            if (Main.dedServ)
                return;

            ModContent.GetInstance<CraftingCoreUISystem>()?.CloseCraftingCore();
            ModContent.GetInstance<TerminalUISystem>()?.CloseTerminal();

            _uiState.SetEntity(entity);
            _entityTilePos = entity.Position;
            _userInterface.SetState(_uiState);
            _isOpen = true;
            Main.playerInventory = true;
        }

        public void CloseDriveBay()
        {
            CloseDiskRecovery();
            _userInterface.SetState(null);
            _isOpen = false;
        }

        public void OpenDiskRecovery()
        {
            if (Main.dedServ) return;
            _recoveryState.Open();
            _recoveryInterface.SetState(_recoveryState);
            _recoveryOpen = true;
        }

        public void CloseDiskRecovery()
        {
            if (!_recoveryOpen) return;
            _recoveryState.ReturnDisk();
            _recoveryInterface.SetState(null);
            _recoveryOpen = false;
        }

        public DriveBayEntity OpenEntity => _isOpen ? _uiState?.Entity : null;

        public override void UpdateUI(GameTime gameTime)
        {
            if (_isOpen)
            {
                if (!Main.playerInventory)
                {
                    CloseDriveBay();
                    return;
                }

                // The same rule the server applies to the disk insert and remove packets this panel
                // sends, so it cannot stay open over a band where they are all refused.
                if (!TerminalReach.IsWithinRange(Main.LocalPlayer.Center.X, Main.LocalPlayer.Center.Y,
                        _entityTilePos.X, _entityTilePos.Y))
                {
                    CloseDriveBay();
                    return;
                }
            }
        }
    }
}
