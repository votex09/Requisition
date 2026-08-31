# Half the 2026-08-24 fixes had no unit-test surface

**Severity:** HIGH — process gap, not a runtime defect
**Area:** `Tests/` project scope
**Status:** FIXED 2026-08-25 — READY FOR TESTING / HUMAN REVIEW

## The problem

`Tests/Tests.csproj` compiled a deliberately tiny set of files: the resolver core plus four
Terraria-free UI helpers. Everything else in the mod touches `Terraria.Item`, `TagCompound`,
`Main.*` or `ModPacket`, so it could not be linked into the runner and was not covered at all.

Of the 20 fixes made on 2026-08-24, **9 were pinned by assertions and 11 rested on reading the code
and one in-game build**. The untested half was also the half that moves items:

| Issue | Fix | Now covered by |
|---|---|---|
| [01](01-disk-upgrade-undercharges.md) disk upgrade undercharge | `TryConsumeMaterials` | `TX-01`..`TX-07` |
| [02](02-server-upgrade-no-material-check.md) server upgrade gate | same | `TX-*` |
| [03](03-executeplan-unchecked-extract-insert.md) `ExecutePlan` refund | checked extract + abort | `PX-01`..`PX-06` |
| [04](04-defragment-destroys-per-instance-data.md) defragment identity | `CanMergeStacks` | `DF-01`..`DF-06`, `DG-*` |
| [05](05-extractitem-stamps-tag-on-whole-withdrawal.md) extract tag stamping | two-pass extract | `SL-01`..`SL-06` |
| [09](09-output-slot-cache-ignores-disk-set.md) output-slot cache | version reset | `RC-01`..`RC-04a` |
| [12](12-storagediskbase-clone-drops-fullitemtag.md) clone drops tag | one field | still in-game only |
| [13](13-partial-deposit-reports-failure.md) deposit flag | `DepositOutcome` | `DP-01`..`DP-06` |
| [14](14-recipe-conditions-snapshotted-once.md) live conditions | timed re-check | `RC-08`..`RC-10a` |
| [15](15-favorites-version-not-polled.md) favorites version | version poll | `RC-05`..`RC-06` |
| [16](16-favorites-hit-rects-outside-clip.md) clipped hit rects | visibility test | `HR-01`..`HR-08` |

An item-duplication fix that nothing asserts is one refactor away from silently coming back — and
these are the bugs that cost a player their save, not their patience.

## It paid for itself immediately

Writing the first assertion this file asked for found
[22](22-aborted-plan-keeps-its-intermediates.md): a multi-step craft that aborts refunded the
materials **and** kept the intermediate made from them. Shipped, reachable, and a duplication bug.

It was not found by reading the code. The refund path had been read carefully twice, most recently
while writing the fix for [03](03-executeplan-unchecked-extract-insert.md), and looked right both
times. What found it was writing down "extraction comes up short mid-plan → every earlier
extraction refunded" as an executable sentence and then asking the obvious follow-up: *and what
happened to the plank?*

A second, smaller leak in the same shape (`TryConsumeMaterials` dropping a crafted shortfall that
would not fit) came out of the same exercise.

## What the codebase already did right

The resolver was made testable by pushing the algorithm behind `IRecipeEnvironment` and keeping
`CoreResolver` free of Terraria. `WindowStackCore`, `DepositGate`, `UIClickBlocker` and
`FavoritesRowCache` follow the same pattern — Terraria-free precisely so they can be linked into
the runner.

**Every extraction below is the same move applied once more.** None needed a mocking framework, and
none changed shipped behaviour except where it fixed [22](22-aborted-plan-keeps-its-intermediates.md).

## Extractions made

### 1. `ICraftingStorage<TItem>` for the consume/execute transaction — covers 01, 02, 03

`Helpers/Resolver/CraftingTransaction.cs`. Storage reduced to a handful of operations over opaque
item handles: `CountItem`, `ExtractStacks`, `ExtractStored`, `Insert`, `StackOf`, `SplitOff`.
Terraria binds `TItem` to `Item`; the tests bind it to a plain class and a dictionary.
(`Extract` was replaced by `ExtractStacks` on 2026-08-26 — see
[25](25-craft-costed-against-a-count-it-cannot-withdraw.md).)

