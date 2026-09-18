using System.Collections.Immutable;
using System.Text.Json;
using Eota.Client.AI;
using Eota.Client.Core;
using Eota.Client.Desktop;
using Eota.Client.Transport.InProcess;
using Eota.Content.Compiler;
using Eota.Kernel.Commands;
using Eota.Kernel.Content;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Protocols;
using Eota.Kernel.Resolution;
using Eota.Server.Application;
using Eota.Transport.Contracts;

namespace Eota.Server.IntegrationTests;

public sealed class CardSetRedesignTests
{
    private static readonly JsonSerializerOptions ReportJson = new() { WriteIndented = true };
    private static readonly Lazy<DesktopCatalog> Catalog = new(() => new DesktopCatalog(Fixture.Root));
    private static readonly Lazy<RuleContentPack> Rules = new(() => CardContentCompiler.Compile(
        Directory.EnumerateFiles(Path.Combine(Fixture.Root, "Content", "Source", "Cards"), "*.json", SearchOption.AllDirectories)
            .Select(path => new ContentSourceDocument(path, File.ReadAllText(path)))).Content!.Rules);

    [Fact]
    public void EachProfessionHasItsDesignedCardCountAndThreeLegalDistinctDecks()
    {
        var catalog = Catalog.Value;
        Assert.Equal(282, catalog.Cards.Length);
        Assert.Equal(22, catalog.Cards.Count(card => card.Profession == "Neutral" && card.Source == "Core"));
        Assert.Equal(15, catalog.ArchetypeDecks.Length);
        foreach (var profession in new[] { "Guardian", "Arcanist", "Artisan", "Hunter", "Soulweaver" })
        {
            Assert.Equal(50, catalog.Cards.Count(card => card.Profession == profession && card.Source == "Core"));
            Assert.Equal(2, catalog.Cards.Count(card => card.Profession == profession && card.Source == "Token"));
            var decks = catalog.ArchetypeDecks.Where(deck => deck.Profession == profession).ToArray();
            Assert.Equal(3, decks.Length); Assert.Equal(3, decks.Select(deck => deck.Name).Distinct().Count());
            foreach (var deck in decks)
            {
                Assert.Empty(catalog.ValidateDeck(deck));
                Assert.Equal(40, deck.Cards.Sum(entry => entry.Copies));
                Assert.All(deck.Cards, entry => Assert.InRange(entry.Copies, 1, 3));
                Assert.InRange(deck.Cards.Sum(entry => entry.Copies * catalog.Cards.Single(card => card.Id == entry.Id).Cost) / 40.0, 2, 4);
            }
            var defaultDeck = catalog.DefaultDeck(profession);
            Assert.Equal(decks[0].Cards, defaultDeck.Cards);
            Assert.Equal(profession, defaultDeck.Name);
            foreach (var pair in decks.SelectMany((one, index) => decks.Skip(index + 1).Select(two => (one, two))))
            {
                var first = pair.one.Cards.Select(card => card.Id).ToHashSet(StringComparer.Ordinal);
                Assert.True(pair.two.Cards.Count(card => !first.Contains(card.Id)) >= 5);
            }
        }
    }

    [Fact]
    public void CustomConstructionStillBuildsALegalDeckWhenFortyCardTemplatesDoNotFit()
    {
        var protocol = DesktopProtocol.Parse(DesktopProtocol.Default.Json);
        var root = System.Text.Json.Nodes.JsonNode.Parse(protocol.Json)!;
        root["protocol"]!["requiredDeckSize"] = 24;
        root["protocol"]!["minCopiesPerCard"] = 2;
        root["protocol"]!["maxCopiesPerCard"] = 2;
        var custom = DesktopProtocol.Parse(root.ToJsonString());
        foreach (var profession in new[] { "Guardian", "Arcanist", "Artisan", "Hunter", "Soulweaver" })
        {
            var deck = Catalog.Value.DefaultDeck(profession, custom);
            Assert.Equal(24, deck.Cards.Sum(card => card.Copies));
            Assert.All(deck.Cards, card => Assert.Equal(2, card.Copies));
            Assert.Empty(Catalog.Value.ValidateDeck(deck, custom));
        }
    }

