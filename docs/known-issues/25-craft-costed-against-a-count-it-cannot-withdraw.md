# A step asked storage for twenty and took one

**Severity:** HIGH — a green CRAFT button that did nothing at all
**Area:** crafting transaction, withdrawal, crafting panel
**Status:** FIXED 2026-08-25 — READY FOR TESTING / HUMAN REVIEW

## Symptom

Band of Door (item 18120 = 1× Shackle + 20× Door Pants, at a Work Bench) showed as craftable and
the CRAFT button was green. Clicking it did nothing: no sound, no message, no item, no change to
storage. Indistinguishable from clicking dead panel background.

## Cause

`RefundLedger.TryTakeExact` paid for a step with **one** `Extract` call.

`Extract` is best-effort. `StackSelection.PlanWithdrawal` drains plain stacks, but a stack that
stands for itself is only ever taken **alone** — its mod state describes those units and no others,
so folding several into one returned item is [05](05-extractitem-stamps-tag-on-whole-withdrawal.md).
One call therefore answers a request for twenty with one.

Door Pants are armour (`maxStack = 1`), so 18 of them are 18 stacks. In build 0.5.15 — the one the
report came from, which predates [24](24-globaldata-treated-as-item-identity.md) — every stack
reported `IsUnique`. The step asked for 20 and got 1.

Everything downstream then behaved correctly and invisibly: `TryTakeExact` refused the short draw,
`PlanExecutor` aborted and refunded, `ExecutePlan` returned air, and `ExecuteCraft` returned without
telling anyone.

## Fix

`TryTakeExact` loops until the amount is met or storage stops yielding. The one-stack-at-a-time rule
is untouched — each draw is kept as **its own handle** in `_taken`, so no stack's state is folded
into another's. Twenty stacks pay for twenty units.

`RefundLedger.Refund` then had to change direction. It withheld conjured units from the **front** of
`_taken`, but a step's product is inserted after the stock the player already had, so extraction
hands it back **last**: the front of the list is always the player's own stacks. A failed craft put
the right count back and the wrong items — the player's stacks dropped, stateless copies in their
place. With one handle per type that was invisible; with one handle per stack it is the difference
between keeping an enchanted item and losing it. `Refund` now withholds from the end.

`PlanExecutor.Abort`, `TryStoreIntermediate` and `MaterialConsumer.TryStockUp` each carried their own
copy of the same take-back loop; they are now one `StorageRecovery.TakeBack`.

`IsDiskUpgradeStep` now refuses a step consuming more than one disk. Only one GUID is read and one
result Item built, so a batched step would stamp that GUID on every disk it made. Nothing registers
such a recipe today; before the loop, `TryTakeExact` refused the draw and hid it.

`StorageWorldSystem.ExtractItem` let the first disk fall back to a unique stack and **returned**,
so one such stack on disk 1 masked pooled stock on disk 2. It now drains pooled stock across the
whole network before the fallback applies.

Draining across disks then needed the guard `DiskData.AllDrawsShareModState` already applies within
one: `result ??= extracted; result.stack = totalExtracted` folded every disk's units into the first
disk's Item, wearing the first disk's tag. Two weapons of the same type on two disks came back as
one 2-stack carrying one weapon's state, and re-inserting split that state across both — the other
weapon's gone. `DiskData.ExtractItem` now reports the tag its result carries, and `ExtractItem`
stops folding when a disk's state would be discarded, putting that draw straight back into the disk
whose slots it just freed. Callers already handle a short return; `TryTakeExact` asks again.

`ExecuteCraft` had four bail-outs that returned in silence. Each now says why.

## Fix — 25-B, the reason now crosses the wire

`EndTrackingAndRespond` sent a success flag; `HandleOperationResponse` wrote it to the debug file and
discarded it. A client's denied operation was "click, nothing happens" no matter what the server
knew. The response now carries the reason.

**The wire.** `StorageOperationFailure` (`Common/StorageOperationFailure.cs`) is a `byte` enum, and
`OperationResponse` appends it after the existing `success` bool — **only when `success` is false**,
so a successful response is still the two bytes it always was and the state "denied, for no reason"
is unrepresentable rather than merely discouraged. A raw string was rejected: it is an unbounded
untrusted payload, and `Language.GetTextValue` of an unknown key returns the key verbatim, so a key
sent as text is a chat-injection vector. A new `PacketType` was rejected too — it costs four edit
points instead of two, carries the identical append-only irreversibility, and adds a second
uncorrelated packet that would have to be ordered against the correction packets that already follow
a denial. `Common/CraftingCondition.cs` is the precedent: a byte enum already crosses this wire.

