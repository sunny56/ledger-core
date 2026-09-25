using LedgerCore.Application;
using LedgerCore.Domain;

namespace LedgerCore.Infrastructure.Strategies;

// Read, compute, write. No lock, no version check.
// Kept in the repo because ConservationTests asserts that this one loses money -
// without it the other strategies are solutions to a problem nobody has seen.
public sealed class NaiveStrategy(IAccountStore store) : IPostingStrategy
{
    public string Name => "naive (unsafe)";

    public void Post(JournalEntry entry)
    {
        foreach (var posting in entry.Postings)
        {
            var snapshot = store.Read(posting.Account);

            // Widens the race window so the test fails reliably instead of one run in fifty.
            // The window exists in real code too, it is just narrower.
            Thread.SpinWait(50);

            store.WriteUnsafe(posting.Account, snapshot.Balance + posting.Amount.MinorUnits);
        }
    }
}
