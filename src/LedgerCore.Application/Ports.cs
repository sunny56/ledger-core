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

public enum IdempotencyOutcome
{
    Claimed,
    Replay
}

public interface IIdempotencyStore
{
    // The fingerprint is what makes this safe against key reuse. A key on its own
    // only answers "have I seen this before". The caller needs "have I seen THIS
    // before", which is a different question with a much worse failure mode.
    IdempotencyOutcome Claim(string idempotencyKey, string fingerprint);

    // Handing the key back after a failed post. Without this, a transfer that threw
    // would block its own retry forever.
    void Release(string idempotencyKey);
}

public interface IPostingStrategy
{
    string Name { get; }
    void Post(JournalEntry entry);
}
