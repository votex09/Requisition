using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.Enums;
using Terraria.GameContent.ObjectInteractions;
using Terraria.ModLoader;
using Terraria.ObjectData;

namespace TerraStorage.Content.Tiles
{
    // Legacy 2x2 Crafting Core retained for world-save compatibility. Cannot be crafted or placed;
    // exists solely so old worlds load correctly. Breaking one drops a CraftingCoreItem (the new 2x3).
    [LegacyName("CraftingCore")]
    public class CraftingCoreLegacy : ModTile
    {
        public override string Texture => "TerraStorage/Content/Tiles/DriveBayOld";

        public override void SetStaticDefaults()
        {
            Main.tileFrameImportant[Type] = true;
            Main.tileNoAttach[Type] = true;
            Main.tileLavaDeath[Type] = false;

            TileObjectData.newTile.CopyFrom(TileObjectData.Style2x2);
            TileObjectData.newTile.Origin = new Point16(0, 1);
            TileObjectData.newTile.CoordinateHeights = new[] { 16, 16 };
            TileObjectData.newTile.CoordinatePadding = 0;
            TileObjectData.newTile.Width = 2;
            TileObjectData.newTile.Height = 2;
            TileObjectData.newTile.UsesCustomCanPlace = true;
            TileObjectData.newTile.AnchorBottom = new AnchorData(AnchorType.SolidTile | AnchorType.SolidWithTop, 2, 0);
            TileObjectData.addTile(Type);

            AddMapEntry(new Color(180, 120, 50), CreateMapEntryName());
        }

        public override bool HasSmartInteract(int i, int j, SmartInteractScanSettings settings) => true;

        public override bool CanKillTile(int i, int j, ref bool blockDamaged)
        {
            var entity = CraftingCoreEntity.FindEntity(i, j);
            if (entity != null && entity.HasStations())
            {
                blockDamaged = false;
                return false;
            }
            return true;
        }

        public override bool RightClick(int i, int j)
        {
            var entity = CraftingCoreEntity.FindEntity(i, j);
            if (entity != null)
            {
                entity.OpenStationUI(Main.LocalPlayer);
                return true;
            }
            return false;
        }

        public override void MouseOver(int i, int j)
        {
            var player = Main.LocalPlayer;
            player.noThrow = 2;
            player.cursorItemIconEnabled = true;
            player.cursorItemIconID = ModContent.ItemType<Items.CraftingCoreItem>();
        }

        public override void KillMultiTile(int i, int j, int frameX, int frameY)
        {
            CraftingCoreEntity.FindEntityAt(i, j)?.DropStations(i, j);

            // Server and singleplayer only, so a multiplayer client does not spawn a local copy.
            if (Main.netMode != Terraria.ID.NetmodeID.MultiplayerClient)
                Item.NewItem(new EntitySource_TileBreak(i, j), i * 16, j * 16, 32, 32,
                    ModContent.ItemType<Items.CraftingCoreItem>());
            ModContent.GetInstance<CraftingCoreEntity>().Kill(i, j);
        }
    }
}
