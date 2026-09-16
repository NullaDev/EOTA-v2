using Eota.Kernel.Matches;
using Eota.Transport.Contracts;

namespace Eota.Server.Application;

public sealed record StoredSystemTimeout(int Turn, string Stage);
public sealed record StoredMatchCommand(Audience Audience, ClientEnvelope Envelope, CommandAckPayload Ack, string StateHash, StoredSystemTimeout? SystemTimeout = null);
public sealed record MatchRecovery(MatchState State, IReadOnlyList<StoredMatchCommand> Commands, string? MatchId = null);

// Infrastructure commits before an acknowledgement or observer update becomes externally visible.
public interface IMatchJournal
{
    MatchRecovery Recovery { get; }
    void Commit(StoredMatchCommand command, MatchState state);
}