    [Theory]
    [InlineData("EOTA-CORE-ARC-FLD-002", 0)]
    [InlineData("EOTA-CORE-ARC-FLD-009", 1)]
    public void EtherFieldsEnableNextTurnResonanceAndBloodAltarPaysLife(string field, long lifePerTurn)
    {
        const string student = "EOTA-CORE-ARC-MIN-001";
        var state = TwoCardMatch(student, field);
        state = Plan(state, PlayerId.One, student);
        state = Plan(state, PlayerId.One, field);
        state = Resolve(state);
        Assert.Equal(1, state.Lanes[0].PlayerOne.EtherActivation);
        Assert.Equal(0, state.Lanes[0].PlayerTwo.EtherActivation);
        Assert.Equal(30 - lifePerTurn, state.Players[0].HeroHealth);
        Assert.Equal(30, state.Players[1].HeroHealth);
        state = Resolve(state);
        Assert.Equal(26, state.Players[1].HeroHealth);
        Assert.Equal(30 - lifePerTurn * 2, state.Players[0].HeroHealth);
        Assert.Equal(2, state.Entities.OfType<MinionEntityState>().Single().Attack);
    }

    [Fact]
    public void PoisonArrowMinionStopsLosingHealthAfterItsTwoEndSteps()
    {
        const string poison = "EOTA-CORE-HUN-MIN-016", target = "EOTA-CORE-GUA-MIN-003";
        var state = Plan(Plan(TwoCardMatch(poison, target), PlayerId.One, poison), PlayerId.Two, target);
        state = Resolve(state);
        Assert.Equal(4, state.Entities.OfType<MinionEntityState>().Single(entity => entity.ControllerId == PlayerId.Two).CurrentHealth);
        state = Resolve(state);
        var survivor = state.Entities.OfType<MinionEntityState>().Single(entity => entity.ControllerId == PlayerId.Two);
        Assert.Equal(1, survivor.CurrentHealth); Assert.Empty(survivor.AttachedEffects);
        state = Resolve(state);
        Assert.Equal(1, state.Entities.OfType<MinionEntityState>().Single().CurrentHealth);
    }

    private static MatchState TwoCardMatch(string one, string two)
    {
        var protocol = CompiledGameProtocol.Compile(GameProtocolDefinition.DefaultV0 with
        {
            RequiredDeckSize = 2, OpeningHandSize = 2, HandLimit = 9, InitialPlayerCost = 5, InitialMaxCost = 5,
            CardsDrawnPerTurn = 0, MulliganEnabled = false, DeckConstructionPolicy = DeckConstructionPolicy.DevelopmentAnySource
        });
        var deck = DeckDefinition.Create(Profession.Neutral, new[] { one, two }.Select(id => new DeckEntry(new CardPrototypeId(id), 1)));
        return MatchFactory.Create(new(protocol, Rules.Value, 146, deck, deck)).State!;
    }

    private static MatchState Plan(MatchState state, PlayerId player, string prototype)
    {
        var card = state.CardInstances.Single(card => card.OwnerId == player && card.CurrentPrototypeId.Value == prototype);
        var result = MatchCommandProcessor.Accept(state, new PlanCardCommand(player, state.Players.Single(value => value.Id == player).CommandRevision, card.Id, new LaneId(0)));
        Assert.True(result.IsAccepted); return result.State;
    }

    private static MatchState Resolve(MatchState state)
    {
        foreach (var player in state.Players) { state = MatchCommandProcessor.Accept(state, new SubmitTurnCommand(player.Id, player.CommandRevision)).State; }
        var result = TurnResolver.ResolveReadyTurn(state); Assert.True(result.CompletedTurn); return result.State;
    }

