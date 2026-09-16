using System.Collections.Immutable;
using Eota.Kernel.Content;
using Eota.Kernel.Determinism;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;
using Eota.Kernel.Rules;

namespace Eota.Kernel.Effects;

public sealed record NumericEffectIntent(IntentId Id, EffectTarget Target, PlayerId SourcePlayerId,
    NumericProperty Attribute, NumericOperation Operation, long Value) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(Target.Type switch
    {
        EffectTargetType.Minion => ConflictKind.MinionState,
        EffectTargetType.Field => ConflictKind.FieldState,
        EffectTargetType.Lane => ConflictKind.LaneResource,
        EffectTargetType.Card => ConflictKind.CardZone,
        EffectTargetType.Hero when Attribute == NumericProperty.MaximumHealth => ConflictKind.HeroHealth,
        EffectTargetType.Hero => ConflictKind.PlayerResource,
        _ => ConflictKind.EffectDiagnostic
    }, Target.Id);
}
public sealed record ChangeMinionKeywordIntent(IntentId Id, EntityId EntityId, Content.MinionKeywordKind Keyword, bool Remove, long Parameter) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(ConflictKind.MinionState, EntityId.Value);
}
public sealed record PreventEtherDecayIntent(IntentId Id, PlayerId PlayerId, LaneId LaneId) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(ConflictKind.LaneResource, ((ulong)(uint)LaneId.Value << 1) | PlayerId.Value);
}
public sealed record ClearEtherIntent(IntentId Id, PlayerId PlayerId, LaneId LaneId) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(ConflictKind.LaneResource, ((ulong)(uint)LaneId.Value << 1) | PlayerId.Value);
}
public sealed record EffectDiagnosticIntent(IntentId Id, string Code, bool IsError) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(ConflictKind.EffectDiagnostic, Id.Value);
}
public sealed record NumericSetChoice(ConflictKey ConflictKey, NumericProperty Attribute, ImmutableArray<IntentId> Candidates,
    IntentId SelectedIntentId, ulong SampleCountBefore, ulong SamplesConsumed);

public sealed record ChangeFieldKeywordIntent(IntentId Id, EntityId TargetEntityId, FieldKeywordKind Keyword, bool Remove) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(ConflictKind.FieldState, TargetEntityId.Value);
}

public static class NumericRules
{
    public static bool Supports(EffectTargetType type, NumericProperty attribute) => type switch
    {
        EffectTargetType.Minion => attribute is NumericProperty.Attack or NumericProperty.BaseAttack or NumericProperty.MaximumHealth or NumericProperty.DamageTaken or NumericProperty.ChargeRequirement or NumericProperty.StoredCharge or NumericProperty.IncomingDamageAdjustment,
        EffectTargetType.Hero => attribute is NumericProperty.MaximumHealth or NumericProperty.MaximumCost or NumericProperty.CurrentCost or NumericProperty.NextTurnCost,
        EffectTargetType.Field => attribute is NumericProperty.FieldEnergy or NumericProperty.ChargeRequirement or NumericProperty.StoredCharge,
        EffectTargetType.Lane => attribute == NumericProperty.EtherActivation,
        EffectTargetType.Card => attribute is NumericProperty.Attack or NumericProperty.MaximumHealth or NumericProperty.CardCost,
        _ => false
    };

    public static bool TryGetController(MatchState state, NumericEffectIntent intent, out PlayerId controller)
    {
        controller = default;
        if (!Supports(intent.Target.Type, intent.Attribute) || !Enum.IsDefined(intent.Operation)) { return false; }
        if (intent.Attribute is NumericProperty.ChargeRequirement or NumericProperty.BaseAttack && intent.Operation != NumericOperation.Set) { return false; }
        if (intent.Target.Type == EffectTargetType.Hero && intent.Target.Id < 2)
        { controller = new PlayerId((byte)intent.Target.Id); return true; }
        if (intent.Target.Type == EffectTargetType.Lane && intent.Target.Id / 2 < (ulong)state.Protocol.Definition.LaneCount)
        { controller = new PlayerId((byte)(intent.Target.Id & 1)); return true; }
        if (intent.Target.Type == EffectTargetType.Card)
        {
            var card = state.CardInstances.SingleOrDefault(value => value.Id.Value == intent.Target.Id && value.Zone is CardZone.Hand or CardZone.Deck);
            if (card is null || (intent.Attribute != NumericProperty.CardCost && CardInstanceRules.Definition(state, card) is not MinionCardDefinition)) { return false; }
            controller = card.OwnerId; return true;
        }
        var entity = state.Entities.SingleOrDefault(value => value.Id.Value == intent.Target.Id);
        if ((intent.Target.Type == EffectTargetType.Minion && entity is MinionEntityState)
            || (intent.Target.Type == EffectTargetType.Field && entity is FieldEntityState field && (intent.Attribute != NumericProperty.FieldEnergy || field.Lifetime is FiniteFieldLifetimeState)))
        { controller = entity.ControllerId; return true; }
        return false;
    }

    internal static ImmutableArray<NumericSetChoice> PlanSets(MatchState state, ImmutableArray<IntentConflictGroup> groups, ref GlobalRuleRngState rng)
    {
        var choices = ImmutableArray.CreateBuilder<NumericSetChoice>();
        foreach (var group in groups)
        {
            foreach (var attribute in group.Intents.OfType<NumericEffectIntent>().Concat(group.Intents.OfType<TemporaryModifierIntent>()
                         .Where(value => IsChargeOverride(value) || IsBaseAttack(value)).Select(value => new NumericEffectIntent(value.Id, value.Target, value.Modifier.SourcePlayerId,
                             value.Modifier.Attribute, NumericOperation.Set, value.Modifier.Value)))
                         .Where(value => value.Operation == NumericOperation.Set && TryGetController(state, value, out _))
                         .GroupBy(value => value.Attribute).OrderBy(value => value.Key))
            {
                var all = attribute.OrderBy(value => value.Id.Value).ToImmutableArray();
                TryGetController(state, all[0], out var controller);
                var friendly = all.Where(value => value.SourcePlayerId == controller).ToImmutableArray();
                var preferred = friendly.IsEmpty ? all : friendly;
                var sample = GlobalRuleRng.NextIndex(rng, preferred.Length);
                choices.Add(new NumericSetChoice(group.Key, attribute.Key, preferred.Select(value => value.Id).ToImmutableArray(),
                    preferred[sample.Index].Id, rng.SampleCount, sample.SamplesConsumed));
                rng = sample.State;
            }
        }
        return choices.ToImmutable();
    }

    internal static bool IsChargeOverride(TemporaryModifierIntent intent) => intent.Target.Type is EffectTargetType.Minion or EffectTargetType.Field
        && intent.Modifier.Id == intent.Id && intent.Modifier.RemainingDuration > 0 && intent.Modifier.Action == EffectAction.ModifyNumber
        && intent.Modifier.Attribute == NumericProperty.ChargeRequirement && intent.Modifier.Operation == NumericOperation.Set;
    internal static bool IsBaseAttack(TemporaryModifierIntent intent) => intent.Target.Type == EffectTargetType.Minion
        && intent.Modifier.Id == intent.Id && intent.Modifier.RemainingDuration > 0 && intent.Modifier.Action == EffectAction.ModifyNumber
        && intent.Modifier.Attribute == NumericProperty.BaseAttack && intent.Modifier.Operation == NumericOperation.Set;
}
