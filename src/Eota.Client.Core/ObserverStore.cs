using System.Collections.Immutable;
using Eota.Transport.Contracts;

namespace Eota.Client.Core;

public sealed record ObserverStoreState(
    ObserverView? View, string? ObserverViewHash, ulong ServerSequence, ulong MatchRevision, bool NeedsSnapshot);
public sealed record ResumedPresentation(ObserverView BaseView, ImmutableArray<PresentationFramePayload> Frames);
public sealed record ObserverFailure(string Code, ulong ReceivedSequence, ulong PreviousSequence, ulong MatchRevision);

public sealed class ObserverStore
{
    private readonly string _matchId;
    private readonly int _presentationCapacity;
    private readonly object _gate = new();
    private readonly Queue<PresentationFramePayload> _frames = [];
    private ObserverView? _resumeBase;
    private readonly Queue<ObserverFailure> _failures = [];
    private TaskCompletionSource _changed = NewSignal();
    private volatile ObserverStoreState _state = new(null, null, 0, 0, true);
    public ObserverStoreState State => _state;
    public ObserverView? View => _state.View;
    public string? ObserverViewHash => _state.ObserverViewHash;
    public ulong ServerSequence => _state.ServerSequence;
    public ulong MatchRevision => _state.MatchRevision;
    public bool NeedsSnapshot => _state.NeedsSnapshot;

    public ObserverStore(string matchId, int presentationCapacity = 256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(matchId);
        ArgumentOutOfRangeException.ThrowIfLessThan(presentationCapacity, 1);
        _matchId = matchId;
        _presentationCapacity = presentationCapacity;
    }

    public bool Apply(ServerEnvelope envelope)
        => Apply(envelope, out _);

    internal bool Apply(ServerEnvelope envelope, out bool accepted)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        lock (_gate)
        {
            var result = ApplyCore(envelope, out accepted);
            SignalChanged();
            return result;
        }
    }

    private bool ApplyCore(ServerEnvelope envelope, out bool accepted)
    {
        accepted = false;
        var current = _state;
        if (envelope.ContractVersion != ContractJson.Version || envelope.MatchId != _matchId
            || envelope.ServerSequence <= current.ServerSequence || envelope.MatchRevision < current.MatchRevision)
        {
            return Fail("envelope-order-or-version", current, envelope);
        }

        var isSnapshot = envelope.Payload is ObserverSnapshotPayload or ObserverResumePayload;
        var gap = envelope.ServerSequence != current.ServerSequence + 1;
        if (!isSnapshot && (current.NeedsSnapshot || gap))
        {
            return Fail("sequence-gap", current, envelope);
        }

        var (view, hash) = envelope.Payload switch
        {
            ObserverSnapshotPayload value => (value.View, value.ObserverViewHash),
            PresentationFramePayload value => (value.View, value.ObserverViewHash),
            ObserverResumePayload value => (value.View, value.ObserverViewHash),
            _ => ((ObserverView?)null, (string?)null)
        };
        if ((envelope.Payload is ObserverSnapshotPayload or PresentationFramePayload or ObserverResumePayload && view is null)
            || (view is not null && ((current.View is not null && current.View.Audience != view.Audience)
                || !string.Equals(ObserverViewHasher.Compute(view), hash, StringComparison.Ordinal))))
        {
            return Fail("observer-hash-or-audience", current, envelope);
        }

        var needsSnapshot = current.NeedsSnapshot;
        if (envelope.Payload is ObserverResumePayload resume)
        {
            if (!ValidateResume(resume)) { return Fail("invalid-resume-batch", current, envelope); }
            _frames.Clear(); _resumeBase = resume.BaseView;
            foreach (var item in resume.Frames) { _frames.Enqueue(item); }
        }
        if (isSnapshot)
        {
            if (envelope.Payload is not ObserverResumePayload && (needsSnapshot || gap)) { _frames.Clear(); _resumeBase = null; }
            needsSnapshot = false;
        }
        if (envelope.Payload is CommandAckPayload { ResyncRequired: true }) { needsSnapshot = true; }
        if (envelope.Payload is PresentationFramePayload frame)
        {
            if (_frames.Count == _presentationCapacity)
            {
                _frames.Clear();
                _resumeBase = null;
                needsSnapshot = true;
            }
            else { _frames.Enqueue(frame); }
        }

        _state = new ObserverStoreState(view ?? current.View, hash ?? current.ObserverViewHash,
            envelope.ServerSequence, envelope.MatchRevision, needsSnapshot);
        accepted = true;
        return !needsSnapshot;
    }

    public void MarkDisconnected()
    {
        lock (_gate) { _state = _state with { NeedsSnapshot = true }; SignalChanged(); }
    }

    // Capture State before deciding to wait. Checking and subscribing under the same lock avoids
    // losing an update between an automated client's inspection and its asynchronous wait.
    public Task WaitForChangeAsync(ObserverStoreState previous, CancellationToken cancellationToken = default)
    {
        lock (_gate) { return ReferenceEquals(previous, _state) ? _changed.Task.WaitAsync(cancellationToken) : Task.CompletedTask; }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void SignalChanged() { var signal = _changed; _changed = NewSignal(); signal.TrySetResult(); }

    public ImmutableArray<PresentationFramePayload> DrainPresentationFrames()
    {
        lock (_gate)
        {
            var frames = _frames.ToImmutableArray();
            _frames.Clear();
            return frames;
        }
    }

    public ResumedPresentation? TakeResumedPresentation()
    {
        lock (_gate)
        {
            if (_resumeBase is not { } view) { return null; }
            var result = new ResumedPresentation(view, [.. _frames]); _frames.Clear(); _resumeBase = null; return result;
        }
    }

    private bool ValidateResume(ObserverResumePayload resume)
    {
        if (resume.BaseView is null || resume.Frames.IsDefault || resume.Frames.Length > _presentationCapacity
            || resume.BaseView.Audience != resume.View.Audience || ObserverViewHasher.Compute(resume.BaseView) != resume.BaseViewHash) { return false; }
        ulong previous = 0;
        foreach (var frame in resume.Frames)
        {
            if (frame is null || frame.View is null || frame.FrameId <= previous || frame.View.Audience != resume.View.Audience
                || frame.View.ProtocolHash != resume.View.ProtocolHash || frame.View.RuleContentHash != resume.View.RuleContentHash
                || ObserverViewHasher.Compute(frame.View) != frame.ObserverViewHash) { return false; }
            previous = frame.FrameId;
        }
        return resume.BaseView.ProtocolHash == resume.View.ProtocolHash && resume.BaseView.RuleContentHash == resume.View.RuleContentHash;
    }

    private bool Fail(string code, ObserverStoreState current, ServerEnvelope envelope)
    {
        _failures.Enqueue(new ObserverFailure(code, envelope.ServerSequence, current.ServerSequence, envelope.MatchRevision));
        while (_failures.Count > 32) { _failures.Dequeue(); }
        _state = current with { NeedsSnapshot = true }; return false;
    }

    public ImmutableArray<ObserverFailure> CaptureFailures() { lock (_gate) { return [.. _failures]; } }
}
