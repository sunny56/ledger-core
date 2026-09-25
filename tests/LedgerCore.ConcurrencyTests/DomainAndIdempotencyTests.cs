using LedgerCore.Domain;
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
