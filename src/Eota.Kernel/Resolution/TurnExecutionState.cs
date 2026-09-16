using System.Collections.Immutable;
using Eota.Kernel.Effects;
using Eota.Kernel.Primitives;

namespace Eota.Kernel.Resolution;

public sealed record SlowApplication(EntityId EntityId, ulong Generation);
public sealed record ChargeSource(EntityId EntityId, CardPrototypeId PrototypeId);

public sealed record TurnExecutionState(
    int NextSystemStep,
    ImmutableArray<EffectProgram> Ready,
    ImmutableArray<EffectProgram> DeferredEntry,
    ImmutableArray<IntentReceipt> ReceiptLedger,
    ulong NextEffectId = 1,
    int EffectFrames = 0,
    CombatPlan? Combat = null,
    ImmutableArray<EntityId> EarlyAttackers = default,
    ImmutableArray<SlowApplication> SlowApplications = default,
    DomainEvent? EndTurnEvent = null,
    int EndTurnPhase = 0,
    ImmutableArray<ChargeSource> ChargeSources = default,
    bool PreCombatChargeCollected = false)
{
    public static TurnExecutionState Initial { get; } = new(0, [], [], []);
}

public sealed record PreparedIntentsWorkItem(WorkItemId Id, FrameId FrameId, ImmutableArray<AtomicIntent> Intents) : WorkItem(Id, FrameId)
{
    public override ImmutableArray<AtomicIntent> Evaluate(FrameSnapshot snapshot) => Intents;
}
