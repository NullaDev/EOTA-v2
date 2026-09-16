using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Eota.Server.Application;
using Eota.Transport.Contracts;
using Microsoft.AspNetCore.Http;

namespace Eota.Server.Transport.WebSocket;

public static class WebSocketMatchEndpoint
{
    public static async Task HandleAsync(HttpContext context, MatchActor actor, Audience audience)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(actor);
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        var contentHash = context.Request.Headers[MatchHandshake.RuleContentHashHeader].ToString();
        if (!MatchHandshake.IsRuleContentHash(contentHash))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("rule-content-hash-required", context.RequestAborted).ConfigureAwait(false);
            return;
        }
        if (!string.Equals(contentHash, actor.RuleContentHash, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsync("rule-content-mismatch", context.RequestAborted).ConfigureAwait(false);
            return;
        }
        MatchConnection connection;
        try { connection = await actor.ConnectAsync(audience, context.RequestAborted).ConfigureAwait(false); }
        catch (InvalidOperationException)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        await using var session = connection;
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var sending = SendAsync(socket, connection, lifetime.Token);
        var receiving = ReceiveAsync(socket, connection, lifetime.Token);
        var closeStatus = WebSocketCloseStatus.NormalClosure;
        var closeReason = "closed";
        try
        {
            var completed = await Task.WhenAny(sending, receiving).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or DecoderFallbackException)
        {
            closeStatus = WebSocketCloseStatus.InvalidPayloadData;
            closeReason = "invalid-contract-message";
        }
        catch (Exception error) when (error is ChannelClosedException or IOException or InvalidOperationException)
        {
            closeStatus = WebSocketCloseStatus.PolicyViolation;
            closeReason = "resync-required";
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException)
        {
            closeReason = "connection-ended";
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(sending, receiving).ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or WebSocketException or ChannelClosedException
                or IOException or JsonException or InvalidOperationException or DecoderFallbackException)
            { }
        }

        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await socket.CloseOutputAsync(closeStatus, closeReason, timeout.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or WebSocketException) { }
        }
    }

    private static async Task SendAsync(System.Net.WebSockets.WebSocket socket, MatchConnection connection, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var message = await connection.ReceiveAsync(token).ConfigureAwait(false);
            var bytes = Encoding.UTF8.GetBytes(ContractJson.Serialize(message));
            if (bytes.Length > ContractJson.MaximumServerMessageBytes) { throw new InvalidDataException("snapshot-too-large"); }
            await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
        }
    }

    private static async Task ReceiveAsync(System.Net.WebSockets.WebSocket socket, MatchConnection connection, CancellationToken token)
    {
        var buffer = new byte[4096];
        var rateWindow = System.Diagnostics.Stopwatch.StartNew(); var received = 0;
        while (!token.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            using var fragments = CancellationTokenSource.CreateLinkedTokenSource(token);
            var fragmented = false;
            ValueWebSocketReceiveResult fragment;
            do
            {
                fragment = await socket.ReceiveAsync(buffer.AsMemory(), fragments.Token).ConfigureAwait(false);
                if (!fragment.EndOfMessage && !fragmented) { fragments.CancelAfter(TimeSpan.FromSeconds(5)); fragmented = true; }
                if (fragment.MessageType == WebSocketMessageType.Close) { return; }
                if (fragment.MessageType != WebSocketMessageType.Text || message.Length + fragment.Count > ContractJson.MaximumClientMessageBytes)
                { throw new InvalidDataException("invalid-message-size-or-type"); }
                message.Write(buffer, 0, fragment.Count);
            } while (!fragment.EndOfMessage);

            if (rateWindow.Elapsed >= TimeSpan.FromSeconds(1)) { rateWindow.Restart(); received = 0; }
            if (++received > 60) { throw new InvalidDataException("command-rate-exceeded"); }

            var json = new UTF8Encoding(false, true).GetString(message.GetBuffer(), 0, (int)message.Length);
            var envelope = ContractJson.Deserialize<ClientEnvelope>(json);
            await connection.SendAsync(envelope, token).ConfigureAwait(false);
        }
    }
}
