using LedgerCore.Application;
using LedgerCore.Domain;
using LedgerCore.Infrastructure;
using LedgerCore.Infrastructure.Strategies;
using Xunit;

namespace LedgerCore.ConcurrencyTests;

public class DomainTests
{
    [Fact]
    public void Entry_MustSumToZero()
    {
        var postings = new[]
        {
            new Posting(new AccountId("a"), new Money(-100, "USD")),
            new Posting(new AccountId("b"), new Money(90, "USD"))
        };

        Assert.Throws<UnbalancedEntryException>(() => JournalEntry.Create("k1", postings, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Entry_MustUseOneCurrency()
    {
        var postings = new[]
        {
            new Posting(new AccountId("a"), new Money(-100, "USD")),
            new Posting(new AccountId("b"), new Money(100, "EUR"))
        };

        Assert.Throws<UnbalancedEntryException>(() => JournalEntry.Create("k2", postings, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Entry_RejectsNonPositiveTransfer()
    {
        Assert.Throws<ArgumentException>(() => JournalEntry.Transfer(
            "k3", new AccountId("a"), new AccountId("b"), new Money(0, "USD"), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Money_RejectsCrossCurrencyArithmetic()
    {
        Assert.Throws<CurrencyMismatchException>(() => new Money(100, "USD").Add(new Money(100, "PKR")));
    }
}

public class IdempotencyTests
{
    // Network retry, queue redelivery, impatient user clicking twice - all the same thing.
    [Fact]
    public void SameKey_AppliedOnce_EvenUnderConcurrency()
    {
        var harness = new LedgerHarness();
        var service = harness.ServiceFor(new PessimisticLockStrategy(harness.Store));

        var result = harness.RunConcurrently(service, threads: 8, operationsPerThread: 100,
            (_, _) => harness.Transfer(0, 1, 500, "the-one-and-only-key"));

        Assert.Empty(result.Failures);
        Assert.Equal(1, result.Applied);
        Assert.Equal(799, result.Duplicates);
        Assert.Equal(harness.OpeningTotal, result.ClosingTotal);
        Assert.Equal(1_000_00 - 500, harness.Store.Read(harness.Accounts[0]).Balance);
        Assert.Equal(1_000_00 + 500, harness.Store.Read(harness.Accounts[1]).Balance);
    }

    // The failure a key-only store cannot see. A client reuses a key - a recycled
    // order id, a key built from something not quite unique - and asks for something
    // different. Matching on the key alone answers "duplicate, you are fine" and the
    // caller books a transfer that never happened.
    [Fact]
    public void SameKey_DifferentEntry_IsRejected()
    {
        var harness = new LedgerHarness();
        var service = harness.ServiceFor(new PessimisticLockStrategy(harness.Store));

        Assert.True(service.Post(harness.Transfer(0, 1, 500, "reused-key")));

        Assert.Throws<IdempotencyConflictException>(
            () => service.Post(harness.Transfer(0, 1, 900, "reused-key")));

        Assert.Equal(1_000_00 - 500, harness.Store.Read(harness.Accounts[0]).Balance);
        Assert.Equal(harness.OpeningTotal, harness.Store.TotalBalance());
    }

    // Claiming the key before doing the work means a rejected transfer can take its
    // own key down with it. The second attempt below has to fail the same way it
    // failed the first time; coming back as a silent duplicate would tell the caller
    // the transfer went through.
    [Fact]
    public void RejectedTransfer_DoesNotPoisonItsOwnKey()
    {
        var harness = new LedgerHarness(accountCount: 2, openingPerAccount: 1_000);
        var service = harness.ServiceFor(new PessimisticLockStrategy(harness.Store, enforceNonNegative: true));

        var entry = harness.Transfer(0, 1, 5_000, "not-enough-money");

        Assert.Throws<InsufficientFundsException>(() => service.Post(entry));
        Assert.Throws<InsufficientFundsException>(() => service.Post(entry));

        Assert.Equal(harness.OpeningTotal, harness.Store.TotalBalance());
    }

    [Fact]
    public void ReleasedKey_CanBeClaimedAgain()
    {
        var store = new InMemoryIdempotencyStore();

        Assert.Equal(IdempotencyOutcome.Claimed, store.Claim("k", "fp"));
        Assert.Equal(IdempotencyOutcome.Replay, store.Claim("k", "fp"));

        store.Release("k");

        Assert.Equal(IdempotencyOutcome.Claimed, store.Claim("k", "fp"));
    }

    // Same transfer, postings handed over in the other order. A fingerprint that
    // depended on ordering would call this a different request and reject a retry
    // that is in fact identical.
    [Fact]
    public void PostingOrder_DoesNotChangeTheFingerprint()
    {
        var from = new AccountId("acct-000");
        var to = new AccountId("acct-001");
        var amount = new Money(500, LedgerHarness.Currency);
        var at = DateTimeOffset.UtcNow;

        var forward = JournalEntry.Create("k",
            [new Posting(from, amount.Negate()), new Posting(to, amount)], at);

        var reversed = JournalEntry.Create("k",
            [new Posting(to, amount), new Posting(from, amount.Negate())], at);

        Assert.Equal(forward.Fingerprint(), reversed.Fingerprint());
    }
}

// Overdraft protection is policy, not a law of the ledger, so it is opt-in.
// It still has to hold under concurrency when it is switched on.
public class OverdraftTests
{
    [Fact]
    public void NonNegativePolicy_HoldsUnderConcurrency()
    {
        var harness = new LedgerHarness(accountCount: 2, openingPerAccount: 1_000);
        var service = harness.ServiceFor(new PessimisticLockStrategy(harness.Store, enforceNonNegative: true));

        // 40,000 requested from an account holding 1,000.
        var result = harness.RunConcurrently(service, threads: 8, operationsPerThread: 50,
            (thread, op) => harness.Transfer(0, 1, 100, $"od-{thread}-{op}"));

        Assert.All(result.Failures, ex => Assert.IsType<InsufficientFundsException>(ex));
        Assert.Equal(10, result.Applied);
        Assert.Equal(0, harness.Store.Read(harness.Accounts[0]).Balance);
        Assert.Equal(harness.OpeningTotal, result.ClosingTotal);
    }
}
