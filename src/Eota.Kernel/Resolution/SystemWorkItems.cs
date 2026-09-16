using System.Collections.Immutable;
using Eota.Kernel.Content;
using Eota.Kernel.Effects;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Protocols;
using Eota.Kernel.Rules;

namespace Eota.Kernel.Resolution;

public sealed record PlannedDeploymentWorkItem(
    WorkItemId Id,
    FrameId FrameId,
    IntentId FirstIntentId) : WorkItem(Id, FrameId)
{
    public override ImmutableArray<AtomicIntent> Evaluate(FrameSnapshot snapshot)
    {
        RequireStage(snapshot, MatchStage.Deployment);
        var plans = snapshot.State.Players
            .SelectMany(value => value.Planning)
            .Where(value => value.Kind is PlannedActionKind.Minion or PlannedActionKind.Field)
            .OrderBy(value => value.Id)
            .ToImmutableArray();
        var intents = ImmutableArray.CreateBuilder<AtomicIntent>(plans.Length);
        var nextIntentId = FirstIntentId.Value;
        foreach (var plan in plans)
        {
            var laneId = plan.LaneId
                         ?? throw new InvalidOperationException($"Deployment plan '{plan.Id}' has no lane.");
            AtomicIntent intent = plan.Kind switch
            {
                PlannedActionKind.Minion => new DeployMinionIntent(
                    new IntentId(nextIntentId++),
                    plan.CardInstanceId,
                    plan.Id.PlayerId,
                    laneId),
                PlannedActionKind.Field => new DeployFieldIntent(
                    new IntentId(nextIntentId++),
                    plan.CardInstanceId,
                    plan.Id.PlayerId,
                    laneId),
                _ => throw new InvalidOperationException($"Unsupported deployment plan kind '{plan.Kind}'.")
            };
            intents.Add(intent);
        }

        return intents.ToImmutable();
    }

    private static void RequireStage(FrameSnapshot snapshot, MatchStage stage)
    {
        if (snapshot.State.Stage != stage)
        {
            throw new InvalidOperationException($"Planned deployment requires stage '{stage}'.");
        }
    }
}

public sealed record StageAdvanceWorkItem(
    WorkItemId Id,
    FrameId FrameId,
    IntentId IntentId,
    MatchStage ExpectedStage,
    MatchStage NextStage) : WorkItem(Id, FrameId)
{
    public override ImmutableArray<AtomicIntent> Evaluate(FrameSnapshot snapshot) =>
        [new AdvanceStageIntent(IntentId, ExpectedStage, NextStage)];
}

public sealed record PlannedSpellWorkItem(
    WorkItemId Id,
    FrameId FrameId,
    IntentId FirstIntentId,
    PlannedActionKind Kind,
    bool EvaluateEffects = true) : WorkItem(Id, FrameId)
{
    public override ImmutableArray<AtomicIntent> Evaluate(FrameSnapshot snapshot)
    {
        if (Kind is not (PlannedActionKind.FastSpell or PlannedActionKind.SlowSpell))
        {
            throw new InvalidOperationException($"Plan kind '{Kind}' is not a spell speed.");
        }


        var requiredStage = Kind == PlannedActionKind.FastSpell ? MatchStage.FastSpells : MatchStage.SlowSpells;
        if (snapshot.State.Stage != requiredStage)
        {
            throw new InvalidOperationException($"Planned spell resolution requires stage '{requiredStage}'.");
        }

        var nextIntentId = FirstIntentId.Value;
        var plans = snapshot.State.Players
            .SelectMany(value => value.Planning)
            .Where(value => value.Kind == Kind)
            .OrderBy(value => value.Id)
            .ToImmutableArray();
        var intents = plans.Select(value => (AtomicIntent)new ConsumePlannedSpellIntent(
                new IntentId(checked(nextIntentId++)),
                value.CardInstanceId,
                value.Id.PlayerId,
                value.Kind))
            .ToImmutableArray();
        return !EvaluateEffects ? intents : intents.AddRange(EffectEvaluator.Evaluate(snapshot,
            EffectTriggers.Spells(snapshot.State, plans), new IntentId(nextIntentId)));
    }
}

