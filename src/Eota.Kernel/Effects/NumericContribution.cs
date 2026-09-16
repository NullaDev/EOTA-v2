using System.Numerics;

namespace Eota.Kernel.Effects;

// Contributions merge before they are applied to a snapshot value. Set selection is separate.
public sealed record NumericContribution
{
    public BigInteger Addition { get; }
    public BigInteger Numerator { get; }
    public BigInteger Denominator { get; }
    public static NumericContribution Identity { get; } = new(0, 1, 1);

    private NumericContribution(BigInteger addition, BigInteger numerator, BigInteger denominator)
    {
        if (denominator.IsZero) { throw new DivideByZeroException(); }
        var divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
        if (denominator.Sign < 0) { divisor = -divisor; }
        Addition = addition; Numerator = numerator / divisor; Denominator = denominator / divisor;
    }

    public static NumericContribution Add(BigInteger value) => new(value, 1, 1);
    public static NumericContribution Multiply(BigInteger value) => new(0, value, 1);
    public static NumericContribution Divide(BigInteger value) => new(0, 1, value);

    public NumericContribution Merge(NumericContribution other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(Addition + other.Addition, Numerator * other.Numerator, Denominator * other.Denominator);
    }

    public BigInteger Apply(BigInteger baseline) => (baseline + Addition) * Numerator / Denominator;
}
