# ledger-core

A double-entry ledger that stays correct when several writers hit the same account at once.

Three concurrency strategies sit behind one interface and are tested against the same
invariant, so the difference between them is something you can measure rather than argue about.

## The problem

A ledger has one law: a transfer moves money, it never creates or destroys it.

```
sum(all balances) after N transfers == sum(all balances) before
```

Easy with one writer. The usual first implementation reads a balance, computes a new one,
and writes it back:

```csharp
var balance = store.Read(account).Balance;   // A and B both read 1000
store.Write(account, balance + delta);       // B overwrites A, one transfer vanishes
```

In a CRUD app that is a stale field. In a ledger it is missing money, found days later
during reconciliation, with nothing in the log to say where it went.

## What is here

`NaiveStrategy` stays in the codebase, and there is a test asserting that it **fails**
conservation:

```csharp
[Fact]
public void NaiveStrategy_LosesMoney_UnderConcurrency()
{
    // 8 threads, 250 transfers each, one account pair
    Assert.NotEqual(harness.OpeningTotal, result.ClosingTotal);
}
```

Everything else is measured against the same harness and has to conserve.

| Strategy | How | Good when | Falls over when |
|---|---|---|---|
| Naive | read, compute, write | never | always, under concurrency |
| Pessimistic lock | exclusive lock on every touched account, ordered by id | low contention, short transactions | one hot account; deadlocks if you drop the ordering |
| Optimistic CAS | read with version, commit if unmoved, retry with jitter | reads dominate, conflicts rare | hot account turns into a retry storm |

## Deadlock

The pessimistic strategy locks accounts sorted by id. That ordering is the only thing
stopping the crossing-transfer deadlock (A to B while B to A). There is a test for that
shape with a join timeout, so a deadlock fails the build instead of hanging it.

## Invariants in the type system

An unbalanced entry cannot be constructed at all:

- postings sum to zero
- one currency per entry
- at least two postings
- integer minor units, so the ledger cannot introduce a rounding error
- transfers must be positive

Validating in the constructor is cheaper than validating at the edge, because then there is
no path in the system that produces an invalid entry in the first place.

## Idempotency

Every entry carries a key, claimed before the strategy runs. There is a test that fires 800
concurrent posts of the same key and asserts exactly one applied.

## Running it

```bash
dotnet build
dotnet test
```

No Docker, no database. The store is in-memory but uses real locks and a real version
counter, so the semantics match what a row lock and a rowversion column give you.

```
src/
  LedgerCore.Domain           Money, AccountId, JournalEntry
  LedgerCore.Application      IAccountStore, IIdempotencyStore, IPostingStrategy
  LedgerCore.Infrastructure   in-memory store, three strategies
tests/
  LedgerCore.ConcurrencyTests harness, conservation, deadlock, idempotency, overdraft
```

## Next

- [ ] Postgres adapter behind `IAccountStore`, driven by Testcontainers
- [ ] Single writer per partition: one thread owns a hash range, no locks at all.
      Needs a cross-partition protocol before it is useful.
- [ ] Serializable isolation: let the database do it, retry on 40001. Postgres SSI fails at
      commit, SQL Server takes range locks, so the same code behaves very differently.
- [ ] BenchmarkDotNet: throughput and p99 per strategy at 1, 8, 64 writers
- [ ] Outbox, so entries publish events without a dual write
- [ ] OpenTelemetry traces and a conservation metric

## Why

I work on transaction and settlement systems, where getting concurrency wrong means money
goes missing rather than a page rendering slowly. Most writing on this stops at "use a
transaction" or jumps to distributed consensus. This sits in the middle: ordinary
approaches, compared honestly, with the failure mode of each one written down and tested.
