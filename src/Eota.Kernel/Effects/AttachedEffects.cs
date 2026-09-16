using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Kernel.Effects;

public sealed record AttachedEffect(IntentId Id, EffectDefinition Definition, long? RemainingDuration, EffectSource Origin);
public sealed record GrantEffect(EffectSelector Selector, EffectDefinition Definition, IntExpression? Duration) : EffectNode;
public sealed record AttachEffectIntent(IntentId Id, EffectTarget Target, EffectDefinition Definition, long? Duration, EffectSource Origin) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(Target.Type == EffectTargetType.Hero ? ConflictKind.PlayerResource
        : Target.Type == EffectTargetType.Field ? ConflictKind.FieldState : ConflictKind.MinionState, Target.Id);
}
public sealed record DecayAttachedEffectsIntent(IntentId Id, EffectTarget Target, long Amount) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(Target.Type == EffectTargetType.Hero ? ConflictKind.PlayerResource
        : Target.Type == EffectTargetType.Field ? ConflictKind.FieldState : ConflictKind.MinionState, Target.Id);
}
