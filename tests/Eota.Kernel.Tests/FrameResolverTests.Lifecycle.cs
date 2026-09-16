using Eota.Kernel.Content;
using Eota.Kernel.Effects;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Kernel.Tests;

public sealed partial class FrameResolverTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LifecycleSubsetsAndPermutationsAgreeForMinionsAndFields(bool field)
    {
        var state = field ? PutField(CreateMatch(finiteFieldKeywords: [FieldKeywordKind.Replaceable]), PlayerId.One, FiniteFieldId)
            : PutMinion(CreateMatch(), PlayerId.One, 7, 4, 5, keywords: [new MinionKeywordDefinition(MinionKeywordKind.Replaceable)]);
        var old = state.Entities[0];
        var target = new EffectTarget(field ? EffectTargetType.Field : EffectTargetType.Minion, old.Id.Value);
        var prototype = field ? FiniteFieldId : MinionId;
        AtomicIntent[] operations = [
            new LifecycleEffectIntent(new IntentId(1), target, PlayerId.One, LifecycleOperation.Transform, prototype),
            new LifecycleEffectIntent(new IntentId(2), target, PlayerId.One, LifecycleOperation.Replace, prototype),
            new LifecycleEffectIntent(new IntentId(3), target, PlayerId.One, LifecycleOperation.Return),
            field ? new DestroyFieldIntent(new IntentId(4), old.Id) : new KillMinionIntent(new IntentId(4), old.Id),
            new LifecycleEffectIntent(new IntentId(5), target, PlayerId.One, LifecycleOperation.Banish) ];
        for (var mask = 1; mask < 32; mask++)
        {
            var chosen = operations.Where((_, index) => (mask & (1 << index)) != 0).ToArray();
            var winning = chosen[^1].Id.Value;
            var baseline = FrameResolver.Resolve(state, chosen);
            Assert.Equal(MatchStatus.Active, baseline.State.Status);
            Assert.Equal(winning == 4, baseline.Events.Events.Any(value => value.Kind == DomainEventKind.EntityDied));
            Assert.Equal(winning is 2 or 3 or 4, baseline.Events.Events.Any(value => value.Kind == DomainEventKind.EntityLeft));
            Assert.Equal(winning == 5, baseline.Events.Events.Any(value => value.Kind == DomainEventKind.EntityBanished));
            Assert.Equal(winning == 1, baseline.Events.Events.Any(value => value.Kind == DomainEventKind.EntityTransformed));
            Assert.Equal(winning == 2, baseline.Events.Events.Any(value => value.Kind == DomainEventKind.EntityEntered));
            Assert.Equal(winning <= 2 ? 1 : 0, baseline.State.Entities.Length);
            Assert.Equal(winning == 1 ? 0 : 1, baseline.State.Tombstones.Length);
            if (winning == 1) { Assert.Equal(old.Id, baseline.State.Entities[0].Id); Assert.Equal(state.NextCardInstanceId, baseline.State.NextCardInstanceId); }
            if (winning == 2) { Assert.NotEqual(old.Id, baseline.State.Entities[0].Id); }
            if (winning == 3) { Assert.NotEqual(old.CardInstanceId, baseline.Receipts.Receipts.Single(value => value.IntentId.Value == 3).CardInstanceId); }
            foreach (var permutation in EffectPermutations(chosen))
            {
                var actual = FrameResolver.Resolve(state, permutation);
                Assert.Equal(baseline.AfterStateHash, actual.AfterStateHash);
                Assert.Equal(baseline.Receipts.Hash, actual.Receipts.Hash);
                Assert.Equal(baseline.Events.Hash, actual.Events.Hash);
            }
        }
    }

    [Fact]
    public void BanishWinsOverKillReturnAndTransformAndHasNoDeathOrLeaveFact()
    {
        var state = PutMinion(CreateMatch(), PlayerId.One, 2, 3, 3);
        var entity = Assert.Single(state.Entities);
        var target = new EffectTarget(EffectTargetType.Minion, entity.Id.Value);
        AtomicIntent[] intents =
        [
            new LifecycleEffectIntent(new IntentId(1), target, PlayerId.One, LifecycleOperation.Transform, MinionId),
            new LifecycleEffectIntent(new IntentId(2), target, PlayerId.One, LifecycleOperation.Return),
            new KillMinionIntent(new IntentId(3), entity.Id),
            new LifecycleEffectIntent(new IntentId(4), target, PlayerId.Two, LifecycleOperation.Banish),
            new ModifyMinionStatsIntent(new IntentId(5), entity.Id, 7, 0)
        ];
        var frame = FrameResolver.Resolve(state, intents);
        Assert.Empty(frame.State.Entities);
        var tombstone = Assert.Single(frame.State.Tombstones);
        Assert.Equal(EntityRemovalReason.Banish, tombstone.Reason);
        Assert.Equal(9, tombstone.Attack);
        Assert.Contains(frame.Events.Events, fact => fact.Kind == DomainEventKind.EntityBanished);
        Assert.DoesNotContain(frame.Events.Events, fact => fact.Kind is DomainEventKind.EntityDied or DomainEventKind.EntityLeft);
        Assert.Contains(entity.CardInstanceId, frame.State.Players[0].Removed);
        foreach (var permutation in EffectPermutations(intents))
        {
            var other = FrameResolver.Resolve(state, permutation);
            Assert.Equal(frame.AfterStateHash, other.AfterStateHash);
            Assert.Equal(frame.Receipts.Hash, other.Receipts.Hash);
            Assert.Equal(frame.Events.Hash, other.Events.Hash);
        }
    }

    [Fact]
    public void ReturnCreatesFreshHandIdentityWithoutDeath()
    {
        var state = PutMinion(CreateMatch(), PlayerId.One, 7, 2, 5);
        var original = Assert.Single(state.Entities);
        var target = new EffectTarget(EffectTargetType.Minion, original.Id.Value);
        var frame = FrameResolver.Resolve(state, [new LifecycleEffectIntent(new IntentId(1), target, PlayerId.One, LifecycleOperation.Return)]);
        Assert.Empty(frame.State.Entities);
        var receipt = Assert.Single(frame.Receipts.Receipts);
        var created = Assert.Single(frame.State.CardInstances, card => card.Id == receipt.CardInstanceId);
        Assert.NotEqual(original.CardInstanceId, created.Id);
        Assert.Equal(MinionId, created.CurrentPrototypeId);
        Assert.Equal(CardZone.Hand, created.Zone);
        Assert.Contains(frame.Events.Events, fact => fact.Kind == DomainEventKind.EntityLeft);
        Assert.DoesNotContain(frame.Events.Events, fact => fact.Kind == DomainEventKind.EntityDied);

    }

    [Theory]
    [InlineData("minion")]
    [InlineData("finite")]
    [InlineData("permanent")]
    public void FullHandReturnKillsTheEntityOnceAndReportsRemovalWithoutCreatingACard(string kind)
    {
        var state = kind == "minion" ? PutMinion(CreateMatch(), PlayerId.Two, 7, 2, 5)
            : PutField(CreateMatch(), PlayerId.Two, kind == "finite" ? FiniteFieldId : PermanentFieldId);
        var original = Assert.Single(state.Entities);
        state = FrameResolver.Resolve(state, [new HandCardIntent(state.NextIntentId, PlayerId.Two, HandRequestKind.Generate,
            new DrawAllocationKey(1, "fill", 0), MinionId)]).State;
        Assert.Equal(state.Protocol.Definition.HandLimit, state.Players[1].Hand.Length);
        var target = new EffectTarget(kind == "minion" ? EffectTargetType.Minion : EffectTargetType.Field, original.Id.Value);
        AtomicIntent[] intents =
        [
            new LifecycleEffectIntent(state.NextIntentId, target, PlayerId.One, LifecycleOperation.Return),
            new LifecycleEffectIntent(new IntentId(state.NextIntentId.Value + 1), target, PlayerId.Two, LifecycleOperation.Return)
        ];
        var frame = FrameResolver.Resolve(state, intents);
        Assert.Empty(frame.State.Entities);
        var tombstone = Assert.Single(frame.State.Tombstones);
        Assert.Equal(EntityRemovalReason.Death, tombstone.Reason);
        Assert.Equal(original.Id, tombstone.EntityId);
        Assert.True(tombstone.FinalEntity is MinionEntityState { IsDead: true } or FieldEntityState { IsDestroyed: true });
        Assert.Contains(original.CardInstanceId, frame.State.Players[1].Discard);
        Assert.Equal(CardZone.Discard, frame.State.CardInstances.Single(card => card.Id == original.CardInstanceId).Zone);
        Assert.Equal(state.Players[1].Hand, frame.State.Players[1].Hand);
        Assert.Equal(state.NextCardInstanceId, frame.State.NextCardInstanceId);
        Assert.Null(frame.State.Lanes[0].PlayerTwo.MinionEntityId);
        Assert.Null(frame.State.Lanes[0].PlayerTwo.FieldEntityId);
        Assert.Single(frame.Events.Events, fact => fact.Kind == DomainEventKind.EntityDied);
        Assert.Single(frame.Events.Events, fact => fact.Kind == DomainEventKind.EntityLeft);
        Assert.DoesNotContain(frame.Events.Events, fact => fact.Kind is DomainEventKind.CardReturned or DomainEventKind.CardBurned);
        Assert.All(frame.Receipts.Receipts, receipt =>
        {
            Assert.Equal(IntentReceiptStatus.Rejected, receipt.Status);
            Assert.Equal("hand-capacity", receipt.DetailCode);
            Assert.Equal(target, Assert.Single(receipt.Outputs!.Removed));
            Assert.Empty(receipt.Outputs.Created); Assert.Empty(receipt.Outputs.EnteredHand);
        });
        var reversed = FrameResolver.Resolve(state, intents.Reverse());
        Assert.Equal(frame.AfterStateHash, reversed.AfterStateHash);
        Assert.Equal(frame.Receipts.Hash, reversed.Receipts.Hash);
        Assert.Equal(frame.Events.Hash, reversed.Events.Hash);
        Assert.Equal(state.RuleRng, frame.State.RuleRng);
    }

    [Fact]
    public void LethalDamageBeatsReturnAndTransformPreservesIdentityWithoutLeave()
    {
        var state = PutMinion(CreateMatch(), PlayerId.One, 7, 2, 5);
        var original = Assert.Single(state.Entities);
        var target = new EffectTarget(EffectTargetType.Minion, original.Id.Value);
        var killed = FrameResolver.Resolve(state,
        [
            new LifecycleEffectIntent(new IntentId(1), target, PlayerId.One, LifecycleOperation.Return),
            new DamageMinionIntent(new IntentId(2), original.Id, 2)
        ]);
        Assert.Equal(EntityRemovalReason.Death, Assert.Single(killed.State.Tombstones).Reason);
        Assert.Equal(IntentReceiptStatus.Rejected, killed.Receipts.Receipts[0].Status);
        var transformed = FrameResolver.Resolve(state,
            [new LifecycleEffectIntent(new IntentId(1), target, PlayerId.One, LifecycleOperation.Transform, MinionId)]);
        var current = Assert.IsType<MinionEntityState>(Assert.Single(transformed.State.Entities));
        Assert.Equal(original.Id, current.Id);
        Assert.Equal(original.CardInstanceId, current.CardInstanceId);
        Assert.Equal(1, current.Attack);
        Assert.Equal(1, current.CurrentHealth);
        Assert.Empty(transformed.State.Tombstones);
        Assert.Equal(DomainEventKind.EntityTransformed, Assert.Single(transformed.Events.Events).Kind);
    }
}
