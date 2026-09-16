using Eota.Kernel.Effects;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Kernel.Tests;

public sealed partial class FrameResolverTests
{
    [Fact]
    public void CenterMovementDoesNotSampleWhenSummonsDefeatBothDirections()
    {
        var state = PutMinion(CreateMatch(protocolDefinition: Eota.Kernel.Protocols.GameProtocolDefinition.DefaultV0 with
        { LaneCount = 3, MovementConflictPolicy = Eota.Kernel.Protocols.MovementConflictPolicy.AllFail }), PlayerId.One, 1, 3, 3,
            new LaneId(1), [new Eota.Kernel.Content.MinionKeywordDefinition(Eota.Kernel.Content.MinionKeywordKind.Skirmisher)]);
        AtomicIntent[] intents = [
            new MoveMinionIntent(new IntentId(1), state.Entities[0].Id, PlayerId.One, new LaneId(1), new LaneId(0), new LaneId(2)),
            new SummonIntent(new IntentId(2), PlayerId.One, PlayerId.One, new LaneId(0), MinionId, BattlefieldSlotKind.Minion),
            new SummonIntent(new IntentId(3), PlayerId.One, PlayerId.One, new LaneId(2), MinionId, BattlefieldSlotKind.Minion) ];
        var frame = FrameResolver.Resolve(state, intents);
        Assert.Empty(frame.Plan.MovementRandomChoices);
        Assert.Equal(state.RuleRng, frame.State.RuleRng);
        Assert.Equal(IntentReceiptStatus.Rejected, frame.Receipts.Receipts[0].Status);
        Assert.Equal(3, frame.State.Entities.Length);
        Assert.Equal(frame.AfterStateHash, FrameResolver.Resolve(state, intents.Reverse()).AfterStateHash);
    }

    [Fact]
    public void FriendlySummonWinsOverHostileSummonAndMovementWithoutSampling()
    {
        var state = PutMinion(CreateMatch(), PlayerId.One, 2, 3, 3);
        var original = Assert.Single(state.Entities);
        AtomicIntent[] intents = [
            new SummonIntent(new IntentId(3), PlayerId.Two, PlayerId.One, new LaneId(1), MinionId, BattlefieldSlotKind.Minion),
            new MoveMinionIntent(new IntentId(2), original.Id, PlayerId.One, new LaneId(0), new LaneId(1)),
            new SummonIntent(new IntentId(1), PlayerId.One, PlayerId.One, new LaneId(1), MinionId, BattlefieldSlotKind.Minion) ];
        var expected = FrameResolver.Resolve(state, intents);
        Assert.Equal(state.RuleRng, expected.State.RuleRng);
        Assert.Equal(IntentReceiptStatus.Applied, expected.Receipts.Receipts[0].Status);
        Assert.Equal(2, expected.State.Entities.Length);
        Assert.Equal(new LaneId(0), expected.State.Entities.Single(value => value.Id == original.Id).LaneId);
        Assert.Equal(state.NextCardInstanceId.Value + 1, expected.State.NextCardInstanceId.Value);
        foreach (var order in EffectPermutations(intents))
        {
            var actual = FrameResolver.Resolve(state, order);
            Assert.Equal(expected.AfterStateHash, actual.AfterStateHash);
            Assert.Equal(expected.Receipts.Hash, actual.Receipts.Hash);
            Assert.Equal(expected.Events.Hash, actual.Events.Hash);
        }
    }

    [Fact]
    public void EqualSummonsUseOneRecordedChoiceAndOccupiedSlotsStayIneligibleAfterDeath()
    {
        var state = CreateMatch();
        AtomicIntent[] summons = [
            new SummonIntent(new IntentId(2), PlayerId.One, PlayerId.One, new LaneId(0), MinionId, BattlefieldSlotKind.Minion),
            new SummonIntent(new IntentId(1), PlayerId.One, PlayerId.One, new LaneId(0), MinionId, BattlefieldSlotKind.Minion) ];
        var frame = FrameResolver.Resolve(state, summons);
        var choice = Assert.Single(frame.Plan.ConflictRandomChoices);
        Assert.Equal(1UL, choice.SamplesConsumed);
        Assert.Single(frame.State.Entities);
        Assert.Equal(frame.AfterStateHash, FrameResolver.Resolve(state, summons.Reverse()).AfterStateHash);
        state = PutMinion(state, PlayerId.One, 1, 1, 1);
        var denied = FrameResolver.Resolve(state, summons.Append(new KillMinionIntent(new IntentId(3), state.Entities[0].Id)));
        Assert.Empty(denied.State.Entities);
        Assert.Equal(state.RuleRng, denied.State.RuleRng);
        Assert.Empty(denied.Plan.ConflictRandomChoices);
        Assert.All(denied.Receipts.Receipts.Take(2), value => Assert.Equal("summon-slot-occupied", value.DetailCode));
    }
}
