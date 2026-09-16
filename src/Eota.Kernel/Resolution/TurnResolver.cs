using System.Collections.Immutable;
using Eota.Kernel.Effects;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;

namespace Eota.Kernel.Resolution;

public sealed record TurnResolutionResult(MatchState State, ImmutableArray<FrameTransition> Frames, bool CompletedTurn);

public static class TurnResolver
{
    public static TurnResolutionResult ResolveReadyTurn(MatchState readyState, ReducerRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(readyState);
        RequireResolvable(readyState);
        var state = readyState;
        var frames = ImmutableArray.CreateBuilder<FrameTransition>();
        do
        {
            var frame = Step(state, registry);
            frames.Add(frame);
            state = frame.State;
        }
        while (state.Status == MatchStatus.Active && state.Execution is not null);
        return new TurnResolutionResult(state, frames.ToImmutable(), state.Status == MatchStatus.Active && state.Stage == MatchStage.Planning);
    }

    public static FrameTransition Step(MatchState state, ReducerRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        RequireResolvable(state);
        var execution = state.Execution ?? TurnExecutionState.Initial with { NextEffectId = state.NextEffectProgramId };
        if (execution.Ready.IsEmpty && execution.NextSystemStep == 8 && !execution.PreCombatChargeCollected)
        {
            var charge = Programs(EffectTriggers.Charge(state, execution.ChargeSources, EffectTriggerKind.PreCombatCharge));
            execution = execution with { PreCombatChargeCollected = true, Ready = charge };
        }
        // End-turn roots form two ordered collection windows; empty windows need no extra frame.
        while (execution.Ready.IsEmpty && execution.NextSystemStep == 17 && execution.EndTurnPhase < 3)
        {
            var phase = execution.EndTurnPhase;
            execution = execution with { EndTurnPhase = execution.EndTurnPhase + 1 };
            var endRoots = Programs(phase == 0 ? EffectTriggers.Charge(state, execution.ChargeSources, EffectTriggerKind.EndTurnCharge)
                : EffectTriggers.TurnEnd(state, phase == 1, execution.EndTurnEvent!));
            execution = execution with { Ready = endRoots };
        }

        PreparedEffectPrograms? prepared = null;
        var systemStep = -1;
        WorkItem work;
        if (!execution.Ready.IsEmpty)
        {
            execution = execution with { EffectFrames = checked(execution.EffectFrames + 1) };
            if (execution.EffectFrames > state.Protocol.Definition.MaxEffectTriggerFramesPerTurn)
            {
                work = new EffectFailureWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId, "effect-trigger-budget-exceeded");
            }
            else
            {
                prepared = EffectEvaluator.PreparePrograms(FrameSnapshot.Create(state), execution.Ready, state.NextIntentId);
                work = new PreparedIntentsWorkItem(state.NextWorkItemId, state.NextFrameId, prepared.Intents);
            }
            execution = execution with { Ready = [] };
        }
        else
        {
            systemStep = execution.NextSystemStep;
            execution = execution with { NextSystemStep = checked(systemStep + 1) };
            if (systemStep is 4 or 15)
            {
                var kind = systemStep == 4 ? PlannedActionKind.FastSpell : PlannedActionKind.SlowSpell;
                var snapshot = FrameSnapshot.Create(state);
                var consumes = new PlannedSpellWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId, kind, false).Evaluate(snapshot);
                var roots = Programs(EffectTriggers.Spells(state, state.Players.SelectMany(value => value.Planning).Where(value => value.Kind == kind)));
                prepared = EffectEvaluator.PreparePrograms(snapshot, roots, new IntentId(checked(state.NextIntentId.Value + (ulong)consumes.Length)));
                work = new PreparedIntentsWorkItem(state.NextWorkItemId, state.NextFrameId, consumes.AddRange(prepared.Intents));
            }
            else
            {
                if (systemStep == 9) { execution = execution with { Combat = CombatPlan.Capture(state) }; }
                if (systemStep == 11) { execution = execution with { EarlyAttackers = CombatRules.EarlyAttackers(state, execution.Combat!) }; }
                work = SystemWork(systemStep);
            }
        }

        var transition = MatchKernel.Step(state, [work], registry);
        if (transition.State.Status != MatchStatus.Active)
        {
            var stopped = transition.State with { Execution = null };
            return transition with { State = stopped, AfterStateHash = MatchStateHasher.Compute(stopped) };
        }

        var continuations = prepared is null ? ImmutableArray<EffectProgram>.Empty : EffectEvaluator.AdvancePrograms(prepared, transition);
        var triggered = Programs(EffectTriggers.FromEvents(state, transition));
        execution = systemStep == 1
            ? execution with { DeferredEntry = triggered, Ready = continuations }
            : execution with { Ready = continuations.AddRange(triggered) };
        if (systemStep == 2)
        {
            var entryRoots = Programs(EffectTriggers.Stage(transition.State, EffectTriggerKind.EntryStage));
            execution = execution with { Ready = execution.Ready.AddRange(execution.DeferredEntry).AddRange(entryRoots), DeferredEntry = [] };
        }
        if (systemStep == 1)
        {
            execution = execution with
            {
                ChargeSources = transition.State.Entities.OrderBy(value => value.Id.Value).Select(entity =>
                new ChargeSource(entity.Id, transition.State.CardInstances.Single(value => value.Id == entity.CardInstanceId).CurrentPrototypeId)).ToImmutableArray()
            };
        }
        if (systemStep == 8)
        {
            var stageRoots = Programs(EffectTriggers.Stage(transition.State, EffectTriggerKind.CombatStage));
            execution = execution with
            {
                Ready = execution.Ready.AddRange(stageRoots),
                SlowApplications = transition.State.Entities.OfType<MinionEntityState>().Where(value => value.SlowTurnsRemaining > 0)
                    .OrderBy(value => value.Id.Value).Select(value => new SlowApplication(value.Id, value.SlowGeneration)).ToImmutableArray()
            };
        }
        if (systemStep == 16) { execution = execution with { EndTurnEvent = transition.Events.Events.Single(value => value.Kind == DomainEventKind.MatchStageChanged) }; }

        var retained = execution.Ready.Concat(execution.DeferredEntry).SelectMany(value => ReferencedReceipts(value.Cursor)).ToHashSet();
        execution = execution with
        {
            ReceiptLedger = execution.ReceiptLedger.Concat(transition.Receipts.Receipts)
            .Where(value => retained.Contains(value.Id)).OrderBy(value => value.Id.Value).ToImmutableArray()
        };
        var committed = transition.State with { Execution = systemStep == 19 ? null : execution, NextEffectProgramId = execution.NextEffectId };
        return transition with { State = committed, AfterStateHash = MatchStateHasher.Compute(committed) };

        ImmutableArray<EffectProgram> Programs(ImmutableArray<EffectInvocation> invocations)
        {
            var result = ImmutableArray.CreateBuilder<EffectProgram>();
            var nextId = execution.NextEffectId;
            foreach (var invocation in invocations.OrderBy(value => value.Event?.Id.Value ?? 0)
                         .ThenBy(value => value.Source.CardId.Value).ThenBy(value => value.Effect.Id, StringComparer.Ordinal))
            {
                EffectNode body = invocation.Effect.Condition is { } condition
                    ? new IfElseEffect(condition, invocation.Effect.Body, null) : invocation.Effect.Body;
                result.Add(new EffectProgram(new EffectProgramId(checked(nextId++)), invocation, new EffectCursor(body, null, EffectResult.Empty)));
            }
            execution = execution with { NextEffectId = nextId };
            return result.ToImmutable();
        }

        WorkItem SystemWork(int step) => step switch
        {
            0 => Advance(MatchStage.ReadyToResolve, MatchStage.Deployment),
            1 => new PlannedDeploymentWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId),
            2 => Advance(MatchStage.Deployment, MatchStage.EntryEffects),
            3 => Advance(MatchStage.EntryEffects, MatchStage.FastSpells),
            5 => Advance(MatchStage.FastSpells, MatchStage.Movement),
            6 => new MovementWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId),
            7 => Advance(MatchStage.Movement, MatchStage.PreCombatCharge),
            8 => Advance(MatchStage.PreCombatCharge, MatchStage.Combat),
            9 => new CombatDeclarationWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId, execution.Combat!),
            10 => new AttackDeclarationWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId, execution.Combat!),
            11 => new FirstStrikeCombatWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId, execution.Combat!),
            12 => new NormalCombatWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId, execution.Combat!, execution.EarlyAttackers),
            13 => new SlowDecayWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId,
                execution.SlowApplications.Select(value => value.EntityId).ToImmutableArray(), 1,
                execution.SlowApplications.ToImmutableDictionary(value => value.EntityId, value => value.Generation)),
            14 => Advance(MatchStage.Combat, MatchStage.SlowSpells),
            16 => Advance(MatchStage.SlowSpells, MatchStage.EndTurnEffects),
            17 => Advance(MatchStage.EndTurnEffects, MatchStage.Cleanup),
            18 => new CleanupWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId),
            19 => new BeginNextTurnWorkItem(state.NextWorkItemId, state.NextFrameId, state.NextIntentId),
            _ => throw new InvalidOperationException("Invalid turn program counter.")
        };
        StageAdvanceWorkItem Advance(MatchStage from, MatchStage to) => new(state.NextWorkItemId, state.NextFrameId, state.NextIntentId, from, to);
    }

    private static IEnumerable<ReceiptId> ReferencedReceipts(EffectCursor cursor)
    {
        foreach (var id in cursor.Previous.Receipts) { yield return id; }
        if (cursor.Result is { } result) { foreach (var id in result.Receipts) { yield return id; } }
        if (!cursor.Children.IsDefault) { foreach (var child in cursor.Children) { foreach (var id in ReferencedReceipts(child)) { yield return id; } } }
    }

    private static void RequireResolvable(MatchState state)
    {
        if (state.Status != MatchStatus.Active || (state.Stage != MatchStage.ReadyToResolve && state.Execution is null))
        { throw new InvalidOperationException("Resolution requires ReadyToResolve or a live turn checkpoint."); }
    }
}
