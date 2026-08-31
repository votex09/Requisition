# Defragment destroys and duplicates per-instance mod data

**Severity:** HIGH — silent item corruption
**Area:** storage maintenance
**Status:** FIXED 2026-08-24 — READY FOR TESTING / HUMAN REVIEW

## Symptom

Running Defragment strips modded per-instance state (enchantments, Calamity/Entropy item data)
from stored items, or copies one item's state onto a whole stack of plain ones.

## Cause

`Common/DiskData.InsertItem` keeps two identities apart — `ModData` (ModItem NBT) **and**
`FullItemTag` (the full `ItemIO` tag, populated whenever any mod wrote `globalData`,
`Common/DiskData.cs:52-56`) — and `PerInstanceDataMatches` (`Common/DiskData.cs:247-271`) refuses
to merge stacks whose `globalData` differs.

`Systems/StorageWorldSystem.Defragment` knows nothing about `FullItemTag`:

- `:526` — the "unique item, move whole" branch tests **only** `stack.ModData != null`.
  A stack unique solely by `FullItemTag` falls into the plain-merge branch.
- `:542-547` — merge predicate is `ItemType && PrefixId && existing.ModData == null && Stack < maxStack`.
  `FullItemTag` is never compared, on either side.
- `:559-566` — a freshly created slot is built with `ModData = null` and **no `FullItemTag` at all**.

## Repro

Any playthrough with a mod writing per-instance `GlobalItem` state (the code's own comments name
Calamity and Entropy enchantments).

**Destroys:** disk 1 holds a plain stackable item; disk 2 holds the same item enchanted
(`FullItemTag` set, `ModData == null`). Defragment. Donor has `ModData == null`, so it takes the
plain branch and merges (or is re-added at `:559` without the tag). **Enchantment gone.**

**Duplicates:** reverse the roles — the target stack is the enchanted one. The predicate at
`:544-547` passes, the plain items fold into the enchanted stack, and `DiskData.ExtractItem`
later hands all of them back carrying the enchanted tag.

## Fix

Treat `FullItemTag != null` exactly like `ModData != null`:

- move those stacks whole (`:526`)
- require `existing.FullItemTag == null` in the merge predicate (`:546`)
- carry `ModData` and `FullItemTag` onto the new `StoredItemStack` (`:559-566`)

Reuse `DiskData.PerInstanceDataMatches` rather than re-implementing the identity test.

## Related

[05](05-extractitem-stamps-tag-on-whole-withdrawal.md) and
[12](12-storagediskbase-clone-drops-fullitemtag.md) — same field, two more places that forget it.

## Fix applied

The identity rule now lives in `DiskData` and is used everywhere: `HasPerInstanceData` (ModData OR a globalData tag) and `CanMergeStacks` (type + prefix + `PerInstanceDataMatches`). `Defragment` moves any stack with per-instance data whole, merges only through `CanMergeStacks`, and carries `ModData` and `FullItemTag` onto newly created stacks.

Needs in-game testing with a mod that writes GlobalItem state: defragment a network holding both plain and enchanted copies of one item.
