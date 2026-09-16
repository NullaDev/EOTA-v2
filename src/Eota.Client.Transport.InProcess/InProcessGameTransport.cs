using Eota.Client.Core;
using Eota.Server.Application;
using Eota.Transport.Contracts;

namespace Eota.Client.Transport.InProcess;

public sealed class InProcessGameTransport : IGameClientTransport
{
    private readonly MatchConnection _connection;
    public string MatchId => _connection.MatchId;

    private InProcessGameTransport(MatchConnection connection) => _connection = connection;

    public static async Task<InProcessGameTransport> ConnectAsync(MatchActor actor, Audience audience,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return new InProcessGameTransport(await actor.ConnectAsync(audience, cancellationToken).ConfigureAwait(false));
    }

    public async ValueTask SendAsync(ClientEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var json = ContractJson.Serialize(envelope);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > ContractJson.MaximumClientMessageBytes)
        { throw new InvalidDataException("client-message-too-large"); }
        await _connection.SendAsync(ContractJson.Deserialize<ClientEnvelope>(json), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ServerEnvelope> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var json = ContractJson.Serialize(await _connection.ReceiveAsync(cancellationToken).ConfigureAwait(false));
        if (System.Text.Encoding.UTF8.GetByteCount(json) > ContractJson.MaximumServerMessageBytes)
        { throw new InvalidDataException("server-message-too-large"); }
        return ContractJson.Deserialize<ServerEnvelope>(json);
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
