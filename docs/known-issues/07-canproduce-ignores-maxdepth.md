# CanProduce ignored MaxDepth — the recursion-depth slider was inert

**Severity:** HIGH
**Area:** `CoreResolver.CanProduce`
**Status:** FIXED 2026-08-24 · kept for the record

## Symptom

Dragging the recursion-depth slider changed nothing about which recipes appeared craftable.
At depth 1 the grid and the ingredient preview still showed long chains as craftable while the
craft button refused them.

## Cause

`ResolveRecursive` opened with `if (depth > MaxDepth) return false;`. `CanProduce` — the
feasibility mirror reached from `RecheckRecipeCraftable` via `IsFeasibleFromSnapshot` — took no
`depth` parameter at all and was bounded only by the cycle guard.
`UICraftingPanel.cs:596` set `MaxDepth = _recursionDepth` on the very resolver used for the flag,
where it was inert. The preview resolver did not set `MaxDepth` at all.

## Repro (before the fix)

```
12-link chain 1000 <- 1001 <- ... <- 1012, only 1012 in stock

MaxDepth= 1: list=True  button=False   MISMATCH
MaxDepth= 2: list=True  button=False   MISMATCH
MaxDepth= 5: list=True  button=False   MISMATCH
MaxDepth=10: list=True  button=False   MISMATCH
```

## Fix applied

- `CanProduce` takes `int depth` and returns false when `depth >= MaxDepth`, placed **after** the
  in-stock branch so an ingredient already in stock is never refused for its position in the tree.
- Recurses with `depth + 1`; `IsFeasibleFromSnapshot`, `IsRecipeFeasibleShared` and
  `CanSubCraftRemainder` all enter at 0.
- `UICraftingPanel.RebuildIngredientCache` sets `MaxDepth = _recursionDepth` on the preview
  resolver, so list flag, preview and craft path share one depth.

Tests `MD-01a..MD-20c` assert list flag, feasibility and preview all agree with `ResolveRecursive`
at depths 1, 2, 5, 10 and 20; `MD-100` asserts a chain within the limit stays feasible.

## Related

[18](18-maxdepth-cut-precedes-stock-check.md) — the remaining off-by-one in `ResolveRecursive`.
