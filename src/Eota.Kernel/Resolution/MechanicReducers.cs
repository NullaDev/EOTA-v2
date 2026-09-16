using System.Collections.Immutable;
using System.Numerics;
using Eota.Kernel.Effects;
using Eota.Kernel.Matches;

namespace Eota.Kernel.Resolution;

internal sealed partial class ReductionContext
{
    private BattlefieldEntityState ApplyMechanics(IntentConflictGroup group, BattlefieldEntityState entity)
    {
        var stored = BigInteger.Max(0, Number(group, NumericProperty.StoredCharge, entity.StoredCharge, 0));
        if (!TryInt64(stored, out var storedValue)) { SetFrameError("arithmetic-overflow"); return entity; }
        var original = _state.Entities.Single(value => value.Id == entity.Id);
        var baseline = original.TemporaryChargeOverrides.IsEmpty ? original.ChargeRequirementOverride : original.PermanentChargeRequirementOverride;
        if (group.Intents.OfType<NumericEffectIntent>().Any(value => value.Attribute == NumericProperty.ChargeRequirement && !_receipts.ContainsKey(value.Id)))
        {
            var value = BigInteger.Max(0, Number(group, NumericProperty.ChargeRequirement, baseline ?? 0, 0));
            if (!TryInt64(value, out var charge)) { SetFrameError("arithmetic-overflow"); return entity; }
            if (group.Intents.OfType<NumericEffectIntent>().Any(intent => intent.Attribute == NumericProperty.ChargeRequirement
                && _receipts[intent.Id].Status == IntentReceiptStatus.Applied)) { baseline = charge; }
        }
        var active = original.TemporaryChargeOverrides.ToList();
        var decays = group.Intents.OfType<DecayModifiersIntent>().ToArray();
        var decay = decays.Aggregate(BigInteger.Zero, (sum, item) => sum + Math.Max(0, item.Amount));
        for (var index = active.Count - 1; index >= 0; index--)
        {
            var remaining = BigInteger.Max(0, active[index].RemainingDuration - decay);
            if (remaining == 0) { active.RemoveAt(index); }
            else { active[index] = active[index] with { RemainingDuration = (long)remaining }; }
        }
        foreach (var intent in decays.Where(value => !_receipts.ContainsKey(value.Id)))
        {
            AddReceipt(intent.Id, decay > 0 && (!original.TemporaryChargeOverrides.IsEmpty || !original.TemporaryModifiers.IsEmpty)
            ? IntentReceiptStatus.Applied : IntentReceiptStatus.NoOp, "decay-modifiers", entity.Id, entity.ControllerId, null);
        }
        var selected = _setChoices.SingleOrDefault(value => value.ConflictKey == group.Key && value.Attribute == NumericProperty.ChargeRequirement);
        foreach (var intent in group.Intents.OfType<TemporaryModifierIntent>().Where(value => !_receipts.ContainsKey(value.Id)))
        {
            if (!NumericRules.IsChargeOverride(intent))
            { AddReceipt(intent.Id, IntentReceiptStatus.Rejected, "invalid-temporary-modifier", entity.Id, null, null); continue; }
            if (selected?.SelectedIntentId != intent.Id)
            { AddReceipt(intent.Id, IntentReceiptStatus.Rejected, "set-not-selected", entity.Id, null, null); continue; }
            active.Add(new TemporaryChargeOverride(intent.Id, Math.Max(0, intent.Modifier.Value), intent.Modifier.RemainingDuration));
            AddReceipt(intent.Id, IntentReceiptStatus.Applied, "temporary-charge-override", entity.Id, entity.ControllerId, Math.Max(0, intent.Modifier.Value));
        }
        var layers = active.OrderBy(value => value.Id.Value).ToImmutableArray();
        var requirement = layers.IsEmpty ? baseline : layers[^1].Value;
        if (storedValue != entity.StoredCharge || requirement != entity.ChargeRequirementOverride || !layers.SequenceEqual(original.TemporaryChargeOverrides))
        { AddEvent(new EventDraft(DomainEventKind.EntityMechanicsChanged, entity.Id, entity.ControllerId, null, null, null, "charge")); }
        return entity with
        {
            StoredCharge = storedValue,
            ChargeRequirementOverride = requirement,
            PermanentChargeRequirementOverride = layers.IsEmpty ? null : baseline,
            TemporaryChargeOverrides = layers,
            AttachedEffects = ApplyAttachments(group, entity.AttachedEffects, entity.ControllerId, entity.Id)
        };
    }
}
