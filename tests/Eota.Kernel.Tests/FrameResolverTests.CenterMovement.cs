using System.Collections.Immutable;
using Eota.Kernel.Content;
using Eota.Kernel.Determinism;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Protocols;
using Eota.Kernel.Resolution;

namespace Eota.Kernel.Tests;

public sealed partial class FrameResolverTests
{
    [Theory]
    [InlineData(0UL, 1, MovementDirectionPreference.OutwardFirst)]
    [InlineData(1UL, 3, MovementDirectionPreference.OutwardFirst)]
    [InlineData(1UL, 3, MovementDirectionPreference.InwardFirst)]
    public void OddCenterUsesRuleRngWhenBothEligibleNeighborsAreOutward(
        ulong seed, int expectedLane, MovementDirectionPreference preference)
    {
        var state = CreateCenterMovementMatch(seed, preference);
        state = PutMinion(state, PlayerId.One, 1, 2, 2, new LaneId(2),
            [new MinionKeywordDefinition(MinionKeywordKind.Pursuit)]);
        state = PutMinion(state, PlayerId.Two, 1, 2, 2, new LaneId(1));
        state = PutMinion(state, PlayerId.Two, 1, 2, 2, new LaneId(3));
        var snapshot = FrameSnapshot.Create(state);
        var work = new MovementWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId);
        var intents = work.Evaluate(snapshot);
        var plan = FrameResolver.Plan(state, intents);

        Assert.Equal(GlobalRuleRng.Create(seed), state.RuleRng);
        Assert.Equal(snapshot.Hash, MatchStateHasher.Compute(state));
        Assert.Equal(snapshot.Hash, plan.SnapshotHash);
        var transition = MatchKernel.Step(state, [work]);
        var repeated = FrameResolver.Resolve(state, work.Evaluate(snapshot));
        var replayedPlan = FrameResolver.Resolve(state, plan.Groups.SelectMany(value => value.Intents));
        var selected = Assert.Single(transition.Plan.MovementRandomChoices);

        Assert.Equal(expectedLane, transition.State.Entities.Single(value => value.ControllerId == PlayerId.One).LaneId.Value);
        Assert.Equal(new LaneId(expectedLane), selected.SelectedLaneId);
        Assert.Equal<LaneId>([new LaneId(1), new LaneId(3)], selected.Candidates);
        Assert.Equal(state.NextFrameId, selected.FrameId);
        Assert.Equal(state.NextIntentId, selected.IntentId);
        Assert.Equal(0UL, selected.SampleCountBefore);
        Assert.Equal(1UL, selected.SamplesConsumed);
        Assert.Equal(1UL, transition.State.RuleRng.SampleCount);
        Assert.Equal(transition.State.RuleRng, repeated.State.RuleRng);
        Assert.Equal(transition.Receipts.Hash, repeated.Receipts.Hash);
        Assert.Equal(transition.Events.Hash, repeated.Events.Hash);
        Assert.Equal(FrameHashes(repeated), FrameHashes(replayedPlan));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void OddCenterWithZeroOrOneEligibleDirectionConsumesNoRng(int enemyCount)
    {
        var state = PutMinion(CreateCenterMovementMatch(1), PlayerId.One, 1, 2, 2, new LaneId(2),
            [new MinionKeywordDefinition(MinionKeywordKind.Pursuit)]);
        if (enemyCount == 1)
        {
            state = PutMinion(state, PlayerId.Two, 1, 2, 2, new LaneId(1));
        }

        var result = MatchKernel.Step(state, [new MovementWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId)]);

