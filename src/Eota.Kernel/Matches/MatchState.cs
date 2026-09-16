using System.Collections.Immutable;
using Eota.Kernel.Commands;
using Eota.Kernel.Content;
using Eota.Kernel.Determinism;
using Eota.Kernel.Primitives;
using Eota.Kernel.Protocols;
using Eota.Kernel.Rules;

namespace Eota.Kernel.Matches;

public enum MatchStatus
{
    Active = 1,
    Finished = 2,
    Failed = 3
}

public enum MatchStage
{
    Mulligan = 1,
    Planning = 2,
    ReadyToResolve = 3,
    Deployment = 4,
    EntryEffects = 5,
    FastSpells = 6,
    Movement = 7,
    PreCombatCharge = 8,
    Combat = 9,
    SlowSpells = 10,
    EndTurnEffects = 11,
    Cleanup = 12
}

public enum MatchOutcome
{
    None = 0,
    PlayerOneWon = 1,
    PlayerTwoWon = 2,
    Draw = 3,
    RuleFailure = 4
}

public enum CardZone
{
    Deck = 1,
    Hand = 2,
    Planning = 3,
    Battlefield = 4,
    Discard = 5,
    Removed = 6
}

public enum BattlefieldSlotKind
{
    Minion = 1,
    Field = 2
}

public enum PlannedActionKind
{
    Minion = 1,
    Field = 2,
    FastSpell = 3,
    SlowSpell = 4
}

public sealed record MatchManifest(
    string ProtocolId,
    int ProtocolVersion,
    Hash256 ProtocolHash,
    string ContentSchemaVersion,
    Hash256 RuleContentHash,
    ulong MatchSeed,
    DeckDefinition PlayerOneDeck,
    DeckDefinition PlayerTwoDeck);

public sealed record CardInstanceState(
    CardInstanceId Id,
    CardPrototypeId OriginalPrototypeId,
    CardPrototypeId CurrentPrototypeId,
    PlayerId OwnerId,
    CardZone Zone,
    long? Cost = null,
    long? Attack = null,
    long? MaximumHealth = null,
    ImmutableArray<MinionKeywordDefinition> Keywords = default);

public sealed record MulliganState(
    ImmutableArray<CardInstanceId> EligibleCards,
    ImmutableArray<CardInstanceId> SelectedCards,
    bool Submitted);

public sealed record PlannedAction(
    PlanCommandId Id,
    CardInstanceId CardInstanceId,
    PlannedActionKind Kind,
    LaneId? LaneId,
    long AuthoritativeCost,
    long ReservedCost);

public sealed record PlayerState(
    PlayerId Id,
    Profession Profession,
    long HeroHealth,
    long HeroMaximumHealth,
    long CurrentCost,
    long MaxCost,
    ImmutableArray<CardInstanceId> Deck,
    ImmutableArray<CardInstanceId> Hand,
    ImmutableArray<PlannedAction> Planning,
    ImmutableArray<CardInstanceId> Discard,
    ImmutableArray<CardInstanceId> Removed,
    MulliganState? Mulligan,
    bool TurnSubmitted,
    ulong CommandRevision,
    ulong NextPlanOrdinal,
    long NextTurnCost = 0)
{
    public ImmutableArray<Eota.Kernel.Effects.AttachedEffect> AttachedEffects { get; init; } = [];
    public long FatigueCount { get; init; }
}

public sealed record PlayerLaneState(
    PlayerId PlayerId,
    EntityId? MinionEntityId,
    EntityId? FieldEntityId,
    long EtherActivation,
    bool PreventNextEtherDecay);

public sealed record LaneState(
    LaneId Id,
    PlayerLaneState PlayerOne,
    PlayerLaneState PlayerTwo)
{
    public ImmutableArray<Eota.Kernel.Effects.LaneStatusState> Statuses { get; init; } = [];
}

