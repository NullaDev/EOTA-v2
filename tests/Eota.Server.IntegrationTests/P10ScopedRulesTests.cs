using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Eota.Client.Desktop;
using Eota.Server.Infrastructure;
using Eota.Transport.Contracts;

namespace Eota.Server.IntegrationTests;

public sealed class P10ScopedRulesTests
{
    [Fact]
    public async Task UnusedChangesAndExtraCardsAllowDifferentPacksAndSurviveRestore()
    {
        await using var room = await P10Room.StartAsync();
        var editor = new DesktopContentEditor(room.Catalog);
        ChangeAttack(editor, "EOTA-CORE-ARC-MIN-001");
        editor.SaveCard(DesktopContentEditor.NewCard());
        var path = Path.Combine(room.Directory, "custom.json"); editor.SavePack(path);
        var custom = new DesktopCatalog(Fixture.Root, path);
        Assert.NotEqual(room.Catalog.RuleHash, custom.RuleHash);
        using var one = room.Client("playerOne", 0); using var two = room.Client("playerTwo", 1);
        await one.InspectAsync(); await two.InspectAsync();
        await one.ReadyAsync(room.Catalog.DefaultDeck("Guardian"), custom);
        Assert.True((await two.ReadyAsync(room.Catalog.DefaultDeck("Hunter"), room.Catalog)).Started);
        var info = await one.InspectAsync();
        Assert.Equal("Guardian", info.SuggestedDeck!.Profession);
        Assert.DoesNotContain("EOTA-CORE-HUN", ContractJson.Serialize(info), StringComparison.Ordinal);
        await using var session = await DesktopSession.JoinRoomAsync(one);
        Assert.Equal(room.Catalog.RuleHash, session.Client.Store.View!.RuleContentHash);
        await using var restored = new HostedRoom(Path.Combine(room.Directory, "room.json"));
        Assert.NotNull(restored.Actor);
        Assert.Equal((await room.Room.Actor!.GetDiagnosticsAsync()).StateHash, (await restored.Actor!.GetDiagnosticsAsync()).StateHash);
    }

    [Theory]
    [InlineData("own")]
    [InlineData("opponent")]
    [InlineData("token")]
    public async Task EitherSeatsDefinitionsMustCoverBothDecksAndTheirDependencies(string mutation)
    {
        await using var room = await P10Room.StartAsync();
        var editor = new DesktopContentEditor(room.Catalog);
        var own = room.Catalog.DefaultDeck("Guardian"); var opponent = room.Catalog.DefaultDeck("Hunter");
        var id = mutation == "token" ? "EOTA-TOKEN-GUA-MIN-001"
            : (mutation == "own" ? own : opponent).Cards.First(entry => entry.Id.Contains(mutation == "own" ? "GUA-MIN" : "HUN-MIN", StringComparison.Ordinal)).Id;
        ChangeAttack(editor, id);
        var path = Path.Combine(room.Directory, "different.json"); editor.SavePack(path);
        var custom = new DesktopCatalog(Fixture.Root, path);
        using var one = room.Client("playerOne", 0); using var two = room.Client("playerTwo", 1);
        await one.InspectAsync(); await two.InspectAsync();
        if (mutation == "opponent")
        {
            await one.ReadyAsync(own, custom);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => two.ReadyAsync(opponent, room.Catalog));
            Assert.Contains("deck-rule-mismatch: playerOne", error.Message, StringComparison.Ordinal);
            Assert.False((await two.InspectAsync()).PlayerTwoReady);
            // The first seat can synchronize definitions and retry its already locked deck.
            await one.ReadyAsync(own, room.Catalog);
            Assert.True((await two.ReadyAsync(opponent, room.Catalog)).Started);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => one.ReadyAsync(own, custom));
            Assert.Contains("deck-rule-mismatch", error.Message, StringComparison.Ordinal);
            Assert.False((await one.InspectAsync()).PlayerOneReady);
            Assert.Null(room.Room.Actor);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingUnusedDefinitionsAreAllowedButMissingReferencedDefinitionsAreRejected(bool removeRequired)
    {
        await using var room = await P10Room.StartAsync();
        var deck = room.Catalog.DefaultDeck("Guardian");
        var id = removeRequired ? "EOTA-TOKEN-GUA-MIN-001" : "EOTA-CORE-ARC-MIN-001";
        var info = await room.Room.DescribeAsync(Audience.PlayerOne);
        var admission = new RoomAdmission(1, new string('a', 64), info.ProtocolHash, DesktopRoomClient.ToRoomDeck(deck),
            info.RoomSettingsHash, room.Catalog.CardRules.Remove(id));
        if (removeRequired) { await Assert.ThrowsAsync<InvalidDataException>(() => room.Room.AdmitAsync(Audience.PlayerOne, admission)); }
        else { Assert.True((await room.Room.AdmitAsync(Audience.PlayerOne, admission)).PlayerOneReady); }
    }

    [Fact]
    public async Task RuleMismatchInTheSecondSeatsCopyOfTheFirstDeckAlsoRejectsStart()
    {
        await using var room = await P10Room.StartAsync();
        var one = room.Catalog.DefaultDeck("Guardian"); var two = room.Catalog.DefaultDeck("Hunter");
        var info = await room.Room.DescribeAsync(Audience.PlayerOne);
        await room.Room.AdmitAsync(Audience.PlayerOne, new(1, room.Catalog.RuleHash, info.ProtocolHash, DesktopRoomClient.ToRoomDeck(one), info.RoomSettingsHash, room.Catalog.CardRules));
        var onlyFirst = one.Cards.First(card => card.Id.Contains("GUA-MIN", StringComparison.Ordinal)).Id;
        var rules = room.Catalog.CardRules.SetItem(onlyFirst, new string('b', 64));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => room.Room.AdmitAsync(Audience.PlayerTwo,
            new(1, room.Catalog.RuleHash, info.ProtocolHash, DesktopRoomClient.ToRoomDeck(two), info.RoomSettingsHash, rules)));
        Assert.Contains("playerTwo", error.Message, StringComparison.Ordinal);
        Assert.Null(room.Room.Actor);
    }

    private static void ChangeAttack(DesktopContentEditor editor, string id)
    {
        var card = editor.Read(id); var json = JsonNode.Parse(card.Json)!; json["attack"] = 999999;
        editor.SaveCard(card with { Json = json.ToJsonString() }, id);
    }
}