public sealed record SlowDecayWorkItem(
    WorkItemId Id,
    FrameId FrameId,
    IntentId FirstIntentId,
    ImmutableArray<EntityId> EligibleEntityIds,
    long Amount,
    ImmutableDictionary<EntityId, ulong>? Generations = null) : WorkItem(Id, FrameId)
{
    public override ImmutableArray<AtomicIntent> Evaluate(FrameSnapshot snapshot)
    {
        if (snapshot.State.Stage != MatchStage.Combat)
        {
            throw new InvalidOperationException("Slow decay requires the Combat stage.");
        }

        var nextIntentId = FirstIntentId.Value;
        return EligibleEntityIds
            .OrderBy(value => value.Value)
            .Where(entityId => snapshot.State.Entities.Any(value => value.Id == entityId))
            .Select(entityId => (AtomicIntent)new DecayMinionSlowIntent(
                new IntentId(checked(nextIntentId++)),
                entityId,
                Amount,
                Generations?.GetValueOrDefault(entityId)))
            .ToImmutableArray();
    }
}

public sealed record CleanupWorkItem(
    WorkItemId Id,
    FrameId FrameId,
    IntentId FirstIntentId) : WorkItem(Id, FrameId)
{
    public override ImmutableArray<AtomicIntent> Evaluate(FrameSnapshot snapshot)
    {
        if (snapshot.State.Stage != MatchStage.Cleanup)
        {
            throw new InvalidOperationException("Turn cleanup requires the Cleanup stage.");
        }

        var state = snapshot.State;
        var nextIntentId = FirstIntentId.Value;
        var intents = ImmutableArray.CreateBuilder<AtomicIntent>();
        foreach (var entity in state.Entities.Where(value => !value.TemporaryModifiers.IsEmpty || !value.TemporaryChargeOverrides.IsEmpty
            || value is MinionEntityState { TemporaryBaseAttacks.IsEmpty: false }).OrderBy(value => value.Id.Value))
        {
            intents.Add(new DecayModifiersIntent(new IntentId(checked(nextIntentId++)),
                new EffectTarget(entity is MinionEntityState ? EffectTargetType.Minion : EffectTargetType.Field, entity.Id.Value), state.Protocol.Definition.EffectDurationDecayPerTurn));
        }
        foreach (var lane in state.Lanes.Where(value => value.Statuses.Any(status => status.RemainingDuration.HasValue)).OrderBy(value => value.Id))
        { intents.Add(new DecayLaneStatusesIntent(new IntentId(checked(nextIntentId++)), lane.Id, state.Protocol.Definition.EffectDurationDecayPerTurn)); }
        foreach (var entity in state.Entities.Where(value => value.AttachedEffects.Any(effect => effect.RemainingDuration.HasValue)).OrderBy(value => value.Id.Value))
        {
            intents.Add(new DecayAttachedEffectsIntent(new IntentId(checked(nextIntentId++)),
                new EffectTarget(entity is MinionEntityState ? EffectTargetType.Minion : EffectTargetType.Field, entity.Id.Value), state.Protocol.Definition.EffectDurationDecayPerTurn));
        }
        foreach (var player in state.Players.Where(value => value.AttachedEffects.Any(effect => effect.RemainingDuration.HasValue)).OrderBy(value => value.Id))
        {
            intents.Add(new DecayAttachedEffectsIntent(new IntentId(checked(nextIntentId++)),
                new EffectTarget(EffectTargetType.Hero, player.Id.Value), state.Protocol.Definition.EffectDurationDecayPerTurn));
        }
        foreach (var field in state.Entities.OfType<FieldEntityState>().OrderBy(value => value.Id.Value))
        {
            if (field.Lifetime is FiniteFieldLifetimeState)
            {
                intents.Add(new ModifyFieldEnergyIntent(
                    new IntentId(checked(nextIntentId++)),
                    field.Id,
                    -state.Protocol.Definition.FieldEnergyDecayPerTurn));
            }
        }

        foreach (var lane in state.Lanes.OrderBy(value => value.Id.Value))
        {
            intents.Add(new DecayEtherIntent(
                new IntentId(checked(nextIntentId++)),
                PlayerId.One,
                lane.Id,
                state.Protocol.Definition.EtherDecayPerTurn));
            intents.Add(new DecayEtherIntent(
                new IntentId(checked(nextIntentId++)),
                PlayerId.Two,
                lane.Id,
                state.Protocol.Definition.EtherDecayPerTurn));
        }

        return intents.ToImmutable();
    }
}

