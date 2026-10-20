# ResolveRecursive can return false with the caller's pool already spent

**Severity:** LOW — latent, no current caller is affected
**Area:** `CoreResolver.ResolveRecursive`
**Status:** FIXED 2026-08-24 — READY FOR TESTING / HUMAN REVIEW

## Symptom

None today. A trap for the next caller that reads its pool after a failed resolve.

## Cause

`Helpers/Resolver/CoreResolver.cs` — partial stock is zeroed **before** the cycle guard and
before the candidate loop:

```csharp
int deficit = needed;
if (have > 0) { deficit -= have; available[itemType] = 0; }   // <- spent here

if (!resolving.Add(itemType)) return false;                   // <- returns without restoring
```

`availSnapshot` is captured *after* the zeroing, so the "all candidates failed" return does not
restore it either.

The documented contract is only "Returns false if it cannot be met" — it says nothing about
leaving the pool untouched.

## Repro

```
recipe: 700 <- 701 x5
pool:   { 700: 3, 701: 2 }
ask:    10x item 700

-> returns false, pool left { 700: 0, 701: 2 }
```

Same via the cycle guard:

```
recipes: 800 <-> 801
pool:    { 800: 1, 801: 1 }
ask:     5x item 800

-> returns false, pool left { 800: 0, 801: 1 }
```

## Why it is currently latent

Every in-tree caller either restores (`TryResolveRecipe`'s `availBackup`) or discards the
dictionary — `RecipeResolver.Resolve` / `ResolveForceCraft` build a fresh snapshot and do not
re-read it after failure.

## Fix

Snapshot before the `have > 0` deduction and restore on every `false` path.

## Fix applied

Both false paths below the partial deduction restore `available[itemType] = have` — the cycle-guard return and the all-candidates-failed return.

Covered by `PR-001..009`, including a success case asserting the pool IS still spent when a plan is found.
