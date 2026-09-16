using System.Collections.Immutable;
using Eota.Kernel.Commands;
using Eota.Kernel.Content;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Protocols;
using Eota.Kernel.Resolution;
using Eota.Kernel.Rules;

namespace Eota.Kernel.Tests;

public sealed partial class FrameResolverTests
{
    private static readonly CardPrototypeId MinionId = new("TEST-FRAME-MINION");
    private static readonly CardPrototypeId FiniteFieldId = new("TEST-FRAME-FINITE-FIELD");
    private static readonly CardPrototypeId PermanentFieldId = new("TEST-FRAME-PERMANENT-FIELD");
    private static readonly CardPrototypeId FastSpellId = new("TEST-FRAME-FAST-SPELL");
    private static readonly CardPrototypeId SlowSpellId = new("TEST-FRAME-SLOW-SPELL");

    [Fact]
    public void MaximumHealthChangePrecedesDamageWithinTheSameFrame()
    {
        var state = PutMinion(CreateMatch(), PlayerId.One, attack: 1, currentHealth: 1, maximumHealth: 1);
        var entity = Assert.IsType<MinionEntityState>(Assert.Single(state.Entities));
        AtomicIntent[] intents =
        [
            new ModifyMinionStatsIntent(new IntentId(1), entity.Id, 1, 1),
            new DamageMinionIntent(new IntentId(2), entity.Id, 1)
        ];

        var transition = FrameResolver.Resolve(state, intents);
        var result = Assert.IsType<MinionEntityState>(Assert.Single(transition.State.Entities));

        Assert.Equal(2, result.Attack);
        Assert.Equal(2, result.MaximumHealth);
        Assert.Equal(1, result.CurrentHealth);
        Assert.Empty(transition.State.Tombstones);
        Assert.All(transition.Receipts.Receipts, receipt => Assert.Equal(IntentReceiptStatus.Applied, receipt.Status));
    }

    [Fact]
    public void MinusHealthCanKillThroughSynchronizedCurrentHealthEvenWhenMaximumRemainsPositive()
    {
        var state = PutMinion(CreateMatch(), PlayerId.One, attack: 4, currentHealth: 2, maximumHealth: 5);
        var entity = Assert.IsType<MinionEntityState>(Assert.Single(state.Entities));

        var transition = FrameResolver.Resolve(
            state,
            [new ModifyMinionStatsIntent(new IntentId(1), entity.Id, -2, -2)]);
        var tombstone = Assert.Single(transition.State.Tombstones);

        Assert.Empty(transition.State.Entities);
        Assert.Equal(2, tombstone.Attack);
        Assert.Equal(3, tombstone.MaximumHealth);
        Assert.Equal(0, tombstone.CurrentHealth);
        Assert.Equal(EntityRemovalReason.Death, tombstone.Reason);
        Assert.Contains(transition.Events.Events, value => value.Kind == DomainEventKind.EntityDied);
        Assert.Contains(transition.Events.Events, value => value.Kind == DomainEventKind.EntityLeft);
        Assert.Equal(CardZone.Discard, GetCard(transition.State, entity.CardInstanceId).Zone);
    }

    [Fact]
    public void ExplicitKillMarkerIsNotClearedByHealingInTheSameFrame()
    {
        var state = PutMinion(CreateMatch(), PlayerId.One, attack: 1, currentHealth: 1, maximumHealth: 1);
        var entity = Assert.IsType<MinionEntityState>(Assert.Single(state.Entities));

        var transition = FrameResolver.Resolve(
            state,
            [
                new KillMinionIntent(new IntentId(1), entity.Id),
                new HealMinionIntent(new IntentId(2), entity.Id, 100)
            ]);

        Assert.Empty(transition.State.Entities);
        Assert.Equal(1, Assert.Single(transition.State.Tombstones).CurrentHealth);
    }

    [Fact]
    public void MinionHealingIsCappedByMaximumHealthAfterSameFrameChanges()
    {
        var state = PutMinion(CreateMatch(), PlayerId.One, attack: 2, currentHealth: 4, maximumHealth: 5);
        var entity = Assert.IsType<MinionEntityState>(Assert.Single(state.Entities));

        var transition = FrameResolver.Resolve(
            state,
            [
                new ModifyMinionStatsIntent(new IntentId(1), entity.Id, 0, 2),
                new DamageMinionIntent(new IntentId(2), entity.Id, 3),
                new HealMinionIntent(new IntentId(3), entity.Id, 100)
            ]);
        var result = Assert.IsType<MinionEntityState>(Assert.Single(transition.State.Entities));
        var healingReceipt = transition.Receipts.Receipts.Single(value => value.IntentId == new IntentId(3));

        Assert.Equal(7, result.MaximumHealth);
        Assert.Equal(7, result.CurrentHealth);
        Assert.Equal(IntentReceiptStatus.PartiallyApplied, healingReceipt.Status);
        Assert.Equal(4, healingReceipt.AppliedValue);
    }

    [Fact]
    public void HeroMaximumHealthIsMutableAndHealingUsesTheCurrentMaximum()
    {
        var damaged = FrameResolver.Resolve(
            CreateMatch(),
            [new DamageHeroIntent(new IntentId(1), PlayerId.One, 10)]).State;

        var transition = FrameResolver.Resolve(
            damaged,
            [
                new ModifyHeroMaximumHealthIntent(damaged.NextIntentId, PlayerId.One, 5),
                new HealHeroIntent(new IntentId(damaged.NextIntentId.Value + 1), PlayerId.One, 100)
            ]);
        var player = transition.State.Players.Single(value => value.Id == PlayerId.One);
        var healingReceipt = transition.Receipts.Receipts.Single(value => value.IntentId.Value == 3);

        Assert.Equal(35, player.HeroMaximumHealth);
        Assert.Equal(35, player.HeroHealth);
        Assert.Equal(IntentReceiptStatus.PartiallyApplied, healingReceipt.Status);
        Assert.Equal(10, healingReceipt.AppliedValue);
        Assert.Contains(transition.Events.Events, value => value.Kind == DomainEventKind.HeroMaximumHealthChanged);
    }

