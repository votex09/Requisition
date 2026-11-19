using System.Collections.Generic;
using System.IO;
using System.Linq;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using Terraria.ObjectData;
using TerraStorage.Common;
using TerraStorage.Content.UI;
using TerraStorage.Helpers;

namespace TerraStorage.Content.Tiles
{
    // Tile entity attached to each placed Crafting Core. Holds up to
    // StationSlotCount crafting station items, exposes the set of
    // tile types and Common.CraftingCondition they provide,
    // and persists that data across save/load and network sync.
    public class CraftingCoreEntity : ModTileEntity
    {
        //Maximum number of crafting station slots in a single Crafting Core.
        public const int StationSlotCount = 40;

        public Item[] StationSlots { get; private set; } = new Item[StationSlotCount];

        public override void OnNetPlace()
        {
            InitializeSlots();
        }

        public override bool IsTileValidForEntity(int x, int y)
        {
            var tile = Main.tile[x, y];
            return tile.HasTile && (tile.TileType == ModContent.TileType<CraftingCoreLarge>() ||
                                    tile.TileType == ModContent.TileType<CraftingCoreLegacy>());
        }

        public override int Hook_AfterPlacement(int i, int j, int type, int style, int direction, int alternate)
        {
            // With processedCoordinates: true, i/j is the top-left corner of the multi-tile.
            if (Main.netMode == Terraria.ID.NetmodeID.MultiplayerClient)
            {
                // Clients defer entity creation to the server via TileEntityPlacement message
                NetMessage.SendTileSquare(Main.myPlayer, i, j, 2, 3);
                NetMessage.SendData(Terraria.ID.MessageID.TileEntityPlacement, number: i, number2: j, number3: Type);
                return -1;
            }

            int placedEntity = Place(i, j);
            if (TileEntity.ByID.TryGetValue(placedEntity, out var entity) && entity is CraftingCoreEntity cce)
                cce.InitializeSlots();

            return placedEntity;
        }

        public void EnsureSlotsInitialized() => InitializeSlots();

        //Returns true if at least one station slot is occupied.
        public bool HasStations()
        {
            for (int i = 0; i < StationSlotCount; i++)
                if (StationSlots[i] != null && !StationSlots[i].IsAir)
                    return true;
            return false;
        }

        // Ensures every slot is a non-null air Item rather than null.
        // Called defensively before slot access to guard against partial deserialization.
        private void InitializeSlots()
        {
            for (int i = 0; i < StationSlotCount; i++)
            {
                if (StationSlots[i] == null)
                {
                    StationSlots[i] = new Item();
                    StationSlots[i].TurnToAir();
                }
            }
        }

        // Get all tile types that the inserted crafting station items would provide.
        // Items that place tiles (createTile >= TileID.Dirt) count as providing that tile type
        // as a crafting station.
        public HashSet<int> GetAvailableTileTypes()
        {
            var tileTypes = new HashSet<int>();
            InitializeSlots();

            for (int i = 0; i < StationSlotCount; i++)
            {
                if (StationSlots[i] != null && !StationSlots[i].IsAir && StationSlots[i].createTile >= TileID.Dirt)
                {
                    tileTypes.Add(StationSlots[i].createTile);
                }
            }

            return tileTypes;
        }

        // Maps vanilla item types to the crafting condition they provide when placed in a Crafting Core.
        private static CraftingCondition GetItemCondition(int itemType) => itemType switch
        {
            ItemID.BottomlessBucket      => CraftingCondition.NearWater,
            ItemID.BottomlessLavaBucket  => CraftingCondition.NearLava,
            ItemID.BottomlessHoneyBucket => CraftingCondition.NearHoney,
            ItemID.IceMachine  => CraftingCondition.InSnow,
            ItemID.Tombstone or ItemID.GraveMarker or ItemID.CrossGraveMarker or
            ItemID.Headstone or ItemID.Gravestone  or ItemID.Obelisk => CraftingCondition.InGraveyard,
            _                  => CraftingCondition.None
        };

        // Get all crafting conditions provided by items in the station slots.
        public HashSet<CraftingCondition> GetAvailableConditions()
        {
            var conditions = new HashSet<CraftingCondition>();
            InitializeSlots();

            for (int i = 0; i < StationSlotCount; i++)
            {
                if (StationSlots[i] == null || StationSlots[i].IsAir) continue;
                var cond = GetItemCondition(StationSlots[i].type);
                if (cond != CraftingCondition.None)
                    conditions.Add(cond);
            }
            return conditions;
        }

