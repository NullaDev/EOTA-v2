using System.Collections.Immutable;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Kernel.Effects;

public sealed record SequenceEffect(ImmutableArray<EffectNode> Steps) : EffectNode;
public sealed record LoopEffect(IntExpression Count, EffectNode Body, EffectCondition? While = null) : EffectNode;
public sealed record EffectErrorNode(string Code) : EffectNode;

public sealed record EffectResult(
    IntentReceiptStatus Status,
    ImmutableArray<ReceiptId> Receipts,
    ImmutableArray<EffectTarget> Affected,
    ImmutableArray<EffectTarget> Created,
    ImmutableArray<EffectTarget> Removed,
    long Scalar)
{
    public static EffectResult Empty { get; } = new(IntentReceiptStatus.NoOp, [], [], [], [], 0);
}

public enum EffectCursorStatus { Ready, Running, AwaitingCommit, Complete }

// This tree stores fork/join positions explicitly; no delegates, iterators or C# call stacks are checkpoint state.
public sealed record EffectCursor(
    EffectNode Node,
    EffectTarget? Binding,
    EffectResult Previous,
    EffectCursorStatus Status = EffectCursorStatus.Ready,
    ImmutableArray<EffectCursor> Children = default,
    ImmutableArray<IntentId> AwaitingIntents = default,
    int Index = 0,
    int Iterations = 0,
    int LoopIndex = 0,
    EffectResult? Result = null);

public readonly record struct EffectProgramId(ulong Value);
public sealed record EffectProgram(EffectProgramId Id, EffectInvocation Invocation, EffectCursor Cursor);

public sealed record PreparedEffectPrograms(ImmutableArray<AtomicIntent> Intents, ImmutableArray<EffectProgram> Programs);
