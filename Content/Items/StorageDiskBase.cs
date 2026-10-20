using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using TerraStorage.Common;
using TerraStorage.Systems;

namespace TerraStorage.Content.Items
{
    // Base class for all Storage Disk items. Manages the disk's unique GUID,
    // NBT persistence, tooltip generation, and world-system registration.
    // Concrete tier subclasses only need to override <see cref="Tier"/>.
    public abstract class StorageDiskBase : ModItem
    {
        //The capacity tier of this disk, determining its max stack count.
        public abstract DiskTier Tier { get; }

        // Globally unique identifier linking this item to its <see cref="Common.DiskData"/>
        // in <see cref="StorageWorldSystem"/>. <see cref="Guid.Empty"/> means uninitialized or archived.
        public Guid DiskId { get; set; } = Guid.Empty;

        // True when this disk has been archived. Archived disks carry their items in
        // <see cref="ArchivedItems"/> rather than in <see cref="StorageWorldSystem"/>,
        // allowing them to be moved between worlds. Cannot be inserted into a Drive Bay.
        public bool IsArchived { get; set; } = false;

        // Item stacks embedded directly in this disk's NBT while archived.
        // Populated on archive, consumed on the first Drive Bay insertion after unarchiving.
        public List<StoredItemStack> ArchivedItems { get; set; } = new();

        public override void SetDefaults()
        {
            Item.width = 24;
            Item.height = 24;
            Item.maxStack = 1;
            // Price and rarity both scale with tier level so higher tiers cost and glow more
            Item.value = Item.buyPrice(gold: (int)Tier + 1);
            Item.rare = (int)Tier + 1;
        }

        // Two registered disks are two different stores of items, whatever their tier. Stated to the
        // game rather than left to maxStack, so anything that asks whether they may be pooled -
        // storage's own defragment included - gets the right answer.
        public override bool CanStack(Item source)
            => source.ModItem is not StorageDiskBase other || (DiskId == Guid.Empty && other.DiskId == Guid.Empty);

        public override void OnCreated(ItemCreationContext context)
        {
            // GUIDs are intentionally NOT assigned here. A disk receives its GUID (and is
            // registered with StorageWorldSystem) only when it is first inserted into a
            // Drive Bay. This prevents empty DiskData entries from accumulating every time
            // a disk is crafted or picked up.
        }

        public override void SaveData(TagCompound tag)
        {
            if (DiskId != Guid.Empty)
                tag["diskId"] = DiskId.ToByteArray();

            if (IsArchived)
                tag["archived"] = true;

            // Save ArchivedItems regardless of IsArchived — an unarchived disk (IsArchived=false)
            // may still have pending items waiting to be restored on next Drive Bay insertion.
            if (ArchivedItems.Count > 0)
                tag["archivedItems"] = ArchivedItems.Select(s => s.Save()).ToList();
        }

        public override void LoadData(TagCompound tag)
        {
            if (tag.ContainsKey("diskId"))
                DiskId = new Guid(tag.GetByteArray("diskId"));

            IsArchived = tag.ContainsKey("archived") && tag.Get<bool>("archived");
            if (tag.ContainsKey("archivedItems"))
                ArchivedItems = tag.GetList<TagCompound>("archivedItems")
                    .Select(StoredItemStack.Load).ToList();
        }

        public override ModItem Clone(Item newEntity)
        {
            var clone = (StorageDiskBase)base.Clone(newEntity);
            clone.DiskId = DiskId;
            clone.IsArchived = IsArchived;
            clone.ArchivedItems = ArchivedItems
                .Select(s => new StoredItemStack
                {
                    ItemType = s.ItemType,
                    Stack = s.Stack,
                    PrefixId = s.PrefixId,
                    InsertionOrder = s.InsertionOrder,
                    ModData = s.ModData,
                    // Carries other mods' per-instance GlobalItem state. Save and network both keep
                    // it; dropping it here silently stripped enchantments on every MP bay insert.
                    FullItemTag = s.FullItemTag
                }).ToList();
            return clone;
        }

        public override void NetSend(BinaryWriter writer)
        {
            // Always transmit the full 16-byte GUID so entity sync (message 86) and
            // SyncDiskInsert packets preserve disk identity across the network.
            writer.Write(DiskId.ToByteArray());
            writer.Write(IsArchived);
            writer.Write(ArchivedItems.Count);
            // Use compact binary format (~18 bytes/stack) — TagIO.Write(s.Save()) averages
            // ~373 bytes/stack and would exceed Terraria's 65,535-byte packet limit on large disks.
            foreach (var s in ArchivedItems)
                s.WriteNet(writer);
        }

        public override void NetReceive(BinaryReader reader)
        {
            DiskId = new Guid(reader.ReadBytes(16));
            IsArchived = reader.ReadBoolean();
            int count = reader.ReadInt32();

            // The count sizes the list before a single stack is read, so a forged one allocates
            // whatever it asks for. A disk cannot hold more stacks than its tier does, and Tier is
            // the item's own compile-time property rather than anything the packet chose.
            // tModLoader hands NetReceive a stream over exactly the bytes the sender declared, so
            // leaving the list empty is safe: the unread remainder is its problem, not ours.
            if (!WireCount.FitsDiskCapacity(count, Tier.GetCapacity()))
            {
                ArchivedItems = new List<StoredItemStack>();
                return;
            }

            ArchivedItems = new List<StoredItemStack>(count);
            for (int i = 0; i < count; i++)
                ArchivedItems.Add(StoredItemStack.ReadNet(reader));
        }