public sealed record BeginNextTurnWorkItem(
    WorkItemId Id,
    FrameId FrameId,
    IntentId IntentId) : WorkItem(Id, FrameId)
{
    public override ImmutableArray<AtomicIntent> Evaluate(FrameSnapshot snapshot)
    {
        var state = snapshot.State;
        if (state.Stage != MatchStage.Cleanup) { throw new InvalidOperationException("Beginning the next turn requires the Cleanup stage."); }
        var intents = ImmutableArray.CreateBuilder<AtomicIntent>();
        intents.Add(new BeginNextTurnIntent(IntentId));
        if (state.Players.Any(player => !player.Planning.IsEmpty)) { return intents.ToImmutable(); }
        var next = checked(IntentId.Value + 1);
        foreach (var player in state.Players.OrderBy(value => value.Id))
        {
            var count = state.Protocol.Definition.DeckExhaustionPolicy == Eota.Kernel.Protocols.DeckExhaustionPolicy.NoFatigue
                ? Math.Min(state.Protocol.Definition.CardsDrawnPerTurn, player.Deck.Length) : state.Protocol.Definition.CardsDrawnPerTurn;
            for (var index = 0; index < count; index++)
            {
                intents.Add(new HandCardIntent(new IntentId(checked(next++)), player.Id, HandRequestKind.Draw,
                new DrawAllocationKey(0, "system-turn-draw", index)));
            }
        }
        return intents.ToImmutable();
    }
}

public enum CombatEngagementKind
{
    None = 0,
    PlayerOneAttacksHero = 1,
    PlayerTwoAttacksHero = 2,
    MinionCombat = 3
}

public sealed record CombatLaneDeclaration(
    LaneId LaneId,
    CombatEngagementKind Kind,
    EntityId? PlayerOneMinionId,
    EntityId? PlayerTwoMinionId,
    bool PlayerOneActivelyAttacks,
    bool PlayerTwoActivelyAttacks,
    bool PlayerOneCanDealMinionDamage,
    bool PlayerTwoCanDealMinionDamage);

public sealed record CombatPlan(ImmutableArray<CombatLaneDeclaration> Lanes)
{
    public static CombatPlan Capture(MatchState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var declarations = state.Lanes
            .OrderBy(value => value.Id.Value)
            .Select(lane => CaptureLane(state, lane))
            .ToImmutableArray();
        return new CombatPlan(declarations);
    }

    private static CombatLaneDeclaration CaptureLane(MatchState state, LaneState lane)
    {
        var playerOne = GetMinion(state, lane.PlayerOne.MinionEntityId);
        var playerTwo = GetMinion(state, lane.PlayerTwo.MinionEntityId);
        var playerOneCanAttack = playerOne is not null && CombatRules.CanActivelyAttack(state, playerOne);
        var playerTwoCanAttack = playerTwo is not null && CombatRules.CanActivelyAttack(state, playerTwo);
        if (playerOne is null)
        {
            return new CombatLaneDeclaration(
                lane.Id,
                playerTwoCanAttack ? CombatEngagementKind.PlayerTwoAttacksHero : CombatEngagementKind.None,
                null,
                playerTwo?.Id,
                false,
                playerTwoCanAttack,
                false,
                false);
        }

        if (playerTwo is null)
        {
            return new CombatLaneDeclaration(
                lane.Id,
                playerOneCanAttack ? CombatEngagementKind.PlayerOneAttacksHero : CombatEngagementKind.None,
                playerOne.Id,
                null,
                playerOneCanAttack,
                false,
                false,
                false);
        }

        if (!playerOneCanAttack && !playerTwoCanAttack)
        {
            return new CombatLaneDeclaration(
                lane.Id,
                CombatEngagementKind.None,
                playerOne.Id,
                playerTwo.Id,
                false,
                false,
                false,
                false);
        }

        var playerOneCanDefend = CombatRules.CanDefend(playerOne);
        var playerTwoCanDefend = CombatRules.CanDefend(playerTwo);
        if (playerOneCanAttack && !playerTwoCanDefend)
        {
            return new CombatLaneDeclaration(
                lane.Id,
                CombatEngagementKind.PlayerOneAttacksHero,
                playerOne.Id,
                playerTwo.Id,
                true,
                false,
                false,
                false);
        }

        if (playerTwoCanAttack && !playerOneCanDefend)
        {
            return new CombatLaneDeclaration(
                lane.Id,
                CombatEngagementKind.PlayerTwoAttacksHero,
                playerOne.Id,
                playerTwo.Id,
                false,
                true,
                false,
                false);
        }

        return new CombatLaneDeclaration(
            lane.Id,
            CombatEngagementKind.MinionCombat,
            playerOne.Id,
            playerTwo.Id,
            playerOneCanAttack,
            playerTwoCanAttack,
            playerOneCanAttack || (playerTwoCanAttack && playerOneCanDefend),
            playerTwoCanAttack || (playerOneCanAttack && playerTwoCanDefend));
    }

    private static MinionEntityState? GetMinion(MatchState state, EntityId? entityId) =>
        entityId is null
            ? null
            : state.Entities.SingleOrDefault(value => value.Id == entityId) as MinionEntityState;
}

