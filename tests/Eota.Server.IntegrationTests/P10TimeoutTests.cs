using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using Eota.Client.Core;
using Eota.Client.Desktop;
using Eota.Client.Transport.InProcess;
using Eota.Client.Transport.WebSocket;
using Eota.Content.Compiler;
using Eota.Kernel.Commands;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;
using Eota.Server.Application;
using Eota.Server.Infrastructure;
using Eota.Transport.Contracts;

namespace Eota.Server.IntegrationTests;

public sealed class P10TimeoutTests
{
    private static readonly JsonSerializerOptions ReplayJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    [Fact]
    public void TimeoutKeepsMulliganAndAlreadyPlannedCardsAndReplaysDeterministically()
    {
        var catalog = new DesktopCatalog(Fixture.Root);
        var initial = MatchFactory.Create(Request(catalog)).State!;
        var state = initial;
        foreach (var player in initial.Players)
        {
            var timeout = MatchCommandProcessor.Accept(state, new SystemTimeoutCommand(player.Id, player.CommandRevision, state.Turn, state.Stage));
            var ordinary = MatchCommandProcessor.Accept(state, new SubmitMulliganCommand(player.Id, player.CommandRevision, []));
            Assert.True(timeout.IsAccepted);
            Assert.Equal(MatchStateHasher.Compute(ordinary.State), MatchStateHasher.Compute(timeout.State with { CommandLog = ordinary.State.CommandLog }));
            state = timeout.State;
        }
        Assert.Equal(MatchStage.Planning, state.Stage);
        var planning = MatchFactory.Create(Fixture.Load()).State!;
        var card = planning.Players[0].Hand[0];
        planning = MatchCommandProcessor.Accept(planning, new PlanCardCommand(PlayerId.One, 0, card, new LaneId(0))).State;
        var forced = MatchCommandProcessor.Accept(planning, new SystemTimeoutCommand(PlayerId.One, 1, planning.Turn, planning.Stage));
        Assert.True(forced.IsAccepted); Assert.Single(forced.State.Players[0].Planning);
        Assert.Equal(card, forced.State.Players[0].Planning[0].CardInstanceId); Assert.True(forced.State.Players[0].TurnSubmitted);
        var replay = MatchReplay.ReplayCommands(initial, state.CommandLog.Select(c => c.Command));
        Assert.True(replay.IsSuccess); Assert.Equal(MatchStateHasher.Compute(state), MatchStateHasher.Compute(replay.State));
    }

    [Theory]
    [InlineData("turn")]
    [InlineData("stage")]
    [InlineData("revision")]
    public void StaleSystemTimeoutCannotSubmitAnotherPhase(string stale)
    {
        var state = MatchFactory.Create(Fixture.Load()).State!;
        var result = MatchCommandProcessor.Accept(state, new SystemTimeoutCommand(PlayerId.One, stale == "revision" ? 99UL : 0,
            stale == "turn" ? state.Turn + 1 : state.Turn, stale == "stage" ? MatchStage.Mulligan : state.Stage));
        Assert.False(result.IsAccepted); Assert.Same(state, result.State);
        Assert.Equal(stale == "revision" ? CommandRejectionReason.PlayerRevisionMismatch : CommandRejectionReason.SystemContextMismatch, result.Receipt.RejectionReason);
    }

    [Fact]
    public async Task DeadlineSurvivesRestartAndOnlyUnsubmittedSeatIsForcedOnce()
    {
        await using var fixture = await TimedRoom.CreateAsync();
        await using (var one = new GameClient(await InProcessGameTransport.ConnectAsync(fixture.Room.Actor!, Audience.PlayerOne)))
        {
            await one.SynchronizeAsync(); Assert.True((await one.SubmitAsync(new SubmitMulliganPayload([]), 0)).Accepted);
        }
        fixture.Clock.Advance(4); await fixture.Room.TickAsync();
        Assert.Equal(1, (await fixture.Room.Actor!.GetDiagnosticsAsync()).AcceptedCommandCount);
        await fixture.RestartAsync(); fixture.Clock.Advance(1); await fixture.Room.TickAsync();
        var log = await fixture.Room.Actor!.GetCommandLogAsync(); Assert.Equal(2, log.Length);
        var timeout = Assert.IsType<SystemTimeoutCommand>(log[1].Command); Assert.Equal(PlayerId.Two, timeout.PlayerId);
        Assert.Equal(MatchStage.Planning, (await fixture.Room.Actor.GetClockStateAsync()).Stage);
        var expected = await fixture.Room.Actor.GetDiagnosticsAsync();
        await fixture.Room.TickAsync(); Assert.Equal(expected, await fixture.Room.Actor.GetDiagnosticsAsync());
        await fixture.RestartAsync(); Assert.Equal(expected, await fixture.Room.Actor!.GetDiagnosticsAsync());
        fixture.Clock.Advance(4); await fixture.Room.TickAsync(); Assert.Equal(expected, await fixture.Room.Actor.GetDiagnosticsAsync());
        fixture.Clock.Advance(1); await fixture.Room.TickAsync();
        Assert.Equal(4, (await fixture.Room.Actor.GetDiagnosticsAsync()).AcceptedCommandCount);
    }

