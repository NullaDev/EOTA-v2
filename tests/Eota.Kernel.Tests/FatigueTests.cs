using Eota.Kernel.Commands;
using Eota.Kernel.Content;
using Eota.Kernel.Effects;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Protocols;
using Eota.Kernel.Resolution;

namespace Eota.Kernel.Tests;

public sealed class FatigueTests
{
    private static readonly CardPrototypeId Card = new("FATIGUE-MINION");

    [Theory]
    [InlineData(DeckExhaustionPolicy.NoFatigue, 30, 0)]
    [InlineData(DeckExhaustionPolicy.IncreasingDamage, 24, 3)]
    [InlineData(DeckExhaustionPolicy.InstantDeath, 0, 3)]
    public void EmptyDrawsUseTheSelectedPolicyAndOnlyAffectTheirPlayer(DeckExhaustionPolicy policy, long health, long count)
    {
        var state = Create(policy); var intents = Draws(state, PlayerId.One, 3);
        var frame = FrameResolver.Resolve(state, intents);
        Assert.Equal(health, frame.State.Players[0].HeroHealth); Assert.Equal(count, frame.State.Players[0].FatigueCount);
        Assert.Equal(30, frame.State.Players[1].HeroHealth); Assert.Equal(0, frame.State.Players[1].FatigueCount);
        Assert.Equal(frame.AfterStateHash, FrameResolver.Resolve(state, intents.Reverse()).AfterStateHash);
        if (policy == DeckExhaustionPolicy.IncreasingDamage)
        { Assert.Equal(new long?[] { 1, 2, 3 }, frame.Receipts.Receipts.Select(receipt => receipt.AppliedValue)); }
        if (policy == DeckExhaustionPolicy.InstantDeath) { Assert.Equal(MatchOutcome.PlayerTwoWon, frame.State.Outcome); }
    }

    [Fact]
    public void FullHandBurnsAnAvailableCardAndOnlyFurtherEmptyDrawsCauseFatigue()
    {
        var state = Create(DeckExhaustionPolicy.IncreasingDamage, opening: 1);
        var frame = FrameResolver.Resolve(state, Draws(state, PlayerId.One, 3));
        var player = frame.State.Players[0];
        Assert.Single(player.Hand); Assert.Single(player.Removed); Assert.Equal(2, player.FatigueCount); Assert.Equal(27, player.HeroHealth);
        Assert.Contains(frame.Events.Events, value => value.Kind == DomainEventKind.CardBurned);
        var next = FrameResolver.Resolve(frame.State, Draws(frame.State, PlayerId.One, 1));
        Assert.Equal(3, next.State.Players[0].FatigueCount); Assert.Equal(24, next.State.Players[0].HeroHealth);
    }

    [Theory]
    [InlineData(1, DomainEventKind.CardBurned)]
    [InlineData(2, DomainEventKind.CardDrawn)]
    public void InstantDeathExecutesOnTheNextDrawAfterTheLastCardAndFinishesTheWholeFrame(int handLimit, DomainEventKind cardEvent)
    {
        var state = Create(DeckExhaustionPolicy.InstantDeath, opening: 1, handLimit: handLimit);
        state = state with { Players = state.Players.SetItem(0, state.Players[0] with { HeroHealth = 1 }) };
        var lastCard = FrameResolver.Resolve(state, Draws(state, PlayerId.One, 1));
        Assert.Empty(lastCard.State.Players[0].Deck);
        Assert.Equal(1, lastCard.State.Players[0].HeroHealth);
        Assert.Equal(0, lastCard.State.Players[0].FatigueCount);
        Assert.Equal(MatchStatus.Active, lastCard.State.Status);
        Assert.Equal(MatchOutcome.None, lastCard.State.Outcome);
        Assert.Contains(lastCard.Events.Events, value => value.Kind == cardEvent && value.PlayerId == PlayerId.One);
        Assert.DoesNotContain(lastCard.Events.Events, value => value.Kind == DomainEventKind.MatchEnded);

        var before = lastCard.State;
        var draw = Draws(before, PlayerId.One, 1)[0];
        var heal = new HealHeroIntent(new IntentId(before.NextIntentId.Value + 1), PlayerId.One, 100);
        var otherDraw = Draws(before, PlayerId.Two, 1)[0] with { Id = new IntentId(before.NextIntentId.Value + 2) };
        AtomicIntent[] intents = [draw, heal, otherDraw];
        var executed = FrameResolver.Resolve(before, intents);
        Assert.Equal(0, executed.State.Players[0].HeroHealth);
        Assert.Equal(1, executed.State.Players[0].FatigueCount);
        Assert.Contains(executed.Receipts.Receipts, receipt => receipt.IntentId == heal.Id && receipt.AppliedValue == 29);
        Assert.Contains(executed.Receipts.Receipts, receipt => receipt.IntentId == draw.Id && receipt.DetailCode == "fatigue-death" && receipt.AppliedValue is null);
        Assert.Empty(executed.State.Players[1].Deck);
        Assert.Equal(30, executed.State.Players[1].HeroHealth);
        Assert.Equal(0, executed.State.Players[1].FatigueCount);
        Assert.Contains(executed.Events.Events, value => value.Kind == cardEvent && value.PlayerId == PlayerId.Two);
        Assert.Single(executed.Events.Events.Where(value => value.Kind == DomainEventKind.MatchEnded));
        Assert.Equal(MatchStatus.Finished, executed.State.Status);
        Assert.Equal(MatchOutcome.PlayerTwoWon, executed.State.Outcome);
        Assert.Equal(executed.AfterStateHash, FrameResolver.Resolve(before, intents.Reverse()).AfterStateHash);
    }

