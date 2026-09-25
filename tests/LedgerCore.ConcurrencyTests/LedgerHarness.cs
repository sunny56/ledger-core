using LedgerCore.Application;
using LedgerCore.Domain;
using LedgerCore.Infrastructure;

namespace LedgerCore.ConcurrencyTests;

public sealed class LedgerHarness
{
    public const string Currency = "USD";

    public InMemoryAccountStore Store { get; }
    public InMemoryIdempotencyStore Idempotency { get; }
    public IReadOnlyList<AccountId> Accounts { get; }
    public long OpeningTotal { get; }

    public LedgerHarness(int accountCount = 4, long openingPerAccount = 1_000_00)
    {
        Accounts = Enumerable.Range(0, accountCount)
            .Select(i => new AccountId($"acct-{i:D3}"))
            .ToArray();

        Store = new InMemoryAccountStore(Accounts.Select(a => (a, openingPerAccount)));
        Idempotency = new InMemoryIdempotencyStore();
        OpeningTotal = openingPerAccount * accountCount;
    }

    public LedgerService ServiceFor(IPostingStrategy strategy) => new(strategy, Idempotency);

    public JournalEntry Transfer(int fromIndex, int toIndex, long minorUnits, string key) =>
        JournalEntry.Transfer(key, Accounts[fromIndex], Accounts[toIndex],
            new Money(minorUnits, Currency), DateTimeOffset.UtcNow);

    // Barrier so every thread starts at the same instant - without it the threads
    // stagger and most of the contention never happens.
    public ConcurrencyRunResult RunConcurrently(
        LedgerService service,
        int threads,
        int operationsPerThread,
        Func<int, int, JournalEntry> entryFactory)
    {
        using var gate = new Barrier(threads);
        var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var applied = 0;
        var duplicates = 0;

        var workers = Enumerable.Range(0, threads).Select(threadIndex => new Thread(() =>
        {
            gate.SignalAndWait();

            for (var op = 0; op < operationsPerThread; op++)
            {
                try
                {
                    if (service.Post(entryFactory(threadIndex, op)))
                        Interlocked.Increment(ref applied);
                    else
                        Interlocked.Increment(ref duplicates);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }
        })
        { IsBackground = true }).ToArray();

        foreach (var worker in workers) worker.Start();

        // Timeout turns a deadlock into a failed test instead of a hung build.
        foreach (var worker in workers) worker.Join(TimeSpan.FromSeconds(60));

        return new ConcurrencyRunResult(applied, duplicates, failures.ToArray(), Store.TotalBalance());
    }
}

public sealed record ConcurrencyRunResult(
    int Applied,
    int Duplicates,
    IReadOnlyList<Exception> Failures,
    long ClosingTotal);
