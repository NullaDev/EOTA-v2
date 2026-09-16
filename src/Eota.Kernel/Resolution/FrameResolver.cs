using System.Collections.Immutable;
using System.Numerics;
using Eota.Kernel.Content;
using Eota.Kernel.Determinism;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Protocols;
using Eota.Kernel.Rules;
using Eota.Kernel.Effects;

namespace Eota.Kernel.Resolution;

public sealed class ReducerRegistry
{
    private readonly ImmutableHashSet<ConflictKind> _supportedKinds =
    [
        ConflictKind.HeroHealth,
        ConflictKind.MinionState,
        ConflictKind.FieldState,
        ConflictKind.SlotOccupancy,
        ConflictKind.LaneResource,
        ConflictKind.CardZone,
        ConflictKind.MatchFlow,
        ConflictKind.CombatFact,
        ConflictKind.PlayerResource,
        ConflictKind.EffectDiagnostic,
        ConflictKind.HandCapacity,
        ConflictKind.LaneStatus
    ];

    public static ReducerRegistry SystemRules { get; } = new();

    public bool Supports(ConflictKind kind) => _supportedKinds.Contains(kind);

    internal void Reduce(ReductionContext context, IntentConflictGroup group)
    {
        if (!_supportedKinds.Contains(group.Key.Kind))
        {
            throw new InvalidOperationException($"No reducer is registered for conflict kind '{group.Key.Kind}'.");
        }

        switch (group.Key.Kind)
        {
            case ConflictKind.HeroHealth:
                context.ReduceHeroHealth(group);
                break;
            case ConflictKind.MinionState:
                context.ReduceMinion(group);
                break;
            case ConflictKind.FieldState:
                context.ReduceField(group);
                break;
            case ConflictKind.SlotOccupancy:
                context.ReduceSlotOccupancy(group);
                break;
            case ConflictKind.LaneResource:
                context.ReduceLaneResource(group);
                break;
            case ConflictKind.CardZone:
                context.ReduceCardZone(group);
                break;
            case ConflictKind.MatchFlow:
                context.ReduceMatchFlow(group);
                break;
            case ConflictKind.CombatFact:
                context.ReduceCombatFact(group);
                break;
            case ConflictKind.PlayerResource:
                context.ReducePlayerResource(group);
                break;
            case ConflictKind.EffectDiagnostic:
                context.ReduceEffectDiagnostic(group);
                break;
            case ConflictKind.LaneStatus:
                context.ReduceLaneStatus(group);
                break;
            case ConflictKind.HandCapacity:
                context.QueueHandRequests(group);
                break;
            default:
                throw new InvalidOperationException($"No reducer is registered for conflict kind '{group.Key.Kind}'.");
        }
    }
}

public static class FrameResolver
{
    public static CommitPlan Plan(MatchState state, IEnumerable<AtomicIntent> intents)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(intents);

        var snapshotHash = MatchStateHasher.Compute(state);
        var orderedIntents = intents.OrderBy(value => value.Id.Value).ToImmutableArray();
        if (orderedIntents.Select(value => value.Id).Distinct().Count() != orderedIntents.Length)
        {
            throw new ArgumentException("A frame cannot contain duplicate IntentId values.", nameof(intents));
        }

        var ruleRng = state.RuleRng;
        var randomChoices = ImmutableArray.CreateBuilder<MovementRandomChoice>();
        var resolvedIntents = orderedIntents.ToDictionary(value => value.Id);
        // Resolve source direction choices in canonical order before grouping by the chosen destination slot.
        foreach (var movement in orderedIntents.OfType<MoveMinionIntent>()
                     .Where(value => value.AlternativeTargetLaneId.HasValue)
                     .OrderBy(value => value.EntityId.Value).ThenBy(value => value.Id.Value))
        {
            if (!MovementRules.TryGetRandomCandidates(state, movement, out var candidates))
            {
                continue;
            }

            // If summons defeat both destinations, a direction draw cannot affect the committed outcome.
            if (candidates.All(lane => orderedIntents.OfType<SummonIntent>().Any(summon => summon.LaneId == lane
                && summon.ControllerId == movement.PlayerId && summon.SlotKind == BattlefieldSlotKind.Minion
                && state.Content.TryGetCard(summon.PrototypeId, out var prototype) && prototype is MinionCardDefinition)))
            { continue; }

            var sample = GlobalRuleRng.NextIndex(ruleRng, candidates.Length);
            var selected = candidates[sample.Index];
            randomChoices.Add(new MovementRandomChoice(state.NextFrameId, movement.Id, movement.EntityId,
                candidates, selected, ruleRng.SampleCount, sample.SamplesConsumed));
            ruleRng = sample.State;
            resolvedIntents[movement.Id] = movement with
            {
                TargetLaneId = selected,
                AlternativeTargetLaneId = candidates.Single(value => value != selected)
            };
        }

        var nextGroupId = state.NextConflictGroupId.Value;
        var groups = resolvedIntents.Values
            .GroupBy(value => value.ConflictKey)
            .OrderBy(value => value.Key)
            .Select(group => new IntentConflictGroup(
                new ConflictGroupId(nextGroupId++),
                group.Key,
                group.OrderBy(value => value.Id.Value).ToImmutableArray()))
            .ToImmutableArray();
        var sets = NumericRules.PlanSets(state, groups, ref ruleRng);
        ImmutableArray<ConflictRandomChoice> extraChoices = [];
        if (orderedIntents.Any(value => value is LifecycleEffectIntent or SummonIntent or HandCardIntent))
        {
            var preview = new ReductionContext(state, state.NextFrameId, sets);
            preview.ConfigureExtendedConflicts(ruleRng);
            preview.ReduceGroups(groups, ReducerRegistry.SystemRules);
            ruleRng = preview.ExtendedRng;
            extraChoices = preview.ConflictChoices;
        }
        return new CommitPlan(state.NextFrameId, snapshotHash, groups, ruleRng, randomChoices.ToImmutable(), sets, extraChoices);
    }

    public static FrameTransition Resolve(
        MatchState state,
        IEnumerable<AtomicIntent> intents,
        ReducerRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Status != MatchStatus.Active)
        {
            throw new InvalidOperationException("Only an active match can resolve a frame.");
        }

        registry ??= ReducerRegistry.SystemRules;
        var plan = Plan(state, intents);
        var context = new ReductionContext(state, plan.FrameId, plan.NumericSetChoices);
        context.ConfigureExtendedConflicts(plan.RuleRng, plan.ConflictRandomChoices);

        context.ReduceGroups(plan.Groups, registry);
        var candidateState = context.TryCreateCommittedState();

        var orderedIntents = plan.Groups
            .SelectMany(group => group.Intents)
            .OrderBy(value => value.Id.Value)
            .ToImmutableArray();
        var nextReceiptId = state.NextReceiptId.Value;
        ImmutableArray<IntentReceipt> receipts;
        MatchState committedState;

        if (context.HasFrameError)
        {
            receipts = orderedIntents.Select(intent => new IntentReceipt(
                new ReceiptId(nextReceiptId++),
                intent.Id,
                IntentReceiptStatus.Error,
                context.FrameErrorCode,
                null,
                null,
                null,
                null))
                .ToImmutableArray();
            committedState = state with
            {
                Status = MatchStatus.Failed,
                Outcome = MatchOutcome.RuleFailure
            };
            context.ClearEvents();
            context.AddEvent(new EventDraft(
                DomainEventKind.MatchEnded,
                null,
                null,
                null,
                null,
                null,
                "rule-failure"));
        }
        else
        {
            receipts = orderedIntents.Select(intent =>
                {
                    var draft = context.GetReceipt(intent.Id);
                    return new IntentReceipt(
                        new ReceiptId(nextReceiptId++),
                        intent.Id,
                        draft.Status,
                        draft.DetailCode,
                        draft.EntityId,
                        draft.PlayerId,
                        draft.AppliedValue,
                        draft.CardInstanceId,
                        draft.Outputs ?? ReceiptOutputRules.Create(intent, draft, candidateState!, plan.FrameId));
                })
                .ToImmutableArray();
            committedState = candidateState! with { RuleRng = plan.RuleRng };
        }

        var nextEventId = state.NextEventId.Value;
        var events = context.Events.Select(draft => new DomainEvent(
                new EventId(nextEventId++),
                draft.Kind,
                plan.FrameId,
                draft.EntityId,
                draft.PlayerId,
                draft.TombstoneId,
                draft.PreviousValue,
                draft.CurrentValue,
                draft.DetailCode,
                draft.CardInstanceId,
                draft.TargetEntityId,
                draft.LaneId,
                draft.TargetPlayerId))
            .ToImmutableArray();
        var maximumIntentId = orderedIntents.Length == 0
            ? state.NextIntentId.Value
            : checked(orderedIntents[^1].Id.Value + 1);
        committedState = committedState with
        {
            NextFrameId = new FrameId(checked(plan.FrameId.Value + 1)),
            NextIntentId = new IntentId(Math.Max(state.NextIntentId.Value, maximumIntentId)),
            NextConflictGroupId = new ConflictGroupId(checked(
                state.NextConflictGroupId.Value + (ulong)plan.Groups.Length)),
            NextReceiptId = new ReceiptId(nextReceiptId),
            NextEventId = new EventId(nextEventId)
        };

        var receiptHash = FrameBatchHasher.ComputeReceipts(plan.FrameId, receipts);
        var eventHash = FrameBatchHasher.ComputeEvents(plan.FrameId, events);
        var afterHash = MatchStateHasher.Compute(committedState);
        return new FrameTransition(
            committedState,
            plan,
            new IntentReceiptBatch(plan.FrameId, receipts, receiptHash),
            new DomainEventBatch(plan.FrameId, events, eventHash),
            plan.SnapshotHash,
            afterHash);
    }
}

