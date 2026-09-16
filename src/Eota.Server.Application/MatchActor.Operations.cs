using System.Diagnostics;
using System.Diagnostics.Metrics;
using Eota.Transport.Contracts;

namespace Eota.Server.Application;

public sealed record MatchOperations(string MatchId, string Status, ulong Revision, long Accepted, long Rejected, long Retries,
    long Frames, int Observers, long SlowDisconnects, int MailboxQueued, int MailboxHighWatermark, int CommandKeys,
    int ReplayRecords, int ReplayBytes, long ResumeHits, long ResumeFallbacks, string? FaultCode);

public sealed partial class MatchActor
{
    public static readonly ActivitySource ActivitySource = new("Eota.Server.Application");
    private static readonly Meter Meter = new("Eota.Server.Application", "1.0");
    private static readonly Counter<long> CommandMetric = Meter.CreateCounter<long>("eota.match.commands");
    private static readonly Histogram<double> CommandLatency = Meter.CreateHistogram<double>("eota.match.command.duration", "ms");
    private readonly Queue<(Audience Seat, OperationTrace Trace)> _traces = [];
    private long _acceptedCount, _rejectedCount, _retryCount, _frameCount, _slowDisconnects;
    private string _operationResult = "None";
    private string? _faultCode;

    public Task<MatchOperations> GetOperationsAsync(CancellationToken token = default) => RunAsync(() => new MatchOperations(MatchId,
        _faulted ? "ServerFaulted" : _state.Status.ToString(), _state.Revision, _acceptedCount, _rejectedCount, _retryCount, _frameCount,
        _connections.Count(c => !c.Closed), _slowDisconnects, _mailbox.Queued, _mailbox.HighWatermark, _commands.Count,
        _history.Sum(pair => pair.Value.Count), _historyBytes.Values.Sum(), _resumeHits, _resumeFallbacks, _faultCode), token);

    internal void ObserverBacklogged() { _slowDisconnects++; }

    private void RecordOperation(Audience seat, string kind, long started, Activity? activity)
    {
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _traces.Enqueue((seat, new OperationTrace(activity?.TraceId.ToHexString() ?? ActivityTraceId.CreateRandom().ToHexString(),
            kind, _operationResult, _state.Revision, Math.Round(elapsed, 3))));
        while (_traces.Count > 64) { _traces.Dequeue(); }
        activity?.SetTag("command.kind", kind); activity?.SetTag("command.result", _operationResult);
        CommandMetric.Add(1, new KeyValuePair<string, object?>("result", _operationResult)); CommandLatency.Record(elapsed);
    }

    private void SendDiagnostics(MatchConnection connection, string requestId)
    {
        var view = ObserverProjector.Project(_state, connection.Audience);
        connection.Publish(new ServerDiagnosticsPayload(requestId, new SupportDiagnostics(1, MatchId, connection.Audience, _state.Revision,
            ObserverViewHasher.Compute(view), view.ProtocolHash, view.RuleContentHash, view.Status, view.Stage, view.Turn,
            [.. _traces.Where(item => item.Seat == connection.Audience).Select(item => item.Trace)])), _state.Revision);
    }
}
