using System;
using System.IO;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using TerraStorage.Content.Items;
using TerraStorage.Systems;

namespace TerraStorage.Content.Players
{
    // Handles middle-click archiving and unarchiving of Storage Disks in the player inventory.
    // Archive embeds items into the disk's NBT and removes their world entry (cross-world transport).
    // Unarchive clears the archived flag; items are restored to world storage on next Drive Bay insertion.
    public class DiskArchivePlayer : ModPlayer
    {
        private bool _prevMiddle;

        public override void PostUpdate()
        {
            // Only process for the local player and only while inventory is open.
            if (Player != Main.LocalPlayer) return;
            if (!Main.playerInventory) return;

            bool middle = Main.mouseMiddle;
            bool clicked = middle && !_prevMiddle;
            _prevMiddle = middle;

            if (!clicked) return;
            if (Main.HoverItem == null || Main.HoverItem.IsAir) return;
            if (Main.HoverItem.ModItem is not StorageDiskBase hoverDisk) return;

            // Find the matching slot in the player inventory.
            for (int i = 0; i < Player.inventory.Length; i++)
            {
                var invItem = Player.inventory[i];
                if (invItem == null || invItem.IsAir) continue;
                if (invItem.ModItem is not StorageDiskBase invDisk) continue;

                if (!MatchesHovered(invItem, invDisk, hoverDisk)) continue;

                if (invDisk.IsArchived)
                    HandleUnarchive(invDisk);
                else if (invDisk.DiskId != Guid.Empty)
                    HandleArchive(i, invDisk);
                // Fresh uninitialized disk — nothing to archive.

                break;
            }
        }

        private static bool MatchesHovered(Item invItem, StorageDiskBase invDisk, StorageDiskBase hoverDisk)
        {
            if (invItem.type != hoverDisk.Item.type)
                return false;

            // Active disk: match by GUID (unique per disk).
            if (hoverDisk.DiskId != Guid.Empty)
                return invDisk.DiskId == hoverDisk.DiskId;

            // Archived or uninitialized: match by archived state and stack count as a
            // tiebreaker (best we can do without a GUID).
            return invDisk.IsArchived == hoverDisk.IsArchived
                && invDisk.ArchivedItems.Count == hoverDisk.ArchivedItems.Count;
        }

        private void HandleArchive(int slot, StorageDiskBase disk)
        {
            if (Main.netMode == NetmodeID.SinglePlayer)
            {
                PerformArchive(disk);
            }
            else
            {
                // Server must perform the archive because StorageWorldSystem is server-authoritative.
                // The server will send back the updated disk item via ArchiveDiskResult.
                NetworkHandler.SendArchiveDiskRequest(
                    ModLoader.GetMod("TerraStorage"), slot, disk.DiskId);
            }
        }

        private static void HandleUnarchive(StorageDiskBase disk)
        {
            // Unarchiving has no world-state side effects; no packet needed.
            // ArchivedItems remain in the disk and are loaded into StorageWorldSystem
            // when the disk is next inserted into a Drive Bay.
            PerformUnarchive(disk);
        }

        //Performs the archive operation directly (singleplayer or server-side).
        public static void PerformArchive(StorageDiskBase disk)
        {
            DBG($"PerformArchive: diskId={disk.DiskId.ToString()[..8]}");
            var items = StorageWorldSystem.Instance.ArchiveDisk(disk.DiskId);
            disk.DiskId = Guid.Empty;
            disk.IsArchived = true;
            disk.ArchivedItems = items;
            DBG($"PerformArchive: done, archivedItems={items.Count}");
        }

        private static void DBG(string msg)
        {
            var path = Requisition.DebugLogPath;
            if (path == null) return;
            try
            {
                using var fs = new System.IO.FileStream(path, System.IO.FileMode.Append, System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite);
                using var sw = new System.IO.StreamWriter(fs);
                sw.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}][net={Terraria.Main.netMode}] {msg}");
            }
            catch { }
        }

        //Performs the unarchive operation directly.
        public static void PerformUnarchive(StorageDiskBase disk)
        {
            disk.IsArchived = false;
            // DiskId stays Empty and ArchivedItems stay populated until Drive Bay insertion.
        }
    }
}