**The numbers are the format.** Members may only ever be appended; renumbering one silently
mistranslates every refusal a peer reports. `DN-14` pins each value and the member count, because
every other assertion in the set passes under any numbering — without it the one irreversible
decision in this change would ship unguarded.

**Version skew is not a concern, and no stream guard is possible.** Decompiling tModLoader
(`1.4.4.9+2026.06.3.6`) settled both: `ModNet.ModHeader.Matches` requires name, version **and** the
20-byte SHA-1 of the `.tmod` to match before a client may join, so a peer that does not write the
byte cannot be in the session. And the `BinaryReader` handed to `Mod.HandlePacket` is one long-lived
stream over a shared 131 070-byte connection buffer, not a per-packet stream — a
`Position < Length` guard is *always true*, so it would not catch a short payload, it would return
the next packet's first byte. The guard that looked prudent was deleted on that evidence.

**One vocabulary, one decision.** The four singleplayer messages moved verbatim into
`UI.OperationFailed` in both catalogs, behind a shared `Prefix` key so `Requisition: ` has one
definition instead of sixteen. More importantly the *decision* is shared: `GetCraftFailure` is one
pure function that both `ExecuteCraft` and `HandleCraftRequest` call. Two hand-written copies of one
rule is exactly the shape of [23a, 23b and 23c](23-agent-audit-2026-08-25.md), and neither of those
two files can be compiled outside the game — so the branch table lives where the runner executes it.

**A denial burst is one line — but only off the wire.** "Deposit all" sends one packet per
inventory slot (`TerminalUIState.cs:838`), so a full network denies up to forty times per click. A
repeat of the same cause within 60 ticks is suppressed; a different cause never is. The throttle is
reached through `ReportServerDenial` and **only** from `HandleOperationResponse`. The panel's own
refusals go through `ReportFailure` unthrottled, because a locally decided refusal is already one
per click: a double-click is 12-30 ticks apart, well inside the window, so throttling it would have
swallowed the second click and restored the exact silence this issue is about.

**A denial that changed nothing does not drag a resync behind it.** `SendOperationResponse`'s
failure arm sends a full `SendDiskPacket` for every disk it is given, to repair client state the
server rejected. Quick-stacking into a full network used to report *success* (it counted slots
tried, not units moved), so it sent none; naively reporting the new `NothingDeposited` there would
have turned a spammable button into a full-network resync storm for an operation that modified
nothing. That path now passes no disk ids — the reason travels, the corrections do not. The
nothing-matched path keeps the corrections it always sent.

**Two defects found while doing it.** `HandleQuickStackToStorage` added to `results` even when the
whole stack bounced, so quick-stacking into a full network reported **success with zero deltas** and
said nothing — this issue's own symptom, alive in the path meant to report it. It now decides from
`DepositOutcome`, and distinguishes "nothing matched" from "nothing fitted". Separately, six refusals
in `HandleDepositItemAtPosition` and `HandleQuickStackToStorage` returned before tracking began and
answered nothing at all; `RefuseOperation` now answers them — `SendOperationResponse` touches no
tracking, so the helper they appeared to need never existed.

### Still silent

Roughly nineteen refusal points across four handlers still answer nothing:
`HandleUpgradeDiskRequest` (5), `HandleRestoreDiskRequest` (3-4), `HandleDefragRequest` (2, plus a
silent no-op when `modified.Count == 0`) and `HandleArchiveDiskRequest` (4 — its `ArchiveDiskResult`
packet is sent only on success, so it is not the feedback channel it looks like). All but Upgrade
have `whoAmI` in hand and are one `RefuseOperation` line each; **`HandleUpgradeDiskRequest(Mod,
BinaryReader)` has no `whoAmI` parameter at all**, so it needs a signature change first. They are
deferred because they are disk-management refusals needing their own vocabulary, not storage-operation
ones, and because a sibling agent is working in this file.

### Needs a real two-client session

Nothing below has a unit-test surface — [21](21-untested-fixes.md) explains why packet ordering does
not get one here, and `Main.NewText` cannot be linked into the runner.

