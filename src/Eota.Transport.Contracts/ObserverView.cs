using System.Text.Json.Serialization;
using System.Collections.Immutable;

namespace Eota.Transport.Contracts;

public sealed record ObserverView(
    [property: JsonRequired] Audience Audience,
    [property: JsonRequired] string ProtocolHash,
    [property: JsonRequired] string RuleContentHash,
    [property: JsonRequired] string Status,
    [property: JsonRequired] string Outcome,
    [property: JsonRequired] string Stage,
    [property: JsonRequired] int Turn,
    [property: JsonRequired] ImmutableArray<PublicPlayerView> Players,
    [property: JsonRequired] ImmutableArray<LaneView> Lanes,
    [property: JsonRequired] ImmutableArray<EntityView> Entities,
    [property: JsonRequired] PrivatePlayerView? Private);

public sealed record PublicPlayerView(
    [property: JsonRequired] int PlayerId,
    [property: JsonRequired] string Profession,
    [property: JsonRequired] long HeroHealth,
    [property: JsonRequired] long HeroMaximumHealth,
    [property: JsonRequired] long MaxCost,
    [property: JsonRequired] int HandCount,
    [property: JsonRequired] int DeckCount,
    [property: JsonRequired] int RemovedCount,
    [property: JsonRequired] bool Submitted,
    [property: JsonRequired] ImmutableArray<CardView> Discard)
{
    [JsonRequired] public ImmutableArray<ActiveEffectView> ActiveEffects { get; init; } = [];
    [JsonRequired] public long FatigueCount { get; init; }
}

public sealed record PrivatePlayerView(
    [property: JsonRequired] int PlayerId,
    [property: JsonRequired] ulong CommandRevision,
    [property: JsonRequired] long AvailableCost,
    [property: JsonRequired] ImmutableArray<CardView> Hand,
    [property: JsonRequired] ImmutableArray<PlanView> Planning,
    [property: JsonRequired] ImmutableArray<ulong> MulliganSelection,
    [property: JsonRequired] long NextTurnCost = 0)
{
    [JsonRequired] public ImmutableArray<PlanOptionView> PlanOptions { get; init; } = [];
}
public sealed record PlanOptionView([property: JsonRequired] ulong CardInstanceId, [property: JsonRequired] int? LaneId,
    [property: JsonRequired] bool Allowed, [property: JsonRequired] string Reason);

public sealed record CardView([property: JsonRequired] ulong CardInstanceId, [property: JsonRequired] string PrototypeId, [property: JsonRequired] string CardKind, [property: JsonRequired] long Cost,
    [property: JsonRequired] long? Attack = null, [property: JsonRequired] long? MaximumHealth = null,
    [property: JsonRequired] ImmutableArray<KeywordView> Keywords = default);
public sealed record PlanView([property: JsonRequired] ulong PlanCommandId, [property: JsonRequired] CardView Card, [property: JsonRequired] string ActionKind, [property: JsonRequired] int? LaneId, [property: JsonRequired] long ReservedCost);
public sealed record LaneView([property: JsonRequired] int LaneId, [property: JsonRequired] LaneSideView PlayerOne, [property: JsonRequired] LaneSideView PlayerTwo)
{
    [JsonRequired] public bool Frozen { get; init; }
    [JsonRequired] public bool Locked { get; init; }
    [JsonRequired] public ImmutableArray<LaneStatusView> Statuses { get; init; } = [];
}
public sealed record LaneStatusView([property: JsonRequired] string Kind, [property: JsonRequired] long? RemainingDuration);
public sealed record LaneSideView([property: JsonRequired] ulong? MinionEntityId, [property: JsonRequired] ulong? FieldEntityId, [property: JsonRequired] long EtherActivation, [property: JsonRequired] bool PreventNextEtherDecay);
public sealed record KeywordView([property: JsonRequired] string Kind, [property: JsonRequired] long Parameter);
public sealed record ActiveEffectView([property: JsonRequired] string EffectId, [property: JsonRequired] string Trigger,
    [property: JsonRequired] long? RemainingDuration);
public sealed record ModifierView([property: JsonRequired] string Action, [property: JsonRequired] string Attribute,
    [property: JsonRequired] string Operation, [property: JsonRequired] long Value,
    [property: JsonRequired] string? Keyword, [property: JsonRequired] long RemainingDuration);
public sealed record EntityView(
    [property: JsonRequired] ulong EntityId,
    [property: JsonRequired] CardView Card,
    [property: JsonRequired] int OwnerId,
    [property: JsonRequired] int ControllerId,
    [property: JsonRequired] int LaneId,
    [property: JsonRequired] long? Attack,
    [property: JsonRequired] long? CurrentHealth,
    [property: JsonRequired] long? MaximumHealth,
    [property: JsonRequired] long? FieldEnergy,
    [property: JsonRequired] bool PermanentField,
    [property: JsonRequired] bool PreventsActiveAttacksInLane,
    [property: JsonRequired] long SlowTurnsRemaining,
    [property: JsonRequired] ImmutableArray<KeywordView> Keywords,
    [property: JsonRequired] long StoredCharge = 0,
    [property: JsonRequired] long? ChargeRequirementOverride = null)
{
    [JsonRequired] public long? BaseAttack { get; init; }
    [JsonRequired] public long? IncomingDamageAdjustment { get; init; }
    [JsonRequired] public ImmutableArray<ActiveEffectView> ActiveEffects { get; init; } = [];
    [JsonRequired] public ImmutableArray<ModifierView> Modifiers { get; init; } = [];
}

// This is an explicit allowlist projection, never a serialized DomainEvent.
public sealed record PresentationEvent(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] ulong? EntityId,
    [property: JsonRequired] int? PlayerId,
    [property: JsonRequired] int? LaneId,
    [property: JsonRequired] ulong? TargetEntityId,
    [property: JsonRequired] int? TargetPlayerId,
    [property: JsonRequired] long? PreviousValue,
    [property: JsonRequired] long? CurrentValue,
    [property: JsonRequired] CardView? Card);
