# A `globalData` key was read as "this stack is its own item"

**Severity:** HIGH — storage never stacked anything in a modded world
**Area:** storage identity (grid, deposit, withdrawal, defragment)
**Status:** FIXED 2026-08-25 — READY FOR TESTING / HUMAN REVIEW

## Symptom

Two 1-stacks of the same fruit deposited into storage stayed two 1-stacks, in two grid cells. The
only way to get one 2-stack was to withdraw both, stack them by hand in the player inventory, and
deposit the result.

## Cause

`Common/DiskData.cs:278` decided whether a stack stands for one particular item from the presence
of a `globalData` key in its serialized `ItemIO` tag, and `PerInstanceDataMatches` byte-compared
that blob as the stacking rule.

`ItemIO.Save` writes `globalData` for **every** item as soon as any loaded mod overrides
`GlobalItem.SaveData`. `CalamityMod.Items.CalamityGlobalItem.SaveData` writes three keys
unconditionally, for an apricot as readily as for a weapon. So the verdict was true for 100% of
items and every stack became its own item:

| site | consequence |
|---|---|
| `StorageWorldSystem.GetConsolidatedItems` | every stack got its own grid cell |
| `DiskData.MatchingSlots` → `PlanWithdrawal` | a withdrawal could only ever drain one stack |
| `StorageWorldSystem.Defragment` → `PlanDonorMove` | stacks relocated whole, never merged |
| `DiskData.InsertItem` | merge gated on byte-equality of a blob mods use for transient state |

Counted against a real world save: **191 of 191 stacks carried `globalData`; 0 carried ModItem save
data.** The blob is third-party state that says nothing about whether two items are the same thing —
byte-comparing it is strictly stricter than the rule the player inventory and chests use.

## Fix

Two questions were conflated, and are now separate (`Common/StackIdentity.cs`):

- **Preserve this stack's mod state** — `MustPreserveFullTag`. Unchanged: any stack carrying
  ModItem data or mod-written bytes still keeps its full tag, so extraction hands the item back
  intact. All 191 tags survive.
- **Is this stack its own item** — `IsUnique`. Now asks the game: ModItem save data (a disk's GUID,
  an unloaded item's original tag) or `ItemLoader.CanStack` refusing the stack against a plain item
  of the same type and prefix. Cached per stack on `StoredItemStack.IsUnique`.

`ItemLoader.CanStack` is what a chest asks, so the two now agree by construction. It covers
`UnloadedItem` (whose `CanStack` returns false) and, now, storage disks — `StorageDiskBase` states
that two registered disks may not stack rather than leaving it to `maxStack = 1`. It is asked both
ways round, because tModLoader runs the hooks on the destination only.

Whether two poolable stacks carry the *same* mod state is a separate question from identity, and
byte equality is the right answer to it — it asks "would folding these lose anything", not "are
these the same item" (`DiskData.ModStateMatches`). So:

- **deposits** follow the full chest protocol: when an item lands on a stack carrying different
  state, `ItemLoader.OnStack` gets its documented chance to fold the two before the count moves,
  and the leftover is re-serialized in case the mod drained it;
- **defragmenting** declines to merge stacks whose state differs. It considers every pair of stacks
  on two disks, and an `OnStack` round trip inside that sweep would cost more than it buys.

`ExtractItem` reuses a stack's full tag only when every stack drawn from carried the same state.
Plain stacks carry a tag too now that they pool, so keeping it is worth doing — but stamping one
stack's state onto units drawn from another is
[05](05-extractitem-stamps-tag-on-whole-withdrawal.md) all over again.

**Superseded 2026-08-26.** Deciding that *after* planning left only one lever when the draws
disagreed — drop the state from all of them — so making these stacks pool made a mixed withdrawal
return them with none. The plan itself now ends at the first stack outside the run it opened, so the
question no longer arises: see
[25](25-craft-costed-against-a-count-it-cannot-withdraw.md), "the plan ends at the boundary".

## Accepted, not fixed

The terminal grid pools on type and prefix. An item a mod keeps per-instance state on **without**
declaring it through `CanStack` — Calamity's `Charge` and `AppliedEnchantment` are the live example
— is therefore poolable, so a charged copy shares a cell with plain ones and cannot be picked out
of that cell deliberately. Its state is not lost: since 2026-08-26 a draw cannot mix states at all,
so `ExtractItem` always hands the tag back — the cell is drawn a run at a time instead, which
[25](25-craft-costed-against-a-count-it-cannot-withdraw.md) records. Keying the grid on mod state as
well was tried and reverted — it produces
cells that draw identically, are not individually addressable (withdrawal routes on type and prefix
alone), and desynchronise from delta sync, which buckets the same way. Making them addressable
means carrying the state key through `MatchingSlots` and `DeltaItemEntry`, i.e. a wire-format
change; worth doing only if a mod is found whose state a player must be able to select.

`ComputeDelta` and `ApplyDeltaToDisk` split unique from pooled stacks on the same verdict instead of
on `ModData != null`, so the two sides of delta sync cannot disagree. Snapshots and stack copies
carry the verdict across rather than re-deciding it: every server operation snapshots every disk,
and re-deciding would put a full item deserialization per stack on the netcode path.

Verified by `SI-*` in `Tests/Program.cs` (red before the fix on "a plain fruit carrying another
mod's GlobalItem bytes is NOT its own item"), and by replaying the new rule over the real save:
191 cells → 190, two Wulfrum Battery 1-stacks collapsing into one, no tag dropped.

Needs in-game testing: deposit two 1-stacks of the same item and confirm one cell; withdraw a stack
larger than one storage stack; defragment a network holding the same item on two disks; withdraw
and re-insert a storage disk; confirm an item deposited with mod state comes back with it.

## Related

[04](04-defragment-destroys-per-instance-data.md),
[05](05-extractitem-stamps-tag-on-whole-withdrawal.md),
[12](12-storagediskbase-clone-drops-fullitemtag.md).
