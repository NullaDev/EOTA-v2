using System.Collections.Immutable;
using Eota.Kernel.Determinism;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;

namespace Eota.Kernel.Resolution;

public sealed record FrameSnapshot(MatchState State, Hash256 Hash)
{
    public static FrameSnapshot Create(MatchState state) => new(state, MatchStateHasher.Compute(state));
}

public abstract record WorkItem(WorkItemId Id, FrameId FrameId)
{
    public abstract ImmutableArray<AtomicIntent> Evaluate(FrameSnapshot snapshot);
}

public enum ConflictKind
{
    HeroHealth = 1,
    MinionState = 2,
    FieldState = 3,
    SlotOccupancy = 4,
    LaneResource = 5,
    CardZone = 6,
    MatchFlow = 7,
    CombatFact = 8,
    PlayerResource = 9,
    EffectDiagnostic = 10,
    HandCapacity = 11,
    LaneStatus = 12
}

public readonly record struct ConflictKey(
    ConflictKind Kind,
    ulong StableTargetId) : IComparable<ConflictKey>
{
    public int CompareTo(ConflictKey other)
    {
        var kindComparison = Kind.CompareTo(other.Kind);
        return kindComparison != 0 ? kindComparison : StableTargetId.CompareTo(other.StableTargetId);
    }

    public static bool operator <(ConflictKey left, ConflictKey right) => left.CompareTo(right) < 0;

    public static bool operator <=(ConflictKey left, ConflictKey right) => left.CompareTo(right) <= 0;

    public static bool operator >(ConflictKey left, ConflictKey right) => left.CompareTo(right) > 0;

    public static bool operator >=(ConflictKey left, ConflictKey right) => left.CompareTo(right) >= 0;
}

public abstract record AtomicIntent(IntentId Id)
{
    public abstract ConflictKey ConflictKey { get; }
}

public sealed record DamageHeroIntent(IntentId Id, PlayerId TargetPlayerId, long Amount) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(ConflictKind.HeroHealth, TargetPlayerId.Value);
}

public sealed record HealHeroIntent(IntentId Id, PlayerId TargetPlayerId, long Amount) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(ConflictKind.HeroHealth, TargetPlayerId.Value);
}

public sealed record KillHeroIntent(IntentId Id, PlayerId TargetPlayerId) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(ConflictKind.HeroHealth, TargetPlayerId.Value);
}

public sealed record ModifyHeroMaximumHealthIntent(IntentId Id, PlayerId TargetPlayerId, long Delta) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(ConflictKind.HeroHealth, TargetPlayerId.Value);
}

public sealed record ModifyMinionStatsIntent(
    IntentId Id,
    EntityId TargetEntityId,
    long AttackDelta,
    long MaximumHealthDelta) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(ConflictKind.MinionState, TargetEntityId.Value);
}

public sealed record DamageMinionIntent(IntentId Id, EntityId TargetEntityId, long Amount) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(ConflictKind.MinionState, TargetEntityId.Value);
}

public sealed record HealMinionIntent(IntentId Id, EntityId TargetEntityId, long Amount) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(ConflictKind.MinionState, TargetEntityId.Value);
}
public sealed record LoseMinionHealthIntent(IntentId Id, EntityId TargetEntityId, long Amount) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(ConflictKind.MinionState, TargetEntityId.Value);
}

public sealed record KillMinionIntent(IntentId Id, EntityId TargetEntityId) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(ConflictKind.MinionState, TargetEntityId.Value);
}

public sealed record DecayMinionSlowIntent(IntentId Id, EntityId TargetEntityId, long Amount, ulong? ExpectedGeneration = null) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(ConflictKind.MinionState, TargetEntityId.Value);
}

public sealed record ModifyFieldEnergyIntent(IntentId Id, EntityId TargetEntityId, long Delta) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(ConflictKind.FieldState, TargetEntityId.Value);
}

public sealed record DestroyFieldIntent(IntentId Id, EntityId TargetEntityId) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(ConflictKind.FieldState, TargetEntityId.Value);
}

public static class SlotConflictKey
{
    public static ConflictKey Create(PlayerId playerId, LaneId laneId, BattlefieldSlotKind slotKind)
    {
        var stableId = ((ulong)(uint)laneId.Value << 3)
                       | ((ulong)slotKind << 1)
                       | playerId.Value;
        return new ConflictKey(ConflictKind.SlotOccupancy, stableId);
    }
}

public sealed record DeployMinionIntent(
    IntentId Id,
    CardInstanceId CardInstanceId,
    PlayerId PlayerId,
    LaneId LaneId) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => SlotConflictKey.Create(PlayerId, LaneId, BattlefieldSlotKind.Minion);
}

public sealed record DeployFieldIntent(
    IntentId Id,
    CardInstanceId CardInstanceId,
    PlayerId PlayerId,
    LaneId LaneId) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => SlotConflictKey.Create(PlayerId, LaneId, BattlefieldSlotKind.Field);
}

public sealed record MoveMinionIntent(
    IntentId Id,
    EntityId EntityId,
    PlayerId PlayerId,
    LaneId FromLaneId,
    LaneId TargetLaneId,
    LaneId? AlternativeTargetLaneId = null) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => SlotConflictKey.Create(PlayerId, TargetLaneId, BattlefieldSlotKind.Minion);
}

public sealed record DecayEtherIntent(
    IntentId Id,
    PlayerId PlayerId,
    LaneId LaneId,
    long Amount) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(
        ConflictKind.LaneResource,
        ((ulong)(uint)LaneId.Value << 1) | PlayerId.Value);
}

