using System.Collections.Immutable;
using Eota.Kernel.Commands;
using Eota.Kernel.Content;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Protocols;
using Eota.Kernel.Resolution;

namespace Eota.Kernel.Tests;

public sealed partial class FrameResolverTests
{
    [Theory]
    [InlineData(MovementConflictPolicy.CenterFirst, false, 2)]
    [InlineData(MovementConflictPolicy.CenterFirst, true, 3)]
    [InlineData(MovementConflictPolicy.OutsideFirst, false, 0)]
    [InlineData(MovementConflictPolicy.OutsideFirst, true, 5)]
    [InlineData(MovementConflictPolicy.AllFail, false, -1)]
    [InlineData(MovementConflictPolicy.AllFail, true, -1)]
    public void MovementConflictUsesConfiguredSourcePriorityAndIsOrderIndependent(
        MovementConflictPolicy policy, bool rightSide, int winningSource)
    {
        var state = CreateMatch(protocolDefinition: GameProtocolDefinition.DefaultV0 with { MovementConflictPolicy = policy });
        var outer = rightSide ? 5 : 0;
        var inner = rightSide ? 3 : 2;
        var target = rightSide ? 4 : 1;
        // Allocate the outer entity first: stable ID must not determine the winner.
        foreach (var lane in new[] { outer, inner })
        {
            state = PutMinion(state, PlayerId.One, 1, 2, 2, new LaneId(lane),
                [new MinionKeywordDefinition(MinionKeywordKind.Skirmisher)]);
            state = PutMinion(state, PlayerId.Two, 1, 2, 2, new LaneId(lane));
        }

        state = state with { Stage = MatchStage.Movement };
        var intents = new MovementWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId)
            .Evaluate(FrameSnapshot.Create(state));
        Assert.Equal(2, intents.Length);
        var result = FrameResolver.Resolve(state, intents);
        var reversed = FrameResolver.Resolve(state with
        {
            Entities = state.Entities.Reverse().ToImmutableArray(),
            Players = state.Players.Reverse().ToImmutableArray(),
            Lanes = state.Lanes.Reverse().ToImmutableArray()
        }, intents.Reverse());

