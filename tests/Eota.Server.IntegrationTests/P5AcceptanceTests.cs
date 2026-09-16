using System.Text.Json;
using Eota.Client.Core;
using Eota.Client.Transport.InProcess;
using Eota.Kernel.Matches;
using Eota.Server.Application;
using Eota.Server.Infrastructure;
using Eota.Transport.Contracts;

namespace Eota.Server.IntegrationTests;

public sealed class P5AcceptanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingPlanRestoresOwnCardAndCostAcrossTransport(bool remote)
    {
        await using var match = await TestMatch.StartAsync(remote);
        await using var client = new GameClient(await match.ConnectAsync(Audience.PlayerOne));
        await client.SynchronizeAsync();
        var initial = client.Store.View!.Private!;
        var planned = await client.SubmitAsync(new PlanCardPayload(1, 0), 0);
        Assert.True(planned.Accepted);
        Assert.NotNull(planned.PlanCommandId);
        var cancelled = await client.SubmitAsync(new CancelPlanPayload(planned.PlanCommandId.Value), planned.PlayerRevision);
        Assert.True(cancelled.Accepted);
        await client.SynchronizeAsync();
        Assert.Empty(client.Store.View!.Private!.Planning);
        // ImmutableArray fields compare storage identities; compare the complete wire value after transport reconstruction.
        Assert.Equal(ContractJson.Serialize(initial.Hand), ContractJson.Serialize(client.Store.View.Private.Hand));
        Assert.Equal(initial.AvailableCost, client.Store.View.Private.AvailableCost);
        Assert.Equal(2UL, client.Store.View.Private.CommandRevision);
    }

    [Fact]
    public async Task MulliganCommandsUseSameBootstrapPathAsReplay()
    {
        var request = Fixture.Load();
        request = request with
        {
            Protocol = Eota.Kernel.Protocols.CompiledGameProtocol.Compile(
            request.Protocol.Definition with { OpeningHandSize = 2, MulliganEnabled = true })
        };
        await using var actor = new MatchActor("mulligan", request);
        await using var one = new GameClient(await InProcessGameTransport.ConnectAsync(actor, Audience.PlayerOne));
        await using var two = new GameClient(await InProcessGameTransport.ConnectAsync(actor, Audience.PlayerTwo));
        await one.SynchronizeAsync();
        var card = one.Store.View!.Private!.Hand[0].CardInstanceId;
        Assert.True((await one.SubmitAsync(new SubmitMulliganPayload([card]), 0)).Accepted);
        Assert.True((await two.SubmitAsync(new SubmitMulliganPayload([]), 0)).Accepted);
        await one.SynchronizeAsync();
        Assert.Equal("Planning", one.Store.View!.Stage);
        var replay = Eota.Kernel.Resolution.MatchReplay.ReplayCommands(MatchFactory.Create(request).State!,
            [new Eota.Kernel.Commands.SubmitMulliganCommand(Eota.Kernel.Primitives.PlayerId.One, 0,
                [new Eota.Kernel.Primitives.CardInstanceId(card)]),
             new Eota.Kernel.Commands.SubmitMulliganCommand(Eota.Kernel.Primitives.PlayerId.Two, 0, [])]);
        Assert.Equal(MatchStateHasher.Compute(replay.State).ToString(), (await actor.GetDiagnosticsAsync()).StateHash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoClientsCompleteGoldenMatchAndAllAudiencesReceivePublicFrames(bool remote)
    {
        await using var match = await TestMatch.StartAsync(remote);
        await using var one = new GameClient(await match.ConnectAsync(Audience.PlayerOne));
        await using var two = new GameClient(await match.ConnectAsync(Audience.PlayerTwo));
        await using var spectator = new GameClient(await match.ConnectAsync(Audience.Spectator));
        await one.SynchronizeAsync();
        await two.SynchronizeAsync();
        await spectator.SynchronizeAsync();
        Assert.Equal(5, one.Store.View!.Private!.Hand.Length);
        Assert.Equal(5, two.Store.View!.Private!.Hand.Length);
        Assert.Null(spectator.Store.View!.Private);

        using var commands = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixture.Path, "commands.json")));
        foreach (var command in commands.RootElement.EnumerateArray())
        {
            var client = command.GetProperty("playerId").GetInt32() == 0 ? one : two;
            var payload = Fixture.ReadPayload(command);
            var ack = await client.SubmitAsync(payload, command.GetProperty("expectedPlayerRevision").GetUInt64());
            Assert.True(ack.Accepted, ack.Code);
        }

        foreach (var client in new[] { one, two, spectator })
        {
            await client.SynchronizeAsync();
            Assert.False(client.Store.NeedsSnapshot);
            Assert.Equal("Finished", client.Store.View!.Status);
            Assert.Equal("PlayerOneWon", client.Store.View.Outcome);
            Assert.Equal(73, client.Store.DrainPresentationFrames().Length);
            Assert.Equal(ObserverViewHasher.Compute(client.Store.View), client.Store.ObserverViewHash);
        }

        var diagnostic = await match.Actor.GetDiagnosticsAsync();
        using var golden = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixture.Path, "match.replay.json")));
        Assert.Equal(golden.RootElement.GetProperty("expectedFinalStateHash").GetString(), diagnostic.StateHash);
        Assert.NotEqual(diagnostic.StateHash, spectator.Store.ObserverViewHash);
        Assert.Equal(13, diagnostic.AcceptedCommandCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeatBoundCommandsAreIdempotentAndStaleRevisionsDoNotMutateState(bool remote)
    {
        await using var match = await TestMatch.StartAsync(remote);
        await using var transport = await match.ConnectAsync(Audience.PlayerOne);
        await transport.ReceiveAsync();
        var command = new ClientEnvelope(0, match.Actor.MatchId, "same-command", 1, 0, new PlanCardPayload(1, 0));
        await transport.SendAsync(command);
        var first = Assert.IsType<CommandAckPayload>((await transport.ReceiveAsync()).Payload);
        Assert.True(first.Accepted);
        await transport.ReceiveAsync(); // projected command state
        var committed = await match.Actor.GetDiagnosticsAsync();
        await transport.SendAsync(command);
        Assert.Equal(first, Assert.IsType<CommandAckPayload>((await transport.ReceiveAsync()).Payload));
        Assert.Equal(committed, await match.Actor.GetDiagnosticsAsync());

        await transport.SendAsync(command with { Payload = new PlanCardPayload(1, 1), ClientSequence = 2 });
        Assert.Equal("client-command-id-conflict",
            Assert.IsType<CommandAckPayload>((await transport.ReceiveAsync()).Payload).Code);
        await transport.SendAsync(command with { ClientCommandId = "stale", ClientSequence = 3 });
        Assert.Equal("PlayerRevisionMismatch",
            Assert.IsType<CommandAckPayload>((await transport.ReceiveAsync()).Payload).Code);
        Assert.Equal(committed, await match.Actor.GetDiagnosticsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpponentPlanningCardsAndAvailableCostStayPrivate(bool remote)
    {
        await using var match = await TestMatch.StartAsync(remote);
        await using var one = new GameClient(await match.ConnectAsync(Audience.PlayerOne));
        await using var two = new GameClient(await match.ConnectAsync(Audience.PlayerTwo));
        await using var spectator = new GameClient(await match.ConnectAsync(Audience.Spectator));
        await one.SynchronizeAsync();
        await two.SynchronizeAsync();
        await spectator.SynchronizeAsync();
        var opponentHash = two.Store.ObserverViewHash;
        var spectatorHash = spectator.Store.ObserverViewHash;
        Assert.True((await one.SubmitAsync(new PlanCardPayload(1, 0), 0)).Accepted);
        await one.SynchronizeAsync();
        await two.SynchronizeAsync();
        await spectator.SynchronizeAsync();
        Assert.Single(one.Store.View!.Private!.Planning);
        Assert.Equal(opponentHash, two.Store.ObserverViewHash);
        Assert.Equal(spectatorHash, spectator.Store.ObserverViewHash);
        Assert.Equal(5, two.Store.View!.Players[0].HandCount);
        var observerJson = ContractJson.Serialize(two.Store.View);
        foreach (var secretField in new[] { "ruleRng", "stateHash", "matchSeed", "receipt", "deckOrder" })
        {
            Assert.DoesNotContain(secretField, observerJson, StringComparison.OrdinalIgnoreCase);
        }
        Assert.All(two.Store.View.Private!.Hand, card => Assert.InRange(card.CardInstanceId, 6UL, 10UL));
        Assert.False((await spectator.SubmitAsync(new SubmitTurnPayload(), 0)).Accepted);
        Assert.False((await two.SubmitAsync(new PlanCardPayload(2, 0), 0)).Accepted);
    }
}

internal static class Fixture
{
    public static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "Eota.sln")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("Repository root missing.");
        }
    }

    public static string Path => System.IO.Path.Combine(Root, "tests", "Fixtures", "P4Match");

    public static MatchCreationRequest Load() => FileMatchLoader.Load(Path, 123456789);

    public static ClientPayload ReadPayload(JsonElement value) => value.GetProperty("kind").GetString() switch
    {
        "planCard" => new PlanCardPayload(value.GetProperty("cardInstanceId").GetUInt64(), value.GetProperty("laneId").GetInt32()),
        "planSpell" => new PlanSpellPayload(value.GetProperty("cardInstanceId").GetUInt64(),
            value.TryGetProperty("laneId", out var lane) && lane.ValueKind != JsonValueKind.Null ? lane.GetInt32() : null),
        "submitTurn" => new SubmitTurnPayload(),
        _ => throw new InvalidOperationException("Unknown fixture command.")
    };
}
