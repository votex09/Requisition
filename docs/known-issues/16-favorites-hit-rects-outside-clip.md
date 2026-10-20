# Favorites hit rects built for rows the scissor clips away

**Severity:** MEDIUM — invisible destructive click target
**Area:** favorited recipes panel
**Status:** FIXED 2026-08-24 — READY FOR TESTING / HUMAN REVIEW

## Symptom

With more than ~9 favorited recipes, alt-clicking well below the panel — including in your own
inventory, where alt-click is the vanilla favorite gesture — silently unfavorites a recipe you
cannot see.

## Cause

`Content/UI/UIFavoritedRecipesPanel.cs:423` appends an output rect for **every** row:

```csharp
_recipeOutputRects.Add((row.RecipeIndex, outRect));
```

at `PanelTop + HeaderHeight - _scrollOffset + i * RowHeight`, while the body is clipped to
`MaxBodyH = 380f` (`:25`) and `RowHeight = 40f` (`Content/UI/FavoritesRowCache.cs:17`).

Rows past ~9 get rects physically below the panel. They are invisible but still hit-tested by the
alt-click handler at `:248-258`, which is guarded only on `!IsCollapsed`.

## Repro

20 favorited recipes, panel scrolled to the top. Rows 10-20 have rects spanning ~400px below the
panel's bottom edge, in a 36px-wide column at `PanelLeft + 4`. Alt-click anywhere in that band.

Pre-existing — the uncommitted work on this branch moved the code but left the behaviour intact.

## Fix

Skip the `_recipeOutputRects.Add` for rows outside
`[PanelTop + HeaderHeight, PanelTop + HeaderHeight + min(BodyHeight, MaxBodyH)]`, or intersect
each rect with the clip rect before storing it.

## Fix applied

Rows outside the clipped body no longer register a hit rect, and the same visibility test now gates hover. Computed from `PanelTop + HeaderHeight` and `min(BodyHeight, MaxBodyH)`.

Needs in-game testing: 20+ favorites, alt-click in the inventory below the panel.
