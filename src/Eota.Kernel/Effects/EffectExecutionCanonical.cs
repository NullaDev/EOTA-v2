using System.Collections.Immutable;
using Eota.Kernel.Determinism;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Kernel.Effects;

internal static class EffectExecutionCanonical
{
    internal static void Attachments(CanonicalWriter writer, ImmutableArray<AttachedEffect> effects)
    {
        writer.WriteCount(effects.Length);
        foreach (var effect in effects.OrderBy(value => value.Id.Value))
        {
            writer.WriteUInt64(effect.Id.Value); EffectCanonical.Write(writer, effect.Definition);
            OptionalNumber(writer, effect.RemainingDuration);
            writer.WriteUInt64(effect.Origin.CardId.Value); writer.WriteString(effect.Origin.PrototypeId.Value);
            writer.WriteByte(effect.Origin.ControllerId.Value); writer.WriteBoolean(effect.Origin.LaneId.HasValue);
            if (effect.Origin.LaneId is { } lane) { writer.WriteInt32(lane.Value); }
            Entity(writer, effect.Origin.FrozenEntity);
        }
    }
    public static void Write(CanonicalWriter writer, TurnExecutionState execution)
    {
        writer.WriteInt32(execution.NextSystemStep);
        writer.WriteUInt64(execution.NextEffectId);
        writer.WriteInt32(execution.EffectFrames);
        Programs(writer, execution.Ready);
        Programs(writer, execution.DeferredEntry);
        writer.WriteHash(FrameBatchHasher.ComputeReceipts(new FrameId(0), execution.ReceiptLedger));
        writer.WriteBoolean(execution.Combat is not null);
        if (execution.Combat is { } combat)
        {
            writer.WriteCount(combat.Lanes.Length);
            foreach (var lane in combat.Lanes.OrderBy(value => value.LaneId.Value))
            {
                writer.WriteInt32(lane.LaneId.Value);
                writer.WriteInt32((int)lane.Kind);
                OptionalId(writer, lane.PlayerOneMinionId?.Value);
                OptionalId(writer, lane.PlayerTwoMinionId?.Value);
                writer.WriteBoolean(lane.PlayerOneActivelyAttacks);
                writer.WriteBoolean(lane.PlayerTwoActivelyAttacks);
                writer.WriteBoolean(lane.PlayerOneCanDealMinionDamage);
                writer.WriteBoolean(lane.PlayerTwoCanDealMinionDamage);
            }
        }
        writer.WriteCount(execution.EarlyAttackers.IsDefault ? 0 : execution.EarlyAttackers.Length);
        if (!execution.EarlyAttackers.IsDefault)
        { foreach (var id in execution.EarlyAttackers.OrderBy(value => value.Value)) { writer.WriteUInt64(id.Value); } }
        writer.WriteCount(execution.SlowApplications.IsDefault ? 0 : execution.SlowApplications.Length);
        if (!execution.SlowApplications.IsDefault)
        {
            foreach (var slow in execution.SlowApplications.OrderBy(value => value.EntityId.Value))
            { writer.WriteUInt64(slow.EntityId.Value); writer.WriteUInt64(slow.Generation); }
        }
        Event(writer, execution.EndTurnEvent);
        writer.WriteInt32(execution.EndTurnPhase);
        writer.WriteBoolean(execution.PreCombatChargeCollected);
        writer.WriteCount(execution.ChargeSources.IsDefault ? 0 : execution.ChargeSources.Length);
        if (!execution.ChargeSources.IsDefault)
        {
            foreach (var source in execution.ChargeSources.OrderBy(value => value.EntityId.Value))
            { writer.WriteUInt64(source.EntityId.Value); writer.WriteString(source.PrototypeId.Value); }
        }
    }

