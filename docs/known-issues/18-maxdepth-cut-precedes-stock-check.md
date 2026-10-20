# MaxDepth cut sits above the in-stock check

**Severity:** LOW — off-by-one, `MaxDepth = 0` unreachable today
**Area:** `CoreResolver.ResolveRecursive`
**Status:** FIXED 2026-08-24 — READY FOR TESTING / HUMAN REVIEW

## Symptom

An ingredient sitting literally in stock is refused purely for its position in the tree, so a
chain of exactly `MaxDepth + 1` fails even when all its leaves are in stock.

## Cause

`Helpers/Resolver/CoreResolver.cs`:

```csharp
if (depth > MaxDepth)
    return false;                                          // <- before the stock check

if (available.TryGetValue(itemType, out int have) && have >= needed) { ... }
```

Taking something out of storage is not recursion and should not consume depth.

## Repro

```
recipe: 500 <- 501 x2
stock:  { 501: 5 }
MaxDepth = 0

-> false
```

`MaxDepth = 0` is unreachable through the UI (`UICraftingPanel.cs:2388` clamps to
`[1, MaxRecursionDepth]`), but the off-by-one still shifts every chain length by one.

## Fix

Move the depth check below the direct-stock branch, as `CanProduce` now does:

```csharp
if (available.TryGetValue(itemType, out int have) && have >= needed) { ...; return true; }
if (depth >= MaxDepth) return false;
```

Note `CanProduce` uses `depth >= MaxDepth` after the stock branch while `ResolveRecursive` uses
`depth > MaxDepth` before it. The two accept the same chains today only because the off-by-one
and the placement cancel out — fixing one without the other will desynchronise the list flag from
the craft button. Change both together and re-run tests `MD-01a..MD-20c`.

## Related

[07](07-canproduce-ignores-maxdepth.md) — the `CanProduce` half, fixed.

## Fix applied

Resolved as part of the group-mixing change: `ResolveIngredientSlot` takes in-stock material directly and only calls `ResolveRecursive` for the remainder, so a stock lookup no longer consumes a depth level on either side. `CanProduce` was realigned to `depth > MaxDepth` to match, and both sides now bound recipe EXPANSION only.

Covered by `DL-*` (a single craft off in-stock material works even at MaxDepth 0) and by the contiguous `MD-*` sweep.
