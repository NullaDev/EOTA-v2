using Eota.Kernel.Content;
using Eota.Kernel.Effects;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;
using Eota.Kernel.Rules;

namespace Eota.Kernel.Tests;

public sealed partial class FrameResolverTests
{
    [Fact]
    public void FieldKeywordRemovalWinsAgainstAddition()
    {
        var state = PutField(CreateMatch(), PlayerId.One, PermanentFieldId);
        var field = Assert.Single(state.Entities);
        AtomicIntent[] intents =
        [
            new ChangeFieldKeywordIntent(new IntentId(1), field.Id, FieldKeywordKind.Replaceable, false),
            new ChangeFieldKeywordIntent(new IntentId(2), field.Id, FieldKeywordKind.Replaceable, true),
            new ChangeFieldKeywordIntent(new IntentId(3), field.Id, FieldKeywordKind.Replace, false)
        ];
        var frame = FrameResolver.Resolve(state, intents);
        Assert.Equal(FieldKeywordKind.Replace, Assert.Single(Assert.IsType<FieldEntityState>(Assert.Single(frame.State.Entities)).Keywords));
        Assert.Equal(frame.AfterStateHash, FrameResolver.Resolve(state, intents.Reverse()).AfterStateHash);
    }

    [Fact]
    public void AllNumericAndKeywordPermutationsHaveIdenticalReceiptsEventsAndState()
    {
        var state = PutMinion(CreateMatch(), PlayerId.One, 3, 4, 5);
        var id = Assert.Single(state.Entities).Id;
        var target = new EffectTarget(EffectTargetType.Minion, id.Value);
        AtomicIntent[] intents =
        [
            new NumericEffectIntent(new IntentId(1), target, PlayerId.One, NumericProperty.Attack, NumericOperation.Set, 7),
            new NumericEffectIntent(new IntentId(2), target, PlayerId.One, NumericProperty.Attack, NumericOperation.Set, -7),
            new NumericEffectIntent(new IntentId(3), target, PlayerId.One, NumericProperty.Attack, NumericOperation.Add, 2),
            new NumericEffectIntent(new IntentId(4), target, PlayerId.One, NumericProperty.Attack, NumericOperation.Divide, 3),
            new ChangeMinionKeywordIntent(new IntentId(5), id, MinionKeywordKind.Swift, false, 0),
            new ChangeMinionKeywordIntent(new IntentId(6), id, MinionKeywordKind.Swift, true, 0)
        ];
        var expected = FrameResolver.Resolve(state, intents);
        foreach (var permutation in EffectPermutations(intents))
        {
            var actual = FrameResolver.Resolve(state, permutation);
            Assert.Equal(expected.AfterStateHash, actual.AfterStateHash);
            Assert.Equal(expected.Receipts.Hash, actual.Receipts.Hash);
            Assert.Equal(expected.Events.Hash, actual.Events.Hash);
            Assert.Equal(expected.State.RuleRng, actual.State.RuleRng);
        }
    }

    [Fact]
    public void DamageTakenAndMaximumHealthAreAttributesWithoutDamageOrHealingFacts()
    {
        var state = PutMinion(CreateMatch(), PlayerId.One, 1, 2, 5);
        var target = new EffectTarget(EffectTargetType.Minion, Assert.Single(state.Entities).Id.Value);
        var frame = FrameResolver.Resolve(state,
        [
            new NumericEffectIntent(new IntentId(1), target, PlayerId.One, NumericProperty.MaximumHealth, NumericOperation.Add, 2),
            new NumericEffectIntent(new IntentId(2), target, PlayerId.One, NumericProperty.DamageTaken, NumericOperation.Set, 1)
        ]);
        var minion = Assert.IsType<MinionEntityState>(Assert.Single(frame.State.Entities));
        Assert.Equal(7, minion.MaximumHealth);
        Assert.Equal(6, minion.CurrentHealth);
        Assert.DoesNotContain(frame.Events.Events, fact => fact.Kind is DomainEventKind.EntityDamaged or DomainEventKind.EntityHealed);
    }

