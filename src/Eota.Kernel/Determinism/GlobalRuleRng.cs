using System.Numerics;

namespace Eota.Kernel.Determinism;

public readonly record struct GlobalRuleRngState(
    ulong S0,
    ulong S1,
    ulong S2,
    ulong S3,
    ulong SampleCount);

public readonly record struct RngSample(GlobalRuleRngState State, ulong Value);

public readonly record struct RngIndexSample(
    GlobalRuleRngState State,
    int Index,
    ulong SamplesConsumed);

public static class GlobalRuleRng
{
    public const string AlgorithmVersion = "splitmix64+xoshiro256ss/v1";
    public const int CallSchemaVersion = 4;

    public static GlobalRuleRngState Create(ulong seed)
    {
        var splitMixState = seed;
        return new GlobalRuleRngState(
            NextSplitMix64(ref splitMixState),
            NextSplitMix64(ref splitMixState),
            NextSplitMix64(ref splitMixState),
            NextSplitMix64(ref splitMixState),
            0);
    }

    public static RngSample NextUInt64(GlobalRuleRngState state)
    {
        unchecked
        {
            var result = BitOperations.RotateLeft(state.S1 * 5, 7) * 9;
            var t = state.S1 << 17;

            var s2 = state.S2 ^ state.S0;
            var s3 = state.S3 ^ state.S1;
            var s1 = state.S1 ^ s2;
            var s0 = state.S0 ^ s3;
            s2 ^= t;
            s3 = BitOperations.RotateLeft(s3, 45);

            return new RngSample(
                new GlobalRuleRngState(s0, s1, s2, s3, checked(state.SampleCount + 1)),
                result);
        }
    }

    public static RngIndexSample NextIndex(GlobalRuleRngState state, int exclusiveUpperBound)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exclusiveUpperBound);
        if (exclusiveUpperBound == 1)
        {
            return new RngIndexSample(state, 0, 0);
        }

        var bound = (ulong)exclusiveUpperBound;
        var threshold = unchecked(0UL - bound) % bound;
        var initialSampleCount = state.SampleCount;

        while (true)
        {
            var sample = NextUInt64(state);
            state = sample.State;
            if (sample.Value >= threshold)
            {
                return new RngIndexSample(
                    state,
                    (int)(sample.Value % bound),
                    state.SampleCount - initialSampleCount);
            }
        }
    }

    public static GlobalRuleRngState Shuffle<T>(Span<T> values, GlobalRuleRngState state)
    {
        for (var index = values.Length - 1; index > 0; index--)
        {
            var sample = NextIndex(state, index + 1);
            state = sample.State;
            (values[index], values[sample.Index]) = (values[sample.Index], values[index]);
        }

        return state;
    }

    private static ulong NextSplitMix64(ref ulong state)
    {
        unchecked
        {
            state += 0x9E3779B97F4A7C15UL;
            var value = state;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }
}
