using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eota.Client.Desktop;
using Eota.Client.Transport.WebSocket;
using Eota.Server.Infrastructure;
using Eota.Transport.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Eota.Server.IntegrationTests;

public sealed class P10AdmissionTests
{
    [Fact]
    public async Task DifferentLegalDecksRequireBothReadyAndUseServerOwnedRules()
    {
        await using var fixture = await P10Room.StartAsync();
        using var one = fixture.Client("playerOne", 0); using var two = fixture.Client("playerTwo", 1); using var observer = fixture.Client("spectator", 2);
        var description = await one.InspectAsync();
        Assert.False(description.Started);
        Assert.False((await one.ReadyAsync(fixture.Catalog.DefaultDeck("Guardian"), fixture.Catalog)).Started);
        Assert.Null(fixture.Room.Actor);
        await two.InspectAsync();
        Assert.True((await two.ReadyAsync(fixture.Catalog.DefaultDeck("Arcanist"), fixture.Catalog)).Started);
        await one.InspectAsync(); await observer.InspectAsync();
        await using var first = await one.ConnectAsync(); await using var second = await two.ConnectAsync(); await using var spectator = await observer.ConnectAsync();
        Assert.Equal("Guardian", first.Store.View!.Players[0].Profession);
        Assert.Equal("Arcanist", second.Store.View!.Players[1].Profession);
        Assert.Null(spectator.Store.View!.Private);
        var minion = first.Store.View.Private!.Hand.First(c => c.CardKind == "Minion");
        Assert.Equal(fixture.Catalog.Cards.Single(c => c.Id == minion.PrototypeId).Attack, minion.Attack);
        // Pretending to have the approved hash does not allow submitting a stat override.
        var raw = ContractJson.Serialize(new RoomAdmission(1, fixture.Catalog.RuleHash, description.ProtocolHash,
            DesktopRoomClient.ToRoomDeck(fixture.Catalog.DefaultDeck("Guardian")), description.RoomSettingsHash, fixture.Catalog.CardRules));
        var forged = JsonNode.Parse(raw)!; forged["deck"]!["cards"]![0]!["attack"] = 999999;
        using var http = fixture.Http(0); using var response = await http.PostAsync(fixture.HttpAddress("ready", "playerOne"), new StringContent(forged.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, (await fixture.Room.Actor!.GetDiagnosticsAsync()).AcceptedCommandCount);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("copies")]
    [InlineData("profession")]
    [InlineData("token")]
    [InlineData("duplicate")]
    [InlineData("size")]
    public async Task ServerIndependentlyRejectsIllegalDecks(string mutation)
    {
        await using var fixture = await P10Room.StartAsync();
        using var client = fixture.Client("playerOne", 0); await client.InspectAsync();
        var deck = fixture.Catalog.DefaultDeck("Guardian"); var cards = deck.Cards.ToArray();
        switch (mutation)
        {
            case "unknown": cards[0] = cards[0] with { Id = "MY-999999-ATTACK-CARD" }; break;
            case "copies": cards[0] = cards[0] with { Copies = 100 }; break;
            case "profession": deck = deck with { Profession = "Arcanist" }; break;
            case "token": cards[0] = cards[0] with { Id = "EOTA-TOKEN-GUA-MIN-001" }; break;
            case "duplicate": cards = [.. cards, cards[0]]; break;
            case "size": cards = cards[1..]; break;
        }
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => client.ReadyAsync(deck with { Cards = [.. cards] }, fixture.Catalog));
        Assert.Contains("invalid-deck", error.Message, StringComparison.Ordinal);
        Assert.False((await client.InspectAsync()).PlayerOneReady);
        Assert.Null(fixture.Room.Actor);
    }