    [Fact]
    public void SameFrameHealingCanRescueHeroFromLethalDamage()
    {
        var transition = FrameResolver.Resolve(
            CreateMatch(),
            [
                new DamageHeroIntent(new IntentId(1), PlayerId.One, 40),
                new HealHeroIntent(new IntentId(2), PlayerId.One, 15)
            ]);

        Assert.Equal(5, transition.State.Players.Single(value => value.Id == PlayerId.One).HeroHealth);
        Assert.Equal(MatchStatus.Active, transition.State.Status);
        Assert.Equal(MatchOutcome.None, transition.State.Outcome);
    }

    [Fact]
    public void SimultaneousHealingCapacityIsAllocatedByStableIntentId()
    {
        AtomicIntent[] intents =
        [
            new DamageHeroIntent(new IntentId(1), PlayerId.One, 5),
            new HealHeroIntent(new IntentId(2), PlayerId.One, 4),
            new HealHeroIntent(new IntentId(3), PlayerId.One, 4)
        ];

        var forward = FrameResolver.Resolve(CreateMatch(), intents);
        var reverse = FrameResolver.Resolve(CreateMatch(), intents.Reverse());
        var receipts = forward.Receipts.Receipts.OrderBy(value => value.IntentId.Value).ToArray();

        Assert.Equal(forward.AfterStateHash, reverse.AfterStateHash);
        Assert.Equal(forward.Receipts.Hash, reverse.Receipts.Hash);
        Assert.Equal(4, receipts[1].AppliedValue);
        Assert.Equal(1, receipts[2].AppliedValue);
        Assert.Equal(IntentReceiptStatus.PartiallyApplied, receipts[2].Status);
    }

    [Fact]
    public void BothHeroesDyingInOneFrameProducesADraw()
    {
        var state = CreateMatch();

        var transition = FrameResolver.Resolve(
            state,
            [
                new DamageHeroIntent(new IntentId(1), PlayerId.One, 30),
                new DamageHeroIntent(new IntentId(2), PlayerId.Two, 30)
            ]);

        Assert.Equal(MatchStatus.Finished, transition.State.Status);
        Assert.Equal(MatchOutcome.Draw, transition.State.Outcome);
        Assert.Contains(transition.Events.Events, value => value.Kind == DomainEventKind.MatchEnded);
    }

    [Fact]
    public void IntentEnumerationOrderCannotChangeStateReceiptsOrEvents()
    {
        var state = PutMinion(CreateMatch(), PlayerId.One, attack: 4, currentHealth: 5, maximumHealth: 5);
        var entity = Assert.IsType<MinionEntityState>(Assert.Single(state.Entities));
        AtomicIntent[] intents =
        [
            new DamageMinionIntent(new IntentId(1), entity.Id, 2),
            new HealMinionIntent(new IntentId(2), entity.Id, 1),
            new ModifyMinionStatsIntent(new IntentId(3), entity.Id, 2, 3),
            new DamageHeroIntent(new IntentId(4), PlayerId.Two, 4)
        ];

        var forward = FrameResolver.Resolve(state, intents);
        var reverse = FrameResolver.Resolve(state, intents.Reverse());

        Assert.Equal(forward.AfterStateHash, reverse.AfterStateHash);
        Assert.Equal(forward.Receipts.Hash, reverse.Receipts.Hash);
        Assert.Equal(forward.Events.Hash, reverse.Events.Hash);
    }

    [Fact]
    public void EveryIntentGetsAReceiptEvenWhenItsTargetDoesNotExist()
    {
        var transition = FrameResolver.Resolve(
            CreateMatch(),
            [new DamageMinionIntent(new IntentId(1), new EntityId(999), 3)]);

        var receipt = Assert.Single(transition.Receipts.Receipts);
        Assert.Equal(IntentReceiptStatus.Rejected, receipt.Status);
        Assert.Equal("minion-not-found", receipt.DetailCode);
    }

    [Fact]
    public void PermanentFieldRejectsEnergyChangesButCanBeExplicitlyDestroyed()
    {
        var state = PutField(CreateMatch(), PlayerId.One, PermanentFieldId);
        var entity = Assert.IsType<FieldEntityState>(Assert.Single(state.Entities));

        var energyChange = FrameResolver.Resolve(
            state,
            [new ModifyFieldEnergyIntent(new IntentId(1), entity.Id, -1)]);
        var rejected = Assert.Single(energyChange.Receipts.Receipts);
        Assert.Equal(IntentReceiptStatus.Rejected, rejected.Status);
        Assert.IsType<PermanentFieldLifetimeState>(Assert.IsType<FieldEntityState>(Assert.Single(energyChange.State.Entities)).Lifetime);

        var destroyed = FrameResolver.Resolve(
            energyChange.State,
            [new DestroyFieldIntent(energyChange.State.NextIntentId, entity.Id)]);
        Assert.Empty(destroyed.State.Entities);
        Assert.Null(Assert.Single(destroyed.State.Tombstones).FieldEnergy);
    }

