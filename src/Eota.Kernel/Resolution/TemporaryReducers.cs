using System.Collections.Immutable;
using System.Numerics;
using Eota.Kernel.Content;
using Eota.Kernel.Effects;
using Eota.Kernel.Matches;

namespace Eota.Kernel.Resolution;

internal sealed partial class ReductionContext
{
    private MinionEntityState ApplyTemporaryModifiers(IntentConflictGroup group, MinionEntityState original, MinionEntityState updated)
    {
        var grants = group.Intents.OfType<TemporaryModifierIntent>().Where(value => !NumericRules.IsChargeOverride(value) && !NumericRules.IsBaseAttack(value)).ToArray();
        var decays = group.Intents.OfType<DecayModifiersIntent>().ToArray();
        if (original.TemporaryModifiers.IsEmpty && grants.Length == 0 && decays.Length == 0) { return updated; }
        var active = original.TemporaryModifiers.ToList();
        var baseline = (original.PermanentKeywords.IsDefault ? original.Keywords : original.PermanentKeywords).ToDictionary(value => value.Kind);
        foreach (var changes in group.Intents.OfType<ChangeMinionKeywordIntent>().GroupBy(value => value.Keyword))
        {
            var remove = changes.Any(value => value.Remove);
            if (remove)
            {
                baseline.Remove(changes.Key);
                active.RemoveAll(value => value.Action == EffectAction.AddKeyword && value.Keyword == changes.Key);
            }
            else if (grants.Any(value => value.Modifier.Action == EffectAction.RemoveKeyword && value.Modifier.Keyword == changes.Key))
            {
                foreach (var change in changes) { _receipts[change.Id] = _receipts[change.Id] with { Status = IntentReceiptStatus.Rejected, DetailCode = "keyword-remove-wins" }; }
            }
            else if (updated.Keywords.FirstOrDefault(value => value.Kind == changes.Key) is { Kind: not 0 } keyword)
            { baseline[changes.Key] = keyword; }
        }
        // Slow has its own combat-boundary timer and is never a temporary keyword layer.
        baseline.Remove(MinionKeywordKind.Slow);
        if (updated.SlowTurnsRemaining > 0) { baseline[MinionKeywordKind.Slow] = new MinionKeywordDefinition(MinionKeywordKind.Slow, updated.SlowTurnsRemaining); }
        var attackDelta = BigInteger.Zero;
        var healthDelta = BigInteger.Zero;
        var incomingDelta = BigInteger.Zero;
        var decay = decays.Aggregate(BigInteger.Zero, (sum, item) => sum + Math.Max(0, item.Amount));
        for (var index = active.Count - 1; index >= 0; index--)
        {
            var modifier = active[index];
            var remaining = BigInteger.Max(0, new BigInteger(modifier.RemainingDuration) - decay);
            if (remaining == 0) { active.RemoveAt(index); AddDelta(modifier, -1); }
            else { active[index] = modifier with { RemainingDuration = (long)remaining }; }
        }
        foreach (var intent in decays.Where(value => original.TemporaryChargeOverrides.IsEmpty && !_receipts.ContainsKey(value.Id)))
        {
            AddReceipt(intent.Id, decay > 0 && original.TemporaryModifiers.Length > 0 ? IntentReceiptStatus.Applied : IntentReceiptStatus.NoOp,
            "decay-modifiers", original.Id, original.ControllerId, null);
        }
        foreach (var grant in grants)
        {
            var modifier = grant.Modifier;
            var valid = grant.Target.Type == EffectTargetType.Minion && modifier.Id == grant.Id && modifier.RemainingDuration > 0
                && (modifier.Action == EffectAction.ModifyNumber && modifier.Operation == NumericOperation.Add
                    && modifier.Attribute is NumericProperty.Attack or NumericProperty.MaximumHealth or NumericProperty.IncomingDamageAdjustment
                    || modifier.Action is EffectAction.AddKeyword or EffectAction.RemoveKeyword && Enum.IsDefined(modifier.Keyword) && modifier.Keyword != MinionKeywordKind.Slow);
            if (!valid)
            { AddReceipt(grant.Id, IntentReceiptStatus.Rejected, "invalid-temporary-modifier", original.Id, null, null); continue; }
            if (modifier.Action == EffectAction.AddKeyword && (grants.Any(value => value.Modifier.Action == EffectAction.RemoveKeyword && value.Modifier.Keyword == modifier.Keyword)
                || group.Intents.OfType<ChangeMinionKeywordIntent>().Any(value => value.Remove && value.Keyword == modifier.Keyword)))
            { AddReceipt(grant.Id, IntentReceiptStatus.Rejected, "keyword-remove-wins", original.Id, null, null); continue; }
            active.Add(modifier);
            AddDelta(modifier, 1);
            AddReceipt(grant.Id, IntentReceiptStatus.Applied, "temporary-modifier", original.Id, original.ControllerId, modifier.Action == EffectAction.ModifyNumber ? modifier.Value : null);
        }
        var keywords = baseline.ToDictionary(value => value.Key, value => value.Value);
        foreach (var modifier in active.Where(value => value.Action == EffectAction.AddKeyword))
        { keywords[modifier.Keyword] = new MinionKeywordDefinition(modifier.Keyword); }
        foreach (var modifier in active.Where(value => value.Action == EffectAction.RemoveKeyword)) { keywords.Remove(modifier.Keyword); }
        if (!TryInt64(updated.Attack + attackDelta, out var attack) || !TryInt64(updated.MaximumHealth + healthDelta, out var maximum)
            || !TryInt64(updated.CurrentHealth + healthDelta, out var health) || !TryInt64(updated.IncomingDamageAdjustment + incomingDelta, out var incoming))
        { SetFrameError("arithmetic-overflow"); return updated; }
        var currentKeywords = keywords.Values.OrderBy(value => value.Kind).ToImmutableArray();
        if (!currentKeywords.SequenceEqual(updated.Keywords))
        { AddEvent(new EventDraft(DomainEventKind.MinionKeywordsChanged, original.Id, null, null, null, null, "temporary-keywords")); }
        if (attackDelta != 0 || healthDelta != 0 || incomingDelta != 0)
        { AddEvent(new EventDraft(DomainEventKind.EntityStatsChanged, original.Id, null, null, updated.MaximumHealth, maximum, "temporary-stats")); }
        return updated with
        {
            Attack = attack,
            MaximumHealth = maximum,
            CurrentHealth = health,
            IncomingDamageAdjustment = incoming,
            Keywords = currentKeywords,
            TemporaryModifiers = active.OrderBy(value => value.Id.Value).ToImmutableArray(),
            PermanentKeywords = active.Count == 0 ? default : baseline.Values.OrderBy(value => value.Kind).ToImmutableArray()
        };

        void AddDelta(TemporaryModifier modifier, int sign)
        {
            if (modifier.Action != EffectAction.ModifyNumber) { return; }
            var delta = new BigInteger(modifier.Value) * sign;
            if (modifier.Attribute == NumericProperty.Attack) { attackDelta += delta; }
            if (modifier.Attribute == NumericProperty.MaximumHealth) { healthDelta += delta; }
            if (modifier.Attribute == NumericProperty.IncomingDamageAdjustment) { incomingDelta += delta; }
        }
    }
}
