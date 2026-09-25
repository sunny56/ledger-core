using System.Collections.Concurrent;
using LedgerCore.Application;
using LedgerCore.Domain;

namespace LedgerCore.Infrastructure;

// In-memory, but with real locks and a real version counter, so the concurrency
// semantics line up with what a row lock and a rowversion column would give us.
// Postgres adapter goes behind the same interface later.
public sealed class InMemoryAccountStore : IAccountStore
{
    private sealed class Row
    {
        public long Balance;
        public long Version;
        public readonly object Gate = new();
    }

    private readonly ConcurrentDictionary<AccountId, Row> _rows = new();

    public InMemoryAccountStore(IEnumerable<(AccountId Id, long Opening)> accounts)
    {
        foreach (var (id, opening) in accounts)
            _rows[id] = new Row { Balance = opening };
    }

    private Row RowFor(AccountId id) =>
        _rows.TryGetValue(id, out var row) ? row : throw new KeyNotFoundException($"Unknown account {id}");

    public AccountSnapshot Read(AccountId id)
    {
        var row = RowFor(id);
        lock (row.Gate)
            return new AccountSnapshot(id, row.Balance, row.Version);
    }

    public void WriteUnsafe(AccountId id, long newBalance)
    {
        var row = RowFor(id);
        row.Balance = newBalance;
        row.Version++;
    }

    public bool TryWrite(AccountId id, long newBalance, long expectedVersion)
    {
        var row = RowFor(id);
        lock (row.Gate)
        {
            if (row.Version != expectedVersion)
                return false;

            row.Balance = newBalance;
            row.Version++;
            return true;
        }
    }

    public IDisposable Lock(IReadOnlyCollection<AccountId> accounts)
    {
        var gates = accounts.Distinct().OrderBy(a => a).Select(a => RowFor(a).Gate).ToArray();
        return new MultiLock(gates);
    }

    public long TotalBalance()
    {
        long total = 0;
        foreach (var row in _rows.Values)
            lock (row.Gate)
                total = checked(total + row.Balance);

        return total;
    }

    private sealed class MultiLock : IDisposable
    {
        private readonly object[] _gates;
        private int _taken;

        public MultiLock(object[] gates)
        {
            _gates = gates;
            foreach (var gate in gates)
            {
                Monitor.Enter(gate);
                _taken++;
            }
        }

        public void Dispose()
        {
            for (var i = _taken - 1; i >= 0; i--)
                Monitor.Exit(_gates[i]);

            _taken = 0;
        }
    }
}

public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);

    public bool TryClaim(string idempotencyKey) => _seen.TryAdd(idempotencyKey, 0);

    public int Count => _seen.Count;
}
