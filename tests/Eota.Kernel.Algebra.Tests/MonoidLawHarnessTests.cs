using System.Numerics;
using Eota.Kernel.Effects;

namespace Eota.Kernel.Algebra.Tests;

public sealed class MonoidLawHarnessTests
{
    [Fact]
    public void RuntimeNumericContributionsSatisfyIdentityCommutativityAndAssociativity()
    {
        var values = new BigInteger[] { -7, -1, 0, 1, 3, 19, BigInteger.One << 100 };
        var samples = values.SelectMany(value => new[] { NumericContribution.Add(value), NumericContribution.Multiply(value) })
            .Concat(values.Where(value => !value.IsZero).Select(NumericContribution.Divide))
            .Append(NumericContribution.Add(5).Merge(NumericContribution.Multiply(-2)).Merge(NumericContribution.Divide(3)))
            .ToArray();
        AssertMonoidLaws(samples, NumericContribution.Identity, (left, right) => left.Merge(right));
    }

    [Fact]
    public void RuntimeContributionsRoundOnlyAfterMergingAndHaveNoIntermediateInt64Overflow()
    {
        var contribution = NumericContribution.Add(1).Merge(NumericContribution.Divide(2)).Merge(NumericContribution.Multiply(3));
        Assert.Equal(7, contribution.Apply(4));
        Assert.Equal(-7, NumericContribution.Divide(2).Merge(NumericContribution.Multiply(3)).Apply(-5));
        var large = NumericContribution.Multiply(long.MaxValue).Merge(NumericContribution.Multiply(long.MaxValue))
            .Merge(NumericContribution.Divide(long.MaxValue)).Merge(NumericContribution.Divide(long.MaxValue));
        Assert.Equal(NumericContribution.Identity, large);
        Assert.Equal(5, large.Apply(5));
        Assert.Equal(NumericContribution.Multiply(0), NumericContribution.Multiply(0).Merge(NumericContribution.Divide(-3)));
        Assert.Throws<DivideByZeroException>(() => NumericContribution.Divide(0));
    }

    private static void AssertMonoidLaws<T>(
        IReadOnlyList<T> samples,
        T identity,
        Func<T, T, T> merge)
    {
        foreach (var first in samples)
        {
            Assert.Equal(first, merge(first, identity));
            Assert.Equal(first, merge(identity, first));
            foreach (var second in samples)
            {
                Assert.Equal(merge(first, second), merge(second, first));
                foreach (var third in samples)
                {
                    Assert.Equal(
                        merge(merge(first, second), third),
                        merge(first, merge(second, third)));
                }
            }
        }
    }
}
