using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.Enums;
using Terraria.GameContent.ObjectInteractions;
using Terraria.ModLoader;
using Terraria.ObjectData;
using TerraStorage.Helpers;

namespace TerraStorage.Content.Tiles
{
    // The Terminal tile (3x3). Players right-click it to browse storage contents, search, sort,
    // and craft items from connected Drive Bays and Crafting Cores within range.
    public class Terminal : ModTile
    {
        public override void SetStaticDefaults()
        {
            Main.tileFrameImportant[Type] = true;
            Main.tileNoAttach[Type] = true;
            Main.tileLavaDeath[Type] = false;

            TileObjectData.newTile.CopyFrom(TileObjectData.Style3x3);
            TileObjectData.newTile.Origin = new Point16(1, 2);
            TileObjectData.newTile.CoordinateHeights = new[] { 16, 16, 16 };
            TileObjectData.newTile.CoordinatePadding = 0;
            TileObjectData.newTile.Width = 3;
            TileObjectData.newTile.Height = 3;
            TileObjectData.newTile.HookPostPlaceMyPlayer = new PlacementHook(
                ModContent.GetInstance<TerminalEntity>().Hook_AfterPlacement, -1, 0, true);
            TileObjectData.newTile.UsesCustomCanPlace = true;
            TileObjectData.newTile.AnchorBottom = new AnchorData(AnchorType.SolidTile | AnchorType.SolidWithTop, 3, 0);
            TileObjectData.addTile(Type);

            AddMapEntry(new Color(50, 150, 200), CreateMapEntryName());
        }

        public override bool HasSmartInteract(int i, int j, SmartInteractScanSettings settings) => true;

        public override bool RightClick(int i, int j)
        {
            var entity = TerminalEntity.FindEntity(i, j);
            if (entity == null) return false;

            // If player is holding a Remote Terminal, bind it instead of opening the UI.
            if (Main.LocalPlayer.HeldItem.ModItem is Items.RemoteTerminal remote)
            {
                remote.Bind(entity);
                Main.NewText("Remote Terminal bound.", Microsoft.Xna.Framework.Color.LightCyan);
                return true;
            }

            entity.OpenTerminalUI(Main.LocalPlayer);
            return true;
        }

        public override void MouseOver(int i, int j)
        {
            var player = Main.LocalPlayer;
            player.noThrow = 2;
            player.cursorItemIconEnabled = true;
            player.cursorItemIconID = ModContent.ItemType<Items.TerminalItem>();
        }

        public override void KillMultiTile(int i, int j, int frameX, int frameY)
        {
            // Notify connected bays they've lost their terminal.
            // Use direct position lookup — FindEntity checks HasTile which may already be
            // cleared by the time KillMultiTile runs.
            if (TileEntity.ByPosition.TryGetValue(new Point16(i, j), out var rawEntity)
                && rawEntity is TerminalEntity terminal)
            {
                foreach (var bay in StorageNetwork.FindConnectedDriveBays(terminal.Position))
                    bay.RefreshVisualState(false);
            }

            ModContent.GetInstance<TerminalEntity>().Kill(i, j);
        }
    }
}
