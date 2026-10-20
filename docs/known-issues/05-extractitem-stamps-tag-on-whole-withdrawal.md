# ExtractItem stamps one stack's tag onto the whole withdrawal

**Severity:** HIGH — item duplication
**Area:** storage extraction
**Status:** FIXED 2026-08-24 — READY FOR TESTING / HUMAN REVIEW

## Symptom

Withdrawing a stack that happens to include one item carrying per-instance mod data returns the
ENTIRE amount carrying that data.

## Cause

`Common/DiskData.cs:95-141`. The loop walks every stack matching `type` + `prefix`, including
unique ones, and records `extractedModData` / `extractedFullTag` from **any** stack it fully
consumes (`:118-121`). It then rebuilds a **single** item from that tag and sets
`result.stack = extracted` (`:135-139`) — the total across all consumed stacks.

The comment at `:100-102` assumes "only one stack is ever consumed" for tagged items. Nothing
enforces that: the loop never checks whether a stack is tagged before consuming it.

`Systems/StorageWorldSystem.ExtractItem` repeats the pattern across disks — `result ??= extracted`
(`:382`) keeps the first disk's item, then `result.stack = totalExtracted` (`:394`) overwrites the
count.

## Repro

One disk holds `[stack#1: Iron Bar x1 with FullItemTag (enchanted), stack#2: Iron Bar x300 plain]`.
`GetConsolidatedItems` shows them as two cells (`:240-252`), so the player sees "Iron Bar x300"
and clicks to withdraw all 300.

1. stack#1 matches, `canTake=1`, drops to 0, removed, `extractedFullTag` = the enchanted tag
2. stack#2, `canTake=299`, drops to 1
3. `extracted == 300`, `extractedFullTag != null`, so
   `result = ItemIO.Load(enchantedTag); result.stack = 300`

**300 enchanted Iron Bars for one. 299 copies of per-instance mod state created from nothing,
and the "unique" cell the player never clicked has silently vanished.**

## Fix

- In the extraction loop, skip stacks whose `ModData`/`FullItemTag` is non-null unless the request
  explicitly targets that identity — the dedicated `ExtractItemWithModData` /
  `ExtractItemWithFullItemTag` paths already exist for that.
- If a tagged stack must be included, return it as a separate `Item` rather than folding its count
  into a differently-identified one.
- Stop reusing the first disk's `Item` instance as the carrier in
  `Systems/StorageWorldSystem.cs:382`.

## Related

[04](04-defragment-destroys-per-instance-data.md),
[12](12-storagediskbase-clone-drops-fullitemtag.md).

## Fix applied

Extraction is two passes: plain stacks are drained first, and a stack with per-instance data is only taken as a fallback when nothing plain matched — alone, never combined. A new `uniqueStack` out-parameter reports that case, and `StorageWorldSystem.ExtractItem` returns such an item as-is rather than overwriting its stack with a cross-disk total. Once plain items are in hand it passes `allowUniqueFallback: false`, so a unique stack is never pulled out only to be mixed in.

Disks still extract correctly: every disk stack is unique, so the fallback pass takes exactly one.

Needs in-game testing: withdraw a large stack from a network that also holds one enchanted copy; withdraw and re-insert a storage disk.

## The same harm, inverted — closed 2026-08-26

The fix above stops one stack's state being stamped onto units from another. It left the opposite
loss open one level down, and [24](24-globaldata-treated-as-item-identity.md) made it reachable by
letting stacks that carry mod-written bytes pool: `DiskData.ExtractItem` decided *after* planning
whether the stacks it had drawn from agreed, and when they did not it dropped the state from all of
them. Two plain stacks with different `globalData` drawn in one withdrawal came back carrying
neither. Prefix was the same story — `Matches(type, -1)` matches any prefix, so a crafting draw
could span two and stamp one over both.

The rule no longer runs after the plan; it *is* the plan. `StackSelection.PlanWithdrawal` ends its
plain pass at the first stack outside the run it opened, on `DiskData.CanMergeStacks` — prefix and
mod state together — so a withdrawal can neither mix states nor lose them, and the caller's handle
budget decides whether the next run opens another item or ends the sweep. See
[25](25-craft-costed-against-a-count-it-cannot-withdraw.md), "the plan ends at the boundary". The
cost is that a one-item withdrawal now hands over the first run rather than the whole cell; that is
recorded there too.

Verified by `SB-*` alongside the `SL-*` assertions above, which still pin this issue's own rule
unchanged.
