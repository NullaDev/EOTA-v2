using System.Collections.Immutable;
using Eota.Kernel.Matches;
using Eota.Kernel.Resolution;

namespace Eota.Kernel.Effects;

public static partial class EffectTriggers
{
    private static IEnumerable<EffectDefinition> AttachedDefinitions(ImmutableArray<AttachedEffect> attached) =>
        attached.OrderBy(value => value.Id.Value).Select(value => value.Definition with { Id = value.Definition.Id + "@" + value.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    private static ImmutableArray<EffectInvocation> PlayerObservers(MatchState before, FrameTransition frame)
    {
        var results = ImmutableArray.CreateBuilder<EffectInvocation>();
        foreach (var fact in frame.Events.Events)
        {
            var leaving = fact.Kind is DomainEventKind.EntityDied or DomainEventKind.EntityLeft;
            if (fact.Kind == DomainEventKind.EntityHealed && !frame.State.Entities.Any(value => value.Id == fact.EntityId)) { continue; }
            var entity = frame.State.Entities.SingleOrDefault(value => value.Id == fact.EntityId)
                ?? frame.State.Tombstones.SingleOrDefault(value => value.EntityId == fact.EntityId)?.FinalEntity;
            var owner = entity?.ControllerId ?? fact.PlayerId;
            var lane = entity?.LaneId ?? fact.LaneId;
            foreach (var player in leaving ? before.Players : frame.State.Players)
            {
                foreach (var attached in player.AttachedEffects.OrderBy(value => value.Id.Value))
                {
                    var definition = AttachedDefinitions([attached]).Single();
                    var trigger = definition.Trigger;
                    var matches = trigger.Kind switch
                    {
                        EffectTriggerKind.EnemyDied => fact.Kind == DomainEventKind.EntityDied && owner == player.Id.Opponent,
                        EffectTriggerKind.FriendlyDied => fact.Kind == DomainEventKind.EntityDied && owner == player.Id,
                        EffectTriggerKind.EnemyLeft => fact.Kind == DomainEventKind.EntityLeft && owner == player.Id.Opponent,
                        EffectTriggerKind.FriendlyLeft => fact.Kind == DomainEventKind.EntityLeft && owner == player.Id,
                        EffectTriggerKind.EnemyEntered => fact.Kind == DomainEventKind.EntityEntered && owner == player.Id.Opponent,
                        EffectTriggerKind.FriendlyEntered => fact.Kind == DomainEventKind.EntityEntered && owner == player.Id,
                        EffectTriggerKind.EnemyAttack => fact.Kind == DomainEventKind.AttackDeclared && owner == player.Id.Opponent,
                        EffectTriggerKind.FriendlyCombat => fact.Kind == DomainEventKind.CombatDeclared && owner == player.Id,
                        EffectTriggerKind.EnemyCombat => fact.Kind == DomainEventKind.CombatDeclared && owner == player.Id.Opponent,
                        EffectTriggerKind.FriendlySpellCast => fact.Kind == DomainEventKind.SpellResolved && owner == player.Id,
                        EffectTriggerKind.FriendlyHealed => fact.Kind == DomainEventKind.EntityHealed && owner == player.Id && fact.CurrentValue > 0,
                        EffectTriggerKind.FriendlyHeroHealed => fact.Kind == DomainEventKind.HeroHealed && owner == player.Id && fact.CurrentValue > 0,
                        _ => false
                    };
                    if (matches && (fact.Kind == DomainEventKind.HeroHealed || trigger.Scope == SelectionScope.All || lane == attached.Origin.LaneId)
                        && SubjectMatches(trigger.SubjectType, entity, fact))
                    {
                        results.Add(new EffectInvocation(attached.Origin with { ControllerId = player.Id, FrozenEntity = null }, definition, fact, entity,
                        EventPrototypeId: frame.State.Tombstones.SingleOrDefault(value => value.Id == fact.TombstoneId)?.PrototypeId is { } leftPrototype && leaving
                            ? leftPrototype : frame.State.CardInstances.SingleOrDefault(value => value.Id == (entity?.CardInstanceId ?? fact.CardInstanceId))?.CurrentPrototypeId));
                    }
                }
            }
        }
        return results.ToImmutable();
    }
}
