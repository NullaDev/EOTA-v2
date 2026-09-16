using System.Collections.Immutable;
using Eota.Kernel.Commands;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;

namespace Eota.Kernel.Resolution;

public sealed record MatchReplayResult(
    MatchState State,
    ImmutableArray<FrameTransition> Frames,
    ImmutableArray<KernelError> Errors)
{
    public bool IsSuccess => Errors.IsEmpty;
}

public static class MatchReplay
{
    public static MatchReplayResult ReplayCommands(
        MatchState initialState,
        IEnumerable<AuthoritativeCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(initialState);
        ArgumentNullException.ThrowIfNull(commands);
        var state = initialState;
        var frames = ImmutableArray.CreateBuilder<FrameTransition>();
        var commandIndex = 0;
        foreach (var command in commands)
        {
            var transition = MatchCommandProcessor.Accept(state, command);
            if (!transition.IsAccepted)
            {
                return new MatchReplayResult(state, frames.ToImmutable(),
                    [new KernelError(KernelErrorCode.InvalidCommand, $"commands[{commandIndex}]",
                        transition.Receipt.RejectionReason.ToString())]);
            }

            state = transition.State;
            if (state.Stage == MatchStage.ReadyToResolve)
            {
                var turn = TurnResolver.ResolveReadyTurn(state);
                state = turn.State;
                frames.AddRange(turn.Frames);
                if (state.Status == MatchStatus.Failed)
                {
                    return new MatchReplayResult(state, frames.ToImmutable(),
                        [new KernelError(KernelErrorCode.EffectConflict, $"commands[{commandIndex}]", "resolution-failed")]);
                }
            }

            commandIndex++;
        }

        return new MatchReplayResult(state, frames.ToImmutable(), ImmutableArray<KernelError>.Empty);
    }
}
