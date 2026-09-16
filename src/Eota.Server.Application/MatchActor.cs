using Eota.Kernel.Commands;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;
using Eota.Transport.Contracts;

namespace Eota.Server.Application;

public sealed record MatchActorOptions(int MailboxCapacity = 64, int ObserverCapacity = 512, int MaximumObservers = 32, int MaximumCommandKeys = 4096,
    int ReplayCapacity = 128, int ReplayBytesPerAudience = 1048576);
public sealed record MatchDiagnostics(string StateHash, ulong MatchRevision, int AcceptedCommandCount, string Status);
public sealed record MatchReplayCapture(System.Collections.Immutable.ImmutableArray<AcceptedCommandRecord> Commands, string StateHash);

public sealed partial class MatchActor : IAsyncDisposable
{
    private readonly SerializedMailbox _mailbox;
    private readonly MatchActorOptions _options;
    private readonly List<MatchConnection> _connections = [];
    private readonly Dictionary<(Audience Seat, string Key), CachedCommand> _commands = [];
    private readonly Queue<((Audience Seat, string Key) Key, CachedCommand Value)> _commandOrder = [];
    private MatchState _state;
    private bool _faulted;
    private readonly IMatchJournal? _journal;
    public string MatchId { get; }
    public string RuleContentHash { get; }

