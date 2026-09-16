using System.Collections.Immutable;
using Eota.Kernel.Content;
using Eota.Kernel.Effects;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;

namespace Eota.Kernel.Resolution;

internal sealed partial class ReductionContext
{
    private void ReduceCardModifiers(IntentConflictGroup group)
    {
        var instance = _cardInstances.SingleOrDefault(value => value.Id.Value == group.Key.StableTargetId && value.Zone is CardZone.Hand or CardZone.Deck);
        if (instance is null) { RejectAll(group, "card-not-in-modifiable-zone", null, null); return; }
        ValidateNumerics(group);
        var definition = CardInstanceRules.Definition(_state, instance);
        var costNumber = Number(group, NumericProperty.CardCost, definition.Cost, 0);
        if (!TryInt64(costNumber, out var cost)) { SetFrameError("arithmetic-overflow"); return; }
        var updated = instance with { Cost = cost == definition.Cost ? instance.Cost : cost };
        if (definition is MinionCardDefinition minion)
        {
            var attackNumber = Number(group, NumericProperty.Attack, minion.Attack, 0);
            var healthNumber = Number(group, NumericProperty.MaximumHealth, minion.Health, 0);
            if (!TryInt64(attackNumber, out var attack) || !TryInt64(healthNumber, out var health))
            { SetFrameError("arithmetic-overflow"); return; }
            var keywords = minion.Keywords.ToDictionary(value => value.Kind);
            foreach (var changes in group.Intents.OfType<ChangeCardKeywordIntent>().GroupBy(value => value.Keyword).OrderBy(value => value.Key))
            {
                var valid = changes.Where(value => Enum.IsDefined(value.Keyword) && (value.Remove || value.Keyword != MinionKeywordKind.Slow || value.Parameter > 0)).ToArray();
                foreach (var invalid in changes.Except(valid))
                { AddReceipt(invalid.Id, IntentReceiptStatus.Rejected, "invalid-card-keyword", null, instance.OwnerId, null, instance.Id); }
                if (valid.Length == 0) { continue; }
                var remove = valid.Any(value => value.Remove);
                var before = keywords.GetValueOrDefault(changes.Key);
                if (remove) { keywords.Remove(changes.Key); }
                else
                {
                    keywords[changes.Key] = new MinionKeywordDefinition(changes.Key, changes.Key == MinionKeywordKind.Slow
                    ? Math.Max(before.Parameter, valid.Max(value => value.Parameter)) : 0);
                }
                foreach (var change in valid)
                {
                    AddReceipt(change.Id, !change.Remove && remove ? IntentReceiptStatus.Rejected
                    : before == keywords.GetValueOrDefault(changes.Key) ? IntentReceiptStatus.NoOp : IntentReceiptStatus.Applied,
                    "card-keyword", null, instance.OwnerId, null, instance.Id);
                }
            }
            var finalKeywords = keywords.Values.OrderBy(value => value.Kind).ToImmutableArray();
            updated = updated with
            {
                Attack = attack == minion.Attack ? instance.Attack : attack,
                MaximumHealth = health == minion.Health ? instance.MaximumHealth : health,
                Keywords = finalKeywords.SequenceEqual(minion.Keywords) ? instance.Keywords : finalKeywords
            };
        }
        else
        {
            foreach (var keyword in group.Intents.OfType<ChangeCardKeywordIntent>())
            { AddReceipt(keyword.Id, IntentReceiptStatus.Rejected, "card-is-not-minion", null, instance.OwnerId, null, instance.Id); }
        }
        _cardInstances = _cardInstances.Replace(instance, updated);
        foreach (var intent in group.Intents)
        {
            var receipt = _receipts[intent.Id];
            _receipts[intent.Id] = receipt with
            {
                CardInstanceId = instance.Id,
                Outputs = new ReceiptOutputs(
                (receipt.Status is IntentReceiptStatus.Applied or IntentReceiptStatus.PartiallyApplied) ? [new EffectTarget(EffectTargetType.Card, instance.Id.Value)] : [], [], [], [], [], [], [])
            };
        }
        if (updated != instance)
        { AddEvent(new EventDraft(DomainEventKind.CardModified, null, instance.OwnerId, null, null, null, "card-modified", instance.Id)); }
    }
}