    [Fact]
    public void CostGrowthClampsActualIncreaseAndNextTurnBonusIsConsumedOnce()
    {
        var state = CreateMatch() with { Stage = MatchStage.Cleanup };
        var target = new EffectTarget(EffectTargetType.Hero, 0);
        AtomicIntent[] intents =
        [
            new NumericEffectIntent(new IntentId(1), target, PlayerId.One, NumericProperty.MaximumCost, NumericOperation.Add, 100),
            new NumericEffectIntent(new IntentId(2), target, PlayerId.One, NumericProperty.NextTurnCost, NumericOperation.Add, 3),
            new NumericEffectIntent(new IntentId(3), target, PlayerId.One, NumericProperty.CurrentCost, NumericOperation.Add, -2)
        ];
        var frame = FrameResolver.Resolve(state, intents);
        var player = frame.State.Players[0];
        Assert.Equal(10, player.MaxCost);
        Assert.Equal(8, player.CurrentCost);
        Assert.Equal(3, player.NextTurnCost);
        Assert.Equal(frame.AfterStateHash, FrameResolver.Resolve(state, intents.Reverse()).AfterStateHash);
        var next = MatchKernel.Step(frame.State, [new BeginNextTurnWorkItem(frame.State.NextWorkItemId, frame.State.NextFrameId, frame.State.NextIntentId)]);
        Assert.Equal(13, next.State.Players[0].CurrentCost);
        Assert.Equal(0, next.State.Players[0].NextTurnCost);
        Assert.NotEqual(MatchStateHasher.Compute(state), MatchStateHasher.Compute(state with { Players = state.Players.SetItem(0, state.Players[0] with { NextTurnCost = 1 }) }));
    }