public sealed record CombatDeclarationWorkItem(
    WorkItemId Id,
    FrameId FrameId,
    IntentId FirstIntentId,
    CombatPlan Plan) : WorkItem(Id, FrameId)
{
    public override ImmutableArray<AtomicIntent> Evaluate(FrameSnapshot snapshot)
    {
        RequireCombatStage(snapshot);
        var nextIntentId = FirstIntentId.Value;
        var intents = ImmutableArray.CreateBuilder<AtomicIntent>();
        foreach (var declaration in Plan.Lanes.OrderBy(value => value.LaneId.Value))
        {
            switch (declaration.Kind)
            {
                case CombatEngagementKind.PlayerOneAttacksHero when declaration.PlayerOneMinionId is { } playerOne:
                    intents.Add(new DeclareCombatIntent(
                        new IntentId(checked(nextIntentId++)),
                        declaration.LaneId,
                        playerOne,
                        null,
                        PlayerId.Two));
                    break;
                case CombatEngagementKind.PlayerTwoAttacksHero when declaration.PlayerTwoMinionId is { } playerTwo:
                    intents.Add(new DeclareCombatIntent(
                        new IntentId(checked(nextIntentId++)),
                        declaration.LaneId,
                        playerTwo,
                        null,
                        PlayerId.One));
                    break;
                case CombatEngagementKind.MinionCombat
                    when declaration.PlayerOneMinionId is { } playerOne
                         && declaration.PlayerTwoMinionId is { } playerTwo:
                    intents.Add(new DeclareCombatIntent(
                        new IntentId(checked(nextIntentId++)),
                        declaration.LaneId,
                        playerOne,
                        playerTwo,
                        null));
                    intents.Add(new DeclareCombatIntent(
                        new IntentId(checked(nextIntentId++)),
                        declaration.LaneId,
                        playerTwo,
                        playerOne,
                        null));
                    break;
            }
        }

        return intents.ToImmutable();
    }

    private static void RequireCombatStage(FrameSnapshot snapshot)
    {
        if (snapshot.State.Stage != MatchStage.Combat)
        {
            throw new InvalidOperationException("Combat declarations require the Combat stage.");
        }
    }
}

public sealed record AttackDeclarationWorkItem(
    WorkItemId Id,
    FrameId FrameId,
    IntentId FirstIntentId,
    CombatPlan Plan) : WorkItem(Id, FrameId)
{
    public override ImmutableArray<AtomicIntent> Evaluate(FrameSnapshot snapshot)
    {
        if (snapshot.State.Stage != MatchStage.Combat)
        {
            throw new InvalidOperationException("Attack declarations require the Combat stage.");
        }

        var nextIntentId = FirstIntentId.Value;
        var intents = ImmutableArray.CreateBuilder<AtomicIntent>();
        foreach (var declaration in Plan.Lanes.Select(value => CombatRules.Revalidate(snapshot.State, value)).OrderBy(value => value.LaneId.Value))
        {
            if (declaration.PlayerOneActivelyAttacks && declaration.PlayerOneMinionId is { } playerOne)
            {
                intents.Add(new DeclareAttackIntent(
                    new IntentId(checked(nextIntentId++)),
                    declaration.LaneId,
                    playerOne,
                    declaration.Kind == CombatEngagementKind.MinionCombat ? declaration.PlayerTwoMinionId : null,
                    declaration.Kind == CombatEngagementKind.PlayerOneAttacksHero ? PlayerId.Two : null));
            }

            if (declaration.PlayerTwoActivelyAttacks && declaration.PlayerTwoMinionId is { } playerTwo)
            {
                intents.Add(new DeclareAttackIntent(
                    new IntentId(checked(nextIntentId++)),
                    declaration.LaneId,
                    playerTwo,
                    declaration.Kind == CombatEngagementKind.MinionCombat ? declaration.PlayerOneMinionId : null,
                    declaration.Kind == CombatEngagementKind.PlayerTwoAttacksHero ? PlayerId.One : null));
            }
        }

        return intents.ToImmutable();
    }
}

