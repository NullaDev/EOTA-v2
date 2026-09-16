using System.Collections.Immutable;
using Eota.Client.Desktop;
using Eota.Kernel.Commands;
using Eota.Kernel.Matches;
using Eota.Kernel.Protocols;
using Eota.Kernel.Resolution;
using Eota.Server.Infrastructure;
using Eota.Transport.Contracts;

namespace Eota.Server.IntegrationTests;

public sealed class P10DeckProtocolTests
{
    [Theory]
    [InlineData("coreAndProfessionOrNeutral")]
    [InlineData("coreProfessionOnly")]
    [InlineData("coreAnyProfession")]
    public void DraftAndDefaultDeckFollowConfiguredSizeCopiesAndClassPolicy(string policy)
    {
        var protocol = Protocol(policy); var catalog = new DesktopCatalog(Fixture.Root);
        var draft = new DesktopDeckDraft(catalog, "Guardian", protocol);
        Assert.DoesNotContain(draft.Pool, card => card.Source != "Core");
        if (policy == "coreProfessionOnly") { Assert.All(draft.Pool, card => Assert.Equal("Guardian", card.Profession)); }
        if (policy == "coreAndProfessionOrNeutral") { Assert.All(draft.Pool, card => Assert.True(card.Profession is "Guardian" or "Neutral")); }
        if (policy == "coreAnyProfession") { Assert.Contains(draft.Pool, card => card.Profession == "Arcanist"); }
        draft.Load(catalog.DefaultDeck("Guardian", protocol));
        Assert.Equal(24, draft.Count); Assert.All(draft.Build().Cards, entry => Assert.InRange(entry.Copies, 2, 4));
        Assert.Empty(catalog.ValidateDeck(draft.Build(), protocol));
        Assert.False(draft.Add(draft.Pool[0].Id));
        draft.Clear(); for (var i = 0; i < 4; i++) { Assert.True(draft.Add(draft.Pool[0].Id)); }
        Assert.False(draft.Add(draft.Pool[0].Id));
    }

    [Fact]
    public void ChangingProtocolPreservesCardsToRepairInsteadOfSilentlyRemovingThem()
    {
        var catalog = new DesktopCatalog(Fixture.Root); var draft = new DesktopDeckDraft(catalog, "Guardian");
        draft.Load(catalog.DefaultDeck("Guardian"));
        draft.Remove(draft.Build().Cards[0].Id);
        Assert.True(draft.Add(catalog.Cards.First(card => card.Profession == "Neutral" && card.Source == "Core").Id));
        var old = draft.Build();
        draft.UseProtocol(Protocol("coreProfessionOnly"));
        Assert.Equal(old.Cards.ToArray(), draft.Build().Cards.ToArray()); Assert.Equal(40, draft.Count);
        Assert.NotEmpty(catalog.ValidateDeck(draft.Build(), draft.Protocol));
        Assert.All(draft.Pool, card => Assert.Equal("Guardian", card.Profession));
        var neutral = draft.Build().Cards.First(entry => catalog.Cards.Single(card => card.Id == entry.Id).Profession == "Neutral");
        draft.Remove(neutral.Id); Assert.Equal(39, draft.Count);
    }

    [Fact]
    public void RenameAndDeleteUsePersistentIdentityAndDoNotTouchOtherDecks()
    {
        var directory = Path.Combine(Path.GetTempPath(), "eota-deck-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DesktopDeckStore(directory); var deck = new DesktopCatalog(Fixture.Root).DefaultDeck("Guardian");
            var first = store.Save(deck with { Name = "我的牌组" }); var second = store.Save(deck with { Name = "另一个牌组" });
            Assert.Equal(first, store.Save(deck with { Name = "重命名 / 与路径无关" }, first));
            var reopened = new DesktopDeckStore(directory); Assert.Equal(2, reopened.Ids.Length);
            Assert.Equal("重命名 / 与路径无关", reopened.Read(first).Name);
            reopened.Delete(first); Assert.Equal(second, Assert.Single(new DesktopDeckStore(directory).Ids));
            Assert.Throws<InvalidDataException>(() => reopened.Delete("../outside"));
            Assert.Throws<InvalidDataException>(() => reopened.Delete("..\\outside"));
            Assert.Equal("另一个牌组", reopened.Read(second).Name);
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    }

    [Fact]
    public async Task RoomCanWaitForAnUnknownOpponentAndChecksCustomConstructionOnAdmission()
    {
        var protocol = Protocol("coreProfessionOnly"); await using var room = await P10Room.StartAsync(protocol: protocol);
        using var one = room.Client("playerOne", 0); using var two = room.Client("playerTwo", 1);
        var info = await two.InspectAsync(); Assert.Null(info.SuggestedDeck); Assert.Null(room.Room.Actor);
        await one.InspectAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => one.ReadyAsync(room.Catalog.DefaultDeck("Guardian"), room.Catalog));
        var deck = room.Catalog.DefaultDeck("Guardian", protocol);
        var wrongClass = deck with { Cards = deck.Cards.SetItem(0, new DeckCard("EOTA-CORE-ARC-MIN-001", deck.Cards[0].Copies)) };
        await Assert.ThrowsAsync<InvalidDataException>(() => one.ReadyAsync(wrongClass, room.Catalog));
        await one.ReadyAsync(deck, room.Catalog); Assert.Null(room.Room.Actor);
        Assert.True((await two.ReadyAsync(room.Catalog.DefaultDeck("Hunter", protocol), room.Catalog)).Started);
    }

