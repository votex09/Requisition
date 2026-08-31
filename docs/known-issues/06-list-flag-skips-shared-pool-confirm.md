# List flag skips the shared-pool confirm when every slot looks directly satisfied

**Severity:** HIGH — recipe grid shows craftable, craft button refuses
**Area:** `CoreResolver.RecheckRecipeCraftable`
**Status:** FIXED 2026-08-24 — READY FOR TESTING / HUMAN REVIEW

## Symptom

A recipe naming the same item in two slots reads craftable in the grid while the craft button
says "Missing Materials".

## Cause

`Helpers/Resolver/CoreResolver.cs`, `RecheckRecipeCraftable`:

```csharp
bool needsSharedConfirm = realIngredients >= 2 && (!allDirect || usedGroupSubstitute);
if (needsSharedConfirm && !IsRecipeFeasibleShared(recipe, available))
    return false;
```

When every slot passes `IngredientSatisfiedDirectly` and no group substitute was used,
`IsRecipeFeasibleShared` never runs. `IngredientSatisfiedDirectly` tests each slot against the
**undeducted** pool, so two slots naming the same item are both compared against the same stock.
`IsRecipeFeasibleShared` is the only place that deducts.

## Repro

```
recipe: TABLE <- WOOD x4 + WOOD x6      (needs 10)
stock:  WOOD = 6

RecheckRecipeCraftable -> true
TryResolveRecipe       -> false
```

Reproduced by probe against the shipped core. A 4000-world fuzz attributed 220 of 290
flag-true/button-false disagreements to this shape.

## Fix

Drop the `(!allDirect || usedGroupSubstitute)` clause, or extend it:

```csharp
bool needsSharedConfirm = realIngredients >= 2
    && (!allDirect || usedGroupSubstitute || HasRepeatedIngredientType(recipe));
```

Dropping the clause outright is simplest and safest; measure before assuming the shared confirm
is too expensive to always run.

## Notes

The *preview* half of this defect was fixed on 2026-08-24 — `ComputeIngredientPreview` now sums
duplicate slots into one view (tests `DS-001..007`). Only the list flag is still affected.
`TerrariaRecipeEnvironment.ToCore` (`:45-47`) copies every `requiredItem` entry without merging,
so duplicate slots do reach the core.

## Related

[19](19-preview-collapses-duplicate-slots.md) — the preview half, fixed.
[03](03-executeplan-unchecked-extract-insert.md) — what makes a wrong verdict cost items.

## Fix applied

The shared confirm now also runs when a recipe names one item in more than one slot (`HasRepeatedIngredientType`). The `allDirect` / `usedGroupSubstitute` short-circuit is kept, so the common case still skips the deducting pass and the real-dump benchmark is unchanged at 3ms full revalidation.

Covered by `LF-dup*`.