    [Fact]
    public async Task ChangedRulesProtocolAndStolenSeatNameCannotBypassAdmission()
    {
        await using var fixture = await P10Room.StartAsync();
        using var stolenSeat = fixture.Client("playerTwo", 0);
        await Assert.ThrowsAsync<InvalidDataException>(() => stolenSeat.InspectAsync());
        using var one = fixture.Client("playerOne", 0);
        var description = await one.InspectAsync();
        using var http = fixture.Http(0);
        foreach (var pair in new[] { ("invalid-hash", description.ProtocolHash), (fixture.Catalog.RuleHash, new string('0', 64)) })
        {
            var admission = new RoomAdmission(1, pair.Item1, pair.Item2, DesktopRoomClient.ToRoomDeck(fixture.Catalog.DefaultDeck("Guardian")),
                description.RoomSettingsHash, fixture.Catalog.CardRules);
            using var response = await http.PostAsync(fixture.HttpAddress("ready", "playerOne"), new StringContent(ContractJson.Serialize(admission), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var socket = new ClientWebSocket(); socket.Options.CollectHttpResponseDetails = true;
        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(new UriBuilder(fixture.Invite("playerOne", 0)) { Fragment = "" }.Uri, CancellationToken.None));
        Assert.Equal(HttpStatusCode.Unauthorized, socket.HttpStatusCode);
        Assert.Null(fixture.Room.Actor);
    }

    [Theory]
    [InlineData("cardRules", false)]
    [InlineData("cardRules", true)]
    [InlineData("roomSettingsHash", false)]
    [InlineData("roomSettingsHash", true)]
    public async Task UnlimitedRoomsRejectMissingOrNullAdmissionFieldsEvenWithMatchingPackHash(string property, bool explicitNull)
    {
        await using var fixture = await P10Room.StartAsync();
        var description = await fixture.Room.DescribeAsync(Audience.PlayerOne);
        var admission = new RoomAdmission(1, fixture.Catalog.RuleHash, description.ProtocolHash,
            DesktopRoomClient.ToRoomDeck(fixture.Catalog.DefaultDeck("Guardian")), description.RoomSettingsHash, fixture.Catalog.CardRules);
        var document = JsonNode.Parse(ContractJson.Serialize(admission))!.AsObject();
        if (explicitNull) { document[property] = null; } else { Assert.True(document.Remove(property)); }
        using var http = fixture.Http(0);
        using var response = await http.PostAsync(fixture.HttpAddress("ready", "playerOne"), new StringContent(document.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False((await fixture.Room.DescribeAsync(Audience.PlayerOne)).PlayerOneReady);
        Assert.Null(fixture.Room.Actor);
    }

    [Fact]
    public async Task AConfirmedDeckIsLockedButReorderedRetryIsIdempotent()
    {
        await using var fixture = await P10Room.StartAsync(); using var client = fixture.Client("playerOne", 0);
        await client.InspectAsync();
        var deck = fixture.Catalog.DefaultDeck("Guardian");
        var original = await client.ReadyAsync(deck, fixture.Catalog);
        var again = await client.ReadyAsync(deck with { Cards = [.. deck.Cards.Reverse()] }, fixture.Catalog);
        Assert.Equal(original.OwnDeckHash, again.OwnDeckHash);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ReadyAsync(fixture.Catalog.DefaultDeck("Hunter"), fixture.Catalog));
    }

    [Fact]
    public async Task WebSocketChecksCredentialsAndBothHashesEvenAfterTheRoomStarts()
    {
        await using var fixture = await P10Room.StartAsync();
        using var one = fixture.Client("playerOne", 0); using var two = fixture.Client("playerTwo", 1);
        var description = await one.InspectAsync(); await two.InspectAsync();
        await one.ReadyAsync(fixture.Catalog.DefaultDeck("Guardian"), fixture.Catalog); await two.ReadyAsync(fixture.Catalog.DefaultDeck("Hunter"), fixture.Catalog);
        var endpoint = new UriBuilder(fixture.Invite("playerOne", 0)) { Fragment = "" }.Uri;
        await Assert.ThrowsAsync<WebSocketException>(() => WebSocketGameTransport.ConnectAuthenticatedAsync(endpoint, "secure-test", fixture.Catalog.RuleHash, new string('0', 64), fixture.Tokens[0], description.RoomSettingsHash));
        await Assert.ThrowsAsync<InvalidDataException>(() => WebSocketGameTransport.ConnectAuthenticatedAsync(endpoint, "secure-test", new string('0', 64), description.ProtocolHash, fixture.Tokens[0], description.RoomSettingsHash));
        await Assert.ThrowsAsync<WebSocketException>(() => WebSocketGameTransport.ConnectAuthenticatedAsync(endpoint, "secure-test", fixture.Catalog.RuleHash, description.ProtocolHash, fixture.Tokens[1], description.RoomSettingsHash));
        await Assert.ThrowsAsync<WebSocketException>(() => WebSocketGameTransport.ConnectAuthenticatedAsync(endpoint, "secure-test", fixture.Catalog.RuleHash, description.ProtocolHash, fixture.Tokens[0]));
        await Assert.ThrowsAsync<WebSocketException>(() => WebSocketGameTransport.ConnectAuthenticatedAsync(endpoint, "secure-test", fixture.Catalog.RuleHash, description.ProtocolHash, fixture.Tokens[0], new string('0', 64)));
    }
}

internal sealed class P10Room : IAsyncDisposable
{
    private readonly WebApplication _host;
    public DesktopCatalog Catalog { get; } = new(Fixture.Root);
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "eota-p10-" + Guid.NewGuid().ToString("N"));
    public string[] Tokens { get; } = Enumerable.Range(0, 3).Select(_ => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()).ToArray();
    public HostedRoom Room => _host.Services.GetRequiredService<HostedRoom>();
    private P10Room(WebApplication host, string directory, string[] tokens) { _host = host; Directory = directory; Tokens = tokens; }
    public static async Task<P10Room> StartAsync(RoomTiming? timing = null, DesktopProtocol? protocol = null)
    {
        var catalog = new DesktopCatalog(Fixture.Root); var directory = Path.Combine(Path.GetTempPath(), "eota-p10-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var tokens = Enumerable.Range(0, 3).Select(_ => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()).ToArray();
        var hashes = tokens.Select(t => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(t))).ToLowerInvariant()).ToArray();
        var config = new HostedRoomConfiguration(1, "secure-test", "测试房间", 146, catalog.RuleHash, (protocol ?? DesktopProtocol.Default).Json,
            new DesktopContentEditor(catalog).Export().Cards, hashes[0], hashes[1], hashes[2], Timing: timing);
        var path = Path.Combine(directory, "room.json"); File.WriteAllText(path, JsonSerializer.Serialize(config));
        var host = await Host.Program.BuildAsync(["--room", path, "--urls", "http://127.0.0.1:0", "--Logging:LogLevel:Default", "Warning"]);
        await host.StartAsync(); return new(host, directory, tokens);
    }
    public Uri Invite(string role, int credential) => new UriBuilder(_host.Urls.Single()) { Scheme = "ws", Path = "/matches/secure-test/ws", Query = "audience=" + role, Fragment = "token=" + Tokens[credential] }.Uri;
    public DesktopRoomClient Client(string role, int credential) => new(Invite(role, credential));
    public Uri HttpAddress(string action, string role) => new UriBuilder(_host.Urls.Single()) { Path = "/matches/secure-test/" + action, Query = "audience=" + role }.Uri;
    public HttpClient Http(int credential) { var client = new HttpClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Tokens[credential]); return client; }
    public async ValueTask DisposeAsync() { await _host.StopAsync(); await _host.DisposeAsync(); System.IO.Directory.Delete(Directory, recursive: true); }
}
