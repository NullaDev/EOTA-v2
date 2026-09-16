using System.Text;
using Eota.Kernel.Resolution;
using Eota.Transport.Contracts;

namespace Eota.Server.Application;

public sealed partial class MatchActor
{
    private sealed record Observation(ulong Revision, ObserverView View, string Hash, PresentationFramePayload? Frame, int Bytes);
    private readonly Dictionary<Audience, Queue<Observation>> _history = new()
    { [Audience.PlayerOne] = new(), [Audience.PlayerTwo] = new(), [Audience.Spectator] = new() };
    private readonly Dictionary<Audience, int> _historyBytes = new()
    { [Audience.PlayerOne] = 0, [Audience.PlayerTwo] = 0, [Audience.Spectator] = 0 };
    private long _resumeHits;
    private long _resumeFallbacks;

    private void RememberObservation(FrameTransition? frame)
    {
        foreach (var (audience, history) in _history)
        {
            var view = ObserverProjector.Project(_state, audience); var hash = ObserverViewHasher.Compute(view);
            var payload = frame is null ? null : new PresentationFramePayload(frame.Plan.FrameId.Value, view, hash, ObserverProjector.ProjectEvents(frame, audience));
            var bytes = Encoding.UTF8.GetByteCount(payload is null ? ContractJson.Serialize(view) : ContractJson.Serialize(payload));
            history.Enqueue(new Observation(_state.Revision, view, hash, payload, bytes)); _historyBytes[audience] += bytes;
            while (history.Count > _options.ReplayCapacity || _historyBytes[audience] > _options.ReplayBytesPerAudience)
            { _historyBytes[audience] -= history.Dequeue().Bytes; }
        }
    }

    private void SendResume(MatchConnection connection, string requestId, ObserverResumeCursor cursor)
    {
        // Match within this authenticated audience only. Never use another seat's history or untrusted client state.
        var history = _history[connection.Audience].ToArray();
        var index = Array.FindLastIndex(history, record => record.Revision == cursor.MatchRevision && record.Hash == cursor.ObserverViewHash);
        if (index >= 0)
        {
            var baseline = history[index]; var current = ObserverProjector.Project(_state, connection.Audience);
            var payload = new ObserverResumePayload(requestId, baseline.View, baseline.Hash,
                [.. history.Skip(index + 1).Where(record => record.Frame is not null).Select(record => record.Frame!)], current, ObserverViewHasher.Compute(current));
            // Leave room for envelope metadata; oversized batches become a current snapshot.
            if (payload.Frames.Length <= 128 && Encoding.UTF8.GetByteCount(ContractJson.Serialize(payload)) <= ContractJson.MaximumServerMessageBytes - 4096)
            { _resumeHits++; connection.Publish(payload, _state.Revision); SendTimer(connection); return; }
        }
        _resumeFallbacks++; SendSnapshot(connection, requestId);
    }
}
