using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Content.Compiler.Tests;

public sealed class HeroKillEffectTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JsonCanKillHeroesAndTerminalFramesStopTheFollowingSequence(bool both)
    {
        var targets = both ? new[] { "enemyHero", "friendlyHero" } : ["enemyHero"];
        var kills = string.Join(',', targets.Select(target => System.Text.Json.JsonSerializer.Serialize(new { kind = "kill", target = new { kind = target } })));
        var card = EffectRuntimeTests.Minion("EXECUTION", """
            [{"id":"entry","trigger":{"kind":"selfEntered"},"body":{"kind":"sequence","steps":[
              {"kind":"parallel","children":[KILLS,
                {"kind":"heal","target":{"kind":"enemyHero"},"amount":100},
                {"kind":"modifyNumber","target":{"kind":"self"},"attribute":"attack","operation":"add","amount":7}]},
              {"kind":"modifyNumber","target":{"kind":"self"},"attribute":"attack","operation":"add","amount":1000}]}}]
            """.Replace("KILLS", kills, StringComparison.Ordinal), attack: 2);
        var turn = EffectRuntimeTests.Resolve(EffectRuntimeTests.Plan(EffectRuntimeTests.Create([card]), PlayerId.One, "TEST-P6-EXECUTION", 0));
        Assert.Equal(MatchStatus.Finished, turn.State.Status);
        Assert.Equal(both ? MatchOutcome.Draw : MatchOutcome.PlayerOneWon, turn.State.Outcome);
        Assert.Equal(0, turn.State.Players[1].HeroHealth);
        Assert.Equal(9, Assert.IsType<MinionEntityState>(Assert.Single(turn.State.Entities)).Attack);
        Assert.Equal(targets.Length, turn.Frames.SelectMany(frame => frame.Receipts.Receipts).Count(receipt => receipt.DetailCode == "kill"));
        Assert.Single(turn.Frames.SelectMany(frame => frame.Events.Events), fact => fact.Kind == DomainEventKind.MatchEnded);
    }
}
