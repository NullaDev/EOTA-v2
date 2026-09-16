using System.Collections.Immutable;
using Eota.Kernel.Commands;
using Eota.Kernel.Content;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Protocols;

namespace Eota.Kernel.Tests;

public sealed class CommandProcessorTests
{
    private static readonly CardPrototypeId MinionId = new("TEST-COMMAND-MINION");
    private static readonly CardPrototypeId FieldId = new("TEST-COMMAND-FIELD");
    private static readonly CardPrototypeId LaneSpellId = new("TEST-COMMAND-LANE-SPELL");
    private static readonly CardPrototypeId GlobalSpellId = new("TEST-COMMAND-GLOBAL-SPELL");
    private static readonly CardPrototypeId NegativeCostSpellId = new("TEST-COMMAND-NEGATIVE-COST-SPELL");

    [Fact]
    public void PlanningAndCancellationReserveAndRefundCostWithoutChangingCardIdentity()
    {
        var state = CreatePlanningMatch();
        var cardId = FindHandCard(state, PlayerId.One, CardKind.Minion);

        var planned = MatchCommandProcessor.Accept(
            state,
            new PlanCardCommand(PlayerId.One, 0, cardId, new LaneId(0)));

        Assert.True(planned.IsAccepted);
        var planId = Assert.IsType<PlanCommandId>(planned.Receipt.PlanCommandId);
        Assert.Equal(new PlanCommandId(PlayerId.One, 1), planId);
        var planningPlayer = GetPlayer(planned.State, PlayerId.One);
        Assert.Equal(8, planningPlayer.CurrentCost);
        Assert.DoesNotContain(cardId, planningPlayer.Hand);
        Assert.Equal(cardId, Assert.Single(planningPlayer.Planning).CardInstanceId);
        Assert.Equal(CardZone.Planning, GetCard(planned.State, cardId).Zone);
        Assert.Equal(1UL, planningPlayer.CommandRevision);
        Assert.Equal(1UL, planned.State.Revision);
        Assert.Single(planned.State.CommandLog);

        var cancelled = MatchCommandProcessor.Accept(
            planned.State,
            new CancelPlanCommand(PlayerId.One, 1, planId));

        Assert.True(cancelled.IsAccepted);
        var cancelledPlayer = GetPlayer(cancelled.State, PlayerId.One);
        Assert.Equal(10, cancelledPlayer.CurrentCost);
        Assert.Contains(cardId, cancelledPlayer.Hand);
        Assert.Empty(cancelledPlayer.Planning);
        Assert.Equal(CardZone.Hand, GetCard(cancelled.State, cardId).Zone);
        Assert.Equal(2UL, cancelledPlayer.NextPlanOrdinal);
    }

    [Fact]
    public void DifferentPlayersCanPlanInEitherArrivalOrderWithTheSameAuthoritativePlan()
    {
        var initial = CreatePlanningMatch();
        var playerOneCard = FindHandCard(initial, PlayerId.One, CardKind.Minion);
        var playerTwoCard = FindHandCard(initial, PlayerId.Two, CardKind.Minion);

        var oneThenTwo = MatchCommandProcessor.Accept(
            MatchCommandProcessor.Accept(
                initial,
                new PlanCardCommand(PlayerId.One, 0, playerOneCard, new LaneId(0))).State,
            new PlanCardCommand(PlayerId.Two, 0, playerTwoCard, new LaneId(1))).State;
        var twoThenOne = MatchCommandProcessor.Accept(
            MatchCommandProcessor.Accept(
                initial,
                new PlanCardCommand(PlayerId.Two, 0, playerTwoCard, new LaneId(1))).State,
            new PlanCardCommand(PlayerId.One, 0, playerOneCard, new LaneId(0))).State;

        Assert.Equal(PlanningStateHasher.Compute(oneThenTwo), PlanningStateHasher.Compute(twoThenOne));
        Assert.Equal(
            new PlanCommandId(PlayerId.One, 1),
            Assert.Single(GetPlayer(oneThenTwo, PlayerId.One).Planning).Id);
        Assert.Equal(
            new PlanCommandId(PlayerId.Two, 1),
            Assert.Single(GetPlayer(oneThenTwo, PlayerId.Two).Planning).Id);
    }