internal sealed partial class ReductionContext
{
    internal void ReduceGroups(ImmutableArray<IntentConflictGroup> groups, ReducerRegistry registry)
    {
        try
        {
            foreach (var group in groups) { registry.Reduce(this, group); }
            if (!HasFrameError) { CompleteLifecycleIntents(); }
            if (!HasFrameError) { CompleteHandRequests(); }
        }
        catch (OverflowException) { SetFrameError("arithmetic-overflow"); }
    }

    internal MatchState? TryCreateCommittedState()
    {
        if (HasFrameError) { return null; }
        try { return CreateCommittedState(); }
        catch (OverflowException) { SetFrameError("arithmetic-overflow"); return null; }
    }
    private readonly MatchState _state;
    private readonly FrameId _frameId;
    private readonly Dictionary<PlayerId, long> _heroHealth;
    private readonly Dictionary<PlayerId, long> _heroMaximumHealth;
    private readonly Dictionary<EntityId, BattlefieldEntityState> _entities;
    private readonly Dictionary<IntentId, ReceiptDraft> _receipts = new();
    private readonly List<EventDraft> _events = [];
    private ImmutableArray<PlayerState> _players;
    private ImmutableArray<LaneState> _lanes;
    private ImmutableArray<CardInstanceState> _cardInstances;
    private ImmutableArray<EntityTombstone> _tombstones;
    private ulong _nextEntityId;
    private ulong _nextTombstoneId;
    private MatchStage _stage;
    private int _turn;

    public ReductionContext(MatchState state, FrameId frameId, ImmutableArray<NumericSetChoice> setChoices)
    {
        _setChoices = setChoices;
        _state = state;
        _frameId = frameId;
        _heroHealth = state.Players.ToDictionary(value => value.Id, value => value.HeroHealth);
        _heroMaximumHealth = state.Players.ToDictionary(value => value.Id, value => value.HeroMaximumHealth);
        _entities = state.Entities.ToDictionary(value => value.Id);
        _players = state.Players;
        _lanes = state.Lanes;
        _cardInstances = state.CardInstances;
        _tombstones = state.Tombstones;
        _nextEntityId = state.NextEntityId.Value;
        _nextTombstoneId = state.NextTombstoneId.Value;
        _stage = state.Stage;
        _turn = state.Turn;
    }

    public bool HasFrameError { get; private set; }

    public string FrameErrorCode { get; private set; } = string.Empty;

    public IReadOnlyList<EventDraft> Events => _events;

    public void ReduceHeroHealth(IntentConflictGroup group)
    {
        ValidateNumerics(group);
        var playerId = new PlayerId(checked((byte)group.Key.StableTargetId));
        if (!_heroHealth.TryGetValue(playerId, out var previousHealth))
        {
            RejectAll(group, "hero-not-found", null, playerId);
            return;
        }

        var previousMaximumHealth = _heroMaximumHealth[playerId];
        var maximumHealthDelta = BigInteger.Zero;
        var damage = BigInteger.Zero;
        var healingRequests = new List<(IntentId Id, long Requested)>();
        var killed = false;
        foreach (var intent in group.Intents)
        {
            switch (intent)
            {
                case DamageHeroIntent damageIntent:
                    var effectiveDamage = AuthoritativeNumbers.DamageFromAttack(damageIntent.Amount);
                    damage += effectiveDamage;
                    AddReceipt(intent.Id, EffectiveStatus(effectiveDamage), "damage", null, playerId, effectiveDamage);
                    break;
                case HealHeroIntent healIntent:
                    var effectiveHealing = AuthoritativeNumbers.NonNegativeActionValue(healIntent.Amount);
                    healingRequests.Add((intent.Id, effectiveHealing));
                    break;
                case KillHeroIntent:
                    killed = true;
                    AddReceipt(intent.Id, IntentReceiptStatus.Applied, "kill", null, playerId, null);
                    break;
                case ModifyHeroMaximumHealthIntent maximumHealthIntent:
                    maximumHealthDelta += maximumHealthIntent.Delta;
                    AddReceipt(
                        intent.Id,
                        EffectiveStatus(maximumHealthIntent.Delta),
                        "modify-hero-maximum-health",
                        null,
                        playerId,
                        maximumHealthIntent.Delta);
                    break;
                case NumericEffectIntent:
                    break;
                default:
                    AddReceipt(intent.Id, IntentReceiptStatus.Rejected, "wrong-intent-type", null, playerId, null);
                    break;
            }
        }

        var maximumHealthValue = Number(group, NumericProperty.MaximumHealth, previousMaximumHealth, maximumHealthDelta);
        maximumHealthDelta = maximumHealthValue - previousMaximumHealth;
        var healthBeforeHealing = new BigInteger(previousHealth) + maximumHealthDelta - damage;
        var healingCapacity = BigInteger.Max(BigInteger.Zero, maximumHealthValue - healthBeforeHealing);
        var actualHealing = AddCappedReceipts(
            healingRequests,
            healingCapacity,
            "heal",
            null,
            playerId);
        if (!TryInt64(maximumHealthValue, out var maximumHealth)
            || !TryInt64(healthBeforeHealing + actualHealing, out var currentHealth))
        {
            SetFrameError("arithmetic-overflow");
            return;
        }

        // Explicit execution is an OR contribution and wins over healing in this frame.
        // Both players' other intents still commit before the frame decides the outcome.
        if (killed) { currentHealth = Math.Min(currentHealth, 0); }
        _heroHealth[playerId] = currentHealth;
        _heroMaximumHealth[playerId] = maximumHealth;
        if (maximumHealth != previousMaximumHealth)
        {
            AddEvent(new EventDraft(
                DomainEventKind.HeroMaximumHealthChanged,
                null,
                playerId,
                null,
                previousMaximumHealth,
                maximumHealth,
                "maximum-health"));
        }

        if (currentHealth != previousHealth)
        {
            AddEvent(new EventDraft(
                DomainEventKind.HeroHealthChanged,
                null,
                playerId,
                null,
                previousHealth,
                currentHealth,
                "health"));
        }
    }

