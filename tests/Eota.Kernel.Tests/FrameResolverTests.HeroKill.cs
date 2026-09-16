using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Kernel.Tests;

public sealed partial class FrameResolverTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeroKillWinsOverHealingAndAllIntentsCommitBeforeVictory(bool both)
    {
        var state = CreateMatch();
        AtomicIntent[] intents =
        [
            new KillHeroIntent(new IntentId(1), PlayerId.Two),
            new HealHeroIntent(new IntentId(2), PlayerId.Two, 100),
            new ModifyHeroMaximumHealthIntent(new IntentId(3), PlayerId.Two, 10),
            new KillHeroIntent(new IntentId(4), PlayerId.Two),
            both ? new KillHeroIntent(new IntentId(5), PlayerId.One) : new DamageHeroIntent(new IntentId(5), PlayerId.One, 1)
        ];
        var frame = FrameResolver.Resolve(state, intents);
        Assert.Equal(MatchStatus.Finished, frame.State.Status);
        Assert.Equal(both ? MatchOutcome.Draw : MatchOutcome.PlayerOneWon, frame.State.Outcome);
        Assert.Equal(0, frame.State.Players[1].HeroHealth);
        Assert.Equal(state.Players[1].HeroMaximumHealth + 10, frame.State.Players[1].HeroMaximumHealth);
        Assert.Equal(both ? 0 : state.Players[0].HeroHealth - 1, frame.State.Players[0].HeroHealth);
        Assert.Equal(2 + (both ? 1 : 0), frame.Receipts.Receipts.Count(receipt => receipt.DetailCode == "kill" && receipt.Status == IntentReceiptStatus.Applied));
        Assert.Single(frame.Events.Events, fact => fact.Kind == DomainEventKind.MatchEnded);
        foreach (var permutation in EffectPermutations(intents))
        {
            var actual = FrameResolver.Resolve(state, permutation);
            Assert.Equal(frame.AfterStateHash, actual.AfterStateHash);
            Assert.Equal(frame.Receipts.Hash, actual.Receipts.Hash);
            Assert.Equal(frame.Events.Hash, actual.Events.Hash);
        }
    }
}
