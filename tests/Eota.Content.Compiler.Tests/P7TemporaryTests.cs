using Eota.Kernel.Content;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Content.Compiler.Tests;

public sealed class P7TemporaryTests
{
    [Fact]
    public void TemporaryBaseAttackKeepsPermanentBonusesBeforeAndAfterExpiry()
    {
        var fortress = EffectRuntimeTests.Minion("BASE", """
            [{"id":"entry","trigger":{"kind":"selfEntered"},"body":{"kind":"sequence","steps":[
              {"kind":"modifyNumber","target":{"kind":"self"},"attribute":"attack","operation":"add","amount":1},
              {"kind":"modifyNumber","target":{"kind":"self"},"attribute":"baseAttack","operation":"set","amount":4,"duration":1}]}},
             {"id":"end","trigger":{"kind":"turnEnd"},"body":{"kind":"damage","target":{"kind":"enemyHero"},"amount":"source.attack"}}]
            """);
        var turn = EffectRuntimeTests.Resolve(EffectRuntimeTests.Plan(EffectRuntimeTests.Create([fortress]), PlayerId.One, "TEST-P6-BASE", 0));
        Assert.True(turn.CompletedTurn);
        Assert.Equal(25, turn.State.Players[1].HeroHealth);
        var entity = Assert.IsType<MinionEntityState>(Assert.Single(turn.State.Entities));
        Assert.Equal(1, entity.Attack);
        Assert.Equal(0, entity.BaseAttack);
        Assert.Empty(entity.TemporaryBaseAttacks);
    }

    [Theory]
    [InlineData(1, 2, 8, 14)]
    [InlineData(2, 1, 5, 17)]
    public void OverlappingBaseSetsRestoreTheSurvivingLayerThenThePermanentBase(int firstDuration, int secondDuration, int afterFirstCleanup, int enemyHealth)
    {
        var fortress = EffectRuntimeTests.Minion("LAYERED", $$$"""
            [{"id":"entry","trigger":{"kind":"selfEntered"},"body":{"kind":"sequence","steps":[
              {"kind":"modifyNumber","target":{"kind":"self"},"attribute":"baseAttack","operation":"set","amount":4,"duration":{{{firstDuration}}}},
              {"kind":"modifyNumber","target":{"kind":"self"},"attribute":"baseAttack","operation":"set","amount":7,"duration":{{{secondDuration}}}},
              {"kind":"modifyNumber","target":{"kind":"self"},"attribute":"attack","operation":"add","amount":1}]}},
             {"id":"end","trigger":{"kind":"turnEnd"},"body":{"kind":"damage","target":{"kind":"enemyHero"},"amount":"source.attack"}}]
            """);
        fortress = fortress.Replace("\"cost\":0", "\"cost\":0,\"keywords\":[\"guard\"]", StringComparison.Ordinal);
        var first = EffectRuntimeTests.Resolve(EffectRuntimeTests.Plan(EffectRuntimeTests.Create([fortress]), PlayerId.One, "TEST-P6-LAYERED", 0));
        Assert.True(first.CompletedTurn);
        Assert.Equal(afterFirstCleanup, Assert.IsType<MinionEntityState>(first.State.Entities[0]).Attack);
        var second = EffectRuntimeTests.Resolve(first.State);
        Assert.Equal(enemyHealth, second.State.Players[1].HeroHealth);
        Assert.Equal(1, Assert.IsType<MinionEntityState>(second.State.Entities[0]).Attack);
        Assert.Empty(Assert.IsType<MinionEntityState>(second.State.Entities[0]).TemporaryBaseAttacks);
    }

    [Fact]
    public void TemporaryAttackAndKeywordsExpireAfterEndEffectsAndKeepPermanentChanges()
    {
        var scout = EffectRuntimeTests.Minion("TIMED", """
            [{"id":"entry","trigger":{"kind":"selfEntered"},"body":{"kind":"sequence","steps":[
              {"kind":"parallel","children":[
                {"kind":"modifyNumber","target":{"kind":"self"},"attribute":"attack","operation":"add","amount":2,"duration":1},
                {"kind":"addKeyword","target":{"kind":"self"},"keyword":"firstStrike","duration":1}]},
              {"kind":"modifyNumber","target":{"kind":"self"},"attribute":"attack","operation":"add","amount":1}]}},
             {"id":"end","trigger":{"kind":"turnEnd"},"body":{"kind":"damage","target":{"kind":"enemyHero"},"amount":"source.attack"}}]
            """, attack: 1);
        var turn = EffectRuntimeTests.Resolve(EffectRuntimeTests.Plan(EffectRuntimeTests.Create([scout]), PlayerId.One, "TEST-P6-TIMED", 0));
        Assert.True(turn.CompletedTurn);
        Assert.Equal(26, turn.State.Players[1].HeroHealth);
        var minion = Assert.IsType<MinionEntityState>(Assert.Single(turn.State.Entities));
        Assert.Equal(2, minion.Attack);
        Assert.DoesNotContain(minion.Keywords, value => value.Kind == MinionKeywordKind.FirstStrike);
        Assert.Empty(minion.TemporaryModifiers);
        Assert.Contains(turn.Frames, frame => frame.State.Entities.OfType<MinionEntityState>().Any(value => value.Attack == 4 && value.Keywords.Any(keyword => keyword.Kind == MinionKeywordKind.FirstStrike)));
    }

    [Fact]
    public void TemporaryDamageReductionAppliesToLaterFramesAndThenExpires()
    {
        var scout = EffectRuntimeTests.Minion("REDUCTION", """
            [{"id":"entry","trigger":{"kind":"selfEntered"},"body":{"kind":"sequence","steps":[
              {"kind":"modifyNumber","target":{"kind":"self"},"attribute":"incomingDamageAdjustment","operation":"add","amount":-3,"duration":1},
              {"kind":"damage","target":{"kind":"self"},"amount":5}]}}]
            """, health: 6);
        var turn = EffectRuntimeTests.Resolve(EffectRuntimeTests.Plan(EffectRuntimeTests.Create([scout]), PlayerId.One, "TEST-P6-REDUCTION", 0));
        Assert.True(turn.CompletedTurn);
        var minion = Assert.IsType<MinionEntityState>(Assert.Single(turn.State.Entities));
        Assert.Equal(4, minion.CurrentHealth);
        Assert.Equal(0, minion.IncomingDamageAdjustment);
        Assert.Equal(2, turn.Frames.SelectMany(frame => frame.Receipts.Receipts).Single(value => value.DetailCode == "damage").AppliedValue);
    }

    [Fact]
    public void PermanentKeywordRemovalDuringTemporaryRemovalDoesNotReappearOnExpiry()
    {
        var scout = EffectRuntimeTests.Minion("KEYWORD", """
            [{"id":"entry","trigger":{"kind":"selfEntered"},"body":{"kind":"sequence","steps":[
              {"kind":"removeKeyword","target":{"kind":"self"},"keyword":"guard","duration":1},
              {"kind":"removeKeyword","target":{"kind":"self"},"keyword":"guard"}]}}]
            """).Replace("\"cost\":0", "\"keywords\":[\"guard\"],\"cost\":0", StringComparison.Ordinal);
        var turn = EffectRuntimeTests.Resolve(EffectRuntimeTests.Plan(EffectRuntimeTests.Create([scout]), PlayerId.One, "TEST-P6-KEYWORD", 0));
        Assert.True(turn.CompletedTurn);
        Assert.DoesNotContain(Assert.IsType<MinionEntityState>(turn.State.Entities[0]).Keywords, value => value.Kind == MinionKeywordKind.Guard);
    }
}
