# Favorites toggled elsewhere never re-filter the recipe grid

**Severity:** MEDIUM
**Area:** `UICraftingPanel` recipe list
**Status:** FIXED 2026-08-24 — READY FOR TESTING / HUMAN REVIEW

## Symptom

Unfavoriting a recipe from the Favorited Recipes panel, the Crafting Tree or the Encyclopedia
leaves it sitting in the Terminal's recipe grid — starless, still in the favorited partition at
the top. Favoriting from those places does not make a recipe appear.

## Cause

`FilterRecipes` (`Content/UI/Elements/UICraftingPanel.cs:633`) and
`SyncFilteredRecipesIncremental` (`:693`) partition `_filteredRecipes` by `IsRecipeFavorited`,
but `FilterRecipes()` is only re-run by the panel's own alt-click (`:1085-1087`).

The four other toggle sites do not notify the panel:

- `Content/UI/UIFavoritedRecipesPanel.cs:254`
- `Content/UI/CraftingTree/CraftingTreeState.cs:832`
- `Content/UI/Encyclopedia/EncyclopediaState.cs:329, 957, 1063`

`UICraftingPanel.Update` polls `StorageVersion` but not `StoragePlayerSystem.FavoritesVersion`.

## Repro

Terminal open on the Crafting tab with "Show uncraftable" **off**, Favorited Recipes panel visible
alongside.

1. Alt-click a favorited-but-uncraftable row in the favorites panel to unfavorite it
2. The row vanishes there (its cache polls `FavoritesVersion`)
3. The recipe stays in the Terminal grid — it was only present *because* it was favorited

It disappears on the next unrelated storage mutation.

## Fix

Cache `FavoritesVersion` in `UICraftingPanel` and call `FilterRecipes()` from `Update` when it
changes. The counter added on this branch (`Systems/StoragePlayerSystem.cs:75, 96, 236`) makes
this a three-line change.

## Fix applied

The panel caches `StoragePlayerSystem.Local.FavoritesVersion` and re-runs `FilterRecipes()` in `Update` when it changes.

Needs in-game testing: unfavorite from the favorites panel and from the encyclopedia with the Terminal open and "show uncraftable" off.
