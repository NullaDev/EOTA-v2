using System.Net.WebSockets;
using System.Text;
using Eota.Client.Core;
using Eota.Transport.Contracts;

namespace Eota.Client.Transport.WebSocket;

public sealed class WebSocketGameTransport : IGameClientTransport
{
    private readonly ClientWebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    public string MatchId { get; }

    private WebSocketGameTransport(ClientWebSocket socket, string matchId)
    {
        _socket = socket;
        MatchId = matchId;
    }

    public static async Task<WebSocketGameTransport> ConnectAsync(Uri endpoint, string matchId, string ruleContentHash, CancellationToken cancellationToken = default)
        => await ConnectAuthenticatedAsync(endpoint, matchId, ruleContentHash, null, null, cancellationToken: cancellationToken).ConfigureAwait(false);

    public static async Task<WebSocketGameTransport> ConnectAuthenticatedAsync(Uri endpoint, string matchId, string ruleContentHash,
        string? protocolHash, string? credential, string? roomSettingsHash = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(matchId);
        if (!MatchHandshake.IsRuleContentHash(ruleContentHash)) { throw new ArgumentException("Invalid rule content hash.", nameof(ruleContentHash)); }
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader(MatchHandshake.RuleContentHashHeader, ruleContentHash);
        if (protocolHash is not null) { socket.Options.SetRequestHeader(MatchHandshake.ProtocolHashHeader, protocolHash); }
        if (roomSettingsHash is not null) { socket.Options.SetRequestHeader(RoomTiming.SettingsHashHeader, roomSettingsHash); }
        if (credential is not null) { socket.Options.SetRequestHeader("Authorization", "Bearer " + credential); }
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return new WebSocketGameTransport(socket, matchId);
        }
        catch (WebSocketException error) when (socket.HttpStatusCode == System.Net.HttpStatusCode.Conflict)
        { socket.Dispose(); throw new InvalidDataException("rule-content-mismatch", error); }
        catch { socket.Dispose(); throw; }
    }

    public async ValueTask SendAsync(ClientEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(ContractJson.Serialize(envelope));
        if (bytes.Length > ContractJson.MaximumClientMessageBytes) { throw new InvalidDataException("client-message-too-large"); }
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await _socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    public async ValueTask<ServerEnvelope> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        ValueWebSocketReceiveResult fragment;
        do
        {
            fragment = await _socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (fragment.MessageType == WebSocketMessageType.Close) { throw new IOException("server-closed; reconnect for a snapshot"); }
            if (fragment.MessageType != WebSocketMessageType.Text || message.Length + fragment.Count > ContractJson.MaximumServerMessageBytes)
            { throw new InvalidDataException("invalid-server-message"); }
            message.Write(buffer, 0, fragment.Count);
        } while (!fragment.EndOfMessage);

        return ContractJson.Deserialize<ServerEnvelope>(new UTF8Encoding(false, true).GetString(message.GetBuffer(), 0, (int)message.Length));
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", timeout.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or WebSocketException) { }
        }

        _socket.Dispose();
        _sendLock.Dispose();
    }
}
