# Partial deposit reports failure, skipping the delta broadcast

**Severity:** MEDIUM — stale clients, no item loss
**Area:** multiplayer deposit
**Status:** FIXED 2026-08-24 — READY FOR TESTING / HUMAN REVIEW

## Symptom

After a deposit that only partly fits, other players' terminals keep showing pre-deposit counts
until an unrelated change forces a full resync.

## Cause

`Systems/NetworkHandler.cs:451-460`. `item.stack` is reassigned to `leftover`, then the success
flag compares the two:

```csharp
451: if (leftover > 0) { item.stack = leftover;  ... }
460: EndTrackingAndRespond(mod, whoAmI, leftover < item.stack, diskIds);
```

Inside the branch that is `leftover < leftover`, always **false**.
(On a *full* deposit the branch is skipped, `item.stack` keeps the original amount, and the flag is
correctly true — so only partial deposits are affected.)

`EndTrackingAndRespond:1297-1306` skips `BroadcastDiskDeltas` when `success == false`, but
`EndModificationTrackingWithDeltas` has already incremented the per-disk sequence number
(`Systems/StorageWorldSystem.cs:101`).

Same shape at `Systems/NetworkHandler.cs:518` + `:521`.

## Repro

1. Player A shift-clicks 500 Wood into a network with room for 300
2. Server stores 300, returns 200, reports failure
3. Player B never learns about the 300 Wood

B's mirror stays stale until the next delta, whose sequence gap triggers a full resync
(`HandleDeltaDiskData:1354-1360`). Until then B's crafting panel shows wrong counts and offers
crafts the server will refuse.

Self-healing and server-authoritative, so no items are lost — but the flag is unambiguously
inverted.

## Fix

```csharp
int deposited = item.stack - leftover;   // before reassigning item.stack
...
EndTrackingAndRespond(mod, whoAmI, deposited > 0, diskIds);
```

Apply at both sites.

## Fix applied

Both sites capture `int deposited = item.stack - leftover;` before `item.stack` is overwritten, and report `deposited > 0`.

Needs multiplayer testing: deposit more than fits and confirm a second client sees the partial amount without waiting for a resync.
