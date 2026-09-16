using System.Threading.Channels;
using Eota.Server.Application;
using Eota.Transport.Contracts;

namespace Eota.Server.IntegrationTests;

public sealed class ActorBoundaryTests
{
    [Fact]
    public async Task FullMailboxWaitsAndCancelledWaitingRequestNeverExecutes()
    {
        await using var mailbox = new SerializedMailbox(1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = mailbox.InvokeAsync(async () => { entered.SetResult(); await release.Task; return 1; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = mailbox.InvokeAsync(() => Task.FromResult(2));
        using var cancellation = new CancellationTokenSource();
        var executed = false;
        var waiting = mailbox.InvokeAsync(() => { executed = true; return Task.FromResult(3); }, cancellation.Token);
        try
        {
            Assert.False(waiting.IsCompleted);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(1, await first);
        Assert.Equal(2, await second);
        Assert.False(executed);
        Assert.Equal(4, await mailbox.InvokeAsync(() => Task.FromResult(4)));
    }

    [Fact]
    public async Task SlowObserverIsDisconnectedWithoutBlockingAcceptedCommands()
    {
        await using var actor = new MatchActor("p4", Fixture.Load(), new MatchActorOptions(ObserverCapacity: 1));
        await using var slow = await actor.ConnectAsync(Audience.PlayerOne);
        await slow.SendAsync(new ClientEnvelope(0, "p4", "command", 1, 0, new PlanCardPayload(1, 0)));
        Assert.IsType<ObserverSnapshotPayload>((await slow.ReceiveAsync()).Payload);
        await Assert.ThrowsAsync<ChannelClosedException>(async () => await slow.ReceiveAsync());
        Assert.Equal(1, (await actor.GetDiagnosticsAsync()).AcceptedCommandCount);
        await using var reconnected = await actor.ConnectAsync(Audience.PlayerOne);
        Assert.Single(Assert.IsType<ObserverSnapshotPayload>((await reconnected.ReceiveAsync()).Payload).View.Private!.Planning);
    }

    [Fact]
    public async Task SimultaneousSameSeatCommandsHaveExactlyOneWriterAndOtherMatchesRemainIndependent()
    {
        await using var actor = new MatchActor("p4", Fixture.Load(), new MatchActorOptions(MailboxCapacity: 1));
        await using var other = new MatchActor("other", Fixture.Load());
        var baseline = await other.GetDiagnosticsAsync();
        await using var connection = await actor.ConnectAsync(Audience.PlayerOne);
        await connection.ReceiveAsync();
        await Task.WhenAll(Enumerable.Range(1, 20).Select(index => connection.SendAsync(
            new ClientEnvelope(0, "p4", $"command-{index}", (ulong)index, 0, new PlanCardPayload(1, 0)))));
        var acknowledgements = new List<CommandAckPayload>();
        while (acknowledgements.Count < 20)
        {
            if ((await connection.ReceiveAsync()).Payload is CommandAckPayload ack) { acknowledgements.Add(ack); }
        }

        Assert.Single(acknowledgements, value => value.Accepted);
        Assert.Equal(1, (await actor.GetDiagnosticsAsync()).AcceptedCommandCount);
        Assert.Equal(baseline, await other.GetDiagnosticsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconnectPreservesSeatScopedDeduplication(bool remote)
    {
        await using var match = await TestMatch.StartAsync(remote);
        var command = new ClientEnvelope(0, match.Actor.MatchId, "durable-key", 1, 0, new PlanCardPayload(1, 0));
        CommandAckPayload original;
        await using (var first = await match.ConnectAsync(Audience.PlayerOne))
        {
            await first.ReceiveAsync();
            await first.SendAsync(command);
            original = Assert.IsType<CommandAckPayload>((await first.ReceiveAsync()).Payload);
        }

        await using var next = await match.ConnectAsync(Audience.PlayerOne);
        var snapshot = await next.ReceiveAsync();
        Assert.Equal(1UL, snapshot.ServerSequence);
        await next.SendAsync(command with { ClientSequence = 9 });
        Assert.Equal(original, Assert.IsType<CommandAckPayload>((await next.ReceiveAsync()).Payload));
        Assert.Equal(1, (await match.Actor.GetDiagnosticsAsync()).AcceptedCommandCount);
    }
}
