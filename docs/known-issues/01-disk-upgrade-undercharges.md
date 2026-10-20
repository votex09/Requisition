# Disk tier upgrade completes after under-paying for materials

**Severity:** CRITICAL — item duplication
**Area:** disk upgrades, single player and server
**Status:** FIXED 2026-08-24 — READY FOR TESTING / HUMAN REVIEW

## Symptom

A Tier N to N+1 upgrade succeeds while storage holds fewer materials than the option lists.
Threshold is `have >= ceil(need / 2)`.
Tiers 2-6 are obtainable *only* through this button, so this is the mod's main progression sink.

## Cause

Both upgrade paths compute the shortfall themselves, then ask the resolver only for the shortfall:

- `Content/UI/Elements/UIDiskPanel.cs:258` (affordability) and `:290` (execution)
- `Systems/NetworkHandler.cs:1165`

```csharp
int have = CountItem(diskIds, itemType);                           // 6
if (have < need)                                                   // need = 10
    var plan = RecipeResolver.Resolve(itemType, need - have, ...); // asks for 4
```

`Resolve` rebuilds its pool from **full storage** (`Helpers/RecipeResolver.cs:104`), so
`available[itemType] == have == 6`. `CoreResolver.ResolveRecursive` sees `6 >= 4`, deducts, and
returns true with **zero steps**.

`Helpers/RecipeResolver.cs:133` then sets `IsFeasible = true` on an empty step list.
`ResolveForceCraft:551` and `ResolveRecipe:596` both guard with `&& plan.Steps.Count > 0`;
`Resolve` does not. `CraftingPlan.IsDirectExtract` (`:49`) flags exactly this case and neither
caller checks it.

`ExecutePlan` loops zero steps and returns air, so nothing is crafted. The following
`ExtractItem(diskIds, itemType, need)` **discards its return value**, so the shortfall is
invisible and the upgrade proceeds unconditionally.

## Repro

Tier 1 to 2 needs `CrimtaneBar 10, GoldBar 1, Lens 1` (`Content/Items/StorageDiskBase.cs:183`).
Storage: 6 Crimtane Bars, 1 Gold Bar, 1 Lens, no Crimtane Ore.

1. `have=6 < need=10` gives `Resolve(CrimtaneBar, 4)`, feasible because `6 >= 4`
2. `Craftable = true`, Upgrade button enabled
3. `ExecutePlan` returns air, `ExtractItem(..., 10)` removes only 6
4. Disk upgraded to Tier 2

**4 Crimtane Bars materialised from nothing.**

## Second manifestation — partial craft, more common

Storage: 3 Crimtane Bars + 21 Crimtane Ore.
`Resolve(CrimtaneBar, 7)` sees `available=3`, deficit 4, plans one step producing 4 bars.
Storage reaches 7. `ExtractItem(..., 10)` takes 7. Paid 7 bars for a 10-bar upgrade.

## Fix

Do **not** add `Steps.Count > 0` to `Resolve` — direct extract is legitimate there.

- Resolve against `need`, not `need - have`, and let the resolver subtract once.
- Reject `plan.IsDirectExtract` in both callers.
- Make extraction checked: abort and refund the whole upgrade when any ingredient comes up short.

## Related

[02](02-server-upgrade-no-material-check.md) — same handler, no server-side gate at all.
[03](03-executeplan-unchecked-extract-insert.md) — the unchecked extract that hides the shortfall.

## Fix applied

New `RecipeResolver.TryConsumeMaterials` acquires every material as one transaction: resolves each ingredient for its FULL need (never the shortfall), rejects a no-step plan when stock is short, extracts with a checked return, and refunds everything already taken on any failure. Both upgrade paths call it — `UIDiskPanel.TryUpgrade` and `NetworkHandler.HandleUpgradeDiskRequest` — and neither installs the upgrade unless it returns true. `BuildStatesForOption` now resolves the full need and requires `Steps.Count > 0`.

Needs in-game testing: upgrade with exactly enough, with one short, and with a craftable shortfall.