    public void ReduceMinion(IntentConflictGroup group)
    {
        if (group.Intents.Any(value => value is LifecycleEffectIntent)) { _lifecycleGroups.Add(group); }
        ValidateNumerics(group);
        var entityId = new EntityId(group.Key.StableTargetId);
        if (!_entities.TryGetValue(entityId, out var entity) || entity is not MinionEntityState minion)
        {
            RejectAll(group, "minion-not-found", entityId, null);
            return;
        }

        var attackDelta = BigInteger.Zero;
        var maximumHealthDelta = BigInteger.Zero;
        var damage = BigInteger.Zero;
        var healingRequests = new List<(IntentId Id, long Requested)>();
        var slowDecayRequests = new List<(IntentId Id, long Requested)>();
        var explicitlyKilled = minion.IsDead;
        var healthLoss = BigInteger.Zero;

        foreach (var intent in group.Intents)
        {
            switch (intent)
            {
                case ModifyMinionStatsIntent stats:
                    attackDelta += stats.AttackDelta;
                    maximumHealthDelta += stats.MaximumHealthDelta;
                    var changed = stats.AttackDelta != 0 || stats.MaximumHealthDelta != 0;
                    AddReceipt(intent.Id, changed ? IntentReceiptStatus.Applied : IntentReceiptStatus.NoOp, "modify-stats", entityId, null, null);
                    break;
                case DamageMinionIntent damageIntent:
                    var effectiveDamage = damageIntent.Amount <= 0 ? 0 : checked(Math.Max(0, checked(damageIntent.Amount + minion.IncomingDamageAdjustment)));
                    damage += effectiveDamage;
                    AddReceipt(intent.Id, EffectiveStatus(effectiveDamage), "damage", entityId, null, effectiveDamage);
                    break;
                case LoseMinionHealthIntent loss:
                    var lost = Math.Max(0, loss.Amount);
                    healthLoss += lost;
                    AddReceipt(loss.Id, EffectiveStatus(lost), "lose-health", entityId, null, lost);
                    break;
                case HealMinionIntent healIntent:
                    var effectiveHealing = AuthoritativeNumbers.NonNegativeActionValue(healIntent.Amount);
                    healingRequests.Add((intent.Id, effectiveHealing));
                    break;
                case KillMinionIntent:
                    explicitlyKilled = true;
                    AddReceipt(intent.Id, IntentReceiptStatus.Applied, "kill", entityId, null, null);
                    break;
                case DecayMinionSlowIntent slowIntent when slowIntent.ExpectedGeneration is null || slowIntent.ExpectedGeneration == minion.SlowGeneration:
                    var effectiveDecay = AuthoritativeNumbers.NonNegativeActionValue(slowIntent.Amount);
                    slowDecayRequests.Add((intent.Id, effectiveDecay));
                    break;
                case DecayMinionSlowIntent:
                    AddReceipt(intent.Id, IntentReceiptStatus.NoOp, "slow-generation-changed", entityId, null, 0);
                    break;
                case NumericEffectIntent:
                case ChangeMinionKeywordIntent:
                case LifecycleEffectIntent:
                case TemporaryModifierIntent:
                case DecayModifiersIntent:
                case AttachEffectIntent:
                case DecayAttachedEffectsIntent:
                    break;
                default:
                    AddReceipt(intent.Id, IntentReceiptStatus.Rejected, "wrong-intent-type", entityId, null, null);
                    break;
            }
        }

        var baseAdjusted = ApplyBaseAttack(group, minion);
        var attackValue = Number(group, NumericProperty.Attack, baseAdjusted.Attack, attackDelta);
        var maximumHealthValue = Number(group, NumericProperty.MaximumHealth, minion.MaximumHealth, maximumHealthDelta);
        var damageTaken = BigInteger.Max(0, Number(group, NumericProperty.DamageTaken,
            new BigInteger(minion.MaximumHealth) - minion.CurrentHealth, 0));
        var healthBeforeHealing = maximumHealthValue - damageTaken - damage - healthLoss;
        var healingCapacity = BigInteger.Max(BigInteger.Zero, maximumHealthValue - healthBeforeHealing);
        var actualHealing = AddCappedReceipts(
            healingRequests,
            healingCapacity,
            "heal",
            entityId,
            null);
        var actualSlowDecay = AddCappedReceipts(
            slowDecayRequests,
            minion.SlowTurnsRemaining,
            "decay-slow",
            entityId,
            null);
        if (!TryInt64(attackValue, out var currentAttack)
            || !TryInt64(maximumHealthValue, out var maximumHealth)
            || !TryInt64(healthBeforeHealing + actualHealing, out var currentHealth)
            || !TryInt64(new BigInteger(minion.SlowTurnsRemaining) - actualSlowDecay, out var slowTurnsRemaining))
        {
            SetFrameError("arithmetic-overflow");
            return;
        }

        var incomingValue = Number(group, NumericProperty.IncomingDamageAdjustment, minion.IncomingDamageAdjustment, 0);
        if (!TryInt64(incomingValue, out var incoming)) { SetFrameError("arithmetic-overflow"); return; }
        _entities[entityId] = ApplyMechanics(group, ApplyTemporaryModifiers(group, minion, Keywords(group, minion, baseAdjusted with
        {
            Attack = currentAttack,
            MaximumHealth = maximumHealth,
            CurrentHealth = currentHealth,
            Keywords = slowTurnsRemaining == 0
                ? minion.Keywords.Where(value => value.Kind != MinionKeywordKind.Slow).ToImmutableArray()
                : minion.Keywords,
            SlowTurnsRemaining = slowTurnsRemaining,
            IsDead = explicitlyKilled,
            IncomingDamageAdjustment = incoming
        })));

        var damageTakenChanged = damageTaken != new BigInteger(minion.MaximumHealth) - minion.CurrentHealth;
        if (currentAttack != minion.Attack || maximumHealth != minion.MaximumHealth || damageTakenChanged)
        {
            AddEvent(new EventDraft(
                DomainEventKind.EntityStatsChanged,
                entityId,
                null,
                null,
                minion.MaximumHealth,
                maximumHealth,
                damageTakenChanged ? "damage-taken" : "attack-and-maximum-health"));
        }

        if (damage > 0)
        {
            AddEvent(new EventDraft(
                DomainEventKind.EntityDamaged,
                entityId,
                null,
                null,
                null,
                TryInt64(damage, out var eventDamage) ? eventDamage : null,
                "damage"));
        }
        if (healthLoss > 0)
        {
            AddEvent(new EventDraft(DomainEventKind.EntityHealthLost, entityId, minion.ControllerId, null, null,
            TryInt64(healthLoss, out var lossValue) ? lossValue : null, "lose-health"));
        }

        if (actualHealing > 0)
        {
            AddEvent(new EventDraft(
                DomainEventKind.EntityHealed,
                entityId,
                null,
                null,
                null,
                TryInt64(actualHealing, out var eventHealing) ? eventHealing : null,
                "heal"));
        }

        if (actualSlowDecay > 0)
        {
            AddEvent(new EventDraft(
                DomainEventKind.MinionSlowChanged,
                entityId,
                null,
                null,
                minion.SlowTurnsRemaining,
                slowTurnsRemaining,
                "slow"));
        }
    }

