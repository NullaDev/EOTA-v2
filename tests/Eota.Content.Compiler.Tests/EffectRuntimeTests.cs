using System.Collections.Immutable;
using Eota.Kernel.Commands;
using Eota.Kernel.Content;
using Eota.Kernel.Effects;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Protocols;
using Eota.Kernel.Resolution;
using Eota.Kernel.Rules;

namespace Eota.Content.Compiler.Tests;

public sealed class EffectRuntimeTests
{
    [Fact]
    public void LosingFirstStrikeAfterEarlyDamageDoesNotProduceASecondAttack()
    {
        var attacker = Minion("FIRST", "[]", attack: 2, health: 10).Replace("\"effects\":[]", "\"keywords\":[\"firstStrike\"],\"effects\":[]", StringComparison.Ordinal);
        var defender = Minion("STRIP", """
            [{"id":"strip","trigger":{"kind":"selfDamaged"},"body":{"kind":"removeKeyword","target":{"kind":"enemyMinions"},"keyword":"firstStrike"}}]
            """, attack: 1, health: 10);
        var state = Create([attacker, defender]);
        state = Plan(state, PlayerId.One, "TEST-P6-FIRST", 0);
        state = Plan(state, PlayerId.Two, "TEST-P6-STRIP", 0);
        state = Resolve(state).State;
        var turn = Resolve(state);
        Assert.Equal(8, turn.State.Entities.OfType<MinionEntityState>().Single(entity => entity.ControllerId == PlayerId.Two).CurrentHealth);
        Assert.Equal(9, turn.State.Entities.OfType<MinionEntityState>().Single(entity => entity.ControllerId == PlayerId.One).CurrentHealth);
        Assert.Equal(2, turn.Frames.SelectMany(frame => frame.Events.Events).Count(fact => fact.Kind == DomainEventKind.EntityDamaged));
    }

    [Fact]
    public void ParallelBranchesReadTheSameStatsAndIntentBudgetFailureRollsBackTheBatch()
    {
        var card = Minion("SNAPSHOT", """
            [{"id":"entry","trigger":{"kind":"selfEntered"},"body":{"kind":"parallel","children":[
              {"kind":"modifyNumber","target":{"kind":"self"},"attribute":"attack","operation":"add","amount":10},
              {"kind":"damage","target":{"kind":"enemyHero"},"amount":"source.attack"}]}}]
            """, attack: 2);
        var turn = Resolve(Plan(Create([card]), PlayerId.One, "TEST-P6-SNAPSHOT", 0));
        Assert.Equal(28, turn.State.Players[1].HeroHealth);
        Assert.Equal(12, Assert.IsType<MinionEntityState>(Assert.Single(turn.State.Entities)).Attack);
        var failed = Resolve(Plan(Create([card], intentBudget: 1), PlayerId.One, "TEST-P6-SNAPSHOT", 0));
        Assert.Equal(MatchStatus.Failed, failed.State.Status);
        Assert.Equal(30, failed.State.Players[1].HeroHealth);
        Assert.Equal(2, Assert.IsType<MinionEntityState>(Assert.Single(failed.State.Entities)).Attack);
        Assert.Contains(failed.Frames[^1].Receipts.Receipts, receipt => receipt.DetailCode == "effect-intent-budget-exceeded");
    }

