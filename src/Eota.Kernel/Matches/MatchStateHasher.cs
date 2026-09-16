using System.Collections.Immutable;
using Eota.Kernel.Commands;
using Eota.Kernel.Determinism;
using Eota.Kernel.Primitives;
using Eota.Kernel.Rules;

namespace Eota.Kernel.Matches;

public static class MatchStateHasher
{
    public const ushort CanonicalSchemaVersion = 10;

    public static Hash256 Compute(MatchState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return CanonicalHash.Compute("eota.match-state", CanonicalSchemaVersion, writer => WriteState(writer, state));
    }

    private static void WriteState(CanonicalWriter writer, MatchState state)
    {
        WriteManifest(writer, state.Manifest);
        writer.WriteInt32((int)state.Status);
        writer.WriteInt32((int)state.Outcome);
        writer.WriteInt32((int)state.Stage);
        writer.WriteInt32(state.Turn);
        writer.WriteUInt64(state.Revision);

        writer.WriteCount(state.Players.Length);
        foreach (var player in state.Players.OrderBy(value => value.Id))
        {
            writer.WriteByte(player.Id.Value);
            writer.WriteInt32((int)player.Profession);
            writer.WriteInt64(player.HeroHealth);
            writer.WriteInt64(player.HeroMaximumHealth);
            writer.WriteInt64(player.CurrentCost);
            writer.WriteInt64(player.MaxCost);
            writer.WriteInt64(player.NextTurnCost);
            writer.WriteInt64(player.FatigueCount);
            Eota.Kernel.Effects.EffectExecutionCanonical.Attachments(writer, player.AttachedEffects);
            WriteOrderedCardIds(writer, player.Deck);
            WriteSortedCardIds(writer, player.Hand);

            writer.WriteCount(player.Planning.Length);
            foreach (var plan in player.Planning.OrderBy(value => value.Id))
            {
                WritePlan(writer, plan);
            }

            WriteSortedCardIds(writer, player.Discard);
            WriteSortedCardIds(writer, player.Removed);
            writer.WriteBoolean(player.Mulligan is not null);
            if (player.Mulligan is { } mulligan)
            {
                WriteSortedCardIds(writer, mulligan.EligibleCards);
                WriteSortedCardIds(writer, mulligan.SelectedCards);
                writer.WriteBoolean(mulligan.Submitted);
            }

            writer.WriteBoolean(player.TurnSubmitted);
            writer.WriteUInt64(player.CommandRevision);
            writer.WriteUInt64(player.NextPlanOrdinal);
        }

        writer.WriteCount(state.Lanes.Length);
        foreach (var lane in state.Lanes.OrderBy(value => value.Id))
        {
            writer.WriteInt32(lane.Id.Value);
            WritePlayerLane(writer, lane.PlayerOne);
            WritePlayerLane(writer, lane.PlayerTwo);
            writer.WriteCount(lane.Statuses.Length);
            foreach (var status in lane.Statuses.OrderBy(value => value.Kind))
            { writer.WriteInt32((int)status.Kind); WriteOptionalInt64(writer, status.RemainingDuration); }
        }

        writer.WriteCount(state.CardInstances.Length);
        foreach (var instance in state.CardInstances.OrderBy(value => value.Id))
        {
            writer.WriteUInt64(instance.Id.Value);
            writer.WriteString(instance.OriginalPrototypeId.Value);
            writer.WriteString(instance.CurrentPrototypeId.Value);
            writer.WriteByte(instance.OwnerId.Value);
            writer.WriteInt32((int)instance.Zone);
            WriteOptionalInt64(writer, instance.Cost);
            WriteOptionalInt64(writer, instance.Attack);
            WriteOptionalInt64(writer, instance.MaximumHealth);
            writer.WriteBoolean(!instance.Keywords.IsDefault);
            if (!instance.Keywords.IsDefault)
            {
                writer.WriteCount(instance.Keywords.Length);
                foreach (var keyword in instance.Keywords.OrderBy(value => value.Kind))
                { writer.WriteInt32((int)keyword.Kind); writer.WriteInt64(keyword.Parameter); }
            }
        }

        writer.WriteCount(state.Entities.Length);
        foreach (var entity in state.Entities.OrderBy(value => value.Id.Value))
        {
            WriteEntity(writer, entity);
        }

        writer.WriteCount(state.Tombstones.Length);
        foreach (var tombstone in state.Tombstones.OrderBy(value => value.Id.Value))
        {
            writer.WriteUInt64(tombstone.Id.Value);
            writer.WriteUInt64(tombstone.EntityId.Value);
            writer.WriteUInt64(tombstone.CardInstanceId.Value);
            writer.WriteString(tombstone.PrototypeId.Value);
            writer.WriteByte(tombstone.OwnerId.Value);
            writer.WriteByte(tombstone.ControllerId.Value);
            writer.WriteInt32(tombstone.LaneId.Value);
            writer.WriteInt32((int)tombstone.Reason);
            WriteOptionalInt64(writer, tombstone.Attack);
            WriteOptionalInt64(writer, tombstone.CurrentHealth);
            WriteOptionalInt64(writer, tombstone.MaximumHealth);
            WriteOptionalInt64(writer, tombstone.FieldEnergy);
            writer.WriteUInt64(tombstone.FrameId.Value);
            writer.WriteBoolean(tombstone.FinalEntity is not null);
            if (tombstone.FinalEntity is { } finalEntity) { WriteEntity(writer, finalEntity); }
        }

        writer.WriteCount(state.CommandLog.Length);
        foreach (var command in state.CommandLog.OrderBy(value => value.Id.Value))
        {
            writer.WriteUInt64(command.Id.Value);
            writer.WriteUInt64(command.MatchRevision);
            WriteCommand(writer, command.Command);
        }

        writer.WriteUInt64(state.NextCardInstanceId.Value);
        writer.WriteUInt64(state.NextEntityId.Value);
        writer.WriteUInt64(state.NextCommandId.Value);
        writer.WriteUInt64(state.NextFrameId.Value);
        writer.WriteUInt64(state.NextWorkItemId.Value);
        writer.WriteUInt64(state.NextIntentId.Value);
        writer.WriteUInt64(state.NextConflictGroupId.Value);
        writer.WriteUInt64(state.NextReceiptId.Value);
        writer.WriteUInt64(state.NextEventId.Value);
        writer.WriteUInt64(state.NextTombstoneId.Value);
        writer.WriteUInt64(state.RuleRng.S0);
        writer.WriteUInt64(state.RuleRng.S1);
        writer.WriteUInt64(state.RuleRng.S2);
        writer.WriteUInt64(state.RuleRng.S3);
        writer.WriteUInt64(state.RuleRng.SampleCount);
        writer.WriteUInt64(state.NextEffectProgramId);
        writer.WriteBoolean(state.Execution is not null);
        if (state.Execution is { } execution) { Eota.Kernel.Effects.EffectExecutionCanonical.Write(writer, execution); }
    }

