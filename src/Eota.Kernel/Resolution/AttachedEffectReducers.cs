using System.Collections.Immutable;
using System.Numerics;
using Eota.Kernel.Effects;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;

namespace Eota.Kernel.Resolution;

internal sealed partial class ReductionContext
{
    private ImmutableArray<AttachedEffect> ApplyAttachments(IntentConflictGroup group, ImmutableArray<AttachedEffect> original, PlayerId controller, EntityId? entity)
    {
        var active = original.ToList();
        var decays = group.Intents.OfType<DecayAttachedEffectsIntent>().ToArray();
        var decay = decays.Aggregate(BigInteger.Zero, (sum, item) => sum + Math.Max(0, item.Amount));
        for (var index = active.Count - 1; index >= 0; index--)
        {
            if (active[index].RemainingDuration is not { } duration) { continue; }
            var remaining = BigInteger.Max(0, new BigInteger(duration) - decay);
            if (remaining == 0) { active.RemoveAt(index); }
            else { active[index] = active[index] with { RemainingDuration = (long)remaining }; }
        }
        foreach (var intent in decays)
        {
            AddReceipt(intent.Id, original.Any(value => value.RemainingDuration.HasValue) && decay > 0 ? IntentReceiptStatus.Applied : IntentReceiptStatus.NoOp,
            "decay-attached-effects", entity, controller, null);
        }
        foreach (var intent in group.Intents.OfType<AttachEffectIntent>())
        {
            if (intent.Duration <= 0 || intent.Target.Type is not (EffectTargetType.Minion or EffectTargetType.Field or EffectTargetType.Hero))
            { AddReceipt(intent.Id, IntentReceiptStatus.Rejected, "invalid-effect-attachment", entity, controller, null); continue; }
            active.Add(new AttachedEffect(intent.Id, intent.Definition, intent.Duration, intent.Origin with { ControllerId = controller, FrozenEntity = null }));
            AddReceipt(intent.Id, IntentReceiptStatus.Applied, "attach-effect", entity, controller, null);
        }
        var result = active.OrderBy(value => value.Id.Value).ToImmutableArray();
        if (!result.SequenceEqual(original))
        { AddEvent(new EventDraft(DomainEventKind.AttachedEffectsChanged, entity, controller, null, null, null, "attached-effects")); }
        return result;
    }
}
