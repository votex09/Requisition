# Feasibility queries started one depth level too shallow

**Severity:** CRITICAL — reintroduced the exact bug the change was written to fix
**Area:** `CoreResolver` — every recipe-level feasibility entry point
**Status:** FIXED 2026-08-24 — READY FOR TESTING / HUMAN REVIEW

## How it got here

This defect was **introduced by the fix for [07](07-canproduce-ignores-maxdepth.md)**, in the same
session, and caught by a second adversarial review round. It is recorded because the failure mode
is more instructive than the bug: a correct-looking fix, a passing test, and the original symptom
still reachable in-game.

## Symptom

At one specific slider value the recipe grid and the ingredient preview said craftable while the
craft button said *Missing Materials* — an orange "will be sub-crafted" square and a green list
row on a dead button. Verbatim the complaint that started this work.

Reachable at every slider position 1..10 (`MaxRecursionDepth` is 10), for any chain of exactly
`depth + 1` crafts.

## Cause

Depth was measured from two different origins.

`ResolveRecursive` enters a recipe at depth 0 and resolves that recipe's ingredients at `depth + 1`.
The three recipe-level feasibility entry points started their *ingredient* query at 0, handing it
one extra level of budget:

| entry point | queried | started at |
|---|---|---|
| `IsRecipeFeasibleShared` | an ingredient | 0 |
| `RecheckRecipeCraftable` prefilter | an ingredient | 0 |
| `CanSubCraftRemainder` | an ingredient | 0 |
| `IsFeasibleFromSnapshot` (public) | an item | 0 — correct |

For a chain of *n* crafts the button succeeded iff `n <= MaxDepth`, the flag and `Satisfiable` iff
`n - 1 <= MaxDepth`. They diverged at exactly `MaxDepth == n - 1`.

## Repro (before the fix)

```
12-craft chain, only the leaf in stock

MaxDepth  button  listFlag  previewSat
      10   False     False       False
      11   False      True        True   <== MISMATCH
      12    True      True        True
```

## Why the test did not catch it

`FeasibilityHonoursMaxDepth` swept `{1, 2, 5, 10, 20}` against a 12-link chain. The only divergent
value is 11. **The test passed because 11 was not in the list, not because the code was right.**

A sampled sweep cannot find a boundary. The sweep is now contiguous:

```csharp
for (int depth = 1; depth <= CHAIN_LEN + 3; depth++)
```

## Fix applied

A named constant makes the origin explicit and greppable:

```csharp
// Depth at which a query about one of a recipe's INGREDIENTS starts.
private const int IngredientDepth = 1;
```

passed by `IsRecipeFeasibleShared`, the `RecheckRecipeCraftable` prefilter and
`CanSubCraftRemainder`. The public item-level `IsFeasibleFromSnapshot` still starts at 0.

## Second correction, same session

The [10](10-resolveingredienttype-partial-stock-lockin.md) fix then changed the arithmetic again.
`ResolveIngredientSlot` takes in-stock material directly and only calls `ResolveRecursive` for the
remainder, so the plan side stopped spending a depth level on a plain stock lookup. That removed
the compensation that had made `CanProduce`'s post-stock `depth >= MaxDepth` equivalent to
`ResolveRecursive`'s pre-stock `depth > MaxDepth`, and the mismatch reappeared at 11 with the
sign flipped (button true, list false).

`CanProduce` was realigned to `depth > MaxDepth`. **Both sides now bound recipe EXPANSION only;
taking something out of storage costs nothing on either side.** That is also what resolves
[18](18-maxdepth-cut-precedes-stock-check.md).

## Guard against recurrence

- `MD-*` sweeps every depth from 1 to `CHAIN_LEN + 3` and asserts list flag, feasibility and
  preview each agree with `ResolveRecursive`.
- `DL-*` asserts a single craft off in-stock material works even at `MaxDepth` 0.

If the depth rule is touched again, change `ResolveRecursive` and `CanProduce` **together** and
re-run both. They are two encodings of one rule and have now drifted apart twice.