    [Fact]
    public void SimultaneousEntryDamageKillsBothSourcesAndHealerRestoresHero()
    {
        var state = Create(RepresentativeCards());
        state = state with { Players = state.Players.Select(player => player with { HeroHealth = 20 }).ToImmutableArray() };
        state = Plan(state, PlayerId.One, "TEST-P6-HUNTER", 0);
        state = Plan(state, PlayerId.Two, "TEST-P6-HUNTER", 0);
        state = Plan(state, PlayerId.One, "TEST-P6-HEALER", 1);
        var turn = Resolve(state);
        Assert.True(turn.CompletedTurn);
        Assert.Equal(23, turn.State.Players[0].HeroHealth);
        Assert.Single(turn.State.Entities);
        Assert.Equal(2, turn.State.Tombstones.Length);
        var effects = Assert.Single(turn.Frames, frame => frame.Events.Events.Any(fact => fact.Kind == DomainEventKind.EntityDamaged));
        Assert.Equal(MatchStage.EntryEffects, effects.State.Stage);
        Assert.Equal(2, effects.Events.Events.Count(fact => fact.Kind == DomainEventKind.EntityDied));
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 4)]
    public void ShieldBindsEachTargetAndChecksGuardFromTheFrameSnapshot(bool guard, int maximumHealth)
    {
        var cards = RepresentativeCards().Select(json => guard && json.Contains("TEST-P6-GOBLIN", StringComparison.Ordinal)
            ? json.Replace("\"effects\": []", "\"keywords\":[\"guard\"],\"effects\":[]", StringComparison.Ordinal) : json);
        var state = Plan(Create(cards), PlayerId.One, "TEST-P6-GOBLIN", 0);
        state = Resolve(state).State;
        state = Plan(state, PlayerId.One, "TEST-P6-SHIELD", 0);
        var turn = Resolve(state);
        var minion = Assert.IsType<MinionEntityState>(Assert.Single(turn.State.Entities));
        Assert.Equal(maximumHealth, minion.MaximumHealth);
        Assert.Equal(maximumHealth, minion.CurrentHealth);
        Assert.DoesNotContain(turn.Frames.SelectMany(frame => frame.Events.Events), fact => fact.Kind == DomainEventKind.EntityHealed);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    public void ArcaneSparkBranchesOnSourceLaneEther(int ether, int damage)
    {
        var state = Plan(Create(RepresentativeCards()), PlayerId.Two, "TEST-P6-HEALER", 0);
        state = Resolve(state).State;
        state = state with { Lanes = state.Lanes.SetItem(0, state.Lanes[0] with { PlayerOne = state.Lanes[0].PlayerOne with { EtherActivation = ether } }) };
        state = Plan(state, PlayerId.One, "TEST-P6-SPARK", 0);
        var turn = Resolve(state);
        var damaged = Assert.Single(turn.Frames.SelectMany(frame => frame.Events.Events), fact => fact.Kind == DomainEventKind.EntityDamaged);
        Assert.Equal(damage, damaged.CurrentValue);
        Assert.Equal(3 - damage, Assert.IsType<MinionEntityState>(Assert.Single(turn.State.Entities)).CurrentHealth);
    }

    [Fact]
    public void FriendlySpellTriggersAfterRootDamageAndUsesTheNewSnapshot()
    {
        var watcher = Minion("WATCHER", """
            [{"id":"watch","trigger":{"kind":"friendlySpellCast"},"body":
              {"kind":"damage","target":{"kind":"enemyMinions"},"amount":1}}]
            """);
        var state = Create(RepresentativeCards().Append(watcher));
        state = Plan(state, PlayerId.One, "TEST-P6-WATCHER", 0);
        state = Plan(state, PlayerId.Two, "TEST-P6-GOBLIN", 0);
        state = Resolve(state).State;
        state = Plan(state, PlayerId.One, "TEST-P6-SHOT", 0);
        var turn = Resolve(state);
        var root = Assert.Single(turn.Frames, frame => frame.Events.Events.Any(fact => fact.Kind == DomainEventKind.SpellResolved));
        Assert.Contains(root.Events.Events, fact => fact.Kind == DomainEventKind.EntityDamaged && fact.CurrentValue == 1);
        var death = Assert.Single(turn.Frames, frame => frame.Events.Events.Any(fact => fact.Kind == DomainEventKind.EntityDied));
        Assert.Equal(root.Plan.FrameId.Value + 1, death.Plan.FrameId.Value);
        Assert.Equal(MatchStage.FastSpells, death.State.Stage);
    }

    [Fact]
    public void FatalSelfDamageKeepsFrozenSourceAndNeverSelectsTheRemovedEntity()
    {
        var avenger = Minion("AVENGER", """
            [{"id":"revenge","trigger":{"kind":"selfDamaged"},"body":{"kind":"parallel","children":[
              {"kind":"damage","target":{"kind":"enemyHero"},"amount":"source.attack + event.amount"},
              {"kind":"heal","target":{"kind":"self"},"amount":10}]}}]
            """, attack: 3, health: 1);
        var state = Create(RepresentativeCards().Append(avenger));
        state = Plan(state, PlayerId.Two, "TEST-P6-AVENGER", 0);
        state = Resolve(state).State;
        state = Plan(state, PlayerId.One, "TEST-P6-SHOT", 0);
        var turn = Resolve(state);
        Assert.True(turn.CompletedTurn);
        Assert.Equal(26, turn.State.Players[0].HeroHealth);
        Assert.Empty(turn.State.Entities);
        Assert.Contains(turn.Frames.SelectMany(frame => frame.Receipts.Receipts), receipt => receipt.DetailCode == "empty-target");
        Assert.NotNull(Assert.Single(turn.State.Tombstones).FinalEntity);
    }

    [Fact]
    public void EntryObserversRespectLaneAndAllScopes()
    {
        var lane = Minion("LANE", """
            [{"id":"watch","trigger":{"kind":"enemyEntered"},"body":{"kind":"modifyNumber","target":{"kind":"self"},"attribute":"attack","operation":"add","amount":1}}]
            """);
        var all = lane.Replace("TEST-P6-LANE", "TEST-P6-ALL", StringComparison.Ordinal)
            .Replace("\"kind\":\"enemyEntered\"", "\"kind\":\"enemyEntered\",\"scope\":\"all\"", StringComparison.Ordinal);
        var state = Create(RepresentativeCards().Concat([lane, all]));
        state = Plan(state, PlayerId.One, "TEST-P6-LANE", 0);
        state = Plan(state, PlayerId.One, "TEST-P6-ALL", 1);
        state = Plan(state, PlayerId.Two, "TEST-P6-GOBLIN", 0);
        state = Plan(state, PlayerId.Two, "TEST-P6-HEALER", 2);
        var turn = Resolve(state);
        Assert.Equal(1, turn.State.Entities.OfType<MinionEntityState>().Single(entity => entity.ControllerId == PlayerId.One && entity.LaneId.Value == 0).Attack);
        Assert.Equal(2, turn.State.Entities.OfType<MinionEntityState>().Single(entity => entity.ControllerId == PlayerId.One && entity.LaneId.Value == 1).Attack);
    }

    [Fact]
    public void MinionTurnEndStabilizesBeforeFieldsReadTheNextSnapshot()
    {
        var minion = Minion("END", """
            [{"id":"grow","trigger":{"kind":"turnEnd"},"body":{"kind":"modifyNumber","target":{"kind":"self"},"attribute":"attack","operation":"add","amount":2}}]
            """);
        const string field = """
            {"schemaVersion":"eota.card/v2","source":"test","profession":"neutral","kind":"field","id":"TEST-P6-FIELD","cost":0,
             "lifetime":{"kind":"finite","energy":4},"effects":[{"id":"end","trigger":{"kind":"turnEnd"},"body":
              {"kind":"retarget","target":{"kind":"friendlyMinions"},"body":{"kind":"damage","target":{"kind":"enemyHero"},"amount":"target.attack"}}}]}
            """;
        var state = Plan(Create([minion, field]), PlayerId.One, "TEST-P6-END", 0);
        state = Plan(state, PlayerId.One, "TEST-P6-FIELD", 0);
        var turn = Resolve(state);
        Assert.Equal(28, turn.State.Players[1].HeroHealth);
        Assert.Equal(3, Assert.IsType<FiniteFieldLifetimeState>(turn.State.Entities.OfType<FieldEntityState>().Single().Lifetime).Energy);
    }

    [Theory]
    [InlineData("combat")]
    [InlineData("attack")]
    public void CombatTriggersCanRevokeAnAttackBeforeDamage(string trigger)
    {
        var card = Minion("BRAKE", """
            [{"id":"stop","trigger":{"kind":"TRIGGER"},"body":{"kind":"addKeyword","target":{"kind":"self"},"keyword":"slow","amount":2}}]
            """.Replace("TRIGGER", trigger, StringComparison.Ordinal), attack: 5);
        var state = Plan(Create([card]), PlayerId.One, "TEST-P6-BRAKE", 0);
        state = Resolve(state).State;
        var turn = Resolve(state);
        Assert.Equal(30, turn.State.Players[1].HeroHealth);
        Assert.Equal(2, Assert.IsType<MinionEntityState>(Assert.Single(turn.State.Entities)).SlowTurnsRemaining);
        Assert.Contains(turn.Frames.SelectMany(frame => frame.Events.Events), fact => fact.Kind == (trigger == "combat" ? DomainEventKind.CombatDeclared : DomainEventKind.AttackDeclared));
    }

    [Fact]
    public void TriggerCycleFailsAtProtocolBudgetWithAtomicErrorReceipt()
    {
        var card = Minion("LOOP", """
            [{"id":"start","trigger":{"kind":"selfEntered"},"body":{"kind":"damage","target":{"kind":"self"},"amount":1}},
             {"id":"loop","trigger":{"kind":"selfDamaged"},"body":{"kind":"damage","target":{"kind":"self"},"amount":1}}]
            """, health: 100);
        var state = Create([card], triggerBudget: 3);
        var turn = Resolve(Plan(state, PlayerId.One, "TEST-P6-LOOP", 0));
        Assert.Equal(MatchStatus.Failed, turn.State.Status);
        Assert.False(turn.CompletedTurn);
        Assert.Equal(97, Assert.IsType<MinionEntityState>(Assert.Single(turn.State.Entities)).CurrentHealth);
        Assert.Contains(turn.Frames[^1].Receipts.Receipts, receipt => receipt.Status == IntentReceiptStatus.Error && receipt.DetailCode == "effect-trigger-budget-exceeded");
    }

    [Fact]
    public void RuntimeExpressionOverflowRollsBackSiblingEffects()
    {
        var card = Minion("OVERFLOW", """
            [{"id":"bad","trigger":{"kind":"selfEntered"},"body":{"kind":"parallel","children":[
              {"kind":"damage","target":{"kind":"enemyHero"},"amount":5},
              {"kind":"damage","target":{"kind":"enemyHero"},"amount":"owner.health * 9223372036854775807"}]}}]
            """);
        var turn = Resolve(Plan(Create([card]), PlayerId.One, "TEST-P6-OVERFLOW", 0));
        Assert.Equal(MatchStatus.Failed, turn.State.Status);
        Assert.Equal(30, turn.State.Players[1].HeroHealth);
        Assert.Contains(turn.Frames[^1].Receipts.Receipts, receipt => receipt.DetailCode == "expression-arithmetic-error");
    }

    internal static IEnumerable<string> RepresentativeCards()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Eota.sln"))) { directory = directory.Parent; }
        return Directory.EnumerateFiles(Path.Combine(directory!.FullName, "tests", "Fixtures", "P6Effects", "Cards"), "*.json").Select(File.ReadAllText);
    }

    internal static MatchState Create(IEnumerable<string> cards, int triggerBudget = 256, int intentBudget = 4096)
    {
        var content = CardContentCompiler.Compile(cards.Select((json, index) => new ContentSourceDocument($"{index}.json", json)));
        Assert.True(content.IsSuccess, string.Join(";", content.Diagnostics.Select(value => value.Path + ":" + value.Message)));
        var rules = content.Content!.Rules;
        var protocol = CompiledGameProtocol.Compile(GameProtocolDefinition.DefaultV0 with
        {
            RequiredDeckSize = rules.Cards.Length,
            OpeningHandSize = rules.Cards.Length,
            HandLimit = rules.Cards.Length,
            InitialPlayerCost = 100,
            InitialMaxCost = 100,
            MaxCostLimit = 100,
            CardsDrawnPerTurn = 0,
            DeckConstructionPolicy = DeckConstructionPolicy.DevelopmentAnySource,
            MaxEffectTriggerFramesPerTurn = triggerBudget,
            MaxEffectIntentsPerFrame = intentBudget
        });
        var deck = DeckDefinition.Create(Profession.Neutral, rules.Cards.Select(card => new DeckEntry(card.Id, 1)));
        var creation = MatchFactory.Create(new MatchCreationRequest(protocol, rules, 123456789, deck, deck));
        Assert.True(creation.IsSuccess);
        return creation.State!;
    }

    internal static MatchState Plan(MatchState state, PlayerId playerId, string prototype, int lane)
    {
        var player = state.Players.Single(value => value.Id == playerId);
        var card = state.CardInstances.Single(value => value.OwnerId == playerId && value.CurrentPrototypeId.Value == prototype);
        state.Content.TryGetCard(card.CurrentPrototypeId, out var definition);
        AuthoritativeCommand command = definition is SpellCardDefinition
            ? new PlanSpellCommand(playerId, player.CommandRevision, card.Id, new LaneId(lane))
            : new PlanCardCommand(playerId, player.CommandRevision, card.Id, new LaneId(lane));
        var result = MatchCommandProcessor.Accept(state, command);
        Assert.True(result.IsAccepted, result.Receipt.RejectionReason.ToString());
        return result.State;
    }

    internal static TurnResolutionResult Resolve(MatchState state)
    {
        foreach (var id in new[] { PlayerId.One, PlayerId.Two })
        {
            var result = MatchCommandProcessor.Accept(state, new SubmitTurnCommand(id, state.Players.Single(player => player.Id == id).CommandRevision));
            Assert.True(result.IsAccepted);
            state = result.State;
        }
        return TurnResolver.ResolveReadyTurn(state);
    }

    internal static string Minion(string name, string effects, int attack = 0, int health = 5) => $$"""
        {"schemaVersion":"eota.card/v2","source":"test","profession":"neutral","kind":"minion",
         "id":"TEST-P6-{{name}}","cost":0,"attack":{{attack}},"health":{{health}},"effects":{{effects}}}
        """;
}
