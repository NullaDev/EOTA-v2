using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Kernel.Effects;

public sealed record SummonEffect(EffectSelector Selector, CardPrototypeId PrototypeId, BattlefieldSlotKind SlotKind) : EffectNode;

public sealed record SummonIntent(IntentId Id, PlayerId SourcePlayerId, PlayerId ControllerId, LaneId LaneId,
    CardPrototypeId PrototypeId, BattlefieldSlotKind SlotKind) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => SlotConflictKey.Create(ControllerId, LaneId, SlotKind);
}