    [Fact]
    public void EtherAddsClampAndProtectionIsBooleanAndConsumedOnce()
    {
        var state = CreateMatch();
        var target = new EffectTarget(EffectTargetType.Lane, 0);
        AtomicIntent[] intents =
        [
            new NumericEffectIntent(new IntentId(1), target, PlayerId.One, NumericProperty.EtherActivation, NumericOperation.Add, 2),
            new NumericEffectIntent(new IntentId(2), target, PlayerId.One, NumericProperty.EtherActivation, NumericOperation.Add, 2),
            new PreventEtherDecayIntent(new IntentId(3), PlayerId.One, new LaneId(0)),
            new PreventEtherDecayIntent(new IntentId(4), PlayerId.One, new LaneId(0)),
            new DecayEtherIntent(new IntentId(5), PlayerId.One, new LaneId(0), 1)
        ];
        var frame = FrameResolver.Resolve(state, intents);
        Assert.Equal(3, frame.State.Lanes[0].PlayerOne.EtherActivation);
        Assert.False(frame.State.Lanes[0].PlayerOne.PreventNextEtherDecay);
        Assert.Equal(frame.AfterStateHash, FrameResolver.Resolve(state, intents.Reverse()).AfterStateHash);
        var next = FrameResolver.Resolve(frame.State, [new DecayEtherIntent(frame.State.NextIntentId, PlayerId.One, new LaneId(0), 1)]);
        Assert.Equal(2, next.State.Lanes[0].PlayerOne.EtherActivation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FieldEnergyUsesNumericReductionAndPermanentFieldsRejectModification(bool permanent)
    {
        var state = PutField(CreateMatch(), PlayerId.One, permanent ? PermanentFieldId : FiniteFieldId);
        var target = new EffectTarget(EffectTargetType.Field, Assert.Single(state.Entities).Id.Value);
        AtomicIntent[] intents =
        [
            new NumericEffectIntent(new IntentId(1), target, PlayerId.One, NumericProperty.FieldEnergy, NumericOperation.Set, 4),
            new NumericEffectIntent(new IntentId(2), target, PlayerId.One, NumericProperty.FieldEnergy, NumericOperation.Add, 3),
            new NumericEffectIntent(new IntentId(3), target, PlayerId.One, NumericProperty.FieldEnergy, NumericOperation.Divide, 2)
        ];
        var frame = FrameResolver.Resolve(state, intents);
        var field = Assert.IsType<FieldEntityState>(Assert.Single(frame.State.Entities));
        if (permanent)
        {
            Assert.IsType<PermanentFieldLifetimeState>(field.Lifetime);
            Assert.All(frame.Receipts.Receipts, receipt => Assert.Equal(IntentReceiptStatus.Rejected, receipt.Status));
            Assert.Empty(frame.Plan.NumericSetChoices);
        }
        else { Assert.Equal(3, Assert.IsType<FiniteFieldLifetimeState>(field.Lifetime).Energy); }
        Assert.Equal(frame.AfterStateHash, FrameResolver.Resolve(state, intents.Reverse()).AfterStateHash);
    }

    [Fact]
    public void RemovingAndReacquiringSlowDoesNotDecayAsTheOldApplication()
    {
        var state = PutMinion(CreateMatch(), PlayerId.One, 1, 5, 5, keywords: [new MinionKeywordDefinition(MinionKeywordKind.Slow, 2)]);
        var original = Assert.IsType<MinionEntityState>(Assert.Single(state.Entities));
        var removed = FrameResolver.Resolve(state, [new ChangeMinionKeywordIntent(state.NextIntentId, original.Id, MinionKeywordKind.Slow, true, 0)]).State;
        var acquired = FrameResolver.Resolve(removed, [new ChangeMinionKeywordIntent(removed.NextIntentId, original.Id, MinionKeywordKind.Slow, false, 3)]).State;
        var decayed = FrameResolver.Resolve(acquired, [new DecayMinionSlowIntent(acquired.NextIntentId, original.Id, 1, original.SlowGeneration)]);
        var minion = Assert.IsType<MinionEntityState>(Assert.Single(decayed.State.Entities));
        Assert.Equal(3, minion.SlowTurnsRemaining);
        Assert.Equal(original.SlowGeneration + 1, minion.SlowGeneration);
        Assert.Equal(IntentReceiptStatus.NoOp, Assert.Single(decayed.Receipts.Receipts).Status);
    }

    [Fact]
    public void ExplicitSlowReductionsAddTogetherClampAtZeroAndCommute()
    {
        var state = PutMinion(CreateMatch(), PlayerId.One, 1, 5, 5, keywords: [new MinionKeywordDefinition(MinionKeywordKind.Slow, 3)]);
        var id = Assert.Single(state.Entities).Id;
        AtomicIntent[] reductions =
        [
            new DecayMinionSlowIntent(new IntentId(1), id, 1),
            new DecayMinionSlowIntent(new IntentId(2), id, 5)
        ];
        var forward = FrameResolver.Resolve(state, reductions);
        var reverse = FrameResolver.Resolve(state, reductions.Reverse());
        var minion = Assert.IsType<MinionEntityState>(Assert.Single(forward.State.Entities));
        Assert.Equal(0, minion.SlowTurnsRemaining);
        Assert.DoesNotContain(minion.Keywords, keyword => keyword.Kind == MinionKeywordKind.Slow);
        Assert.Equal(forward.AfterStateHash, reverse.AfterStateHash);
        Assert.Equal(forward.Receipts.Hash, reverse.Receipts.Hash);
    }

    private static IEnumerable<AtomicIntent[]> EffectPermutations(AtomicIntent[] values)
    {
        if (values.Length == 0) { yield return []; yield break; }
        for (var index = 0; index < values.Length; index++)
        {
            foreach (var tail in EffectPermutations(values.Where((_, position) => position != index).ToArray()))
            { yield return [values[index], .. tail]; }
        }
    }

    [Fact]
    public void NumericSetUsesControllerPriorityThenAddsAndScalesOnce()
    {
        var state = PutMinion(CreateMatch(), PlayerId.One, 3, 3, 3);
        var entity = Assert.Single(state.Entities);
        var target = new EffectTarget(EffectTargetType.Minion, entity.Id.Value);
        AtomicIntent[] intents =
        [
            new NumericEffectIntent(new IntentId(1), target, PlayerId.Two, NumericProperty.Attack, NumericOperation.Set, 100),
            new NumericEffectIntent(new IntentId(2), target, PlayerId.One, NumericProperty.Attack, NumericOperation.Set, 5),
            new NumericEffectIntent(new IntentId(3), target, PlayerId.One, NumericProperty.Attack, NumericOperation.Add, 2),
            new NumericEffectIntent(new IntentId(4), target, PlayerId.One, NumericProperty.Attack, NumericOperation.Multiply, 3),
            new NumericEffectIntent(new IntentId(5), target, PlayerId.One, NumericProperty.Attack, NumericOperation.Divide, 2)
        ];
        var forward = FrameResolver.Resolve(state, intents);
        var reverse = FrameResolver.Resolve(state, intents.Reverse());
        Assert.Equal(10, Assert.IsType<MinionEntityState>(Assert.Single(forward.State.Entities)).Attack);
        Assert.Equal(forward.AfterStateHash, reverse.AfterStateHash);
        Assert.Equal(forward.Receipts.Hash, reverse.Receipts.Hash);
        Assert.Equal(forward.Events.Hash, reverse.Events.Hash);
        Assert.Equal(state.RuleRng, forward.State.RuleRng);
    }

    [Fact]
    public void SameSideSetsConsumeRngEvenForEqualValuesAndRollbackOnFrameError()
    {
        var state = PutMinion(CreateMatch(), PlayerId.One, 3, 3, 3);
        var target = new EffectTarget(EffectTargetType.Minion, Assert.Single(state.Entities).Id.Value);
        AtomicIntent[] intents =
        [
            new NumericEffectIntent(new IntentId(1), target, PlayerId.One, NumericProperty.Attack, NumericOperation.Set, 4),
            new NumericEffectIntent(new IntentId(2), target, PlayerId.One, NumericProperty.Attack, NumericOperation.Set, 4)
        ];
        var success = FrameResolver.Resolve(state, intents);
        Assert.Equal(state.RuleRng.SampleCount + 1, success.State.RuleRng.SampleCount);
        Assert.Single(success.Plan.NumericSetChoices);
        var failed = FrameResolver.Resolve(state, intents.Append(new EffectDiagnosticIntent(new IntentId(3), "expression-overflow", true)));
        Assert.Equal(MatchStatus.Failed, failed.State.Status);
        Assert.Equal(state.RuleRng, failed.State.RuleRng);
        Assert.Equal(3, Assert.IsType<MinionEntityState>(Assert.Single(failed.State.Entities)).Attack);
    }

    [Fact]
    public void KeywordRemovalWinsAndSlowAdditionsMergeByMaximum()
    {
        var state = PutMinion(CreateMatch(), PlayerId.One, 3, 3, 3);
        var entity = Assert.Single(state.Entities);
        AtomicIntent[] additions =
        [
            new ChangeMinionKeywordIntent(new IntentId(1), entity.Id, MinionKeywordKind.Slow, false, 2),
            new ChangeMinionKeywordIntent(new IntentId(2), entity.Id, MinionKeywordKind.Slow, false, 5)
        ];
        var added = FrameResolver.Resolve(state, additions);
        Assert.Equal(5, Assert.IsType<MinionEntityState>(Assert.Single(added.State.Entities)).SlowTurnsRemaining);
        var removed = FrameResolver.Resolve(state, additions.Append(new ChangeMinionKeywordIntent(new IntentId(3), entity.Id, MinionKeywordKind.Slow, true, 0)));
        Assert.Equal(0, Assert.IsType<MinionEntityState>(Assert.Single(removed.State.Entities)).SlowTurnsRemaining);
        Assert.DoesNotContain(Assert.IsType<MinionEntityState>(Assert.Single(removed.State.Entities)).Keywords, value => value.Kind == MinionKeywordKind.Slow);
    }
}
