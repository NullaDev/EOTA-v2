using Eota.Kernel.Content;

namespace Eota.Kernel.Effects;

internal static class EffectReferences
{
    internal static void Validate(IEnumerable<CardDefinition> definitions)
    {
        var cards = definitions.ToDictionary(value => value.Id);
        foreach (var card in cards.Values)
        {
            if (card.StoredCharge < 0 || card.Kind == CardKind.Spell && card.StoredCharge != 0)
            { throw new ArgumentException($"Invalid stored charge on '{card.Id}'."); }
            foreach (var effect in card.Effects)
            {
                if ((effect.Trigger.Kind is EffectTriggerKind.PreCombatCharge or EffectTriggerKind.EndTurnCharge) != effect.ChargeRequirement.HasValue || effect.ChargeRequirement < 0)
                { throw new ArgumentException($"Invalid charge requirement on '{card.Id}/{effect.Id}'."); }
                Visit(effect.Body, null, card.Kind == CardKind.Minion ? EffectTargetType.Minion : card.Kind == CardKind.Field ? EffectTargetType.Field : null, effect.Trigger.SubjectType);
            }

            void Visit(EffectNode node, EffectTargetType? binding, EffectTargetType? selfType, EffectTargetType? eventType)
            {
                switch (node)
                {
                    case GrantEffect grant: Visit(grant.Definition.Body, null, TypeOf(grant.Selector, binding, selfType, eventType), grant.Definition.Trigger.SubjectType); break;
                    case ParallelEffect parallel: foreach (var child in parallel.Children) { Visit(child, binding, selfType, eventType); } break;
                    case SequenceEffect sequence: foreach (var child in sequence.Steps) { Visit(child, binding, selfType, eventType); } break;
                    case LoopEffect loop: Visit(loop.Body, binding, selfType, eventType); break;
                    case RetargetEffect retarget: Visit(retarget.Body, TypeOf(retarget.Selector, binding, selfType, eventType), selfType, eventType); break;
                    case IfElseEffect branch:
                        Visit(branch.Then, binding, selfType, eventType); if (branch.Else is not null) { Visit(branch.Else, binding, selfType, eventType); }
                        break;
                    case SummonEffect summon:
                        if (!cards.TryGetValue(summon.PrototypeId, out var summoned) || (summon.SlotKind == Matches.BattlefieldSlotKind.Minion
                            ? summoned.Kind != CardKind.Minion : summoned.Kind != CardKind.Field))
                        { throw new ArgumentException($"Invalid summon prototype '{summon.PrototypeId}' on '{card.Id}'."); }
                        break;
                    case HandEffect { Kind: HandRequestKind.Generate } generated:
                        if (generated.PrototypeId is not { } generatedId || !cards.ContainsKey(generatedId))
                        { throw new ArgumentException($"Missing generation prototype on '{card.Id}'."); }
                        break;
                    case LifecycleEffect { PrototypeId: { } prototype } lifecycle:
                        var targetType = TypeOf(lifecycle.Selector, binding, selfType, eventType);
                        if (!cards.TryGetValue(prototype, out var transformed) || transformed.Kind == CardKind.Spell
                            || targetType == EffectTargetType.Minion && transformed.Kind != CardKind.Minion
                            || targetType == EffectTargetType.Field && transformed.Kind != CardKind.Field)
                        { throw new ArgumentException($"Invalid lifecycle prototype '{prototype}' on '{card.Id}'."); }
                        break;
                }
            }

            static EffectTargetType? TypeOf(EffectSelector selector, EffectTargetType? binding, EffectTargetType? selfType, EffectTargetType? eventType) => selector.Kind switch
            {
                SelectorKind.Self => selfType,
                SelectorKind.EventSubject => eventType,
                SelectorKind.EventTarget => EffectTargetType.Minion,
                SelectorKind.FriendlyHero or SelectorKind.EnemyHero => EffectTargetType.Hero,
                SelectorKind.Targets => binding,
                SelectorKind.FriendlyMinions or SelectorKind.EnemyMinions or SelectorKind.AllMinions => EffectTargetType.Minion,
                SelectorKind.FriendlyFields or SelectorKind.EnemyFields => EffectTargetType.Field,
                SelectorKind.PreviousAffected or SelectorKind.PreviousCreated or SelectorKind.PreviousRemoved => selector.FilterType,
                _ => null
            };
        }
    }
}
