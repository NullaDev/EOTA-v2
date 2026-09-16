using System.Collections.Immutable;
using System.Numerics;
using Eota.Kernel.Effects;
using Eota.Kernel.Matches;

namespace Eota.Kernel.Resolution;

internal sealed partial class ReductionContext
{
    private MinionEntityState ApplyBaseAttack(IntentConflictGroup group, MinionEntityState original)
    {
        var baseline = original.BaseAttack ?? original.Attack;
        var oldBase = original.TemporaryBaseAttacks.IsEmpty ? baseline : original.TemporaryBaseAttacks[^1].Value;
        if (!TryInt64(Number(group, NumericProperty.BaseAttack, baseline, 0), out baseline))
        { SetFrameError("arithmetic-overflow"); return original; }
        var active = original.TemporaryBaseAttacks.ToList();
        var decays = group.Intents.OfType<DecayModifiersIntent>().ToArray();
        var decay = decays.Aggregate(BigInteger.Zero, (sum, item) => sum + Math.Max(0, item.Amount));
        for (var index = active.Count - 1; index >= 0; index--)
        {
            var remaining = BigInteger.Max(0, active[index].RemainingDuration - decay);
            if (remaining == 0) { active.RemoveAt(index); }
            else { active[index] = active[index] with { RemainingDuration = (long)remaining }; }
        }
        if (!original.TemporaryBaseAttacks.IsEmpty)
        {
            foreach (var intent in decays)
            { AddReceipt(intent.Id, decay > 0 ? IntentReceiptStatus.Applied : IntentReceiptStatus.NoOp, "decay-modifiers", original.Id, original.ControllerId, null); }
        }
        var selected = _setChoices.SingleOrDefault(value => value.ConflictKey == group.Key && value.Attribute == NumericProperty.BaseAttack);
        foreach (var intent in group.Intents.OfType<TemporaryModifierIntent>().Where(NumericRules.IsBaseAttack))
        {
            if (selected?.SelectedIntentId != intent.Id)
            { AddReceipt(intent.Id, IntentReceiptStatus.Rejected, "set-not-selected", original.Id, null, null); continue; }
            active.Add(new TemporaryBaseAttack(intent.Id, intent.Modifier.Value, intent.Modifier.RemainingDuration));
            AddReceipt(intent.Id, IntentReceiptStatus.Applied, "temporary-base-attack", original.Id, original.ControllerId, intent.Modifier.Value);
        }
        var layers = active.OrderBy(value => value.Id.Value).ToImmutableArray();
        var currentBase = layers.IsEmpty ? baseline : layers[^1].Value;
        if (!TryInt64(new BigInteger(original.Attack) - oldBase + currentBase, out var attack))
        { SetFrameError("arithmetic-overflow"); return original; }
        if (!layers.SequenceEqual(original.TemporaryBaseAttacks))
        { AddEvent(new EventDraft(DomainEventKind.EntityMechanicsChanged, original.Id, original.ControllerId, null, null, null, "base-attack")); }
        return original with { Attack = attack, BaseAttack = baseline, TemporaryBaseAttacks = layers };
    }
}