public sealed record ConsumePlannedSpellIntent(
    IntentId Id,
    CardInstanceId CardInstanceId,
    PlayerId PlayerId,
    PlannedActionKind Kind) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(ConflictKind.CardZone, CardInstanceId.Value);
}

public sealed record AdvanceStageIntent(
    IntentId Id,
    MatchStage ExpectedStage,
    MatchStage NextStage) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(ConflictKind.MatchFlow, 0);
}

public sealed record BeginNextTurnIntent(IntentId Id) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(ConflictKind.MatchFlow, 0);
}

public sealed record DeclareCombatIntent(
    IntentId Id,
    LaneId LaneId,
    EntityId SubjectEntityId,
    EntityId? OpposingEntityId,
    PlayerId? DefendingPlayerId) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(ConflictKind.CombatFact, SubjectEntityId.Value);
}

public sealed record DeclareAttackIntent(
    IntentId Id,
    LaneId LaneId,
    EntityId AttackerEntityId,
    EntityId? TargetEntityId,
    PlayerId? TargetPlayerId) : AtomicIntent(Id)
{
    public override ConflictKey ConflictKey => new(ConflictKind.CombatFact, AttackerEntityId.Value);
}

public sealed record IntentConflictGroup(
    ConflictGroupId Id,
    ConflictKey Key,
    ImmutableArray<AtomicIntent> Intents);

public sealed record CommitPlan(
    FrameId FrameId,
    Hash256 SnapshotHash,
    ImmutableArray<IntentConflictGroup> Groups,
    GlobalRuleRngState RuleRng,
    ImmutableArray<MovementRandomChoice> MovementRandomChoices,
    ImmutableArray<Effects.NumericSetChoice> NumericSetChoices = default,
    ImmutableArray<ConflictRandomChoice> ConflictRandomChoices = default);

public sealed record ConflictRandomChoice(string Domain, ulong TargetId, ImmutableArray<IntentId> Candidates,
    IntentId SelectedIntentId, ulong SampleCountBefore, ulong SamplesConsumed);

public sealed record MovementRandomChoice(
    FrameId FrameId,
    IntentId IntentId,
    EntityId EntityId,
    ImmutableArray<LaneId> Candidates,
    LaneId SelectedLaneId,
    ulong SampleCountBefore,
    ulong SamplesConsumed);

public enum IntentReceiptStatus
{
    Applied = 1,
    PartiallyApplied = 2,
    NoOp = 3,
    Rejected = 4,
    Error = 5
}

public sealed record IntentReceipt(
    ReceiptId Id,
    IntentId IntentId,
    IntentReceiptStatus Status,
    string DetailCode,
    EntityId? EntityId,
    PlayerId? PlayerId,
    long? AppliedValue,
    CardInstanceId? CardInstanceId = null,
    ReceiptOutputs? Outputs = null);

public sealed record ReceiptOutputs(
    ImmutableArray<Eota.Kernel.Effects.EffectTarget> Affected,
    ImmutableArray<Eota.Kernel.Effects.EffectTarget> Created,
    ImmutableArray<Eota.Kernel.Effects.EffectTarget> Removed,
    ImmutableArray<CardInstanceId> RemovedFromDeck,
    ImmutableArray<CardInstanceId> EnteredHand,
    ImmutableArray<CardInstanceId> Burned,
    ImmutableArray<CardPrototypeId> NotCreated);

public enum DomainEventKind
{
    HeroHealthChanged = 1,
    EntityStatsChanged = 2,
    EntityDamaged = 3,
    EntityHealed = 4,
    FieldEnergyChanged = 5,
    EntityDied = 6,
    EntityLeft = 7,
    MatchEnded = 8,
    HeroMaximumHealthChanged = 9,
    EntityEntered = 10,
    EntityMoved = 11,
    MinionSlowChanged = 12,
    EtherActivationChanged = 13,
    EtherDecayPrevented = 14,
    SpellResolved = 15,
    MatchStageChanged = 16,
    CardDrawn = 17,
    CardBurned = 18,
    TurnStarted = 19,
    CombatDeclared = 20,
    AttackDeclared = 21,
    MinionKeywordsChanged = 22,
    PlayerResourcesChanged = 23,
    FieldKeywordsChanged = 24,
    EntityBanished = 25,
    EntityTransformed = 26,
    CardReturned = 27,
    CardGenerated = 28,
    CardModified = 29,
    EntityMechanicsChanged = 30,
    AttachedEffectsChanged = 31,
    EntityHealthLost = 32,
    LaneStatusChanged = 33
}

public sealed record DomainEvent(
    EventId Id,
    DomainEventKind Kind,
    FrameId FrameId,
    EntityId? EntityId,
    PlayerId? PlayerId,
    TombstoneId? TombstoneId,
    long? PreviousValue,
    long? CurrentValue,
    string DetailCode,
    CardInstanceId? CardInstanceId = null,
    EntityId? TargetEntityId = null,
    LaneId? LaneId = null,
    PlayerId? TargetPlayerId = null);

public sealed record DomainEventBatch(
    FrameId FrameId,
    ImmutableArray<DomainEvent> Events,
    Hash256 Hash);

public sealed record IntentReceiptBatch(
    FrameId FrameId,
    ImmutableArray<IntentReceipt> Receipts,
    Hash256 Hash);

public sealed record FrameTransition(
    MatchState State,
    CommitPlan Plan,
    IntentReceiptBatch Receipts,
    DomainEventBatch Events,
    Hash256 BeforeStateHash,
    Hash256 AfterStateHash);
