using Eota.Transport.Contracts;

namespace Eota.Client.Core;

public interface IGameClientTransport : IAsyncDisposable
{
    string MatchId { get; }
    ValueTask SendAsync(ClientEnvelope envelope, CancellationToken cancellationToken = default);
    ValueTask<ServerEnvelope> ReceiveAsync(CancellationToken cancellationToken = default);
}
