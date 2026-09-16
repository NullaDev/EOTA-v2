using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Kernel.Effects;

public enum LifecycleOperation { Transform = 1, Replace = 2, Return = 3, Banish = 5 }

public sealed record LifecycleEffectIntent(IntentId Id, EffectTarget Target, PlayerId SourcePlayerId,
    LifecycleOperation Operation, CardPrototypeId? PrototypeId = null) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(Target.Type == EffectTargetType.Field ? ConflictKind.FieldState : ConflictKind.MinionState, Target.Id);
}
