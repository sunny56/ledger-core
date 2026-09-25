using LedgerCore.Application;
using LedgerCore.Domain;

namespace LedgerCore.Infrastructure.Strategies;

// Rowversion compare-and-swap. Nothing is held across the think time, so reads stay
// cheap, but a hot account turns into a retry storm instead. Hitting maxAttempts is a
// signal to shard the account, not to raise the number.
public sealed class OptimisticConcurrencyStrategy(IAccountStore store, int maxAttempts = 100) : IPostingStrategy
{
    public string Name => "optimistic CAS";

    public void Post(JournalEntry entry)
    {
        var accounts = entry.TouchedAccounts.OrderBy(a => a).ToArray();

        var deltas = accounts.ToDictionary(a => a, _ => 0L);
        foreach (var posting in entry.Postings)
            deltas[posting.Account] += posting.Amount.MinorUnits;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var snapshots = accounts.ToDictionary(a => a, store.Read);

            // An entry lands whole or not at all, otherwise the ledger goes unbalanced.
            // The CAS still detects the interleaved writer; this short lock only stops
            // us tearing one entry in half across two accounts.
            using (store.Lock(accounts))
            {
                if (accounts.All(a => store.Read(a).Version == snapshots[a].Version))
                {
                    foreach (var account in accounts)
                    {
                        var applied = store.TryWrite(
                            account,
                            snapshots[account].Balance + deltas[account],
                            snapshots[account].Version);

                        if (!applied)
                            throw new InvalidOperationException("CAS failed inside the commit phase.");
                    }

                    return;
                }
            }

            // Jitter, otherwise the losers all wake up together and collide again.
            Thread.SpinWait(Random.Shared.Next(10, 10 * Math.Min(attempt, 16)));
        }

        throw new ConcurrencyRetriesExhaustedException(maxAttempts);
    }
}
