using System;

namespace TerraStorage.Common
{
    // How close a player has to be to a placed block to operate it. One encoding, because there
    // were three: the two UI panels closed themselves at a tile distance from the block's stored
    // position, while the server measured pixels to the block's 3x3 centre. The centre sits 1.5
    // tiles down-right of the stored position, so a player up-and-left of a Terminal could be
    // inside the range the panel enforced and outside the one the server did - a panel that stays
    // open while every packet it sends is refused.
    //
    // The UI's origin wins: it is the one a player can see. Taking pixels for the player and tiles
    // for the block matches how both callers already hold those values.
    public static class TerminalReach
    {
        public static int GetRangeInTiles() => 15;

        public static int GetTilePixelSize() => 16;

        public static bool IsWithinRange(float playerCenterXPixels, float playerCenterYPixels,
            int blockTileX, int blockTileY)
        {
            float tileSize = GetTilePixelSize();
            float playerTileX = playerCenterXPixels / tileSize;
            float playerTileY = playerCenterYPixels / tileSize;

            float offsetX = playerTileX - blockTileX;
            float offsetY = playerTileY - blockTileY;
            float distanceSquared = offsetX * offsetX + offsetY * offsetY;

            float range = GetRangeInTiles();
            return distanceSquared <= range * range;
        }
    }
}