    public void ReduceField(IntentConflictGroup group)
    {
        if (group.Intents.Any(value => value is LifecycleEffectIntent)) { _lifecycleGroups.Add(group); }
        ValidateNumerics(group);
        var entityId = new EntityId(group.Key.StableTargetId);
        if (!_entities.TryGetValue(entityId, out var entity) || entity is not FieldEntityState field)
        {
            RejectAll(group, "field-not-found", entityId, null);
            return;
        }

        var energyDelta = BigInteger.Zero;
        var explicitlyDestroyed = field.IsDestroyed;
        foreach (var intent in group.Intents)
        {
            switch (intent)
            {
                case ModifyFieldEnergyIntent energyIntent when field.Lifetime is FiniteFieldLifetimeState:
                    energyDelta += energyIntent.Delta;
                    AddReceipt(
                        intent.Id,
                        energyIntent.Delta == 0 ? IntentReceiptStatus.NoOp : IntentReceiptStatus.Applied,
                        "modify-field-energy",
                        entityId,
                        null,
                        energyIntent.Delta);
                    break;
                case ModifyFieldEnergyIntent:
                    AddReceipt(intent.Id, IntentReceiptStatus.Rejected, "permanent-field", entityId, null, null);
                    break;
                case DestroyFieldIntent:
                    explicitlyDestroyed = true;
                    AddReceipt(intent.Id, IntentReceiptStatus.Applied, "destroy", entityId, null, null);
                    break;
                case NumericEffectIntent:
                case ChangeFieldKeywordIntent:
                case TemporaryModifierIntent:
                case DecayModifiersIntent:
                case LifecycleEffectIntent:
                case AttachEffectIntent:
                case DecayAttachedEffectsIntent:
                    break;
                default:
                    AddReceipt(intent.Id, IntentReceiptStatus.Rejected, "wrong-intent-type", entityId, null, null);
                    break;
            }
        }

        IFieldLifetimeState lifetime = field.Lifetime;
        if (field.Lifetime is FiniteFieldLifetimeState finite)
        {
            if (!TryInt64(Number(group, NumericProperty.FieldEnergy, finite.Energy, energyDelta), out var energy))
            {
                SetFrameError("arithmetic-overflow");
                return;
            }

            lifetime = new FiniteFieldLifetimeState(energy);
            if (energy != finite.Energy)
            {
                AddEvent(new EventDraft(
                    DomainEventKind.FieldEnergyChanged,
                    entityId,
                    null,
                    null,
                    finite.Energy,
                    energy,
                    "energy"));
            }
        }

        _entities[entityId] = ApplyMechanics(group, FieldKeywords(group, field with { Lifetime = lifetime, IsDestroyed = explicitlyDestroyed }));
    }

    public void ReduceSlotOccupancy(IntentConflictGroup group)
    {
        if (group.Intents.Any(value => value is SummonIntent))
        {
            ReduceSummons(group);
            return;
        }
        if (group.Intents.All(value => value is MoveMinionIntent))
        {
            ReduceMovement(group);
            return;
        }

        if (group.Intents.Length != 1)
        {
            RejectAll(group, "slot-conflict", null, null);
            return;
        }

        switch (group.Intents[0])
        {
            case DeployMinionIntent minionIntent:
                DeployMinion(minionIntent);
                break;
            case DeployFieldIntent fieldIntent:
                DeployField(fieldIntent);
                break;
            default:
                AddReceipt(
                    group.Intents[0].Id,
                    IntentReceiptStatus.Rejected,
                    "wrong-intent-type",
                    null,
                    null,
                    null);
                break;
        }
    }

    private void ReduceMovement(IntentConflictGroup group)
    {
        var candidates = new List<MoveMinionIntent>();
        foreach (var intent in group.Intents.Cast<MoveMinionIntent>())
        {
            var rejection = GetMoveRejection(intent);
            if (rejection is not null)
            {
                AddReceipt(intent.Id, IntentReceiptStatus.Rejected, rejection, intent.EntityId, intent.PlayerId, null);
            }
            else
            {
                candidates.Add(intent);
            }
        }

        if (candidates.Count == 0)
        {
            return;
        }

        var policy = _state.Protocol.Definition.MovementConflictPolicy;
        MoveMinionIntent? winner = null;
        if (candidates.Count == 1)
        {
            winner = candidates[0];
        }
        else if (policy != MovementConflictPolicy.AllFail)
        {
            var ranked = candidates.GroupBy(value => MovementRules.DistanceFromCenter(value.FromLaneId,
                _state.Protocol.Definition.LaneCount));
            var preferred = policy == MovementConflictPolicy.CenterFirst
                ? ranked.OrderBy(value => value.Key).First()
                : ranked.OrderByDescending(value => value.Key).First();
            // Legal adjacent sources have distinct distances on an even-sized board.
            // Duplicate or otherwise tied candidates never gain priority from their IDs.
            if (preferred.Count() == 1)
            {
                winner = preferred.Single();
            }
        }

        foreach (var intent in candidates)
        {
            if (intent == winner)
            {
                MoveMinion(intent);
            }
            else
            {
                AddReceipt(intent.Id, IntentReceiptStatus.Rejected,
                    winner is null ? "slot-conflict" : "movement-priority-lost", intent.EntityId, intent.PlayerId, null);
            }
        }
    }

    public void ReduceLaneResource(IntentConflictGroup group)
    {
        if (group.Intents.Any(value => value is NumericEffectIntent or PreventEtherDecayIntent or ClearEtherIntent))
        {
            ReduceEffectLaneResource(group);
            return;
        }
        if (group.Intents.Any(value => value is not DecayEtherIntent))
        {
            RejectAll(group, "wrong-intent-type", null, null);
            return;
        }

        var first = (DecayEtherIntent)group.Intents[0];
        var lane = _lanes.SingleOrDefault(value => value.Id == first.LaneId);
        if (lane is null)
        {
            RejectAll(group, "lane-not-found", null, first.PlayerId);
            return;
        }

        var playerLane = GetPlayerLane(lane, first.PlayerId);
        if (group.Intents.Cast<DecayEtherIntent>().Any(value =>
                value.PlayerId != first.PlayerId || value.LaneId != first.LaneId))
        {
            SetFrameError("lane-resource-key-mismatch");
            return;
        }

        if (playerLane.PreventNextEtherDecay)
        {
            foreach (var intent in group.Intents.Cast<DecayEtherIntent>())
            {
                AddReceipt(intent.Id, IntentReceiptStatus.NoOp, "ether-decay-prevented", null, first.PlayerId, 0);
            }

            _lanes = OccupySlot(_lanes, lane, playerLane with { PreventNextEtherDecay = false });
            AddEvent(new EventDraft(
                DomainEventKind.EtherDecayPrevented,
                null,
                first.PlayerId,
                null,
                playerLane.EtherActivation,
                playerLane.EtherActivation,
                $"lane:{first.LaneId.Value}"));
            return;
        }

        var capacity = BigInteger.Max(
            BigInteger.Zero,
            new BigInteger(playerLane.EtherActivation) - _state.Protocol.Definition.MinEtherActivation);
        var requests = group.Intents
            .Cast<DecayEtherIntent>()
            .Select(value => (value.Id, AuthoritativeNumbers.NonNegativeActionValue(value.Amount)));
        var actualDecay = AddCappedReceipts(requests, capacity, "decay-ether", null, first.PlayerId);
        if (!TryInt64(new BigInteger(playerLane.EtherActivation) - actualDecay, out var current))
        {
            SetFrameError("arithmetic-overflow");
            return;
        }

        _lanes = OccupySlot(_lanes, lane, playerLane with { EtherActivation = current });
        if (current != playerLane.EtherActivation)
        {
            AddEvent(new EventDraft(
                DomainEventKind.EtherActivationChanged,
                null,
                first.PlayerId,
                null,
                playerLane.EtherActivation,
                current,
                $"lane:{first.LaneId.Value}"));
        }
    }

