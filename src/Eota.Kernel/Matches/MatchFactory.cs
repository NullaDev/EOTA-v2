using System.Collections.Immutable;
using Eota.Kernel.Content;
using Eota.Kernel.Determinism;
using Eota.Kernel.Primitives;
using Eota.Kernel.Protocols;

namespace Eota.Kernel.Matches;

public static class MatchFactory
{
    public static MatchCreationResult Create(MatchCreationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = ImmutableArray.CreateBuilder<KernelError>();
        var playerOneDeck = NormalizeAndValidateDeck(request.PlayerOneDeck, request.Protocol, request.Content, errors, PlayerId.One);
        var playerTwoDeck = NormalizeAndValidateDeck(request.PlayerTwoDeck, request.Protocol, request.Content, errors, PlayerId.Two);
        if (errors.Count > 0)
        {
            return new MatchCreationResult(null, errors.ToImmutable());
        }

        var manifest = new MatchManifest(
            request.Protocol.Definition.ProtocolId,
            request.Protocol.Definition.ProtocolVersion,
            request.Protocol.Hash,
            request.Content.SchemaVersion,
            request.Content.Hash,
            request.MatchSeed,
            playerOneDeck,
            playerTwoDeck);

        var instances = ImmutableArray.CreateBuilder<CardInstanceState>();
        var playerOneCards = AllocateInstances(PlayerId.One, playerOneDeck, instances, 1);
        var playerTwoCards = AllocateInstances(
            PlayerId.Two,
            playerTwoDeck,
            instances,
            checked((ulong)playerOneCards.Length + 1));

        var nextCardInstanceId = new CardInstanceId(checked((ulong)(playerOneCards.Length + playerTwoCards.Length) + 1));
        var rng = GlobalRuleRng.Create(request.MatchSeed);
        rng = GlobalRuleRng.Shuffle(playerOneCards.AsSpan(), rng);
        rng = GlobalRuleRng.Shuffle(playerTwoCards.AsSpan(), rng);

        var openingHandSize = request.Protocol.Definition.OpeningHandSize;
        var players = ImmutableArray.Create(
            CreatePlayerState(PlayerId.One, playerOneDeck.Profession, playerOneCards, openingHandSize, request.Protocol.Definition),
            CreatePlayerState(PlayerId.Two, playerTwoDeck.Profession, playerTwoCards, openingHandSize, request.Protocol.Definition));
        var handIds = players.SelectMany(player => player.Hand).ToHashSet();
        var cardInstances = instances
            .Select(instance => handIds.Contains(instance.Id) ? instance with { Zone = CardZone.Hand } : instance)
            .ToImmutableArray();
        var lanes = Enumerable.Range(0, request.Protocol.Definition.LaneCount)
            .Select(index => new LaneState(
                new LaneId(index),
                new PlayerLaneState(PlayerId.One, null, null, request.Protocol.Definition.MinEtherActivation, false),
                new PlayerLaneState(PlayerId.Two, null, null, request.Protocol.Definition.MinEtherActivation, false)))
            .ToImmutableArray();

        var state = new MatchState(
            manifest,
            request.Protocol,
            request.Content,
            MatchStatus.Active,
            MatchOutcome.None,
            request.Protocol.Definition.MulliganEnabled ? MatchStage.Mulligan : MatchStage.Planning,
            1,
            0,
            players,
            lanes,
            cardInstances,
            ImmutableArray<BattlefieldEntityState>.Empty,
            ImmutableArray<EntityTombstone>.Empty,
            ImmutableArray<AcceptedCommandRecord>.Empty,
            nextCardInstanceId,
            new EntityId(1),
            new CommandId(1),
            new FrameId(1),
            new WorkItemId(1),
            new IntentId(1),
            new ConflictGroupId(1),
            new ReceiptId(1),
            new EventId(1),
            new TombstoneId(1),
            rng);

        return new MatchCreationResult(state, ImmutableArray<KernelError>.Empty);
    }

