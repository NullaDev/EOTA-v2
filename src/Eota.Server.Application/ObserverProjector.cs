using System.Collections.Immutable;
using Eota.Kernel.Content;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;
using Eota.Kernel.Rules;
using Eota.Transport.Contracts;

namespace Eota.Server.Application;

public static class ObserverProjector
{
    public static ObserverView Project(MatchState state, Audience audience)
    {
        ArgumentNullException.ThrowIfNull(state);
        return audience switch
        {
            Audience.PlayerOne => ProjectPlayer(state, PlayerId.One, audience),
            Audience.PlayerTwo => ProjectPlayer(state, PlayerId.Two, audience),
            Audience.Spectator => ProjectPublic(state, audience, null),
            _ => throw new ArgumentOutOfRangeException(nameof(audience))
        };
    }

    private static ObserverView ProjectPlayer(MatchState state, PlayerId seat, Audience audience)
    {
        var player = state.Players.Single(value => value.Id == seat);
        var own = new PrivatePlayerView(seat.Value, player.CommandRevision, player.CurrentCost,
            player.Hand.OrderBy(value => value.Value).Select(id => Card(state, id)).ToImmutableArray(),
            player.Planning.OrderBy(value => value.Id.Ordinal).Select(value => new PlanView(
                value.Id.Ordinal, Card(state, value.CardInstanceId), value.Kind.ToString(), value.LaneId?.Value,
                value.ReservedCost)).ToImmutableArray(),
            player.Mulligan?.SelectedCards.OrderBy(value => value.Value).Select(value => value.Value).ToImmutableArray() ?? [],
            player.NextTurnCost)
        { PlanOptions = PlanOptions(state, player) };
        return ProjectPublic(state, audience, own);
    }

    private static ObserverView ProjectPublic(MatchState state, Audience audience, PrivatePlayerView? own) => new(
        audience, state.Protocol.Hash.ToString(), state.Content.Hash.ToString(), state.Status.ToString(),
        state.Outcome.ToString(), state.Stage.ToString(), state.Turn,
        state.Players.OrderBy(value => value.Id).Select(player => new PublicPlayerView(
            player.Id.Value, player.Profession.ToString(), player.HeroHealth, player.HeroMaximumHealth,
            player.MaxCost, player.Hand.Length + player.Planning.Length, player.Deck.Length,
            player.Removed.Length, player.Mulligan?.Submitted ?? player.TurnSubmitted,
            player.Discard.OrderBy(value => value.Value).Select(id => Card(state, id)).ToImmutableArray())
        { ActiveEffects = ActiveEffects(player.AttachedEffects), FatigueCount = player.FatigueCount }).ToImmutableArray(),
        state.Lanes.OrderBy(value => value.Id).Select(lane => new LaneView(
            lane.Id.Value, LaneSide(lane.PlayerOne), LaneSide(lane.PlayerTwo))
        {
            Frozen = Eota.Kernel.Effects.LaneStatusRules.IsFrozen(state, lane.Id),
            Locked = Eota.Kernel.Effects.LaneStatusRules.IsLocked(state, lane.Id),
            Statuses = lane.Statuses.OrderBy(value => value.Kind).Select(value => new LaneStatusView(value.Kind.ToString(), value.RemainingDuration)).ToImmutableArray()
        }).ToImmutableArray(),
        state.Entities.OrderBy(value => value.Id.Value).Select(entity => Entity(state, entity)).ToImmutableArray(), own);

    private static LaneSideView LaneSide(PlayerLaneState value) => new(
        value.MinionEntityId?.Value, value.FieldEntityId?.Value, value.EtherActivation, value.PreventNextEtherDecay);

    private static ImmutableArray<PlanOptionView> PlanOptions(MatchState state, PlayerState player)
    {
        if (state.Stage != MatchStage.Planning || state.Status != MatchStatus.Active) { return []; }
        var options = ImmutableArray.CreateBuilder<PlanOptionView>();
        foreach (var cardId in player.Hand.OrderBy(value => value.Value))
        {
            var card = state.CardInstances.Single(value => value.Id == cardId);
            var global = Eota.Kernel.Effects.CardInstanceRules.Definition(state, card) is SpellCardDefinition { TargetScope: SpellTargetScope.Global };
            foreach (var laneId in global ? new LaneId?[] { null } : state.Lanes.OrderBy(value => value.Id).Select(value => (LaneId?)value.Id))
            {
                var reason = Eota.Kernel.Commands.MatchCommandProcessor.ValidatePlan(state, player.Id, cardId, laneId);
                options.Add(new PlanOptionView(cardId.Value, laneId?.Value, reason == Eota.Kernel.Commands.CommandRejectionReason.None, reason.ToString()));
            }
        }
        return options.ToImmutable();
    }

