using System.Collections.Immutable;
using Eota.Kernel.Effects;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;

namespace Eota.Kernel.Resolution;

internal static class ReceiptOutputRules
{
    internal static ReceiptOutputs Create(AtomicIntent intent, ReceiptDraft receipt, MatchState state, FrameId frame)
    {
        var affected = ImmutableArray.CreateBuilder<EffectTarget>();
        var created = ImmutableArray.CreateBuilder<EffectTarget>();
        var removed = ImmutableArray.CreateBuilder<EffectTarget>();
        if (receipt.Status is IntentReceiptStatus.Applied or IntentReceiptStatus.PartiallyApplied)
        {
            var target = intent switch
            {
                NumericEffectIntent number => (EffectTarget?)number.Target,
                LifecycleEffectIntent lifecycle => lifecycle.Target,
                ClearEtherIntent clear => Lane(clear.PlayerId, clear.LaneId),
                ChangeLaneStatusIntent status => Lane(status.SourcePlayerId, status.LaneId),
                PreventEtherDecayIntent prevent => Lane(prevent.PlayerId, prevent.LaneId),
                DecayEtherIntent decay => Lane(decay.PlayerId, decay.LaneId),
                ConsumePlannedSpellIntent spell => new EffectTarget(EffectTargetType.Card, spell.CardInstanceId.Value),
                _ => receipt.EntityId is { } entity ? Entity(entity)
                    : receipt.PlayerId is { } player && intent.ConflictKey.Kind is ConflictKind.HeroHealth or ConflictKind.PlayerResource
                        ? new EffectTarget(EffectTargetType.Hero, player.Value) : null
            };
            if (target is { } value)
            {
                affected.Add(value);
                if (value.Type is EffectTargetType.Minion or EffectTargetType.Field
                    && state.Tombstones.Any(tombstone => tombstone.FrameId == frame && tombstone.EntityId.Value == value.Id))
                { removed.Add(value); }
            }
            if (receipt.EntityId is { } newEntity && intent is SummonIntent or DeployMinionIntent or DeployFieldIntent or LifecycleEffectIntent { Operation: LifecycleOperation.Replace })
            { created.Add(Entity(newEntity)); }
            if (intent is DeployMinionIntent or DeployFieldIntent)
            {
                var entered = state.Entities.SingleOrDefault(value => value.Id == receipt.EntityId);
                if (entered is not null)
                {
                    foreach (var tombstone in state.Tombstones.Where(value => value.FrameId == frame && value.Reason == EntityRemovalReason.Replace
                        && value.ControllerId == entered.ControllerId && value.LaneId == entered.LaneId
                        && (value.FinalEntity is MinionEntityState) == (entered is MinionEntityState)))
                    { removed.Add(Entity(tombstone.EntityId)); }
                }
            }
        }
        return new ReceiptOutputs(affected.ToImmutable(), created.ToImmutable(), removed.ToImmutable(), [], [], [], []);

        static EffectTarget Lane(PlayerId player, LaneId lane) => new(EffectTargetType.Lane, ((ulong)(uint)lane.Value << 1) | player.Value);
        EffectTarget Entity(EntityId id)
        {
            var entity = state.Entities.SingleOrDefault(value => value.Id == id) ?? state.Tombstones.SingleOrDefault(value => value.EntityId == id)?.FinalEntity;
            return new EffectTarget(entity is FieldEntityState ? EffectTargetType.Field : EffectTargetType.Minion, id.Value);
        }
    }
}
