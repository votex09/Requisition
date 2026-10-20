# List-flag prefilter is blind to the recipe's AcceptedGroups

**Severity:** MEDIUM — craftable recipe never appears in the grid
**Area:** `CoreResolver.RecheckRecipeCraftable`
**Status:** FIXED 2026-08-24 — READY FOR TESTING / HUMAN REVIEW

## Symptom

A recipe the resolver *can* craft is hidden from the recipe grid.

## Cause

`Helpers/Resolver/CoreResolver.cs`, in `RecheckRecipeCraftable`:

```csharp
ok = IsFeasibleFromSnapshot(ing.Type, ing.Stack, available);
```

The ingredient's own type is passed with no reference to `recipe`, so the outer recipe's
`AcceptedGroups` never enter the recursive branch — `CanProduce` applies only the *sub*-recipes'
own groups.

`IngredientSatisfiedDirectly` (`:46-60`) **is** group-aware, so the two halves of the same check
disagree: a slot that groups could fill by sub-crafting a substitute is direct-rejected, then
recursively rejected on the wrong item.

## Repro

```
group AnyGoldBar = { GoldBar, PlatinumBar }
recipes: Crown <- GoldBar x10 + Lens x1   (accepts the group)
         PlatinumBar <- PlatinumOre x4
stock:   GoldBar 0, PlatinumBar 2, PlatinumOre 40, Lens 5

RecheckRecipeCraftable -> false      (recipe never shown)
TryResolveRecipe       -> true, emits a valid 2-step plan
```

10 of 10 flag-false/button-true disagreements in a 4000-world fuzz were this shape.

## The ingCache key

`(ctx, type, stack)` is **not** unsound for what it currently memoises — the memoised function
genuinely ignores groups, so two recipes with different `AcceptedGroups` provably get the same
verdict. The `ctx` component is correct: it captures the whole difference the force-craft
`available.Remove(recipe.OutputType)` makes.

The defect is upstream: the verdict *should* depend on the groups and does not.
**Any fix that makes the prefilter group-aware must add the accepted-group set (or the resolved
substitute) to the cache key, or the cache becomes unsound at that moment.**

## Fix

Pass `recipe` into the prefilter, resolve the slot against own-type-plus-substitutes, and extend
the cache key accordingly.

## Related

[10](10-resolveingredienttype-partial-stock-lockin.md) — the resolver's matching group limitation.
[08](08-prefilter-missing-output-cycle-seed.md) — same call site, different omission.

## Fix applied

The prefilter resolves a slot against own-type-plus-substitutes via `CanFillSlot`. The cache key was widened to `(ctx, group, type, stack)` — `group` is the accepted group containing the ingredient, from `AcceptedGroupFor` — so two recipes naming the same item with different groups can no longer share a verdict.

Covered by `LF-grp*` and `IC-001..004`, which check both evaluation orders.