    [Fact]
    public void PlayerRevisionIsIndependentButRejectsStaleCommandsFromTheSamePlayer()
    {
        var initial = CreatePlanningMatch();
        var playerOneCard = FindHandCard(initial, PlayerId.One, CardKind.Minion);
        var playerTwoCard = FindHandCard(initial, PlayerId.Two, CardKind.Minion);
        var afterPlayerOne = MatchCommandProcessor.Accept(
            initial,
            new PlanCardCommand(PlayerId.One, 0, playerOneCard, new LaneId(0))).State;

        var otherPlayer = MatchCommandProcessor.Accept(
            afterPlayerOne,
            new PlanCardCommand(PlayerId.Two, 0, playerTwoCard, new LaneId(1)));
        var staleSamePlayer = MatchCommandProcessor.Accept(
            afterPlayerOne,
            new SubmitTurnCommand(PlayerId.One, 0));

        Assert.True(otherPlayer.IsAccepted);
        Assert.False(staleSamePlayer.IsAccepted);
        Assert.Equal(CommandRejectionReason.PlayerRevisionMismatch, staleSamePlayer.Receipt.RejectionReason);
        Assert.Same(afterPlayerOne, staleSamePlayer.State);
    }

    [Fact]
    public void APlayerCannotPlanAnOpponentsCardOrAnOutOfRangeLane()
    {
        var initial = CreatePlanningMatch();
        var playerOneCard = FindHandCard(initial, PlayerId.One, CardKind.Minion);

        var wrongOwner = MatchCommandProcessor.Accept(
            initial,
            new PlanCardCommand(PlayerId.Two, 0, playerOneCard, new LaneId(0)));
        var invalidLane = MatchCommandProcessor.Accept(
            initial,
            new PlanCardCommand(PlayerId.One, 0, playerOneCard, new LaneId(initial.Lanes.Length)));

        Assert.Equal(CommandRejectionReason.CardNotInHand, wrongOwner.Receipt.RejectionReason);
        Assert.Equal(CommandRejectionReason.InvalidLane, invalidLane.Receipt.RejectionReason);
        Assert.Same(initial, wrongOwner.State);
        Assert.Same(initial, invalidLane.State);
    }

    [Fact]
    public void PlanningRejectsASecondCardForTheSameOwnedSlot()
    {
        var initial = CreatePlanningMatch();
        var minions = GetPlayer(initial, PlayerId.One).Hand
            .Where(cardId => GetDefinition(initial, cardId).Kind == CardKind.Minion)
            .ToArray();
        var first = MatchCommandProcessor.Accept(
            initial,
            new PlanCardCommand(PlayerId.One, 0, minions[0], new LaneId(0)));
        var second = MatchCommandProcessor.Accept(
            first.State,
            new PlanCardCommand(PlayerId.One, 1, minions[1], new LaneId(0)));

        Assert.False(second.IsAccepted);
        Assert.Equal(CommandRejectionReason.PlanningSlotOccupied, second.Receipt.RejectionReason);
        Assert.Equal(MatchStateHasher.Compute(first.State), MatchStateHasher.Compute(second.State));
    }

    [Fact]
    public void SpellTargetScopeIsValidatedBeforeCostIsReserved()
    {
        var initial = CreatePlanningMatch();
        var laneSpell = FindHandCard(initial, PlayerId.One, LaneSpellId);
        var globalSpell = FindHandCard(initial, PlayerId.One, GlobalSpellId);

        var missingLane = MatchCommandProcessor.Accept(
            initial,
            new PlanSpellCommand(PlayerId.One, 0, laneSpell, null));
        var unexpectedLane = MatchCommandProcessor.Accept(
            initial,
            new PlanSpellCommand(PlayerId.One, 0, globalSpell, new LaneId(0)));

        Assert.Equal(CommandRejectionReason.InvalidLane, missingLane.Receipt.RejectionReason);
        Assert.Equal(CommandRejectionReason.InvalidLane, unexpectedLane.Receipt.RejectionReason);
        Assert.Equal(10, GetPlayer(initial, PlayerId.One).CurrentCost);
    }

    [Fact]
    public void NegativeAuthoritativeCostReservesZeroAndNeverRefundsResources()
    {
        var initial = CreatePlanningMatch();
        var cardId = FindHandCard(initial, PlayerId.One, NegativeCostSpellId);

        var planned = MatchCommandProcessor.Accept(
            initial,
            new PlanSpellCommand(PlayerId.One, 0, cardId, null));
        var plan = Assert.Single(GetPlayer(planned.State, PlayerId.One).Planning);

        Assert.True(planned.IsAccepted);
        Assert.Equal(-5, plan.AuthoritativeCost);
        Assert.Equal(0, plan.ReservedCost);
        Assert.Equal(10, GetPlayer(planned.State, PlayerId.One).CurrentCost);
    }