1. A denied craft prints **one** line naming the right cause. Drive all four: no materials;
   craft-to-inventory with a full inventory; full storage **and** full inventory; a second client
   emptying the network between the plan and `ExecutePlan`.
2. A **successful** craft prints nothing, and its response is still two bytes.
3. "Deposit all" into a full network prints **one** line, not forty.
4. Denied withdraw, deposit, quick-stack, out-of-range and no-disks each print their own line.
5. Quick-stack into a full network now says so instead of silently confirming.
6. The correction packets after a denial still arrive and still resync the client — the appended
   byte did not disturb what follows it.
7. The server prints nothing locally; the denial sound is distinguishable from the send tick.
8. Two *different* denials in one tick: both are heard (the throttle keys on the cause).
9. **Singleplayer, clicked twice quickly** — a full inventory and two CRAFT clicks 300 ms apart must
   print **two** lines. This is the regression the throttle introduced and `ReportFailure` undoes;
   it has no unit-test surface because the split is in which entry point each caller uses.
10. Quick-stack into a full network prints its line and does **not** trigger a full-disk resync —
    watch the packet volume, not just the chat.

**Accepted risk:** the response carries no correlation id, so two operations denied at nearly the
same moment can attribute a reason to the wrong click. Today nothing is displayed at all, so this is
a new risk rather than a pre-existing one; it is accepted because the causes are distinct enough to
read and a correlation id is a much larger protocol change.

### Verified by — the denial vocabulary

`DN-*` covers 25-B's testable half: the wire codes and their pinned byte values, the craft decision's
full sixteen-row truth table, the quick-stack decision, the burst throttle (including the
`GameUpdateCount` wrap), both catalogs on disk, and a source scan asserting every named cause is a
real enum member and no site settles for `Unspecified`. That last one is deliberately the compiler
this change does not otherwise get. Reverting the enum's numbering turns `DN-14i`/`DN-14j` red;
changing `GetCraftFailure`'s `||` to `&&` turns `DN-09e`, `DN-09f` and `DN-09g` red.

## Fix applied 2026-08-26 — one sweep, and recovery by handle

Three of the four bullets below were closed. What changed:

**The sweep now runs once.** `StorageWorldSystem.ExtractItem` walked every disk once per unit a
caller needed, because one item handle carries one stack's mod state and a caller holding only one
had to ask again — and `TryTakeExact` and `StorageRecovery.TakeBack` each carried that loop, at four
call sites between them. The rule moved to `Common/NetworkWithdrawal.cs`, free of Terraria and
parameterised by **how many items the caller can hold**: one for a withdrawal onto the cursor, as
many as it takes for a crafting step's ledger. Both callers fall out of the one rule, so there is no
second encoding to drift.

**A state boundary opens another handle instead of ending the sweep**, so a material spread over two
disks whose stacks carry different state now pays from both. At `handleLimit: 1` the draw is still
put back and the sweep still stops, which is what every UI and network caller has always seen.

**`TakeBack` recovers by handle.** Each handle the run inserted is asked for its own units first,
bounded by what the run actually stored — a stack that grew past that also holds units the player
owned, and taking it whole would destroy them. Plain units have no state to match on and still
recover by type, which is correct: they are interchangeable.

The match is `DiskData.ExtractStoredStack`, on item type, prefix, mod item data and mod-written
state **together**. `ExtractItemWithModData` was the obvious thing to reach for and is the wrong
tool: it carries no item type, so `StorageDiskBase`'s `{"archived": true}` — written identically by
every disk tier — matches across types, and it says nothing about `globalData`, so the player's
enchanted copy answers for the plain one the run made. Routing recovery through it would have
introduced a way to destroy an item of a different type than the one being recovered.

`ICraftingStorage.Extract` was **replaced** by `ExtractStacks` rather than joined by it, so the
re-entrant loop could not survive inside `TakeBack`.

## Fix applied 2026-08-26 — the refund withholds by handle too

`RefundLedger.Refund` identified conjured units by **position**, withholding from the end of
`_taken`. That is a guess about which handle the run made, and it was wrong in a reachable case: the
player owns unique `CHARM[own-a]` on disk 1 and `CHARM[own-b]` on disk 2; the run conjures one,
which lands on disk 1 after `own-a` (`StorageWorldSystem.InsertItem` walks disks in order and fills
the first with room); a later 3-unit draw yields `_taken = [own-a, conjured, own-b]`; withholding
one from the end dropped **`own-b`** and re-inserted the run's copy. The count balanced, `own-b`'s
state was gone. Same defect as the `TakeBack` one above, at the site with the larger blast radius —
`Refund` runs on every abort.