public abstract record BattlefieldEntityState(
    EntityId Id,
    CardInstanceId CardInstanceId,
    PlayerId OwnerId,
    PlayerId ControllerId,
    LaneId LaneId)
{
    public long StoredCharge { get; init; }
    public long? ChargeRequirementOverride { get; init; }
    public long? PermanentChargeRequirementOverride { get; init; }
    public ImmutableArray<Eota.Kernel.Effects.TemporaryChargeOverride> TemporaryChargeOverrides { get; init; } = [];
    public ImmutableArray<Eota.Kernel.Effects.TemporaryModifier> TemporaryModifiers { get; init; } = [];
    public ImmutableArray<MinionKeywordDefinition> PermanentKeywords { get; init; }
    public ImmutableArray<Eota.Kernel.Effects.AttachedEffect> AttachedEffects { get; init; } = [];
}

public sealed record MinionEntityState(
    EntityId Id,
    CardInstanceId CardInstanceId,
    PlayerId OwnerId,
    PlayerId ControllerId,
    LaneId LaneId,
    long Attack,
    long CurrentHealth,
    long MaximumHealth,
    ImmutableArray<MinionKeywordDefinition> Keywords,
    long SlowTurnsRemaining,
    int EnteredOnTurn,
    bool IsDead,
    ulong SlowGeneration = 0)
    : BattlefieldEntityState(Id, CardInstanceId, OwnerId, ControllerId, LaneId)
{
    public long IncomingDamageAdjustment { get; init; }
    public long? BaseAttack { get; init; }
    public ImmutableArray<Eota.Kernel.Effects.TemporaryBaseAttack> TemporaryBaseAttacks { get; init; } = [];
}

public sealed record FieldEntityState(
    EntityId Id,
    CardInstanceId CardInstanceId,
    PlayerId OwnerId,
    PlayerId ControllerId,
    LaneId LaneId,
    IFieldLifetimeState Lifetime,
    ImmutableArray<FieldKeywordKind> Keywords,
    int EnteredOnTurn,
    bool IsDestroyed)
    : BattlefieldEntityState(Id, CardInstanceId, OwnerId, ControllerId, LaneId)
{
}

public enum EntityRemovalReason
{
    Death = 1,
    Return = 2,
    Replace = 3,
    Banish = 4
}

public sealed record EntityTombstone(
    TombstoneId Id,
    EntityId EntityId,
    CardInstanceId CardInstanceId,
    CardPrototypeId PrototypeId,
    PlayerId OwnerId,
    PlayerId ControllerId,
    LaneId LaneId,
    EntityRemovalReason Reason,
    long? Attack,
    long? CurrentHealth,
    long? MaximumHealth,
    long? FieldEnergy,
    FrameId FrameId,
    BattlefieldEntityState? FinalEntity = null);

public sealed record AcceptedCommandRecord(
    CommandId Id,
    ulong MatchRevision,
    AuthoritativeCommand Command);

public sealed record MatchState(
    MatchManifest Manifest,
    CompiledGameProtocol Protocol,
    RuleContentPack Content,
    MatchStatus Status,
    MatchOutcome Outcome,
    MatchStage Stage,
    int Turn,
    ulong Revision,
    ImmutableArray<PlayerState> Players,
    ImmutableArray<LaneState> Lanes,
    ImmutableArray<CardInstanceState> CardInstances,
    ImmutableArray<BattlefieldEntityState> Entities,
    ImmutableArray<EntityTombstone> Tombstones,
    ImmutableArray<AcceptedCommandRecord> CommandLog,
    CardInstanceId NextCardInstanceId,
    EntityId NextEntityId,
    CommandId NextCommandId,
    FrameId NextFrameId,
    WorkItemId NextWorkItemId,
    IntentId NextIntentId,
    ConflictGroupId NextConflictGroupId,
    ReceiptId NextReceiptId,
    EventId NextEventId,
    TombstoneId NextTombstoneId,
    GlobalRuleRngState RuleRng,
    Eota.Kernel.Resolution.TurnExecutionState? Execution = null,
    ulong NextEffectProgramId = 1);

public sealed record MatchCreationRequest(
    CompiledGameProtocol Protocol,
    RuleContentPack Content,
    ulong MatchSeed,
    DeckDefinition PlayerOneDeck,
    DeckDefinition PlayerTwoDeck);

public sealed record MatchCreationResult(MatchState? State, ImmutableArray<KernelError> Errors)
{
    public bool IsSuccess => State is not null && Errors.IsEmpty;
}
