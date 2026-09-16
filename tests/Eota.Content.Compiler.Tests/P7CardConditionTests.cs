using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Content.Compiler.Tests;

public sealed class P7CardConditionTests
{
    [Theory]
    [InlineData("mechanical", 1)]
    [InlineData("beast", 0)]
    public void DeathObserverFiltersFrozenEventPrototypeBeforeSummoning(string tag, int expected)
    {
        const string junkyard = """
            {"schemaVersion":"eota.card/v2","source":"test","profession":"neutral","kind":"field","id":"TEST-P7-JUNKYARD","cost":0,
             "lifetime":{"kind":"finite","energy":3},"effects":[
              {"id":"salvage","trigger":{"kind":"friendlyDied","subjectType":"minion"},
               "condition":{"kind":"cardMatches","subject":"event","filter":{"tag":"mechanical"}},
               "body":{"kind":"summon","target":{"kind":"friendlyLanes"},"prototype":"TEST-P6-SCRAP","slot":"minion"}}]}
            """;
        var victim = EffectRuntimeTests.Minion("TAGGED", """
            [{"id":"die","trigger":{"kind":"selfEntered"},"body":{"kind":"kill","target":{"kind":"self"}}}]
            """).Replace("\"cost\":0", $"\"tags\":[\"{tag}\"],\"cost\":0", StringComparison.Ordinal);
        var state = EffectRuntimeTests.Create([junkyard, victim, EffectRuntimeTests.Minion("SCRAP", "[]")]);
        state = EffectRuntimeTests.Plan(state, PlayerId.One, "TEST-P7-JUNKYARD", 0);
        state = EffectRuntimeTests.Plan(state, PlayerId.One, "TEST-P6-TAGGED", 0);
        var turn = EffectRuntimeTests.Resolve(state);
        Assert.True(turn.CompletedTurn);
        Assert.Equal(expected, turn.State.Entities.OfType<MinionEntityState>().Count());
    }

    [Fact]
    public void HandKeywordConditionReadsTheModifiedInstance()
    {
        var trainer = EffectRuntimeTests.Minion("TRAINER", """
            [{"id":"train","trigger":{"kind":"selfEntered"},"body":{"kind":"sequence","steps":[
              {"kind":"addKeyword","target":{"kind":"friendlyHand","filter":{"kind":"minion"}},"keyword":"guard"},
              {"kind":"forEach","target":{"kind":"friendlyHand","filter":{"kind":"minion"}},"body":
                {"kind":"ifElse","condition":{"kind":"hasKeyword","target":{"kind":"targets"},"keyword":"guard"},
                 "then":{"kind":"modifyNumber","target":{"kind":"targets"},"attribute":"attack","operation":"add","amount":2}}}]}}]
            """);
        var state = EffectRuntimeTests.Plan(EffectRuntimeTests.Create([trainer, EffectRuntimeTests.Minion("RECRUIT", "[]", attack: 1)]), PlayerId.One, "TEST-P6-TRAINER", 0);
        var turn = EffectRuntimeTests.Resolve(state);
        Assert.True(turn.CompletedTurn);
        Assert.Equal(3, turn.State.CardInstances.Single(value => value.OwnerId == PlayerId.One && value.CurrentPrototypeId.Value == "TEST-P6-RECRUIT").Attack);
    }
}
