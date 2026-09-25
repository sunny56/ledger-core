namespace LedgerCore.Domain;

// Minor units only. Keeping decimal out of the ledger means the ledger itself
// can never introduce a rounding error - conversion happens at the edges.
public readonly record struct Money(long MinorUnits, string Currency)
{
    public static Money Zero(string currency) => new(0, currency);

    public Money Add(Money other)
    {
        if (!string.Equals(Currency, other.Currency, StringComparison.Ordinal))
            throw new CurrencyMismatchException(Currency, other.Currency);

        return this with { MinorUnits = checked(MinorUnits + other.MinorUnits) };
    }

    public Money Negate() => this with { MinorUnits = checked(-MinorUnits) };

    public bool IsPositive => MinorUnits > 0;

    public override string ToString() => $"{MinorUnits / 100m:0.00} {Currency}";
}

public sealed class CurrencyMismatchException(string left, string right)
    : InvalidOperationException($"Currency mismatch: {left} vs {right}");
