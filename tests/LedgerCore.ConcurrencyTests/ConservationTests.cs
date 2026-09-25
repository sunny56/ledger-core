using LedgerCore.Infrastructure.Strategies;
using Xunit;

namespace LedgerCore.ConcurrencyTests;

// sum(balances) must not move when money is only being transferred.
public class ConservationTests
{
    private const int Threads = 8;
    private const int OperationsPerThread = 250;

    // The negative result. If this ever starts conserving, the race window has closed
    // and the test has stopped proving anything - widen the window, do not delete it.
    [Fact]
    public void NaiveStrategy_LosesMoney_UnderConcurrency()
    {
        var harness = new LedgerHarness();
        var service = harness.ServiceFor(new NaiveStrategy(harness.Store));

        var result = harness.RunConcurrently(service, Threads, OperationsPerThread,
            (thread, op) => harness.Transfer(0, 1, 100, $"naive-{thread}-{op}"));

        Assert.Empty(result.Failures);
        Assert.NotEqual(harness.OpeningTotal, result.ClosingTotal);
    }

    [Fact]
    public void PessimisticLock_ConservesTotal_UnderConcurrency()
    {
        var harness = new LedgerHarness();
        var service = harness.ServiceFor(new PessimisticLockStrategy(harness.Store));

        var result = harness.RunConcurrently(service, Threads, OperationsPerThread,
            (thread, op) => harness.Transfer(0, 1, 100, $"pess-{thread}-{op}"));

        Assert.Empty(result.Failures);
        Assert.Equal(Threads * OperationsPerThread, result.Applied);
        Assert.Equal(harness.OpeningTotal, result.ClosingTotal);
    }

    [Fact]
    public void OptimisticCas_ConservesTotal_UnderConcurrency()
    {
        var harness = new LedgerHarness();
        var service = harness.ServiceFor(new OptimisticConcurrencyStrategy(harness.Store));

        var result = harness.RunConcurrently(service, Threads, OperationsPerThread,
            (thread, op) => harness.Transfer(0, 1, 100, $"opt-{thread}-{op}"));

        Assert.Empty(result.Failures);
        Assert.Equal(Threads * OperationsPerThread, result.Applied);
        Assert.Equal(harness.OpeningTotal, result.ClosingTotal);
    }

    // A->B and B->A at the same time. Deadlocks unless the lock order is deterministic.
    [Fact]
    public void PessimisticLock_DoesNotDeadlock_OnCrossingTransfers()
    {
        var harness = new LedgerHarness();
        var service = harness.ServiceFor(new PessimisticLockStrategy(harness.Store));

        var result = harness.RunConcurrently(service, Threads, OperationsPerThread,
            (thread, op) => thread % 2 == 0
                ? harness.Transfer(0, 1, 50, $"cross-a-{thread}-{op}")
                : harness.Transfer(1, 0, 50, $"cross-b-{thread}-{op}"));

        Assert.Empty(result.Failures);
        Assert.Equal(Threads * OperationsPerThread, result.Applied);
        Assert.Equal(harness.OpeningTotal, result.ClosingTotal);
    }

    // Worst case for CAS: every thread touches every account, so almost every
    // attempt invalidates somebody.
    [Fact]
    public void OptimisticCas_ConservesTotal_UnderFullContention()
    {
        var harness = new LedgerHarness(accountCount: 4);
        var service = harness.ServiceFor(new OptimisticConcurrencyStrategy(harness.Store));

        var result = harness.RunConcurrently(service, Threads, 100, (thread, op) =>
        {
            var from = (thread + op) % 4;
            return harness.Transfer(from, (from + 1) % 4, 25, $"full-{thread}-{op}");
        });

        Assert.Empty(result.Failures);
        Assert.Equal(harness.OpeningTotal, result.ClosingTotal);
    }
}
