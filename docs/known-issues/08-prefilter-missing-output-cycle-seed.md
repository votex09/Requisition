# List-flag prefilter plans routes looping back through the item being crafted

**Severity:** HIGH — recipe grid shows craftable, craft button refuses
**Area:** `CoreResolver.IsFeasibleFromSnapshot`
**Status:** FIXED 2026-08-24 — READY FOR TESTING / HUMAN REVIEW

## Symptom

A recipe whose ingredients are only reachable by crafting its own output reads craftable in the
grid; the button says "Missing Materials" or "Nothing to Craft".

## Cause

Three feasibility entry points, only two of which seed the cycle guard with the output:

| entry point | seeds `OutputType`? |
|---|---|
| `IsRecipeFeasibleShared` (`:230-231`) | yes |
| `CanSubCraftRemainder` (`:602-603`) | yes (added 2026-08-24) |
| `IsFeasibleFromSnapshot` (`:255`) | **no — only clears** |

`ResolveRecursive` / `ResolveRecipe` carry `resolving = { itemType }`, forbidding any production
of the output inside its own subtree. `IsFeasibleFromSnapshot` is the per-ingredient prefilter
used by `RecheckRecipeCraftable`, and it is the **sole** decider for single-ingredient recipes,
since `needsSharedConfirm` requires `realIngredients >= 2`.

This is exactly the class the comment at `CoreResolver.cs:476-482` claims to have closed.

## Repro

```
recipes: A x3 <- A x3      (the recipe under test)
         A x1 <- B x2
stock:   B = 20, no A

RecheckRecipeCraftable -> true
TryResolveRecipe       -> false
```

Also without the ingredient being the output:

```
recipes: 10 x3 <- 9 x3 ; 9 x2 <- 10 x3 ; 10 x3 <- 11 x1
stock:   {10: 5, 11: 2}
```

The prefilter satisfies item 9 by crafting item 10 (the output) via the alternative recipe;
the button's `resolving = {10}` forbids it. 38 of 290 fuzz false-positives.

## Fix

Seed `recipe.OutputType` before the prefilter call, as the other two entry points do.
`IsFeasibleFromSnapshot` is public and has other callers, so add the seed at the
`RecheckRecipeCraftable` call site (or via a private overload) rather than changing its semantics
for everyone.

Note the `ingCache` key `(ctx, type, stack)` already carries the excluded output as `ctx`, so it
does not need widening for this fix — but see
[11](11-prefilter-ignores-accepted-groups.md), which does require widening it.

## Fix applied

The prefilter no longer calls `IsFeasibleFromSnapshot`. It calls a new `IsIngredientFeasible`, which seeds the cycle guard with `recipe.OutputType` before testing the slot — matching the `resolving = { itemType }` the craft button plans under. The public `IsFeasibleFromSnapshot` keeps its item-level semantics for other callers.

Covered by `LF-loop*`.
