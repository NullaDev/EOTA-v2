using Eota.Kernel.Effects;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Kernel.Tests;

public sealed partial class FrameResolverTests
{
    [Fact]
    public void CompetingReturnsUseOneFreeHandSlotAndKillOnlyTheCapacityLoser()
    {
        var state = PutMinion(CreateMatch(), PlayerId.One, 2, 3, 3, new LaneId(0));
        state = PutMinion(state, PlayerId.One, 4, 5, 5, new LaneId(1));
        state = FrameResolver.Resolve(state, [new HandCardIntent(state.NextIntentId, PlayerId.One, HandRequestKind.Generate,
            new DrawAllocationKey(1, "fill", 0), MinionId)]).State;
        Assert.Equal(1, state.Protocol.Definition.HandLimit - state.Players[0].Hand.Length);
        var intents = state.Entities.Select((entity, index) => (AtomicIntent)new LifecycleEffectIntent(new IntentId(state.NextIntentId.Value + (ulong)index),
            new EffectTarget(EffectTargetType.Minion, entity.Id.Value), PlayerId.One, LifecycleOperation.Return)).ToArray();
        var frame = FrameResolver.Resolve(state, intents);
        Assert.Empty(frame.State.Entities);
        Assert.Equal(state.Protocol.Definition.HandLimit, frame.State.Players[0].Hand.Length);
        Assert.Equal(state.NextCardInstanceId.Value + 1, frame.State.NextCardInstanceId.Value);
        var returned = Assert.Single(frame.Receipts.Receipts, receipt => receipt.Status == IntentReceiptStatus.Applied);
        var failed = Assert.Single(frame.Receipts.Receipts, receipt => receipt.Status == IntentReceiptStatus.Rejected);
        Assert.Equal(EntityRemovalReason.Return, frame.State.Tombstones.Single(value => value.EntityId == returned.EntityId).Reason);
        Assert.Equal(EntityRemovalReason.Death, frame.State.Tombstones.Single(value => value.EntityId == failed.EntityId).Reason);
        Assert.Single(frame.Events.Events, fact => fact.Kind == DomainEventKind.EntityDied && fact.EntityId == failed.EntityId);
        Assert.Single(frame.Events.Events, fact => fact.Kind == DomainEventKind.CardReturned && fact.EntityId == returned.EntityId);
        Assert.Single(failed.Outputs!.Removed); Assert.Empty(failed.Outputs.EnteredHand);
        Assert.Equal(1UL, Assert.Single(frame.Plan.ConflictRandomChoices).SamplesConsumed);
        var reversed = FrameResolver.Resolve(state, intents.Reverse());
        Assert.Equal(frame.AfterStateHash, reversed.AfterStateHash);
        Assert.Equal(frame.Receipts.Hash, reversed.Receipts.Hash);
        Assert.Equal(frame.Events.Hash, reversed.Events.Hash);
        Assert.Equal(frame.State.RuleRng, reversed.State.RuleRng);
    }

