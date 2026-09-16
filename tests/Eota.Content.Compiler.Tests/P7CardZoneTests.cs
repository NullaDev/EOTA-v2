using System.Collections.Immutable;
using Eota.Kernel.Effects;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Content.Compiler.Tests;

public sealed class P7CardZoneTests
{
    [Fact]
    public void FilteredDrawModifiesOnlyTheActualInstanceAndDeploymentKeepsItsStats()
    {
        var blueprint = EffectRuntimeTests.Minion("BLUEPRINT", """
            [{"id":"blueprint","trigger":{"kind":"selfEntered"},"body":{"kind":"sequence","steps":[
              {"kind":"draw","target":{"kind":"friendlyHero"},"count":1,"filter":{"tag":"mechanical"}},
              {"kind":"parallel","children":[
                {"kind":"modifyNumber","target":{"kind":"previousAffected","targetType":"card"},"attribute":"attack","operation":"add","amount":1},
                {"kind":"modifyNumber","target":{"kind":"previousAffected","targetType":"card"},"attribute":"maximumHealth","operation":"add","amount":1}]}]}}]
            """);
        var token = EffectRuntimeTests.Minion("ROBOT", "[]", 1, 1).Replace("\"cost\":0", "\"tags\":[\"mechanical\"],\"cost\":0", StringComparison.Ordinal);
        var state = EffectRuntimeTests.Create([blueprint, token]);
        var robot = state.CardInstances.Single(value => value.OwnerId == PlayerId.One && value.CurrentPrototypeId.Value == "TEST-P6-ROBOT");
        state = state with
        {
            CardInstances = state.CardInstances.Replace(robot, robot with { Zone = CardZone.Deck }),
            Players = state.Players.SetItem(0, state.Players[0] with { Hand = state.Players[0].Hand.Remove(robot.Id), Deck = [robot.Id] })
        };
        var turn = EffectRuntimeTests.Resolve(EffectRuntimeTests.Plan(state, PlayerId.One, "TEST-P6-BLUEPRINT", 0));
        Assert.True(turn.CompletedTurn);
        var modified = turn.State.CardInstances.Single(value => value.Id == robot.Id);
        Assert.Equal(2, modified.Attack);
        Assert.Equal(2, modified.MaximumHealth);
        Assert.Null(turn.State.CardInstances.Single(value => value.OwnerId == PlayerId.Two && value.CurrentPrototypeId == robot.CurrentPrototypeId).Attack);
        var deployed = EffectRuntimeTests.Resolve(EffectRuntimeTests.Plan(turn.State, PlayerId.One, "TEST-P6-ROBOT", 1));
        var entity = deployed.State.Entities.OfType<MinionEntityState>().Single(value => value.CardInstanceId == robot.Id);
        Assert.Equal(2, entity.Attack);
        Assert.Equal(2, entity.MaximumHealth);
    }

    [Fact]
    public void ClearEtherReturnsCommittedScalarForBoundedLoop()
    {
        var card = EffectRuntimeTests.Minion("RESET", """
            [{"id":"reset","trigger":{"kind":"selfEntered"},"body":{"kind":"sequence","steps":[
              {"kind":"modifyNumber","target":{"kind":"friendlyLanes"},"attribute":"etherActivation","operation":"add","amount":3},
              {"kind":"clearEther","target":{"kind":"friendlyLanes"}},
              {"kind":"loop","count":"previous.scalar","body":{"kind":"damage","target":{"kind":"enemyHero"},"amount":1}}]}}]
            """);
        var turn = EffectRuntimeTests.Resolve(EffectRuntimeTests.Plan(EffectRuntimeTests.Create([card]), PlayerId.One, "TEST-P6-RESET", 0));
        Assert.True(turn.CompletedTurn);
        Assert.Equal(27, turn.State.Players[1].HeroHealth);
        Assert.Equal(0, turn.State.Lanes[0].PlayerOne.EtherActivation);
        Assert.Equal(3, turn.Frames.SelectMany(frame => frame.Receipts.Receipts).Single(value => value.DetailCode == "clear-ether").AppliedValue);
    }
}
