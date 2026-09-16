using System.Collections.Concurrent;
using Eota.Transport.Contracts;

namespace Eota.Client.Core;

public sealed class GameClient : IAsyncDisposable
{
    private readonly IGameClientTransport _transport;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ServerPayload>> _pending = new(StringComparer.Ordinal);
    private readonly Task _reader;
    private ulong _clientSequence;
    private int _disposed;
    private int _readerStopped;
    private readonly TimeSpan _requestTimeout;
    private MatchTimerReading? _timer;
    public MatchTimerReading? Timer => Volatile.Read(ref _timer) is { } timer && Store.View is { Status: "Active" } view
        && timer.Payload.Turn == view.Turn && timer.Payload.Stage == view.Stage ? timer : null;
    public ObserverStore Store { get; }
    public Task Completion => _reader;
    public bool IsClosed => Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _readerStopped) != 0;
    public string? TransportFailureCode { get; private set; }

    public GameClient(IGameClientTransport transport, TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(15);
        if (_requestTimeout <= TimeSpan.Zero) { throw new ArgumentOutOfRangeException(nameof(requestTimeout)); }
        Store = new ObserverStore(transport.MatchId);
        _reader = ReadLoopAsync();
    }

    public async Task<CommandAckPayload> SubmitAsync(ClientPayload payload, ulong expectedPlayerRevision,
        string? clientCommandId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload is RequestSnapshotPayload) { throw new ArgumentException("Use SynchronizeAsync for snapshots.", nameof(payload)); }
        return (CommandAckPayload)await RequestAsync(payload, expectedPlayerRevision, clientCommandId, cancellationToken).ConfigureAwait(false);
    }

    public async Task SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync(new RequestSnapshotPayload(), 0, null, cancellationToken).ConfigureAwait(false);
        if (reply is CommandAckPayload ack) { throw new IOException(ack.Code); }
    }

    public async Task ResumeAsync(ObserverResumeCursor cursor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        var reply = await RequestAsync(new RequestSnapshotPayload(cursor), 0, null, cancellationToken).ConfigureAwait(false);
        if (reply is CommandAckPayload ack) { throw new IOException(ack.Code); }
        if (reply is ObserverResumePayload resumed && resumed.BaseViewHash != cursor.ObserverViewHash)
        { Store.MarkDisconnected(); throw new IOException("resume-cursor-mismatch"); }
    }

    public async Task<SupportDiagnostics> GetSupportDiagnosticsAsync(CancellationToken cancellationToken = default) =>
        (await RequestAsync(new RequestDiagnosticsPayload(), 0, null, cancellationToken).ConfigureAwait(false) as ServerDiagnosticsPayload)?.Report
        ?? throw new IOException("diagnostics-unavailable");

    private async Task<ServerPayload> RequestAsync(ClientPayload payload, ulong revision, string? key, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var callerToken = token;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        linked.CancelAfter(_requestTimeout);
        token = linked.Token;
        var requestId = key ?? Guid.NewGuid().ToString("N");
        var result = new TaskCompletionSource<ServerPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, result)) { throw new InvalidOperationException("command-key-already-pending"); }
        try
        {
            await _sendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _readerStopped) != 0) { throw new IOException("transport-closed; reconnect for a snapshot"); }
                var envelope = new ClientEnvelope(ContractJson.Version, _transport.MatchId, requestId,
                    checked(++_clientSequence), revision, payload);
                await _transport.SendAsync(envelope, token).ConfigureAwait(false);
            }
            finally { _sendLock.Release(); }
            return await result.Task.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!callerToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        { Store.MarkDisconnected(); throw new IOException("response-timeout", error); }
        finally { _pending.TryRemove(requestId, out _); }
    }

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var message = await _transport.ReceiveAsync(_lifetime.Token).ConfigureAwait(false);
                Store.Apply(message, out var accepted);
                if (accepted && message.Payload is MatchTimerPayload timer)
                { Volatile.Write(ref _timer, new MatchTimerReading(timer, System.Diagnostics.Stopwatch.GetTimestamp())); }
                var requestId = message.Payload switch
                {
                    CommandAckPayload ack => ack.ClientCommandId,
                    ObserverSnapshotPayload snapshot => snapshot.RequestId,
                    ObserverResumePayload resumed => resumed.RequestId,
                    ServerDiagnosticsPayload diagnostics => diagnostics.RequestId,
                    _ => null
                };
                if (requestId is not null && _pending.TryGetValue(requestId, out var result))
                {
                    if (!accepted)
                    {
                        result.TrySetException(new IOException(message.Payload is ObserverSnapshotPayload
                            ? "observer-snapshot-invalid" : "observer-envelope-invalid"));
                    }
                    else { result.TrySetResult(message.Payload); }
                }
            }
        }
        catch (Exception error) { failure = error; TransportFailureCode = error.GetType().Name; }
        finally
        {
            // Close admission before completing pending requests; Task.IsCompleted is still false here.
            Volatile.Write(ref _readerStopped, 1);
            Store.MarkDisconnected();
            foreach (var result in _pending.Values)
            { result.TrySetException(new IOException("transport-closed; reconnect for a snapshot", failure)); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _reader.ConfigureAwait(false);
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try { await _transport.DisposeAsync().ConfigureAwait(false); }
        finally { _sendLock.Release(); }
        _lifetime.Dispose();
        _sendLock.Dispose();
    }
}
