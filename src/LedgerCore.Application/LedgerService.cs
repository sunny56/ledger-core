using LedgerCore.Domain;

namespace LedgerCore.Application;

public sealed class LedgerService(IPostingStrategy strategy, IIdempotencyStore idempotency)
{
    public string StrategyName => strategy.Name;

    // Returns false for a duplicate replay rather than throwing - callers retry a lot
    // and an exception per retry is noise, not information. A key reused for a
    // different entry is the opposite case and does throw, because silently
    // reporting success for work nobody did is the worst outcome available here.
    public bool Post(JournalEntry entry)
    {
        if (idempotency.Claim(entry.IdempotencyKey, entry.Fingerprint()) == IdempotencyOutcome.Replay)
            return false;

        try
        {
            strategy.Post(entry);
        }
        catch
        {
            // The claim goes back, or an overdraft rejection permanently poisons its
            // own key. Note what this does not fix: a process death between the two
            // leaves the claim behind and the postings undone. The only real answer
            // is writing the claim and the postings in one transaction, which an
            // in-memory store cannot do and a database can.
            idempotency.Release(entry.IdempotencyKey);
            throw;
        }

        return true;
    }
}