`MarkConjured` now takes the handle the step produced, and `Refund` withholds from the drawn handles
whose state matches it (`ICraftingStorage.SameStoredState`, bound to
`StorageWorldSystem.ItemsShareStoredState` — type, prefix, `ModItem.SaveData` and `globalData`,
the same terms `ExtractStoredStack` matches on) before falling back to position. The fallback is
still right for what reaches it: units with no state to compare are interchangeable, so any of them
will do, and a product with nothing to distinguish it merged into stock that was already there.

`NW-09`'s justification was rewritten rather than its assertion removed. It previously rested on
end-withholding; the rule it pins — a handle is a run of *consecutive* draws sharing state — stands
on its own terms, because that is what lets `handleLimit` mean "how many separate items the caller
can hold".

**Verified by `RF-*`.** Disabling only `WithholdMatchingHandles` turns `RF-02` red with the reported
outcome — `[got made,own-a]`, the player's stack destroyed and the run's copy in its place — while
`RF-04` (the trailing layout the old rule handled correctly) stays green, so the test is not merely
biased toward the new rule. `RF-05` pins that plain interchangeable units still refund by count.
`FakeStorage` grew a per-disk slot model (`WithDiskSlots`, `WithUniqueStackOn`) because without one
every insert lands at the end — the single layout in which withholding from the end is correct, and
the reason this defect survived `ID-04`.

## Not fixed

- **Recovery by handle only reaches a product that landed as its own stack.** `ExtractStoredStack`
  matches on item type, prefix, mod item data and mod-written state together. When the conjured
  product *merged* into a stack the player already had, `DiskData.InsertItem` leaves the
  destination's `FullItemTag` in place (or has the mod rewrite it through `FoldInModState`), so
  nothing the handle can be re-serialised into will match it and the recovery falls back to the
  by-type draw. That fallback is correct there, though **not** because merging implies agreement:
  `DiskData.InsertItem`'s merge gate is `Matches` + `StacksWith`, and `ModStateMatches` only decides
  whether `FoldInModState` runs first — so a merge happens *even when state differs*, and the mod is
  told to fold rather than asked whether to. The fallback is right for the downstream reason: once
  `FoldInModState` has run, the resulting `globalData` is neither the player's nor the run's, so
  there are no distinguishable "run's units" left to recover precisely. It does mean the precise
  path fires for stacks that stand for themselves and not for stateful stock that still pools.
  It is precision by **state**, not by object: two stacks carrying byte-identical state are
  indistinguishable in every observable respect, so taking either is equivalent. The size guard is
  what stops more units coming back than the run put in.
- **A withdrawal onto the cursor now yields the first run, not the whole cell.** The cost of the
  fix below, stated where a reader will look for it. `handleLimit: 1` — every UI and network
  withdrawal — stops at the first state boundary, and that boundary can be anywhere: the yield is
  the size of the **first** run, not the largest. A disk holding one unit of state A in front of
  999 of state B answers a 999-unit click with **one**, and the player clicks again for the rest.
  Nothing is lost — the units and their state are all still there — but the click count is real.
  The one caller that could legitimately raise its budget is `TerminalUIState.cs:701-707`'s
  shift-click branch, which hands the result to `player.GetItem` (the inventory, which holds many
  items) rather than to the cursor; it is left at 1 here because that file is outside this change
  and has no assertion surface. `SB-12` pins the worst case.
- **`DiskWithdrawal.PutBack` re-dates the stack it restores.** It inserts through
  `disk.InsertItem(item, ++_insertionCounter)`, and `DiskData.InsertItem` writes that order onto the
  merge target, so a stack the player never received jumps to the front of the "recently added"
  sort. Pre-existing across disks; the fix below makes it fire within one disk on every mixed-state
  cursor withdrawal, so it is now routine rather than rare. Cosmetic — a sort order, not an item —
  and fixing it needs `DrawnUnits` to carry the order it took, which this change did not add.
  **No assertion pins it, in either direction.** `InsertionOrder` lives on `StoredItemStack`, which
  `DiskWithdrawal` reaches through `DiskData.InsertItem`; neither file can be linked into the
  runner, and neither fake models an insertion order at all. Pinning the current behaviour first
  would mean teaching a fake to carry one, which is the same work as fixing it.

