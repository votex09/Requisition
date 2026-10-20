# Preview collapsed duplicate ingredient slots

**Severity:** HIGH (as shipped) — no red square anywhere on an uncraftable recipe
**Area:** `CoreResolver.ComputeIngredientPreview`
**Status:** FIXED 2026-08-24 · kept for the record

## Symptom

For a recipe naming the same item in two slots, every ingredient square read satisfied while the
button said "Missing Materials" — the exact complaint the `Satisfiable` flag was added to fix,
reached by a different route.

## Cause

`ComputeIngredientPreview` deduped with a `seen` set and computed
`needed = ingredient.Stack * craftAmount` from the **first** slot only — the same
non-accumulation mistake that `CoreStep.Consumed` carried. `Satisfiable` was then computed against
the too-small need.

The panel made it worse: `RebuildIngredientCache` built the cached text from the **last** matching
raw slot while the draw loop recomputed the colour from **its own** slot's need, so the two
squares of a `WOOD x4 + WOOD x6` recipe disagreed.

## Repro (before the fix)

```
recipe: TABLE <- WOOD x4 + WOOD x6      (needs 10)

wood=10  listCraftable=True   ResolveRecursive=True    preview 4/4 sat=True
wood=9   listCraftable=True   ResolveRecursive=False   preview 4/4 sat=True
wood=6   listCraftable=True   ResolveRecursive=False   preview 4/4 sat=True
wood=5   listCraftable=False  ResolveRecursive=False   preview 4/4 sat=True
```

Panel render at wood=5, replaying both loops:

```
slot need=4 -> text '6/6' GREEN
slot need=6 -> text '6/6' ORANGE
```

## Fix applied

- `ComputeIngredientPreview` builds a `neededByType` map summing every slot times `craftAmount`
  and emits one view per distinct type, in first-appearance order.
- `_ingredientCache` gained a `needed` member; `RebuildIngredientCache` iterates the views rather
  than raw `requiredItem`, so text and colour come from one source.
- The detail draw loop skips duplicate types via `_drawnIngredientTypes` and reads `cached.needed`.

Tests `DS-001..007`.

## The same double-count reached the list flag too — closed

Through a different path: `RecheckRecipeCraftable` skipped the shared-pool confirm when every slot
looked directly satisfied. Fixed alongside this one by `HasRepeatedIngredientType`, covered by
`LF-dup*` — see [06](06-list-flag-skips-shared-pool-confirm.md). This section previously said it was
still open, which stopped being true the day it was written.