    public MatchActor(string matchId, MatchCreationRequest request, MatchActorOptions? options = null, IMatchJournal? journal = null, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(matchId);
        _options = options ?? new MatchActorOptions();
        _time = timeProvider ?? TimeProvider.System;
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.ObserverCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaximumObservers, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaximumCommandKeys, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.ReplayCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.ReplayBytesPerAudience, 1);
        var creation = MatchFactory.Create(request);
        _state = creation.State ?? throw new ArgumentException("Invalid match creation inputs.", nameof(request));
        _journal = journal;
        if (journal is not null)
        {
            _state = journal.Recovery.State;
            if (journal.Recovery.MatchId is { } recoveredId && recoveredId != matchId) { throw new InvalidDataException("journal-match-id-mismatch"); }
            if (_state.Manifest.RuleContentHash != request.Content.Hash || _state.Manifest.ProtocolHash != request.Protocol.Hash)
            { throw new InvalidDataException("journal-manifest-mismatch"); }
            foreach (var stored in journal.Recovery.Commands)
            {
                if (stored.Envelope.MatchId != matchId) { throw new InvalidDataException("journal-match-id-mismatch"); }
                if (stored.SystemTimeout is not null) { continue; }
                CacheCommand((stored.Audience, stored.Envelope.ClientCommandId), new CachedCommand(ContractJson.Serialize(stored.Envelope with { ClientSequence = 0 }), stored.Ack));
            }
        }
        MatchId = matchId;
        MatchRecoveryValidator.Validate(_state);
        RuleContentHash = _state.Manifest.RuleContentHash.ToString();
        _mailbox = new SerializedMailbox(_options.MailboxCapacity);
        RememberObservation(null);
    }

    public Task<MatchConnection> ConnectAsync(Audience audience, CancellationToken cancellationToken = default) => RunAsync(() =>
    {
        if (!Enum.IsDefined(audience)) { throw new ArgumentOutOfRangeException(nameof(audience)); }
        if (_faulted) { throw new InvalidOperationException("match-faulted"); }
        _connections.RemoveAll(value => value.Closed);
        if (_connections.Count >= _options.MaximumObservers) { throw new InvalidOperationException("observer-limit-exceeded"); }
        var connection = new MatchConnection(this, audience, _options.ObserverCapacity);
        _connections.Add(connection);
        SendSnapshot(connection, null);
        return connection;
    }, cancellationToken);

    internal Task DisconnectAsync(MatchConnection connection) => RunAsync(() =>
    {
        connection.Close();
        _connections.Remove(connection);
        return true;
    });

    internal Task SubmitAsync(MatchConnection connection, ClientEnvelope envelope, CancellationToken cancellationToken) => RunAsync(() =>
    {
        if (!_connections.Contains(connection) || connection.Closed) { throw new IOException("connection-closed"); }
        var started = System.Diagnostics.Stopwatch.GetTimestamp(); using var activity = ActivitySource.StartActivity("match.command");
        _operationResult = "None";
        try { Process(connection, envelope); }
        catch (Exception error)
        {
            // A faulty match stops accepting commands; other actors retain their own readers and state.
            _faulted = true;
            _faultCode = _operationResult = error.GetType().Name;
            foreach (var observer in _connections) { observer.Close(new IOException("match-faulted", error)); }
            throw;
        }
        finally { RecordOperation(connection.Audience, envelope?.Payload?.GetType().Name ?? "Invalid", started, activity); }

        return true;
    }, cancellationToken);

    public Task<MatchDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken = default) => RunAsync(() => new MatchDiagnostics(
        MatchStateHasher.Compute(_state).ToString(), _state.Revision, _state.CommandLog.Length,
        _faulted ? "ServerFaulted" : _state.Status.ToString()), cancellationToken);

    // Trusted local composition only; never exposed through an observer connection.
    public Task<System.Collections.Immutable.ImmutableArray<AcceptedCommandRecord>> GetCommandLogAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => _state.CommandLog, cancellationToken);

    // Capture both in one mailbox operation: an automated player may submit while a replay is saved.
    public Task<MatchReplayCapture> CaptureReplayAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => new MatchReplayCapture(_state.CommandLog, MatchStateHasher.Compute(_state).ToString()), cancellationToken);

    private Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken = default) =>
        _mailbox.InvokeAsync(() => Task.FromResult(action()), cancellationToken);

    private void Process(MatchConnection connection, ClientEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.ContractVersion != ContractJson.Version) { Reject(connection, envelope, "unsupported-contract-version", true); return; }
        if (!string.Equals(envelope.MatchId, MatchId, StringComparison.Ordinal)) { Reject(connection, envelope, "match-id-mismatch", true); return; }
        if (string.IsNullOrWhiteSpace(envelope.ClientCommandId) || envelope.ClientCommandId.Length > 96 || envelope.Payload is null)
        { Reject(connection, envelope, "invalid-envelope", false); return; }
        if (_faulted) { Reject(connection, envelope, "match-faulted", true); return; }
        ExpireClock();

        var key = (connection.Audience, envelope.ClientCommandId);
        var fingerprint = ContractJson.Serialize(envelope with { ClientSequence = 0 });
        if (_commands.TryGetValue(key, out var cached))
        {
            if (cached.Fingerprint == fingerprint)
            {
                _retryCount++; _operationResult = "Duplicate";
                connection.ClientSequence = Math.Max(connection.ClientSequence, envelope.ClientSequence);
                connection.Publish(cached.Ack, _state.Revision);
            }
            else { Reject(connection, envelope, "client-command-id-conflict", false); }
            return;
        }

        if (envelope.ClientSequence <= connection.ClientSequence) { Reject(connection, envelope, "client-sequence-not-increasing", true); return; }
        connection.ClientSequence = envelope.ClientSequence;
        if (envelope.Payload is RequestDiagnosticsPayload) { SendDiagnostics(connection, envelope.ClientCommandId); return; }
        if (envelope.Payload is RequestSnapshotPayload snapshot)
        {
            if (snapshot.Resume is { } cursor) { SendResume(connection, envelope.ClientCommandId, cursor); }
            else { SendSnapshot(connection, envelope.ClientCommandId); }
            return;
        }
        if (connection.Audience == Audience.Spectator) { Reject(connection, envelope, "spectator-read-only", false); return; }

        var seat = connection.Audience == Audience.PlayerOne ? PlayerId.One : PlayerId.Two;
        MatchRecoveryValidator.Validate(_state);
        var command = MapCommand(envelope, seat);
        if (command is null) { Reject(connection, envelope, "invalid-command-payload", false); return; }
        var transition = MatchCommandProcessor.Accept(_state, command);
        CompleteTransition(transition, connection.Audience, envelope, connection, () => CacheCommand(key,
            new CachedCommand(fingerprint, MakeAck(envelope, transition))));
    }

    private static CommandAckPayload MakeAck(ClientEnvelope envelope, CommandTransition transition)
    {
        var receipt = transition.Receipt;
        return new CommandAckPayload(envelope.ClientCommandId, transition.IsAccepted, receipt.RejectionReason.ToString(),
            receipt.ResultingPlayerRevision, receipt.ResultingMatchRevision, receipt.PlanCommandId?.Ordinal,
            receipt.RejectionReason == CommandRejectionReason.PlayerRevisionMismatch);
    }

    private void CompleteTransition(CommandTransition transition, Audience audience, ClientEnvelope envelope,
        MatchConnection? connection = null, Action? committed = null, StoredSystemTimeout? timeout = null)
    {
        _state = transition.State;
        var ack = MakeAck(envelope, transition);
        _operationResult = ack.Code;
        if (transition.IsAccepted) { _acceptedCount++; } else { _rejectedCount++; }
        var acceptedState = _state;
        var frames = transition.IsAccepted && _state.Stage == MatchStage.ReadyToResolve ? TurnResolver.ResolveReadyTurn(_state).Frames : [];
        if (!frames.IsEmpty) { _state = frames[^1].State; }
        _frameCount += frames.Length;
        _journal?.Commit(new StoredMatchCommand(audience, envelope, ack, MatchStateHasher.Compute(_state).ToString(), timeout), _state);
        committed?.Invoke();
        connection?.Publish(ack, acceptedState.Revision);
        if (!transition.IsAccepted) { return; }
        var finalState = _state; _state = acceptedState;
        RememberObservation(null);
        foreach (var observer in _connections) { SendSnapshot(observer, null); }
        foreach (var frame in frames)
        {
            _state = frame.State;
            RememberObservation(frame);
            foreach (var observer in _connections.Where(value => !value.Closed))
            {
                var view = ObserverProjector.Project(_state, observer.Audience);
                observer.Publish(new PresentationFramePayload(frame.Plan.FrameId.Value, view, ObserverViewHasher.Compute(view),
                    ObserverProjector.ProjectEvents(frame, observer.Audience)), _state.Revision);
            }
        }
        _state = finalState;
    }

    public static AuthoritativeCommand? MapCommand(ClientEnvelope envelope, PlayerId seat) => (envelope ?? throw new ArgumentNullException(nameof(envelope))).Payload switch
    {
        PlanCardPayload { CardInstanceId: > 0, LaneId: >= 0 } value => new PlanCardCommand(seat, envelope.ExpectedPlayerRevision,
            new CardInstanceId(value.CardInstanceId), new LaneId(value.LaneId)),
        PlanSpellPayload { CardInstanceId: > 0 } value when value.LaneId is null or >= 0 => new PlanSpellCommand(seat,
            envelope.ExpectedPlayerRevision, new CardInstanceId(value.CardInstanceId), value.LaneId is { } lane ? new LaneId(lane) : null),
        CancelPlanPayload { PlanCommandId: > 0 } value => new CancelPlanCommand(seat, envelope.ExpectedPlayerRevision,
            new PlanCommandId(seat, value.PlanCommandId)),
        SubmitTurnPayload => new SubmitTurnCommand(seat, envelope.ExpectedPlayerRevision),
        SubmitMulliganPayload value when !value.ReplacedCards.IsDefault && value.ReplacedCards.Length <= 256
            && value.ReplacedCards.All(id => id > 0) => new SubmitMulliganCommand(seat, envelope.ExpectedPlayerRevision,
                [.. value.ReplacedCards.Select(id => new CardInstanceId(id))]),
        _ => null
    };

    private void Reject(MatchConnection connection, ClientEnvelope envelope, string code, bool resync)
    {
        _rejectedCount++; _operationResult = code;
        var revision = connection.Audience == Audience.Spectator ? 0 : _state.Players.Single(value =>
            value.Id == (connection.Audience == Audience.PlayerOne ? PlayerId.One : PlayerId.Two)).CommandRevision;
        connection.Publish(new CommandAckPayload(envelope.ClientCommandId ?? string.Empty, false, code, revision, _state.Revision, null, resync), _state.Revision);
    }

    private void SendSnapshot(MatchConnection observer, string? requestId)
    {
        if (observer.Closed) { return; }
        var view = ObserverProjector.Project(_state, observer.Audience);
        observer.Publish(new ObserverSnapshotPayload(requestId, view, ObserverViewHasher.Compute(view)), _state.Revision);
        SendTimer(observer);
    }

    public async ValueTask DisposeAsync()
    {
        await RunAsync(() =>
        {
            foreach (var connection in _connections) { connection.Close(); }
            _connections.Clear();
            return true;
        }).ConfigureAwait(false);
        await _mailbox.DisposeAsync().ConfigureAwait(false);
    }

    private sealed record CachedCommand(string Fingerprint, CommandAckPayload Ack);

    private void CacheCommand((Audience Seat, string Key) key, CachedCommand value)
    {
        _commands[key] = value; _commandOrder.Enqueue((key, value));
        while (_commandOrder.Count > _options.MaximumCommandKeys)
        {
            var previous = _commandOrder.Dequeue();
            if (_commands.TryGetValue(previous.Key, out var current) && ReferenceEquals(current, previous.Value)) { _commands.Remove(previous.Key); }
        }
    }
}
