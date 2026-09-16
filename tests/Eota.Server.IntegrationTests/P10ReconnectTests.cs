using Eota.Client.Desktop;
using Eota.Transport.Contracts;

namespace Eota.Server.IntegrationTests;

public sealed class P10ReconnectTests
{
    [Fact]
    public async Task DroppedConnectionAutomaticallyRestoresSameSeatAndAcceptedPlanning()
    {
        await using var room = await P10Room.StartAsync(); using var one = room.Client("playerOne", 0); using var two = room.Client("playerTwo", 1);
        await one.InspectAsync(); await two.InspectAsync();
        await one.ReadyAsync(room.Catalog.DefaultDeck("Guardian"), room.Catalog); await two.ReadyAsync(room.Catalog.DefaultDeck("Hunter"), room.Catalog);
        await using var session = await DesktopSession.JoinRoomAsync(one);
        Assert.True((await session.SubmitAsync(new SubmitMulliganPayload([]))).Accepted);
        await session.Client.SynchronizeAsync(); var expected = session.Client.Store.ObserverViewHash;
        var previous = session.Client; await previous.DisposeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (session.ConnectionGeneration == 0) { await Task.Delay(20, timeout.Token); }
        Assert.NotSame(previous, session.Client); Assert.Equal(Audience.PlayerOne, session.Client.Store.View!.Audience);
        Assert.Equal(expected, session.Client.Store.ObserverViewHash); Assert.Equal("Connected", session.ConnectionStatus.State);
        Assert.Equal(1, (await room.Room.Actor!.GetDiagnosticsAsync()).AcceptedCommandCount);
        await session.DisposeAsync();
        var generation = session.ConnectionGeneration; await Task.Delay(60); Assert.Equal(generation, session.ConnectionGeneration);
    }

    [Fact]
    public async Task ManualReconnectPreservesSpectatorPrivacy()
    {
        await using var room = await P10Room.StartAsync(); using var one = room.Client("playerOne", 0); using var two = room.Client("playerTwo", 1); using var observer = room.Client("spectator", 2);
        await one.InspectAsync(); await two.InspectAsync();
        await one.ReadyAsync(room.Catalog.DefaultDeck("Guardian"), room.Catalog); await two.ReadyAsync(room.Catalog.DefaultDeck("Hunter"), room.Catalog);
        await observer.InspectAsync(); await using var session = await DesktopSession.JoinRoomAsync(observer);
        await session.ReconnectAsync();
        Assert.Null(session.Client.Store.View!.Private); Assert.Equal(Audience.Spectator, session.Client.Store.View.Audience);
        Assert.Throws<InvalidOperationException>(session.SwitchSeat);
    }
}
