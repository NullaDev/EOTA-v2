using System.Threading.Channels;
using Eota.Transport.Contracts;

namespace Eota.Server.Application;

public sealed class MatchConnection : IAsyncDisposable
{
    private readonly MatchActor _actor;
    private readonly Channel<ServerEnvelope> _outgoing;
    private ulong _serverSequence;
    internal ulong ClientSequence { get; set; }
    internal bool Closed { get; private set; }
    public Audience Audience { get; }
    public string MatchId => _actor.MatchId;

    internal MatchConnection(MatchActor actor, Audience audience, int capacity)
    {
        _actor = actor;
        Audience = audience;
        _outgoing = Channel.CreateBounded<ServerEnvelope>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    public Task SendAsync(ClientEnvelope envelope, CancellationToken cancellationToken = default) =>
        _actor.SubmitAsync(this, envelope, cancellationToken);

    public ValueTask<ServerEnvelope> ReceiveAsync(CancellationToken cancellationToken = default) =>
        _outgoing.Reader.ReadAsync(cancellationToken);

    internal void Publish(ServerPayload payload, ulong revision)
    {
        if (Closed) { return; }
        var envelope = new ServerEnvelope(ContractJson.Version, MatchId, checked(++_serverSequence), revision, payload);
        if (!_outgoing.Writer.TryWrite(envelope))
        {
            _actor.ObserverBacklogged();
            // A slow observer cannot stall the authoritative match. Reconnect for a fresh snapshot.
            Close(new IOException("observer-backlog-exceeded"));
        }
    }

    internal void Close(Exception? error = null)
    {
        Closed = true;
        _outgoing.Writer.TryComplete(error);
    }

    public ValueTask DisposeAsync() => new(_actor.DisconnectAsync(this));
}
