# A forged disk packet wiped another player's disk, and a wire count sized the server's allocations

**Severity:** CRITICAL — one packet destroyed any player's entire disk contents
**Area:** networking (`SyncDiskInsert`, disk registration, wire-count handling)
**Status:** FIXED 2026-08-26 — READY FOR TESTING / HUMAN REVIEW

These were the three unverified leads left at the bottom of
[23](23-agent-audit-2026-08-25.md). Each was traced from the wire to the mutation before anything
was changed; the verdict table lives in that file, the fixes here.

## 1. A forged disk item overwrote any disk — CRITICAL

`HandleSyncDiskInsert` (`Systems/NetworkHandler.cs:187`) takes `whoAmI` as a parameter and never
referenced it. The disk item came off the wire through `StorageDiskBase.NetReceive`, which assigns
`DiskId`, `IsArchived` and `ArchivedItems` from raw bytes.

The sequence, in prose: a modified client places its own Drive Bay, reads any other player's disk
GUID out of the ordinary `SyncDriveBay` traffic that sends every bay slot's item to every client,
and sends one `SyncDiskInsert` naming its own bay, an empty slot, and a hand-built disk item
carrying the victim's GUID, `IsArchived = false`, and a single junk stack. On the server that lands
in `DriveBayEntity.InsertDisk`'s "already has a GUID" branch, which calls
`RegisterDiskWithItems`, which did `_allDiskData[diskId] = data` unconditionally. The victim's
disk — wherever it physically was in the world — now held one junk item, and everything it had was
gone from the world save.

Two aggravating details: the registration ran *before* the slot-availability check, so it landed
even when the insert then failed; and with `ArchivedItems` left empty the same packet reached
`UpgradeDisk` instead and rewrote a disk's tier, which `UpgradeDisk` applied in either direction.

**Fixed** in four places, because the rule has four encodings:

- `RegisterDiskWithItems` returns `bool` and refuses to replace an existing entry. It is the
  mutation, so every present and future caller is covered — [23g](23-agent-audit-2026-08-25.md)'s
  lesson applied at the point the rule lives rather than at one caller.
- `HandleSyncDiskInsert` gates on `SenderMayClaimDisk`, composed from 23g's existing
  `IsDiskGuidInUse` and `PlayerHoldsDisk` rather than a second authorization vocabulary. A GUID is
  claimable when it is empty, when no physical disk carries it, or when the sender carries it.
- `InsertDisk` resolves the target slot **before** registering anything, so a failed insert leaves
  the world exactly as it was.
- `UpgradeDisk` refuses a downgrade. The stale-tier case it exists to correct is always upward.

**Refusal returns the disk to the sender *and* corrects its view of the bay.** The handler relays
the slot's post-attempt contents to every client including the sender, and the sender emptied its
cursor before sending — so a refusal that stayed silent would delete the disk. That was already
reachable before this change, on the race where two players fill the same slot; it is fixed here
because the new gate would otherwise route into it.

Both halves are needed, and getting only the first half wrong is instructive. The client writes the
disk into its *own* copy of the bay before it sends (`DriveBayUIState.cs:185`), so returning the item
while relaying nothing leaves that client showing the disk in the bay *and* holding a second copy in
inventory — two items with the same `DiskId`, the one state `IsDiskGuidInUse` and the whole recovery
flow assume cannot exist. Every refusal therefore also sends `SendSyncDriveBay` to the sender.

The return is restricted to Storage Disks. `InsertDisk` refuses *non-disk* items too
(`DriveBayEntity.cs:190`), and handing those back would turn a packet that used to be a no-op into a
faucet for arbitrary items.

**The re-mint, and why it is not a refusal.** `IsDiskGuidInUse` only sees disks in bays and in
*active* players' inventories, so it cannot speak for a disk sitting in a chest or in an offline
player's inventory. When archived items claim a GUID the world already knows, the server mints a
fresh GUID and registers them under that instead. The victim's data is untouched, the attacker gets
a disk holding items they forged themselves — which a modified client could already give itself —
and no refusal has to be signalled to a sender whose copy is already gone.