    [Fact]
    public void BothSubmissionsCreateAResolutionBoundaryAndLockFurtherPlanning()
    {
        var afterOne = MatchCommandProcessor.Accept(
            CreatePlanningMatch(),
            new SubmitTurnCommand(PlayerId.One, 0));
        var locked = MatchCommandProcessor.Accept(
            afterOne.State,
            new SubmitTurnCommand(PlayerId.One, 1));
        var afterBoth = MatchCommandProcessor.Accept(
            afterOne.State,
            new SubmitTurnCommand(PlayerId.Two, 0));

        Assert.Equal(CommandRejectionReason.PlayerAlreadySubmitted, locked.Receipt.RejectionReason);
        Assert.Equal(MatchStage.ReadyToResolve, afterBoth.State.Stage);
        Assert.All(afterBoth.State.Players, player => Assert.True(player.TurnSubmitted));
    }

    [Fact]
    public void AcceptedCommandLogReconstructsTheSamePlanningState()
    {
        var initial = CreatePlanningMatch();
        var cardId = FindHandCard(initial, PlayerId.One, CardKind.Minion);
        var planned = MatchCommandProcessor.Accept(
            initial,
            new PlanCardCommand(PlayerId.One, 0, cardId, new LaneId(2))).State;
        var submitted = MatchCommandProcessor.Accept(
            planned,
            new SubmitTurnCommand(PlayerId.One, 1)).State;

        var replay = CommandLogReplay.Replay(initial, submitted.CommandLog);

        Assert.True(replay.IsSuccess);
        Assert.Equal(MatchStateHasher.Compute(submitted), MatchStateHasher.Compute(replay.State!));
    }

    private static MatchState CreatePlanningMatch()
    {
        var cards = new CardDefinition[]
        {
            new MinionCardDefinition(MinionId, CardSource.Test, Profession.Neutral, 2, 2, 3, ImmutableArray<MinionKeywordDefinition>.Empty),
            new FieldCardDefinition(FieldId, CardSource.Test, Profession.Neutral, 3, new FiniteFieldLifetimeDefinition(2), false),
            new SpellCardDefinition(LaneSpellId, CardSource.Test, Profession.Neutral, 1, SpellSpeed.Slow, SpellTargetScope.Lane),
            new SpellCardDefinition(GlobalSpellId, CardSource.Test, Profession.Neutral, 1, SpellSpeed.Fast, SpellTargetScope.Global),
            new SpellCardDefinition(NegativeCostSpellId, CardSource.Test, Profession.Neutral, -5, SpellSpeed.Fast, SpellTargetScope.Global)
        };
        var entries = cards.Select(card => new DeckEntry(card.Id, 2)).ToArray();
        var protocol = CompiledGameProtocol.Compile(GameProtocolDefinition.DefaultV0 with
        {
            ProtocolId = "eota.test.commands",
            InitialPlayerCost = 10,
            InitialMaxCost = 10,
            RequiredDeckSize = 10,
            MaxCopiesPerCard = 2,
            OpeningHandSize = 10,
            HandLimit = 10,
            DeckConstructionPolicy = DeckConstructionPolicy.DevelopmentAnySource
        });
        var result = MatchFactory.Create(new MatchCreationRequest(
            protocol,
            RuleContentPack.Create("eota.card/v2", cards),
            1234,
            DeckDefinition.Create(Profession.Neutral, entries),
            DeckDefinition.Create(Profession.Neutral, entries)));

        return Assert.IsType<MatchState>(result.State);
    }

    private static PlayerState GetPlayer(MatchState state, PlayerId playerId) =>
        state.Players.Single(player => player.Id == playerId);

    private static CardInstanceState GetCard(MatchState state, CardInstanceId cardInstanceId) =>
        state.CardInstances.Single(card => card.Id == cardInstanceId);

    private static CardDefinition GetDefinition(MatchState state, CardInstanceId cardInstanceId)
    {
        var instance = GetCard(state, cardInstanceId);
        Assert.True(state.Content.TryGetCard(instance.CurrentPrototypeId, out var definition));
        return Assert.IsAssignableFrom<CardDefinition>(definition);
    }

    private static CardInstanceId FindHandCard(MatchState state, PlayerId playerId, CardKind kind) =>
        GetPlayer(state, playerId).Hand.First(cardId => GetDefinition(state, cardId).Kind == kind);

    private static CardInstanceId FindHandCard(MatchState state, PlayerId playerId, CardPrototypeId prototypeId) =>
        GetPlayer(state, playerId).Hand.First(cardId => GetCard(state, cardId).CurrentPrototypeId == prototypeId);
}