    public void ReduceCardZone(IntentConflictGroup group)
    {
        if (group.Intents.All(value => value is NumericEffectIntent or ChangeCardKeywordIntent))
        { ReduceCardModifiers(group); return; }
        if (group.Intents.Length != 1 || group.Intents[0] is not ConsumePlannedSpellIntent intent)
        {
            RejectAll(group, "card-zone-conflict", null, null);
            return;
        }

        var expectedStage = intent.Kind switch
        {
            PlannedActionKind.FastSpell => MatchStage.FastSpells,
            PlannedActionKind.SlowSpell => MatchStage.SlowSpells,
            _ => (MatchStage?)null
        };
        var card = _cardInstances.SingleOrDefault(value => value.Id == intent.CardInstanceId);
        var playerIndex = IndexOfPlayer(_players, intent.PlayerId);
        var player = _players[playerIndex];
        var plan = player.Planning.SingleOrDefault(value => value.CardInstanceId == intent.CardInstanceId);
        if (expectedStage is null
            || _stage != expectedStage
            || card is null
            || card.OwnerId != intent.PlayerId
            || card.Zone != CardZone.Planning
            || plan is null
            || plan.Kind != intent.Kind)
        {
            AddReceipt(
                intent.Id,
                IntentReceiptStatus.Rejected,
                "invalid-spell-resolution",
                null,
                intent.PlayerId,
                null,
                intent.CardInstanceId);
            return;
        }

        _cardInstances = SetCardZone(_cardInstances, card.Id, CardZone.Discard);
        _players = _players.SetItem(playerIndex, player with
        {
            Planning = player.Planning.Where(value => value.CardInstanceId != card.Id).ToImmutableArray(),
            Discard = player.Discard.Add(card.Id).OrderBy(value => value.Value).ToImmutableArray()
        });
        AddReceipt(
            intent.Id,
            IntentReceiptStatus.Applied,
            "resolve-spell",
            null,
            intent.PlayerId,
            null,
            intent.CardInstanceId);
        AddEvent(new EventDraft(
            DomainEventKind.SpellResolved,
            null,
            intent.PlayerId,
            null,
            null,
            null,
            intent.Kind == PlannedActionKind.FastSpell ? "fast" : "slow",
            intent.CardInstanceId,
            LaneId: plan.LaneId));
    }

    public void ReduceMatchFlow(IntentConflictGroup group)
    {
        if (group.Intents.Length != 1)
        {
            RejectAll(group, "match-flow-conflict", null, null);
            return;
        }

        switch (group.Intents[0])
        {
            case AdvanceStageIntent advance:
                AdvanceStage(advance);
                break;
            case BeginNextTurnIntent nextTurn:
                BeginNextTurn(nextTurn);
                break;
            default:
                AddReceipt(group.Intents[0].Id, IntentReceiptStatus.Rejected, "wrong-intent-type", null, null, null);
                break;
        }
    }

    public void ReduceCombatFact(IntentConflictGroup group)
    {
        if (group.Intents.Length != 1 || _stage != MatchStage.Combat)
        {
            RejectAll(group, "invalid-combat-fact", null, null);
            return;
        }

        switch (group.Intents[0])
        {
            case DeclareCombatIntent combat:
                if (!TryGetCombatSubject(combat.SubjectEntityId, combat.LaneId, out var combatSubject)
                    || !IsValidCombatTarget(combat.OpposingEntityId, combat.DefendingPlayerId, combat.LaneId))
                {
                    AddReceipt(combat.Id, IntentReceiptStatus.Rejected, "invalid-combat-declaration", combat.SubjectEntityId, null, null);
                    return;
                }

                AddReceipt(combat.Id, IntentReceiptStatus.Applied, "declare-combat", combat.SubjectEntityId, combatSubject.ControllerId, null);
                AddEvent(new EventDraft(
                    DomainEventKind.CombatDeclared,
                    combat.SubjectEntityId,
                    combatSubject.ControllerId,
                    null,
                    null,
                    null,
                    "combat",
                    null,
                    combat.OpposingEntityId,
                    combat.LaneId,
                    combat.DefendingPlayerId));
                break;
            case DeclareAttackIntent attack:
                if (!TryGetCombatSubject(attack.AttackerEntityId, attack.LaneId, out var attacker)
                    || !IsValidCombatTarget(attack.TargetEntityId, attack.TargetPlayerId, attack.LaneId))
                {
                    AddReceipt(attack.Id, IntentReceiptStatus.Rejected, "invalid-attack-declaration", attack.AttackerEntityId, null, null);
                    return;
                }

                AddReceipt(attack.Id, IntentReceiptStatus.Applied, "declare-attack", attack.AttackerEntityId, attacker.ControllerId, null);
                AddEvent(new EventDraft(
                    DomainEventKind.AttackDeclared,
                    attack.AttackerEntityId,
                    attacker.ControllerId,
                    null,
                    null,
                    null,
                    "attack",
                    null,
                    attack.TargetEntityId,
                    attack.LaneId,
                    attack.TargetPlayerId));
                break;
            default:
                AddReceipt(group.Intents[0].Id, IntentReceiptStatus.Rejected, "wrong-intent-type", null, null, null);
                break;
        }
    }

    public MatchState CreateCommittedState()
    {
        foreach (var pair in _heroHealth.OrderBy(value => value.Key))
        {
            var index = IndexOfPlayer(_players, pair.Key);
            _players = _players.SetItem(index, _players[index] with
            {
                HeroHealth = pair.Value,
                HeroMaximumHealth = _heroMaximumHealth[pair.Key]
            });
        }

        var entities = _entities.Values.OrderBy(value => value.Id.Value).ToImmutableArray();
        foreach (var entity in entities.OrderBy(value => value.Id.Value))
        {
            var dies = entity switch
            {
                MinionEntityState minion => AuthoritativeNumbers.IsMinionDead(
                    minion.CurrentHealth,
                    minion.MaximumHealth,
                    minion.IsDead),
                FieldEntityState field => field.IsDestroyed || field.Lifetime.IsDestroyed,
                _ => false
            };
            if (!dies)
            {
                continue;
            }

            RemoveEntity(entity, EntityRemovalReason.Death, isDeath: true);
        }

        var playerOneDead = AuthoritativeNumbers.IsHeroDead(_heroHealth[PlayerId.One]);
        var playerTwoDead = AuthoritativeNumbers.IsHeroDead(_heroHealth[PlayerId.Two]);
        var outcome = playerOneDead && playerTwoDead
            ? MatchOutcome.Draw
            : playerOneDead
                ? MatchOutcome.PlayerTwoWon
                : playerTwoDead
                    ? MatchOutcome.PlayerOneWon
                    : MatchOutcome.None;
        var status = outcome == MatchOutcome.None ? _state.Status : MatchStatus.Finished;
        if (outcome != MatchOutcome.None)
        {
            AddEvent(new EventDraft(DomainEventKind.MatchEnded, null, null, null, null, null, outcome.ToString()));
        }

        return _state with
        {
            Status = status,
            Outcome = outcome,
            Stage = _stage,
            Turn = _turn,
            Players = _players,
            Lanes = _lanes,
            CardInstances = _cardInstances,
            Entities = _entities.Values.OrderBy(value => value.Id.Value).ToImmutableArray(),
            Tombstones = _tombstones.OrderBy(value => value.Id.Value).ToImmutableArray(),
            NextEntityId = new EntityId(_nextEntityId),
            NextCardInstanceId = new CardInstanceId(_nextCardInstanceId),
            NextTombstoneId = new TombstoneId(_nextTombstoneId)
        };
    }

    public ReceiptDraft GetReceipt(IntentId intentId) => _receipts[intentId];

    public void AddEvent(EventDraft value) => _events.Add(value);

    public void ClearEvents() => _events.Clear();