    // A reproducible playability sample, not a competitive balance estimate.
    // Every inter-profession archetype pairing is played from both seats.
    [Theory]
    [InlineData("Guardian", "Arcanist")]
    [InlineData("Guardian", "Hunter")]
    [InlineData("Guardian", "Artisan")]
    [InlineData("Arcanist", "Hunter")]
    [InlineData("Arcanist", "Artisan")]
    [InlineData("Hunter", "Artisan")]
    [InlineData("Soulweaver", "Guardian")]
    [InlineData("Soulweaver", "Arcanist")]
    [InlineData("Soulweaver", "Hunter")]
    [InlineData("Soulweaver", "Artisan")]
    public async Task ArchetypesPlayThroughNormalClientsFromBothSeats(string firstProfession, string secondProfession)
    {
        var results = new List<object>();
        var catalog = Catalog.Value;
        var directory = Path.Combine(Fixture.Root, "artifacts", "CardSetRedesign"); Directory.CreateDirectory(directory);
        foreach (var firstDeck in catalog.ArchetypeDecks.Where(deck => deck.Profession == firstProfession))
        foreach (var secondDeck in catalog.ArchetypeDecks.Where(deck => deck.Profession == secondProfession))
        foreach (var reverse in new[] { false, true })
        {
            var one = reverse ? secondDeck : firstDeck; var two = reverse ? firstDeck : secondDeck;
            var request = new MatchCreationRequest(ProtocolCompiler.Compile("default", DesktopProtocol.Default.Json).Protocol!, Rules.Value, 20260914UL, Deck(one), Deck(two));
            await using var actor = new MatchActor("cardset-review", request);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await using var first = new GameClient(await InProcessGameTransport.ConnectAsync(actor, Audience.PlayerOne));
            await using var second = new GameClient(await InProcessGameTransport.ConnectAsync(actor, Audience.PlayerTwo));
            await first.SynchronizeAsync(deadline.Token); await second.SynchronizeAsync(deadline.Token);
            await using var botOne = new AiPlayer(first, new AiPolicy(AiDifficulty.Normal, reverse ? 23UL : 11UL, catalog.AiCards), TimeSpan.Zero);
            await using var botTwo = new AiPlayer(second, new AiPolicy(AiDifficulty.Normal, reverse ? 11UL : 23UL, catalog.AiCards), TimeSpan.Zero);
            while (true)
            {
                var observed = first.Store.State;
                Assert.NotEqual("Faulted", botOne.Status.State); Assert.NotEqual("Faulted", botTwo.Status.State);
                if (observed.View!.Status != "Active" || observed.View.Turn > 100) { break; }
                await first.Store.WaitForChangeAsync(observed, deadline.Token);
            }
            await botOne.DisposeAsync(); await botTwo.DisposeAsync();
            var capture = await actor.CaptureReplayAsync(deadline.Token);
            var view = first.Store.View!;
            results.Add(new { one = one.Name, two = two.Name, firstProfession = one.Profession, secondProfession = two.Profession,
                view.Outcome, view.Status, view.Turn, capture.StateHash, commands = capture.Commands.Length,
                reachedTurnLimit = view.Status == "Active" && view.Turn > 100,
                players = view.Players,
                battlefield = view.Entities,
                rejected = botOne.Status.RejectedCommands + botTwo.Status.RejectedCommands });
            File.WriteAllText(Path.Combine(directory, firstProfession + "-" + secondProfession + ".json"), JsonSerializer.Serialize(results, ReportJson));
            Assert.NotEqual("Failed", view.Status);
            Assert.Equal(0, botOne.Status.RejectedCommands + botTwo.Status.RejectedCommands);
            // No-fatigue games can run out of winning moves. Keep these as explicit
            // unfinished samples; never report a turn-limit stop as a victory or draw.
            Assert.True(view.Status == "Finished" || view.Status == "Active" && view.Turn > 100);
        }
    }

    private static DeckDefinition Deck(DesktopDeck deck) => DeckDefinition.Create(Enum.Parse<Profession>(deck.Profession),
        deck.Cards.Select(card => new DeckEntry(new CardPrototypeId(card.Id), card.Copies)));
}
