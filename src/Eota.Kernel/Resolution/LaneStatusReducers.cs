using System.Collections.Immutable;
using Eota.Kernel.Effects;

namespace Eota.Kernel.Resolution;

internal sealed partial class ReductionContext
{
    public void ReduceLaneStatus(IntentConflictGroup group)
    {
        var lane = _lanes.SingleOrDefault(value => (ulong)value.Id.Value == group.Key.StableTargetId);
        if (lane is null) { RejectAll(group, "lane-not-found", null, null); return; }
        var updates = group.Intents.OfType<ChangeLaneStatusIntent>().ToArray();
        var decays = group.Intents.OfType<DecayLaneStatusesIntent>().ToArray();
        if (updates.Length + decays.Length != group.Intents.Length
            || updates.Any(value => !Enum.IsDefined(value.Status) || value.Duration is <= 0)
            || decays.Any(value => value.Amount < 0))
        { RejectAll(group, "invalid-lane-status", null, null); return; }
        var statuses = lane.Statuses.ToDictionary(value => value.Kind);
        // Expire the previous snapshot's durations before incorporating this frame's additions.
        foreach (var decay in decays)
        {
            foreach (var status in statuses.Values.ToArray())
            {
                if (status.RemainingDuration is not { } duration) { continue; }
                if (duration <= decay.Amount) { statuses.Remove(status.Kind); }
                else { statuses[status.Kind] = status with { RemainingDuration = duration - decay.Amount }; }
            }
            AddReceipt(decay.Id, IntentReceiptStatus.Applied, "decay-lane-status", null, null, null);
        }
        foreach (var changes in updates.GroupBy(value => value.Status).OrderBy(value => value.Key))
        {
            if (changes.Any(value => value.Remove)) { statuses.Remove(changes.Key); }
            else
            {
                var existing = statuses.GetValueOrDefault(changes.Key);
                var permanent = existing is { RemainingDuration: null } || changes.Any(value => value.Duration is null);
                long? duration = permanent ? null : Math.Max(existing?.RemainingDuration ?? 0, changes.Max(value => value.Duration!.Value));
                statuses[changes.Key] = new LaneStatusState(changes.Key, duration);
            }
            foreach (var change in changes)
            { AddReceipt(change.Id, IntentReceiptStatus.Applied, "lane-status", null, change.SourcePlayerId, null); }
        }
        var next = statuses.Values.OrderBy(value => value.Kind).ToImmutableArray();
        _lanes = _lanes.Replace(lane, lane with { Statuses = next });
        if (!lane.Statuses.SequenceEqual(next))
        { AddEvent(new EventDraft(DomainEventKind.LaneStatusChanged, null, null, null, null, null, "lane-status", LaneId: lane.Id)); }
    }
}