    private static void Programs(CanonicalWriter writer, ImmutableArray<EffectProgram> programs)
    {
        writer.WriteCount(programs.Length);
        foreach (var program in programs.OrderBy(value => value.Id.Value))
        {
            writer.WriteUInt64(program.Id.Value);
            var invocation = program.Invocation;
            var source = invocation.Source;
            writer.WriteUInt64(source.CardId.Value);
            writer.WriteString(source.PrototypeId.Value);
            writer.WriteByte(source.ControllerId.Value);
            writer.WriteBoolean(source.LaneId.HasValue);
            if (source.LaneId is { } lane) { writer.WriteInt32(lane.Value); }
            Entity(writer, source.FrozenEntity);
            EffectCanonical.Write(writer, invocation.Effect);
            Event(writer, invocation.Event);
            Entity(writer, invocation.EventEntity);
            writer.WriteBoolean(invocation.EventPrototypeId.HasValue);
            if (invocation.EventPrototypeId is { } eventPrototype) { writer.WriteString(eventPrototype.Value); }
            writer.WriteBoolean(invocation.Replaced is not null);
            if (invocation.Replaced is { } replaced)
            {
                writer.WriteUInt64(replaced.Id.Value);
                writer.WriteUInt64(replaced.EntityId.Value);
                writer.WriteUInt64(replaced.CardInstanceId.Value);
                writer.WriteString(replaced.PrototypeId.Value);
                writer.WriteByte(replaced.OwnerId.Value);
                writer.WriteByte(replaced.ControllerId.Value);
                writer.WriteInt32(replaced.LaneId.Value);
                writer.WriteInt32((int)replaced.Reason);
                OptionalNumber(writer, replaced.Attack); OptionalNumber(writer, replaced.CurrentHealth);
                OptionalNumber(writer, replaced.MaximumHealth); OptionalNumber(writer, replaced.FieldEnergy);
                writer.WriteUInt64(replaced.FrameId.Value);
                Entity(writer, replaced.FinalEntity);
            }
            Cursor(writer, program.Cursor);
        }
    }

    private static void Cursor(CanonicalWriter writer, EffectCursor cursor)
    {
        EffectCanonical.Node(writer, cursor.Node);
        writer.WriteBoolean(cursor.Binding.HasValue);
        if (cursor.Binding is { } binding) { Target(writer, binding); }
        Result(writer, cursor.Previous);
        writer.WriteInt32((int)cursor.Status);
        writer.WriteInt32(cursor.Index);
        writer.WriteInt32(cursor.Iterations);
        writer.WriteInt32(cursor.LoopIndex);
        writer.WriteCount(cursor.AwaitingIntents.IsDefault ? 0 : cursor.AwaitingIntents.Length);
        if (!cursor.AwaitingIntents.IsDefault)
        { foreach (var id in cursor.AwaitingIntents.OrderBy(value => value.Value)) { writer.WriteUInt64(id.Value); } }
        writer.WriteCount(cursor.Children.IsDefault ? 0 : cursor.Children.Length);
        if (!cursor.Children.IsDefault) { foreach (var child in cursor.Children) { Cursor(writer, child); } }
        writer.WriteBoolean(cursor.Result is not null);
        if (cursor.Result is { } result) { Result(writer, result); }
    }

    private static void Result(CanonicalWriter writer, EffectResult result)
    {
        writer.WriteInt32((int)result.Status);
        writer.WriteInt64(result.Scalar);
        writer.WriteCount(result.Receipts.Length);
        foreach (var id in result.Receipts.OrderBy(value => value.Value)) { writer.WriteUInt64(id.Value); }
        Targets(result.Affected); Targets(result.Created); Targets(result.Removed);
        void Targets(ImmutableArray<EffectTarget> targets)
        {
            writer.WriteCount(targets.Length);
            foreach (var target in targets.OrderBy(value => value.Type).ThenBy(value => value.Id)) { Target(writer, target); }
        }
    }

    private static void Target(CanonicalWriter writer, EffectTarget target) { writer.WriteInt32((int)target.Type); writer.WriteUInt64(target.Id); }
    private static void Entity(CanonicalWriter writer, BattlefieldEntityState? entity)
    {
        writer.WriteBoolean(entity is not null);
        if (entity is not null) { MatchStateHasher.WriteEntity(writer, entity); }
    }
    private static void Event(CanonicalWriter writer, DomainEvent? fact)
    {
        writer.WriteBoolean(fact is not null);
        if (fact is not null) { writer.WriteHash(FrameBatchHasher.ComputeEvents(fact.FrameId, [fact])); }
    }
    private static void OptionalId(CanonicalWriter writer, ulong? id)
    { writer.WriteBoolean(id.HasValue); if (id.HasValue) { writer.WriteUInt64(id.Value); } }
    private static void OptionalNumber(CanonicalWriter writer, long? value)
    { writer.WriteBoolean(value.HasValue); if (value.HasValue) { writer.WriteInt64(value.Value); } }
}