public sealed record FirstStrikeCombatWorkItem(
    WorkItemId Id,
    FrameId FrameId,
    IntentId FirstIntentId,
    CombatPlan Plan) : WorkItem(Id, FrameId)
{
    public override ImmutableArray<AtomicIntent> Evaluate(FrameSnapshot snapshot)
    {
        if (snapshot.State.Stage != MatchStage.Combat)
        {
            throw new InvalidOperationException("First-strike combat requires the Combat stage.");
        }

        var intents = ImmutableArray.CreateBuilder<AtomicIntent>();
        var nextIntentId = FirstIntentId.Value;
        foreach (var lane in Plan.Lanes.Select(value => CombatRules.Revalidate(snapshot.State, value))
                     .Where(value => value.Kind == CombatEngagementKind.MinionCombat)
                     .OrderBy(value => value.LaneId.Value))
        {
            if (!CombatRules.TryGetDeclaredMinions(snapshot.State, lane, out var playerOne, out var playerTwo))
            {
                continue;
            }

            var playerOneFirst = CombatRules.HasKeyword(playerOne, MinionKeywordKind.FirstStrike);
            var playerTwoFirst = CombatRules.HasKeyword(playerTwo, MinionKeywordKind.FirstStrike);
            if (playerOneFirst && !playerTwoFirst && lane.PlayerOneCanDealMinionDamage)
            {
                CombatRules.AddMinionDamage(snapshot.State, playerOne, playerTwo, intents, ref nextIntentId);
            }
            else if (playerTwoFirst && !playerOneFirst && lane.PlayerTwoCanDealMinionDamage)
            {
                CombatRules.AddMinionDamage(snapshot.State, playerTwo, playerOne, intents, ref nextIntentId);
            }
        }

        return intents.ToImmutable();
    }
}

public sealed record NormalCombatWorkItem(
    WorkItemId Id,
    FrameId FrameId,
    IntentId FirstIntentId,
    CombatPlan Plan,
    ImmutableArray<EntityId> EarlyAttackers = default) : WorkItem(Id, FrameId)
{
    public override ImmutableArray<AtomicIntent> Evaluate(FrameSnapshot snapshot)
    {
        if (snapshot.State.Stage != MatchStage.Combat)
        {
            throw new InvalidOperationException("Normal combat requires the Combat stage.");
        }

        var intents = ImmutableArray.CreateBuilder<AtomicIntent>();
        var nextIntentId = FirstIntentId.Value;
        foreach (var declaration in Plan.Lanes.Select(value => CombatRules.Revalidate(snapshot.State, value)).OrderBy(value => value.LaneId.Value))
        {
            switch (declaration.Kind)
            {
                case CombatEngagementKind.PlayerOneAttacksHero when declaration.PlayerOneActivelyAttacks:
                    AddHeroAttack(snapshot.State, declaration.PlayerOneMinionId, PlayerId.Two, intents, ref nextIntentId);
                    break;
                case CombatEngagementKind.PlayerTwoAttacksHero when declaration.PlayerTwoActivelyAttacks:
                    AddHeroAttack(snapshot.State, declaration.PlayerTwoMinionId, PlayerId.One, intents, ref nextIntentId);
                    break;
                case CombatEngagementKind.MinionCombat:
                    AddMinionCombat(snapshot.State, declaration, intents, ref nextIntentId);
                    break;
            }
        }

        return intents.ToImmutable();
    }

    private static void AddHeroAttack(
        MatchState state,
        EntityId? attackerId,
        PlayerId targetPlayerId,
        ImmutableArray<AtomicIntent>.Builder intents,
        ref ulong nextIntentId)
    {
        if (attackerId is null
            || state.Entities.SingleOrDefault(value => value.Id == attackerId) is not MinionEntityState attacker
            || !CombatRules.CanActivelyAttack(state, attacker))
        {
            return;
        }

        var damage = AuthoritativeNumbers.DamageFromAttack(attacker.Attack);
        intents.Add(new DamageHeroIntent(new IntentId(checked(nextIntentId++)), targetPlayerId, damage));
        if (damage > 0 && CombatRules.HasKeyword(attacker, MinionKeywordKind.Lifesteal))
        {
            intents.Add(new HealHeroIntent(new IntentId(checked(nextIntentId++)), attacker.ControllerId, damage));
        }
    }

    private void AddMinionCombat(
        MatchState state,
        CombatLaneDeclaration declaration,
        ImmutableArray<AtomicIntent>.Builder intents,
        ref ulong nextIntentId)
    {
        if (!CombatRules.TryGetDeclaredMinions(state, declaration, out var playerOne, out var playerTwo))
        {
            return;
        }

        var playerOneFirst = CombatRules.HasKeyword(playerOne, MinionKeywordKind.FirstStrike);
        var playerTwoFirst = CombatRules.HasKeyword(playerTwo, MinionKeywordKind.FirstStrike);
        var playerOneDealsNow = declaration.PlayerOneCanDealMinionDamage && (EarlyAttackers.IsDefault ? !playerOneFirst || playerTwoFirst : !EarlyAttackers.Contains(playerOne.Id));
        var playerTwoDealsNow = declaration.PlayerTwoCanDealMinionDamage && (EarlyAttackers.IsDefault ? !playerTwoFirst || playerOneFirst : !EarlyAttackers.Contains(playerTwo.Id));
        if (playerOneDealsNow)
        {
            CombatRules.AddMinionDamage(state, playerOne, playerTwo, intents, ref nextIntentId);
        }

        if (playerTwoDealsNow)
        {
            CombatRules.AddMinionDamage(state, playerTwo, playerOne, intents, ref nextIntentId);
        }

        if (declaration.PlayerOneCanDealMinionDamage && CombatRules.HasKeyword(playerOne, MinionKeywordKind.Execute))
        {
            intents.Add(new KillMinionIntent(new IntentId(checked(nextIntentId++)), playerTwo.Id));
        }

        if (declaration.PlayerTwoCanDealMinionDamage && CombatRules.HasKeyword(playerTwo, MinionKeywordKind.Execute))
        {
            intents.Add(new KillMinionIntent(new IntentId(checked(nextIntentId++)), playerOne.Id));
        }
    }
}

