using System.Collections.Immutable;
using Eota.Kernel.Content;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Kernel.Effects;

public static partial class EffectTriggers
{
    public static ImmutableArray<EffectInvocation> Charge(MatchState state, ImmutableArray<ChargeSource> eligible, EffectTriggerKind timing)
    {
        var ready = ImmutableArray.CreateBuilder<EffectInvocation>();
        foreach (var player in state.Players.OrderBy(value => value.Id))
        {
            var roots = new List<(EffectInvocation Invocation, long Demand)>();
            foreach (var candidate in eligible.IsDefault ? [] : eligible)
            {
                var entity = state.Entities.SingleOrDefault(value => value.Id == candidate.EntityId && value.ControllerId == player.Id);
                if (entity is null) { continue; }
                var source = Source(state, entity);
                if (source.PrototypeId != candidate.PrototypeId) { continue; }
                state.Content.TryGetCard(source.PrototypeId, out var card);
                foreach (var effect in card!.Effects.Where(value => value.Trigger.Kind == timing && value.ChargeRequirement.HasValue))
                { roots.Add((new EffectInvocation(source, effect, null), Math.Max(0, entity.ChargeRequirementOverride ?? effect.ChargeRequirement!.Value))); }
            }
            var demand = roots.Aggregate(System.Numerics.BigInteger.Zero, (sum, item) => sum + item.Demand);
            var available = state.Entities.Where(value => value.ControllerId == player.Id)
                .Aggregate(new System.Numerics.BigInteger(player.CurrentCost), (sum, item) => sum + Math.Max(0, item.StoredCharge));
            if (demand <= available) { ready.AddRange(roots.Select(value => value.Invocation)); }
        }
        return ready.ToImmutable();
    }
    public static ImmutableArray<EffectInvocation> FromEvents(MatchState before, FrameTransition frame)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.State.Status != MatchStatus.Active) { return []; }
        var after = frame.State;
        var entities = before.Entities.Concat(after.Entities)
            .Concat(after.Tombstones.Where(value => value.FrameId == frame.Plan.FrameId && value.FinalEntity is not null).Select(value => value.FinalEntity!))
            .GroupBy(value => value.Id).Select(value => value.Last()).OrderBy(value => value.Id.Value).ToArray();
        var results = ImmutableArray.CreateBuilder<EffectInvocation>();
        foreach (var fact in frame.Events.Events.OrderBy(value => value.Id.Value))
        {
            var subject = entities.SingleOrDefault(value => value.Id == fact.EntityId);
            var owner = subject?.ControllerId ?? fact.PlayerId;
            var lane = subject?.LaneId ?? fact.LaneId;
            var leaving = fact.Kind is DomainEventKind.EntityDied or DomainEventKind.EntityLeft;
            var healing = fact.Kind is DomainEventKind.EntityHealed or DomainEventKind.HeroHealed;
            // Healing belongs to surviving targets and observers in the committed frame.
            // Damage/death triggers deliberately retain their existing frozen-source semantics.
            if (fact.Kind == DomainEventKind.EntityHealed && !after.Entities.Any(value => value.Id == fact.EntityId)) { continue; }
            var observers = leaving ? before.Entities : healing ? after.Entities : entities.AsEnumerable();
            foreach (var observer in observers.OrderBy(value => value.Id.Value))
            {
                var finalObserver = entities.Single(value => value.Id == observer.Id);
                var source = Source(leaving ? before : after, finalObserver);
                var deadSource = after.Tombstones.SingleOrDefault(value => value.EntityId == observer.Id);
                if (deadSource is not null) { source = source with { PrototypeId = deadSource.PrototypeId }; }
                after.Content.TryGetCard(source.PrototypeId, out var card);
                foreach (var effect in card!.Effects.Concat(AttachedDefinitions(leaving ? observer.AttachedEffects : finalObserver.AttachedEffects)))
                {
                    var matching = effect.Trigger.Kind switch
                    {
                        EffectTriggerKind.SelfEntered => fact.Kind == DomainEventKind.EntityEntered && subject?.Id == observer.Id,
                        EffectTriggerKind.FriendlyEntered => fact.Kind == DomainEventKind.EntityEntered && owner == observer.ControllerId,
                        EffectTriggerKind.EnemyEntered => fact.Kind == DomainEventKind.EntityEntered && owner == observer.ControllerId.Opponent,
                        EffectTriggerKind.FriendlySpellCast => fact.Kind == DomainEventKind.SpellResolved && owner == observer.ControllerId && lane.HasValue,
                        EffectTriggerKind.SelfDamaged => fact.Kind == DomainEventKind.EntityDamaged && subject?.Id == observer.Id && fact.CurrentValue > 0,
                        EffectTriggerKind.SelfHealed => fact.Kind == DomainEventKind.EntityHealed && subject?.Id == observer.Id && fact.CurrentValue > 0,
                        EffectTriggerKind.FriendlyHealed => fact.Kind == DomainEventKind.EntityHealed && owner == observer.ControllerId && fact.CurrentValue > 0,
                        EffectTriggerKind.FriendlyHeroHealed => fact.Kind == DomainEventKind.HeroHealed && owner == observer.ControllerId && fact.CurrentValue > 0,
                        EffectTriggerKind.Combat => fact.Kind == DomainEventKind.CombatDeclared && subject?.Id == observer.Id,
                        EffectTriggerKind.MinionCombat => fact.Kind == DomainEventKind.CombatDeclared && subject?.Id == observer.Id && fact.TargetEntityId.HasValue,
                        EffectTriggerKind.Attack => fact.Kind == DomainEventKind.AttackDeclared && subject?.Id == observer.Id,
                        EffectTriggerKind.SelfDied => fact.Kind == DomainEventKind.EntityDied && subject?.Id == observer.Id,
                        EffectTriggerKind.SelfLeft => fact.Kind == DomainEventKind.EntityLeft && subject?.Id == observer.Id,
                        EffectTriggerKind.FriendlyDied => fact.Kind == DomainEventKind.EntityDied && owner == observer.ControllerId,
                        EffectTriggerKind.EnemyDied => fact.Kind == DomainEventKind.EntityDied && owner == observer.ControllerId.Opponent,
                        EffectTriggerKind.FriendlyLeft => fact.Kind == DomainEventKind.EntityLeft && owner == observer.ControllerId,
                        EffectTriggerKind.EnemyLeft => fact.Kind == DomainEventKind.EntityLeft && owner == observer.ControllerId.Opponent,
                        EffectTriggerKind.FriendlyCombat => fact.Kind == DomainEventKind.CombatDeclared && owner == observer.ControllerId,
                        EffectTriggerKind.EnemyCombat => fact.Kind == DomainEventKind.CombatDeclared && owner == observer.ControllerId.Opponent,
                        EffectTriggerKind.EnemyAttack => fact.Kind == DomainEventKind.AttackDeclared && owner == observer.ControllerId.Opponent,
                        EffectTriggerKind.ReplacementEntered => fact.Kind == DomainEventKind.EntityEntered && subject?.Id == observer.Id && fact.TombstoneId.HasValue,
                        _ => false
                    };
                    if (matching && (fact.Kind == DomainEventKind.HeroHealed || effect.Trigger.Scope == SelectionScope.All || lane == observer.LaneId)
                        && (!effect.Trigger.OtherOnly || subject?.Id != observer.Id)
                        && SubjectMatches(effect.Trigger.SubjectType, subject, fact))
                    {
                        results.Add(new EffectInvocation(source, effect, fact, subject,
                        fact.Kind == DomainEventKind.EntityEntered ? after.Tombstones.SingleOrDefault(value => value.Id == fact.TombstoneId) : null,
                        after.Tombstones.SingleOrDefault(value => value.Id == fact.TombstoneId)?.PrototypeId is { } leftPrototype && leaving
                            ? leftPrototype : after.CardInstances.SingleOrDefault(value => value.Id == (subject?.CardInstanceId ?? fact.CardInstanceId))?.CurrentPrototypeId));
                    }
                }
            }
        }
        return results.ToImmutable().AddRange(PlayerObservers(before, frame));
    }

    public static ImmutableArray<EffectInvocation> Spells(MatchState state, IEnumerable<PlannedAction> plans)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(plans);
        var results = ImmutableArray.CreateBuilder<EffectInvocation>();
        foreach (var plan in plans.OrderBy(value => value.Id))
        {
            var cardId = state.CardInstances.Single(value => value.Id == plan.CardInstanceId).CurrentPrototypeId;
            state.Content.TryGetCard(cardId, out var card);
            var source = new EffectSource(plan.CardInstanceId, cardId, plan.Id.PlayerId, plan.LaneId, null);
            foreach (var effect in card!.Effects.Where(value => value.Trigger.Kind == EffectTriggerKind.SelfSpellCast))
            { results.Add(new EffectInvocation(source, effect, null)); }
        }
        return results.ToImmutable();
    }

    public static ImmutableArray<EffectInvocation> TurnEnd(MatchState state, bool minions, DomainEvent stageEvent)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(stageEvent);
        var results = ImmutableArray.CreateBuilder<EffectInvocation>();
        foreach (var entity in state.Entities.Where(value => minions ? value is MinionEntityState : value is FieldEntityState).OrderBy(value => value.Id.Value))
        {
            var source = Source(state, entity);
            state.Content.TryGetCard(source.PrototypeId, out var card);
            foreach (var effect in card!.Effects.Concat(AttachedDefinitions(entity.AttachedEffects)).Where(value => value.Trigger.Kind == EffectTriggerKind.TurnEnd))
            { results.Add(new EffectInvocation(source, effect, stageEvent)); }
        }
        // Player turn-end attachments share the minion end-step batch: one tick per turn,
        // both seats in the same frame, before field end effects and duration cleanup.
        if (minions)
        {
            foreach (var player in state.Players.OrderBy(value => value.Id))
            foreach (var attached in player.AttachedEffects.OrderBy(value => value.Id.Value))
            {
                var definition = AttachedDefinitions([attached]).Single();
                if (definition.Trigger.Kind == EffectTriggerKind.TurnEnd)
                {
                    results.Add(new EffectInvocation(attached.Origin with { ControllerId = player.Id, FrozenEntity = null }, definition, stageEvent));
                }
            }
        }
        return results.ToImmutable();
    }

    public static ImmutableArray<EffectInvocation> Stage(MatchState state, EffectTriggerKind timing) => state.Entities.OrderBy(value => value.Id.Value)
        .SelectMany(entity =>
        {
            var source = Source(state, entity);
            state.Content.TryGetCard(source.PrototypeId, out var card);
            return card!.Effects.Concat(AttachedDefinitions(entity.AttachedEffects)).Where(value => value.Trigger.Kind == timing)
                .Select(effect => new EffectInvocation(source, effect, null));
        }).ToImmutableArray();

    private static EffectSource Source(MatchState state, BattlefieldEntityState entity) => new(entity.CardInstanceId,
        state.CardInstances.Single(value => value.Id == entity.CardInstanceId).CurrentPrototypeId, entity.ControllerId, entity.LaneId, entity);

    private static bool SubjectMatches(EffectTargetType? type, BattlefieldEntityState? subject, DomainEvent fact) => type switch
    {
        null => true,
        EffectTargetType.Minion => subject is MinionEntityState,
        EffectTargetType.Field => subject is FieldEntityState,
        EffectTargetType.Hero => fact.Kind == DomainEventKind.HeroHealed && fact.PlayerId.HasValue,
        _ => false
    };
}
