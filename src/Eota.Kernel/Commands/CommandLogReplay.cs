using System.Collections.Immutable;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;

namespace Eota.Kernel.Commands;

public sealed record CommandLogReplayResult(
    MatchState? State,
    ImmutableArray<KernelError> Errors)
{
    public bool IsSuccess => State is not null && Errors.IsEmpty;
}

public static class CommandLogReplay
{
    public static CommandLogReplayResult Replay(
        MatchState initialState,
        IEnumerable<AcceptedCommandRecord> records)
    {
        ArgumentNullException.ThrowIfNull(initialState);
        ArgumentNullException.ThrowIfNull(records);

        var state = initialState;
        foreach (var record in records.OrderBy(value => value.Id.Value))
        {
            var transition = MatchCommandProcessor.Accept(state, record.Command);
            if (!transition.IsAccepted)
            {
                return Failure("rejected-command");
            }

            if (transition.Receipt.CommandId != record.Id)
            {
                return Failure("command-id-mismatch");
            }

            if (transition.State.Revision != record.MatchRevision)
            {
                return Failure("revision-mismatch");
            }

            state = transition.State;
        }

        return new CommandLogReplayResult(state, ImmutableArray<KernelError>.Empty);
    }

    private static CommandLogReplayResult Failure(string detailCode) => new(
        null,
        [new KernelError(KernelErrorCode.InvalidCommand, "commandLog", detailCode)]);
}