internal static class CombatRules
{
    internal static void AddMinionDamage(MatchState state, MinionEntityState source, MinionEntityState target,
        ImmutableArray<AtomicIntent>.Builder intents, ref ulong nextId)
    {
        var damage = AuthoritativeNumbers.DamageFromAttack(source.Attack);
        intents.Add(new DamageMinionIntent(new IntentId(checked(nextId++)), target.Id, damage));
        if (damage <= 0 || new System.Numerics.BigInteger(damage) + target.IncomingDamageAdjustment <= 0) { return; }
        var cardId = state.CardInstances.Single(value => value.Id == source.CardInstanceId).CurrentPrototypeId;
        state.Content.TryGetCard(cardId, out var card);
        var effects = card!.Effects.Where(value => value.Trigger.Kind == EffectTriggerKind.CombatDamage).ToArray();
        if (effects.Length == 0) { return; }
        var effective = new System.Numerics.BigInteger(damage) + target.IncomingDamageAdjustment;
        if (effective > long.MaxValue)
        { intents.Add(new EffectDiagnosticIntent(new IntentId(checked(nextId++)), "arithmetic-overflow", true)); return; }
        var fact = new DomainEvent(new EventId(0), DomainEventKind.EntityDamaged, state.NextFrameId, target.Id, target.ControllerId,
            null, null, (long)effective, "combat-contribution", LaneId: target.LaneId);
        var origin = new EffectSource(source.CardInstanceId, cardId, source.ControllerId, source.LaneId, source);
        var contributions = EffectEvaluator.Evaluate(FrameSnapshot.Create(state), effects.Select(effect => new EffectInvocation(origin, effect, fact, target)).ToImmutableArray(), new IntentId(nextId));
        nextId = checked(nextId + (ulong)contributions.Length);
        intents.AddRange(contributions);
        if (intents.Count > state.Protocol.Definition.MaxEffectIntentsPerFrame)
        { intents.Add(new EffectDiagnosticIntent(new IntentId(checked(nextId++)), "effect-intent-budget-exceeded", true)); }
    }
    // Declaration fixes participants and targets. Later frames can revoke eligibility, never retarget.
    public static CombatLaneDeclaration Revalidate(MatchState state, CombatLaneDeclaration declaration)
    {
        var one = state.Entities.OfType<MinionEntityState>().SingleOrDefault(value => value.Id == declaration.PlayerOneMinionId && value.LaneId == declaration.LaneId);
        var two = state.Entities.OfType<MinionEntityState>().SingleOrDefault(value => value.Id == declaration.PlayerTwoMinionId && value.LaneId == declaration.LaneId);
        var oneActive = declaration.PlayerOneActivelyAttacks && one is not null && CanActivelyAttack(state, one);
        var twoActive = declaration.PlayerTwoActivelyAttacks && two is not null && CanActivelyAttack(state, two);
        if (declaration.Kind == CombatEngagementKind.PlayerOneAttacksHero && two is not null && CanDefend(two)) { oneActive = false; }
        if (declaration.Kind == CombatEngagementKind.PlayerTwoAttacksHero && one is not null && CanDefend(one)) { twoActive = false; }
        return declaration with
        {
            PlayerOneActivelyAttacks = oneActive,
            PlayerTwoActivelyAttacks = twoActive,
            PlayerOneCanDealMinionDamage = declaration.PlayerOneCanDealMinionDamage && one is not null && two is not null && CanDefend(two)
                && (oneActive || (twoActive && CanDefend(one))),
            PlayerTwoCanDealMinionDamage = declaration.PlayerTwoCanDealMinionDamage && one is not null && two is not null && CanDefend(one)
                && (twoActive || (oneActive && CanDefend(two)))
        };
    }

