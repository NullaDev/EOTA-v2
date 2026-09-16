using System.Collections.Immutable;
using Eota.Kernel.Content;
using Eota.Kernel.Determinism;
using Eota.Kernel.Effects;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Rules;

namespace Eota.Kernel.Resolution;

internal sealed partial class ReductionContext
{
    private readonly List<IntentConflictGroup> _lifecycleGroups = [];
    private readonly List<ConflictRandomChoice> _conflictChoices = [];
    private ImmutableArray<ConflictRandomChoice> _plannedConflictChoices;
    private GlobalRuleRngState _extendedRng;
    private ulong _nextCardInstanceId;

    internal GlobalRuleRngState ExtendedRng => _extendedRng;
    internal ImmutableArray<ConflictRandomChoice> ConflictChoices => _conflictChoices.ToImmutableArray();

    internal void ConfigureExtendedConflicts(GlobalRuleRngState rng, ImmutableArray<ConflictRandomChoice> choices = default)
    {
        _extendedRng = rng;
        _plannedConflictChoices = choices;
        _nextCardInstanceId = _state.NextCardInstanceId.Value;
    }

    private IntentId Choose(string domain, ulong target, ImmutableArray<IntentId> candidates)
    {
        if (!_plannedConflictChoices.IsDefault)
        {
            var planned = _plannedConflictChoices.Single(value => value.Domain == domain && value.TargetId == target);
            if (!planned.Candidates.SequenceEqual(candidates)) { throw new InvalidOperationException("Conflict candidate drift between Plan and Commit."); }
            return planned.SelectedIntentId;
        }
        var sample = GlobalRuleRng.NextIndex(_extendedRng, candidates.Length);
        var result = candidates[sample.Index];
        _conflictChoices.Add(new ConflictRandomChoice(domain, target, candidates, result, _extendedRng.SampleCount, sample.SamplesConsumed));
        _extendedRng = sample.State;
        return result;
    }

    internal void CompleteLifecycleIntents()
    {
        foreach (var group in _lifecycleGroups.OrderBy(value => value.Key))
        {
            var id = new EntityId(group.Key.StableTargetId);
            if (!_entities.TryGetValue(id, out var entity))
            {
                foreach (var intent in group.Intents.OfType<LifecycleEffectIntent>().Where(value => !_receipts.ContainsKey(value.Id)))
                { AddReceipt(intent.Id, IntentReceiptStatus.Rejected, "entity-not-found", id, null, null); }
                continue;
            }
            var candidates = group.Intents.OfType<LifecycleEffectIntent>().Where(value => !_receipts.ContainsKey(value.Id)).ToArray();
            var valid = candidates.Where(value => ValidLifecycle(value, entity)).ToArray();
            foreach (var invalid in candidates.Except(valid))
            { AddReceipt(invalid.Id, IntentReceiptStatus.Rejected, "invalid-lifecycle-target", id, null, null); }
            if (valid.Length == 0) { continue; }
            var banished = valid.Any(value => value.Operation == LifecycleOperation.Banish);
            if (banished)
            {
                foreach (var kill in group.Intents.Where(value => value is KillMinionIntent or DestroyFieldIntent))
                { _receipts[kill.Id] = new ReceiptDraft(IntentReceiptStatus.Rejected, "banish-wins", id, null, null); }
            }
            var dying = entity switch
            {
                MinionEntityState minion => AuthoritativeNumbers.IsMinionDead(minion.CurrentHealth, minion.MaximumHealth, minion.IsDead),
                FieldEntityState field => field.IsDestroyed || field.Lifetime.IsDestroyed,
                _ => false
            };
            if (!banished && dying)
            {
                foreach (var intent in valid) { AddReceipt(intent.Id, IntentReceiptStatus.Rejected, "death-wins", id, null, null); }
                continue;
            }
            var operation = valid.Max(value => value.Operation);
            var winners = valid.Where(value => value.Operation == operation).OrderBy(value => value.Id.Value).ToArray();
            foreach (var loser in valid.Except(winners))
            { AddReceipt(loser.Id, IntentReceiptStatus.Rejected, "lifecycle-priority", id, null, null); }
            if (operation == LifecycleOperation.Banish)
            {
                RemoveEntity(entity, EntityRemovalReason.Banish, false);
                foreach (var winner in winners) { AddReceipt(winner.Id, IntentReceiptStatus.Applied, "banish", id, entity.OwnerId, null); }
            }
            else if (operation == LifecycleOperation.Return)
            {
                var prototype = _cardInstances.Single(value => value.Id == entity.CardInstanceId).CurrentPrototypeId;
                _pendingReturns.Add(new HandAllocation(winners[0].Id, entity.OwnerId, 0, prototype, null, entity, winners.Select(value => value.Id).ToImmutableArray()));
            }
            else
            {
                var friendly = winners.Where(value => value.SourcePlayerId == entity.ControllerId).ToArray();
                var preferred = friendly.Length == 0 ? winners : friendly;
                var selected = Choose("lifecycle-" + operation, id.Value, preferred.Select(value => value.Id).ToImmutableArray());
                var chosen = preferred.Single(value => value.Id == selected);
                foreach (var loser in winners.Where(value => value.Id != selected))
                { AddReceipt(loser.Id, IntentReceiptStatus.Rejected, "lifecycle-not-selected", id, null, null); }
                _state.Content.TryGetCard(chosen.PrototypeId!.Value, out var definition);
                var cardId = entity.CardInstanceId;
                var entityId = entity.Id;
                if (operation == LifecycleOperation.Replace)
                {
                    RemoveEntity(entity, EntityRemovalReason.Replace, false);
                    cardId = CreateCard(definition!.Id, entity.OwnerId, CardZone.Battlefield).Id;
                    entityId = new EntityId(checked(_nextEntityId++));
                }
                else
                {
                    var instance = _cardInstances.Single(value => value.Id == cardId);
                    _cardInstances = _cardInstances.Replace(instance, instance with
                    {
                        CurrentPrototypeId = definition!.Id,
                        Cost = null,
                        Attack = null,
                        MaximumHealth = null,
                        Keywords = default
                    });
                }
                var updated = CreateEntity(definition!, entityId, cardId, entity.OwnerId, entity.ControllerId, entity.LaneId);
                if (operation == LifecycleOperation.Transform)
                {
                    updated = (updated, entity) switch
                    {
                        (MinionEntityState next, MinionEntityState old) => next with { EnteredOnTurn = old.EnteredOnTurn, SlowGeneration = checked(old.SlowGeneration + 1) },
                        (FieldEntityState next, FieldEntityState old) => next with { EnteredOnTurn = old.EnteredOnTurn },
                        _ => updated
                    };
                }
                _entities[entityId] = updated;
                var lane = _lanes.Single(value => value.Id == entity.LaneId);
                var side = GetPlayerLane(lane, entity.ControllerId);
                _lanes = OccupySlot(_lanes, lane, updated is MinionEntityState ? side with { MinionEntityId = entityId } : side with { FieldEntityId = entityId });
                AddReceipt(chosen.Id, IntentReceiptStatus.Applied, operation.ToString().ToLowerInvariant(), entityId, entity.ControllerId, null, cardId);
                AddEvent(new EventDraft(operation == LifecycleOperation.Transform ? DomainEventKind.EntityTransformed : DomainEventKind.EntityEntered,
                    entityId, entity.ControllerId, operation == LifecycleOperation.Replace ? _tombstones.Single(value => value.EntityId == entity.Id).Id : null,
                    null, null, operation.ToString().ToLowerInvariant(), cardId, LaneId: entity.LaneId));
            }
        }
    }