- `RefundLedger<TItem>` — everything taken this run, and putting it back
- `MaterialConsumer<TItem>` — the all-or-nothing material list behind both disk-upgrade paths
- `PlanExecutor<TItem>` — the plan's step loop
- `IStepProducer<TItem>` — the Terraria-only half (building an item, carrying a disk GUID across an
  upgrade, splitting batch excess), so it stays out of the core

`RecipeResolver` keeps `WorldCraftingStorage` and `PlanStepProducer` as the live bindings. Assertions
cover every bullet this file originally listed, including the two that turned out to be wrong.

### 2. `StackSelection` — covers 04, 05

`Common/StackSelection.cs`. Deciding whether a stack has per-instance data needs NBT and stays on
`DiskData`; deciding what to *do* about that verdict does not.

- `PlanWithdrawal` — which stacks a bulk extract draws from, in what order, and when the unique
  fallback applies. `DiskData.ExtractItem` now carries out its plan.
- `PlanDonorMove` — what moving one donor stack onto a target disk comes to: merges into partials,
  fresh slots, what stays behind, and the rule that a unique stack moves whole or not at all.
  `StorageWorldSystem.Defragment` now carries out its plan.

`SL-01` is the reported shape verbatim: a unique stack sorted first, 300 plain units behind it,
withdrawn as 300 — returns 300 plain and leaves the unique stack alone.

### 3. `PanelRefreshCache` — covers 09, 14, 15

`Content/UI/PanelRefreshCache.cs`, extracted the way `FavoritesRowCache` already was. Every stamp
that says whether the panel's derived state has gone stale: the output slot's `(storageVersion,
outputType)` pair plus an explicit `InvalidateOutputStock()` for a disk-set change, the favorites
version, the storage version, and the condition re-check interval. `ApplyFlags` carries the
"only re-filter when a flag actually flipped" rule and the stale-array guard.

`RC-10` covers the `uint` tick wrap, which nothing had looked at.

### 4. Row visibility on `FavoritesRowCache` — covers 16

`IsHitRectVisible` and `GetBodyBottom`. The rule turned out to be simpler than it read: a row
registers a hit rect when its rect's **bottom edge** lies within the clipped body. `HR-06` builds 40
rows into a 200px body and asserts exactly 5 register.

### 5. `DepositOutcome` — covers 13

`Common/DepositOutcome.cs`. The offered count and the leftover held as one value, with `Deposited`,
`AnyDeposited` and `NeedsReturn` derived from them, so reading the offered count after overwriting
it is unspellable. Both deposit sites in `NetworkHandler` build one before touching `item.stack`.

### 6. `NetworkWithdrawal` — covers 25-A, and closes 25-D

`Common/NetworkWithdrawal.cs`, added 2026-08-26. How a withdrawal walks the disks: pooled stock
first across the whole network, then stacks that each stand for themselves, with a handle opened at
every state boundary and a `handleLimit` saying how many items the caller can hold. Deciding whether
two draws may share one item needs NBT and stays on `DiskData`; deciding what to *do* about that
verdict does not, so the draw reports a `StateGroup` and the rule compares integers.

This is what [25](25-craft-costed-against-a-count-it-cannot-withdraw.md)'s fourth bullet asked for.
`StorageWorldSystem` and `DiskData` still cannot be linked — they bind `Terraria.Item`,
`TagCompound` and `Main.*`, exactly as this file said — so the rule came out instead of the files
going in. `Tests/FakeStorage.cs` runs through it too, so the crafting tests exercise the shipped
sweep rather than a copy of it.

`Tests/LegacySingleHandleDrain.cs` keeps the pre-change rule so `NW-12` can assert a one-item
withdrawal still agrees with it across a matrix of layouts — `BuggyPreview`'s trick applied to
item movement.

### 7. `DefragmentCore` — closes 04's last untested half, and 23i's recommended follow-up

`Common/DefragmentCore.cs`, added 2026-08-26. The defragment sweep itself: the target/donor/slot loop
nesting, the merge-candidate index bookkeeping, the stale-slot bounds check, the self-donor guard and
the application of `PlanDonorMove`'s output. `StorageWorldSystem.Defragment` is now a caller of it.

