using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Content.Compiler.Tests;

public sealed class P7CombatContributionTests
{
    [Theory]
    [InlineData("fieldPower", "source.energy", "invalid-enum")]
    [InlineData("fieldEnergy", "source.power", "unknown-variable")]
    [InlineData("fieldEnergy", "target.power", "unknown-variable")]
    [InlineData("fieldEnergy", "event.power", "unknown-variable")]
    public void FieldEffectsAcceptOnlyEnergyNames(string attribute, string amount, string errorCode)
    {
        const string json = """
            {"schemaVersion":"eota.card/v2","kind":"field","id":"TEST-ENERGY-NAMES",
             "lifetime":{"kind":"finite","energy":3},"effects":[
               {"id":"entry","trigger":{"kind":"selfEntered"},"body":{"kind":"retarget","target":{"kind":"self"},
                "body":{"kind":"modifyNumber","target":{"kind":"targets"},"attribute":"fieldEnergy","operation":"set","amount":"source.energy"}}}]}
            """;
        var card = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var action = card["effects"]![0]!["body"]!["body"]!;
        action["amount"] = amount.Replace("power", "energy", StringComparison.Ordinal);
        Assert.True(CardContentCompiler.Compile([new ContentSourceDocument("field.json", card.ToJsonString())]).IsSuccess);
        action["attribute"] = attribute; action["amount"] = amount;
        var result = CardContentCompiler.Compile([new ContentSourceDocument("field.json", card.ToJsonString())]);
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == errorCode);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 5)]
    public void MinionCollisionTriggerPoisonsOnlyTheActualDefender(bool slow, int health)
    {
        var toad = EffectRuntimeTests.Minion("TOAD", """
            [{"id":"poison","trigger":{"kind":"minionCombat"},"body":{"kind":"grantEffect","target":{"kind":"eventTarget"},
              "effect":{"id":"poison","trigger":{"kind":"turnEnd"},"body":{"kind":"loseHealth","target":{"kind":"self"},"amount":2}}}}]
            """, attack: 1).Replace("\"cost\":0", "\"cost\":0,\"keywords\":[\"swift\"]", StringComparison.Ordinal);
        var target = EffectRuntimeTests.Minion("DEFENDER", "[]").Replace("\"cost\":0",
            slow ? "\"cost\":0,\"keywords\":[{\"kind\":\"slow\",\"turns\":1}]" : "\"cost\":0,\"keywords\":[\"guard\"]", StringComparison.Ordinal);
        var state = EffectRuntimeTests.Plan(EffectRuntimeTests.Create([toad, target]), PlayerId.One, "TEST-P6-TOAD", 0);
        state = EffectRuntimeTests.Plan(state, PlayerId.Two, "TEST-P6-DEFENDER", 0);
        var turn = EffectRuntimeTests.Resolve(state);
        Assert.True(turn.CompletedTurn);
        var defender = turn.State.Entities.OfType<MinionEntityState>().Single(value => value.ControllerId == PlayerId.Two);
        Assert.Equal(health, defender.CurrentHealth);
        Assert.Equal(slow ? 0 : 1, defender.AttachedEffects.Length);
    }

    [Theory]
    [InlineData(false, 30)]
    [InlineData(true, 26)]
    public void CombatStageEffectsRunBeforeEngagementCaptureWithoutRequiringAnAttack(bool swift, int heroHealth)
    {
        var miner = EffectRuntimeTests.Minion("MINER", """
            [{"id":"clear","trigger":{"kind":"combatStage"},"condition":{"kind":"not","condition":{"kind":"exists","target":{"kind":"enemyMinions"}}},
              "body":{"kind":"kill","target":{"kind":"enemyFields"}}}]
            """, attack: 4);
        if (swift) { miner = miner.Replace("\"cost\":0", "\"cost\":0,\"keywords\":[\"swift\"]", StringComparison.Ordinal); }
        const string field = """
            {"schemaVersion":"eota.card/v2","source":"test","profession":"neutral","kind":"field","id":"TEST-P7-BLOCKER","cost":0,
             "lifetime":{"kind":"permanent"},"preventsActiveAttacksInLane":true}
            """;
        var state = EffectRuntimeTests.Plan(EffectRuntimeTests.Create([miner, field]), PlayerId.One, "TEST-P6-MINER", 0);
        state = EffectRuntimeTests.Plan(state, PlayerId.Two, "TEST-P7-BLOCKER", 0);
        var turn = EffectRuntimeTests.Resolve(state);
        Assert.True(turn.CompletedTurn);
        Assert.Empty(turn.State.Entities.OfType<FieldEntityState>());
        Assert.Equal(heroHealth, turn.State.Players[1].HeroHealth);
    }

    [Fact]
    public void MortarHeroDamageIsInTheCombatFrameEvenWhenTheSourceDies()
    {
        var mortar = EffectRuntimeTests.Minion("MORTAR", """
            [{"id":"splash","trigger":{"kind":"combatDamage"},"body":{"kind":"damage","target":{"kind":"enemyHero"},"amount":"event.amount"}}]
            """, attack: 6, health: 2).Replace("\"cost\":0", "\"keywords\":[\"swift\"],\"cost\":0", StringComparison.Ordinal);
        var victim = EffectRuntimeTests.Minion("MORTAR-VICTIM", "[]", attack: 2, health: 6);
        var state = EffectRuntimeTests.Plan(EffectRuntimeTests.Create([mortar, victim]), PlayerId.One, "TEST-P6-MORTAR", 0);
        state = EffectRuntimeTests.Plan(state, PlayerId.Two, "TEST-P6-MORTAR-VICTIM", 0);
        var turn = EffectRuntimeTests.Resolve(state);
        Assert.True(turn.CompletedTurn);
        Assert.Empty(turn.State.Entities);
        Assert.Equal(24, turn.State.Players[1].HeroHealth);
        var damage = Assert.Single(turn.Frames, frame => frame.Events.Events.Any(value => value.Kind == DomainEventKind.HeroHealthChanged));
        Assert.Equal(2, damage.Events.Events.Count(value => value.Kind == DomainEventKind.EntityDied));
        Assert.Equal(MatchStage.Combat, damage.State.Stage);
    }

    [Fact]
    public void LoseHealthBypassesDamageReductionAndDoesNotTriggerSelfDamaged()
    {
        var card = EffectRuntimeTests.Minion("HEALTH-LOSS", """
            [{"id":"start","trigger":{"kind":"selfEntered"},"body":{"kind":"sequence","steps":[
              {"kind":"modifyNumber","target":{"kind":"self"},"attribute":"incomingDamageAdjustment","operation":"add","amount":-3,"duration":1},
              {"kind":"loseHealth","target":{"kind":"self"},"amount":2}]}},
             {"id":"hurt","trigger":{"kind":"selfDamaged"},"body":{"kind":"damage","target":{"kind":"enemyHero"},"amount":10}}]
            """, health: 5);
        var turn = EffectRuntimeTests.Resolve(EffectRuntimeTests.Plan(EffectRuntimeTests.Create([card]), PlayerId.One, "TEST-P6-HEALTH-LOSS", 0));
        Assert.Equal(30, turn.State.Players[1].HeroHealth);
        Assert.Equal(3, Assert.IsType<MinionEntityState>(turn.State.Entities[0]).CurrentHealth);
        Assert.Contains(turn.Frames.SelectMany(frame => frame.Events.Events), value => value.Kind == DomainEventKind.EntityHealthLost);
        Assert.DoesNotContain(turn.Frames.SelectMany(frame => frame.Events.Events), value => value.Kind == DomainEventKind.EntityDamaged);
    }

    [Fact]
    public void FieldEnergyChangesDurabilityAndCleanupConsumesTheSameValue()
    {
        const string field = """
            {"schemaVersion":"eota.card/v2","source":"test","profession":"neutral","kind":"field","id":"TEST-P7-ENERGY","cost":0,
             "lifetime":{"kind":"finite","energy":5},"effects":[
               {"id":"entry","trigger":{"kind":"selfEntered"},"body":{"kind":"sequence","steps":[
                 {"kind":"modifyNumber","target":{"kind":"self"},"attribute":"fieldEnergy","operation":"set","amount":3},
                 {"kind":"damage","target":{"kind":"enemyHero"},"amount":"source.energy"}]}}]}
            """;
        var turn = EffectRuntimeTests.Resolve(EffectRuntimeTests.Plan(EffectRuntimeTests.Create([field]), PlayerId.One, "TEST-P7-ENERGY", 0));
        var entity = Assert.IsType<FieldEntityState>(Assert.Single(turn.State.Entities));
        Assert.Equal(2, entity.Lifetime.VisibleEnergy);
        Assert.Equal(27, turn.State.Players[1].HeroHealth);
    }
}