    [Fact]
    public void SameBatchCapacityPrioritizesReturnThenGenerateThenDrawWithExactOutputs()
    {
        var state = PutMinion(CreateMatch(openingHandSize: 9, handLimit: 10), PlayerId.One, 4, 4, 4);
        var entity = state.Entities[0];
        var deckTop = state.Players[0].Deck[0];
        AtomicIntent[] intents = [
            new HandCardIntent(new IntentId(1), PlayerId.One, HandRequestKind.Draw, new DrawAllocationKey(1, "draw", 0)),
            new HandCardIntent(new IntentId(2), PlayerId.One, HandRequestKind.Generate, new DrawAllocationKey(1, "generate", 0), MinionId),
            new LifecycleEffectIntent(new IntentId(3), new EffectTarget(EffectTargetType.Minion, entity.Id.Value), PlayerId.One, LifecycleOperation.Return) ];
        var frame = FrameResolver.Resolve(state, intents);
        Assert.Equal(10, frame.State.Players[0].Hand.Length);
        Assert.Empty(frame.State.Entities);
        Assert.Empty(frame.State.Players[0].Deck);
        Assert.Equal(state.RuleRng, frame.State.RuleRng);
        var draw = frame.Receipts.Receipts[0];
        Assert.Equal(IntentReceiptStatus.Rejected, draw.Status);
        Assert.Equal(deckTop, Assert.Single(draw.Outputs!.RemovedFromDeck));
        Assert.Equal(deckTop, Assert.Single(draw.Outputs.Burned));
        Assert.Contains(deckTop, frame.State.Players[0].Removed);
        Assert.NotEqual(entity.CardInstanceId, Assert.Single(frame.Receipts.Receipts[2].Outputs!.EnteredHand));
        Assert.All(frame.Receipts.Receipts.Skip(1), value => Assert.Equal(IntentReceiptStatus.Applied, value.Status));
        foreach (var order in EffectPermutations(intents))
        {
            var actual = FrameResolver.Resolve(state, order);
            Assert.Equal(frame.AfterStateHash, actual.AfterStateHash);
            Assert.Equal(frame.Receipts.Hash, actual.Receipts.Hash);
            Assert.Equal(frame.Events.Hash, actual.Events.Hash);
        }
    }

    [Fact]
    public void EqualPriorityOverflowSamplesOnlyWhenAnAvailableSeatMustChoose()
    {
        var state = CreateMatch(openingHandSize: 9, handLimit: 10);
        AtomicIntent[] intents = [
            new HandCardIntent(new IntentId(1), PlayerId.One, HandRequestKind.Generate, new DrawAllocationKey(1, "generate", 0), MinionId),
            new HandCardIntent(new IntentId(2), PlayerId.One, HandRequestKind.Generate, new DrawAllocationKey(1, "generate", 1), MinionId) ];
        var frame = FrameResolver.Resolve(state, intents);
        Assert.Equal(1UL, Assert.Single(frame.Plan.ConflictRandomChoices).SamplesConsumed);
        Assert.Equal(state.NextCardInstanceId.Value + 1, frame.State.NextCardInstanceId.Value);
        Assert.Single(frame.Receipts.Receipts.Single(value => value.Status == IntentReceiptStatus.Rejected).Outputs!.NotCreated);
        var full = FrameResolver.Resolve(frame.State, intents.Select((intent, i) => ((HandCardIntent)intent) with { Id = new IntentId(frame.State.NextIntentId.Value + (ulong)i) }));
        Assert.Empty(full.Plan.ConflictRandomChoices);
        Assert.Equal(frame.State.NextCardInstanceId, full.State.NextCardInstanceId);
    }

    [Fact]
    public void DrawAllocationUsesSourceKeyAndDeckOrderBeforeCapacityLottery()
    {
        var state = CreateMatch(openingHandSize: 6, handLimit: 7);
        AtomicIntent[] intents = [
            new HandCardIntent(new IntentId(1), PlayerId.One, HandRequestKind.Draw, new DrawAllocationKey(20, "draw", 0)),
            new HandCardIntent(new IntentId(2), PlayerId.One, HandRequestKind.Draw, new DrawAllocationKey(10, "draw", 0)) ];
        var frame = FrameResolver.Resolve(state, intents);
        Assert.Equal(state.Players[0].Deck[0], Assert.Single(frame.Receipts.Receipts[1].Outputs!.RemovedFromDeck));
        Assert.Equal(state.Players[0].Deck[1], Assert.Single(frame.Receipts.Receipts[0].Outputs!.RemovedFromDeck));
        Assert.Equal(state.Players[0].Deck.Skip(2), frame.State.Players[0].Deck);
        Assert.Equal(1, frame.Receipts.Receipts.Sum(value => value.Outputs!.Burned.Length));
        Assert.Equal(frame.AfterStateHash, FrameResolver.Resolve(state, intents.Reverse()).AfterStateHash);
    }
}
