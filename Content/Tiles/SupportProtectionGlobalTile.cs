using Terraria;
using Terraria.DataStructures;
using Terraria.ModLoader;
using Terraria.ObjectData;

namespace TerraStorage.Content.Tiles
{
    // Prevents breaking the floor block directly underneath an occupied multi-tile
    // so the device above doesn't lose support and destroy its contents.
    public class SupportProtectionGlobalTile : GlobalTile
    {
        // Largest footprint of any protected device (the 3x3 Drive Bay). Every device's entity sits
        // at its top-left, so candidates are found by looking up and left of the floor tile by up to
        // this much, then kept only if that device's own footprint really rests on (i, j). The legacy
        // 2x2 Drive Bay and Crafting Core are protected the same way: a bay whose support goes
        // drops nothing, so losing one costs every disk in it.
        private const int MaxDeviceWidth = 3;
        private const int MaxDeviceHeight = 3;

        public override bool CanKillTile(int i, int j, int type, ref bool blockDamaged)
        {
            for (int h = 1; h <= MaxDeviceHeight; h++)
            {
                int topLeftY = j - h;
                if (topLeftY < 0) continue;

                for (int topLeftX = i - (MaxDeviceWidth - 1); topLeftX <= i; topLeftX++)
                {
                    if (topLeftX < 0) continue;
                    var pos = new Point16(topLeftX, topLeftY);
                    if (!TileEntity.ByPosition.TryGetValue(pos, out var entity))
                        continue;

                    bool occupied = (entity is DriveBayEntity driveBay && driveBay.HasDisks())
                        || (entity is CraftingCoreEntity craftingCore && craftingCore.HasStations());
                    if (!occupied || !RestsOn(pos, i, j))
                        continue;

                    blockDamaged = false;
                    return false;
                }
            }

            return true;
        }

        // Whether (i, j) is the floor directly beneath the device whose top-left is at pos.
        private static bool RestsOn(Point16 pos, int i, int j)
        {
            var data = TileObjectData.GetTileData(Main.tile[pos.X, pos.Y]);
            if (data == null)
                return false;

            return j == pos.Y + data.Height && i >= pos.X && i < pos.X + data.Width;
        }
    }
}