    private void RejectAll(
        IntentConflictGroup group,
        string detailCode,
        EntityId? entityId,
        PlayerId? playerId)
    {
        foreach (var intent in group.Intents)
        {
            AddReceipt(intent.Id, IntentReceiptStatus.Rejected, detailCode, entityId, playerId, null);
        }
    }

    private void AddReceipt(
        IntentId intentId,
        IntentReceiptStatus status,
        string detailCode,
        EntityId? entityId,
        PlayerId? playerId,
        long? appliedValue,
        CardInstanceId? cardInstanceId = null)
    {
        _receipts.Add(intentId, new ReceiptDraft(status, detailCode, entityId, playerId, appliedValue, cardInstanceId));
    }

    private BigInteger AddCappedReceipts(
        IEnumerable<(IntentId Id, long Requested)> requests,
        BigInteger capacity,
        string detailCode,
        EntityId? entityId,
        PlayerId? playerId)
    {
        var total = BigInteger.Zero;
        capacity = BigInteger.Max(BigInteger.Zero, capacity);
        foreach (var request in requests.OrderBy(value => value.Id.Value))
        {
            var applied = BigInteger.Min(request.Requested, capacity);
            capacity -= applied;
            total += applied;
            var appliedValue = (long)applied;
            var status = appliedValue == 0
                ? IntentReceiptStatus.NoOp
                : appliedValue == request.Requested
                    ? IntentReceiptStatus.Applied
                    : IntentReceiptStatus.PartiallyApplied;
            AddReceipt(request.Id, status, detailCode, entityId, playerId, appliedValue);
        }

        return total;
    }

    private void SetFrameError(string detailCode)
    {
        HasFrameError = true;
        FrameErrorCode = detailCode;
    }

    private void RemoveEntity(
        BattlefieldEntityState entity,
        EntityRemovalReason reason,
        bool isDeath)
    {
        var tombstoneId = new TombstoneId(checked(_nextTombstoneId++));
        var prototypeId = _cardInstances.Single(value => value.Id == entity.CardInstanceId).CurrentPrototypeId;
        var tombstone = entity switch
        {
            MinionEntityState minion => new EntityTombstone(
                tombstoneId,
                minion.Id,
                minion.CardInstanceId,
                prototypeId,
                minion.OwnerId,
                minion.ControllerId,
                minion.LaneId,
                reason,
                minion.Attack,
                minion.CurrentHealth,
                minion.MaximumHealth,
                null,
                _frameId),
            FieldEntityState field => new EntityTombstone(
                tombstoneId,
                field.Id,
                field.CardInstanceId,
                prototypeId,
                field.OwnerId,
                field.ControllerId,
                field.LaneId,
                reason,
                null,
                null,
                null,
                field.Lifetime.VisibleEnergy,
                _frameId),
            _ => throw new InvalidOperationException($"Unsupported entity type '{entity.GetType().Name}'.")
        };
        _tombstones = _tombstones.Add(tombstone with { FinalEntity = entity });
        _cardInstances = SetCardZone(_cardInstances, entity.CardInstanceId, CardZone.Discard);
        _players = MoveCardToDiscard(_players, entity.OwnerId, entity.CardInstanceId);
        if (reason == EntityRemovalReason.Banish)
        {
            _cardInstances = SetCardZone(_cardInstances, entity.CardInstanceId, CardZone.Removed);
            var index = IndexOfPlayer(_players, entity.OwnerId);
            _players = _players.SetItem(index, _players[index] with
            {
                Discard = _players[index].Discard.Remove(entity.CardInstanceId),
                Removed = _players[index].Removed.Add(entity.CardInstanceId)
            });
        }
        _lanes = VacateSlot(_lanes, entity);
        _entities.Remove(entity.Id);
        if (isDeath)
        {
            AddEvent(new EventDraft(
                DomainEventKind.EntityDied,
                entity.Id,
                entity.ControllerId,
                tombstoneId,
                null,
                null,
                "death"));
        }

        AddEvent(new EventDraft(
            reason == EntityRemovalReason.Banish ? DomainEventKind.EntityBanished : DomainEventKind.EntityLeft,
            entity.Id,
            entity.ControllerId,
            tombstoneId,
            null,
            null,
            reason.ToString().ToLowerInvariant()));
    }

    private void AdvanceStage(AdvanceStageIntent intent)
    {
        if (_stage != intent.ExpectedStage || !IsAllowedStageTransition(intent.ExpectedStage, intent.NextStage))
        {
            AddReceipt(intent.Id, IntentReceiptStatus.Rejected, "invalid-stage-transition", null, null, null);
            return;
        }

        var previous = _stage;
        _stage = intent.NextStage;
        AddReceipt(intent.Id, IntentReceiptStatus.Applied, "advance-stage", null, null, null);
        AddEvent(new EventDraft(
            DomainEventKind.MatchStageChanged,
            null,
            null,
            null,
            (int)previous,
            (int)_stage,
            "stage"));
    }

    private void BeginNextTurn(BeginNextTurnIntent intent)
    {
        if (_stage != MatchStage.Cleanup || _players.Any(value => !value.Planning.IsEmpty))
        {
            AddReceipt(intent.Id, IntentReceiptStatus.Rejected, "cannot-begin-next-turn", null, null, null);
            return;
        }

        var nextTurnValue = (long)_turn + 1;
        if (nextTurnValue > int.MaxValue)
        {
            SetFrameError("arithmetic-overflow");
            return;
        }

        foreach (var playerId in new[] { PlayerId.One, PlayerId.Two })
        {
            var index = IndexOfPlayer(_players, playerId);
            var player = _players[index];
            var maximumCostValue = BigInteger.Min(
                _state.Protocol.Definition.MaxCostLimit,
                new BigInteger(player.MaxCost) + _state.Protocol.Definition.MaxCostGrowthPerTurn);
            if (!TryInt64(maximumCostValue, out var maximumCost)
                || !TryInt64(maximumCostValue + player.NextTurnCost, out var replenishedCost))
            {
                SetFrameError("arithmetic-overflow");
                return;
            }

            _players = _players.SetItem(index, player with
            {
                CurrentCost = replenishedCost,
                NextTurnCost = 0,
                MaxCost = maximumCost,
                TurnSubmitted = false
            });
        }

        var previousStage = _stage;
        _turn = (int)nextTurnValue;
        _stage = MatchStage.Planning;
        AddReceipt(intent.Id, IntentReceiptStatus.Applied, "begin-next-turn", null, null, _turn);
        AddEvent(new EventDraft(
            DomainEventKind.MatchStageChanged,
            null,
            null,
            null,
            (int)previousStage,
            (int)_stage,
            "stage"));
        AddEvent(new EventDraft(DomainEventKind.TurnStarted, null, null, null, _turn - 1, _turn, "turn"));
    }

    private static bool IsAllowedStageTransition(MatchStage previous, MatchStage next) =>
        (previous, next) is
            (MatchStage.ReadyToResolve, MatchStage.Deployment)
            or (MatchStage.Deployment, MatchStage.EntryEffects)
            or (MatchStage.EntryEffects, MatchStage.FastSpells)
            or (MatchStage.FastSpells, MatchStage.Movement)
            or (MatchStage.Movement, MatchStage.PreCombatCharge)
            or (MatchStage.PreCombatCharge, MatchStage.Combat)
            or (MatchStage.Combat, MatchStage.SlowSpells)
            or (MatchStage.SlowSpells, MatchStage.EndTurnEffects)
            or (MatchStage.EndTurnEffects, MatchStage.Cleanup);

    private bool TryGetCombatSubject(EntityId entityId, LaneId laneId, out MinionEntityState minion)
    {
        minion = null!;
        if (!_entities.TryGetValue(entityId, out var entity)
            || entity is not MinionEntityState found
            || found.LaneId != laneId)
        {
            return false;
        }

        minion = found;
        return true;
    }

