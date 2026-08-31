# Partial own-type stock blocks recipe-group substitutes

**Severity:** MEDIUM — craftable recipe refuses to craft
**Area:** `CoreResolver.ResolveIngredientType`
**Status:** FIXED 2026-08-24 — READY FOR TESTING / HUMAN REVIEW

## Symptom

A recipe-group slot refuses to craft when you hold *some* of the named item plus plenty of a valid
substitute. Holding **none** of the named item works fine — so acquiring one of the wrong bar can
make a recipe stop being craftable.

## Cause

`Helpers/Resolver/CoreResolver.cs:66-82`:

```csharp
private int ResolveIngredientType(CoreRecipe recipe, int ingredientType, Dictionary<int,int> available)
{
    if (available.TryGetValue(ingredientType, out int have) && have > 0)
        return ingredientType;          // <- any amount at all wins, even if it is not enough
    ...
}
```

The slot is committed to one concrete item type. `ResolveRecursive` then tries to cover the whole
need from that type, sub-crafting the deficit; it never falls back to the substitute that holds
the rest, and never splits the slot across two group members.

The preview's direct draw (`ComputeIngredientPreview`) **does** mix freely across group members,
so the two halves disagree: the panel can show a green `10/10` on a slot the resolver cannot fill.

## Repro

```
group AnyGoldBar = { GoldBar, PlatinumBar }
recipe: Disk <- AnyGoldBar x10 + Glass x3 + Lens x1

stock: 0 gold + 99 platinum + sand + lens   -> craftable   (substitute used wholesale)
stock: 3 gold + 99 platinum + sand + lens   -> NOT craftable
stock: 99 gold + sand + lens                -> craftable
```

Second symptom, single-ingredient recipes — the same defect inverts:

```
recipe: Crown <- AnyGoldBar x10       (one ingredient)
stock:  3 gold + 99 platinum

RecheckRecipeCraftable -> true    (needsSharedConfirm needs >= 2 ingredients, so it never runs)
TryResolveRecipe       -> false
```

So the recipe is *hidden* when it has 2+ ingredients and *falsely offered* when it has one.
Any fix must cover both.

## Fix

Teach `TryResolveRecipe` to split a slot across group members, matching vanilla consumption
(vanilla counts group items in aggregate). `CoreStep.Consumed` is already a `type -> amount` map,
so a mixed draw is representable and `ExecutePlan` needs no change.

Cheaper stopgap: prefer the group member that alone covers the need, falling back to the named
type. That fixes the common case without supporting genuine mixing.

If mixing is implemented, also revisit `CanSubCraftRemainder`
(`CoreResolver.cs:599`), which calls `CanProduce` on the named type only and would then diverge.

## Related

[11](11-prefilter-ignores-accepted-groups.md) — the prefilter's group blindness, same area.

## Fix applied

A slot is no longer committed to one concrete type. `ResolveIngredientSlot` draws stock across every accepted group member, then sub-crafts only the remainder — through the named type where possible, otherwise a substitute — and records each member it actually spent into `CoreStep.Consumed`, so `ExecutePlan` extracts the right items. `CanFillSlot` mirrors it exactly on the feasibility side, and `CanSubCraftRemainder` tries every member for the remainder.

`ResolveIngredientType` is now unused by the plan path and remains only for the shared-confirm ordering.

Covered by `GM-*`, including a mixed 3 gold + 7 platinum case asserting both members appear in `Consumed`.
