using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Content.Compiler.Tests;

public sealed class P7SummonTests
{
    [Fact]
    public void ReturnThenSummonUsesNewSlotAndCreatedEntityInNextStep()
    {
        var caster = EffectRuntimeTests.Minion("SWAP", """
            [{"id":"swap","trigger":{"kind":"selfEntered"},"body":{"kind":"sequence","steps":[
              {"kind":"return","target":{"kind":"self"}},
              {"kind":"summon","target":{"kind":"friendlyLanes"},"prototype":"TEST-P6-TOKEN"},
              {"kind":"modifyNumber","target":{"kind":"previousCreated","targetType":"minion"},"attribute":"attack","operation":"add","amount":2}]}}]
            """);
        var token = EffectRuntimeTests.Minion("TOKEN", """
            [{"id":"entry","trigger":{"kind":"selfEntered"},"body":{"kind":"damage","target":{"kind":"enemyHero"},"amount":1}}]
            """, attack: 1);
        var turn = EffectRuntimeTests.Resolve(EffectRuntimeTests.Plan(EffectRuntimeTests.Create([caster, token]), PlayerId.One, "TEST-P6-SWAP", 0));
        Assert.True(turn.CompletedTurn);
        Assert.Equal(29, turn.State.Players[1].HeroHealth);
        var minion = Assert.IsType<MinionEntityState>(Assert.Single(turn.State.Entities));
        Assert.Equal(3, minion.Attack);
        Assert.Equal("TEST-P6-TOKEN", turn.State.CardInstances.Single(value => value.Id == minion.CardInstanceId).CurrentPrototypeId.Value);
        Assert.Equal(EntityRemovalReason.Return, Assert.Single(turn.State.Tombstones).Reason);
        Assert.Equal(2, turn.Frames.SelectMany(frame => frame.Events.Events).Count(value => value.Kind == DomainEventKind.EntityEntered));
    }
}