    [Fact]
    public void FiniteFieldIsRemovedAtTheFrameLifecycleCheckpointWhenEnergyReachesZero()
    {
        var state = PutField(CreateMatch(), PlayerId.One, FiniteFieldId);
        var entity = Assert.IsType<FieldEntityState>(Assert.Single(state.Entities));

        var transition = FrameResolver.Resolve(
            state,
            [new ModifyFieldEnergyIntent(new IntentId(1), entity.Id, -1)]);

        Assert.Empty(transition.State.Entities);
        Assert.Equal(0, Assert.Single(transition.State.Tombstones).FieldEnergy);
    }

    [Fact]
    public void ArithmeticOverflowFailsTheWholeFrameWithoutPartiallyChangingTargets()
    {
        var state = PutMinion(CreateMatch(), PlayerId.One, long.MaxValue, 2, 2);
        var entity = Assert.IsType<MinionEntityState>(Assert.Single(state.Entities));

        var transition = FrameResolver.Resolve(
            state,
            [new ModifyMinionStatsIntent(new IntentId(1), entity.Id, 1, 0)]);
        var unchanged = Assert.IsType<MinionEntityState>(Assert.Single(transition.State.Entities));

        Assert.Equal(MatchStatus.Failed, transition.State.Status);
        Assert.Equal(MatchOutcome.RuleFailure, transition.State.Outcome);
        Assert.Equal(long.MaxValue, unchanged.Attack);
        Assert.Equal(IntentReceiptStatus.Error, Assert.Single(transition.Receipts.Receipts).Status);
    }

    [Fact]
    public void WorkItemsEvaluateAgainstOneSharedFrameSnapshot()
    {
        var state = CreateMatch();
        WorkItem[] workItems =
        [
            new SnapshotHeroDamageWorkItem(new WorkItemId(1), state.NextFrameId, new IntentId(1), PlayerId.One),
            new SnapshotHeroDamageWorkItem(new WorkItemId(2), state.NextFrameId, new IntentId(2), PlayerId.One)
        ];

        var transition = MatchKernel.Step(state, workItems);

        Assert.Equal(-30, transition.State.Players.Single(value => value.Id == PlayerId.One).HeroHealth);
        Assert.Equal(new WorkItemId(3), transition.State.NextWorkItemId);
    }

    [Fact]
    public void RunnerDiscardsLaterFramesAsSoonAsTheMatchEnds()
    {
        var state = CreateMatch();
        ImmutableArray<WorkItem>[] frames =
        [
            [new FixedIntentWorkItem(
                new WorkItemId(1),
                new FrameId(1),
                [
                    new DamageHeroIntent(new IntentId(1), PlayerId.One, 30),
                    new DamageHeroIntent(new IntentId(2), PlayerId.Two, 30)
                ])],
            [new FixedIntentWorkItem(
                new WorkItemId(2),
                new FrameId(2),
                [new HealHeroIntent(new IntentId(3), PlayerId.One, 100)])]
        ];

        var result = KernelRunner.RunUntilStopped(state, frames);

        Assert.Equal(MatchOutcome.Draw, result.State.Outcome);
        Assert.True(result.DiscardedRemainingFrames);
        Assert.Single(result.Frames);
    }

    [Fact]
    public void FinishedMatchCannotBeSteppedOrResolvedAgain()
    {
        var finished = FrameResolver.Resolve(
            CreateMatch(),
            [new DamageHeroIntent(new IntentId(1), PlayerId.One, 30)]).State;

        Assert.Equal(MatchStatus.Finished, finished.Status);
        Assert.Throws<InvalidOperationException>(() => FrameResolver.Resolve(
            finished,
            [new HealHeroIntent(finished.NextIntentId, PlayerId.One, 100)]));
        Assert.Throws<InvalidOperationException>(() => MatchKernel.Step(
            finished,
            ImmutableArray<WorkItem>.Empty));
    }

    [Fact]
    public void PlannedDeploymentsUseTheIntentPipelineForBothPlayers()
    {
        var state = CreateMatch();
        var playerOneCard = state.CardInstances.First(value =>
            value.OwnerId == PlayerId.One && value.CurrentPrototypeId == MinionId && value.Zone == CardZone.Hand);
        var playerTwoCard = state.CardInstances.First(value =>
            value.OwnerId == PlayerId.Two && value.CurrentPrototypeId == PermanentFieldId && value.Zone == CardZone.Hand);
        state = MatchCommandProcessor.Accept(
            state,
            new PlanCardCommand(PlayerId.One, 0, playerOneCard.Id, new LaneId(0))).State;
        state = MatchCommandProcessor.Accept(
            state,
            new PlanCardCommand(PlayerId.Two, 0, playerTwoCard.Id, new LaneId(0))).State;
        state = state with { Stage = MatchStage.Deployment };

        var transition = MatchKernel.Step(
            state,
            [new PlannedDeploymentWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId)]);