    private static EntityView Entity(MatchState state, BattlefieldEntityState entity)
    {
        var minion = entity as MinionEntityState;
        var field = entity as FieldEntityState;
        var card = Card(state, entity.CardInstanceId);
        state.Content.TryGetCard(CardPrototypeId.Parse(card.PrototypeId), out var definition);
        var keywords = minion is not null
            ? minion.Keywords.OrderBy(value => value.Kind).Select(value => new KeywordView(value.Kind.ToString(), value.Parameter)).ToImmutableArray()
            : field!.Keywords.OrderBy(value => value).Select(value => new KeywordView(value.ToString(), 0)).ToImmutableArray();
        return new EntityView(entity.Id.Value, card, entity.OwnerId.Value, entity.ControllerId.Value,
            entity.LaneId.Value, minion is null ? null : AuthoritativeNumbers.DisplayAttack(minion.Attack), minion?.CurrentHealth, minion?.MaximumHealth,
            (field?.Lifetime as FiniteFieldLifetimeState)?.Energy, field?.Lifetime is PermanentFieldLifetimeState,
            definition is FieldCardDefinition { PreventsActiveAttacksInLane: true }, minion?.SlowTurnsRemaining ?? 0, keywords,
            entity.StoredCharge, entity.ChargeRequirementOverride)
        {
            BaseAttack = minion is null ? null : minion.TemporaryBaseAttacks.IsEmpty ? minion.BaseAttack ?? minion.Attack : minion.TemporaryBaseAttacks[^1].Value,
            IncomingDamageAdjustment = minion?.IncomingDamageAdjustment,
            ActiveEffects = ActiveEffects(entity.AttachedEffects),
            Modifiers = entity.TemporaryModifiers.OrderBy(value => value.Id.Value).Select(value => new ModifierView(value.Action.ToString(),
                value.Attribute.ToString(), value.Operation.ToString(), value.Value,
                value.Action is Eota.Kernel.Effects.EffectAction.AddKeyword or Eota.Kernel.Effects.EffectAction.RemoveKeyword ? value.Keyword.ToString() : null,
                value.RemainingDuration)).Concat(entity.TemporaryChargeOverrides.OrderBy(value => value.Id.Value)
                    .Select(value => new ModifierView("ModifyNumber", "ChargeRequirement", "Set", value.Value, null, value.RemainingDuration)))
                .Concat((minion?.TemporaryBaseAttacks ?? []).OrderBy(value => value.Id.Value)
                    .Select(value => new ModifierView("ModifyNumber", "BaseAttack", "Set", value.Value, null, value.RemainingDuration))).ToImmutableArray()
        };
    }

    private static ImmutableArray<ActiveEffectView> ActiveEffects(ImmutableArray<Eota.Kernel.Effects.AttachedEffect> effects) =>
        effects.OrderBy(value => value.Id.Value).Select(value => new ActiveEffectView(value.Definition.Id, value.Definition.Trigger.Kind.ToString(), value.RemainingDuration)).ToImmutableArray();

    private static CardView Card(MatchState state, CardInstanceId id)
    {
        var instance = state.CardInstances.Single(value => value.Id == id);
        var definition = Eota.Kernel.Effects.CardInstanceRules.Definition(state, instance);

        var minion = definition as MinionCardDefinition;
        return new CardView(id.Value, instance.CurrentPrototypeId.Value, definition.Kind.ToString(), AuthoritativeNumbers.DisplayCost(definition.Cost),
            minion is null ? null : AuthoritativeNumbers.DisplayAttack(minion.Attack), minion?.Health,
            minion?.Keywords.OrderBy(value => value.Kind).Select(value => new KeywordView(value.Kind.ToString(), value.Parameter)).ToImmutableArray() ?? []);
    }

    public static ImmutableArray<PresentationEvent> ProjectEvents(FrameTransition frame, Audience audience)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var seat = audience switch { Audience.PlayerOne => (PlayerId?)PlayerId.One, Audience.PlayerTwo => PlayerId.Two, _ => null };
        var events = ImmutableArray.CreateBuilder<PresentationEvent>();
        foreach (var value in frame.Events.Events)
        {
            // Explicitly review each new domain-event family before making it public.
            var visible = value.Kind is DomainEventKind.HeroHealthChanged or DomainEventKind.HeroMaximumHealthChanged or DomainEventKind.HeroHealed
                or DomainEventKind.EntityStatsChanged or DomainEventKind.EntityDamaged or DomainEventKind.EntityHealed
                or DomainEventKind.FieldEnergyChanged or DomainEventKind.EntityDied or DomainEventKind.EntityLeft
                or DomainEventKind.MatchEnded or DomainEventKind.EntityEntered or DomainEventKind.EntityMoved
                or DomainEventKind.MinionSlowChanged or DomainEventKind.MinionKeywordsChanged or DomainEventKind.PlayerResourcesChanged or DomainEventKind.FieldKeywordsChanged
                or DomainEventKind.EtherActivationChanged or DomainEventKind.EtherDecayPrevented
                or DomainEventKind.SpellResolved or DomainEventKind.MatchStageChanged or DomainEventKind.CardDrawn
                or DomainEventKind.CardBurned or DomainEventKind.TurnStarted or DomainEventKind.CombatDeclared or DomainEventKind.AttackDeclared
                or DomainEventKind.EntityBanished or DomainEventKind.EntityTransformed or DomainEventKind.CardReturned or DomainEventKind.CardGenerated
                or DomainEventKind.CardModified or DomainEventKind.EntityMechanicsChanged or DomainEventKind.AttachedEffectsChanged or DomainEventKind.EntityHealthLost or DomainEventKind.LaneStatusChanged;
            if (!visible)
            {
                continue;
            }

            // Hidden-zone modifications are private even as facts: their count must not identify deck matches.
            if (value.Kind == DomainEventKind.CardModified && seat != value.PlayerId) { continue; }
            var privateCard = value.Kind is DomainEventKind.CardDrawn or DomainEventKind.CardBurned or DomainEventKind.CardGenerated or DomainEventKind.CardReturned;
            var revealCard = !privateCard || seat == value.PlayerId;
            events.Add(new PresentationEvent(value.Kind.ToString(), value.EntityId?.Value, value.PlayerId?.Value,
                value.LaneId?.Value, value.TargetEntityId?.Value, value.TargetPlayerId?.Value,
                value.PreviousValue, value.CurrentValue,
                revealCard && value.CardInstanceId is { } cardId ? Card(frame.State, cardId) : null));
        }

        return events.ToImmutable();
    }
}
