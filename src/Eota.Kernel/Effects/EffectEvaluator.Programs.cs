using System.Collections.Immutable;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Kernel.Effects;

public static partial class EffectEvaluator
{
    public static PreparedEffectPrograms PreparePrograms(FrameSnapshot snapshot, ImmutableArray<EffectProgram> programs, IntentId firstIntentId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var evaluation = new Evaluation(snapshot.State, firstIntentId.Value);
        var prepared = ImmutableArray.CreateBuilder<EffectProgram>();
        try
        {
            foreach (var program in programs.OrderBy(value => value.Id.Value))
            {
                evaluation.Invocation = program.Invocation;
                prepared.Add(program with { Cursor = Run(program.Cursor, 0) });
            }
        }
        catch (EffectEvaluationException error) { evaluation.Error(error.Message); }
        catch (ArithmeticException) { evaluation.Error("expression-arithmetic-error"); }
        return new PreparedEffectPrograms(evaluation.Intents.ToImmutable(), prepared.ToImmutable());

        EffectCursor Run(EffectCursor cursor, int depth)
        {
            if (depth > 32) { throw new EffectEvaluationException("effect-depth-exceeded"); }
            if (cursor.Status == EffectCursorStatus.Complete) { return cursor; }
            evaluation.Previous = cursor.Previous;
            evaluation.LoopIndex = cursor.Node is LoopEffect ? cursor.Index : cursor.LoopIndex;
            if (cursor.Status == EffectCursorStatus.Ready)
            {
                switch (cursor.Node)
                {
                    case ParallelEffect parallel when !parallel.Children.IsEmpty:
                        cursor = cursor with { Status = EffectCursorStatus.Running, Children = parallel.Children.Select(Child).ToImmutableArray() };
                        break;
                    case SequenceEffect sequence when !sequence.Steps.IsEmpty:
                        cursor = cursor with { Status = EffectCursorStatus.Running, Children = [Child(sequence.Steps[0])] };
                        break;
                    case RetargetEffect retarget:
                        var targets = evaluation.Select(retarget.Selector, cursor.Binding);
                        if (targets.IsEmpty) { return NoOp(cursor, "empty-target"); }
                        cursor = cursor with
                        {
                            Status = EffectCursorStatus.Running,
                            Children = targets.Select(target => Child(retarget.Body) with { Binding = target }).ToImmutableArray()
                        };
                        break;
                    case IfElseEffect branch:
                        var chosen = evaluation.Condition(branch.Condition, cursor.Binding) ? branch.Then : branch.Else;
                        if (chosen is null) { return NoOp(cursor, "condition-false"); }
                        cursor = cursor with { Status = EffectCursorStatus.Running, Children = [Child(chosen)] };
                        break;
                    case LoopEffect loop:
                        var iterations = evaluation.Expression(loop.Count, cursor.Binding);
                        if (iterations > snapshot.State.Protocol.Definition.DefaultEffectLoopLimit)
                        { throw new EffectEvaluationException("effect-loop-budget-exceeded"); }
                        if (iterations <= 0 || (loop.While is not null && !evaluation.Condition(loop.While, cursor.Binding))) { return NoOp(cursor, "loop-stopped"); }
                        cursor = cursor with { Status = EffectCursorStatus.Running, Iterations = (int)iterations, Children = [Child(loop.Body) with { LoopIndex = 0 }] };
                        break;
                    case ParallelEffect:
                    case SequenceEffect:
                        return NoOp(cursor, "empty-program");
                    default:
                        var start = evaluation.Intents.Count;
                        evaluation.Node(cursor.Node, cursor.Binding, depth);
                        if (start == evaluation.Intents.Count) { return NoOp(cursor, "empty-program"); }
                        return cursor with { Status = EffectCursorStatus.AwaitingCommit, AwaitingIntents = evaluation.Intents.Skip(start).Select(value => value.Id).ToImmutableArray() };
                }
            }
            if (cursor.Node is LoopEffect loopNode && cursor.Index > 0 && cursor.Children.All(child => child.Status == EffectCursorStatus.Ready) && loopNode.While is not null
                && !evaluation.Condition(loopNode.While, cursor.Binding)) { return NoOp(cursor, "loop-stopped"); }
            return cursor with { Children = cursor.Children.Select(child => Run(child, depth + 1)).ToImmutableArray() };

            EffectCursor Child(EffectNode node) => new(node, cursor.Binding, cursor.Previous, LoopIndex: cursor.LoopIndex);
        }

        EffectCursor NoOp(EffectCursor cursor, string code)
        {
            evaluation.Add(id => new EffectDiagnosticIntent(id, code, false));
            return cursor with { Status = EffectCursorStatus.AwaitingCommit, AwaitingIntents = [evaluation.Intents[^1].Id] };
        }
    }

