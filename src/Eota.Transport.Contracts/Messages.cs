using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Eota.Transport.Contracts;

public enum Audience { PlayerOne, PlayerTwo, Spectator }

public sealed record ClientEnvelope(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] string MatchId,
    [property: JsonRequired] string ClientCommandId,
    [property: JsonRequired] ulong ClientSequence,
    [property: JsonRequired] ulong ExpectedPlayerRevision,
    [property: JsonRequired] ClientPayload Payload);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RequestSnapshotPayload), "requestSnapshot")]
[JsonDerivedType(typeof(PlanCardPayload), "planCard")]
[JsonDerivedType(typeof(PlanSpellPayload), "planSpell")]
[JsonDerivedType(typeof(CancelPlanPayload), "cancelPlan")]
[JsonDerivedType(typeof(SubmitTurnPayload), "submitTurn")]
[JsonDerivedType(typeof(SubmitMulliganPayload), "submitMulligan")]
[JsonDerivedType(typeof(RequestDiagnosticsPayload), "requestDiagnostics")]
public abstract record ClientPayload;

public sealed record ObserverResumeCursor(ulong MatchRevision, string ObserverViewHash);
public sealed record RequestSnapshotPayload(ObserverResumeCursor? Resume = null) : ClientPayload;
public sealed record PlanCardPayload(
    [property: JsonRequired] ulong CardInstanceId,
    [property: JsonRequired] int LaneId) : ClientPayload;
public sealed record PlanSpellPayload(
    [property: JsonRequired] ulong CardInstanceId,
    [property: JsonRequired] int? LaneId) : ClientPayload;
public sealed record CancelPlanPayload([property: JsonRequired] ulong PlanCommandId) : ClientPayload;
public sealed record SubmitTurnPayload : ClientPayload;
public sealed record SubmitMulliganPayload(
    [property: JsonRequired] ImmutableArray<ulong> ReplacedCards) : ClientPayload;

public sealed record ServerEnvelope(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] string MatchId,
    [property: JsonRequired] ulong ServerSequence,
    [property: JsonRequired] ulong MatchRevision,
    [property: JsonRequired] ServerPayload Payload);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ObserverSnapshotPayload), "observerSnapshot")]
[JsonDerivedType(typeof(PresentationFramePayload), "presentationFrame")]
[JsonDerivedType(typeof(CommandAckPayload), "commandAck")]
[JsonDerivedType(typeof(MatchTimerPayload), "matchTimer")]
[JsonDerivedType(typeof(ObserverResumePayload), "observerResume")]
[JsonDerivedType(typeof(ServerDiagnosticsPayload), "serverDiagnostics")]
public abstract record ServerPayload;

public sealed record ObserverResumePayload([property: JsonRequired] string RequestId,
    [property: JsonRequired] ObserverView BaseView, [property: JsonRequired] string BaseViewHash,
    [property: JsonRequired] ImmutableArray<PresentationFramePayload> Frames,
    [property: JsonRequired] ObserverView View, [property: JsonRequired] string ObserverViewHash) : ServerPayload;

public sealed record MatchTimerPayload([property: JsonRequired] int Turn, [property: JsonRequired] string Stage,
    [property: JsonRequired] long ServerNowUnixMilliseconds, [property: JsonRequired] long DeadlineUnixMilliseconds,
    [property: JsonRequired] int LimitSeconds) : ServerPayload;

public sealed record ObserverSnapshotPayload(
    [property: JsonRequired] string? RequestId,
    [property: JsonRequired] ObserverView View,
    [property: JsonRequired] string ObserverViewHash) : ServerPayload;

public sealed record PresentationFramePayload(
    [property: JsonRequired] ulong FrameId,
    [property: JsonRequired] ObserverView View,
    [property: JsonRequired] string ObserverViewHash,
    [property: JsonRequired] ImmutableArray<PresentationEvent> Events) : ServerPayload;

public sealed record CommandAckPayload(
    [property: JsonRequired] string ClientCommandId,
    [property: JsonRequired] bool Accepted,
    [property: JsonRequired] string Code,
    [property: JsonRequired] ulong PlayerRevision,
    [property: JsonRequired] ulong ResultingMatchRevision,
    [property: JsonRequired] ulong? PlanCommandId,
    [property: JsonRequired] bool ResyncRequired) : ServerPayload;