    [Fact]
    public void FilterMissInANonEmptyDeckAndGenerationDoNotCauseFatigue()
    {
        var state = Create(DeckExhaustionPolicy.InstantDeath, opening: 1);
        var draw = Draws(state, PlayerId.One, 1)[0] with { Filter = new CardFilter(Kind: CardKind.Spell) };
        var frame = FrameResolver.Resolve(state, [draw, draw with { Id = new IntentId(draw.Id.Value + 1), Kind = HandRequestKind.Generate, PrototypeId = Card, Filter = null }]);
        Assert.Equal(30, frame.State.Players[0].HeroHealth); Assert.Equal(0, frame.State.Players[0].FatigueCount);
        Assert.Single(frame.State.Players[0].Deck);
    }

    [Theory]
    [InlineData(DeckExhaustionPolicy.IncreasingDamage, 1)]
    [InlineData(DeckExhaustionPolicy.InstantDeath, 30)]
    public void BothPlayersCanDieFromTheSameTurnDrawFrame(DeckExhaustionPolicy policy, long health)
    {
        var state = Create(policy, health: health);
        foreach (var player in new[] { PlayerId.One, PlayerId.Two })
        { state = MatchCommandProcessor.Accept(state, new SubmitTurnCommand(player, state.Players[player.Value].CommandRevision)).State; }
        var result = TurnResolver.ResolveReadyTurn(state);
        Assert.Equal(MatchOutcome.Draw, result.State.Outcome); Assert.Equal(MatchStatus.Finished, result.State.Status);
        Assert.All(result.State.Players, player => Assert.Equal(1, player.FatigueCount));
    }

    [Fact]
    public void FatigueCounterParticipatesInCanonicalStateHash()
    {
        var state = Create(DeckExhaustionPolicy.IncreasingDamage);
        var modified = state with { Players = state.Players.SetItem(0, state.Players[0] with { FatigueCount = 2 }) };
        Assert.NotEqual(MatchStateHasher.Compute(state), MatchStateHasher.Compute(modified));
        Assert.Equal(MatchStateHasher.CanonicalSchemaVersion, state.Protocol.Definition.CanonicalStateVersion);
    }

    private static HandCardIntent[] Draws(MatchState state, PlayerId player, int count) => Enumerable.Range(0, count)
        .Select(index => new HandCardIntent(new IntentId(state.NextIntentId.Value + (ulong)index), player, HandRequestKind.Draw, new DrawAllocationKey(0, "test", index))).ToArray();

    private static MatchState Create(DeckExhaustionPolicy policy, int opening = 2, long health = 30, int? handLimit = null)
    {
        var content = RuleContentPack.Create("eota.card/v2", [new MinionCardDefinition(Card, CardSource.Core, Profession.Neutral, 0, 1, 1, [])]);
        var protocol = CompiledGameProtocol.Compile(GameProtocolDefinition.DefaultV0 with
        { RequiredDeckSize = 2, OpeningHandSize = opening, HandLimit = handLimit ?? opening, DeckExhaustionPolicy = policy, InitialHeroHealth = health });
        var deck = DeckDefinition.Create(Profession.Guardian, [new DeckEntry(Card, 2)]);
        return MatchFactory.Create(new(protocol, content, 146, deck, deck)).State!;
    }
}
