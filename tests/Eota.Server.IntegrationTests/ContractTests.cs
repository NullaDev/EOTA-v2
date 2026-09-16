using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Eota.Transport.Contracts;

namespace Eota.Server.IntegrationTests;

public sealed class ContractTests
{
    [Fact]
    public void UInt64IdentitySurvivesJsonWithoutJavaScriptPrecisionLoss()
    {
        var command = new ClientEnvelope(0, "match", "key", ulong.MaxValue, ulong.MaxValue, new PlanCardPayload(ulong.MaxValue, 0));
        var json = ContractJson.Serialize(command);
        Assert.Contains("\"18446744073709551615\"", json, StringComparison.Ordinal);
        Assert.Equal(command, ContractJson.Deserialize<ClientEnvelope>(json));
    }

    [Theory]
    [InlineData("{\"contractVersion\":0}")]
    [InlineData("{\"contractVersion\":0,\"matchId\":\"p4\",\"clientCommandId\":\"id\",\"clientSequence\":\"1\",\"expectedPlayerRevision\":\"0\",\"payload\":{\"kind\":\"submitTurn\",\"playerId\":1}}")]
    [InlineData("{\"contractVersion\":0,\"matchId\":\"p4\",\"clientCommandId\":\"id\",\"clientSequence\":\"1\",\"expectedPlayerRevision\":\"0\",\"payload\":{\"kind\":\"unknown\"}}")]
    [InlineData("{\"contractVersion\":0,\"matchId\":\"p4\",\"clientCommandId\":\"id\",\"clientSequence\":1,\"expectedPlayerRevision\":\"0\",\"payload\":{\"kind\":\"submitTurn\"}}")]
    [InlineData("{\"contractVersion\":0,\"matchId\":\"p4\",\"clientCommandId\":\"id\",\"clientSequence\":\"18446744073709551616\",\"expectedPlayerRevision\":\"0\",\"payload\":{\"kind\":\"submitTurn\"}}")]
    [InlineData("{\"contractVersion\":0,\"matchId\":\"p4\",\"clientCommandId\":\"id\",\"clientSequence\":\"01\",\"expectedPlayerRevision\":\"0\",\"payload\":{\"kind\":\"submitTurn\"}}")]
    [InlineData("{\"contractVersion\":0,\"matchId\":\"p4\",\"matchId\":\"other\",\"clientCommandId\":\"id\",\"clientSequence\":\"1\",\"expectedPlayerRevision\":\"0\",\"payload\":{\"kind\":\"submitTurn\"}}")]
    public void InvalidWireMessagesAreRejectedBeforeReachingKernel(string json) =>
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<ClientEnvelope>(json));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VersionMatchAndSequenceFailuresAreStableAndDoNotMutateState(bool remote)
    {
        await using var match = await TestMatch.StartAsync(remote);
        await using var transport = await match.ConnectAsync(Audience.PlayerOne);
        await transport.ReceiveAsync();
        var baseline = await match.Actor.GetDiagnosticsAsync();
        var command = new ClientEnvelope(0, match.Actor.MatchId, "key", 1, 0, new SubmitTurnPayload());
        foreach (var (message, code) in new[]
        {
            (command with { ContractVersion = 99 }, "unsupported-contract-version"),
            (command with { MatchId = "other" }, "match-id-mismatch"),
            (command with { ClientSequence = 0 }, "client-sequence-not-increasing"),
            (command with { Payload = new PlanCardPayload(1, -1) }, "invalid-command-payload"),
            (command with { ClientSequence = 1, ClientCommandId = "reused-sequence" }, "client-sequence-not-increasing")
        })
        {
            await transport.SendAsync(message);
            Assert.Equal(code, Assert.IsType<CommandAckPayload>((await transport.ReceiveAsync()).Payload).Code);
        }
        Assert.Equal(baseline, await match.Actor.GetDiagnosticsAsync());
    }

    [Fact]
    public async Task HostHealthAndMalformedSocketDoNotExposeOrCorruptMatchState()
    {
        await using var match = await TestMatch.StartAsync(true);
        using var http = new HttpClient { BaseAddress = match.Address };
        using var health = await http.GetAsync(new Uri("/health", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.DoesNotContain("StateHash", await health.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        using var unknown = await http.GetAsync(new Uri("/matches/unknown/ws?audience=spectator", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader(MatchHandshake.RuleContentHashHeader, match.Actor.RuleContentHash);
        await socket.ConnectAsync(new UriBuilder(match.Address!) { Scheme = "ws", Path = "/matches/p4-demo/ws", Query = "audience=playerOne" }.Uri,
            CancellationToken.None);
        var before = await match.Actor.GetDiagnosticsAsync();
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"contractVersion\":0}"), WebSocketMessageType.Text, true, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[4096];
        WebSocketReceiveResult fragment;
        do { fragment = await socket.ReceiveAsync(buffer, timeout.Token); }
        while (fragment.MessageType != WebSocketMessageType.Close);
        Assert.Equal(WebSocketCloseStatus.InvalidPayloadData, fragment.CloseStatus);
        Assert.Equal(before, await match.Actor.GetDiagnosticsAsync());
    }
}
