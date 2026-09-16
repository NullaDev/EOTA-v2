using System.Collections.Immutable;
using Eota.Kernel.Determinism;
using Eota.Kernel.Primitives;

namespace Eota.Kernel.Resolution;

public static class FrameBatchHasher
{
    public const ushort ReceiptSchemaVersion = 3;
    public const ushort EventSchemaVersion = 5;

    public static Hash256 ComputeReceipts(FrameId frameId, ImmutableArray<IntentReceipt> receipts) =>
        CanonicalHash.Compute("eota.intent-receipts", ReceiptSchemaVersion, writer =>
        {
            writer.WriteUInt64(frameId.Value);
            writer.WriteCount(receipts.Length);
            foreach (var receipt in receipts.OrderBy(value => value.IntentId.Value))
            {
                writer.WriteUInt64(receipt.Id.Value);
                writer.WriteUInt64(receipt.IntentId.Value);
                writer.WriteInt32((int)receipt.Status);
                writer.WriteString(receipt.DetailCode);
                WriteOptionalUInt64(writer, receipt.EntityId?.Value);
                writer.WriteBoolean(receipt.PlayerId.HasValue);
                if (receipt.PlayerId is { } playerId)
                {
                    writer.WriteByte(playerId.Value);
                }

                WriteOptionalInt64(writer, receipt.AppliedValue);
                WriteOptionalUInt64(writer, receipt.CardInstanceId?.Value);
                writer.WriteBoolean(receipt.Outputs is not null);
                if (receipt.Outputs is { } outputs)
                {
                    foreach (var targets in new[] { outputs.Affected, outputs.Created, outputs.Removed })
                    {
                        writer.WriteCount(targets.Length);
                        foreach (var target in targets.OrderBy(value => value.Type).ThenBy(value => value.Id))
                        { writer.WriteInt32((int)target.Type); writer.WriteUInt64(target.Id); }
                    }
                    foreach (var cards in new[] { outputs.RemovedFromDeck, outputs.EnteredHand, outputs.Burned })
                    {
                        writer.WriteCount(cards.Length);
                        foreach (var card in cards) { writer.WriteUInt64(card.Value); }
                    }
                    writer.WriteCount(outputs.NotCreated.Length);
                    foreach (var prototype in outputs.NotCreated) { writer.WriteString(prototype.Value); }
                }
            }
        });

    public static Hash256 ComputeEvents(FrameId frameId, ImmutableArray<DomainEvent> events) =>
        CanonicalHash.Compute("eota.domain-events", EventSchemaVersion, writer =>
        {
            writer.WriteUInt64(frameId.Value);
            writer.WriteCount(events.Length);
            foreach (var domainEvent in events.OrderBy(value => value.Id.Value))
            {
                writer.WriteUInt64(domainEvent.Id.Value);
                writer.WriteInt32((int)domainEvent.Kind);
                writer.WriteUInt64(domainEvent.FrameId.Value);
                WriteOptionalUInt64(writer, domainEvent.EntityId?.Value);
                writer.WriteBoolean(domainEvent.PlayerId.HasValue);
                if (domainEvent.PlayerId is { } playerId)
                {
                    writer.WriteByte(playerId.Value);
                }

                WriteOptionalUInt64(writer, domainEvent.TombstoneId?.Value);
                WriteOptionalInt64(writer, domainEvent.PreviousValue);
                WriteOptionalInt64(writer, domainEvent.CurrentValue);
                writer.WriteString(domainEvent.DetailCode);
                WriteOptionalUInt64(writer, domainEvent.CardInstanceId?.Value);
                WriteOptionalUInt64(writer, domainEvent.TargetEntityId?.Value);
                writer.WriteBoolean(domainEvent.LaneId.HasValue);
                if (domainEvent.LaneId is { } laneId)
                {
                    writer.WriteInt32(laneId.Value);
                }

                writer.WriteBoolean(domainEvent.TargetPlayerId.HasValue);
                if (domainEvent.TargetPlayerId is { } targetPlayerId)
                {
                    writer.WriteByte(targetPlayerId.Value);
                }
            }
        });

    private static void WriteOptionalUInt64(CanonicalWriter writer, ulong? value)
    {
        writer.WriteBoolean(value.HasValue);
        if (value.HasValue)
        {
            writer.WriteUInt64(value.Value);
        }
    }

    private static void WriteOptionalInt64(CanonicalWriter writer, long? value)
    {
        writer.WriteBoolean(value.HasValue);
        if (value.HasValue)
        {
            writer.WriteInt64(value.Value);
        }
    }
}