## Fix applied 2026-08-26 — the plan ends at the boundary instead of dropping the state

The second `## Not fixed` bullet, closed. `DiskData.ExtractItem` decided *after* planning whether
every stack it had drawn from carried the same mod state, and when they disagreed its only lever was
to drop the state: a bulk withdrawal spanning two plain stacks with different `globalData` came back
with **none**. `AllDrawsShareModState` is deleted, not bypassed.

**The rule moved into the planner.** `StackSlot` carries the run of stacks it merges into, and
`StackSelection.PlanWithdrawal` ends its plain pass at the first stack outside that run. A plan can
no longer span a boundary, so the stack that opened the run speaks for every unit drawn — and three
copies of "did these draws share state?" (`DiskData`, `FakeDiskNetwork`, `FakeStorage`) collapse
into one rule with no runtime check left to disagree with it. A stack that stands for itself is
skipped rather than drawn from, so it is transparent to the stacks either side of it, not a
boundary.

**`DrainPooledStock` asks a disk until it stops yielding**, the shape `DrainStandaloneStacks` and
`StorageWorldSystem.ExtractStoredItem` already had. This is not optional: `RefundLedger.TryTakeExact`
reads its whole amount from one `ExtractStacks` call, so without it a step needing twelve units off
an `[A x7, B x5]` disk would be paid seven and the craft would fail in the exact shape 25-A was.
Reverting only this loop turns `SB-11` red at `[expected 20, got 16]` and `SB-13a` at
`[expected 8, got 4]`.

**A second axis of the same defect, found by the design review and confirmed against the source.**
`StoredItemStack.Matches(type, -1)` matches **any** prefix, and `-1` is what every crafting path
passes (`RecipeResolver.cs:679`, `UICraftingPanel.cs:1297,1307`). `ModStateMatches` reads only
`globalData`, so two stacks of one type with different prefixes and identical mod state were drawn
together and `ItemIO.Load` stamped one prefix over both. The run rule is therefore
`DiskData.CanMergeStacks` — prefix and mod state together, the same rule defragmenting asks — and
the returned item takes its prefix from the stack that opened the run rather than from a request
that named none. `DiskWithdrawal` groups its draws on the same terms, using the tag the disk just
built rather than re-serializing.

**Runs are numbered, not interned.** The planner only ever compares a stack against the one that
opened the run, and the sweep only ever compares a draw against the handle it is holding open
(`NW-09`), so one comparison per stack is enough. Interning distinct states would put the disk's
stack count inside the loop; a full Terra disk of one `maxStack = 1` type — armour, the class this
issue was reported against — makes that quadratic. `ExtractBenchmark` now measures that shape and
finds it **linear**: 0.0128 / 0.0991 / 0.4003 ms at 64 / 512 / 2048 stacks, on a per-action path.

### Verified by `SB-*`

`SB-01`..`SB-06` pin the planner's rule; `SB-07`..`SB-13` pin what the sweep and the fakes do with
it. On the unfixed code `SB-07` reports the defect verbatim — `[expected A,B, got none]`, two states
folded into one item carrying neither — and disabling only `PlanWithdrawal`'s boundary turns
fourteen assertions red with that same shape. `SB-14` is a source scan of `DiskData.cs`, which cannot
be linked into the runner; it is the only guard on the prefix half, and swapping `CanMergeStacks`
back to `ModStateMatches` turns `SB-14f` red while everything else stays green.

`NW-12` keeps its differential against `Tests/LegacySingleHandleDrain.cs`, but the layout matrix now
declares what each layout is *expected* to do rather than leaving the divergent shapes out of the
list — the omission [20](20-depth-origin-off-by-one.md) is about. Most layouts still assert
equality, including the mixed-state single-disk ones, because the legacy rule never asks a disk
twice and a one-item caller puts the second run back: both hand over the first run. The one shape
where they part is an in-disk boundary with the opening state waiting on a **later** disk, which the
old rule walked past and folded; that is pinned by value as `SB-15`, and a layout parked in the
divergent list that quietly agrees everywhere now fails. `SB-16` sweeps both arms asserting no item
comes back stateless and no drain overdraws.

