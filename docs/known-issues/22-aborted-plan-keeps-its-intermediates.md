# An aborted multi-step craft refunded the materials AND kept what it made

**Severity:** HIGH — item duplication
**Area:** `RecipeResolver.ExecutePlan`, now `PlanExecutor.Run`
**Status:** FIXED 2026-08-25 — READY FOR TESTING / HUMAN REVIEW

## How it was found

Not by reading the code. It fell out of writing the first assertion that
[21](21-untested-fixes.md) asked for: extraction 1, the `IStorage` seam. The scenario "extraction
comes up short mid-plan → every earlier extraction refunded" was written straight from that file's
bullet list, and the obvious follow-up question — *and what happened to the plank?* — had the wrong
answer.

This is the argument in [21](21-untested-fixes.md) made concrete. The refund logic had been read
carefully twice and looked right both times.

## Symptom

A two-step craft that aborts at the second step leaves the player holding both the ingredients and
the intermediate made from them. Repeatable: every retry that aborts the same way mints another.

## Cause

`ExecutePlan` tracked everything it pulled out of storage in one `consumed` list spanning all
steps, and on abort put all of it back. An intermediate is *extracted* by the step that consumes
it, so it lands in that same list — and gets refunded alongside the materials it was made from.

```
step 0: take 5 WOOD          -> ledger: [5 WOOD]
        make 1 PLANK, store it
step 1: take 1 PLANK         -> ledger: [5 WOOD, 1 PLANK]
        take 3 IRON          -> only 2 in storage, abort
refund:  5 WOOD back, 1 PLANK back
```

Storage now holds the original 5 wood, the original 2 iron, **and a plank that nothing paid for.**

The abort path itself was correct in intent — issue
[03](03-executeplan-unchecked-extract-insert.md) added it precisely so a failed craft could not eat
materials. It just had no way to tell an ingredient the player owned from one this run had
conjured, because both arrive in storage the same way.

## Reachable how

The abort needs storage to hold less than the plan was built against, between resolving and
executing:

- another player on a server withdrawing the same material in that window
- an auto-deposit or sorter moving it
- a plan built against a disk that is unplugged mid-craft

Rare per craft, but it is a duplication bug: rare and repeatable is all it needs.

## Fix applied

`PlanExecutor.Run` records what each step *produced and stored*, separately from what it *took*:

```csharp
// Materials go back, but anything this run conjured must not.
private TItem Abort(RefundLedger<TItem> ledger, List<(int itemType, int count)> intermediates)
{
    ledger.Refund();

    foreach (var (itemType, count) in intermediates)
        _storage.Extract(itemType, count);

    return _storage.Nothing;
}
```

Refund first, then discard: the refund is what puts a consumed intermediate back, so the discard
has to run after it or there is nothing there to remove. Pre-existing stock of the same type is
unaffected — the discard takes exactly the count this run added.

## Second leak, same shape

`TryConsumeMaterials` crafted a shortfall and inserted it without checking the leftover. On a full
network the units that did not fit were simply dropped, and the refund of any earlier material then
had nowhere to go either. `MaterialConsumer.TryStockUp` now takes back the part that did land and
fails the transaction, mirroring what the intermediate path already did.

## Guard against recurrence

- `PX-03c` asserts the intermediate is **not** in storage after a mid-plan abort, alongside
  `PX-03a`/`PX-03b` asserting the materials are.
- `PX-05` covers the same abort via an unstorable intermediate.
- `TX-06b` covers the crafted-shortfall leftover.

The rule to hold on to: **a refund puts back what the player owned, never what this run made.**
Any future step that stores something mid-plan has to register it as an intermediate.
