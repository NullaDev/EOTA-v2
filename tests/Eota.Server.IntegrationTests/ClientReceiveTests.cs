using System.Threading.Channels;
using Eota.Client.Core;
using Eota.Kernel.Matches;
using Eota.Server.Application;
using Eota.Transport.Contracts;

namespace Eota.Server.IntegrationTests;

public sealed class ClientReceiveTests
{
    [Fact]
    public async Task SilentTransportTimesOutPendingRequestsAndRequiresResync()
    {
        var view = ObserverProjector.Project(MatchFactory.Create(Fixture.Load()).State!, Audience.PlayerOne);
        var transport = new ScriptedTransport(view, _ => null);
        await using var client = new GameClient(transport, TimeSpan.FromMilliseconds(50));
        var error = await Assert.ThrowsAsync<IOException>(() => client.SubmitAsync(new SubmitTurnPayload(), 0));
        Assert.Equal("response-timeout", error.Message); Assert.True(client.Store.NeedsSnapshot);
    }
    [Fact]
    public async Task IdleClientReceivesPushAndCanSendWhileReceiverWaits()
    {
        var view = ObserverProjector.Project(MatchFactory.Create(Fixture.Load()).State!, Audience.PlayerOne);
        var transport = new ScriptedTransport(view);
        await using var client = new GameClient(transport);
        await transport.WaitingForPush.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(client.Store.View);
        Assert.False(client.Store.NeedsSnapshot);
        var ack = await client.SubmitAsync(new SubmitTurnPayload(), 0).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ack.Accepted);
        Assert.Equal(2UL, client.Store.State.ServerSequence);
    }

    private sealed class ScriptedTransport : IGameClientTransport
    {
        private readonly Channel<ServerEnvelope> _outgoing = Channel.CreateUnbounded<ServerEnvelope>();
        private int _reads;
        private readonly Func<ClientEnvelope, ServerEnvelope?>? _response;
        public string MatchId => "client-test";
        public TaskCompletionSource WaitingForPush { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RequestSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ScriptedTransport(ObserverView view, Func<ClientEnvelope, ServerEnvelope?>? response = null)
        {
            _response = response;
            _outgoing.Writer.TryWrite(new ServerEnvelope(0, MatchId, 1, 1,
                new ObserverSnapshotPayload(null, view, ObserverViewHasher.Compute(view))));
        }

        public ValueTask SendAsync(ClientEnvelope envelope, CancellationToken cancellationToken = default)
        {
            var response = _response is null ? Ack(envelope) : _response(envelope);
            if (response is not null) { _outgoing.Writer.TryWrite(response); }
            RequestSent.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask<ServerEnvelope> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            if (++_reads == 2) { WaitingForPush.SetResult(); }
            return _outgoing.Reader.ReadAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            _outgoing.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData("match")]
    [InlineData("version")]
    [InlineData("duplicate")]
    [InlineData("gap")]
    [InlineData("revision")]
    public async Task RejectedEnvelopeCannotCompleteCommandWithAcceptedAck(string invalidField)
    {
        var view = ObserverProjector.Project(MatchFactory.Create(Fixture.Load()).State!, Audience.PlayerOne);
        var transport = new ScriptedTransport(view, request => invalidField switch
        {
            "match" => Ack(request) with { MatchId = "another-match" },
            "version" => Ack(request) with { ContractVersion = ContractJson.Version + 1 },
            "duplicate" => Ack(request) with { ServerSequence = 1 },
            "gap" => Ack(request) with { ServerSequence = 3 },
            "revision" => Ack(request) with { MatchRevision = 0 },
            _ => throw new InvalidOperationException()
        });
        await using var client = new GameClient(transport);
        var error = await Assert.ThrowsAsync<IOException>(() =>
            client.SubmitAsync(new SubmitTurnPayload(), 0).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("observer-envelope-invalid", error.Message);
        Assert.True(client.Store.NeedsSnapshot);
        Assert.Equal(1UL, client.Store.ServerSequence);
    }

    [Fact]
    public async Task ValidResyncAckIsDeliveredEvenThoughStoreNeedsSnapshot()
    {
        var view = ObserverProjector.Project(MatchFactory.Create(Fixture.Load()).State!, Audience.PlayerOne);
        var transport = new ScriptedTransport(view, request => Ack(request) with
        {
            Payload = new CommandAckPayload(request.ClientCommandId, false, "StalePlayerRevision", 1, 1, null, true)
        });
        await using var client = new GameClient(transport);
        var ack = await client.SubmitAsync(new SubmitTurnPayload(), 0).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(ack.Accepted);
        Assert.True(ack.ResyncRequired);
        Assert.True(client.Store.NeedsSnapshot);
        Assert.Equal(2UL, client.Store.ServerSequence);
    }

    [Fact]
    public async Task DisconnectFailsPendingAndSubsequentRequestsWithoutCallerCancellation()
    {
        var view = ObserverProjector.Project(MatchFactory.Create(Fixture.Load()).State!, Audience.PlayerOne);
        var transport = new ScriptedTransport(view, _ => null);
        await using var client = new GameClient(transport);
        var pending = client.SubmitAsync(new SubmitTurnPayload(), 0);
        await transport.RequestSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await transport.DisposeAsync();
        await client.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<IOException>(() =>
            client.SubmitAsync(new SubmitTurnPayload(), 0).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(client.Store.NeedsSnapshot);
    }

    private static ServerEnvelope Ack(ClientEnvelope request) => new(ContractJson.Version, request.MatchId, 2, 1,
        new CommandAckPayload(request.ClientCommandId, true, "None", 1, 1, null, false));
}
