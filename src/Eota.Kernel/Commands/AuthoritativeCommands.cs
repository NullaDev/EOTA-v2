using System.Collections.Immutable;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;

namespace Eota.Kernel.Commands;

public enum AuthoritativeCommandKind
{
    SubmitMulligan = 1,
    PlanCard = 2,
    PlanSpell = 3,
    CancelPlan = 4,
    SubmitTurn = 5,
    SystemTimeout = 6
}

public abstract record AuthoritativeCommand(
    PlayerId PlayerId,
    ulong ExpectedPlayerRevision)
{
    public abstract AuthoritativeCommandKind Kind { get; }
}

public sealed record SubmitMulliganCommand(
    PlayerId PlayerId,
    ulong ExpectedPlayerRevision,
    ImmutableArray<CardInstanceId> ReplacedCards)
    : AuthoritativeCommand(PlayerId, ExpectedPlayerRevision)
{
    public override AuthoritativeCommandKind Kind => AuthoritativeCommandKind.SubmitMulligan;
}

public sealed record PlanCardCommand(
    PlayerId PlayerId,
    ulong ExpectedPlayerRevision,
    CardInstanceId CardInstanceId,
    LaneId LaneId)
    : AuthoritativeCommand(PlayerId, ExpectedPlayerRevision)
{
    public override AuthoritativeCommandKind Kind => AuthoritativeCommandKind.PlanCard;
}

public sealed record PlanSpellCommand(
    PlayerId PlayerId,
    ulong ExpectedPlayerRevision,
    CardInstanceId CardInstanceId,
    LaneId? TargetLaneId)
    : AuthoritativeCommand(PlayerId, ExpectedPlayerRevision)
{
    public override AuthoritativeCommandKind Kind => AuthoritativeCommandKind.PlanSpell;
}

public sealed record CancelPlanCommand(
    PlayerId PlayerId,
    ulong ExpectedPlayerRevision,
    PlanCommandId PlanCommandId)
    : AuthoritativeCommand(PlayerId, ExpectedPlayerRevision)
{
    public override AuthoritativeCommandKind Kind => AuthoritativeCommandKind.CancelPlan;
}

public sealed record SubmitTurnCommand(
    PlayerId PlayerId,
    ulong ExpectedPlayerRevision)
    : AuthoritativeCommand(PlayerId, ExpectedPlayerRevision)
{
    public override AuthoritativeCommandKind Kind => AuthoritativeCommandKind.SubmitTurn;
}

// Only trusted server scheduling creates this command. The Kernel never reads a clock.
public sealed record SystemTimeoutCommand(PlayerId PlayerId, ulong ExpectedPlayerRevision, int ExpectedTurn, MatchStage ExpectedStage)
    : AuthoritativeCommand(PlayerId, ExpectedPlayerRevision)
{
    public override AuthoritativeCommandKind Kind => AuthoritativeCommandKind.SystemTimeout;
}

public enum CommandReceiptStatus
{
    Accepted = 1,
    Rejected = 2
}

public enum CommandRejectionReason
{
    None = 0,
    MatchNotActive = 1,
    PlayerRevisionMismatch = 2,
    WrongStage = 3,
    PlayerAlreadySubmitted = 4,
    CardNotInHand = 5,
    CardTypeMismatch = 6,
    InvalidLane = 7,
    PlanningSlotOccupied = 8,
    InsufficientCost = 9,
    PlanNotFound = 10,
    PlanOwnedByAnotherPlayer = 11,
    DuplicateMulliganCard = 12,
    MulliganCardNotEligible = 13,
    MulliganDeckTooSmall = 14,
    BattlefieldSlotUnavailable = 15,
    LaneLocked = 16,
    SystemContextMismatch = 17
}

public sealed record CommandReceipt(
    CommandReceiptStatus Status,
    CommandRejectionReason RejectionReason,
    CommandId? CommandId,
    AuthoritativeCommandKind CommandKind,
    PlayerId PlayerId,
    ulong PreviousMatchRevision,
    ulong ResultingMatchRevision,
    ulong PreviousPlayerRevision,
    ulong ResultingPlayerRevision,
    PlanCommandId? PlanCommandId);

public sealed record CommandTransition(MatchState State, CommandReceipt Receipt)
{
    public bool IsAccepted => Receipt.Status == CommandReceiptStatus.Accepted;
}