    private bool ValidLifecycle(LifecycleEffectIntent intent, BattlefieldEntityState entity)
    {
        if (intent.Operation is LifecycleOperation.Return or LifecycleOperation.Replace && LaneStatusRules.IsLocked(_state, entity.LaneId)) { return false; }
        if (!Enum.IsDefined(intent.Operation) || (intent.Target.Type == EffectTargetType.Minion) != (entity is MinionEntityState)
            || intent.Target.Type is not (EffectTargetType.Minion or EffectTargetType.Field)) { return false; }
        if (intent.Operation is LifecycleOperation.Banish or LifecycleOperation.Return) { return intent.PrototypeId is null; }
        if (intent.PrototypeId is not { } prototype || !_state.Content.TryGetCard(prototype, out var definition)
            || (entity is MinionEntityState ? definition is not MinionCardDefinition : definition is not FieldCardDefinition)) { return false; }
        return intent.Operation != LifecycleOperation.Replace || (entity, definition) switch
        {
            (MinionEntityState old, MinionCardDefinition next) => HasKeyword(old.Keywords, MinionKeywordKind.Replaceable) || HasKeyword(next.Keywords, MinionKeywordKind.Replace),
            (FieldEntityState old, FieldCardDefinition next) => old.Keywords.Contains(FieldKeywordKind.Replaceable) || next.Keywords.Contains(FieldKeywordKind.Replace),
            _ => false
        };
    }

    private CardInstanceState CreateCard(CardPrototypeId prototype, PlayerId owner, CardZone zone)
    {
        var card = new CardInstanceState(new CardInstanceId(checked(_nextCardInstanceId++)), prototype, prototype, owner, zone);
        _cardInstances = _cardInstances.Add(card);
        return card;
    }

    private BattlefieldEntityState CreateEntity(CardDefinition definition, EntityId id, CardInstanceId card,
        PlayerId owner, PlayerId controller, LaneId lane) => (definition switch
        {
            MinionCardDefinition minion => (BattlefieldEntityState)new MinionEntityState(id, card, owner, controller, lane, minion.Attack, minion.Health, minion.Health,
                minion.Keywords, minion.Keywords.Where(value => value.Kind == MinionKeywordKind.Slow).Select(value => value.Parameter).DefaultIfEmpty(0).Max(), _turn, false)
            { BaseAttack = minion.Attack },
            FieldCardDefinition field => new FieldEntityState(id, card, owner, controller, lane,
                field.Lifetime is FiniteFieldLifetimeDefinition finite ? new FiniteFieldLifetimeState(finite.InitialEnergy) : new PermanentFieldLifetimeState(),
                field.Keywords, _turn, false),
            _ => throw new InvalidOperationException("Battlefield prototype required.")
        }) with
        { StoredCharge = definition.StoredCharge };
}