        Assert.Equal(state.RuleRng, result.State.RuleRng);
        Assert.Empty(result.Plan.MovementRandomChoices);
        Assert.Equal(enemyCount == 0 ? 2 : 1, result.State.Entities.Single(value => value.ControllerId == PlayerId.One).LaneId.Value);
    }

    [Fact]
    public void OccupiedOwnSlotIsExcludedBeforeCenterDirectionRandomization()
    {
        var state = PutMinion(CreateCenterMovementMatch(0), PlayerId.One, 1, 2, 2, new LaneId(2),
            [new MinionKeywordDefinition(MinionKeywordKind.Pursuit)]);
        var moverId = Assert.Single(state.Entities).Id;
        state = PutMinion(state, PlayerId.One, 1, 2, 2, new LaneId(1));
        state = PutMinion(state, PlayerId.Two, 1, 2, 2, new LaneId(1));
        state = PutMinion(state, PlayerId.Two, 1, 2, 2, new LaneId(3));

        var result = MatchKernel.Step(state, [new MovementWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId)]);

        Assert.Equal(new LaneId(3), result.State.Entities.Single(value => value.Id == moverId).LaneId);
        Assert.Equal(state.RuleRng, result.State.RuleRng);
        Assert.Empty(result.Plan.MovementRandomChoices);
    }

    [Fact]
    public void BothPlayersCenterChoicesShareOneStableRngStream()
    {
        var state = PutMinion(CreateCenterMovementMatch(1), PlayerId.Two, 1, 2, 2, new LaneId(2),
            [new MinionKeywordDefinition(MinionKeywordKind.Skirmisher)]);
        state = PutMinion(state, PlayerId.One, 1, 2, 2, new LaneId(2),
            [new MinionKeywordDefinition(MinionKeywordKind.Skirmisher)]);
        var work = new MovementWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId);
        var reordered = state with
        {
            Players = state.Players.Reverse().ToImmutableArray(),
            Entities = state.Entities.Reverse().ToImmutableArray(),
            Lanes = state.Lanes.Reverse().ToImmutableArray()
        };
        var first = FrameResolver.Resolve(state, work.Evaluate(FrameSnapshot.Create(state)));
        var second = FrameResolver.Resolve(reordered, work.Evaluate(FrameSnapshot.Create(reordered)).Reverse());

        Assert.Equal(FrameHashes(first), FrameHashes(second));
        Assert.Equal(2UL, first.State.RuleRng.SampleCount);
        Assert.Equal(new LaneId(3), first.State.Entities.Single(value => value.ControllerId == PlayerId.Two).LaneId);
        Assert.Equal(new LaneId(1), first.State.Entities.Single(value => value.ControllerId == PlayerId.One).LaneId);
        Assert.Equal(new ulong[] { 0, 1 }, first.Plan.MovementRandomChoices.Select(value => value.SampleCountBefore));
        Assert.Equal(new ulong[] { 1, 2 }, first.Plan.MovementRandomChoices.Select(value => value.EntityId.Value));
    }

    [Fact]
    public void RandomlyChosenDirectionStillParticipatesInSlotConflictWithoutRetrying()
    {
        var state = CreateCenterMovementMatch(0);
        foreach (var lane in new[] { 0, 2 })
        {
            state = PutMinion(state, PlayerId.One, 1, 2, 2, new LaneId(lane),
                [new MinionKeywordDefinition(MinionKeywordKind.Skirmisher)]);
            state = PutMinion(state, PlayerId.Two, 1, 2, 2, new LaneId(lane));
        }

        var result = MatchKernel.Step(state, [new MovementWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId)]);

        Assert.All(result.Receipts.Receipts, value => Assert.Equal(IntentReceiptStatus.Rejected, value.Status));
        Assert.Equal(1UL, result.State.RuleRng.SampleCount);
        Assert.Equal(new LaneId(1), Assert.Single(result.Plan.MovementRandomChoices).SelectedLaneId);
        Assert.Equal(state.Entities.Select(value => value.LaneId), result.State.Entities.Select(value => value.LaneId));
    }

    [Fact]
    public void FrameFailureRollsBackMovementRandomSamplesTogetherWithMovement()
    {
        var state = PutMinion(CreateCenterMovementMatch(1), PlayerId.One, 1, 2, 2, new LaneId(2),
            [new MinionKeywordDefinition(MinionKeywordKind.Skirmisher)]);
        state = PutMinion(state, PlayerId.Two, 1, 2, 2, new LaneId(2));
        var intents = new MovementWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId)
            .Evaluate(FrameSnapshot.Create(state));
        var result = FrameResolver.Resolve(state, intents.Add(
            new ModifyHeroMaximumHealthIntent(new IntentId(99), PlayerId.One, long.MaxValue)));

        Assert.Equal(MatchStatus.Failed, result.State.Status);
        Assert.Equal(state.RuleRng, result.State.RuleRng);
        Assert.All(result.State.Entities, value => Assert.Equal(new LaneId(2), value.LaneId));
        Assert.All(result.Receipts.Receipts, value => Assert.Equal(IntentReceiptStatus.Error, value.Status));
    }

    [Fact]
    public void InvalidAlternativeTargetDoesNotConsumeRngOrMoveTheEntity()
    {
        var state = PutMinion(CreateCenterMovementMatch(1), PlayerId.One, 1, 2, 2, new LaneId(2));
        var entity = Assert.Single(state.Entities);
        var result = FrameResolver.Resolve(state,
        [
            new MoveMinionIntent(new IntentId(1), entity.Id, PlayerId.One, new LaneId(2), new LaneId(1), new LaneId(4))
        ]);

        Assert.Equal("invalid-movement-random-choice", Assert.Single(result.Receipts.Receipts).DetailCode);
        Assert.Equal(new LaneId(2), Assert.Single(result.State.Entities).LaneId);
        Assert.Equal(state.RuleRng, result.State.RuleRng);
        Assert.Empty(result.Plan.MovementRandomChoices);
    }

    private static MatchState CreateCenterMovementMatch(
        ulong seed, MovementDirectionPreference preference = MovementDirectionPreference.OutwardFirst) =>
        CreateMatch(protocolDefinition: GameProtocolDefinition.DefaultV0 with
        {
            LaneCount = 5,
            MovementConflictPolicy = MovementConflictPolicy.AllFail,
            MovementDirectionPreference = preference
        }) with
        { Stage = MatchStage.Movement, RuleRng = GlobalRuleRng.Create(seed) };
}
