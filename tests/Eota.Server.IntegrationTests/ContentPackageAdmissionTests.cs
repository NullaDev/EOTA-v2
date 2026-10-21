using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Eota.Client.Desktop;
using Eota.Content.Compiler;
using Eota.Server.Infrastructure;
using Eota.Transport.Contracts;

namespace Eota.Server.IntegrationTests;

public sealed class ContentPackageAdmissionTests
{
    [Theory]
    [InlineData("unused")]
    [InlineData("text")]
    [InlineData("extra")]
    [InlineData("same-version")]
    public async Task EveryEnabledPackMustMatchEvenWhenChangedContentIsNotInEitherDeck(string mutation)
    {
        await using var room = await P10Room.StartAsync();
        var standard = room.Catalog.Packages.Single();
        var cards = standard.Cards.ToArray();
        if (mutation is "unused" or "same-version")
        {
            var index = Array.FindIndex(cards, c => c.GetProperty("id").GetString() == "EOTA-CORE-ARC-MIN-001");
            var card = JsonNode.Parse(cards[index].GetRawText())!; card["attack"] = 999999;
            cards[index] = System.Text.Json.JsonSerializer.SerializeToElement(card);
        }
        var changed = ContentPackages.Seal(standard with { Version = mutation == "same-version" ? standard.Version : "2.0.0", Cards = [.. cards],
            Texts = mutation == "text" ? standard.Texts.SetItem("test.extra", "extra") : standard.Texts });
        var extra = ContentPackages.Seal(new(ContentPackage.Schema, "extra", "3.0.0", "额外内容", "", [], [],
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty, []));
        var catalog = new DesktopCatalog(Fixture.Root, mutation == "extra" ? [standard, extra] : [changed]);
        using var one = new DesktopRoomClient(room.Invite("playerOne", 0), catalog.ContentPacks);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => one.InspectAsync());
        Assert.Contains("content-pack-mismatch", error.Message, StringComparison.Ordinal);
        Assert.Contains(mutation == "extra" ? "3.0.0" : changed.Version, error.Message, StringComparison.Ordinal);
        Assert.Contains("主机版本", error.Message, StringComparison.Ordinal);
        var info = await room.Room.DescribeAsync(Audience.PlayerOne);
        var admission = new RoomAdmission(2, catalog.RuleHash, info.ProtocolHash, DesktopRoomClient.ToRoomDeck(room.Catalog.DefaultDeck("Guardian")),
            info.RoomSettingsHash, catalog.CardRules, catalog.ContentPacks);
        using var http = room.Http(0);
        using var response = await http.PostAsync(room.HttpAddress("ready", "playerOne"), new StringContent(ContractJson.Serialize(admission), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("主机版本", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Null(room.Room.Actor);
    }

    [Fact]
    public async Task IdenticalPackagesSurviveRoomRestoreAndSpectatorMustHaveThem()
    {
        await using var room = await P10Room.StartAsync();
        using var one = room.Client("playerOne", 0); using var two = room.Client("playerTwo", 1);
        await one.InspectAsync(); await two.InspectAsync();
        await one.ReadyAsync(room.Catalog.DefaultDeck("Guardian"), room.Catalog);
        await two.ReadyAsync(room.Catalog.DefaultDeck("Hunter"), room.Catalog);
        await using var restored = new HostedRoom(Path.Combine(room.Directory, "room.json"));
        Assert.NotNull(restored.Actor);
        var original = await room.Room.DescribeAsync(Audience.PlayerOne);
        Assert.Equal(RoomContentPacks.Hash(original.ContentPacks), RoomContentPacks.Hash((await restored.DescribeAsync(Audience.PlayerOne)).ContentPacks));
        using var spectator = new DesktopRoomClient(room.Invite("spectator", 2),
            [room.Catalog.ContentPacks[0] with { Version = "0.9.0", Hash = new string('b', 64) }]);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => spectator.InspectAsync());
        Assert.Contains("0.9.0", error.Message, StringComparison.Ordinal);
        using var uncheckedSpectator = new DesktopRoomClient(room.Invite("spectator", 2));
        await uncheckedSpectator.InspectAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => uncheckedSpectator.ConnectAsync());
    }

    [Fact]
    public async Task MatchingIdentityCannotBypassCardRuleValidation()
    {
        await using var room = await P10Room.StartAsync();
        var deck = room.Catalog.DefaultDeck("Guardian");
        var info = await room.Room.DescribeAsync(Audience.PlayerOne);
        var rules = room.Catalog.CardRules.SetItem(deck.Cards[0].Id, new string('b', 64));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => room.Room.AdmitAsync(Audience.PlayerOne,
            new(2, room.Catalog.RuleHash, info.ProtocolHash, DesktopRoomClient.ToRoomDeck(deck), info.RoomSettingsHash, rules, room.Catalog.ContentPacks)));
        Assert.Contains("deck-rule-mismatch", error.Message, StringComparison.Ordinal);
        Assert.Null(room.Room.Actor);
    }
}