    [Fact]
    public async Task DeadlineIsEnforcedWhenAClientCommandArrivesBeforeSchedulerTick()
    {
        await using var fixture = await TimedRoom.CreateAsync();
        await using var one = new GameClient(await InProcessGameTransport.ConnectAsync(fixture.Room.Actor!, Audience.PlayerOne));
        await one.SynchronizeAsync(); fixture.Clock.Advance(5);
        var ack = await one.SubmitAsync(new SubmitMulliganPayload([]), 0);
        Assert.False(ack.Accepted); Assert.Equal("PlayerRevisionMismatch", ack.Code);
        Assert.Equal(2, (await fixture.Room.Actor!.GetDiagnosticsAsync()).AcceptedCommandCount);
        await fixture.Room.TickAsync(); Assert.Equal(MatchStage.Planning, (await fixture.Room.Actor.GetClockStateAsync()).Stage);
    }

    [Fact]
    public async Task TimeoutCheckpointTailAndDesktopReplayRestoreSameHash()
    {
        await using var fixture = await TimedRoom.CreateAsync();
        for (var index = 0; index < 10; index++) { fixture.Clock.Advance(5); await fixture.Room.TickAsync(); }
        Assert.True(File.Exists(Path.Combine(fixture.Directory, "checkpoint.json")));
        var expected = await fixture.Room.Actor!.GetDiagnosticsAsync();
        await fixture.RestartAsync(); Assert.Equal(expected, await fixture.Room.Actor!.GetDiagnosticsAsync());
        var capture = await fixture.Room.Actor.CaptureReplayAsync();
        var one = fixture.Catalog.DefaultDeck("Guardian"); var two = fixture.Catalog.DefaultDeck("Hunter");
        var commands = capture.Commands.Select(c => Assert.IsType<SystemTimeoutCommand>(c.Command)).Select(c => new DesktopReplayCommand("systemTimeout",
            c.PlayerId.Value, c.ExpectedPlayerRevision, ExpectedTurn: c.ExpectedTurn, ExpectedStage: c.ExpectedStage.ToString()));
        var file = new DesktopReplayFile(1, fixture.Catalog.RuleHash, new LocalMatchSettings(), one, two, [.. commands], capture.StateHash);
        File.WriteAllText(Path.Combine(fixture.Directory, "replay.json"), JsonSerializer.Serialize(file, ReplayJson));
        var replay = DesktopReplay.Load(fixture.Catalog, Path.Combine(fixture.Directory, "replay.json"));
        Assert.Equal(expected.StateHash, replay.StateHash);
    }

