using System.Collections.Immutable;
using Eota.Kernel.Commands;
using Eota.Kernel.Content;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Protocols;

namespace Eota.Kernel.Tests;

public sealed class MulliganTests
{
    [Fact]
    public void EnabledProtocolWaitsForBothPlayersThenReplacesWithoutImmediateRedraw()
    {
        var initial = CreateMulliganMatch();
        var playerOne = GetPlayer(initial, PlayerId.One);
        var selected = playerOne.Hand[0];
        var expectedReplacement = playerOne.Deck[0];
        var initialRngCount = initial.RuleRng.SampleCount;

        var afterOne = MatchCommandProcessor.Accept(
            initial,
            new SubmitMulliganCommand(PlayerId.One, 0, [selected]));

        Assert.True(afterOne.IsAccepted);
        Assert.Equal(MatchStage.Mulligan, afterOne.State.Stage);
        Assert.Equal(playerOne.Hand, GetPlayer(afterOne.State, PlayerId.One).Hand);
        Assert.Equal(initialRngCount, afterOne.State.RuleRng.SampleCount);

        var afterBoth = MatchCommandProcessor.Accept(
            afterOne.State,
            new SubmitMulliganCommand(PlayerId.Two, 0, ImmutableArray<CardInstanceId>.Empty));
        var resolvedPlayerOne = GetPlayer(afterBoth.State, PlayerId.One);

        Assert.Equal(MatchStage.Planning, afterBoth.State.Stage);
        Assert.Null(resolvedPlayerOne.Mulligan);
        Assert.DoesNotContain(selected, resolvedPlayerOne.Hand);
        Assert.Contains(selected, resolvedPlayerOne.Deck);
        Assert.Contains(expectedReplacement, resolvedPlayerOne.Hand);
        Assert.Equal(CardZone.Deck, GetCard(afterBoth.State, selected).Zone);
        Assert.Equal(CardZone.Hand, GetCard(afterBoth.State, expectedReplacement).Zone);
        Assert.True(afterBoth.State.RuleRng.SampleCount > initialRngCount);
    }

    [Fact]
    public void ZeroCardMulliganConsumesNoAdditionalRandomSamples()
    {
        var initial = CreateMulliganMatch();
        var initialHands = initial.Players.Select(player => player.Hand).ToArray();
        var initialDecks = initial.Players.Select(player => player.Deck).ToArray();

        var resolved = MatchCommandProcessor.Accept(
            MatchCommandProcessor.Accept(
                initial,
                new SubmitMulliganCommand(PlayerId.One, 0, ImmutableArray<CardInstanceId>.Empty)).State,
            new SubmitMulliganCommand(PlayerId.Two, 0, ImmutableArray<CardInstanceId>.Empty)).State;

        Assert.Equal(initial.RuleRng, resolved.RuleRng);
        Assert.Equal(initialHands[0].ToArray(), GetPlayer(resolved, PlayerId.One).Hand.ToArray());
        Assert.Equal(initialHands[1].ToArray(), GetPlayer(resolved, PlayerId.Two).Hand.ToArray());
        Assert.Equal(initialDecks[0].ToArray(), GetPlayer(resolved, PlayerId.One).Deck.ToArray());
        Assert.Equal(initialDecks[1].ToArray(), GetPlayer(resolved, PlayerId.Two).Deck.ToArray());
    }

    [Fact]
    public void MulliganResolutionDoesNotDependOnSubmissionArrivalOrder()
    {
        var initial = CreateMulliganMatch();
        var playerOneSelection = GetPlayer(initial, PlayerId.One).Hand[0];
        var playerTwoSelection = GetPlayer(initial, PlayerId.Two).Hand[0];

        var oneThenTwo = MatchCommandProcessor.Accept(
            MatchCommandProcessor.Accept(
                initial,
                new SubmitMulliganCommand(PlayerId.One, 0, [playerOneSelection])).State,
            new SubmitMulliganCommand(PlayerId.Two, 0, [playerTwoSelection])).State;
        var twoThenOne = MatchCommandProcessor.Accept(
            MatchCommandProcessor.Accept(
                initial,
                new SubmitMulliganCommand(PlayerId.Two, 0, [playerTwoSelection])).State,
            new SubmitMulliganCommand(PlayerId.One, 0, [playerOneSelection])).State;

        Assert.Equal(oneThenTwo.Stage, twoThenOne.Stage);
        Assert.Equal(oneThenTwo.RuleRng, twoThenOne.RuleRng);
        foreach (var playerId in new[] { PlayerId.One, PlayerId.Two })
        {
            Assert.Equal(GetPlayer(oneThenTwo, playerId).Hand.ToArray(), GetPlayer(twoThenOne, playerId).Hand.ToArray());
            Assert.Equal(GetPlayer(oneThenTwo, playerId).Deck.ToArray(), GetPlayer(twoThenOne, playerId).Deck.ToArray());
            Assert.Equal(GetPlayer(oneThenTwo, playerId).CommandRevision, GetPlayer(twoThenOne, playerId).CommandRevision);
        }
    }