    public static ImmutableArray<EntityId> EarlyAttackers(MatchState state, CombatPlan plan)
    {
        var result = ImmutableArray.CreateBuilder<EntityId>();
        foreach (var declaration in plan.Lanes.Select(value => Revalidate(state, value)).Where(value => value.Kind == CombatEngagementKind.MinionCombat))
        {
            if (!TryGetDeclaredMinions(state, declaration, out var one, out var two)) { continue; }
            var oneFirst = HasKeyword(one, MinionKeywordKind.FirstStrike);
            var twoFirst = HasKeyword(two, MinionKeywordKind.FirstStrike);
            if (oneFirst && !twoFirst && declaration.PlayerOneCanDealMinionDamage) { result.Add(one.Id); }
            if (twoFirst && !oneFirst && declaration.PlayerTwoCanDealMinionDamage) { result.Add(two.Id); }
        }
        return result.ToImmutable();
    }

    public static bool CanActivelyAttack(MatchState state, MinionEntityState minion)
    {
        if (minion.SlowTurnsRemaining > 0
            || HasKeyword(minion, MinionKeywordKind.Guard)
            || (minion.EnteredOnTurn == state.Turn && !HasKeyword(minion, MinionKeywordKind.Swift)))
        {
            return false;
        }

        return !LaneStatusRules.IsFrozen(state, minion.LaneId);
    }

    public static bool CanDefend(MinionEntityState minion) => minion.SlowTurnsRemaining <= 0;

    public static bool HasKeyword(MinionEntityState minion, MinionKeywordKind kind) =>
        minion.Keywords.Any(value => value.Kind == kind);

    public static bool TryGetDeclaredMinions(
        MatchState state,
        CombatLaneDeclaration declaration,
        out MinionEntityState playerOne,
        out MinionEntityState playerTwo)
    {
        playerOne = null!;
        playerTwo = null!;
        if (declaration.PlayerOneMinionId is not { } playerOneId
            || declaration.PlayerTwoMinionId is not { } playerTwoId
            || state.Entities.SingleOrDefault(value => value.Id == playerOneId) is not MinionEntityState foundPlayerOne
            || state.Entities.SingleOrDefault(value => value.Id == playerTwoId) is not MinionEntityState foundPlayerTwo
            || foundPlayerOne.LaneId != declaration.LaneId
            || foundPlayerTwo.LaneId != declaration.LaneId)
        {
            return false;
        }

        playerOne = foundPlayerOne;
        playerTwo = foundPlayerTwo;
        return true;
    }

    private static bool LaneFieldBlocksAttacks(MatchState state, EntityId? fieldEntityId)
    {
        if (fieldEntityId is null
            || state.Entities.SingleOrDefault(value => value.Id == fieldEntityId) is not FieldEntityState field
            || !state.Content.TryGetCard(
                state.CardInstances.Single(value => value.Id == field.CardInstanceId).CurrentPrototypeId,
                out var definition))
        {
            return false;
        }

        return definition is FieldCardDefinition { PreventsActiveAttacksInLane: true };
    }
}

