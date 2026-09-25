using LedgerCore.Application;
using LedgerCore.Domain;

namespace LedgerCore.Infrastructure.Strategies;

// The SELECT ... FOR UPDATE shape. Correct and easy to reason about, but every
// writer queues behind the hottest account, so throughput falls off under contention.
public sealed class PessimisticLockStrategy(IAccountStore store, bool enforceNonNegative = false) : IPostingStrategy
{
    public string Name => "pessimistic lock";

    public void Post(JournalEntry entry)
    {
        var accounts = entry.TouchedAccounts.ToArray();

        // Store sorts these before locking. Without that, A->B and B->A deadlock.
        using var _ = store.Lock(accounts);

        if (enforceNonNegative)
        {
            foreach (var posting in entry.Postings)
            {
                var current = store.Read(posting.Account).Balance;
                if (current + posting.Amount.MinorUnits < 0)
                    throw new InsufficientFundsException(posting.Account, current, -posting.Amount.MinorUnits);
            }
        }

        foreach (var posting in entry.Postings)
        {
            var snapshot = store.Read(posting.Account);
            store.WriteUnsafe(posting.Account, snapshot.Balance + posting.Amount.MinorUnits);
        }
    }
}