Needs in-game testing, and specific to this pass: **withdraw a large count of a `maxStack = 1`
poolable type** — armour in a world running a mod that writes per-instance state on it, which
[24](24-globaldata-treated-as-item-identity.md)'s "Accepted, not fixed" names Calamity's `Charge`
and `AppliedEnchantment` as — and confirm the per-click count and that no piece loses its state.
Then craft with that type as an ingredient and confirm the step is paid in full from one sweep.

## Verified by

`BD-*`, `ID-*`, `FX-*`, `NW-*`, `HB-*`, `SB-*` and `PX-07` in `Tests/Program.cs`. Reverting only the loop in
`TryTakeExact` turns `BD-02*` and `FX-06*` red with the reported outcome — the craft produces
nothing and all 18 Door Pants stay put. Reverting only `Refund`'s direction turns `ID-04` red with
one of the player's three stacks left and two stateless copies in place of the others.

`ID-02` rules out merging handles, which `PlanWithdrawal` cannot produce today — it guards the rule,
not this change. `ID-04` is the guard on this change. `FX-*` runs against
`Tests/Fixtures/band-of-door.tsdump.txt`, a three-hop slice of the reported `/tsdump` that resolves
to the same three steps as the full 14,178-recipe graph.

`/tsdump` now writes item names on every storage and recipe line. Item type ids are assigned at load
time, so a dump without names cannot be read against anyone else's mod list.

`NW-*` covers the one-sweep drain against `FakeDiskNetwork`: that the network is swept for pooled
stock exactly once, that a state boundary opens a second handle, and that the handle budget is
honoured at every value from 0 to 13 rather than at sampled points. `NW-12` is the guard on
*unchanged* behaviour — `Tests/LegacySingleHandleDrain.cs` keeps the pre-change rule, and `NW-12`
sweeps a matrix of disk layouts asserting a one-item withdrawal still agrees with it everywhere.
Kept for the same reason `BuggyPreview` is: once the new implementation has replaced the old, a
committed copy of the old is the only thing that makes "unchanged" checkable.

`HB-*` covers recovery by handle. Reverting only `TakeBack`'s handle lookup turns `HB-01`, `HB-03`,
`HB-04a` and `HB-06a` red with the reported shape — `HB-01` reporting `got made`, the player's charm
destroyed and the run's copy left in its place — while `HB-02`'s unit count stays green, which is
the defect stated exactly: the arithmetic balances, the identity does not. `HB-05` stays green
throughout, pinning that plain interchangeable units still recover by type.

`Tests/FakeStorage.cs` now runs through `NetworkWithdrawal.Drain` rather than a second hand-written
copy of the rule, so `BD-*`, `ID-*`, `FX-*`, `PX-*` and `TX-*` exercise the shipped sweep.

Needs in-game testing: craft Band of Door; craft something whose material is spread over two disks;
upgrade a storage disk (always its own item, and consumed as an ingredient); confirm a craft that
cannot be paid for now prints a reason.

And, specific to this pass — **the two things no assertion can reach**, because
`StorageWorldSystem.cs` and `DiskData.cs` still cannot be linked into the runner:

- **That recovery by handle fires at all.** For a product that landed as its own stack it depends on
  `ItemIO.Save` on the still-unmutated produced item reproducing what `DiskData.InsertItem` stored
  for it. Note the invariant is weaker than "the whole tag, byte for byte": `ExtractStoredStack`
  compares through `ModStateMatches`, which reads **only the `globalData` key**, so the stack count
  embedded in the tag is not part of the comparison. If it does not hold, every recovery quietly
  falls back to the type-based draw: today's behaviour, never worse, but 25-C would not actually be
  fixed in-game.
  Craft a multi-step chain that aborts while the player holds a stack of the intermediate's type, and
  confirm the player's stack is the one still there.
  `Tests/FakeStorage.cs` cannot stand in for this: its insert never merges, so it models only the
  own-stack case.
- **The adapter itself** — `DiskWithdrawal`'s state grouping, put-back, `_modifiedTracker` marking
  and `StorageVersion` bumping. The rule it carries out is asserted; the binding to real disks is not.

## Related

[05](05-extractitem-stamps-tag-on-whole-withdrawal.md),
[22](22-aborted-plan-keeps-its-intermediates.md),
[24](24-globaldata-treated-as-item-identity.md).
