# StorageDiskBase.Clone drops FullItemTag

**Severity:** MEDIUM — silent item corruption, multiplayer only
**Area:** disk archive
**Status:** FIXED 2026-08-24 — READY FOR TESTING / HUMAN REVIEW

## Symptom

In multiplayer, every archived item's modded per-instance state is destroyed when the disk is
placed into a Drive Bay.

## Cause

`Content/Items/StorageDiskBase.cs:84-92` deep-copies `ArchivedItems` field by field and copies
`ModData` but not `FullItemTag`:

```csharp
clone.ArchivedItems = ArchivedItems
    .Select(s => new StoredItemStack {
        ItemType = s.ItemType, Stack = s.Stack, PrefixId = s.PrefixId,
        InsertionOrder = s.InsertionOrder, ModData = s.ModData      // FullItemTag missing
    }).ToList();
```

The NBT round-trip (`SaveData:64-65` -> `StoredItemStack.Save:56-63` -> `Load:140-141`) and the
network round-trip (`WriteNet:155-162`) both preserve `FullItemTag` correctly. Only `Clone` loses it.

## Repro

Multiplayer.

1. Archive a disk containing an enchanted item (`DiskArchivePlayer.cs:90-98` keeps `FullItemTag`)
2. Unarchive it, then shift-click it into a Drive Bay
3. `DriveBayEntity.InsertDisk:201-220` — on an MP client the restoration branch is skipped and
   `ArchivedItems` is deliberately left intact to ride along in the sync packet
4. `:241` / `:252` — `DiskSlots[slot] = diskItem.Clone()` **strips `FullItemTag` from every stack**
5. `Systems/StoragePlayerSystem.cs:203` sends that clone; the server calls `RegisterDiskWithItems`
   with the stripped list

Single player is unaffected — `InsertDisk` restores from the original list before cloning.

## Fix

Add `FullItemTag = s.FullItemTag` at `Content/Items/StorageDiskBase.cs:91`.

One line. Lowest effort-to-value ratio in this folder.

## Related

[04](04-defragment-destroys-per-instance-data.md),
[05](05-extractitem-stamps-tag-on-whole-withdrawal.md).

## Fix applied

Added `FullItemTag = s.FullItemTag` to the `ArchivedItems` projection in `Clone`.

Needs multiplayer testing: archive a disk holding an enchanted item, unarchive, insert into a Drive Bay.
