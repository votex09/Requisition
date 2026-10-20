# Server performs a disk upgrade with no material check

**Severity:** HIGH — item duplication, remotely triggerable
**Area:** multiplayer, `UpgradeDiskRequest`
**Status:** FIXED 2026-08-24 — READY FOR TESTING / HUMAN REVIEW

## Symptom

A multiplayer client can obtain a disk tier upgrade for free.

## Cause

`Systems/NetworkHandler.cs:1160-1184` (`HandleUpgradeDiskRequest`) validates the tile entity,
slot index, GUID and `optionIdx` (`:1143-1152`) but never validates that the materials exist.

It loops ingredients, opportunistically crafts shortfalls, calls
`sys.ExtractItem(diskIds, itemType, need)` **ignoring the return** (`:1173`), then unconditionally
builds and installs the upgraded disk (`:1177-1184`).

The affordability gate `_ingCacheCanAfford` exists only on the client
(`Content/UI/Elements/UIDiskPanel.cs:269`).

The handler also trusts the client's `diskIds` list verbatim — ingredients can be sourced from,
or the shortfall crafted out of, any disk GUID the client names, in range or not.

## Repro

Two players share a Terminal network.

1. Player A opens the Disks tab; the ingredient cache is built and reads "affordable"
2. Player B withdraws all 10 Crimtane Bars
3. Player A clicks Upgrade before `StorageVersion` propagates
4. Server: `have=0 < 10`, `Resolve` infeasible, skipped, `ExtractItem(..., 10)` extracts 0
5. Disk upgraded to Tier 2 for free

A modified client reproduces this deterministically against an empty network.

## Fix

- Re-run the affordability check server-side before mutating anything.
- Compute every ingredient count (including craftable shortfall) first; on any shortfall,
  `EndTrackingAndRespond(..., false, ...)` and return without touching storage or the bay slot.
- Extract with a checked return.
- Resolve `diskIds` server-side from the bay's own network, not from the packet.

## Related

[01](01-disk-upgrade-undercharges.md) — the same handler also carries the double-count bug.

## Fix applied

The handler now runs the same `TryConsumeMaterials` transaction and returns without touching storage or the bay slot when it fails. The client-supplied disk list is no longer used for the transaction: the network is re-derived server-side via `StorageNetwork.GetAllConnectedDiskIds(bay.Position)`. The packet list is still read to advance the stream.

Needs multiplayer testing: upgrade while a second player empties the network.