        Assert.Equal(FrameHashes(result), FrameHashes(reversed));
        Assert.Equal(state.RuleRng, result.State.RuleRng);
        Assert.Equal(2, result.Receipts.Receipts.Length);
        if (winningSource < 0)
        {
            Assert.All(result.Receipts.Receipts, receipt => Assert.Equal(IntentReceiptStatus.Rejected, receipt.Status));
            Assert.Empty(result.Events.Events);
        }
        else
        {
            var expected = state.Entities.Single(value => value.ControllerId == PlayerId.One && value.LaneId.Value == winningSource);
            Assert.Equal(expected.Id, result.State.Lanes.Single(value => value.Id.Value == target).PlayerOne.MinionEntityId);
            Assert.Equal(expected.Id, Assert.Single(result.Receipts.Receipts.Where(value => value.Status == IntentReceiptStatus.Applied)).EntityId);
            Assert.Single(result.Receipts.Receipts.Where(value => value.Status == IntentReceiptStatus.Rejected));
            Assert.Equal(expected.Id, Assert.Single(result.Events.Events).EntityId);
        }
    }

    [Theory]
    [InlineData(MovementDirectionPreference.OutwardFirst, 2, 1)]
    [InlineData(MovementDirectionPreference.InwardFirst, 2, 3)]
    [InlineData(MovementDirectionPreference.OutwardFirst, 3, 4)]
    [InlineData(MovementDirectionPreference.InwardFirst, 3, 2)]
    public void MovementDirectionPreferenceChoosesBetweenEligibleAdjacentLanes(
        MovementDirectionPreference preference, int source, int expected)
    {
        var state = CreateMatch(protocolDefinition: GameProtocolDefinition.DefaultV0 with { MovementDirectionPreference = preference });
        state = PutMinion(state, PlayerId.One, 1, 2, 2, new LaneId(source),
            [new MinionKeywordDefinition(MinionKeywordKind.Pursuit)]);
        state = PutMinion(state, PlayerId.Two, 1, 2, 2, new LaneId(source - 1));
        state = PutMinion(state, PlayerId.Two, 1, 2, 2, new LaneId(source + 1));
        state = state with { Stage = MatchStage.Movement };

        var result = MatchKernel.Step(state, [new MovementWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId)]);

        Assert.Equal(expected, result.State.Entities.Single(value => value.ControllerId == PlayerId.One).LaneId.Value);
        Assert.Equal(state.RuleRng, result.State.RuleRng);
    }

    [Fact]
    public void InvalidHighPriorityMovementCannotDisplaceAValidCandidate()
    {
        var state = PutMinion(CreateMatch(), PlayerId.One, 1, 2, 2, new LaneId(0));
        var minion = Assert.Single(state.Entities);
        var result = FrameResolver.Resolve(state,
        [
            new MoveMinionIntent(new IntentId(1), new EntityId(999), PlayerId.One, new LaneId(2), new LaneId(1)),
            new MoveMinionIntent(new IntentId(2), minion.Id, PlayerId.One, new LaneId(0), new LaneId(1))
        ]);

        Assert.Equal(new LaneId(1), Assert.Single(result.State.Entities).LaneId);
        Assert.Equal(IntentReceiptStatus.Rejected, result.Receipts.Receipts[0].Status);
        Assert.Equal(IntentReceiptStatus.Applied, result.Receipts.Receipts[1].Status);
    }

    [Fact]
    public void MovingOutDoesNotOpenASnapshotOccupiedSlotForAnotherMover()
    {
        var state = PutMinion(CreateMatch(), PlayerId.One, 1, 2, 2, new LaneId(0));
        state = PutMinion(state, PlayerId.One, 1, 2, 2, new LaneId(1));
        var outer = state.Entities.Single(value => value.LaneId.Value == 0);
        var inner = state.Entities.Single(value => value.LaneId.Value == 1);
        var result = FrameResolver.Resolve(state,
        [
            new MoveMinionIntent(new IntentId(1), inner.Id, PlayerId.One, new LaneId(1), new LaneId(2)),
            new MoveMinionIntent(new IntentId(2), outer.Id, PlayerId.One, new LaneId(0), new LaneId(1))
        ]);

        Assert.Equal(new LaneId(0), result.State.Entities.Single(value => value.Id == outer.Id).LaneId);
        Assert.Equal(new LaneId(2), result.State.Entities.Single(value => value.Id == inner.Id).LaneId);
        Assert.Equal("move-slot-unavailable", result.Receipts.Receipts.Single(value => value.IntentId.Value == 2).DetailCode);
    }

    [Fact]
    public void RepeatedMovesOfOneEntityCannotOccupyTwoSlots()
    {
        var state = PutMinion(CreateMatch(), PlayerId.One, 1, 2, 2, new LaneId(2));
        var entity = Assert.Single(state.Entities);
        AtomicIntent[] intents =
        [
            new MoveMinionIntent(new IntentId(1), entity.Id, PlayerId.One, new LaneId(2), new LaneId(3)),
            new MoveMinionIntent(new IntentId(2), entity.Id, PlayerId.One, new LaneId(2), new LaneId(1))
        ];

        var result = FrameResolver.Resolve(state, intents);
        var reversed = FrameResolver.Resolve(state, intents.Reverse());

        Assert.Equal(FrameHashes(result), FrameHashes(reversed));
        Assert.Single(result.State.Lanes.Where(value => value.PlayerOne.MinionEntityId == entity.Id));
        Assert.Single(result.Receipts.Receipts.Where(value => value.Status == IntentReceiptStatus.Applied));
        Assert.Single(result.Receipts.Receipts.Where(value => value.Status == IntentReceiptStatus.Rejected));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void FieldReplacementRequiresIncomingReplaceOrOccupantReplaceable(
        bool incomingReplace, bool occupantReplaceable, bool incomingPermanent)
    {
        ImmutableArray<FieldKeywordKind> incomingKeywords = incomingReplace ? [FieldKeywordKind.Replace] : [];
        ImmutableArray<FieldKeywordKind> occupyingKeywords = occupantReplaceable ? [FieldKeywordKind.Replaceable] : [];
        var state = CreateMatch(
            finiteFieldKeywords: incomingPermanent ? occupyingKeywords : incomingKeywords,
            permanentFieldKeywords: incomingPermanent ? incomingKeywords : occupyingKeywords);
        var incomingId = incomingPermanent ? PermanentFieldId : FiniteFieldId;
        var occupyingId = incomingPermanent ? FiniteFieldId : PermanentFieldId;
        state = PutField(state, PlayerId.One, occupyingId);
        var oldField = Assert.IsType<FieldEntityState>(Assert.Single(state.Entities));
        var card = state.CardInstances.First(value => value.OwnerId == PlayerId.One && value.CurrentPrototypeId == incomingId);
        var planned = MatchCommandProcessor.Accept(state, new PlanCardCommand(PlayerId.One, 0, card.Id, new LaneId(0)));

        if (!incomingReplace && !occupantReplaceable)
        {
            Assert.False(planned.IsAccepted);
            Assert.Equal(CommandRejectionReason.BattlefieldSlotUnavailable, planned.Receipt.RejectionReason);
            Assert.Equal(MatchStateHasher.Compute(state), MatchStateHasher.Compute(planned.State));
            return;
        }

        Assert.True(planned.IsAccepted);
        var ready = planned.State with { Stage = MatchStage.Deployment };
        var result = MatchKernel.Step(ready, [new PlannedDeploymentWorkItem(ready.NextWorkItemId, ready.NextFrameId, ready.NextIntentId)]);
        var field = Assert.IsType<FieldEntityState>(Assert.Single(result.State.Entities));
        Assert.NotEqual(oldField.Id, field.Id);
        Assert.Equal(card.Id, field.CardInstanceId);
        Assert.Equal<FieldKeywordKind>(incomingKeywords, field.Keywords);
        Assert.Equal(field.Id, result.State.Lanes[0].PlayerOne.FieldEntityId);
        Assert.Equal(CardZone.Discard, GetCard(result.State, oldField.CardInstanceId).Zone);
        Assert.Equal(CardZone.Battlefield, GetCard(result.State, card.Id).Zone);
        Assert.Equal(EntityRemovalReason.Replace, Assert.Single(result.State.Tombstones).Reason);
        Assert.Single(result.Events.Events.Where(value => value.Kind == DomainEventKind.EntityLeft && value.EntityId == oldField.Id));
        Assert.Single(result.Events.Events.Where(value => value.Kind == DomainEventKind.EntityEntered && value.EntityId == field.Id));
        Assert.DoesNotContain(result.Events.Events, value => value.Kind == DomainEventKind.EntityDied);
        Assert.Equal(IntentReceiptStatus.Applied, Assert.Single(result.Receipts.Receipts).Status);
    }

    [Fact]
    public void FieldReplaceAndReplaceableAreNotInterchangeable()
    {
        var state = CreateMatch(finiteFieldKeywords: [FieldKeywordKind.Replaceable], permanentFieldKeywords: [FieldKeywordKind.Replace]);
        state = PutField(state, PlayerId.One, PermanentFieldId);
        var card = state.CardInstances.First(value => value.OwnerId == PlayerId.One && value.CurrentPrototypeId == FiniteFieldId);

        var result = MatchCommandProcessor.Accept(state, new PlanCardCommand(PlayerId.One, 0, card.Id, new LaneId(0)));

        Assert.False(result.IsAccepted);
        Assert.Equal(CommandRejectionReason.BattlefieldSlotUnavailable, result.Receipt.RejectionReason);
    }

    [Fact]
    public void FieldKeywordsAreIncludedInTheAuthoritativeStateHash()
    {
        var state = PutField(CreateMatch(), PlayerId.One, PermanentFieldId);
        var field = Assert.IsType<FieldEntityState>(Assert.Single(state.Entities));
        var changed = state with { Entities = [field with { Keywords = [FieldKeywordKind.Replaceable] }] };

        Assert.NotEqual(MatchStateHasher.Compute(state), MatchStateHasher.Compute(changed));
    }
}
