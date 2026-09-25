using LedgerCore.Domain;

namespace LedgerCore.Application;

public sealed class LedgerService(IPostingStrategy strategy, IIdempotencyStore idempotency)
{
    public string StrategyName => strategy.Name;

    // Returns false for a duplicate replay rather than throwing - callers retry a lot
    // and an exception per retry is noise, not information.
    public bool Post(JournalEntry entry)
    {
        if (!idempotency.TryClaim(entry.IdempotencyKey))
            return false;

        strategy.Post(entry);
        return true;
    }
}
