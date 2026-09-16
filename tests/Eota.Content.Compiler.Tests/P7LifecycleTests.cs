using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Content.Compiler.Tests;

public sealed class P7LifecycleTests
{
    [Fact]
    public void SimultaneouslyRemovedObserversUseFinalFrozenStatsAndObserveEachOther()
    {
        const string effects = """
            [{"id":"revenge","trigger":{"kind":"friendlyDied","scope":"all","subjectType":"minion","otherOnly":true},
              "body":{"kind":"damage","target":{"kind":"enemyHero"},"amount":"source.attack + event.attack"}}]
            """;
        var first = EffectRuntimeTests.Minion("WATCH-A", effects, 2, 1);
        var second = EffectRuntimeTests.Minion("WATCH-B", effects, 3, 1);
        const string spell = """
            {"schemaVersion":"eota.card/v2","source":"test","profession":"neutral","kind":"spell","id":"TEST-P7-SWEEP","cost":0,
             "speed":"fast","targetScope":"lane","effects":[{"id":"sweep","trigger":{"kind":"selfSpellCast"},
              "body":{"kind":"parallel","children":[
                {"kind":"modifyNumber","target":{"kind":"enemyMinions","scope":"all"},"attribute":"attack","operation":"add","amount":1},
                {"kind":"damage","target":{"kind":"enemyMinions","scope":"all"},"amount":1}]}}]}
            """;
        var state = EffectRuntimeTests.Create([first, second, spell]);
        state = EffectRuntimeTests.Plan(state, PlayerId.One, "TEST-P6-WATCH-A", 0);
        state = EffectRuntimeTests.Plan(state, PlayerId.One, "TEST-P6-WATCH-B", 1);
        state = EffectRuntimeTests.Plan(state, PlayerId.Two, "TEST-P7-SWEEP", 0);
        var turn = EffectRuntimeTests.Resolve(state);
        Assert.True(turn.CompletedTurn);
        Assert.Empty(turn.State.Entities);
        Assert.Equal(16, turn.State.Players[1].HeroHealth);
        Assert.Equal(2, turn.State.Tombstones.Length);
        Assert.Equal(3, turn.State.Tombstones[0].Attack);
        Assert.Equal(4, turn.State.Tombstones[1].Attack);
    }

    [Theory]
    [InlineData("banish", 30, DomainEventKind.EntityBanished)]
    [InlineData("return", 29, DomainEventKind.EntityLeft)]
    public void BanishAndReturnHaveDistinctTriggerFacts(string operation, int enemyHealth, DomainEventKind fact)
    {
        var minion = EffectRuntimeTests.Minion("LEAVE", """
            [{"id":"remove","trigger":{"kind":"selfEntered"},"body":{"kind":"OPERATION","target":{"kind":"self"}}},
             {"id":"left","trigger":{"kind":"selfLeft"},"body":{"kind":"damage","target":{"kind":"enemyHero"},"amount":1}},
             {"id":"died","trigger":{"kind":"selfDied"},"body":{"kind":"damage","target":{"kind":"enemyHero"},"amount":10}}]
            """.Replace("OPERATION", operation, StringComparison.Ordinal));
        var state = EffectRuntimeTests.Plan(EffectRuntimeTests.Create([minion]), PlayerId.One, "TEST-P6-LEAVE", 0);
        var turn = EffectRuntimeTests.Resolve(state);
        Assert.True(turn.CompletedTurn);
        Assert.Equal(enemyHealth, turn.State.Players[1].HeroHealth);
        Assert.Contains(turn.Frames.SelectMany(frame => frame.Events.Events), value => value.Kind == fact);
        Assert.DoesNotContain(turn.Frames.SelectMany(frame => frame.Events.Events), value => value.Kind == DomainEventKind.EntityDied);
    }
}
