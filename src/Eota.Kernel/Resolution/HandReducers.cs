using System.Collections.Immutable;
using Eota.Kernel.Effects;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Protocols;

namespace Eota.Kernel.Resolution;

internal sealed partial class ReductionContext
{
    private sealed record HandAllocation(IntentId Id, PlayerId Player, int Priority, CardPrototypeId Prototype,
        CardInstanceId? Drawn, BattlefieldEntityState? Returning, ImmutableArray<IntentId> Receivers);
    private readonly List<HandAllocation> _pendingReturns = [];
    private readonly List<HandCardIntent> _handRequests = [];

    internal void QueueHandRequests(IntentConflictGroup group)
    {
        foreach (var intent in group.Intents)
        {
            if (intent is HandCardIntent request && group.Key.StableTargetId == request.PlayerId.Value && Enum.IsDefined(request.Kind))
            { _handRequests.Add(request); }
            else { AddReceipt(intent.Id, IntentReceiptStatus.Rejected, "invalid-hand-request", null, null, null); }
        }
    }

    private void CompleteHandRequests()
    {
        var allocations = new List<HandAllocation>(_pendingReturns);
        // Allocate deck identities before resolving hand capacity; a rejected draw still burns that identity.
        foreach (var request in _handRequests.OrderBy(value => value.PlayerId).ThenBy(value => value.AllocationKey).ThenBy(value => value.Id.Value))
        {
            if (request.Kind == HandRequestKind.Generate)
            {
                if (request.PrototypeId is not { } prototype || !_state.Content.TryGetCard(prototype, out _) || request.Filter is not null)
                { AddReceipt(request.Id, IntentReceiptStatus.Rejected, "invalid-generated-prototype", null, request.PlayerId, null); continue; }
                allocations.Add(new HandAllocation(request.Id, request.PlayerId, 1, prototype, null, null, [request.Id]));
                continue;
            }
            if (request.PrototypeId is not null)
            { AddReceipt(request.Id, IntentReceiptStatus.Rejected, "invalid-draw-request", null, request.PlayerId, null); continue; }
            var index = IndexOfPlayer(_players, request.PlayerId);
            var player = _players[index];
            var card = player.Deck.Select(id => _cardInstances.Single(value => value.Id == id))
                .FirstOrDefault(value => CardInstanceRules.Matches(CardInstanceRules.Definition(_state, value), request.Filter));
            if (card is null)
            {
                if (player.Deck.IsEmpty && _state.Protocol.Definition.DeckExhaustionPolicy != DeckExhaustionPolicy.NoFatigue)
                { ApplyFatigue(request, index); }
                else { AddReceipt(request.Id, IntentReceiptStatus.NoOp, "draw-no-match", null, request.PlayerId, 0); }
                continue;
            }
            _players = _players.SetItem(index, player with { Deck = player.Deck.Remove(card.Id) });
            allocations.Add(new HandAllocation(request.Id, request.PlayerId, 2, card.CurrentPrototypeId, card.Id, null, [request.Id]));
        }
        foreach (var playerGroup in allocations.GroupBy(value => value.Player).OrderBy(value => value.Key))
        {
            var index = IndexOfPlayer(_players, playerGroup.Key);
            var available = Math.Max(0, _state.Protocol.Definition.HandLimit - _players[index].Hand.Length);
            foreach (var priority in playerGroup.GroupBy(value => value.Priority).OrderBy(value => value.Key))
            {
                var candidates = priority.OrderBy(value => value.Id.Value).ToList();
                var selected = new HashSet<IntentId>();
                if (candidates.Count <= available) { selected.UnionWith(candidates.Select(value => value.Id)); }
                else if (available > 0)
                {
                    for (var seat = 0; seat < available; seat++)
                    {
                        var winner = Choose($"hand-{playerGroup.Key.Value}-{priority.Key}", (ulong)seat,
                            candidates.Select(value => value.Id).ToImmutableArray());
                        selected.Add(winner);
                        candidates.RemoveAll(value => value.Id == winner);
                    }
                }
                available -= selected.Count;
                foreach (var request in priority.OrderBy(value => value.Id.Value)) { ApplyHandAllocation(request, selected.Contains(request.Id)); }
            }
        }
    }