        Assert.Equal(2, transition.State.Entities.Length);
        Assert.All(transition.Receipts.Receipts, value => Assert.Equal(IntentReceiptStatus.Applied, value.Status));
        Assert.All(transition.State.Players, value => Assert.Empty(value.Planning));
        Assert.Equal(CardZone.Battlefield, GetCard(transition.State, playerOneCard.Id).Zone);
        Assert.Equal(CardZone.Battlefield, GetCard(transition.State, playerTwoCard.Id).Zone);
        Assert.Equal(2, transition.Events.Events.Count(value => value.Kind == DomainEventKind.EntityEntered));
    }

    [Fact]
    public void ReplaceDeploymentLeavesWithoutProducingADeathFact()
    {
        var state = PutMinion(CreateMatch(), PlayerId.One, attack: 3, currentHealth: 2, maximumHealth: 2);
        var occupying = Assert.IsType<MinionEntityState>(Assert.Single(state.Entities));
        var replacementCard = state.CardInstances.First(value =>
            value.OwnerId == PlayerId.One && value.CurrentPrototypeId == MinionId && value.Zone == CardZone.Hand);
        state = MatchCommandProcessor.Accept(
            state,
            new PlanCardCommand(PlayerId.One, 0, replacementCard.Id, occupying.LaneId)).State;
        state = state with { Stage = MatchStage.Deployment };

        var transition = MatchKernel.Step(
            state,
            [new PlannedDeploymentWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId)]);
        var replacement = Assert.IsType<MinionEntityState>(Assert.Single(transition.State.Entities));
        var tombstone = Assert.Single(transition.State.Tombstones);

        Assert.NotEqual(occupying.Id, replacement.Id);
        Assert.Equal(EntityRemovalReason.Replace, tombstone.Reason);
        Assert.Contains(transition.Events.Events, value => value.Kind == DomainEventKind.EntityLeft);
        Assert.DoesNotContain(transition.Events.Events, value => value.Kind == DomainEventKind.EntityDied);
    }

    [Fact]
    public void PlanningRejectsAnOccupiedFieldSlot()
    {
        var state = PutField(CreateMatch(), PlayerId.One, PermanentFieldId);
        var secondField = state.CardInstances.First(value =>
            value.OwnerId == PlayerId.One && value.CurrentPrototypeId == PermanentFieldId && value.Zone == CardZone.Hand);

        var transition = MatchCommandProcessor.Accept(
            state,
            new PlanCardCommand(PlayerId.One, 0, secondField.Id, new LaneId(0)));

        Assert.False(transition.IsAccepted);
        Assert.Equal(CommandRejectionReason.BattlefieldSlotUnavailable, transition.Receipt.RejectionReason);
    }

    [Fact]
    public void CompetingMovementIntentsAllFailWithoutUsingVacatedSlots()
    {
        var skirmisher = ImmutableArray.Create(new MinionKeywordDefinition(MinionKeywordKind.Skirmisher));
        var initial = CreateMatch(protocolDefinition: GameProtocolDefinition.DefaultV0 with { MovementConflictPolicy = MovementConflictPolicy.AllFail });
        var state = PutMinion(initial, PlayerId.One, 1, 2, 2, new LaneId(0), skirmisher);
        state = PutMinion(state, PlayerId.One, 1, 2, 2, new LaneId(2), skirmisher);
        state = PutMinion(state, PlayerId.Two, 1, 2, 2, new LaneId(0));
        state = PutMinion(state, PlayerId.Two, 1, 2, 2, new LaneId(2));
        state = state with { Stage = MatchStage.Movement };
        var originalHash = MatchStateHasher.Compute(state);

        var transition = MatchKernel.Step(
            state,
            [new MovementWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId)]);

        Assert.All(transition.Receipts.Receipts, value =>
        {
            Assert.Equal(IntentReceiptStatus.Rejected, value.Status);
            Assert.Equal("slot-conflict", value.DetailCode);
        });
        Assert.Equal(
            state.Entities.Select(value => (value.Id, value.LaneId)).OrderBy(value => value.Id),
            transition.State.Entities.Select(value => (value.Id, value.LaneId)).OrderBy(value => value.Id));
        Assert.NotEqual(originalHash, transition.AfterStateHash);
    }

    [Fact]
    public void PursuitMovementChoosesTheStableAdjacentTarget()
    {
        var pursuit = ImmutableArray.Create(new MinionKeywordDefinition(MinionKeywordKind.Pursuit));
        var state = PutMinion(CreateMatch(), PlayerId.One, 1, 2, 2, new LaneId(2), pursuit);
        state = PutMinion(state, PlayerId.Two, 1, 2, 2, new LaneId(1));
        state = state with { Stage = MatchStage.Movement };

        var transition = MatchKernel.Step(
            state,
            [new MovementWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId)]);
        var moved = transition.State.Entities
            .OfType<MinionEntityState>()
            .Single(value => value.ControllerId == PlayerId.One);

        Assert.Equal(new LaneId(1), moved.LaneId);
        Assert.Contains(transition.Events.Events, value => value.Kind == DomainEventKind.EntityMoved);
    }

    [Fact]
    public void AllLaneCombatDamageAndLifestealShareOneAtomicFrame()
    {
        var swiftLifesteal = ImmutableArray.Create(
            new MinionKeywordDefinition(MinionKeywordKind.Swift),
            new MinionKeywordDefinition(MinionKeywordKind.Lifesteal));
        var swift = ImmutableArray.Create(new MinionKeywordDefinition(MinionKeywordKind.Swift));
        var state = PutMinion(CreateMatch(), PlayerId.One, 5, 2, 2, new LaneId(0), swiftLifesteal);
        state = PutMinion(state, PlayerId.Two, 5, 2, 2, new LaneId(1), swift);
        state = state with
        {
            Stage = MatchStage.Combat,
            Players = state.Players.Select(value => value with { HeroHealth = 3 }).ToImmutableArray()
        };
        var plan = CombatPlan.Capture(state);

        var transition = MatchKernel.Step(
            state,
            [new NormalCombatWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId, plan)]);

        Assert.Equal(3, transition.State.Players.Single(value => value.Id == PlayerId.One).HeroHealth);
        Assert.Equal(-2, transition.State.Players.Single(value => value.Id == PlayerId.Two).HeroHealth);
        Assert.Equal(MatchStatus.Finished, transition.State.Status);
        Assert.Equal(MatchOutcome.PlayerOneWon, transition.State.Outcome);
    }

    [Fact]
    public void OrdinaryMinionCombatDamageIsSimultaneous()
    {
        var swift = ImmutableArray.Create(new MinionKeywordDefinition(MinionKeywordKind.Swift));
        var state = PutMinion(CreateMatch(), PlayerId.One, 2, 2, 2, new LaneId(0), swift);
        state = PutMinion(state, PlayerId.Two, 2, 2, 2, new LaneId(0), swift);
        state = state with { Stage = MatchStage.Combat };
        var plan = CombatPlan.Capture(state);

        var transition = MatchKernel.Step(
            state,
            [new NormalCombatWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId, plan)]);

        Assert.Empty(transition.State.Entities);
        Assert.Equal(2, transition.State.Tombstones.Length);
    }

    [Fact]
    public void CombatAndActiveAttackDeclarationsAreCommittedBeforeDamage()
    {
        var swift = ImmutableArray.Create(new MinionKeywordDefinition(MinionKeywordKind.Swift));
        var state = PutMinion(CreateMatch(), PlayerId.One, 2, 3, 3, new LaneId(0), swift);
        state = PutMinion(state, PlayerId.Two, 2, 3, 3, new LaneId(0), swift);
        state = state with { Stage = MatchStage.Combat };
        var plan = CombatPlan.Capture(state);

        var combatFrame = MatchKernel.Step(
            state,
            [new CombatDeclarationWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId, plan)]);
        var attackFrame = MatchKernel.Step(
            combatFrame.State,
            [new AttackDeclarationWorkItem(
                combatFrame.State.NextWorkItemId,
                combatFrame.State.NextFrameId,
                combatFrame.State.NextIntentId,
                plan)]);

        var combatEvents = combatFrame.Events.Events.Where(value => value.Kind == DomainEventKind.CombatDeclared).ToArray();
        var attackEvents = attackFrame.Events.Events.Where(value => value.Kind == DomainEventKind.AttackDeclared).ToArray();
        Assert.Equal(2, combatEvents.Length);
        Assert.Equal(2, attackEvents.Length);
        Assert.All(combatEvents, value =>
        {
            Assert.NotNull(value.EntityId);
            Assert.NotNull(value.TargetEntityId);
            Assert.Equal(new LaneId(0), value.LaneId);
        });
        Assert.All(attackFrame.State.Entities.OfType<MinionEntityState>(), value => Assert.Equal(3, value.CurrentHealth));
    }

    [Fact]
    public void FirstStrikeDeathPreventsNormalRetaliation()
    {
        var firstStrike = ImmutableArray.Create(
            new MinionKeywordDefinition(MinionKeywordKind.Swift),
            new MinionKeywordDefinition(MinionKeywordKind.FirstStrike));
        var swift = ImmutableArray.Create(new MinionKeywordDefinition(MinionKeywordKind.Swift));
        var state = PutMinion(CreateMatch(), PlayerId.One, 2, 1, 1, new LaneId(0), firstStrike);
        state = PutMinion(state, PlayerId.Two, 5, 2, 2, new LaneId(0), swift);
        state = state with { Stage = MatchStage.Combat };
        var plan = CombatPlan.Capture(state);

        var firstStrikeFrame = MatchKernel.Step(
            state,
            [new FirstStrikeCombatWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId, plan)]);
        var normalFrame = MatchKernel.Step(
            firstStrikeFrame.State,
            [new NormalCombatWorkItem(
                firstStrikeFrame.State.NextWorkItemId,
                firstStrikeFrame.State.NextFrameId,
                firstStrikeFrame.State.NextIntentId,
                plan)]);
        var survivor = Assert.IsType<MinionEntityState>(Assert.Single(normalFrame.State.Entities));

        Assert.Equal(PlayerId.One, survivor.ControllerId);
        Assert.Equal(1, survivor.CurrentHealth);
        Assert.Equal(30, normalFrame.State.Players.Single(value => value.Id == PlayerId.Two).HeroHealth);
    }

    [Fact]
    public void SlowMinionCannotDefendAgainstAnActiveAttacker()
    {
        var swift = ImmutableArray.Create(new MinionKeywordDefinition(MinionKeywordKind.Swift));
        var slow = ImmutableArray.Create(new MinionKeywordDefinition(MinionKeywordKind.Slow, 1));
        var state = PutMinion(CreateMatch(), PlayerId.One, 3, 2, 2, new LaneId(0), swift);
        state = PutMinion(state, PlayerId.Two, 9, 9, 9, new LaneId(0), slow);
        state = state with { Stage = MatchStage.Combat };
        var plan = CombatPlan.Capture(state);

        var transition = MatchKernel.Step(
            state,
            [new NormalCombatWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId, plan)]);

        Assert.Equal(27, transition.State.Players.Single(value => value.Id == PlayerId.Two).HeroHealth);
        Assert.Equal(9, transition.State.Entities.OfType<MinionEntityState>()
            .Single(value => value.ControllerId == PlayerId.Two).CurrentHealth);
    }

    [Fact]
    public void ReadyTurnTraversesAllStagesAndReturnsToPlanning()
    {
        var state = CreateMatch();
        var minionCard = state.CardInstances.First(value =>
            value.OwnerId == PlayerId.One && value.CurrentPrototypeId == MinionId && value.Zone == CardZone.Hand);
        var fieldCard = state.CardInstances.First(value =>
            value.OwnerId == PlayerId.Two && value.CurrentPrototypeId == PermanentFieldId && value.Zone == CardZone.Hand);
        var fastSpell = state.CardInstances.First(value =>
            value.OwnerId == PlayerId.One && value.CurrentPrototypeId == FastSpellId && value.Zone == CardZone.Hand);
        var slowSpell = state.CardInstances.First(value =>
            value.OwnerId == PlayerId.Two && value.CurrentPrototypeId == SlowSpellId && value.Zone == CardZone.Hand);
        state = MatchCommandProcessor.Accept(
            state,
            new PlanCardCommand(PlayerId.One, 0, minionCard.Id, new LaneId(0))).State;
        state = MatchCommandProcessor.Accept(
            state,
            new PlanSpellCommand(PlayerId.One, 1, fastSpell.Id, null)).State;
        state = MatchCommandProcessor.Accept(
            state,
            new PlanCardCommand(PlayerId.Two, 0, fieldCard.Id, new LaneId(0))).State;
        state = MatchCommandProcessor.Accept(
            state,
            new PlanSpellCommand(PlayerId.Two, 1, slowSpell.Id, null)).State;
        state = MatchCommandProcessor.Accept(state, new SubmitTurnCommand(PlayerId.One, 2)).State;
        state = MatchCommandProcessor.Accept(state, new SubmitTurnCommand(PlayerId.Two, 2)).State;

        var result = TurnResolver.ResolveReadyTurn(state);

        Assert.True(result.CompletedTurn);
        Assert.Equal(MatchStage.Planning, result.State.Stage);
        Assert.Equal(2, result.State.Turn);
        Assert.All(result.State.Players, player =>
        {
            Assert.False(player.TurnSubmitted);
            Assert.Empty(player.Planning);
            Assert.Equal(2, player.MaxCost);
            Assert.Equal(2, player.CurrentCost);
        });
        Assert.Equal(CardZone.Discard, GetCard(result.State, fastSpell.Id).Zone);
        Assert.Equal(CardZone.Discard, GetCard(result.State, slowSpell.Id).Zone);
        Assert.Contains(result.Frames.SelectMany(value => value.Events.Events), value =>
            value.Kind == DomainEventKind.SpellResolved && value.CardInstanceId == fastSpell.Id);
        Assert.Contains(result.Frames.SelectMany(value => value.Events.Events), value =>
            value.Kind == DomainEventKind.TurnStarted);
        for (var index = 1; index < result.Frames.Length; index++)
        {
            Assert.Equal(result.Frames[index - 1].AfterStateHash, result.Frames[index].BeforeStateHash);
        }
    }

    [Fact]
    public void CleanupDecaysFiniteFieldsAndSlowPresentAtCombatStart()
    {
        var slow = ImmutableArray.Create(new MinionKeywordDefinition(MinionKeywordKind.Slow, 1));
        var state = PutMinion(CreateMatch(), PlayerId.One, 1, 3, 3, new LaneId(0), slow);
        state = PutField(state, PlayerId.One, FiniteFieldId);
        state = state with { Stage = MatchStage.ReadyToResolve };

        var result = TurnResolver.ResolveReadyTurn(state);
        var minion = Assert.IsType<MinionEntityState>(Assert.Single(result.State.Entities));

        Assert.Equal(0, minion.SlowTurnsRemaining);
        Assert.DoesNotContain(minion.Keywords, value => value.Kind == MinionKeywordKind.Slow);
        Assert.Contains(result.State.Tombstones, value => value.FieldEnergy == 0);
        Assert.Contains(result.Frames.SelectMany(value => value.Events.Events), value =>
            value.Kind == DomainEventKind.MinionSlowChanged);
    }

    [Fact]
    public void NoEffectMatchCanRunFromCommandsToACombatWinner()
    {
        var state = CreateMatch();
        var minionCard = state.CardInstances.First(value =>
            value.OwnerId == PlayerId.One && value.CurrentPrototypeId == MinionId && value.Zone == CardZone.Hand);
        state = MatchCommandProcessor.Accept(
            state,
            new PlanCardCommand(PlayerId.One, 0, minionCard.Id, new LaneId(0))).State;
        state = MatchCommandProcessor.Accept(state, new SubmitTurnCommand(PlayerId.One, 1)).State;
        state = MatchCommandProcessor.Accept(state, new SubmitTurnCommand(PlayerId.Two, 0)).State;
        state = TurnResolver.ResolveReadyTurn(state).State;

        for (var turn = 0; turn < 35 && state.Status == MatchStatus.Active; turn++)
        {
            var playerOne = state.Players.Single(value => value.Id == PlayerId.One);
            var playerTwo = state.Players.Single(value => value.Id == PlayerId.Two);
            state = MatchCommandProcessor.Accept(
                state,
                new SubmitTurnCommand(PlayerId.One, playerOne.CommandRevision)).State;
            state = MatchCommandProcessor.Accept(
                state,
                new SubmitTurnCommand(PlayerId.Two, playerTwo.CommandRevision)).State;
            state = TurnResolver.ResolveReadyTurn(state).State;
        }

        Assert.Equal(MatchStatus.Finished, state.Status);
        Assert.Equal(MatchOutcome.PlayerOneWon, state.Outcome);
        Assert.Equal(0, state.Players.Single(value => value.Id == PlayerId.Two).HeroHealth);
        Assert.Equal(MatchStage.Combat, state.Stage);
    }

    [Fact]
    public void GuardCannotAttackButStillDealsDefensiveCombatDamage()
    {
        var guard = ImmutableArray.Create(new MinionKeywordDefinition(MinionKeywordKind.Guard));
        var swift = ImmutableArray.Create(new MinionKeywordDefinition(MinionKeywordKind.Swift));
        var state = PutMinion(CreateMatch(), PlayerId.One, 2, 3, 3, new LaneId(0), guard);
        state = PutMinion(state, PlayerId.Two, 3, 3, 3, new LaneId(0), swift);
        state = state with { Stage = MatchStage.Combat };
        var plan = CombatPlan.Capture(state);

        var transition = MatchKernel.Step(
            state,
            [new NormalCombatWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId, plan)]);
        var survivor = Assert.IsType<MinionEntityState>(Assert.Single(transition.State.Entities));

        Assert.Equal(PlayerId.Two, survivor.ControllerId);
        Assert.Equal(1, survivor.CurrentHealth);
    }

    [Fact]
    public void ExecuteKillsInTheFinalCombatFrameEvenAtZeroAttack()
    {
        var execute = ImmutableArray.Create(
            new MinionKeywordDefinition(MinionKeywordKind.Swift),
            new MinionKeywordDefinition(MinionKeywordKind.Execute));
        var swift = ImmutableArray.Create(new MinionKeywordDefinition(MinionKeywordKind.Swift));
        var state = PutMinion(CreateMatch(), PlayerId.One, 0, 2, 2, new LaneId(0), execute);
        state = PutMinion(state, PlayerId.Two, 0, 10, 10, new LaneId(0), swift);
        state = state with { Stage = MatchStage.Combat };
        var plan = CombatPlan.Capture(state);

        var transition = MatchKernel.Step(
            state,
            [new NormalCombatWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId, plan)]);

        Assert.Single(transition.State.Entities);
        Assert.Equal(PlayerId.Two, Assert.Single(transition.State.Tombstones).ControllerId);
    }

    [Fact]
    public void BlockingFieldPreventsActiveAttacksForBothSidesOfItsLane()
    {
        var swift = ImmutableArray.Create(new MinionKeywordDefinition(MinionKeywordKind.Swift));
        var state = PutMinion(CreateMatch(), PlayerId.One, 5, 2, 2, new LaneId(0), swift);
        state = PutField(state, PlayerId.Two, PermanentFieldId);
        state = state with { Stage = MatchStage.Combat };
        var plan = CombatPlan.Capture(state);

        var transition = MatchKernel.Step(
            state,
            [new NormalCombatWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId, plan)]);

        Assert.Equal(30, transition.State.Players.Single(value => value.Id == PlayerId.Two).HeroHealth);
    }

    [Fact]
    public void BeginTurnDrawsOrBurnsUsingTheProtocolHandLimit()
    {
        var drawState = CreateMatch(openingHandSize: 9, handLimit: 10) with { Stage = MatchStage.Cleanup };
        var drawnCardId = drawState.Players.Single(value => value.Id == PlayerId.One).Deck[0];
        var drawn = MatchKernel.Step(
            drawState,
            [new BeginNextTurnWorkItem(drawState.NextWorkItemId, drawState.NextFrameId, drawState.NextIntentId)]);

        Assert.Contains(drawnCardId, drawn.State.Players.Single(value => value.Id == PlayerId.One).Hand);
        Assert.Equal(CardZone.Hand, GetCard(drawn.State, drawnCardId).Zone);
        Assert.Contains(drawn.Events.Events, value =>
            value.Kind == DomainEventKind.CardDrawn && value.CardInstanceId == drawnCardId);

        var burnState = CreateMatch(openingHandSize: 9, handLimit: 9) with { Stage = MatchStage.Cleanup };
        var burnedCardId = burnState.Players.Single(value => value.Id == PlayerId.One).Deck[0];
        var burned = MatchKernel.Step(
            burnState,
            [new BeginNextTurnWorkItem(burnState.NextWorkItemId, burnState.NextFrameId, burnState.NextIntentId)]);

        Assert.Contains(burnedCardId, burned.State.Players.Single(value => value.Id == PlayerId.One).Removed);
        Assert.Equal(CardZone.Removed, GetCard(burned.State, burnedCardId).Zone);
        Assert.Contains(burned.Events.Events, value =>
            value.Kind == DomainEventKind.CardBurned && value.CardInstanceId == burnedCardId);
    }

    [Fact]
    public void EtherDecayProtectionIsConsumedBeforeLaterDecay()
    {
        var state = CreateMatch() with { Stage = MatchStage.Cleanup };
        var lane = state.Lanes[0];
        state = state with
        {
            Lanes = state.Lanes.SetItem(0, lane with
            {
                PlayerOne = lane.PlayerOne with
                {
                    EtherActivation = 3,
                    PreventNextEtherDecay = true
                }
            })
        };

        var protectedFrame = MatchKernel.Step(
            state,
            [new CleanupWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId)]);
        var protectedLane = protectedFrame.State.Lanes[0].PlayerOne;
        Assert.Equal(3, protectedLane.EtherActivation);
        Assert.False(protectedLane.PreventNextEtherDecay);

        var decayFrame = MatchKernel.Step(
            protectedFrame.State,
            [new CleanupWorkItem(
                protectedFrame.State.NextWorkItemId,
                protectedFrame.State.NextFrameId,
                protectedFrame.State.NextIntentId)]);
        Assert.Equal(2, decayFrame.State.Lanes[0].PlayerOne.EtherActivation);
    }

    private static MatchState CreateMatch(
        int openingHandSize = 10,
        int handLimit = 10,
        GameProtocolDefinition? protocolDefinition = null,
        ImmutableArray<FieldKeywordKind> finiteFieldKeywords = default,
        ImmutableArray<FieldKeywordKind> permanentFieldKeywords = default)
    {
        CardDefinition[] definitions =
        [
            new MinionCardDefinition(
                MinionId,
                CardSource.Test,
                Profession.Neutral,
                0,
                1,
                1,
                [new MinionKeywordDefinition(MinionKeywordKind.Replace)]),
            new FieldCardDefinition(FiniteFieldId, CardSource.Test, Profession.Neutral, 0, new FiniteFieldLifetimeDefinition(1), false, finiteFieldKeywords),
            new FieldCardDefinition(PermanentFieldId, CardSource.Test, Profession.Neutral, 0, new PermanentFieldLifetimeDefinition(), true, permanentFieldKeywords),
            new SpellCardDefinition(FastSpellId, CardSource.Test, Profession.Neutral, 0, SpellSpeed.Fast, SpellTargetScope.Global),
            new SpellCardDefinition(SlowSpellId, CardSource.Test, Profession.Neutral, 0, SpellSpeed.Slow, SpellTargetScope.Global)
        ];
        var entries = definitions.Select(value => new DeckEntry(value.Id, 2)).ToArray();
        var protocol = CompiledGameProtocol.Compile((protocolDefinition ?? GameProtocolDefinition.DefaultV0) with
        {
            ProtocolId = "eota.test.frame-resolver",
            RequiredDeckSize = 10,
            MaxCopiesPerCard = 2,
            OpeningHandSize = openingHandSize,
            HandLimit = handLimit,
            DeckConstructionPolicy = DeckConstructionPolicy.DevelopmentAnySource
        });
        var deck = DeckDefinition.Create(Profession.Neutral, entries);
        var result = MatchFactory.Create(new MatchCreationRequest(
            protocol,
            RuleContentPack.Create("eota.card/v2", definitions),
            123,
            deck,
            deck));
        return Assert.IsType<MatchState>(result.State);
    }

    private static MatchState PutMinion(
        MatchState state,
        PlayerId playerId,
        long attack,
        long currentHealth,
        long maximumHealth,
        LaneId? laneId = null,
        ImmutableArray<MinionKeywordDefinition> keywords = default)
    {
        var card = state.CardInstances.First(value =>
            value.OwnerId == playerId && value.CurrentPrototypeId == MinionId && value.Zone == CardZone.Hand);
        var entityId = state.NextEntityId;
        var appliedKeywords = keywords.IsDefault ? ImmutableArray<MinionKeywordDefinition>.Empty : keywords;
        var entity = new MinionEntityState(
            entityId,
            card.Id,
            playerId,
            playerId,
            laneId ?? new LaneId(0),
            attack,
            currentHealth,
            maximumHealth,
            appliedKeywords,
            appliedKeywords
                .Where(value => value.Kind == MinionKeywordKind.Slow)
                .Select(value => value.Parameter)
                .DefaultIfEmpty(0)
                .Max(),
            state.Turn,
            false);
        return PutEntity(state, entity, card);
    }

    private static MatchState PutField(MatchState state, PlayerId playerId, CardPrototypeId prototypeId)
    {
        var card = state.CardInstances.First(value =>
            value.OwnerId == playerId && value.CurrentPrototypeId == prototypeId && value.Zone == CardZone.Hand);
        var definition = Assert.IsType<FieldCardDefinition>(state.Content.Cards.Single(value => value.Id == prototypeId));
        IFieldLifetimeState lifetime = definition.Lifetime switch
        {
            FiniteFieldLifetimeDefinition finite => new FiniteFieldLifetimeState(finite.InitialEnergy),
            PermanentFieldLifetimeDefinition => new PermanentFieldLifetimeState(),
            _ => throw new InvalidOperationException()
        };
        var entity = new FieldEntityState(
            state.NextEntityId,
            card.Id,
            playerId,
            playerId,
            new LaneId(0),
            lifetime,
            definition.Keywords,
            state.Turn,
            false);
        return PutEntity(state, entity, card);
    }

    private static MatchState PutEntity(
        MatchState state,
        BattlefieldEntityState entity,
        CardInstanceState card)
    {
        var players = state.Players.Select(player => player.Id == entity.OwnerId
                ? player with { Hand = player.Hand.Where(value => value != card.Id).ToImmutableArray() }
                : player)
            .ToImmutableArray();
        var instances = state.CardInstances.Select(value => value.Id == card.Id
                ? value with { Zone = CardZone.Battlefield }
                : value)
            .ToImmutableArray();
        var lanes = state.Lanes.Select(lane => lane.Id == entity.LaneId
                ? SetLaneEntity(lane, entity)
                : lane)
            .ToImmutableArray();
        return state with
        {
            Players = players,
            CardInstances = instances,
            Lanes = lanes,
            Entities = state.Entities.Add(entity),
            NextEntityId = new EntityId(checked(entity.Id.Value + 1))
        };
    }

    private static LaneState SetLaneEntity(LaneState lane, BattlefieldEntityState entity)
    {
        if (entity.ControllerId == PlayerId.One)
        {
            return entity is MinionEntityState
                ? lane with { PlayerOne = lane.PlayerOne with { MinionEntityId = entity.Id } }
                : lane with { PlayerOne = lane.PlayerOne with { FieldEntityId = entity.Id } };
        }

        return entity is MinionEntityState
            ? lane with { PlayerTwo = lane.PlayerTwo with { MinionEntityId = entity.Id } }
            : lane with { PlayerTwo = lane.PlayerTwo with { FieldEntityId = entity.Id } };
    }

    private static CardInstanceState GetCard(MatchState state, CardInstanceId cardInstanceId) =>
        state.CardInstances.Single(value => value.Id == cardInstanceId);

    private sealed record FixedIntentWorkItem(
        WorkItemId Id,
        FrameId FrameId,
        ImmutableArray<AtomicIntent> Intents) : WorkItem(Id, FrameId)
    {
        public override ImmutableArray<AtomicIntent> Evaluate(FrameSnapshot snapshot) => Intents;
    }

    private sealed record SnapshotHeroDamageWorkItem(
        WorkItemId Id,
        FrameId FrameId,
        IntentId IntentId,
        PlayerId TargetPlayerId) : WorkItem(Id, FrameId)
    {
        public override ImmutableArray<AtomicIntent> Evaluate(FrameSnapshot snapshot)
        {
            var health = snapshot.State.Players.Single(value => value.Id == TargetPlayerId).HeroHealth;
            return [new DamageHeroIntent(IntentId, TargetPlayerId, health)];
        }
    }
}