public sealed record MovementWorkItem(
    WorkItemId Id,
    FrameId FrameId,
    IntentId FirstIntentId) : WorkItem(Id, FrameId)
{
    public override ImmutableArray<AtomicIntent> Evaluate(FrameSnapshot snapshot)
    {
        if (snapshot.State.Stage != MatchStage.Movement)
        {
            throw new InvalidOperationException("Automatic movement requires the Movement stage.");
        }

        var state = snapshot.State;
        var intents = ImmutableArray.CreateBuilder<AtomicIntent>();
        var nextIntentId = FirstIntentId.Value;
        foreach (var minion in state.Entities.OfType<MinionEntityState>().OrderBy(value => value.Id.Value))
        {
            var hasSkirmisher = HasKeyword(minion, Eota.Kernel.Content.MinionKeywordKind.Skirmisher);
            var hasPursuit = HasKeyword(minion, Eota.Kernel.Content.MinionKeywordKind.Pursuit);
            var enemyInCurrentLane = HasEnemyMinion(state, minion.LaneId, minion.ControllerId);
            Func<LaneId, bool>? targetPredicate = hasSkirmisher && enemyInCurrentLane
                ? laneId => !HasEnemyMinion(state, laneId, minion.ControllerId)
                : hasPursuit && !enemyInCurrentLane
                    ? laneId => HasEnemyMinion(state, laneId, minion.ControllerId)
                    : null;
            if (targetPredicate is null)
            {
                continue;
            }

            var candidates = AdjacentLanes(minion.LaneId, state.Protocol.Definition.LaneCount)
                .Where(laneId => LaneStatusRules.CanMove(state, minion.LaneId, laneId))
                .Where(laneId => OwnMinionSlotIsEmpty(state, laneId, minion.ControllerId))
                .Where(targetPredicate)
                .ToArray();
            if (candidates.Length == 0)
            {
                continue;
            }

            var target = ChooseMovementTarget(
                candidates,
                state.Protocol.Definition.LaneCount,
                state.Protocol.Definition.MovementDirectionPreference);
            var randomCenterChoice = candidates.Length == 2
                                     && MovementRules.DistanceFromCenter(minion.LaneId, state.Protocol.Definition.LaneCount) == 0;
            intents.Add(new MoveMinionIntent(
                new IntentId(checked(nextIntentId++)),
                minion.Id,
                minion.ControllerId,
                minion.LaneId,
                target,
                randomCenterChoice ? candidates.Single(value => value != target) : null));
        }

        return intents.ToImmutable();
    }

    private static bool HasKeyword(MinionEntityState minion, Eota.Kernel.Content.MinionKeywordKind kind) =>
        minion.Keywords.Any(value => value.Kind == kind);

    private static bool HasEnemyMinion(MatchState state, LaneId laneId, PlayerId playerId)
    {
        var lane = state.Lanes.Single(value => value.Id == laneId);
        var enemyLane = playerId == PlayerId.One ? lane.PlayerTwo : lane.PlayerOne;
        return enemyLane.MinionEntityId is not null;
    }

    private static bool OwnMinionSlotIsEmpty(MatchState state, LaneId laneId, PlayerId playerId)
    {
        var lane = state.Lanes.Single(value => value.Id == laneId);
        var ownLane = playerId == PlayerId.One ? lane.PlayerOne : lane.PlayerTwo;
        return ownLane.MinionEntityId is null;
    }

    private static IEnumerable<LaneId> AdjacentLanes(LaneId laneId, int laneCount)
    {
        if (laneId.Value > 0)
        {
            yield return new LaneId(laneId.Value - 1);
        }

        if (laneId.Value + 1 < laneCount)
        {
            yield return new LaneId(laneId.Value + 1);
        }
    }

    private static LaneId ChooseMovementTarget(
        IReadOnlyCollection<LaneId> candidates,
        int laneCount,
        MovementDirectionPreference preference)
    {
        var ordered = preference == MovementDirectionPreference.OutwardFirst
            ? candidates.OrderByDescending(value => MovementRules.DistanceFromCenter(value, laneCount))
            : candidates.OrderBy(value => MovementRules.DistanceFromCenter(value, laneCount));
        return ordered.ThenBy(value => value.Value).First();
    }
}

internal static class MovementRules
{
    public static long DistanceFromCenter(LaneId laneId, int laneCount) =>
        Math.Abs(2L * laneId.Value - (laneCount - 1L));

    public static bool TryGetRandomCandidates(MatchState state, MoveMinionIntent intent, out ImmutableArray<LaneId> candidates)
    {
        candidates = ImmutableArray<LaneId>.Empty;
        if (state.Stage != MatchStage.Movement
            || intent.AlternativeTargetLaneId is not { } alternative
            || alternative == intent.TargetLaneId
            || DistanceFromCenter(intent.FromLaneId, state.Protocol.Definition.LaneCount) != 0
            || state.Entities.SingleOrDefault(value => value.Id == intent.EntityId) is not MinionEntityState minion
            || minion.ControllerId != intent.PlayerId || minion.LaneId != intent.FromLaneId)
        {
            return false;
        }

        var sourceLane = state.Lanes.SingleOrDefault(value => value.Id == intent.FromLaneId);
        if (sourceLane is null
            || (intent.PlayerId == PlayerId.One ? sourceLane.PlayerOne : sourceLane.PlayerTwo).MinionEntityId != intent.EntityId)
        {
            return false;
        }

        var ordered = new[] { intent.TargetLaneId, alternative }.OrderBy(value => value.Value).ToImmutableArray();
        if (ordered[0].Value != intent.FromLaneId.Value - 1 || ordered[1].Value != intent.FromLaneId.Value + 1)
        {
            return false;
        }

        foreach (var candidate in ordered)
        {
            var lane = state.Lanes.SingleOrDefault(value => value.Id == candidate);
            if (lane is null || !LaneStatusRules.CanMove(state, intent.FromLaneId, candidate)
                || (intent.PlayerId == PlayerId.One ? lane.PlayerOne : lane.PlayerTwo).MinionEntityId is not null)
            {
                return false;
            }
        }

        candidates = ordered;
        return true;
    }
}
