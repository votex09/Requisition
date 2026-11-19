using System.IO;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using TerraStorage.Content.Tiles;
using TerraStorage.Content.UI;

namespace TerraStorage.Content.Items
{
    // Binds to a placed Terminal and opens its full UI from anywhere in the world via left-click use,
    // middle-click, or hotkey. Right-click a Terminal tile while holding this to bind it.
    public class RemoteTerminal : ModItem
    {
        // Bound by the Terminal's tile position. Tile entity IDs are handed out afresh every time a
        // world loads, so an ID saved with the item pointed at whichever entity happened to get that
        // number next session - another Terminal, or nothing. The position is what stays put.
        private Point16 _boundPosition = Point16.NegativeOne;

        // An ID saved by an older version. Honoured once, then converted to a position.
        private int _legacyBoundId = -1;

        public bool IsBound => _boundPosition != Point16.NegativeOne || _legacyBoundId >= 0;

        public void Bind(TerminalEntity terminal)
        {
            _boundPosition = terminal.Position;
            _legacyBoundId = -1;
        }

        // The Terminal this remote is bound to, or null if it is unbound or the Terminal is gone.
        public TerminalEntity FindBoundTerminal()
        {
            if (_boundPosition != Point16.NegativeOne)
            {
                return TileEntity.ByPosition.TryGetValue(_boundPosition, out var te)
                    ? te as TerminalEntity
                    : null;
            }

            if (_legacyBoundId >= 0 && TileEntity.ByID.TryGetValue(_legacyBoundId, out var legacy)
                && legacy is TerminalEntity legacyTerminal)
            {
                Bind(legacyTerminal);
                return legacyTerminal;
            }

            return null;
        }

        // Opens the bound Terminal's UI, or says why it cannot.
        public void OpenBoundTerminal()
        {
            if (!IsBound)
            {
                Main.NewText("Remote Terminal is not bound. Right-click a Crafting Terminal to bind.", Color.Yellow);
                return;
            }

            var terminal = FindBoundTerminal();
            if (terminal == null)
            {
                Main.NewText("Bound Terminal not found. The Terminal may have been destroyed.", Color.OrangeRed);
                return;
            }

            ModContent.GetInstance<TerminalUISystem>()?.OpenTerminalRemote(terminal);
        }

        public override void SetDefaults()
        {
            Item.width = 32;
            Item.height = 32;
            Item.maxStack = 1;
            Item.rare = ItemRarityID.Red;
            Item.value = Item.buyPrice(gold: 20);
            Item.useStyle = ItemUseStyleID.HoldUp;
            Item.useTime = 20;
            Item.useAnimation = 20;
        }

        public override bool? UseItem(Player player)
        {
            if (player.whoAmI == Main.myPlayer)
                OpenBoundTerminal();
            return true;
        }

        public override void SaveData(TagCompound tag)
        {
            if (_boundPosition != Point16.NegativeOne)
            {
                tag["boundX"] = _boundPosition.X;
                tag["boundY"] = _boundPosition.Y;
            }
            else if (_legacyBoundId >= 0)
            {
                tag["boundId"] = _legacyBoundId;
            }
        }

        public override void LoadData(TagCompound tag)
        {
            _boundPosition = tag.ContainsKey("boundX") && tag.ContainsKey("boundY")
                ? new Point16(tag.GetShort("boundX"), tag.GetShort("boundY"))
                : Point16.NegativeOne;
            _legacyBoundId = tag.ContainsKey("boundId") ? tag.GetInt("boundId") : -1;
        }

        public override void NetSend(BinaryWriter writer)
        {
            writer.Write(_boundPosition.X);
            writer.Write(_boundPosition.Y);
            writer.Write(_legacyBoundId);
        }

        public override void NetReceive(BinaryReader reader)
        {
            _boundPosition = new Point16(reader.ReadInt16(), reader.ReadInt16());
            _legacyBoundId = reader.ReadInt32();
        }
    }
}