    internal static void WriteEntity(CanonicalWriter writer, BattlefieldEntityState entity)
    {
        writer.WriteInt32(entity is MinionEntityState ? 1 : 2);
        writer.WriteUInt64(entity.Id.Value);
        writer.WriteUInt64(entity.CardInstanceId.Value);
        writer.WriteByte(entity.OwnerId.Value);
        writer.WriteByte(entity.ControllerId.Value);
        writer.WriteInt32(entity.LaneId.Value);
        writer.WriteInt64(entity.StoredCharge);
        WriteOptionalInt64(writer, entity.ChargeRequirementOverride);
        WriteOptionalInt64(writer, entity.PermanentChargeRequirementOverride);
        writer.WriteCount(entity.TemporaryChargeOverrides.Length);
        foreach (var charge in entity.TemporaryChargeOverrides.OrderBy(value => value.Id.Value))
        { writer.WriteUInt64(charge.Id.Value); writer.WriteInt64(charge.Value); writer.WriteInt64(charge.RemainingDuration); }
        Eota.Kernel.Effects.EffectExecutionCanonical.Attachments(writer, entity.AttachedEffects);
        writer.WriteCount(entity.TemporaryModifiers.Length);
        foreach (var modifier in entity.TemporaryModifiers.OrderBy(value => value.Id.Value))
        {
            writer.WriteUInt64(modifier.Id.Value); writer.WriteByte(modifier.SourcePlayerId.Value);
            writer.WriteInt32((int)modifier.Action); writer.WriteInt32((int)modifier.Attribute); writer.WriteInt32((int)modifier.Operation);
            writer.WriteInt64(modifier.Value); writer.WriteInt32((int)modifier.Keyword); writer.WriteInt64(modifier.RemainingDuration);
        }
        writer.WriteBoolean(!entity.PermanentKeywords.IsDefault);
        if (!entity.PermanentKeywords.IsDefault)
        {
            writer.WriteCount(entity.PermanentKeywords.Length);
            foreach (var keyword in entity.PermanentKeywords.OrderBy(value => value.Kind))
            { writer.WriteInt32((int)keyword.Kind); writer.WriteInt64(keyword.Parameter); }
        }
        switch (entity)
        {
            case MinionEntityState minion:
                WriteOptionalInt64(writer, minion.BaseAttack);
                writer.WriteCount(minion.TemporaryBaseAttacks.Length);
                foreach (var layer in minion.TemporaryBaseAttacks.OrderBy(value => value.Id.Value))
                { writer.WriteUInt64(layer.Id.Value); writer.WriteInt64(layer.Value); writer.WriteInt64(layer.RemainingDuration); }
                writer.WriteInt64(minion.IncomingDamageAdjustment);
                writer.WriteInt64(minion.Attack);
                writer.WriteInt64(minion.CurrentHealth);
                writer.WriteInt64(minion.MaximumHealth);
                writer.WriteCount(minion.Keywords.Length);
                foreach (var keyword in minion.Keywords
                             .OrderBy(value => value.Kind)
                             .ThenBy(value => value.Parameter))
                {
                    writer.WriteInt32((int)keyword.Kind);
                    writer.WriteInt64(keyword.Parameter);
                }

                writer.WriteInt64(minion.SlowTurnsRemaining);
                writer.WriteUInt64(minion.SlowGeneration);
                writer.WriteInt32(minion.EnteredOnTurn);
                writer.WriteBoolean(minion.IsDead);
                break;
            case FieldEntityState field:
                switch (field.Lifetime)
                {
                    case FiniteFieldLifetimeState finite:
                        writer.WriteInt32(1);
                        writer.WriteInt64(finite.Energy);
                        break;
                    case PermanentFieldLifetimeState:
                        writer.WriteInt32(2);
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported field lifetime type '{field.Lifetime.GetType().Name}'.");
                }

                writer.WriteCount(field.Keywords.Length);
                foreach (var keyword in field.Keywords.OrderBy(value => value))
                {
                    writer.WriteInt32((int)keyword);
                }

                writer.WriteInt32(field.EnteredOnTurn);
                writer.WriteBoolean(field.IsDestroyed);
                break;
            default:
                throw new InvalidOperationException($"Unsupported entity type '{entity.GetType().Name}'.");
        }
    }

