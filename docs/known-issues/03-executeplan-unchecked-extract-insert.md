# ExecutePlan ignores extraction shortfall and intermediate insert leftover

**Severity:** HIGH — item duplication and item loss in one craft
**Area:** crafting execution
**Status:** FIXED 2026-08-24 — READY FOR TESTING / HUMAN REVIEW

## Symptom

A recursive craft can produce its output without fully paying for it, and can destroy the
intermediate products the player did pay for.

## Cause

Two unchecked returns on the same path in `Helpers/RecipeResolver.cs`:

```csharp
663: private static void ExtractFromBoth(List<Guid> diskList, int itemType, int amount)
665:     StorageWorldSystem.Instance.ExtractItem(diskList, itemType, amount);   // return discarded
...
695:     ExtractFromBoth(diskList, kvp.Key, kvp.Value);
...
731:     StorageWorldSystem.Instance.InsertItem(diskList, produced);            // leftover discarded
```

`ExtractItem` is explicitly a best-effort PARTIAL extractor
(`Systems/StorageWorldSystem.cs:376-395`) — it breaks out of the disk loop only on
`totalExtracted >= count` and otherwise returns whatever it found.
`ExecutePlan` never compares the extracted amount against `step.Consumed[key]` and produces
`ProducedType x ProducedCount` unconditionally (`:697-699`).

Symmetrically, an intermediate that cannot be stored is dropped. The full-storage pre-check in
both callers (`Content/UI/Elements/UICraftingPanel.cs:1320`, `Systems/NetworkHandler.cs:672`)
measures room for the FINAL item only, never for intermediates.

## Repro

Recursive Campfire (10 Wood + 5 Torch). Storage has Wood and Gel but no Torch stack, every disk
slot occupied, player has a free inventory slot so the final-item pre-check passes.

1. Step 1: `craftsNeeded = ceil(5/3) = 2`, producing **6** Torches.
   `InsertItem` finds no Torch stack to merge into and the disk is full, leftover 6,
   **discarded at `:731` — six torches destroyed.**
2. Step 2: `ExtractFromBoth(Wood, 10)` succeeds. `ExtractFromBoth(Torch, 5)` extracts **0**,
   discarded. The Campfire is produced anyway.

**The surplus Torch the player paid for is gone, and the 5-Torch cost was never paid.**

## Fix

- Make `ExtractFromBoth` return the extracted count; abort the plan (restoring what was already
  extracted) when short of `kvp.Value`.
- Check `InsertItem`'s leftover on the intermediate branch and abort rather than dropping.
- Better: hold intermediates in a local dictionary for the duration of `ExecutePlan` instead of
  round-tripping them through storage — which is what the final step already does, for exactly
  this reason (see the comment at `:711-716`).

## Notes

This is the missing safety net that let the `CoreStep.Consumed` overwrite bug (fixed 2026-08-24)
ship a finished item for less than its recipe cost. Fixing accounting without adding this guard
leaves the same class of bug one arithmetic slip away.

## Related

[01](01-disk-upgrade-undercharges.md) — the disk-upgrade paths repeat the unchecked-extract shape.

## Fix applied

Extraction goes through `TryExtractExact`, which reports whether it got the full amount; every extraction is recorded so a shortfall aborts the plan and refunds. An intermediate whose insert leaves a remainder now takes back the part that landed FIRST (so the refund cannot be blocked by the product the materials were spent on), then refunds and aborts. `ExecutePlan` returns air on abort, which every caller already treats as failure.

Needs in-game testing: craft recursively into a completely full storage network.
