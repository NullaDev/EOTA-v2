using System.Collections.Immutable;
using Eota.Kernel.Content;
using Eota.Kernel.Determinism;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Rules;

namespace Eota.Kernel.Commands;

public static class MatchCommandProcessor
{
    public static CommandRejectionReason ValidatePlan(MatchState state, PlayerId seat, CardInstanceId cardId, LaneId? laneId)
    {
        ArgumentNullException.ThrowIfNull(state);
        var player = GetPlayer(state, seat);
        if (state.Status != MatchStatus.Active) { return CommandRejectionReason.MatchNotActive; }
        var card = GetCardInstance(state, cardId);
        if (card is null) { return CommandRejectionReason.CardNotInHand; }
        var definition = Eota.Kernel.Effects.CardInstanceRules.Definition(state, card);
        return definition is SpellCardDefinition
            ? PlanSpell(state, player, new PlanSpellCommand(seat, player.CommandRevision, cardId, laneId)).RejectionReason
            : laneId is { } lane
                ? PlanCard(state, player, new PlanCardCommand(seat, player.CommandRevision, cardId, lane)).RejectionReason
                : CommandRejectionReason.InvalidLane;
    }

    public static CommandTransition Accept(MatchState state, AuthoritativeCommand command)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);

        var player = GetPlayer(state, command.PlayerId);
        if (state.Status != MatchStatus.Active)
        {
            return Reject(state, command, player, CommandRejectionReason.MatchNotActive);
        }

        if (command.ExpectedPlayerRevision != player.CommandRevision)
        {
            return Reject(state, command, player, CommandRejectionReason.PlayerRevisionMismatch);
        }

        var mutation = command switch
        {
            SubmitMulliganCommand mulligan => SubmitMulligan(state, player, mulligan),
            PlanCardCommand planCard => PlanCard(state, player, planCard),
            PlanSpellCommand planSpell => PlanSpell(state, player, planSpell),
            CancelPlanCommand cancelPlan => CancelPlan(state, player, cancelPlan),
            SubmitTurnCommand submitTurn => SubmitTurn(state, player, submitTurn),
            SystemTimeoutCommand timeout => SystemTimeout(state, player, timeout),
            _ => CommandMutation.Rejected(CommandRejectionReason.WrongStage)
        };

        if (!mutation.IsAccepted)
        {
            return Reject(state, command, player, mutation.RejectionReason);
        }

        var acceptedCommand = mutation.Command ?? command;
        var commandId = state.NextCommandId;
        var resultingMatchRevision = checked(state.Revision + 1);
        var resultingPlayerRevision = checked(player.CommandRevision + 1);
        var mutatedPlayer = GetPlayer(mutation.State!, command.PlayerId) with
        {
            CommandRevision = resultingPlayerRevision
        };
        var acceptedState = mutation.State! with
        {
            Revision = resultingMatchRevision,
            Players = ReplacePlayer(mutation.State.Players, mutatedPlayer),
            CommandLog = state.CommandLog.Add(new AcceptedCommandRecord(commandId, resultingMatchRevision, acceptedCommand)),
            NextCommandId = new CommandId(checked(commandId.Value + 1))
        };

        if (acceptedCommand is SubmitMulliganCommand or SystemTimeoutCommand { ExpectedStage: MatchStage.Mulligan }
            && acceptedState.Players.All(value => value.Mulligan is { Submitted: true }))
        {
            acceptedState = ResolveMulligans(acceptedState);
        }
        else if (acceptedCommand is SubmitTurnCommand or SystemTimeoutCommand { ExpectedStage: MatchStage.Planning }
                 && acceptedState.Players.All(value => value.TurnSubmitted))
        {
            acceptedState = acceptedState with { Stage = MatchStage.ReadyToResolve };
        }

        return new CommandTransition(
            acceptedState,
            new CommandReceipt(
                CommandReceiptStatus.Accepted,
                CommandRejectionReason.None,
                commandId,
                acceptedCommand.Kind,
                command.PlayerId,
                state.Revision,
                resultingMatchRevision,
                player.CommandRevision,
                resultingPlayerRevision,
                mutation.PlanCommandId));
    }

    private static CommandMutation SystemTimeout(MatchState state, PlayerState player, SystemTimeoutCommand command)
    {
        if (command.ExpectedTurn != state.Turn || command.ExpectedStage != state.Stage)
        { return CommandMutation.Rejected(CommandRejectionReason.SystemContextMismatch); }
        var mutation = state.Stage switch
        {
            MatchStage.Mulligan => SubmitMulligan(state, player, new SubmitMulliganCommand(player.Id, player.CommandRevision, [])),
            MatchStage.Planning => SubmitTurn(state, player, new SubmitTurnCommand(player.Id, player.CommandRevision)),
            _ => CommandMutation.Rejected(CommandRejectionReason.WrongStage)
        };
        return mutation.IsAccepted ? CommandMutation.Accepted(mutation.State!, command, null) : mutation;
    }

    private static CommandMutation SubmitMulligan(
        MatchState state,
        PlayerState player,
        SubmitMulliganCommand command)
    {
        if (state.Stage != MatchStage.Mulligan || player.Mulligan is null)
        {
            return CommandMutation.Rejected(CommandRejectionReason.WrongStage);
        }

        if (player.Mulligan.Submitted)
        {
            return CommandMutation.Rejected(CommandRejectionReason.PlayerAlreadySubmitted);
        }

        var selected = command.ReplacedCards.IsDefault
            ? ImmutableArray<CardInstanceId>.Empty
            : command.ReplacedCards;
        if (selected.Distinct().Count() != selected.Length)
        {
            return CommandMutation.Rejected(CommandRejectionReason.DuplicateMulliganCard);
        }

        if (selected.Any(cardId => !player.Mulligan.EligibleCards.Contains(cardId) || !player.Hand.Contains(cardId)))
        {
            return CommandMutation.Rejected(CommandRejectionReason.MulliganCardNotEligible);
        }

        if (selected.Length > player.Deck.Length)
        {
            return CommandMutation.Rejected(CommandRejectionReason.MulliganDeckTooSmall);
        }

        var normalizedSelection = selected.OrderBy(cardId => cardId).ToImmutableArray();
        var nextPlayer = player with
        {
            Mulligan = player.Mulligan with
            {
                SelectedCards = normalizedSelection,
                Submitted = true
            }
        };
        var nextState = state with { Players = ReplacePlayer(state.Players, nextPlayer) };
        var normalizedCommand = command with { ReplacedCards = normalizedSelection };
        return CommandMutation.Accepted(nextState, normalizedCommand, null);
    }

    private static CommandMutation PlanCard(MatchState state, PlayerState player, PlanCardCommand command)
    {
        var validation = ValidatePlanningCommand(state, player, command.CardInstanceId);
        if (validation != CommandRejectionReason.None)
        {
            return CommandMutation.Rejected(validation);
        }

        if (!IsValidLane(state, command.LaneId))
        {
            return CommandMutation.Rejected(CommandRejectionReason.InvalidLane);
        }

        if (Eota.Kernel.Effects.LaneStatusRules.IsLocked(state, command.LaneId))
        { return CommandMutation.Rejected(CommandRejectionReason.LaneLocked); }

        var instance = GetCardInstance(state, command.CardInstanceId)!;
        var card = Eota.Kernel.Effects.CardInstanceRules.Definition(state, instance);
        if (card is not (MinionCardDefinition or FieldCardDefinition))
        {
            return CommandMutation.Rejected(CommandRejectionReason.CardTypeMismatch);
        }

        if (!IsBattlefieldSlotAvailable(state, player.Id, command.LaneId, card))
        {
            return CommandMutation.Rejected(CommandRejectionReason.BattlefieldSlotUnavailable);
        }

        var actionKind = card.Kind == CardKind.Minion ? PlannedActionKind.Minion : PlannedActionKind.Field;
        if (player.Planning.Any(action => action.Kind == actionKind && action.LaneId == command.LaneId))
        {
            return CommandMutation.Rejected(CommandRejectionReason.PlanningSlotOccupied);
        }

        return AddPlan(state, player, command, instance, card, actionKind, command.LaneId);
    }

    private static CommandMutation PlanSpell(MatchState state, PlayerState player, PlanSpellCommand command)
    {
        var validation = ValidatePlanningCommand(state, player, command.CardInstanceId);
        if (validation != CommandRejectionReason.None)
        {
            return CommandMutation.Rejected(validation);
        }

        var instance = GetCardInstance(state, command.CardInstanceId)!;
        var card = Eota.Kernel.Effects.CardInstanceRules.Definition(state, instance);
        if (card is not SpellCardDefinition spell)
        {
            return CommandMutation.Rejected(CommandRejectionReason.CardTypeMismatch);
        }

        if ((spell.TargetScope == SpellTargetScope.Global && command.TargetLaneId is not null)
            || (spell.TargetScope == SpellTargetScope.Lane
                && (command.TargetLaneId is null || !IsValidLane(state, command.TargetLaneId.Value))))
        {
            return CommandMutation.Rejected(CommandRejectionReason.InvalidLane);
        }

        var actionKind = spell.Speed == SpellSpeed.Fast ? PlannedActionKind.FastSpell : PlannedActionKind.SlowSpell;
        return AddPlan(state, player, command, instance, card, actionKind, command.TargetLaneId);
    }

    private static CommandMutation AddPlan(
        MatchState state,
        PlayerState player,
        AuthoritativeCommand command,
        CardInstanceState instance,
        CardDefinition card,
        PlannedActionKind kind,
        LaneId? laneId)
    {
        var payableCost = AuthoritativeNumbers.PayableCost(card.Cost);
        if (player.CurrentCost < payableCost)
        {
            return CommandMutation.Rejected(CommandRejectionReason.InsufficientCost);
        }

        var planId = new PlanCommandId(player.Id, player.NextPlanOrdinal);
        var plan = new PlannedAction(planId, instance.Id, kind, laneId, card.Cost, payableCost);
        var nextPlayer = player with
        {
            CurrentCost = checked(player.CurrentCost - payableCost),
            Hand = player.Hand.Where(cardId => cardId != instance.Id).ToImmutableArray(),
            Planning = player.Planning.Add(plan).OrderBy(action => action.Id).ToImmutableArray(),
            NextPlanOrdinal = checked(player.NextPlanOrdinal + 1)
        };
        var nextState = state with
        {
            Players = ReplacePlayer(state.Players, nextPlayer),
            CardInstances = SetCardZone(state.CardInstances, instance.Id, CardZone.Planning)
        };
        return CommandMutation.Accepted(nextState, command, planId);
    }

    private static CommandMutation CancelPlan(MatchState state, PlayerState player, CancelPlanCommand command)
    {
        if (state.Stage != MatchStage.Planning)
        {
            return CommandMutation.Rejected(CommandRejectionReason.WrongStage);
        }

        if (player.TurnSubmitted)
        {
            return CommandMutation.Rejected(CommandRejectionReason.PlayerAlreadySubmitted);
        }

        if (command.PlanCommandId.PlayerId != player.Id)
        {
            return CommandMutation.Rejected(CommandRejectionReason.PlanOwnedByAnotherPlayer);
        }

        var plan = player.Planning.SingleOrDefault(action => action.Id == command.PlanCommandId);
        if (plan is null)
        {
            return CommandMutation.Rejected(CommandRejectionReason.PlanNotFound);
        }

        var nextPlayer = player with
        {
            CurrentCost = checked(player.CurrentCost + plan.ReservedCost),
            Hand = player.Hand.Add(plan.CardInstanceId).OrderBy(cardId => cardId).ToImmutableArray(),
            Planning = player.Planning.Where(action => action.Id != plan.Id).ToImmutableArray()
        };
        var nextState = state with
        {
            Players = ReplacePlayer(state.Players, nextPlayer),
            CardInstances = SetCardZone(state.CardInstances, plan.CardInstanceId, CardZone.Hand)
        };
        return CommandMutation.Accepted(nextState, command, plan.Id);
    }

    private static CommandMutation SubmitTurn(MatchState state, PlayerState player, SubmitTurnCommand command)
    {
        if (state.Stage != MatchStage.Planning)
        {
            return CommandMutation.Rejected(CommandRejectionReason.WrongStage);
        }

        if (player.TurnSubmitted)
        {
            return CommandMutation.Rejected(CommandRejectionReason.PlayerAlreadySubmitted);
        }

        var nextPlayer = player with { TurnSubmitted = true };
        var nextState = state with { Players = ReplacePlayer(state.Players, nextPlayer) };
        return CommandMutation.Accepted(nextState, command, null);
    }

    private static CommandRejectionReason ValidatePlanningCommand(
        MatchState state,
        PlayerState player,
        CardInstanceId cardInstanceId)
    {
        if (state.Stage != MatchStage.Planning)
        {
            return CommandRejectionReason.WrongStage;
        }

        if (player.TurnSubmitted)
        {
            return CommandRejectionReason.PlayerAlreadySubmitted;
        }

        var instance = GetCardInstance(state, cardInstanceId);
        return instance is null
               || instance.OwnerId != player.Id
               || instance.Zone != CardZone.Hand
               || !player.Hand.Contains(cardInstanceId)
            ? CommandRejectionReason.CardNotInHand
            : CommandRejectionReason.None;
    }

    private static MatchState ResolveMulligans(MatchState state)
    {
        var rng = state.RuleRng;
        var players = state.Players;
        var instances = state.CardInstances;

        foreach (var player in players.OrderBy(value => value.Id))
        {
            var selected = player.Mulligan!.SelectedCards;
            var replacementCards = player.Deck.Take(selected.Length).ToImmutableArray();
            var remainingDeck = player.Deck.Skip(selected.Length).Concat(selected).ToArray();
            if (selected.Length > 0)
            {
                rng = GlobalRuleRng.Shuffle(remainingDeck.AsSpan(), rng);
            }

            var selectedSet = selected.ToHashSet();
            var nextHand = player.Hand
                .Where(cardId => !selectedSet.Contains(cardId))
                .Concat(replacementCards)
                .OrderBy(cardId => cardId)
                .ToImmutableArray();
            var nextPlayer = player with
            {
                Deck = remainingDeck.ToImmutableArray(),
                Hand = nextHand,
                Mulligan = null
            };
            players = ReplacePlayer(players, nextPlayer);

            foreach (var cardId in selected)
            {
                instances = SetCardZone(instances, cardId, CardZone.Deck);
            }

            foreach (var cardId in replacementCards)
            {
                instances = SetCardZone(instances, cardId, CardZone.Hand);
            }
        }

        return state with
        {
            Stage = MatchStage.Planning,
            Players = players,
            CardInstances = instances,
            RuleRng = rng
        };
    }

    private static CommandTransition Reject(
        MatchState state,
        AuthoritativeCommand command,
        PlayerState player,
        CommandRejectionReason reason)
    {
        return new CommandTransition(
            state,
            new CommandReceipt(
                CommandReceiptStatus.Rejected,
                reason,
                null,
                command.Kind,
                command.PlayerId,
                state.Revision,
                state.Revision,
                player.CommandRevision,
                player.CommandRevision,
                null));
    }

    private static PlayerState GetPlayer(MatchState state, PlayerId playerId) =>
        state.Players.Single(player => player.Id == playerId);

    private static CardInstanceState? GetCardInstance(MatchState state, CardInstanceId cardInstanceId) =>
        state.CardInstances.SingleOrDefault(instance => instance.Id == cardInstanceId);

    private static bool IsValidLane(MatchState state, LaneId laneId) =>
        laneId.Value < state.Protocol.Definition.LaneCount;

    private static bool IsBattlefieldSlotAvailable(
        MatchState state,
        PlayerId playerId,
        LaneId laneId,
        CardDefinition card)
    {
        var lane = state.Lanes.Single(value => value.Id == laneId);
        var playerLane = playerId == PlayerId.One ? lane.PlayerOne : lane.PlayerTwo;
        if (card is FieldCardDefinition incomingField)
        {
            if (playerLane.FieldEntityId is not { } fieldId)
            {
                return true;
            }

            return state.Entities.SingleOrDefault(value => value.Id == fieldId) is FieldEntityState occupyingField
                   && (incomingField.Keywords.Contains(FieldKeywordKind.Replace)
                       || occupyingField.Keywords.Contains(FieldKeywordKind.Replaceable));
        }

        if (card is not MinionCardDefinition incoming || playerLane.MinionEntityId is not { } occupyingId)
        {
            return true;
        }

        var occupying = state.Entities.SingleOrDefault(value => value.Id == occupyingId) as MinionEntityState;
        return occupying is not null
               && (incoming.Keywords.Any(value => value.Kind == MinionKeywordKind.Replace)
                   || occupying.Keywords.Any(value => value.Kind == MinionKeywordKind.Replaceable));
    }

    private static ImmutableArray<PlayerState> ReplacePlayer(
        ImmutableArray<PlayerState> players,
        PlayerState replacement)
    {
        for (var index = 0; index < players.Length; index++)
        {
            if (players[index].Id == replacement.Id)
            {
                return players.SetItem(index, replacement);
            }
        }

        throw new InvalidOperationException($"Player '{replacement.Id}' is missing from match state.");
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

    private sealed record CommandMutation(
        MatchState? State,
        AuthoritativeCommand? Command,
        PlanCommandId? PlanCommandId,
        CommandRejectionReason RejectionReason)
    {
        public bool IsAccepted => State is not null;

        public static CommandMutation Accepted(
            MatchState state,
            AuthoritativeCommand command,
            PlanCommandId? planCommandId) =>
            new(state, command, planCommandId, CommandRejectionReason.None);

        public static CommandMutation Rejected(CommandRejectionReason reason) =>
            new(null, null, null, reason);
    }
}
