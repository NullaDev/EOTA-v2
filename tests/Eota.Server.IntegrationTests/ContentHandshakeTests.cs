using System.Net;
using System.Net.WebSockets;
using Eota.Client.Transport.WebSocket;
using Eota.Transport.Contracts;

namespace Eota.Server.IntegrationTests;

public sealed class ContentHandshakeTests
{
    [Theory]
    [InlineData("playerOne")]
    [InlineData("playerTwo")]
    [InlineData("spectator")]
    public async Task DifferentCardRulesAreRejectedBeforeJoiningTheMatch(string audience)
    {
        await using var match = await TestMatch.StartAsync(true);
        var endpoint = new UriBuilder(match.Address!) { Scheme = "ws", Path = "/matches/p4-demo/ws", Query = "audience=" + audience }.Uri;
        var before = await match.Actor.GetDiagnosticsAsync();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => WebSocketGameTransport.ConnectAsync(endpoint, "p4-demo", new string('0', 64)));
        Assert.Equal("rule-content-mismatch", error.Message);
        Assert.Equal(before, await match.Actor.GetDiagnosticsAsync());
        await using var accepted = await WebSocketGameTransport.ConnectAsync(endpoint, "p4-demo", match.Actor.RuleContentHash);
        var snapshot = Assert.IsType<ObserverSnapshotPayload>((await accepted.ReceiveAsync()).Payload);
        Assert.Equal(match.Actor.RuleContentHash, snapshot.View.RuleContentHash);
    }

    [Fact]
    public async Task MissingContentHashCannotBypassAdmission()
    {
        await using var match = await TestMatch.StartAsync(true);
        using var socket = new ClientWebSocket(); socket.Options.CollectHttpResponseDetails = true;
        var endpoint = new UriBuilder(match.Address!) { Scheme = "ws", Path = "/matches/p4-demo/ws", Query = "audience=playerOne" }.Uri;
        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(endpoint, CancellationToken.None));
        Assert.Equal(HttpStatusCode.BadRequest, socket.HttpStatusCode);
    }
}
