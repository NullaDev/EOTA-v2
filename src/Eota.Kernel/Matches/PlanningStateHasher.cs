using Eota.Kernel.Determinism;

namespace Eota.Kernel.Matches;

public static class PlanningStateHasher
{
    public const ushort CanonicalSchemaVersion = 1;

    public static Hash256 Compute(MatchState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return CanonicalHash.Compute("eota.planning-state", CanonicalSchemaVersion, writer =>
        {
            writer.WriteInt32(state.Turn);
            writer.WriteInt32((int)state.Stage);
            writer.WriteCount(state.Players.Length);
            foreach (var player in state.Players.OrderBy(value => value.Id))
            {
                writer.WriteByte(player.Id.Value);
                writer.WriteInt64(player.CurrentCost);
                writer.WriteBoolean(player.TurnSubmitted);
                writer.WriteUInt64(player.CommandRevision);
                writer.WriteUInt64(player.NextPlanOrdinal);

                writer.WriteCount(player.Hand.Length);
                foreach (var cardId in player.Hand.OrderBy(value => value))
                {
                    writer.WriteUInt64(cardId.Value);
                }

                writer.WriteCount(player.Planning.Length);
                foreach (var action in player.Planning.OrderBy(value => value.Id))
                {
                    writer.WriteByte(action.Id.PlayerId.Value);
                    writer.WriteUInt64(action.Id.Ordinal);
                    writer.WriteUInt64(action.CardInstanceId.Value);
                    writer.WriteInt32((int)action.Kind);
                    writer.WriteBoolean(action.LaneId.HasValue);
                    if (action.LaneId is { } laneId)
                    {
                        writer.WriteInt32(laneId.Value);
                    }

                    writer.WriteInt64(action.AuthoritativeCost);
                    writer.WriteInt64(action.ReservedCost);
                }
            }
        });
    }
}
