namespace LedgerCore.Domain;

public sealed record Posting(AccountId Account, Money Amount);

public sealed class JournalEntry
{
    public string IdempotencyKey { get; }
    public IReadOnlyList<Posting> Postings { get; }
    public DateTimeOffset OccurredAt { get; }

    private JournalEntry(string idempotencyKey, IReadOnlyList<Posting> postings, DateTimeOffset occurredAt)
    {
        IdempotencyKey = idempotencyKey;
        Postings = postings;
        OccurredAt = occurredAt;
    }

    // Validate here rather than at the API edge, so there is no code path anywhere
    // that can hand an unbalanced entry to a strategy.
    public static JournalEntry Create(string idempotencyKey, IReadOnlyList<Posting> postings, DateTimeOffset occurredAt)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Idempotency key is required.", nameof(idempotencyKey));

        if (postings.Count < 2)
            throw new UnbalancedEntryException("An entry needs at least two postings.");

        if (postings.Select(p => p.Amount.Currency).Distinct(StringComparer.Ordinal).Count() != 1)
            throw new UnbalancedEntryException("All postings in an entry must share one currency.");

        var sum = postings.Aggregate(0L, (acc, p) => checked(acc + p.Amount.MinorUnits));
        if (sum != 0)
            throw new UnbalancedEntryException($"Postings must sum to zero, summed to {sum}.");

        return new JournalEntry(idempotencyKey, postings, occurredAt);
    }

    public static JournalEntry Transfer(string idempotencyKey, AccountId from, AccountId to, Money amount, DateTimeOffset occurredAt)
    {
        if (!amount.IsPositive)
            throw new ArgumentException("Transfer amount must be positive.", nameof(amount));

        return Create(idempotencyKey, [new Posting(from, amount.Negate()), new Posting(to, amount)], occurredAt);
    }

    public IEnumerable<AccountId> TouchedAccounts => Postings.Select(p => p.Account).Distinct();
}

public sealed class UnbalancedEntryException(string message) : InvalidOperationException(message);

public sealed class InsufficientFundsException(AccountId account, long balance, long requested)
    : InvalidOperationException($"Account {account} has {balance} but {requested} was requested.");

public sealed class ConcurrencyRetriesExhaustedException(int attempts)
    : InvalidOperationException($"Gave up after {attempts} optimistic retries.");
