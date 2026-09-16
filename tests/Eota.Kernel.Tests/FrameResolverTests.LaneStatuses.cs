using System.Collections.Immutable;
using Eota.Kernel.Commands;
using Eota.Kernel.Content;
using Eota.Kernel.Effects;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Kernel.Tests;

public sealed partial class FrameResolverTests
{
    [Fact]
    public void LaneStatusesMergeByMaximumDurationAndRemoveWinsInEveryOrder()
    {
        var state = CreateMatch();
        AtomicIntent[] intents =
        [
            new ChangeLaneStatusIntent(new IntentId(1), new LaneId(0), PlayerId.One, LaneStatusKind.Frozen, false, 2),
            new ChangeLaneStatusIntent(new IntentId(2), new LaneId(0), PlayerId.Two, LaneStatusKind.Frozen, false, 4),
            new ChangeLaneStatusIntent(new IntentId(3), new LaneId(0), PlayerId.One, LaneStatusKind.Locked, false, null),
            new ChangeLaneStatusIntent(new IntentId(4), new LaneId(0), PlayerId.Two, LaneStatusKind.Locked, true, null)
        ];
        var result = FrameResolver.Resolve(state, intents);
        foreach (var order in Permutations(intents))
        { Assert.Equal(FrameHashes(result), FrameHashes(FrameResolver.Resolve(state, order))); }
        Assert.Equal(new LaneStatusState(LaneStatusKind.Frozen, 4), Assert.Single(result.State.Lanes[0].Statuses));
        Assert.Equal(DomainEventKind.LaneStatusChanged, Assert.Single(result.Events.Events).Kind);
        Assert.Equal(new LaneId(0), result.Events.Events[0].LaneId);
        Assert.Equal(state.RuleRng, result.State.RuleRng);
        Assert.Equal(4, result.Receipts.Receipts.Length);
    }

