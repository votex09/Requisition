using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.Enums;
using Terraria.GameContent.ObjectInteractions;
using Terraria.ModLoader;
using Terraria.ObjectData;

namespace TerraStorage.Content.Tiles
{
    // The Crafting Core tile (2x3). Accepts crafting station items and condition provider items
    // in its slots so connected Terminals can automatically satisfy recipe requirements.
    // Blocks destruction while any station slot is occupied.
    public class CraftingCoreLarge : ModTile
    {
        public override string Texture => "TerraStorage/Content/Tiles/CraftingCore";

        public override void SetStaticDefaults()
        {
            Main.tileFrameImportant[Type] = true;
            Main.tileNoAttach[Type] = true;
            Main.tileLavaDeath[Type] = false;

            TileObjectData.newTile.CopyFrom(TileObjectData.Style2xX);
            TileObjectData.newTile.Origin = new Point16(0, 2);
            TileObjectData.newTile.CoordinateHeights = new[] { 16, 16, 16 };
            TileObjectData.newTile.CoordinatePadding = 0;
            TileObjectData.newTile.Width = 2;
            TileObjectData.newTile.Height = 3;
            TileObjectData.newTile.HookPostPlaceMyPlayer = new PlacementHook(
                ModContent.GetInstance<CraftingCoreEntity>().Hook_AfterPlacement, -1, 0, true);
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

            ModContent.GetInstance<CraftingCoreEntity>().Kill(i, j);
        }
    }
}
