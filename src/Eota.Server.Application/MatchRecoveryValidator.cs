using Eota.Kernel.Matches;

namespace Eota.Server.Application;

// Host admission/recovery policy. These checks do not rewrite canonical state or change historical hashes.
public static class MatchRecoveryValidator
{
    // Reserve ample IDs for a complete bounded resolution before admitting another command.
    public const ulong AllocationReserve = 1UL << 40;
    public static void Validate(MatchState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var counters = new[] { state.NextCardInstanceId.Value, state.NextEntityId.Value, state.NextCommandId.Value, state.NextFrameId.Value,
            state.NextWorkItemId.Value, state.NextIntentId.Value, state.NextConflictGroupId.Value, state.NextReceiptId.Value,
            state.NextEventId.Value, state.NextTombstoneId.Value, state.NextEffectProgramId };
        if (counters.Any(value => value == 0) || state.Turn < 1 || state.Players.IsDefault || state.Players.Length != 2
            || state.Players.Select(p => p.Id).Distinct().Count() != 2 || state.CardInstances.IsDefault || state.Entities.IsDefault
            || state.Tombstones.IsDefault || state.CommandLog.IsDefault) { throw new InvalidDataException("checkpoint-invalid-identifiers"); }
        if (state.Players.Any(player => player.FatigueCount < 0 || player.FatigueCount >= long.MaxValue - (long)AllocationReserve))
        { throw new InvalidDataException("checkpoint-invalid-fatigue-counter"); }
        if (state.Status == MatchStatus.Active && (counters.Any(value => value >= ulong.MaxValue - AllocationReserve)
            || state.Revision >= ulong.MaxValue - AllocationReserve || state.RuleRng.SampleCount >= ulong.MaxValue - AllocationReserve
            || state.Turn >= int.MaxValue - 1 || state.Players.Any(p => p.CommandRevision >= ulong.MaxValue - AllocationReserve
                || p.NextPlanOrdinal >= ulong.MaxValue - AllocationReserve)
            || state.Execution?.NextEffectId >= ulong.MaxValue - AllocationReserve))
        { throw new InvalidDataException("checkpoint-counter-exhausted"); }
        CheckNext(state.NextCardInstanceId.Value, state.CardInstances.Select(c => c.Id.Value));
        CheckNext(state.NextEntityId.Value, state.Entities.Select(e => e.Id.Value));
        if (state.Tombstones.Any(t => t.EntityId.Value >= state.NextEntityId.Value || t.CardInstanceId.Value >= state.NextCardInstanceId.Value))
        { throw new InvalidDataException("checkpoint-id-reuse"); }
        CheckNext(state.NextTombstoneId.Value, state.Tombstones.Select(t => t.Id.Value));
        CheckNext(state.NextCommandId.Value, state.CommandLog.Select(c => c.Id.Value));
        if (state.CommandLog.Any(c => c.MatchRevision > state.Revision)) { throw new InvalidDataException("checkpoint-revision-order"); }
        foreach (var player in state.Players)
        {
            if (player.NextPlanOrdinal == 0 || player.Planning.IsDefault) { throw new InvalidDataException("checkpoint-invalid-plan-id"); }
            CheckNext(player.NextPlanOrdinal, player.Planning.Select(p => p.Id.Ordinal));
        }
        if (state.Execution is { } execution)
        {
            if (execution.NextEffectId == 0) { throw new InvalidDataException("checkpoint-invalid-effect-id"); }
            CheckNext(state.NextReceiptId.Value, execution.ReceiptLedger.Select(r => r.Id.Value));
        }
    }

    private static void CheckNext(ulong next, IEnumerable<ulong> allocated)
    {
        var values = allocated.ToArray();
        if (values.Any(id => id == 0 || id >= next) || values.Distinct().Count() != values.Length)
        { throw new InvalidDataException("checkpoint-id-reuse"); }
    }
}
