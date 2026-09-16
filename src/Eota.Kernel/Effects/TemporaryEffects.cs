using Eota.Kernel.Content;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Kernel.Effects;

public sealed record TemporaryModifier(IntentId Id, PlayerId SourcePlayerId, EffectAction Action, NumericProperty Attribute,
    NumericOperation Operation, long Value, MinionKeywordKind Keyword, long RemainingDuration);
public sealed record TemporaryChargeOverride(IntentId Id, long Value, long RemainingDuration);
public sealed record TemporaryBaseAttack(IntentId Id, long Value, long RemainingDuration);

public sealed record TemporaryModifierIntent(IntentId Id, EffectTarget Target, TemporaryModifier Modifier) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(Target.Type == EffectTargetType.Field ? ConflictKind.FieldState : ConflictKind.MinionState, Target.Id);
}
public sealed record DecayModifiersIntent(IntentId Id, EffectTarget Target, long Amount) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(Target.Type == EffectTargetType.Field ? ConflictKind.FieldState : ConflictKind.MinionState, Target.Id);
}