        // Check if a specific item can be inserted into the Crafting Core.
        // Valid items either place a crafting station tile or provide a crafting condition.
        public static bool IsValidStation(Item item)
        {
            return item != null && !item.IsAir &&
                   (item.createTile >= TileID.Dirt || GetItemCondition(item.type) != CraftingCondition.None);
        }

        // Try to insert a station into the first available slot. 
        public bool InsertStation(Item stationItem, int slot = -1)
        {
            if (!IsValidStation(stationItem))
                return false;

            InitializeSlots();

            if (slot >= 0 && slot < StationSlotCount)
            {
                if (StationSlots[slot].IsAir)
                {
                    StationSlots[slot] = stationItem.Clone();
                    return true;
                }
                return false;
            }

            for (int i = 0; i < StationSlotCount; i++)
            {
                if (StationSlots[i].IsAir)
                {
                    StationSlots[i] = stationItem.Clone();
                    return true;
                }
            }

            return false;
        }

        // Remove a station from a specific slot.
        public Item RemoveStation(int slot)
        {
            InitializeSlots();

            if (slot < 0 || slot >= StationSlotCount || StationSlots[slot].IsAir)
                return new Item();

            var removed = StationSlots[slot].Clone();
            StationSlots[slot].TurnToAir();
            return removed;
        }

        // Drop all stations when the block is destroyed. Server and singleplayer only: the server
        // breaks the tile too, and a client's own drop would be a second, local-only copy.
        // The whole item is dropped rather than its type/stack/prefix, so per-instance mod data
        // survives.
        public void DropStations(int x, int y)
        {
            if (Main.netMode == NetmodeID.MultiplayerClient)
                return;

            InitializeSlots();
            for (int i = 0; i < StationSlotCount; i++)
            {
                if (!StationSlots[i].IsAir)
                {
                    Item.NewItem(new EntitySource_TileBreak(x, y), x * 16, y * 16, 32, 32, StationSlots[i]);
                    StationSlots[i].TurnToAir();
                }
            }
        }

        // The entity of a Crafting Core being destroyed. KillMultiTile runs after the tiles are
        // cleared, so FindEntity (which needs HasTile) cannot see it; the entity sits at (i, j).
        public static CraftingCoreEntity FindEntityAt(int i, int j)
        {
            return TileEntity.ByPosition.TryGetValue(new Point16(i, j), out var entity)
                ? entity as CraftingCoreEntity
                : null;
        }

        //Opens the station management UI for this Crafting Core.
        public void OpenStationUI(Player player)
        {
            var uiSystem = ModContent.GetInstance<CraftingCoreUISystem>();
            uiSystem?.OpenCraftingCore(this);
        }

        public override void SaveData(TagCompound tag)
        {
            InitializeSlots();
            var stationTags = new List<TagCompound>();
            for (int i = 0; i < StationSlotCount; i++)
            {
                stationTags.Add(ItemIO.Save(StationSlots[i]));
            }
            tag["stations"] = stationTags;
        }

        public override void LoadData(TagCompound tag)
        {
            InitializeSlots();
            if (tag.ContainsKey("stations"))
            {
                var stationTags = tag.GetList<TagCompound>("stations");
                for (int i = 0; i < StationSlotCount && i < stationTags.Count; i++)
                {
                    StationSlots[i] = ItemIO.Load(stationTags[i]);
                }
            }
        }

        public override void NetSend(BinaryWriter writer)
        {
            InitializeSlots();
            for (int i = 0; i < StationSlotCount; i++)
            {
                ItemIO.Send(StationSlots[i], writer, true);
            }
        }

        public override void NetReceive(BinaryReader reader)
        {
            InitializeSlots();
            for (int i = 0; i < StationSlotCount; i++)
            {
                StationSlots[i] = ItemIO.Receive(reader, true);
            }
        }

        // Finds the <see cref="CraftingCoreEntity"/> for any tile coordinate within the multi-tile.
        public static CraftingCoreEntity FindEntity(int i, int j)
        {
            var tile = Main.tile[i, j];
            if (!tile.HasTile)
                return null;

            // Entity is stored at the top-left corner of the multi-tile.
            Point16 topLeft = TileObjectData.TopLeft(i, j);
            if (topLeft == Point16.NegativeOne)
                return null;

            if (TileEntity.ByPosition.TryGetValue(topLeft, out var entity) && entity is CraftingCoreEntity cce)
                return cce;

            return null;
        }
    }
}