    [Theory]
    [InlineData("increasingDamage", "Active", 29)]
    [InlineData("instantDeath", "Finished", 0)]
    public async Task FatigueIsVisibleAndReplayableInRealDesktopSessions(string policy, string status, long health)
    {
        var protocol = DesktopProtocol.Default.WithValues(new Dictionary<string, string>
        { ["requiredDeckSize"] = "2", ["openingHandSize"] = "2", ["mulliganEnabled"] = "false", ["deckExhaustionPolicy"] = policy });
        var catalog = new DesktopCatalog(Fixture.Root);
        await using var session = await DesktopSession.LocalAsync(catalog, catalog.DefaultDeck("Guardian", protocol), catalog.DefaultDeck("Hunter", protocol), new(ProtocolJson: protocol.Json));
        Assert.True((await session.SubmitAsync(new SubmitTurnPayload())).Accepted); session.SwitchSeat();
        Assert.True((await session.SubmitAsync(new SubmitTurnPayload())).Accepted);
        await session.Client.SynchronizeAsync();
        Assert.Equal(status, session.Client.Store.View!.Status);
        Assert.All(session.Client.Store.View.Players, player => { Assert.Equal(health, player.HeroHealth); Assert.Equal(1, player.FatigueCount); });
        var directory = Path.Combine(Fixture.Root, "artifacts", "P10Fatigue-" + policy);
        await session.SaveReplayAsync(directory); var replay = DesktopReplay.Load(catalog, Path.Combine(directory, "replay.json"));
        Assert.Equal(session.FinalStateHash, replay.StateHash);
        Assert.All(replay.Final.Players, player => Assert.Equal(1, player.FatigueCount));
    }

    [Fact]
    public void CheckpointRestoresFatigueAndTheNextEmptyDrawKeepsIncreasing()
    {
        var request = Fixture.Load();
        var protocol = CompiledGameProtocol.Compile(request.Protocol.Definition with
        { DeckExhaustionPolicy = DeckExhaustionPolicy.IncreasingDamage, OpeningHandSize = request.Protocol.Definition.RequiredDeckSize,
            HandLimit = request.Protocol.Definition.RequiredDeckSize, MulliganEnabled = false, CardsDrawnPerTurn = 1 });
        var state = MatchFactory.Create(request with { Protocol = protocol }).State!;
        state = NextTurn(state);
        Assert.All(state.Players, player => Assert.Equal(1, player.FatigueCount));
        var restored = MatchCheckpointCodec.Decode(MatchCheckpointCodec.Encode(state), state.Protocol, state.Content);
        Assert.Equal(MatchStateHasher.Compute(state), MatchStateHasher.Compute(restored));
        var expected = NextTurn(state); var actual = NextTurn(restored);
        Assert.Equal(MatchStateHasher.Compute(expected), MatchStateHasher.Compute(actual));
        Assert.All(actual.Players, player => { Assert.Equal(2, player.FatigueCount); Assert.Equal(protocol.Definition.InitialHeroHealth - 3, player.HeroHealth); });

        static MatchState NextTurn(MatchState state)
        {
            foreach (var player in state.Players)
            { state = MatchCommandProcessor.Accept(state, new SubmitTurnCommand(player.Id, player.CommandRevision)).State; }
            return TurnResolver.ResolveReadyTurn(state).State;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(999)]
    [InlineData(long.MaxValue)]
    public void UnsupportedStateFormatsAreRejectedWithoutRewritingPreferences(long version)
    {
        var protocol = Protocol("coreProfessionOnly");
        var document = System.Text.Json.Nodes.JsonNode.Parse(protocol.Json)!;
        document["protocol"]!["canonicalStateVersion"] = version;
        Assert.Throws<InvalidDataException>(() => DesktopProtocol.Parse(document.ToJsonString()));
        Assert.Equal(version, document["protocol"]!["canonicalStateVersion"]!.GetValue<long>());
    }

    private static DesktopProtocol Protocol(string policy) => DesktopProtocol.Default.WithValues(new Dictionary<string, string>
    { ["requiredDeckSize"] = "24", ["minCopiesPerCard"] = "2", ["maxCopiesPerCard"] = "4", ["deckConstructionPolicy"] = policy });
}
