using Terraria;
using Terraria.ModLoader;
using TerraStorage.Content.Items;

namespace TerraStorage.Content.Players
{
    // Handles middle-click and hotkey activation of the Remote Terminal.
    public class RemoteTerminalPlayer : ModPlayer
    {
        private bool _prevMiddle;

        public override void PostUpdate()
        {
            if (Player != Main.LocalPlayer) return;

            HandleHotkey();

            if (!Main.playerInventory) return;

            bool middle = Main.mouseMiddle;
            bool clicked = middle && !_prevMiddle;
            _prevMiddle = middle;

            if (!clicked) return;
            if (Main.HoverItem == null || Main.HoverItem.IsAir) return;
            if (Main.HoverItem.ModItem is not RemoteTerminal hoverRemote) return;

            hoverRemote.OpenBoundTerminal();
        }

        private void HandleHotkey()
        {
            if (!(Requisition.OpenRemoteTerminalHotkey?.JustPressed ?? false)) return;

            for (int i = 0; i < 50; i++)
            {
                if (Player.inventory[i].ModItem is RemoteTerminal remote)
                {
                    remote.OpenBoundTerminal();
                    return;
                }
            }
        }
    }
}
