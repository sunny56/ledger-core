namespace LedgerCore.Domain;

public readonly record struct AccountId(string Value) : IComparable<AccountId>
{
    public int CompareTo(AccountId other) => string.CompareOrdinal(Value, other.Value);
    public override string ToString() => Value;
}