    private bool IsValidCombatTarget(EntityId? targetEntityId, PlayerId? targetPlayerId, LaneId laneId)
    {
        if (targetEntityId.HasValue == targetPlayerId.HasValue)
        {
            return false;
        }

        return targetEntityId is not { } entityId
               || (_entities.TryGetValue(entityId, out var target) && target is MinionEntityState && target.LaneId == laneId);
    }

    private void DeployMinion(DeployMinionIntent intent)
    {
        if (_state.Lanes.Any(value => value.Id == intent.LaneId) && LaneStatusRules.IsLocked(_state, intent.LaneId))
        { AddReceipt(intent.Id, IntentReceiptStatus.Rejected, "lane-locked", null, intent.PlayerId, null); return; }
        if (_stage != MatchStage.Deployment
            || !TryGetPlannedCard(intent.CardInstanceId, intent.PlayerId, intent.LaneId, PlannedActionKind.Minion, out var card)
            || card is null
            || CardInstanceRules.Definition(_state, card) is not MinionCardDefinition minionDefinition)
        {
            AddReceipt(intent.Id, IntentReceiptStatus.Rejected, "invalid-minion-deployment", null, intent.PlayerId, null);
            return;
        }

        var lane = _lanes.SingleOrDefault(value => value.Id == intent.LaneId);
        if (lane is null)
        {
            AddReceipt(intent.Id, IntentReceiptStatus.Rejected, "lane-not-found", null, intent.PlayerId, null);
            return;
        }

        var playerLane = GetPlayerLane(lane, intent.PlayerId);
        if (playerLane.MinionEntityId is { } occupyingId)
        {
            if (!_entities.TryGetValue(occupyingId, out var occupyingEntity)
                || occupyingEntity is not MinionEntityState occupyingMinion
                || (!HasKeyword(minionDefinition.Keywords, MinionKeywordKind.Replace)
                    && !HasKeyword(occupyingMinion.Keywords, MinionKeywordKind.Replaceable)))
            {
                AddReceipt(intent.Id, IntentReceiptStatus.Rejected, "minion-slot-occupied", null, intent.PlayerId, null);
                return;
            }

            RemoveEntity(occupyingMinion, EntityRemovalReason.Replace, isDeath: false);
            lane = _lanes.Single(value => value.Id == intent.LaneId);
            playerLane = GetPlayerLane(lane, intent.PlayerId);
        }

        var entityId = new EntityId(checked(_nextEntityId++));
        var slowTurns = minionDefinition.Keywords
            .Where(value => value.Kind == MinionKeywordKind.Slow)
            .Select(value => value.Parameter)
            .DefaultIfEmpty(0)
            .Max();
        var entity = new MinionEntityState(
            entityId,
            card.Id,
            intent.PlayerId,
            intent.PlayerId,
            intent.LaneId,
            minionDefinition.Attack,
            minionDefinition.Health,
            minionDefinition.Health,
            minionDefinition.Keywords,
            slowTurns,
            _state.Turn,
            false);
        _state.Content.TryGetCard(card.CurrentPrototypeId, out var prototype);
        _entities.Add(entityId, entity with { StoredCharge = minionDefinition.StoredCharge, BaseAttack = ((MinionCardDefinition)prototype!).Attack });
        _lanes = OccupySlot(_lanes, lane, playerLane with { MinionEntityId = entityId });
        FinishDeployment(card, intent.PlayerId);
        AddReceipt(intent.Id, IntentReceiptStatus.Applied, "deploy-minion", entityId, intent.PlayerId, null);
        AddEvent(new EventDraft(DomainEventKind.EntityEntered, entityId, intent.PlayerId,
            _tombstones.LastOrDefault(value => value.FrameId == _frameId && value.ControllerId == intent.PlayerId && value.LaneId == intent.LaneId
                && value.FinalEntity is MinionEntityState && value.Reason == EntityRemovalReason.Replace)?.Id, null, null, "minion"));
    }

    private void DeployField(DeployFieldIntent intent)
    {
        if (_state.Lanes.Any(value => value.Id == intent.LaneId) && LaneStatusRules.IsLocked(_state, intent.LaneId))
        { AddReceipt(intent.Id, IntentReceiptStatus.Rejected, "lane-locked", null, intent.PlayerId, null); return; }
        if (_stage != MatchStage.Deployment
            || !TryGetPlannedCard(intent.CardInstanceId, intent.PlayerId, intent.LaneId, PlannedActionKind.Field, out var card)
            || card is null
            || CardInstanceRules.Definition(_state, card) is not FieldCardDefinition fieldDefinition)
        {
            AddReceipt(intent.Id, IntentReceiptStatus.Rejected, "invalid-field-deployment", null, intent.PlayerId, null);
            return;
        }

        var lane = _lanes.SingleOrDefault(value => value.Id == intent.LaneId);
        if (lane is null)
        {
            AddReceipt(intent.Id, IntentReceiptStatus.Rejected, "lane-not-found", null, intent.PlayerId, null);
            return;
        }

        var playerLane = GetPlayerLane(lane, intent.PlayerId);
        if (playerLane.FieldEntityId is { } occupyingId)
        {
            if (!_entities.TryGetValue(occupyingId, out var occupyingEntity)
                || occupyingEntity is not FieldEntityState occupyingField
                || (!fieldDefinition.Keywords.Contains(FieldKeywordKind.Replace)
                    && !occupyingField.Keywords.Contains(FieldKeywordKind.Replaceable)))
            {
                AddReceipt(intent.Id, IntentReceiptStatus.Rejected, "field-slot-occupied", null, intent.PlayerId, null);
                return;
            }

            RemoveEntity(occupyingField, EntityRemovalReason.Replace, isDeath: false);
            lane = _lanes.Single(value => value.Id == intent.LaneId);
            playerLane = GetPlayerLane(lane, intent.PlayerId);
        }

        IFieldLifetimeState lifetime = fieldDefinition.Lifetime switch
        {
            FiniteFieldLifetimeDefinition finite => new FiniteFieldLifetimeState(finite.InitialEnergy),
            PermanentFieldLifetimeDefinition => new PermanentFieldLifetimeState(),
            _ => throw new InvalidOperationException($"Unsupported field lifetime '{fieldDefinition.Lifetime.GetType().Name}'.")
        };
        var entityId = new EntityId(checked(_nextEntityId++));
        var entity = new FieldEntityState(
            entityId,
            card.Id,
            intent.PlayerId,
            intent.PlayerId,
            intent.LaneId,
            lifetime,
            fieldDefinition.Keywords,
            _state.Turn,
            false);
        _entities.Add(entityId, entity with { StoredCharge = fieldDefinition.StoredCharge });
        _lanes = OccupySlot(_lanes, lane, playerLane with { FieldEntityId = entityId });
        FinishDeployment(card, intent.PlayerId);
        AddReceipt(intent.Id, IntentReceiptStatus.Applied, "deploy-field", entityId, intent.PlayerId, null);
        AddEvent(new EventDraft(DomainEventKind.EntityEntered, entityId, intent.PlayerId,
            _tombstones.LastOrDefault(value => value.FrameId == _frameId && value.ControllerId == intent.PlayerId && value.LaneId == intent.LaneId
                && value.FinalEntity is FieldEntityState && value.Reason == EntityRemovalReason.Replace)?.Id, null, null, "field"));
    }

