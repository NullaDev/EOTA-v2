using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Protocols;

namespace Eota.Content.Compiler.Tests;

public sealed class FatigueEffectTests
{
    [Theory]
    [InlineData(DeckExhaustionPolicy.IncreasingDamage, 24)]
    [InlineData(DeckExhaustionPolicy.InstantDeath, 0)]
    public void CardEffectDrawsUseTheSameFatigueCounterAsTurnDraws(DeckExhaustionPolicy policy, long health)
    {
        var state = EffectRuntimeTests.Create([EffectRuntimeTests.Minion("FATIGUE", """
            [{"id":"draw","trigger":{"kind":"selfEntered"},"body":{"kind":"draw","target":{"kind":"friendlyHero"},"count":3}}]
            """)]);
        var protocol = CompiledGameProtocol.Compile(state.Protocol.Definition with { DeckExhaustionPolicy = policy });
        state = state with { Protocol = protocol, Manifest = state.Manifest with { ProtocolHash = protocol.Hash } };
        var result = EffectRuntimeTests.Resolve(EffectRuntimeTests.Plan(state, PlayerId.One, "TEST-P6-FATIGUE", 0));
        Assert.Equal(health, result.State.Players[0].HeroHealth); Assert.Equal(3, result.State.Players[0].FatigueCount);
        Assert.Equal(30, result.State.Players[1].HeroHealth); Assert.Equal(0, result.State.Players[1].FatigueCount);
        Assert.Equal(policy == DeckExhaustionPolicy.InstantDeath ? MatchOutcome.PlayerTwoWon : MatchOutcome.None, result.State.Outcome);
    }
}
