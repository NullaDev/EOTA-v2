using System.Collections.Immutable;

namespace Eota.Kernel.Primitives;

public enum KernelErrorCode
{
    InvalidCommand = 1,
    InvalidTarget = 2,
    InvalidContent = 3,
    VersionMismatch = 4,
    RandomProtocolMismatch = 5,
    EffectConflict = 6,
    ResolutionLimitExceeded = 7,
    ArithmeticError = 8,
    ReplayHashMismatch = 9,
    InvalidDeck = 10,
    InvalidProtocol = 11
}

public sealed record KernelError(
    KernelErrorCode Code,
    string Parameter,
    string DetailCode);

public sealed record KernelValidationResult(ImmutableArray<KernelError> Errors)
{
    public bool IsValid => Errors.IsEmpty;

    public static KernelValidationResult Success { get; } = new(ImmutableArray<KernelError>.Empty);
}