    [Fact]
    public void DuplicateOrNonOpeningCardSelectionIsRejectedWithoutMutation()
    {
        var initial = CreateMulliganMatch();
        var player = GetPlayer(initial, PlayerId.One);
        var openingCard = player.Hand[0];
        var deckCard = player.Deck[0];

        var duplicate = MatchCommandProcessor.Accept(
            initial,
            new SubmitMulliganCommand(PlayerId.One, 0, [openingCard, openingCard]));
        var notEligible = MatchCommandProcessor.Accept(
            initial,
            new SubmitMulliganCommand(PlayerId.One, 0, [deckCard]));

        Assert.Equal(CommandRejectionReason.DuplicateMulliganCard, duplicate.Receipt.RejectionReason);
        Assert.Equal(CommandRejectionReason.MulliganCardNotEligible, notEligible.Receipt.RejectionReason);
        Assert.Same(initial, duplicate.State);
        Assert.Same(initial, notEligible.State);
    }

    [Fact]
    public void DefaultProtocolSkipsMulliganButKeepsTheCapabilityInTheKernel()
    {
        var mulliganState = CreateMulliganMatch();
        var protocol = mulliganState.Protocol with
        {
            Definition = mulliganState.Protocol.Definition with { MulliganEnabled = false }
        };
        protocol = CompiledGameProtocol.Compile(protocol.Definition);
        var result = MatchFactory.Create(new MatchCreationRequest(
            protocol,
            mulliganState.Content,
            mulliganState.Manifest.MatchSeed,
            mulliganState.Manifest.PlayerOneDeck,
            mulliganState.Manifest.PlayerTwoDeck));
        var state = Assert.IsType<MatchState>(result.State);

        Assert.Equal(MatchStage.Planning, state.Stage);
        Assert.All(state.Players, player => Assert.Null(player.Mulligan));
    }

    private static MatchState CreateMulliganMatch()
    {
        var definitions = Enumerable.Range(0, 4)
            .Select(index => new MinionCardDefinition(
                new CardPrototypeId($"TEST-MULLIGAN-{index}"),
                CardSource.Test,
                Profession.Neutral,
                1,
                1,
                1,
                ImmutableArray<MinionKeywordDefinition>.Empty))
            .ToArray();
        var entries = definitions.Select(card => new DeckEntry(card.Id, 2)).ToArray();
        var protocol = CompiledGameProtocol.Compile(GameProtocolDefinition.DefaultV0 with
        {
            ProtocolId = "eota.test.mulligan",
            MulliganEnabled = true,
            RequiredDeckSize = 8,
            MaxCopiesPerCard = 2,
            OpeningHandSize = 3,
            HandLimit = 8,
            DeckConstructionPolicy = DeckConstructionPolicy.DevelopmentAnySource
        });
        var deck = DeckDefinition.Create(Profession.Neutral, entries);
        var result = MatchFactory.Create(new MatchCreationRequest(
            protocol,
            RuleContentPack.Create("eota.card/v2", definitions),
            987654321,
            deck,
            deck));

        return Assert.IsType<MatchState>(result.State);
    }

    private static PlayerState GetPlayer(MatchState state, PlayerId playerId) =>
        state.Players.Single(player => player.Id == playerId);

    private static CardInstanceState GetCard(MatchState state, CardInstanceId cardInstanceId) =>
        state.CardInstances.Single(card => card.Id == cardInstanceId);
}
