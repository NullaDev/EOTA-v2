using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Content.Compiler.Tests;

public sealed class P7AttachedEffectTests
{
    [Fact]
    public void GrantedDefinitionsShareTheParentCardGraphBudget()
    {
        var children = string.Join(',', Enumerable.Repeat("""{"kind":"damage","target":{"kind":"enemyHero"},"amount":1}""", 126));
        var card = EffectRuntimeTests.Minion("GRANT-BUDGET", """
            [{"id":"outer","trigger":{"kind":"selfEntered"},"body":{"kind":"parallel","children":[
              {"kind":"grantEffect","target":{"kind":"self"},"effect":{"id":"inner","trigger":{"kind":"turnEnd"},
                "body":{"kind":"parallel","children":[CHILDREN]}}}]}}]
            """.Replace("CHILDREN", children, StringComparison.Ordinal));
        var result = CardContentCompiler.Compile([new ContentSourceDocument("budget.json", card)]);
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, value => value.Code == "effect-budget-exceeded");
    }

    [Fact]
    public void TemporaryGrantedTriggerSurvivesTrapDestructionAndExpiresAfterFiring()
    {
        const string trap = """
            {"schemaVersion":"eota.card/v2","source":"test","profession":"neutral","kind":"field","id":"TEST-P7-POISON","cost":0,
             "lifetime":{"kind":"finite","energy":5},"effects":[
              {"id":"trap","trigger":{"kind":"enemyEntered","subjectType":"minion"},"body":{"kind":"sequence","steps":[
                {"kind":"grantEffect","target":{"kind":"eventSubject"},"duration":1,
                 "effect":{"id":"poison","trigger":{"kind":"turnEnd"},"body":{"kind":"damage","target":{"kind":"self"},"amount":2}}},
                {"kind":"kill","target":{"kind":"self"}}]}}]}
            """;
        var victim = EffectRuntimeTests.Minion("POISON-VICTIM", "[]", health: 6);
        var state = EffectRuntimeTests.Create([trap, victim]);
        state = EffectRuntimeTests.Plan(state, PlayerId.One, "TEST-P7-POISON", 0);
        state = EffectRuntimeTests.Plan(state, PlayerId.Two, "TEST-P6-POISON-VICTIM", 0);
        var turn = EffectRuntimeTests.Resolve(state);
        Assert.True(turn.CompletedTurn);
        Assert.Empty(turn.State.Entities.OfType<FieldEntityState>());
        var minion = Assert.IsType<MinionEntityState>(Assert.Single(turn.State.Entities));
        Assert.Equal(4, minion.CurrentHealth);
        Assert.Empty(minion.AttachedEffects);
        Assert.Equal(4, EffectRuntimeTests.Resolve(turn.State).State.Entities.OfType<MinionEntityState>().Single().CurrentHealth);
    }

    [Fact]
    public void PoisonTrapPermanentlyGrantsHealthLossWithoutDamageTriggers()
    {
        const string trap = """
            {"schemaVersion":"eota.card/v2","source":"test","profession":"neutral","kind":"field","id":"TEST-P7-TRUE-POISON","cost":0,
             "lifetime":{"kind":"finite","energy":3},"effects":[
              {"id":"trap","trigger":{"kind":"enemyEntered","subjectType":"minion"},"body":{"kind":"parallel","children":[
                {"kind":"grantEffect","target":{"kind":"eventSubject"},
                 "effect":{"id":"poison","trigger":{"kind":"turnEnd"},"body":{"kind":"loseHealth","target":{"kind":"self"},"amount":2}}},
                {"kind":"kill","target":{"kind":"self"}}]}}]}
            """;
        var victim = EffectRuntimeTests.Minion("TRUE-POISON-VICTIM", """
            [{"id":"reaction","trigger":{"kind":"selfDamaged"},"body":{"kind":"damage","target":{"kind":"enemyHero"},"amount":1}}]
            """, health: 6);
        var state = EffectRuntimeTests.Plan(EffectRuntimeTests.Create([trap, victim]), PlayerId.One, "TEST-P7-TRUE-POISON", 0);
        state = EffectRuntimeTests.Plan(state, PlayerId.Two, "TEST-P6-TRUE-POISON-VICTIM", 0);
        var first = EffectRuntimeTests.Resolve(state);
        Assert.True(first.CompletedTurn);
        Assert.Equal(4, Assert.IsType<MinionEntityState>(Assert.Single(first.State.Entities)).CurrentHealth);
        Assert.Null(Assert.Single(first.State.Entities[0].AttachedEffects).RemainingDuration);
        var second = EffectRuntimeTests.Resolve(first.State);
        Assert.Equal(2, Assert.IsType<MinionEntityState>(Assert.Single(second.State.Entities)).CurrentHealth);
        Assert.All(second.State.Players, value => Assert.Equal(30, value.HeroHealth));
        Assert.DoesNotContain(first.Frames.Concat(second.Frames).SelectMany(frame => frame.Events.Events), value => value.Kind == DomainEventKind.EntityDamaged);
    }

    [Fact]
    public void PlayerDeathWatcherSurvivesItsOriginalSourceAndExpiresAtCleanup()
    {
        var watcher = EffectRuntimeTests.Minion("WATCHER", """
            [{"id":"watch","trigger":{"kind":"selfEntered"},"body":{"kind":"sequence","steps":[
              {"kind":"grantEffect","target":{"kind":"friendlyHero"},"duration":1,
               "effect":{"id":"loot","trigger":{"kind":"enemyDied","scope":"all","subjectType":"minion"},
                 "body":{"kind":"generate","target":{"kind":"friendlyHero"},"count":1,"prototype":"TEST-P6-LOOT"}}},
              {"kind":"parallel","children":[{"kind":"kill","target":{"kind":"self"}},{"kind":"kill","target":{"kind":"enemyMinions"}}]}]}}]
            """);
        var victim = EffectRuntimeTests.Minion("WATCHED", "[]");
        var loot = EffectRuntimeTests.Minion("LOOT", "[]");
        var state = EffectRuntimeTests.Create([watcher, victim, loot]);
        state = EffectRuntimeTests.Plan(state, PlayerId.One, "TEST-P6-WATCHER", 0);
        state = EffectRuntimeTests.Plan(state, PlayerId.Two, "TEST-P6-WATCHED", 0);
        var turn = EffectRuntimeTests.Resolve(state);
        Assert.True(turn.CompletedTurn);
        Assert.Empty(turn.State.Entities);
        Assert.Empty(turn.State.Players[0].AttachedEffects);
        Assert.Single(turn.Frames.SelectMany(frame => frame.Events.Events), value => value.Kind == DomainEventKind.CardGenerated);
        Assert.Equal(2, turn.State.Players[0].Hand.Count(id => turn.State.CardInstances.Single(value => value.Id == id).CurrentPrototypeId.Value == "TEST-P6-LOOT"));
    }
}
