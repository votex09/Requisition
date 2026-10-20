# Recipe conditions snapshotted once per full refresh

**Severity:** MEDIUM — grid contradicts the detail panel
**Area:** `UICraftingPanel` recipe list
**Status:** FIXED 2026-08-24 — READY FOR TESTING / HUMAN REVIEW

## Symptom

Leave a Terminal open across nightfall (or a Blood Moon, or a teammate downing a boss) and the
recipe list keeps its old craftability. A recipe shown craftable flips to "Missing Materials" the
moment you select it.

## Cause

`Content/UI/Elements/UICraftingPanel.cs:283-296` evaluates conditions once per full refresh and
caches the result — the comment calls it "stable between full refreshes":

```csharp
_stationsConditionsMet = new bool[_allRecipes.Count];
...
met = RecipeResolver.CheckRecipeConditionsPublic(recipe, _availableConditions);
```

`CheckRecipeConditions` (`Helpers/RecipeResolver.cs:255-280`) calls `condition.Predicate()` — live
world state: night, Blood Moon, hardmode, downed bosses, graveyard, biome.

A full refresh only happens when the disk / station / condition **sets** change
(`SetDiskIds` / `SetAvailableStations` / `SetConditions`, `:177-199`), and all three early-return
when unchanged. The 2-second `RefreshDiskConnections` poll therefore never forces one.

`UpdateCanCraftFlags` recomputes `ingredientsMet` but ANDs it with the stale
`_stationsConditionsMet[i]` (consumed at `:520`).

## Repro

1. Open the Terminal during the day, Crafting tab
2. Night falls
3. Night-gated recipes keep their daytime flag — now-craftable ones stay hidden with
   "Show uncraftable" off; now-uncraftable ones keep their craftable cell
4. Selecting the latter re-resolves live and the button flips to "Missing Materials"

List and detail panel contradict each other until the terminal is closed and reopened.

## Fix

Either re-evaluate conditions on a low-frequency tick (they are cheap predicates) and set
`_needsRecipeRefresh` on any flip, or drop the array and call `CheckRecipeConditionsPublic` inside
`UpdateCanCraftFlags`, keeping only the station half cached.

## Fix applied

The snapshot loop was extracted into `RefreshStationConditionFlags`, which now also runs on a 60-tick timer in `Update` and calls `UpdateCanCraftFlags` only when a flag actually flips.

Needs in-game testing: leave a Terminal open across nightfall with a night-gated recipe.
