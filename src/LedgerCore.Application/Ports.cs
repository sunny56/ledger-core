using LedgerCore.Domain;

namespace LedgerCore.Application;

public readonly record struct AccountSnapshot(AccountId Id, long Balance, long Version);

// Kept deliberately small. Every strategy is built from these four primitives,
// which is the only reason they are comparable to each other at all.
public interface IAccountStore
{
    AccountSnapshot Read(AccountId id);

    // No version check. Only the naive strategy uses this directly.
    void WriteUnsafe(AccountId id, long newBalance);

    // Compare-and-swap. False means the version moved under us.
    bool TryWrite(AccountId id, long newBalance, long expectedVersion);

    // Implementations must order the accounts, or crossing transfers deadlock.
    IDisposable Lock(IReadOnlyCollection<AccountId> accounts);

    long TotalBalance();
}

public interface IIdempotencyStore
{
    bool TryClaim(string idempotencyKey);
}

public interface IPostingStrategy
{
    string Name { get; }
    void Post(JournalEntry entry);
}