This removes the *destructive* half of the forged-item class. It does not stop a client registering
forged items under a GUID it owns; that is pre-existing, is client-side item creation, and is not an
escalation under [23](23-agent-audit-2026-08-25.md)'s calibration.

**What it does NOT cover, precisely.** The re-mint lives only on the path where the disk carries
archived items. A forged disk with an *empty* `ArchivedItems` claiming a GUID whose physical disk is
in a chest, or in an offline player's inventory, passes the gate — `IsDiskGuidInUse` cannot see
either — and lands in the attacker's bay, binding that disk into their network. Closing it means
either widening `IsDiskGuidInUse` past what it can see, or refusing a GUID the world already knows
unless the sender demonstrably holds it. The second breaks the ordinary bay-to-bay move: this mod
never syncs an inventory slot it empties, so the server's view of the sender's inventory is stale in
both directions and `PlayerHoldsDisk` cannot be relied on as the *only* arm. That is a design
decision needing a live session, so it is recorded at the end of
[23](23-agent-audit-2026-08-25.md) rather than guessed at here.

> **Narrowed 2026-08-26, still open.**
> [27](27-packets-named-disks-instead-of-a-terminal.md) added a proximity check to
> `HandleSyncDiskInsert`: the insert now has to come from a sender standing within 15 tiles of the
> bay, which the Drive Bay UI enforces on itself anyway. So the attacker must be physically at a
> Drive Bay rather than anywhere in the world. **Both objections above still hold** — the offline
> inventory is still invisible, and the stale-equipment problem is unchanged — and the attacker can
> place their own bay, so this narrows the reach without closing the hole. It still needs the live
> session.
>
> Two things in this file are superseded by 27: `RefuseInsert` now also sends the *reason*
> (`DiskClaimRefused` or `DriveBaySlotUnavailable`) alongside the disk return and the bay correction,
> so the two-clients-race case tells the loser why; and the wire-count bound this file added to
> `ReadGuidList` covers six fewer handlers, because those packets no longer carry a GUID list at all
> — `ReadGuidList` is gone. `WireCount` itself still guards the chunked-sync length and
> `DiskData.ReadNet`, and `WB-*` still pins it.

## 2. A wire-supplied count sized the server's allocations — HIGH

`ReadGuidList` did `new List<Guid>(count)` with `count` straight off the wire, from eight
server-side handlers. `List<T>(capacity)` commits the whole backing array **before the first element
is read**, so no care in the read loop could bound it: a ~20-byte packet asked the server for
gigabytes. Repeat it and the server dies; the exception is swallowed by tModLoader's bare
`catch { }` in `ModNet.HandleModPacket`, so it dies quietly.

The read loop is not a backstop either. Terraria reads every packet into one reused 131,070-byte
buffer (`MessageBuffer.readBufferMax`), so reading past the packet returns the *previous* packet's
bytes rather than throwing.

**Fixed** with `Common/WireCount.cs`, two predicates with derived bounds and no invented numbers:

- `FitsInOnePacket` — the count must describe elements that fit in one read buffer. Used for the
  disk-GUID list and for the chunked-sync `dataLength`, which had the same `ReadBytes(n)` shape.
- `FitsDiskCapacity` — a disk's stack count against `DiskTier.GetCapacity()`, roughly 3× tighter
  and needing no constant at all.

Both **refuse** rather than clamp. Clamping and carrying on reads the rest of the packet from an
offset that no longer means anything, and in two of these handlers the next statement is
`TagIO.Read`. Aborting is safe: `MessageBuffer.GetData` resets the reader position per message. It
also strands nothing — in all eight handlers the read precedes `BeginModificationTracking`, so a
mid-handler return cannot leave the tracker set, which would have been
[23f](23-agent-audit-2026-08-25.md) again.

`DiskData.ReadNet` returns `null` on a bad count rather than emptying its list, because it reads
straight from the packet inside a loop over several disks; `StorageDiskBase.NetReceive` may empty
its list safely, because tModLoader hands it a stream over exactly the bytes the sender declared.
Its tier byte is also validated — it indexes a six-element capacity table, so any byte above 5 threw
on the first read of `MaxStacks`.

