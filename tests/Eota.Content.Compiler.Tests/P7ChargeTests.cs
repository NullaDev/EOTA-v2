using System.Collections.Immutable;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Content.Compiler.Tests;

public sealed class P7ChargeTests
{
    [Theory]
    [InlineData(0, 30)]
    [InlineData(1, 27)]
    public void DemandsAcrossMinionAndFieldAreOneBatchAndStoredChargeIsNotConsumed(int stored, int health)
    {
        var scout = EffectRuntimeTests.Minion("CHARGE", """
            [{"id":"charge","trigger":{"kind":"preCombatCharge"},"charge":1,
              "body":{"kind":"damage","target":{"kind":"enemyHero"},"amount":1}}]
            """);
        var battery = $$$"""
            {"schemaVersion":"eota.card/v2","source":"test","profession":"neutral","kind":"field","id":"TEST-P7-BATTERY","cost":0,
             "lifetime":{"kind":"finite","energy":5},"storedCharge":{{{stored}}},
             "effects":[{"id":"charge","trigger":{"kind":"preCombatCharge"},"charge":1,
               "body":{"kind":"damage","target":{"kind":"enemyHero"},"amount":2}}]}
            """;
        var state = EffectRuntimeTests.Create([scout, battery]);
        state = state with { Players = state.Players.Select(value => value with { CurrentCost = 1 }).ToImmutableArray() };
        state = EffectRuntimeTests.Plan(state, PlayerId.One, "TEST-P6-CHARGE", 0);
        state = EffectRuntimeTests.Plan(state, PlayerId.One, "TEST-P7-BATTERY", 0);
        var turn = EffectRuntimeTests.Resolve(state);
        Assert.True(turn.CompletedTurn);
        Assert.Equal(health, turn.State.Players[1].HeroHealth);
        Assert.Equal(stored, turn.State.Entities.OfType<FieldEntityState>().Single().StoredCharge);
        var combatStart = turn.Frames.First(frame => frame.State.Stage == MatchStage.Combat).State;
        Assert.Equal(1, combatStart.Players[0].CurrentCost);
        var hits = turn.Frames.Where(frame => frame.Events.Events.Any(value => value.Kind == DomainEventKind.HeroHealthChanged)).ToArray();
        Assert.Equal(stored == 0 ? 0 : 1, hits.Length);
    }

    [Fact]
    public void EntryRequirementOverrideAffectsTheLaterChargeBatch()
    {
        var scout = EffectRuntimeTests.Minion("CHARGE-FREE", """
            [{"id":"override","trigger":{"kind":"selfEntered"},"body":{"kind":"modifyNumber","target":{"kind":"self"},
               "attribute":"chargeRequirement","operation":"set","amount":0}},
             {"id":"charge","trigger":{"kind":"preCombatCharge"},"charge":99,
               "body":{"kind":"damage","target":{"kind":"enemyHero"},"amount":1}}]
            """);
        var state = EffectRuntimeTests.Create([scout]);
        state = state with { Players = state.Players.Select(value => value with { CurrentCost = 0 }).ToImmutableArray() };
        var turn = EffectRuntimeTests.Resolve(EffectRuntimeTests.Plan(state, PlayerId.One, "TEST-P6-CHARGE-FREE", 0));
        Assert.True(turn.CompletedTurn);
        Assert.Equal(29, turn.State.Players[1].HeroHealth);
        Assert.Equal(0, turn.State.Entities[0].ChargeRequirementOverride);
    }

    [Fact]
    public void EndTurnChargeCompletesBeforeMinionEndRoots()
    {
        var scout = EffectRuntimeTests.Minion("END-CHARGE", """
            [{"id":"charge","trigger":{"kind":"endTurnCharge"},"charge":1,
               "body":{"kind":"modifyNumber","target":{"kind":"self"},"attribute":"attack","operation":"add","amount":2}},
             {"id":"end","trigger":{"kind":"turnEnd"},
               "body":{"kind":"damage","target":{"kind":"enemyHero"},"amount":"source.attack"}}]
            """);
        var turn = EffectRuntimeTests.Resolve(EffectRuntimeTests.Plan(EffectRuntimeTests.Create([scout]), PlayerId.One, "TEST-P6-END-CHARGE", 0));
        Assert.Equal(28, turn.State.Players[1].HeroHealth);
        Assert.True(turn.CompletedTurn);
    }

    [Fact]
    public void EntryStageFieldRefreshesTemporaryRequirementEachTurnAndCleanupRestoresDefaults()
    {
        var scout = EffectRuntimeTests.Minion("TIMED-CHARGE", """
            [{"id":"charge","trigger":{"kind":"preCombatCharge"},"charge":99,
              "body":{"kind":"damage","target":{"kind":"enemyHero"},"amount":1}}]
            """);
        const string station = """
            {"schemaVersion":"eota.card/v2","source":"test","profession":"neutral","kind":"field","id":"TEST-P7-STATION","cost":0,
             "lifetime":{"kind":"finite","energy":5},"effects":[{"id":"free","trigger":{"kind":"entryStage"},
             "body":{"kind":"parallel","children":[
               {"kind":"modifyNumber","target":{"kind":"friendlyMinions"},"attribute":"chargeRequirement","operation":"set","amount":0,"duration":1},
               {"kind":"modifyNumber","target":{"kind":"self"},"attribute":"chargeRequirement","operation":"set","amount":0,"duration":1}]}},
             {"id":"charge","trigger":{"kind":"preCombatCharge"},"charge":99,
              "body":{"kind":"damage","target":{"kind":"enemyHero"},"amount":2}}]}
            """;
        var state = EffectRuntimeTests.Plan(EffectRuntimeTests.Create([scout, station]), PlayerId.One, "TEST-P6-TIMED-CHARGE", 0);
        state = EffectRuntimeTests.Plan(state, PlayerId.One, "TEST-P7-STATION", 0);
        var first = EffectRuntimeTests.Resolve(state);
        Assert.True(first.CompletedTurn);
        Assert.Equal(27, first.State.Players[1].HeroHealth);
        Assert.All(first.State.Entities, entity => { Assert.Null(entity.ChargeRequirementOverride); Assert.Empty(entity.TemporaryChargeOverrides); });
        var second = EffectRuntimeTests.Resolve(first.State);
        Assert.True(second.CompletedTurn);
        Assert.Equal(24, second.State.Players[1].HeroHealth);
        Assert.True(second.State.NextEffectProgramId > first.State.NextEffectProgramId);
        Assert.All(second.State.Entities, entity => Assert.Null(entity.ChargeRequirementOverride));
    }
}
