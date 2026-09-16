using System.Collections.Immutable;
using Eota.Kernel.Determinism;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;

namespace Eota.Kernel.Resolution;

public static class MatchKernel
{
    public static FrameTransition Step(MatchState state, ReducerRegistry? registry = null) => TurnResolver.Step(state, registry);

    public static FrameTransition Step(
        MatchState state,
        IEnumerable<WorkItem> readyWorkItems,
        ReducerRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(readyWorkItems);
        if (state.Status != MatchStatus.Active)
        {
            throw new InvalidOperationException("Only an active match can step the kernel.");
        }

        var workItems = readyWorkItems.OrderBy(value => value.Id.Value).ToImmutableArray();
        if (workItems.Select(value => value.Id).Distinct().Count() != workItems.Length)
        {
            throw new ArgumentException("A frame cannot contain duplicate WorkItemId values.", nameof(readyWorkItems));
        }

        if (workItems.Any(value => value.FrameId != state.NextFrameId))
        {
            throw new ArgumentException("Every ready work item must belong to the next frame.", nameof(readyWorkItems));
        }

        var snapshot = FrameSnapshot.Create(state);
        var intents = workItems
            .SelectMany(value => value.Evaluate(snapshot))
            .ToImmutableArray();
        var transition = FrameResolver.Resolve(state, intents, registry);
        var nextWorkItemId = workItems.Length == 0
            ? state.NextWorkItemId.Value
            : Math.Max(state.NextWorkItemId.Value, checked(workItems[^1].Id.Value + 1));
        var steppedState = transition.State with
        {
            NextWorkItemId = new WorkItemId(nextWorkItemId)
        };
        return transition with
        {
            State = steppedState,
            AfterStateHash = MatchStateHasher.Compute(steppedState)
        };
    }
}

public sealed record KernelRunResult(
    MatchState State,
    ImmutableArray<FrameTransition> Frames,
    bool DiscardedRemainingFrames);

public static class KernelRunner
{
    public static KernelRunResult RunUntilStopped(
        MatchState initialState,
        IEnumerable<ImmutableArray<WorkItem>> frameWorkItems,
        ReducerRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(initialState);
        ArgumentNullException.ThrowIfNull(frameWorkItems);
        var state = initialState;
        var transitions = ImmutableArray.CreateBuilder<FrameTransition>();
        var discarded = false;

        using var enumerator = frameWorkItems.GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (state.Status != MatchStatus.Active)
            {
                discarded = true;
                break;
            }

            var transition = MatchKernel.Step(state, enumerator.Current, registry);
            transitions.Add(transition);
            state = transition.State;
        }

        return new KernelRunResult(state, transitions.ToImmutable(), discarded);
    }
}