    [Fact]
    public void CleanupExpiresFiniteLaneStatusesAndKeepsPermanentOnes()
    {
        var state = FrameResolver.Resolve(CreateMatch(),
        [
            new ChangeLaneStatusIntent(new IntentId(1), new LaneId(0), PlayerId.One, LaneStatusKind.Frozen, false, 1),
            new ChangeLaneStatusIntent(new IntentId(2), new LaneId(0), PlayerId.Two, LaneStatusKind.Locked, false, null)
        ]).State with
        { Stage = MatchStage.Cleanup };
        var result = MatchKernel.Step(state, [new CleanupWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId)]);
        Assert.Equal(new LaneStatusState(LaneStatusKind.Locked, null), Assert.Single(result.State.Lanes[0].Statuses));
        Assert.NotEqual(MatchStateHasher.Compute(state), result.AfterStateHash);
    }

    [Fact]
    public void FreezeRevokesBothDeclaredAttacksAndFieldFreezeSurvivesStatusRemoval()
    {
        var state = PutMinion(CreateMatch(), PlayerId.One, 4, 5, 5, keywords: [new(MinionKeywordKind.Swift)]);
        state = PutMinion(state, PlayerId.Two, 4, 5, 5, keywords: [new(MinionKeywordKind.Swift)]) with { Stage = MatchStage.Combat };
        var plan = CombatPlan.Capture(state);
        state = FrameResolver.Resolve(state, [new ChangeLaneStatusIntent(state.NextIntentId, new LaneId(0), PlayerId.One, LaneStatusKind.Frozen, false, 1)]).State;
        var damage = MatchKernel.Step(state, [new NormalCombatWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId, plan)]);
        Assert.All(damage.State.Entities.OfType<MinionEntityState>(), entity => Assert.Equal(5, entity.CurrentHealth));
        Assert.DoesNotContain(damage.Events.Events, value => value.Kind == DomainEventKind.EntityDamaged);
        state = PutField(state, PlayerId.One, PermanentFieldId);
        state = FrameResolver.Resolve(state, [new ChangeLaneStatusIntent(state.NextIntentId, new LaneId(0), PlayerId.Two, LaneStatusKind.Frozen, true, null)]).State;
        Assert.Empty(state.Lanes[0].Statuses);
        Assert.True(LaneStatusRules.IsFrozen(state, new LaneId(0)));
        Assert.All(CombatPlan.Capture(state).Lanes, lane =>
        { Assert.False(lane.PlayerOneActivelyAttacks); Assert.False(lane.PlayerTwoActivelyAttacks); });
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    public void LockedSourceOrDestinationRejectsMovementForEitherPlayer(byte player, int lockedLane)
    {
        var owner = new PlayerId(player);
        var state = PutMinion(CreateMatch(), owner, 1, 2, 2);
        state = FrameResolver.Resolve(state, [new ChangeLaneStatusIntent(state.NextIntentId, new LaneId(lockedLane), PlayerId.One, LaneStatusKind.Locked, false, null)]).State;
        var entity = Assert.Single(state.Entities);
        var result = FrameResolver.Resolve(state, [new MoveMinionIntent(state.NextIntentId, entity.Id, owner, new LaneId(0), new LaneId(1))]);
        Assert.Equal("lane-locked", Assert.Single(result.Receipts.Receipts).DetailCode);
        Assert.Equal(new LaneId(0), Assert.Single(result.State.Entities).LaneId);
        Assert.Equal(state.RuleRng, result.State.RuleRng);
    }

    [Fact]
    public void LockAddedInSameFrameDoesNotRetroactivelyRejectMovement()
    {
        var state = PutMinion(CreateMatch(), PlayerId.One, 1, 2, 2);
        AtomicIntent[] intents =
        [
            new ChangeLaneStatusIntent(new IntentId(1), new LaneId(1), PlayerId.Two, LaneStatusKind.Locked, false, null),
            new MoveMinionIntent(new IntentId(2), state.Entities[0].Id, PlayerId.One, new LaneId(0), new LaneId(1))
        ];
        var result = FrameResolver.Resolve(state, intents);
        Assert.Equal(new LaneId(1), Assert.Single(result.State.Entities).LaneId);
        Assert.True(LaneStatusRules.IsLocked(result.State, new LaneId(1)));
        Assert.Equal(FrameHashes(result), FrameHashes(FrameResolver.Resolve(state, intents.Reverse())));
    }

    [Fact]
    public void LockedLaneRejectsPlanningAndDeploymentButAllowsSpellsAndDeath()
    {
        var state = CreateMatch();
        var card = state.CardInstances.First(value => value.OwnerId == PlayerId.One && value.CurrentPrototypeId == MinionId);
        state = MatchCommandProcessor.Accept(state, new PlanCardCommand(PlayerId.One, 0, card.Id, new LaneId(0))).State;
        state = FrameResolver.Resolve(state, [new ChangeLaneStatusIntent(state.NextIntentId, new LaneId(0), PlayerId.One, LaneStatusKind.Locked, false, null)]).State;
        var enemy = state.CardInstances.First(value => value.OwnerId == PlayerId.Two && value.CurrentPrototypeId == MinionId);
        var rejected = MatchCommandProcessor.Accept(state, new PlanCardCommand(PlayerId.Two, 0, enemy.Id, new LaneId(0)));
        Assert.False(rejected.IsAccepted);
        var spell = state.CardInstances.First(value => value.OwnerId == PlayerId.Two && value.CurrentPrototypeId == FastSpellId);
        Assert.True(MatchCommandProcessor.Accept(state, new PlanSpellCommand(PlayerId.Two, 0, spell.Id, null)).IsAccepted);
        state = state with { Stage = MatchStage.Deployment };
        var deployed = MatchKernel.Step(state, [new PlannedDeploymentWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId)]);
        Assert.Empty(deployed.State.Entities);
        Assert.Contains(deployed.Receipts.Receipts, value => value.DetailCode == "lane-locked");
        state = PutMinion(state, PlayerId.Two, 1, 2, 2);
        var killed = FrameResolver.Resolve(state, [new KillMinionIntent(state.NextIntentId, state.Entities[0].Id)]);
        Assert.Empty(killed.State.Entities);
        Assert.Contains(killed.Events.Events, value => value.Kind == DomainEventKind.EntityDied);
    }

    [Theory]
    [InlineData(LifecycleOperation.Return, false)]
    [InlineData(LifecycleOperation.Replace, false)]
    [InlineData(LifecycleOperation.Banish, true)]
    [InlineData(LifecycleOperation.Transform, true)]
    public void LockBlocksReturnAndReplacementButAllowsRemovalAndTransformation(LifecycleOperation operation, bool allowed)
    {
        var state = PutMinion(CreateMatch(), PlayerId.One, 1, 2, 2);
        state = FrameResolver.Resolve(state, [new ChangeLaneStatusIntent(state.NextIntentId, new LaneId(0), PlayerId.Two, LaneStatusKind.Locked, false, null)]).State;
        var entity = state.Entities[0];
        var result = FrameResolver.Resolve(state, [new LifecycleEffectIntent(state.NextIntentId,
            new EffectTarget(EffectTargetType.Minion, entity.Id.Value), PlayerId.One, operation,
            operation is LifecycleOperation.Replace or LifecycleOperation.Transform ? MinionId : null)]);
        Assert.Equal(allowed ? IntentReceiptStatus.Applied : IntentReceiptStatus.Rejected, Assert.Single(result.Receipts.Receipts).Status);
        Assert.True(LaneStatusRules.IsLocked(result.State, new LaneId(0)));
        if (!allowed)
        {
            var remaining = Assert.IsType<MinionEntityState>(Assert.Single(result.State.Entities));
            Assert.Equal(entity.Id, remaining.Id); Assert.Equal(entity.CardInstanceId, remaining.CardInstanceId);
            Assert.Equal(2, remaining.CurrentHealth); Assert.Empty(result.State.Tombstones);
            Assert.Empty(result.Events.Events);
        }
    }

    [Fact]
    public void AutomaticMovementUsesUnlockedAlternativeWithoutConsumingRng()
    {
        var state = PutMinion(CreateMatch(), PlayerId.One, 1, 2, 2, new LaneId(2), [new(MinionKeywordKind.Pursuit)]);
        state = PutMinion(state, PlayerId.Two, 1, 2, 2, new LaneId(1));
        state = PutMinion(state, PlayerId.Two, 1, 2, 2, new LaneId(3));
        state = FrameResolver.Resolve(state, [new ChangeLaneStatusIntent(state.NextIntentId, new LaneId(1), PlayerId.Two, LaneStatusKind.Locked, false, null)]).State with { Stage = MatchStage.Movement };
        var moved = MatchKernel.Step(state, [new MovementWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId)]);
        Assert.Equal(new LaneId(3), moved.State.Entities.Single(value => value.ControllerId == PlayerId.One).LaneId);
        Assert.Equal(state.RuleRng, moved.State.RuleRng);
    }

    private static IEnumerable<AtomicIntent[]> Permutations(AtomicIntent[] values)
    {
        if (values.Length == 0) { yield return []; }
        for (var index = 0; index < values.Length; index++)
        {
            foreach (var tail in Permutations(values.Where((_, position) => position != index).ToArray()))
            { yield return [values[index], .. tail]; }
        }
    }
}