Extractions 1–6 all handed the core an interface and kept the collection behind it. This one does the
opposite, following `ICraftingStorage<TItem>` rather than `IWithdrawalNetwork`: `Sweep<TStack, TRules>`
takes the caller's own `List<TStack>` and does every `Add`, `RemoveAt` and count assignment itself.
That is the whole point — an interface that owned the mutations would have put the descending donor
walk, the same-object relocation and the self-donor guard on the *fake's* side, which is the second
encoding this file keeps warning about. `IDefragmentRules<TStack>` carries only what needs Terraria:
eight one-line bindings, of which `CanMerge` is `DiskData.CanMergeStacks` and nothing else.

`TRules` is a type parameter rather than the interface so a `readonly struct` implementation is
specialised and inlined by the JIT. Measured: without it the sweep gave back ~30% on the bulk-storage
fixture, because the rules are asked several times per candidate stack.

`DG-01`..`DG-18c` — 42 assertions — drive the shipped sweep. Nine deliberate mutations of it were
tried and **eight turned a specific assertion red**; the one for `DG-02` (two donors of one identity
must end as one stack, which needs the index fed before every append) turned **nothing** in `DF-*` or
`MX-*` red, which is the measurement of what this gap was worth.

The ninth is recorded because it did not: swapping `Items.Add` and `Items.RemoveAt` in the whole-move
branch changes nothing. The core holds the stack in a local before either call, so the ordering has
no observable consequence — it was a real constraint only in a rejected design where the sweep passed
slot positions across an interface and the removal invalidated the handle. No assertion covers it
because there is nothing there to cover.

Two of the first-draft assertions were themselves vacuous and were caught by review rather than by
the suite, which is worth recording as the same lesson one level up. `DG-12b` — the one certifying
that a stack of another item shifted into a recorded slot is never credited — passed unchanged when
the merge rule was mutated to accept everything: `PlanDonorMove` stops as soon as the donor is
placed, so a 6-unit donor never reached the second candidate. Enlarging the donor to 200 fixes it.
Then it passed *again*, because the fixture sprang its trap from inside `CanMerge`, the very call the
mutation deleted. The trigger moved to `GetCount`, which the sweep reads for every candidate whatever
the rule answers.

`Tests/HotPathBenchmarks.cs` no longer transcribes the sweep either: `DisksHoldTheSame` now compares
the shipped sweep against the linear rescan it replaced, at six scales up to 65 520 stacks. The old
"indexed" replica had neither the bounds check nor the self-donor guard, so it had been measuring
something cheaper than what ships.

**What is still in-game only**, and it is the residual gap: `StoredStackRules`' eight bindings and
`CopyStackWithCount`. Issue 04's third fix bullet — carry `ModData` and `FullItemTag` onto a split
stack — lives in `CopyStackWithCount`, and until this pass it had **no assertion of any kind**.
`MX-14` adds a source match, which is the only mechanism available for a method that builds a
`StoredItemStack`. The sweep around it is now executable; the field copy at its centre is not.

## Still not extracted

Packet read/write ordering, `SendSyncDriveBay`, prefix rolling and mod `OnCreated` hooks. These are
thin adapters over tModLoader and a fake would only assert that the fake was called.

[12](12-storagediskbase-clone-drops-fullitemtag.md) is one field assignment in a `Clone` override
and stays in-game only.

Multiplayer behaviour ([02](02-server-upgrade-no-material-check.md),
[12](12-storagediskbase-clone-drops-fullitemtag.md),
[13](13-partial-deposit-reports-failure.md)) still needs a real two-client session — but what that
session has to prove has shrunk to "the packet reaches the transaction", not "the transaction is
correct".

## Lesson worth keeping

[20](20-depth-origin-off-by-one.md) shipped a passing test over a defect because the test sampled
`{1, 2, 5, 10, 20}` and the only divergent value was 11. Where a rule has a boundary, sweep it
contiguously; where two components encode the same rule, assert they agree across a range rather
than at hand-picked points. `SA-*` and `LF-*` are the pattern to copy.

And the one this file added: **the assertion you write to confirm a fix is where you find the next
bug.** Reading the code twice did not find [22](22-aborted-plan-keeps-its-intermediates.md).
Writing down what the code was supposed to guarantee did.
