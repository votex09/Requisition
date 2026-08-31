# Output-slot stock cache survives a disk-set change

**Severity:** HIGH — phantom stock, dead click
**Area:** `UICraftingPanel` detail panel
**Status:** FIXED 2026-08-24 — READY FOR TESTING / HUMAN REVIEW

## Symptom

The crafting detail panel's output slot shows stock from a different Terminal's network and offers
"Click to take". Clicking does nothing.

## Cause

`Content/UI/Elements/UICraftingPanel.cs:1745-1752` (fields `:166-168`) stamps the cached count on
`(StorageVersion, outputType)` only:

```csharp
if (_outputInStorageVersion != storageVersion || _outputInStorageType != outputType)
{ ... _outputInStorage = CountItem(_diskIds, outputType); }
```

`SetDiskIds` (`:177-184`) sets `_needsRecipeRefresh = true` but leaves `_outputInStorageVersion`
and `_outputInStorageType` untouched, and neither `UpdatePlan` nor `RefreshRecipes` touches them.

`StorageVersion` is bumped only by **content** mutation (`Systems/StorageWorldSystem.cs:287, 392,
410, 431, 465, 477, 493, 587, 618, 630, 647, 662, 683`). Connecting or disconnecting a disk does
not bump it — `GetOrCreateDiskData` (`:182-195`) and `DriveBayEntity.RemoveDisk` (`:262-273`) have
no `StorageVersion++`.

Introduced by the uncommitted work on `fix/ui-click-arbitration`, which replaced a per-frame
`CountItem` with this cache.

## Repro

Two Terminals on separate Drive Bay networks.

1. Open Terminal A, Crafting tab, select Iron Anvil. Network A holds 3 → slot renders 3,
   "x3 in storage / Click to take"
2. Close, walk to Terminal B (network B holds none), open Crafting tab
3. `_craftingPanel` and `_selectedRecipe` survive — the panel is built once in
   `TerminalUIState.OnInitialize` (`:302`) and nothing calls `DeselectRecipe` on open
4. `SetDiskIds` fires; ingredient rows correctly re-resolve against network B
5. **The output slot still draws 3 phantom Iron Anvils.** `TakeFromStorage` (`:1212-1215`)
   recounts live and silently returns

Same via the 2-second `RefreshDiskConnections` poll (`Content/UI/TerminalUIState.cs:946-952`) after
pulling a disk from the bay with the terminal open.

## Fix

Stamp the cache on the disk set too, as `UIFavoritedRecipesPanel` already does with
`_diskIdsToken` (`Content/UI/UIFavoritedRecipesPanel.cs:82, 84-88, 138-145`) — or simply reset
`_outputInStorageVersion = -1` in `SetDiskIds`.

## Fix applied

`SetDiskIds` resets `_outputInStorageVersion = -1`, forcing the next draw to recount.

Needs in-game testing: select a recipe at one Terminal, walk to another on a separate network, confirm the output slot recounts.
