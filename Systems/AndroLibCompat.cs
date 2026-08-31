using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Terraria;
using Terraria.Audio;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using androLib;
using androLib.UI;
using TerraStorage.Common;
using TerraStorage.Content.Tiles;
using TerraStorage.Content.UI;
using TerraStorage.Helpers;

namespace TerraStorage.Systems
{
    // Cross-mod compatibility with androLib (https://steamcommunity.com/sharedfiles/filedetails/?id=3021285778),
    // the library behind vacuum bags such as the Vacuum Ore Bag. Adds a "Deposit to Requisition"
    // button to every vacuum bag's UI that empties the bag into an accessible Requisition network.
    //
    // androLib is a weak reference: this ModSystem references no androLib types, so it loads cleanly
    // when androLib is absent. All androLib-facing code lives in AndroLibBagButtons, which is only
    // touched after a runtime presence check.
    internal class AndroLibCompatSystem : ModSystem
    {
        public override void PostSetupContent()
        {
            // The bag UI is client-only; a dedicated server registers no bags (androLib's
            // RegisterVacuumStorageClass returns early on Server).
            if (Main.dedServ)
                return;

            if (ModLoader.TryGetMod("androLib", out _))
                AndroLibBagButtons.Register();
        }
    }

    // All androLib-facing code. Gated so its members are only JIT-compiled when androLib is present;
    // reached exclusively through AndroLibCompatSystem's guarded call.
    [JITWhenModsEnabled("androLib")]
    internal static class AndroLibBagButtons
    {
        // Adds the deposit button to every registered vacuum bag. AddBagUIEdit defers the AddButton
        // call into androLib's PostSetupResipes, which runs after BagUI.PreSetup builds the button
        // list (so the button is not wiped) and after every mod's PostSetupContent. storageID equals
        // the BagUIs index — androLib assigns storageID = BagUIs.Count at registration.
        // NoInlining keeps androLib type references out of the caller's JIT.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Register()
        {
            for (int id = 0; id < StorageManager.BagUIs.Count; id++)
                StorageManager.AddBagUIEdit(id, bagUI => bagUI.AddButton(OnDepositClicked, GetButtonText, null));
        }

        private static string GetButtonText()
            => Language.GetTextValue("Mods.TerraStorage.UI.VacuumBags.DepositButton");

        // Invoked by androLib on click with the displayed bag (the button is never last — CloseBag is
        // appended in PostSetup — so dispatch passes DisplayedBagUI). MyInventory is this bag's own items.
        private static void OnDepositClicked(BagUI bagUI)
        {
            Item[] items = bagUI.MyInventory;
            if (items == null)
                return;

            // Priority 1: a Requisition Terminal is open (placed or Remote). The player reached this
            // network through the open handshake, so the existing client-GUID deposit path is used.
            StoragePlayerSystem local = StoragePlayerSystem.Local;
            if (local != null
                && ModContent.GetInstance<TerminalUISystem>().IsTerminalOpen
                && local.LastOpenedDiskIds.Count > 0)
            {
                DepositToOpenNetwork(items, local.LastOpenedDiskIds.ToList(), local.LastOpenedTerminalId);
                return;
            }

            // Priority 2: nearest Terminal within 15 tiles whose network has disks.
            if (TryFindNearestTerminal(Main.LocalPlayer, out Point16 terminalPos))
            {
                DepositNearby(items, terminalPos);
                return;
            }

            // Priority 3: no accessible network — do nothing but tell the player why.
            SoundEngine.PlaySound(SoundID.MenuClose);
            Main.NewText(Language.GetTextValue("Mods.TerraStorage.UI.VacuumBags.NoNetwork"));
        }

        private static void DepositToOpenNetwork(Item[] items, List<Guid> diskIds, int terminalEntityId)
        {
            bool client = Main.netMode == NetmodeID.MultiplayerClient;
            Mod mod = client ? ModContent.GetInstance<Requisition>() : null;
            bool moved = false;

            for (int i = 0; i < items.Length; i++)
            {
                Item item = items[i];
                if (item == null || item.IsAir || item.favorited)
                    continue;

                if (client)
                {
                    NetworkHandler.SendDepositItem(mod, terminalEntityId, item);
                    item.TurnToAir();
                }
                else
                {
                    int leftover = StorageWorldSystem.Instance.InsertItem(diskIds, item);
                    if (leftover <= 0)
                        item.TurnToAir();
                    else
                        item.stack = leftover;
                }
                moved = true;
            }

            if (moved)
                SoundEngine.PlaySound(SoundID.Grab);
        }

        // The not-open case: single-player resolves the network locally; the multiplayer client sends
        // the terminal position and lets the server resolve + range-check it, so client-resolved disk
        // GUIDs are never trusted for a terminal the player has not opened.
        private static void DepositNearby(Item[] items, Point16 terminalPos)
        {
            bool client = Main.netMode == NetmodeID.MultiplayerClient;
            List<Guid> diskIds = client ? null : StorageNetwork.GetAllConnectedDiskIds(terminalPos);
            Mod mod = client ? ModContent.GetInstance<Requisition>() : null;
            bool moved = false;

            for (int i = 0; i < items.Length; i++)
            {
                Item item = items[i];
                if (item == null || item.IsAir || item.favorited)
                    continue;

                if (client)
                {
                    NetworkHandler.SendDepositItemAtPosition(mod, terminalPos, item);
                    item.TurnToAir();
                }
                else
                {
                    int leftover = StorageWorldSystem.Instance.InsertItem(diskIds, item);
                    if (leftover <= 0)
                        item.TurnToAir();
                    else
                        item.stack = leftover;
                }
                moved = true;
            }

            if (moved)
                SoundEngine.PlaySound(SoundID.Grab);
        }

        // Nearest TerminalEntity within 15 tiles of the player whose network has at least one disk.
        // Disk GUIDs sync to clients (DriveBayEntity.NetSend), so this is valid on the multiplayer
        // client too; it only selects which terminal — the server still re-resolves authoritatively.
        private static bool TryFindNearestTerminal(Player player, out Point16 pos)
        {
            pos = default;
            float bestSq = float.MaxValue;
            bool found = false;

            foreach (var kvp in TileEntity.ByID)
            {
                if (kvp.Value is not TerminalEntity terminal)
                    continue;

                if (!TerminalReach.IsWithinRange(player.Center.X, player.Center.Y,
                        terminal.Position.X, terminal.Position.Y))
                    continue;

                float dx = player.Center.X - terminal.Position.X * TerminalReach.GetTilePixelSize();
                float dy = player.Center.Y - terminal.Position.Y * TerminalReach.GetTilePixelSize();
                float distSq = dx * dx + dy * dy;
                if (distSq >= bestSq)
                    continue;

                if (StorageNetwork.GetAllConnectedDiskIds(terminal.Position).Count == 0)
                    continue;

                bestSq = distSq;
                pos = terminal.Position;
                found = true;
            }

            return found;
        }
    }
}