    public static ImmutableArray<EffectProgram> AdvancePrograms(PreparedEffectPrograms prepared, FrameTransition frame)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.State.Status != MatchStatus.Active) { return []; }
        var receipts = frame.Receipts.Receipts.ToDictionary(value => value.IntentId);
        var intents = frame.Plan.Groups.SelectMany(value => value.Intents).ToDictionary(value => value.Id);
        return prepared.Programs.Select(program =>
        {
            var source = program.Invocation.Source;
            var tombstone = frame.State.Tombstones.SingleOrDefault(value => value.EntityId == source.FrozenEntity?.Id);
            if (tombstone is not null) { source = source with { FrozenEntity = tombstone.FinalEntity, PrototypeId = tombstone.PrototypeId }; }
            EffectCursor cursor;
            try { cursor = Advance(program.Cursor); }
            catch (OverflowException)
            {
                // The committed frame remains committed; result delivery fails in the next causal frame.
                cursor = new EffectCursor(new EffectErrorNode("effect-result-overflow"), null, EffectResult.Empty);
            }
            return program with { Cursor = cursor, Invocation = program.Invocation with { Source = source } };
        }).Where(program => program.Cursor.Status != EffectCursorStatus.Complete).ToImmutableArray();

        EffectCursor Advance(EffectCursor cursor)
        {
            if (cursor.Status == EffectCursorStatus.Complete) { return cursor; }
            if (cursor.Status == EffectCursorStatus.AwaitingCommit)
            {
                var result = Merge(cursor.AwaitingIntents.Select(id => Result(intents[id], receipts[id], frame)));
                return cursor with { Status = EffectCursorStatus.Complete, AwaitingIntents = [], Result = result };
            }
            var children = cursor.Children.Select(Advance).ToImmutableArray();
            cursor = cursor with { Children = children };
            if (children.Any(child => child.Status != EffectCursorStatus.Complete)) { return cursor; }
            var merged = Merge(children.Select(child => child.Result!));
            if (cursor.Node is SequenceEffect sequence && merged.Status is not (IntentReceiptStatus.Rejected or IntentReceiptStatus.Error) && cursor.Index + 1 < sequence.Steps.Length)
            {
                return cursor with
                {
                    Index = cursor.Index + 1,
                    Previous = merged,
                    Children = [new EffectCursor(sequence.Steps[cursor.Index + 1], cursor.Binding, merged, LoopIndex: cursor.LoopIndex)]
                };
            }
            if (cursor.Node is LoopEffect loop && merged.Status is not (IntentReceiptStatus.Rejected or IntentReceiptStatus.Error) && cursor.Index + 1 < cursor.Iterations)
            {
                return cursor with
                {
                    Index = cursor.Index + 1,
                    Previous = merged,
                    Children = [new EffectCursor(loop.Body, cursor.Binding, merged, LoopIndex: cursor.Index + 1)]
                };
            }
            return cursor with { Status = EffectCursorStatus.Complete, Result = merged };
        }
    }

    private static EffectResult Result(AtomicIntent intent, IntentReceipt receipt, FrameTransition frame)
    {
        var success = receipt.Status is IntentReceiptStatus.Applied or IntentReceiptStatus.PartiallyApplied;
        if (receipt.Outputs is { } outputs)
        { return new EffectResult(receipt.Status, [receipt.Id], outputs.Affected, outputs.Created, outputs.Removed, receipt.AppliedValue ?? 0); }
        var affected = ImmutableArray.CreateBuilder<EffectTarget>();
        var created = ImmutableArray.CreateBuilder<EffectTarget>();
        var removed = ImmutableArray.CreateBuilder<EffectTarget>();
        if (success)
        {
            if (receipt.EntityId is { } entity)
            {
                var current = frame.State.Entities.SingleOrDefault(value => value.Id == entity);
                var tombstone = frame.State.Tombstones.SingleOrDefault(value => value.EntityId == entity && value.FrameId == frame.Plan.FrameId);
                var kind = (current ?? tombstone?.FinalEntity) is FieldEntityState ? EffectTargetType.Field : EffectTargetType.Minion;
                affected.Add(new EffectTarget(kind, entity.Value));
                if (tombstone is not null) { removed.Add(new EffectTarget(kind, entity.Value)); }
                if (intent is DeployMinionIntent or DeployFieldIntent or SummonIntent or LifecycleEffectIntent { Operation: LifecycleOperation.Replace })
                { created.Add(new EffectTarget(kind, entity.Value)); }
            }
            else if (intent is ClearEtherIntent clear) { affected.Add(new EffectTarget(EffectTargetType.Lane, ((ulong)(uint)clear.LaneId.Value << 1) | clear.PlayerId.Value)); }
            else if (intent is NumericEffectIntent number) { affected.Add(number.Target); }
            else if (intent is ChangeLaneStatusIntent status) { affected.Add(new EffectTarget(EffectTargetType.Lane, ((ulong)(uint)status.LaneId.Value << 1) | status.SourcePlayerId.Value)); }
            else if (receipt.PlayerId is { } player) { affected.Add(new EffectTarget(EffectTargetType.Hero, player.Value)); }
            if (receipt.CardInstanceId is { } card && intent is LifecycleEffectIntent { Operation: LifecycleOperation.Return })
            { created.Add(new EffectTarget(EffectTargetType.Card, card.Value)); }
        }
        return new EffectResult(receipt.Status, [receipt.Id], affected.ToImmutable(), created.ToImmutable(), removed.ToImmutable(), success ? receipt.AppliedValue ?? 0 : 0);
    }

    private static EffectResult Merge(IEnumerable<EffectResult> values)
    {
        var items = values.ToArray();
        var anyApplied = items.Any(value => value.Status is IntentReceiptStatus.Applied or IntentReceiptStatus.PartiallyApplied);
        var anyRejected = items.Any(value => value.Status == IntentReceiptStatus.Rejected);
        var status = items.Any(value => value.Status == IntentReceiptStatus.Error) ? IntentReceiptStatus.Error
            : anyApplied ? anyRejected || items.Any(value => value.Status == IntentReceiptStatus.PartiallyApplied) ? IntentReceiptStatus.PartiallyApplied : IntentReceiptStatus.Applied
            : anyRejected ? IntentReceiptStatus.Rejected : IntentReceiptStatus.NoOp;
        var scalar = items.Aggregate(System.Numerics.BigInteger.Zero, (sum, item) => sum + item.Scalar);
        if (scalar < long.MinValue || scalar > long.MaxValue) { throw new OverflowException("Effect result scalar overflow."); }
        return new EffectResult(status, items.SelectMany(value => value.Receipts).Distinct().OrderBy(value => value.Value).ToImmutableArray(),
            Targets(value => value.Affected), Targets(value => value.Created), Targets(value => value.Removed), (long)scalar);

        ImmutableArray<EffectTarget> Targets(Func<EffectResult, ImmutableArray<EffectTarget>> select) =>
            items.SelectMany(value => select(value)).Distinct().OrderBy(value => value.Type).ThenBy(value => value.Id).ToImmutableArray();
    }
}