The bound at `DiskData.ReadNet` is the **largest** tier's capacity, not the packet's own tier. A
disk whose tier was wrongly lowered still legitimately reports the stacks it holds, and a
tier-derived bound there would have blanked an honest disk on every client — the tier-flip defect
above turned into a worse one.

`Common/DiskDelta.cs` was checked and is **exempt**: its lists take no capacity argument, and its
handler is client-only before the read.

## 3. The sweep — the same rules elsewhere

[23g](23-agent-audit-2026-08-25.md) bounds-checked a wire slot index into a fixed array in the two
disk handlers. The two *station* handlers are the same shape and were missed:
`HandleSyncStationInsert` and `HandleSyncStationRemove` wrote `cce.StationSlots[slot]` with an
unchecked wire `int` into an `Item[40]`. Both now bounds-check and return before the relay, matching
`HandleSyncDiskInsert` rather than `HandleSyncDiskRemove`, which relays an out-of-range slot.

Three handlers had **no `netMode` guard at all**, so a client packet ran them on the server:
`HandleSyncDiskData` and `HandleSyncDiskDataChunked` (both reach `ApplyDiskDataFromNetwork`, which
replaces a whole disk) and `HandleSyncDriveBay` (rewrites all 40 slots of any bay).
`ApplyDiskDataFromNetwork` is now guarded **inside the method**, so a future handler that forgets
its own check cannot let a client rewrite server storage. `HandleSyncDiskData` keeps a handler-level
guard as well — the mutation guard alone would still let `SetDiskSeqNum` grow a dictionary on
attacker-chosen GUIDs and `RefreshAllDriveBays` walk every tile entity. Guarding the mutation means
*also*, never *only*. `HandleWithdrawItemResult` got the same guard for consistency.

`HandleSyncDiskDataChunked` also accepted a chunk index past the buffer it was writing into.

## Verified by

`WB-*` and `DC-*` in `Tests/Program.cs` — 19 assertions over `Common/WireCount.cs` and
`Common/DiskClaim.cs`, the two rules extracted as Terraria-free predicates so they can be pinned at
all. Suite 483 → 503, zero failures.

The handler wiring itself has no unit-test surface, for the reasons
[21](21-untested-fixes.md) sets out. It was instead **compiled** — the whole mod type-checks clean
against `tModLoader.dll` (0 errors, 0 warnings) via a throwaway project, which is as far as
verification goes without a running server.

## Needs a two-client session

- A forged `SyncDiskInsert` naming an **online** player's disk GUID: refused, the disk comes back to
  the sender, the victim's contents unchanged.
- The same naming an **offline** player's disk GUID: victim's contents unchanged, the attacker's
  items land under a fresh GUID.
- Unarchive a disk and insert it: items restore exactly once, and the bay lights are correct
  immediately rather than at the next Terminal open.
- Two clients race for the same empty bay slot: the loser gets the disk back in inventory **and**
  their bay slot is corrected to the winner's disk. Both halves matter — the client puts the disk
  into its own copy of the bay before sending, so a refusal that only returned the item would leave
  that client showing two copies of the same disk.
- Move a disk between bays; insert a fresh uninitialised disk; insert and remove a crafting station.
- Ordinary deposit, withdraw, craft, defragment and quick-stack are unchanged — the new bounds are
  no-ops on honest counts.
- Drive Bay status lights on a bay far from any Terminal still update. This one matters: it is why
  the six unscoped-GUID handlers in [23](23-agent-audit-2026-08-25.md) were left alone.

## Related

[23](23-agent-audit-2026-08-25.md) (the leads, and the confirmed holes still open),
[24](24-globaldata-treated-as-item-identity.md) (why `SnapshotItems` was already correct),
[20](20-depth-origin-off-by-one.md) (why the open holes were not speculatively fixed),
[21](21-untested-fixes.md) (what has no unit-test surface).
