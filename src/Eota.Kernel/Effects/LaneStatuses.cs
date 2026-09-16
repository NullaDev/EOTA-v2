using System.Collections.Immutable;
using Eota.Kernel.Content;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Kernel.Effects;

public enum LaneStatusKind { Frozen, Locked }
public sealed record LaneStatusState(LaneStatusKind Kind, long? RemainingDuration);
public sealed record LaneStatusEffect(EffectSelector Selector, LaneStatusKind Status, bool Remove, IntExpression? Duration) : EffectNode;
public sealed record ChangeLaneStatusIntent(IntentId Id, LaneId LaneId, PlayerId SourcePlayerId,
    LaneStatusKind Status, bool Remove, long? Duration) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(ConflictKind.LaneStatus, (ulong)(uint)LaneId.Value);
}
public sealed record DecayLaneStatusesIntent(IntentId Id, LaneId LaneId, long Amount) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(ConflictKind.LaneStatus, (ulong)(uint)LaneId.Value);
}

public static class LaneStatusRules
{
    public static bool IsLocked(MatchState state, LaneId laneId) => HasStatus(state, laneId, LaneStatusKind.Locked);

    public static bool IsFrozen(MatchState state, LaneId laneId) => HasStatus(state, laneId, LaneStatusKind.Frozen)
        || state.Entities.OfType<FieldEntityState>().Any(field => field.LaneId == laneId
            && CardInstanceRules.Definition(state, state.CardInstances.Single(card => card.Id == field.CardInstanceId))
                is FieldCardDefinition { PreventsActiveAttacksInLane: true });

    public static bool CanMove(MatchState state, LaneId from, LaneId to) => !IsLocked(state, from) && !IsLocked(state, to);

    private static bool HasStatus(MatchState state, LaneId laneId, LaneStatusKind kind) =>
        state.Lanes.Single(lane => lane.Id == laneId).Statuses.Any(status => status.Kind == kind
            && (status.RemainingDuration is null or > 0));
}