        // Appends tier name, stack usage, and a short disk ID to the item tooltip.
        // The tier name is colored using the tier's accent color via Terraria's inline color tag syntax.
        public override void ModifyTooltips(List<TooltipLine> tooltips)
        {
            var tierColor = Tier.GetColor();
            int tierNumber = (int)Tier + 1;
            int capacity = Tier.GetCapacity();
            // Inline color tag: [c/RRGGBB:text]
            tooltips.Add(new TooltipLine(Mod, "DiskTier", $"[c/{tierColor.R:X2}{tierColor.G:X2}{tierColor.B:X2}:Tier {tierNumber} Storage Disk - Holds {capacity} item stacks]"));

            if (IsArchived)
            {
                tooltips.Add(new TooltipLine(Mod, "DiskArchived", "[c/FF9944:Archived]"));
                tooltips.Add(new TooltipLine(Mod, "DiskArchivedContent", $"Contains: {ArchivedItems.Count} stacks"));
                tooltips.Add(new TooltipLine(Mod, "DiskArchiveHint", "Middle-click to unarchive (binds items to this world)"));
            }
            else if (DiskId != Guid.Empty)
            {
                var diskData = StorageWorldSystem.Instance.GetDiskData(DiskId);
                int used = diskData?.UsedStacks ?? 0;
                int max = Tier.GetCapacity();
                tooltips.Add(new TooltipLine(Mod, "DiskUsage", $"Stored: {used} / {max} stacks"));

                // Show only the first 8 hex chars of the GUID to keep the tooltip concise
                string shortId = DiskId.ToString()[..8];
                tooltips.Add(new TooltipLine(Mod, "DiskId", $"ID: {shortId}..."));
                tooltips.Add(new TooltipLine(Mod, "DiskArchiveHint", "Middle-click to archive"));
            }
            else if (ArchivedItems.Count > 0)
            {
                // Unarchived but not yet inserted into a Drive Bay
                tooltips.Add(new TooltipLine(Mod, "DiskUnarchived", "Place in a Drive Bay to restore items"));
                tooltips.Add(new TooltipLine(Mod, "DiskUnarchivedContent", $"Contains: {ArchivedItems.Count} stacks"));
            }
            else
            {
                tooltips.Add(new TooltipLine(Mod, "DiskUninitialized", "Uninitialized - place in a Drive Bay"));
            }
        }

        // Assign a specific Guid to this disk (for restoration).
        public void AssignDiskId(Guid id)
        {
            DiskId = id;
        }

        public static int GetItemTypeForTier(DiskTier tier) => tier switch
        {
            DiskTier.Tier1 => ModContent.ItemType<StorageDiskTier1>(),
            DiskTier.Tier2 => ModContent.ItemType<StorageDiskTier2>(),
            DiskTier.Tier3 => ModContent.ItemType<StorageDiskTier3>(),
            DiskTier.Tier4 => ModContent.ItemType<StorageDiskTier4>(),
            DiskTier.Tier5 => ModContent.ItemType<StorageDiskTier5>(),
            DiskTier.Tier6 => ModContent.ItemType<StorageDiskTier6>(),
            _              => 0
        };

        // Upgrade ingredient options per tier.  Each outer entry is one alternative recipe;
        // the server and client both use this to determine what to consume.
        // Returns null for max-tier disks (no upgrade available).
        public static (int itemType, int count)[][] GetUpgradeOptions(DiskTier tier) => tier switch
        {
            DiskTier.Tier1 => new[]
            {
                new (int, int)[] { (ItemID.CrimtaneBar,    10), (ItemID.GoldBar,     1), (ItemID.Lens, 1) },
                new (int, int)[] { (ItemID.DemoniteBar,    10), (ItemID.PlatinumBar, 1), (ItemID.Lens, 1) },
            },
            DiskTier.Tier2 => new[] { new (int, int)[] { (ItemID.HellstoneBar,   10), (ItemID.Obsidian,    3), (ItemID.Wire, 4) } },
            DiskTier.Tier3 => new[] { new (int, int)[] { (ItemID.HallowedBar,    10), (ItemID.SoulofLight, 2), (ItemID.Wire, 4) } },
            DiskTier.Tier4 => new[] { new (int, int)[] { (ItemID.ChlorophyteBar, 10), (ItemID.SoulofSight, 2), (ItemID.Wire, 4) } },
            DiskTier.Tier5 => new[] { new (int, int)[] { (ItemID.ShroomiteBar,   10), (ItemID.Wire, 4) } },
            _              => null
        };

        // Answers "can this tier be upgraded" without building the options, so per-frame UI layout
        // does not allocate. Must list exactly the tiers GetUpgradeOptions returns non-null for.
        public static bool HasUpgradePath(DiskTier tier) => tier switch
        {
            DiskTier.Tier1 or DiskTier.Tier2 or DiskTier.Tier3
                or DiskTier.Tier4 or DiskTier.Tier5 => true,
            _                                       => false
        };
    }
}