    [Fact]
    public async Task LimitedRoomRequiresTimingConsentForReadyAndSocketAndSendsPublicCountdown()
    {
        await using var fixture = await P10Room.StartAsync(new RoomTiming(30, 60));
        using var one = fixture.Client("playerOne", 0); using var two = fixture.Client("playerTwo", 1);
        var description = await one.InspectAsync(); await two.InspectAsync();
        var admission = new RoomAdmission(1, fixture.Catalog.RuleHash, description.ProtocolHash, DesktopRoomClient.ToRoomDeck(fixture.Catalog.DefaultDeck("Guardian")),
            null!, fixture.Catalog.CardRules);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Room.AdmitAsync(Audience.PlayerOne, admission));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Room.AdmitAsync(Audience.PlayerOne, admission with { RoomSettingsHash = new string('0', 64) }));
        await one.ReadyAsync(fixture.Catalog.DefaultDeck("Guardian"), fixture.Catalog);
        await two.ReadyAsync(fixture.Catalog.DefaultDeck("Hunter"), fixture.Catalog);
        await Assert.ThrowsAsync<WebSocketException>(() => WebSocketGameTransport.ConnectAuthenticatedAsync(one.Endpoint, "secure-test", fixture.Catalog.RuleHash,
            description.ProtocolHash, fixture.Tokens[0]));
        await fixture.Room.TickAsync();
        using var spectator = fixture.Client("spectator", 2); await spectator.InspectAsync();
        await using var client = await spectator.ConnectAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (client.Timer is null) { await Task.Delay(10, timeout.Token); }
        Assert.Null(client.Store.View!.Private); Assert.InRange(client.Timer.RemainingSeconds, 28, 30);
        Assert.Equal("Mulligan", client.Timer.Payload.Stage);
    }

    [Fact]
    public void ClientCannotForgeSystemTimeoutAndCountdownUsesElapsedTime()
    {
        var normal = ContractJson.Serialize(new ClientEnvelope(0, "m", "x", 1, 0, new SubmitTurnPayload()));
        Assert.Throws<JsonException>(() => ContractJson.Deserialize<ClientEnvelope>(normal.Replace("submitTurn", "systemTimeout", StringComparison.Ordinal)));
        var reading = new MatchTimerReading(new MatchTimerPayload(1, "Planning", 1000, 31000, 30), Stopwatch.GetTimestamp() - 5 * Stopwatch.Frequency);
        Assert.InRange(reading.RemainingSeconds, 24, 25);
        Assert.Equal(0, new MatchTimerReading(reading.Payload with { DeadlineUnixMilliseconds = 0 }, Stopwatch.GetTimestamp()).RemainingSeconds);
        Assert.Throws<InvalidDataException>(() => new RoomTiming(-1, 0).ComputeHash("a", "b"));
    }

    private static MatchCreationRequest Request(DesktopCatalog catalog)
    {
        var content = CardContentCompiler.Compile(new DesktopContentEditor(catalog).Export().Cards.Select((card, index) =>
            new ContentSourceDocument(index.ToString(System.Globalization.CultureInfo.InvariantCulture), card.GetRawText()))).Content!.Rules;
        return new MatchCreationRequest(ProtocolCompiler.Compile("test", DesktopProtocol.Default.Json).Protocol!, content, 146,
            HostedRoom.ToDeck(DesktopRoomClient.ToRoomDeck(catalog.DefaultDeck("Guardian"))), HostedRoom.ToDeck(DesktopRoomClient.ToRoomDeck(catalog.DefaultDeck("Hunter"))));
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }

    private sealed class TimedRoom : IAsyncDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "eota-timeout-" + Guid.NewGuid().ToString("N"));
        public TestClock Clock { get; } = new();
        public DesktopCatalog Catalog { get; } = new(Fixture.Root);
        public HostedRoom Room { get; private set; } = null!;
        private string Configuration => Path.Combine(Directory, "room.json");
        public static async Task<TimedRoom> CreateAsync()
        {
            var result = new TimedRoom(); System.IO.Directory.CreateDirectory(result.Directory);
            var config = new HostedRoomConfiguration(1, "clock-test", "限时对局", 146, result.Catalog.RuleHash, DesktopProtocol.Default.Json,
                new DesktopContentEditor(result.Catalog).Export().Cards, new string('1', 64), new string('2', 64), new string('3', 64), Timing: new RoomTiming(5, 5));
            File.WriteAllText(result.Configuration, JsonSerializer.Serialize(config)); result.Room = new HostedRoom(result.Configuration, result.Clock);
            foreach (var (seat, profession) in new[] { (Audience.PlayerOne, "Guardian"), (Audience.PlayerTwo, "Hunter") })
            {
                var info = await result.Room.DescribeAsync(seat);
                await result.Room.AdmitAsync(seat, new RoomAdmission(1, info.RuleContentHash, info.ProtocolHash,
                    DesktopRoomClient.ToRoomDeck(result.Catalog.DefaultDeck(profession)), info.RoomSettingsHash, result.Catalog.CardRules));
            }
            await result.Room.TickAsync(); return result;
        }
        public async Task RestartAsync() { await Room.DisposeAsync(); Room = new HostedRoom(Configuration, Clock); await Room.TickAsync(); }
        public async ValueTask DisposeAsync() { await Room.DisposeAsync(); System.IO.Directory.Delete(Directory, true); }
    }
}