    private static DeckDefinition NormalizeAndValidateDeck(
        DeckDefinition deck,
        CompiledGameProtocol protocol,
        RuleContentPack content,
        ImmutableArray<KernelError>.Builder errors,
        PlayerId playerId)
    {
        var orderedEntries = deck.Entries.OrderBy(entry => entry.CardId).ToImmutableArray();
        var parameters = protocol.Definition;
        long totalCards = 0;
        CardPrototypeId? previousId = null;

        foreach (var entry in orderedEntries)
        {
            if (previousId == entry.CardId)
            {
                AddDeckError(errors, playerId, "duplicate-card-entry");
            }

            previousId = entry.CardId;
            if (entry.Copies < parameters.MinCopiesPerCard || entry.Copies > parameters.MaxCopiesPerCard)
            {
                AddDeckError(errors, playerId, "copy-count-out-of-range");
            }

            totalCards = checked(totalCards + entry.Copies);
            if (!content.TryGetCard(entry.CardId, out var card) || card is null)
            {
                AddDeckError(errors, playerId, "unknown-card");
                continue;
            }

            if (!IsCardAllowed(card, deck.Profession, parameters.DeckConstructionPolicy))
            {
                AddDeckError(errors, playerId, "card-not-allowed");
            }
        }

        if (totalCards != parameters.RequiredDeckSize)
        {
            AddDeckError(errors, playerId, "incorrect-deck-size");
        }

        return new DeckDefinition(deck.Profession, orderedEntries);
    }

    public static bool IsCardAllowed(
        CardDefinition card,
        Profession deckProfession,
        DeckConstructionPolicy policy)
    {
        if (policy == DeckConstructionPolicy.DevelopmentAnySource)
        {
            return true;
        }

        return card.Source == CardSource.Core && (policy switch
        {
            DeckConstructionPolicy.CoreAndProfessionOrNeutral => card.Profession == Profession.Neutral || card.Profession == deckProfession,
            DeckConstructionPolicy.CoreProfessionOnly => card.Profession == deckProfession,
            DeckConstructionPolicy.CoreAnyProfession => true,
            _ => false
        });
    }

    private static void AddDeckError(
        ImmutableArray<KernelError>.Builder errors,
        PlayerId playerId,
        string detailCode)
    {
        errors.Add(new KernelError(KernelErrorCode.InvalidDeck, $"player[{playerId.Value}]", detailCode));
    }

    private static CardInstanceId[] AllocateInstances(
        PlayerId ownerId,
        DeckDefinition deck,
        ImmutableArray<CardInstanceState>.Builder instances,
        ulong firstId)
    {
        var cards = new CardInstanceId[deck.Entries.Sum(entry => entry.Copies)];
        var cardIndex = 0;
        var nextId = firstId;
        foreach (var entry in deck.Entries)
        {
            for (var copyIndex = 0; copyIndex < entry.Copies; copyIndex++)
            {
                var id = new CardInstanceId(nextId);
                nextId = checked(nextId + 1);
                cards[cardIndex] = id;
                cardIndex++;
                instances.Add(new CardInstanceState(id, entry.CardId, entry.CardId, ownerId, CardZone.Deck));
            }
        }

        return cards;
    }

    private static PlayerState CreatePlayerState(
        PlayerId playerId,
        Profession profession,
        CardInstanceId[] shuffledDeck,
        int openingHandSize,
        GameProtocolDefinition protocol)
    {
        var hand = shuffledDeck
            .Take(openingHandSize)
            .OrderBy(id => id)
            .ToImmutableArray();
        var deck = shuffledDeck.Skip(openingHandSize).ToImmutableArray();
        return new PlayerState(
            playerId,
            profession,
            protocol.InitialHeroHealth,
            protocol.InitialHeroHealth,
            protocol.InitialPlayerCost,
            protocol.InitialMaxCost,
            deck,
            hand,
            ImmutableArray<PlannedAction>.Empty,
            ImmutableArray<CardInstanceId>.Empty,
            ImmutableArray<CardInstanceId>.Empty,
            protocol.MulliganEnabled
                ? new MulliganState(hand, ImmutableArray<CardInstanceId>.Empty, false)
                : null,
            false,
            0,
            1);
    }
}