    private static void WritePlayerLane(CanonicalWriter writer, PlayerLaneState lane)
    {
        writer.WriteByte(lane.PlayerId.Value);
        WriteOptionalEntityId(writer, lane.MinionEntityId);
        WriteOptionalEntityId(writer, lane.FieldEntityId);
        writer.WriteInt64(lane.EtherActivation);
        writer.WriteBoolean(lane.PreventNextEtherDecay);
    }

    private static void WriteOptionalEntityId(CanonicalWriter writer, EntityId? entityId)
    {
        writer.WriteBoolean(entityId.HasValue);
        if (entityId is { } value)
        {
            writer.WriteUInt64(value.Value);
        }
    }

    private static void WritePlan(CanonicalWriter writer, PlannedAction plan)
    {
        writer.WriteByte(plan.Id.PlayerId.Value);
        writer.WriteUInt64(plan.Id.Ordinal);
        writer.WriteUInt64(plan.CardInstanceId.Value);
        writer.WriteInt32((int)plan.Kind);
        writer.WriteBoolean(plan.LaneId.HasValue);
        if (plan.LaneId is { } laneId)
        {
            writer.WriteInt32(laneId.Value);
        }

        writer.WriteInt64(plan.AuthoritativeCost);
        writer.WriteInt64(plan.ReservedCost);
    }

