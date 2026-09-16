using Eota.Client.Core;

namespace Eota.Client.Desktop;

public sealed record RemoteConnectionStatus(string State, int Attempt, string Message);

public sealed partial class DesktopSession
{
    private DesktopRoomClient? _remoteRoom;
    private string? _remoteRuleHash;
    private string? _remoteProtocolHash;
    private string? _remoteSettingsHash;
    private readonly CancellationTokenSource _remoteLifetime = new();
    private readonly SemaphoreSlim _reconnectGate = new(1, 1);
    private Task? _remoteMonitor;
    private RemoteConnectionStatus _remoteStatus = new("Connected", 0, "已连接");
    private int _connectionGeneration;
    public bool CanReconnect => _remoteRoom is not null;
    public RemoteConnectionStatus ConnectionStatus => Volatile.Read(ref _remoteStatus);
    public int ConnectionGeneration => Volatile.Read(ref _connectionGeneration);

    private void StartRemoteMonitor() => _remoteMonitor = MonitorRemoteAsync();

    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_remoteRoom is null) { await Client.SynchronizeAsync(cancellationToken).ConfigureAwait(false); return; }
        await ReconnectCoreAsync(Client, cancellationToken).ConfigureAwait(false);
        if (_remoteMonitor is null || _remoteMonitor.IsCompleted) { StartRemoteMonitor(); }
    }

    private async Task MonitorRemoteAsync()
    {
        var token = _remoteLifetime.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = Client;
                await Task.WhenAny(client.Completion, Task.Delay(TimeSpan.FromSeconds(10), token)).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (client != Client) { continue; }
                if (client.Store.View?.Status != "Active") { return; }
                if (!client.Completion.IsCompleted)
                {
                    using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(token); heartbeat.CancelAfter(TimeSpan.FromSeconds(5));
                    try { await client.SynchronizeAsync(heartbeat.Token).ConfigureAwait(false); continue; }
                    catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { token.ThrowIfCancellationRequested(); }
                }
                client.Store.MarkDisconnected(); var attempt = 0;
                while (!token.IsCancellationRequested)
                {
                    try { await ReconnectCoreAsync(client, token).ConfigureAwait(false); break; }
                    catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException)
                    { Volatile.Write(ref _remoteStatus, new("Blocked", attempt, error.Message)); return; }
                    catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException or System.Net.WebSockets.WebSocketException)
                    {
                        token.ThrowIfCancellationRequested(); attempt++;
                        Volatile.Write(ref _remoteStatus, new("Reconnecting", attempt, $"连接中断，正在重试（{attempt}）…"));
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, attempt * 2)), token).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0) { }
    }

    private async Task ReconnectCoreAsync(GameClient previous, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _remoteLifetime.Token);
        lifetime.CancelAfter(TimeSpan.FromSeconds(12)); var token = lifetime.Token;
        await _reconnectGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (Client != previous && !Client.Completion.IsCompleted) { return; }
            previous.Store.MarkDisconnected();
            Volatile.Write(ref _remoteStatus, new("Reconnecting", ConnectionStatus.Attempt, "连接中断，正在重新连接…"));
            var room = _remoteRoom ?? throw new InvalidOperationException("This session has no reconnect credentials.");
            var description = await room.InspectAsync(token).ConfigureAwait(false);
            if (description.RuleContentHash != _remoteRuleHash || description.ProtocolHash != _remoteProtocolHash || description.RoomSettingsHash != _remoteSettingsHash)
            { throw new InvalidDataException("服务器协议或时限发生变化，请返回大厅重新核对。"); }
            var next = await room.ConnectAsync(token).ConfigureAwait(false);
            try
            {
                if (previous.Store.ObserverViewHash is { } hash)
                { await next.ResumeAsync(new Eota.Transport.Contracts.ObserverResumeCursor(previous.Store.MatchRevision, hash), token).ConfigureAwait(false); }
                token.ThrowIfCancellationRequested();
                if (next.Store.MatchRevision < previous.Store.MatchRevision || next.Store.View!.Audience != previous.Store.View!.Audience)
                { throw new InvalidDataException("服务器恢复的进度或席位与原对局不一致，已停止重连。"); }
            }
            catch { await next.DisposeAsync().ConfigureAwait(false); throw; }
            Interlocked.Exchange(ref _clients[0], next);
            Interlocked.Increment(ref _connectionGeneration);
            Volatile.Write(ref _remoteStatus, new("Connected", 0, "连接已恢复"));
            await previous.DisposeAsync().ConfigureAwait(false);
        }
        finally { _reconnectGate.Release(); }
    }
}
