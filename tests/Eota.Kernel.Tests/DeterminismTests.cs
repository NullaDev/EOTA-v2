using Eota.Kernel.Determinism;

namespace Eota.Kernel.Tests;

public sealed class DeterminismTests
{
    [Fact]
    public void CanonicalWriterHasPinnedV1ByteLayoutAndHash()
    {
        var writer = new CanonicalWriter("test", 1);
        writer.WriteBoolean(true);
        writer.WriteInt32(-2);
        writer.WriteString("e\u0301");

        var bytes = Convert.ToHexString(writer.ToArray()).ToLowerInvariant();
        var hash = CanonicalHash.Compute("test", 1, payload =>
        {
            payload.WriteBoolean(true);
            payload.WriteInt32(-2);
            payload.WriteString("é");
        });

        Assert.Equal("454f54410400000074657374010001feffffff02000000c3a9", bytes);
        Assert.Equal("d9c2f717fbdad6e5fddb7ccb8d58267b7e8a6cf4cc6bc99b2cd51d8df7614a52", hash.ToString());
    }

    [Fact]
    public void RngSeedExpansionAndSamplesMatchV1Vectors()
    {
        var state = GlobalRuleRng.Create(0);
        Assert.Equal(0xe220a8397b1dcdafUL, state.S0);
        Assert.Equal(0x6e789e6aa1b965f4UL, state.S1);
        Assert.Equal(0x06c45d188009454fUL, state.S2);
        Assert.Equal(0xf88bb8a8724c81ecUL, state.S3);

        ulong[] expected =
        [
            0x99ec5f36cb75f2b4UL,
            0xbf6e1f784956452aUL,
            0x1a5f849d4933e6e0UL,
            0x6aa594f1262d2d2cUL,
            0xbba5ad4a1f842e59UL,
            0xffef8375d9ebcacaUL,
            0x6c160deed2f54c98UL,
            0x8920ad648fc30a3fUL
        ];

        foreach (var expectedValue in expected)
        {
            var sample = GlobalRuleRng.NextUInt64(state);
            Assert.Equal(expectedValue, sample.Value);
            state = sample.State;
        }

        Assert.Equal((ulong)expected.Length, state.SampleCount);
    }

    [Fact]
    public void SingleCandidateDoesNotConsumeRuleRng()
    {
        var state = GlobalRuleRng.Create(42);

        var sample = GlobalRuleRng.NextIndex(state, 1);

        Assert.Equal(0, sample.Index);
        Assert.Equal(0UL, sample.SamplesConsumed);
        Assert.Equal(state, sample.State);
    }

    [Fact]
    public void RngAuditCounterCannotWrapWhileAlgorithmArithmeticRemainsModular()
    {
        var state = GlobalRuleRng.Create(42) with { SampleCount = ulong.MaxValue - 1 };
        var last = GlobalRuleRng.NextUInt64(state);
        Assert.Equal(ulong.MaxValue, last.State.SampleCount);
        Assert.Equal(GlobalRuleRng.NextUInt64(GlobalRuleRng.Create(42)).Value, last.Value);
        Assert.Throws<OverflowException>(() => GlobalRuleRng.NextUInt64(last.State));
        Assert.Equal(last.State, GlobalRuleRng.NextIndex(last.State, 1).State);
    }
}
