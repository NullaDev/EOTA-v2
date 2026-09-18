using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Eota.Client.AI;
using Eota.Client.Core;
using Eota.Client.Desktop;
using Eota.Client.Transport.InProcess;
using Eota.Content.Compiler;
using Eota.Kernel.Content;
using Eota.Kernel.Effects;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Protocols;
using Eota.Kernel.Resolution;
using Eota.Server.Application;
using Eota.Transport.Contracts;

namespace Eota.Server.IntegrationTests;

public sealed class P95AiTests
{
    private static readonly JsonSerializerOptions ReportJson = new() { WriteIndented = true };
    private static readonly Lazy<DesktopCatalog> Catalog = new(() => new DesktopCatalog(Fixture.Root));
    private static readonly Lazy<RuleContentPack> Rules = new(() => CardContentCompiler.Compile(
        Directory.EnumerateFiles(Path.Combine(Fixture.Root, "Content", "Source", "Cards"), "*.json", SearchOption.AllDirectories)
            .Select(path => new ContentSourceDocument(Path.GetFileName(path), File.ReadAllText(path)))).Content!.Rules);

    [Theory]
    [InlineData(AiDifficulty.Easy)]
    [InlineData(AiDifficulty.Normal)]
    [InlineData(AiDifficulty.Hard)]
    public void PolicyIsDeterministicLegalBoundedAndCannotSeeChangedEnemySecrets(AiDifficulty difficulty)
    {
        var state = MatchFactory.Create(Request("Guardian", "Arcanist", 146, false)).State!;
        var enemy = state.Players[1];
        var hiddenId = enemy.Hand[0];
        var replacement = state.Content.Cards.First(card => card.Kind == CardKind.Minion).Id;
        var hidden = state with
        {
            Players = state.Players.SetItem(1, enemy with { Deck = enemy.Deck.Reverse().ToImmutableArray() }),
            CardInstances = state.CardInstances.Select(card => card.Id == hiddenId ? card with { CurrentPrototypeId = replacement } : card).ToImmutableArray()
        };
        Assert.NotEqual(MatchStateHasher.Compute(state), MatchStateHasher.Compute(hidden));
        var first = ObserverProjector.Project(state, Audience.PlayerOne);
        var second = ObserverProjector.Project(hidden, Audience.PlayerOne);
        Assert.Equal(ObserverViewHasher.Compute(first), ObserverViewHasher.Compute(second));
        var policy = new AiPolicy(difficulty, 42, Catalog.Value.AiCards);
        Assert.Equal(ContractJson.Serialize<ClientPayload>(policy.Decide(first).Command!), ContractJson.Serialize<ClientPayload>(policy.Decide(second).Command!));
        Assert.Equal(ContractJson.Serialize<ClientPayload>(policy.Decide(first).Command!), ContractJson.Serialize<ClientPayload>(new AiPolicy(difficulty, 42, Catalog.Value.AiCards).Decide(first).Command!));
        var decision = policy.Decide(first);
        Assert.InRange(decision.Evaluations, 0, AiPolicy.MaximumEvaluations);
        if (decision.Command is PlanCardPayload card)
        { Assert.Contains(first.Private!.PlanOptions, option => option.Allowed && option.CardInstanceId == card.CardInstanceId && option.LaneId == card.LaneId); }
        else if (decision.Command is PlanSpellPayload spell)
        { Assert.Contains(first.Private!.PlanOptions, option => option.Allowed && option.CardInstanceId == spell.CardInstanceId && option.LaneId == spell.LaneId); }
        Assert.Null(policy.Decide(first with { Private = null }).Command);
        Assert.Null(policy.Decide(first with { Status = "Finished" }).Command);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => policy.Decide(first, cancelled.Token));
    }

    [Fact]
    public void MulliganAndGlobalSpellsHaveDistinctLegalPolicies()
    {
        var expensive = new CardView(1, "expensive", "Minion", 7, 8, 8, []);
        var cheap = new CardView(2, "cheap", "Minion", 1, 2, 2, []);
        var view = Board([expensive, cheap], 1) with { Stage = "Mulligan" };
        Assert.Empty(Assert.IsType<SubmitMulliganPayload>(new AiPolicy(AiDifficulty.Easy, 1, []).Decide(view).Command).ReplacedCards);
        foreach (var difficulty in new[] { AiDifficulty.Normal, AiDifficulty.Hard })
        { Assert.Equal(1UL, Assert.Single(Assert.IsType<SubmitMulliganPayload>(new AiPolicy(difficulty, 1, []).Decide(view).Command).ReplacedCards)); }
        var global = new CardView(3, "global", "Spell", 1);
        view = Board([global], 1);
        view = view with { Private = view.Private! with { PlanOptions = [new(3, null, true, "")] } };
        var policy = new AiPolicy(AiDifficulty.Hard, 1, [new("global", [new("Damage", "EnemyHero", "All", 3)])]);
        Assert.Null(Assert.IsType<PlanSpellPayload>(policy.Decide(view).Command).LaneId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleRevisionResynchronizesAndRepeatedRejectionsStop(bool alwaysReject)
    {
        var transport = new RevisionTestTransport(Board([], 0), alwaysReject);
        await using var client = new GameClient(transport);
        await using var bot = new AiPlayer(client, new AiPolicy(AiDifficulty.Hard, 1, []), TimeSpan.Zero);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await bot.Completion.WaitAsync(deadline.Token);
        Assert.Equal(alwaysReject ? "Faulted" : "Finished", bot.Status.State);
        Assert.Equal(alwaysReject ? AiPlayer.MaximumConsecutiveRejections : 1, bot.Status.RejectedCommands);
        Assert.Equal(alwaysReject ? 0 : 1, bot.Status.AcceptedCommands);
        Assert.True(transport.Snapshots >= 3);
        Assert.Equal(0, transport.WrongRevisions);
    }

    [Fact]
    public async Task ObserverWaitHandlesUpdateBeforeSubscriptionAndCancellation()
    {
        var store = new ObserverStore("ai-wait");
        var previous = store.State; var waiting = store.WaitForChangeAsync(previous);
        Assert.False(waiting.IsCompleted);
        store.MarkDisconnected();
        await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(store.WaitForChangeAsync(previous).IsCompletedSuccessfully);
        using var cancellation = new CancellationTokenSource();
        var pending = store.WaitForChangeAsync(store.State, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public void HardSearchChoosesTwoEfficientPlaysOverOneGreedyPlay()
    {
        var expensive = new CardView(1, "large", "Minion", 4, 5, 4, []);
        var cheapOne = new CardView(2, "small", "Minion", 2, 3, 3, []);
        var cheapTwo = cheapOne with { CardInstanceId = 3 };
        var view = Board([expensive, cheapOne, cheapTwo], 4);
        var normal = new AiPolicy(AiDifficulty.Normal, 7, []).Decide(view);
        var hard = new AiPolicy(AiDifficulty.Hard, 7, []).Decide(view);
        Assert.Equal(1UL, Assert.IsType<PlanCardPayload>(normal.Command).CardInstanceId);
        Assert.Contains(Assert.IsType<PlanCardPayload>(hard.Command).CardInstanceId, new ulong[] { 2, 3 });
        Assert.InRange(hard.Evaluations, normal.Evaluations + 1, AiPolicy.MaximumEvaluations);
    }

    [Fact]
    public void LegalOptionsOwnPlansThreatsAndOwnerSpecificEtherGuideDecisions()
    {
        var minion = new CardView(1, "body", "Minion", 1, 3, 4, []);
        var enemy = new EntityView(100, minion with { CardInstanceId = 100 }, 1, 1, 1, 8, 3, 3, null, false, false, 0, []);
        var view = Board([minion], 1) with { Entities = [enemy] };
        var policy = new AiPolicy(AiDifficulty.Normal, 1, []);
        Assert.Equal(1, Assert.IsType<PlanCardPayload>(policy.Decide(view).Command).LaneId);
        view = view with { Private = view.Private! with { PlanOptions = view.Private.PlanOptions.Select(option => option with { Allowed = false }).ToImmutableArray() } };
        Assert.IsType<SubmitTurnPayload>(policy.Decide(view).Command);
        var spell = new CardView(2, "clear", "Spell", 0);
        var etherPolicy = new AiPolicy(AiDifficulty.Normal, 7, [new("clear", [new("ClearEther", "EnemyLanes", "Lane", 1)])]);
        view = Board([spell], 0);
        view = view with { Lanes = view.Lanes.SetItem(0, view.Lanes[0] with { PlayerOne = view.Lanes[0].PlayerOne with { EtherActivation = 4 } }) };
        Assert.IsType<SubmitTurnPayload>(etherPolicy.Decide(view).Command);
        view = view with { Lanes = view.Lanes.SetItem(1, view.Lanes[1] with { PlayerTwo = view.Lanes[1].PlayerTwo with { EtherActivation = 2 } }) };
        Assert.Equal(1, Assert.IsType<PlanSpellPayload>(etherPolicy.Decide(view).Command).LaneId);

        view = Board([minion], 1);
        var planned = new PlanView(1, minion with { CardInstanceId = 99, Attack = 20, MaximumHealth = 20 }, "Minion", 0, 1);
        view = view with { Private = view.Private! with { Planning = [planned] } };
        Assert.Equal(1, Assert.IsType<PlanCardPayload>(policy.Decide(view).Command).LaneId);
    }

    [Theory]
    [InlineData(AiDifficulty.Easy, AiDifficulty.Normal, 146UL)]
    [InlineData(AiDifficulty.Normal, AiDifficulty.Easy, 2026UL)]
    [InlineData(AiDifficulty.Hard, AiDifficulty.Normal, 146UL)]
    public async Task AllTwentyFiveProfessionPairingsCompleteThroughOrdinaryClients(AiDifficulty oneDifficulty, AiDifficulty twoDifficulty, ulong seed)
    {
        string[] professions = ["Guardian", "Arcanist", "Artisan", "Hunter", "Soulweaver"];
        var results = new List<object>();
        foreach (var one in professions)
        {
            foreach (var two in professions)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                await using var actor = new MatchActor("ai-pair", Request(one, two, seed));
                await using var first = new GameClient(await InProcessGameTransport.ConnectAsync(actor, Audience.PlayerOne));
                await using var second = new GameClient(await InProcessGameTransport.ConnectAsync(actor, Audience.PlayerTwo));
                await first.SynchronizeAsync(deadline.Token); await second.SynchronizeAsync(deadline.Token);
                await using var botOne = new AiPlayer(first, new AiPolicy(oneDifficulty, 11, Catalog.Value.AiCards), TimeSpan.Zero);
                await using var botTwo = new AiPlayer(second, new AiPolicy(twoDifficulty, 23, Catalog.Value.AiCards), TimeSpan.Zero);
                var watch = Stopwatch.StartNew();
                while (first.Store.View!.Status == "Active" && first.Store.View.Turn <= 100)
                {
                    var observed = first.Store.State;
                    Assert.NotEqual("Faulted", botOne.Status.State); Assert.NotEqual("Faulted", botTwo.Status.State);
                    if (observed.View!.Status != "Active") { break; }
                    await first.Store.WaitForChangeAsync(observed, deadline.Token);
                }
                await botOne.DisposeAsync(); await botTwo.DisposeAsync();
                var capture = await actor.CaptureReplayAsync(deadline.Token);
                var view = first.Store.View!;
                results.Add(new
                {
                    one,
                    two,
                    oneDifficulty,
                    twoDifficulty,
                    seed,
                    view.Outcome,
                    view.Status,
                    view.Turn,
                    milliseconds = watch.ElapsedMilliseconds,
                    commands = capture.Commands.Length,
                    rejected = botOne.Status.RejectedCommands + botTwo.Status.RejectedCommands,
                    evaluations = Math.Max(botOne.Status.Evaluations, botTwo.Status.Evaluations),
                    capture.StateHash
                });
                var directory = Path.Combine(Fixture.Root, "artifacts", "P95AiAcceptance"); Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, $"{oneDifficulty}-{twoDifficulty}-{seed}.json"), JsonSerializer.Serialize(results, ReportJson));
                Assert.Equal("Finished", view.Status);
                Assert.Equal(0, botOne.Status.RejectedCommands + botTwo.Status.RejectedCommands);
                Assert.InRange(capture.Commands.Length, 2, (view.Turn + 1) * AiPlayer.MaximumCommandsPerPhase * 2);
                Assert.InRange(botOne.Status.Evaluations, 0, AiPolicy.MaximumEvaluations);
            }
        }
    }

    [Theory]
    [InlineData("Easy")]
    [InlineData("Normal")]
    [InlineData("Hard")]
    public async Task HumanSeatCanPlayCancelSaveDuringThinkingRestartAndLeave(string difficulty)
    {
        var catalog = Catalog.Value;
        await using var session = await DesktopSession.LocalAsync(catalog, catalog.DefaultDeck("Guardian"), catalog.DefaultDeck("Hunter"),
            new LocalMatchSettings(146, 3, 12, true, Ai: new DesktopAiSettings(difficulty, 42)));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Assert.False(session.CanSwitchSeat); Assert.Equal(0, session.Client.Store.View!.Private!.PlayerId);
        Assert.Throws<InvalidOperationException>(session.SwitchSeat);
        var directory = Path.Combine(Fixture.Root, "artifacts", "P95Desktop", difficulty);
        await session.SaveReplayAsync(directory);
        var path = Path.Combine(directory, "replay.json");
        Assert.Equal(session.FinalStateHash, DesktopReplay.Load(catalog, path).StateHash);
        using (var file = JsonDocument.Parse(File.ReadAllText(path)))
        {
            var ai = file.RootElement.GetProperty("settings").GetProperty("ai");
            Assert.Equal(difficulty, ai.GetProperty("difficulty").GetString());
            Assert.Equal(42UL, ai.GetProperty("seed").GetUInt64());
            Assert.Equal(AiPolicy.Version, ai.GetProperty("policyVersion").GetString());
        }
        var humanPolicy = new AiPolicy(AiDifficulty.Normal, 99, catalog.AiCards);
        var cancelledPlan = false;
        while (session.Client.Store.View!.Turn < 4 && session.Client.Store.View.Status == "Active")
        {
            deadline.Token.ThrowIfCancellationRequested();
            session.Client.Store.DrainPresentationFrames();
            await session.Client.SynchronizeAsync(deadline.Token);
            var state = session.Client.Store.State;
            var action = humanPolicy.Decide(state.View!).Command;
            if (action is null) { await session.Client.Store.WaitForChangeAsync(state, deadline.Token); continue; }
            var ack = await session.SubmitAsync(action); Assert.True(ack.Accepted, ack.Code);
            await session.Client.SynchronizeAsync(deadline.Token);
            if (!cancelledPlan && ack.PlanCommandId is { } plan)
            {
                var cancel = await session.SubmitAsync(new CancelPlanPayload(plan)); Assert.True(cancel.Accepted, cancel.Code);
                cancelledPlan = true; await session.Client.SynchronizeAsync(deadline.Token);
            }
            await session.SaveReplayAsync(directory);
            Assert.Equal(session.FinalStateHash, DesktopReplay.Load(catalog, path).StateHash);
        }
        Assert.True(cancelledPlan); Assert.NotEqual("Faulted", session.AiStatus!.State);
        Assert.Equal(0, session.AiStatus.RejectedCommands);
        await using var restarted = await session.RestartAsync(catalog);
        Assert.Equal("Stopped", session.AiStatus.State);
        Assert.Equal("Mulligan", restarted.Client.Store.View!.Stage);
        Assert.Equal(session.AiSettings, restarted.AiSettings);
        Assert.Equal(0, restarted.Client.Store.View.Private!.PlayerId);
        await restarted.DisposeAsync();
        var stopped = restarted.AiStatus;
        await Task.Delay(200, deadline.Token);
        Assert.Equal(stopped, restarted.AiStatus);
    }

    [Fact]
    public void FrozenLockedLanesAndNoMulliganProtocolStayWithinAuthorityOptions()
    {
        var state = MatchFactory.Create(Request("Guardian", "Hunter", 42, false, 3)).State!;
        state = FrameResolver.Resolve(state,
        [
            new ChangeLaneStatusIntent(new IntentId(1), new LaneId(0), PlayerId.One, LaneStatusKind.Locked, false, null),
            new ChangeLaneStatusIntent(new IntentId(2), new LaneId(1), PlayerId.One, LaneStatusKind.Frozen, false, 2)
        ]).State;
        var view = ObserverProjector.Project(state, Audience.PlayerOne);
        Assert.Equal("Planning", view.Stage);
        foreach (var difficulty in Enum.GetValues<AiDifficulty>())
        {
            var action = new AiPolicy(difficulty, 42, Catalog.Value.AiCards).Decide(view).Command;
            if (action is PlanCardPayload card)
            { Assert.Contains(view.Private!.PlanOptions, option => option.Allowed && option.CardInstanceId == card.CardInstanceId && option.LaneId == card.LaneId); Assert.NotEqual(0, card.LaneId); }
            else if (action is PlanSpellPayload spell)
            { Assert.Contains(view.Private!.PlanOptions, option => option.Allowed && option.CardInstanceId == spell.CardInstanceId && option.LaneId == spell.LaneId); }
            else { Assert.IsType<SubmitTurnPayload>(action); }
        }
    }

    private static MatchCreationRequest Request(string one, string two, ulong seed, bool mulligan = true, int lanes = 6)
    {
        DeckDefinition Deck(string profession) => DeckDefinition.Create(Enum.Parse<Profession>(profession),
            Catalog.Value.DefaultDeck(profession).Cards.Select(card => new DeckEntry(CardPrototypeId.Parse(card.Id), card.Copies)));
        return new MatchCreationRequest(CompiledGameProtocol.Compile(GameProtocolDefinition.DefaultV0 with
        { MulliganEnabled = mulligan, LaneCount = lanes, MovementConflictPolicy = lanes % 2 == 0 ? MovementConflictPolicy.CenterFirst : MovementConflictPolicy.AllFail }),
            Rules.Value, seed, Deck(one), Deck(two));
    }

    private static ObserverView Board(ImmutableArray<CardView> cards, long cost) => new(Audience.PlayerOne, "protocol", "rules", "Active", "None", "Planning", 1,
        [new(0, "Guardian", 30, 30, cost, cards.Length, 30, 0, false, []), new(1, "Hunter", 30, 30, cost, 4, 30, 0, false, [])],
        [new(0, new(null, null, 0, false), new(null, null, 0, false)), new(1, new(null, null, 0, false), new(null, null, 0, false))], [],
        new PrivatePlayerView(0, 0, cost, cards, [], [])
        { PlanOptions = cards.SelectMany(card => new[] { new PlanOptionView(card.CardInstanceId, 0, true, ""), new PlanOptionView(card.CardInstanceId, 1, true, "") }).ToImmutableArray() });

    // Deliberately sends ACK without a following view. The bot must explicitly request a snapshot
    // to discover the new revision, including after an accepted terminal submit.
    private sealed class RevisionTestTransport(ObserverView initial, bool alwaysReject) : IGameClientTransport
    {
        private readonly Channel<ServerEnvelope> _messages = Channel.CreateUnbounded<ServerEnvelope>();
        private ObserverView _view = initial;
        private ulong _sequence;
        private int _attempts;
        public string MatchId => "revision-test";
        public int Snapshots { get; private set; }
        public int WrongRevisions { get; private set; }
        public async ValueTask SendAsync(ClientEnvelope envelope, CancellationToken cancellationToken = default)
        {
            ServerPayload payload;
            if (envelope.Payload is RequestSnapshotPayload)
            { Snapshots++; payload = new ObserverSnapshotPayload(envelope.ClientCommandId, _view, ObserverViewHasher.Compute(_view)); }
            else
            {
                if (envelope.ExpectedPlayerRevision != _view.Private!.CommandRevision) { WrongRevisions++; }
                var accepted = ++_attempts > 1 && !alwaysReject;
                _view = _view with { Private = _view.Private! with { CommandRevision = (ulong)_attempts }, Status = accepted ? "Finished" : "Active" };
                payload = new CommandAckPayload(envelope.ClientCommandId, accepted, accepted ? "Accepted" : "PlayerRevisionMismatch", (ulong)_attempts, 0, null, !accepted);
            }
            await _messages.Writer.WriteAsync(new ServerEnvelope(ContractJson.Version, MatchId, ++_sequence, 0, payload), cancellationToken);
        }
        public ValueTask<ServerEnvelope> ReceiveAsync(CancellationToken cancellationToken = default) => _messages.Reader.ReadAsync(cancellationToken);
        public ValueTask DisposeAsync() { _messages.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