    private void ApplyFatigue(HandCardIntent request, int playerIndex)
    {
        var player = _players[playerIndex]; var count = checked(player.FatigueCount + 1);
        var previous = _heroHealth[player.Id];
        var instant = _state.Protocol.Definition.DeckExhaustionPolicy == DeckExhaustionPolicy.InstantDeath;
        // Execute after ordinary health reduction so same-frame healing cannot undo the kill.
        // Complete all hand allocations; CreateCommittedState decides the outcome at frame end.
        var current = instant ? Math.Min(0, previous) : checked(previous - count);
        _players = _players.SetItem(playerIndex, player with { FatigueCount = count });
        _heroHealth[player.Id] = current;
        AddReceipt(request.Id, IntentReceiptStatus.Applied, instant ? "fatigue-death" : "fatigue-damage", null, player.Id, instant ? null : count);
        AddEvent(new EventDraft(DomainEventKind.HeroHealthChanged, null, player.Id, null, previous, current,
            instant ? "fatigue-death" : "fatigue-damage"));
    }

    private void ApplyHandAllocation(HandAllocation request, bool accepted)
    {
        var index = IndexOfPlayer(_players, request.Player);
        var entity = request.Returning;
        CardInstanceId? entered = null;
        if (accepted)
        {
            if (entity is not null) { RemoveEntity(entity, EntityRemovalReason.Return, false); }
            entered = request.Drawn ?? CreateCard(request.Prototype, request.Player, CardZone.Hand).Id;
            if (request.Drawn is { } drawn) { _cardInstances = SetCardZone(_cardInstances, drawn, CardZone.Hand); }
            _players = _players.SetItem(index, _players[index] with { Hand = _players[index].Hand.Add(entered.Value) });
            AddEvent(new EventDraft(entity is not null ? DomainEventKind.CardReturned : request.Drawn is not null ? DomainEventKind.CardDrawn : DomainEventKind.CardGenerated,
                entity?.Id, request.Player, null, null, null, "hand-entry", entered));
        }
        else if (request.Drawn is { } burned)
        {
            _cardInstances = SetCardZone(_cardInstances, burned, CardZone.Removed);
            _players = _players.SetItem(index, _players[index] with { Removed = _players[index].Removed.Add(burned) });
            AddEvent(new EventDraft(DomainEventKind.CardBurned, null, request.Player, null, null, null, "hand-capacity", burned));
        }
        else if (entity is not null)
        {
            // Capacity failure kills the returning entity. Commit removes all dying entities
            // together and emits the ordinary death/leave facts for the next trigger frame.
            _entities[entity.Id] = entity switch
            {
                MinionEntityState minion => minion with { IsDead = true },
                FieldEntityState field => field with { IsDestroyed = true },
                _ => throw new InvalidOperationException("Only battlefield entities can return to hand.")
            };
        }
        var oldTarget = entity is null ? (EffectTarget?)null : new EffectTarget(entity is MinionEntityState ? EffectTargetType.Minion : EffectTargetType.Field, entity.Id.Value);
        var cardTarget = entered is null ? (EffectTarget?)null : new EffectTarget(EffectTargetType.Card, entered.Value.Value);
        var outputs = new ReceiptOutputs(cardTarget is { } card ? [card] : [],
            cardTarget is { } created && request.Drawn is null ? [created] : [],
            oldTarget is { } removed ? [removed] : [],
            request.Drawn is { } fromDeck ? [fromDeck] : [], entered is { } hand ? [hand] : [],
            !accepted && request.Drawn is { } burn ? [burn] : [], !accepted && request.Drawn is null ? [request.Prototype] : []);
        foreach (var id in request.Receivers)
        {
            AddReceipt(id, accepted ? IntentReceiptStatus.Applied : IntentReceiptStatus.Rejected,
                accepted ? entity is not null ? "return" : request.Drawn is not null ? "draw" : "generate" : "hand-capacity", entity?.Id, request.Player,
                accepted ? 1 : 0, entered ?? request.Drawn);
            _receipts[id] = _receipts[id] with { Outputs = outputs };
        }
    }
}