    private string? GetMoveRejection(MoveMinionIntent intent)
    {
        if (intent.AlternativeTargetLaneId.HasValue && !MovementRules.TryGetRandomCandidates(_state, intent, out _))
        {
            return "invalid-movement-random-choice";
        }

        if (Math.Abs((long)intent.TargetLaneId.Value - intent.FromLaneId.Value) != 1
            || !_entities.TryGetValue(intent.EntityId, out var entity)
            || entity is not MinionEntityState minion
            || minion.ControllerId != intent.PlayerId
            || minion.LaneId != intent.FromLaneId)
        {
            return "invalid-move";
        }

        var snapshotSourceLane = _state.Lanes.SingleOrDefault(value => value.Id == intent.FromLaneId);
        var snapshotTargetLane = _state.Lanes.SingleOrDefault(value => value.Id == intent.TargetLaneId);
        if (snapshotSourceLane is null
            || snapshotTargetLane is null
            || GetPlayerLane(snapshotSourceLane, intent.PlayerId).MinionEntityId != intent.EntityId
            || GetPlayerLane(snapshotTargetLane, intent.PlayerId).MinionEntityId is not null)
        {
            return "move-slot-unavailable";
        }

        if (!LaneStatusRules.CanMove(_state, intent.FromLaneId, intent.TargetLaneId)) { return "lane-locked"; }

        return null;
    }

    private void MoveMinion(MoveMinionIntent intent)
    {
        var minion = (MinionEntityState)_entities[intent.EntityId];
        var currentSourceLane = _lanes.Single(value => value.Id == intent.FromLaneId);
        var currentTargetLane = _lanes.Single(value => value.Id == intent.TargetLaneId);
        var sourcePlayerLane = GetPlayerLane(currentSourceLane, intent.PlayerId);
        var targetPlayerLane = GetPlayerLane(currentTargetLane, intent.PlayerId);
        _lanes = OccupySlot(_lanes, currentSourceLane, sourcePlayerLane with { MinionEntityId = null });
        currentTargetLane = _lanes.Single(value => value.Id == intent.TargetLaneId);
        _lanes = OccupySlot(_lanes, currentTargetLane, targetPlayerLane with { MinionEntityId = intent.EntityId });
        _entities[intent.EntityId] = minion with { LaneId = intent.TargetLaneId };
        AddReceipt(intent.Id, IntentReceiptStatus.Applied, "move-minion", intent.EntityId, intent.PlayerId, null);
        AddEvent(new EventDraft(
            DomainEventKind.EntityMoved,
            intent.EntityId,
            intent.PlayerId,
            null,
            intent.FromLaneId.Value,
            intent.TargetLaneId.Value,
            "move"));
    }

    private bool TryGetPlannedCard(
        CardInstanceId cardInstanceId,
        PlayerId playerId,
        LaneId laneId,
        PlannedActionKind kind,
        out CardInstanceState? card)
    {
        card = _cardInstances.SingleOrDefault(value => value.Id == cardInstanceId);
        if (card is null || card.OwnerId != playerId || card.Zone != CardZone.Planning)
        {
            return false;
        }

        var player = _players.Single(value => value.Id == playerId);
        return player.Planning.Any(value =>
            value.CardInstanceId == cardInstanceId
            && value.Kind == kind
            && value.LaneId == laneId);
    }

    private void FinishDeployment(CardInstanceState card, PlayerId playerId)
    {
        _cardInstances = SetCardZone(_cardInstances, card.Id, CardZone.Battlefield);
        var playerIndex = IndexOfPlayer(_players, playerId);
        var player = _players[playerIndex];
        _players = _players.SetItem(playerIndex, player with
        {
            Planning = player.Planning.Where(value => value.CardInstanceId != card.Id).ToImmutableArray()
        });
    }

    private static bool HasKeyword(
        ImmutableArray<MinionKeywordDefinition> keywords,
        MinionKeywordKind kind) => keywords.Any(value => value.Kind == kind);

    private static PlayerLaneState GetPlayerLane(LaneState lane, PlayerId playerId) =>
        playerId == PlayerId.One ? lane.PlayerOne : lane.PlayerTwo;

    private static ImmutableArray<LaneState> OccupySlot(
        ImmutableArray<LaneState> lanes,
        LaneState lane,
        PlayerLaneState playerLane)
    {
        var updated = playerLane.PlayerId == PlayerId.One
            ? lane with { PlayerOne = playerLane }
            : lane with { PlayerTwo = playerLane };
        for (var index = 0; index < lanes.Length; index++)
        {
            if (lanes[index].Id == lane.Id)
            {
                return lanes.SetItem(index, updated);
            }
        }

        throw new InvalidOperationException($"Lane '{lane.Id}' is missing from match state.");
    }

    private static IntentReceiptStatus EffectiveStatus(long value) =>
        value == 0 ? IntentReceiptStatus.NoOp : IntentReceiptStatus.Applied;

    private static bool TryInt64(BigInteger value, out long result)
    {
        if (value < long.MinValue || value > long.MaxValue)
        {
            result = default;
            return false;
        }

        result = (long)value;
        return true;
    }

    private static int IndexOfPlayer(ImmutableArray<PlayerState> players, PlayerId playerId)
    {
        for (var index = 0; index < players.Length; index++)
        {
            if (players[index].Id == playerId)
            {
                return index;
            }
        }

        throw new InvalidOperationException($"Player '{playerId}' is missing from match state.");
    }

    private static ImmutableArray<PlayerState> MoveCardToDiscard(
        ImmutableArray<PlayerState> players,
        PlayerId ownerId,
        CardInstanceId cardInstanceId)
    {
        var index = IndexOfPlayer(players, ownerId);
        var player = players[index];
        return players.SetItem(index, player with
        {
            Discard = player.Discard.Add(cardInstanceId).OrderBy(value => value.Value).ToImmutableArray()
        });
    }

    private static ImmutableArray<CardInstanceState> SetCardZone(
        ImmutableArray<CardInstanceState> instances,
        CardInstanceId cardInstanceId,
        CardZone zone)
    {
        for (var index = 0; index < instances.Length; index++)
        {
            if (instances[index].Id == cardInstanceId)
            {
                return instances.SetItem(index, instances[index] with { Zone = zone });
            }
        }

        throw new InvalidOperationException($"Card instance '{cardInstanceId}' is missing from match state.");
    }

    private static ImmutableArray<LaneState> VacateSlot(
        ImmutableArray<LaneState> lanes,
        BattlefieldEntityState entity)
    {
        for (var index = 0; index < lanes.Length; index++)
        {
            if (lanes[index].Id != entity.LaneId)
            {
                continue;
            }

            var lane = lanes[index];
            if (entity.ControllerId == PlayerId.One)
            {
                var slot = lane.PlayerOne;
                lane = entity is MinionEntityState
                    ? lane with { PlayerOne = slot with { MinionEntityId = null } }
                    : lane with { PlayerOne = slot with { FieldEntityId = null } };
            }
            else
            {
                var slot = lane.PlayerTwo;
                lane = entity is MinionEntityState
                    ? lane with { PlayerTwo = slot with { MinionEntityId = null } }
                    : lane with { PlayerTwo = slot with { FieldEntityId = null } };
            }

            return lanes.SetItem(index, lane);
        }

        throw new InvalidOperationException($"Lane '{entity.LaneId}' is missing from match state.");
    }
}

internal sealed record ReceiptDraft(
    IntentReceiptStatus Status,
    string DetailCode,
    EntityId? EntityId,
    PlayerId? PlayerId,
    long? AppliedValue,
    CardInstanceId? CardInstanceId = null,
    ReceiptOutputs? Outputs = null);

internal sealed record EventDraft(
    DomainEventKind Kind,
    EntityId? EntityId,
    PlayerId? PlayerId,
    TombstoneId? TombstoneId,
    long? PreviousValue,
    long? CurrentValue,
    string DetailCode,
    CardInstanceId? CardInstanceId = null,
    EntityId? TargetEntityId = null,
    LaneId? LaneId = null,
    PlayerId? TargetPlayerId = null);