    private static void WriteCommand(CanonicalWriter writer, AuthoritativeCommand command)
    {
        writer.WriteInt32((int)command.Kind);
        writer.WriteByte(command.PlayerId.Value);
        writer.WriteUInt64(command.ExpectedPlayerRevision);
        switch (command)
        {
            case SubmitMulliganCommand mulligan:
                WriteSortedCardIds(writer, mulligan.ReplacedCards);
                break;
            case PlanCardCommand planCard:
                writer.WriteUInt64(planCard.CardInstanceId.Value);
                writer.WriteInt32(planCard.LaneId.Value);
                break;
            case PlanSpellCommand planSpell:
                writer.WriteUInt64(planSpell.CardInstanceId.Value);
                writer.WriteBoolean(planSpell.TargetLaneId.HasValue);
                if (planSpell.TargetLaneId is { } laneId)
                {
                    writer.WriteInt32(laneId.Value);
                }

                break;
            case CancelPlanCommand cancelPlan:
                writer.WriteByte(cancelPlan.PlanCommandId.PlayerId.Value);
                writer.WriteUInt64(cancelPlan.PlanCommandId.Ordinal);
                break;
            case SubmitTurnCommand:
                break;
            case SystemTimeoutCommand timeout:
                writer.WriteInt32(timeout.ExpectedTurn);
                writer.WriteInt32((int)timeout.ExpectedStage);
                break;
            default:
                throw new InvalidOperationException($"Unsupported command type '{command.GetType().Name}'.");
        }
    }

    private static void WriteOrderedCardIds(CanonicalWriter writer, ImmutableArray<CardInstanceId> cardIds)
    {
        writer.WriteCount(cardIds.Length);
        foreach (var cardId in cardIds)
        {
            writer.WriteUInt64(cardId.Value);
        }
    }

    private static void WriteSortedCardIds(CanonicalWriter writer, ImmutableArray<CardInstanceId> cardIds)
    {
        writer.WriteCount(cardIds.Length);
        foreach (var cardId in cardIds.OrderBy(value => value))
        {
            writer.WriteUInt64(cardId.Value);
        }
    }

    private static void WriteManifest(CanonicalWriter writer, MatchManifest manifest)
    {
        writer.WriteString(manifest.ProtocolId);
        writer.WriteInt32(manifest.ProtocolVersion);
        writer.WriteHash(manifest.ProtocolHash);
        writer.WriteString(manifest.ContentSchemaVersion);
        writer.WriteHash(manifest.RuleContentHash);
        writer.WriteUInt64(manifest.MatchSeed);
        WriteDeck(writer, manifest.PlayerOneDeck);
        WriteDeck(writer, manifest.PlayerTwoDeck);
    }

    private static void WriteDeck(CanonicalWriter writer, DeckDefinition deck)
    {
        writer.WriteInt32((int)deck.Profession);
        writer.WriteCount(deck.Entries.Length);
        foreach (var entry in deck.Entries.OrderBy(value => value.CardId))
        {
            writer.WriteString(entry.CardId.Value);
            writer.WriteInt32(entry.Copies);
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
