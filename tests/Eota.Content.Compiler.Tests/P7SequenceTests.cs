using Eota.Kernel.Commands;
using Eota.Kernel.Effects;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Content.Compiler.Tests;

public sealed class P7SequenceTests
{
    [Fact]
    public void SequenceReadsCommittedResultsAndEveryCausalCheckpointResumesIdentically()
    {
        var card = EffectRuntimeTests.Minion("SEQUENCE", """
            [{"id":"sequence","trigger":{"kind":"selfEntered"},"body":{"kind":"sequence","steps":[
              {"kind":"damage","target":{"kind":"self"},"amount":2},
              {"kind":"damage","target":{"kind":"enemyHero"},"amount":"previous.scalar + source.health"},
              {"kind":"loop","count":2,"body":{"kind":"damage","target":{"kind":"enemyHero"},"amount":1}}]}}]
            """, health: 5);
        var ready = Ready(EffectRuntimeTests.Plan(EffectRuntimeTests.Create([card]), PlayerId.One, "TEST-P6-SEQUENCE", 0));
        var complete = TurnResolver.ResolveReadyTurn(ready);
        Assert.Equal(23, complete.State.Players[1].HeroHealth);
        Assert.True(complete.CompletedTurn);
        Assert.Null(complete.State.Execution);
        Assert.Contains(complete.Frames, frame => frame.State.Execution is { ReceiptLedger.IsEmpty: false });
        for (var index = 0; index < complete.Frames.Length - 1; index++)
        {
            var checkpoint = complete.Frames[index].State;
            var resumed = TurnResolver.ResolveReadyTurn(checkpoint with { });
            Assert.Equal(MatchStateHasher.Compute(complete.State), MatchStateHasher.Compute(resumed.State));
            Assert.Equal(complete.Frames.Skip(index + 1).Select(frame => frame.AfterStateHash), resumed.Frames.Select(frame => frame.AfterStateHash));
            Assert.Equal(complete.Frames.Skip(index + 1).Select(frame => frame.Receipts.Hash), resumed.Frames.Select(frame => frame.Receipts.Hash));
        }
        var inProgress = complete.Frames.First(frame => frame.State.Execution is { ReceiptLedger.IsEmpty: false }).State;
        Assert.NotEqual(MatchStateHasher.Compute(inProgress), MatchStateHasher.Compute(inProgress with { Execution = inProgress.Execution! with { Ready = [] } }));
    }

    [Fact]
    public void ParallelSequencesJoinBeforeTheOuterContinuation()
    {
        var card = EffectRuntimeTests.Minion("JOIN", """
            [{"id":"entry","trigger":{"kind":"selfEntered"},"body":{"kind":"sequence","steps":[
              {"kind":"parallel","children":[
                {"kind":"sequence","steps":[
                  {"kind":"damage","target":{"kind":"enemyHero"},"amount":1},
                  {"kind":"damage","target":{"kind":"enemyHero"},"amount":2}]},
                {"kind":"damage","target":{"kind":"enemyHero"},"amount":3}]},
              {"kind":"damage","target":{"kind":"enemyHero"},"amount":"previous.scalar"}]}}]
            """);
        var turn = EffectRuntimeTests.Resolve(EffectRuntimeTests.Plan(EffectRuntimeTests.Create([card]), PlayerId.One, "TEST-P6-JOIN", 0));
        Assert.Equal(19, turn.State.Players[1].HeroHealth);
        var hits = turn.Frames.Where(frame => frame.Events.Events.Any(value => value.Kind == DomainEventKind.HeroHealthChanged)).ToArray();
        Assert.Equal(3, hits.Length);
        Assert.Equal(4, hits[0].Events.Events.Single(value => value.Kind == DomainEventKind.HeroHealthChanged).PreviousValue
            - hits[0].Events.Events.Single(value => value.Kind == DomainEventKind.HeroHealthChanged).CurrentValue);
    }

    [Fact]
    public void RejectedStepStopsItsSequenceButNoOpContinuesIndependentWork()
    {
        var rejected = EffectRuntimeTests.Minion("REJECT", """
            [{"id":"entry","trigger":{"kind":"selfEntered"},"body":{"kind":"sequence","steps":[
              {"kind":"replace","target":{"kind":"self"},"prototype":"TEST-P6-REJECT"},
              {"kind":"damage","target":{"kind":"enemyHero"},"amount":10}]}},
             {"id":"empty","trigger":{"kind":"selfEntered"},"body":{"kind":"sequence","steps":[
              {"kind":"damage","target":{"kind":"enemyMinions"},"amount":1},
              {"kind":"damage","target":{"kind":"enemyHero"},"amount":1}]}}]
            """);
        var turn = EffectRuntimeTests.Resolve(EffectRuntimeTests.Plan(EffectRuntimeTests.Create([rejected]), PlayerId.One, "TEST-P6-REJECT", 0));
        Assert.True(turn.CompletedTurn);
        Assert.Equal(29, turn.State.Players[1].HeroHealth);
    }

    [Fact]
    public void PreviousAndLoopVariablesAreRejectedOutsideTheirScopes()
    {
        foreach (var expression in new[] { "previous.scalar", "loop.index" })
        {
            var json = EffectRuntimeTests.Minion("BAD-SCOPE", """
                [{"id":"entry","trigger":{"kind":"selfEntered"},"body":{"kind":"damage","target":{"kind":"enemyHero"},"amount":"EXPRESSION"}}]
                """.Replace("EXPRESSION", expression, StringComparison.Ordinal));
            Assert.False(CardContentCompiler.Compile([new ContentSourceDocument("bad.json", json)]).IsSuccess);
        }
    }

    [Fact]
    public void LoopWhileIsCheckedBetweenIterationsAndNestedIndicesStartAtZero()
    {
        var card = EffectRuntimeTests.Minion("WHILE", """
            [{"id":"while","trigger":{"kind":"selfEntered"},"body":{"kind":"loop","count":3,
              "while":{"kind":"compare","left":"owner.health","operator":"greater","right":28},
              "body":{"kind":"sequence","steps":[{"kind":"damage","target":{"kind":"friendlyHero"},"amount":1},
                {"kind":"damage","target":{"kind":"enemyHero"},"amount":1}]}}},
             {"id":"nested","trigger":{"kind":"turnEnd"},"body":{"kind":"loop","count":2,
              "body":{"kind":"loop","count":2,"body":{"kind":"damage","target":{"kind":"enemyHero"},"amount":"loop.index + 1"}}}}]
            """);
        var turn = EffectRuntimeTests.Resolve(EffectRuntimeTests.Plan(EffectRuntimeTests.Create([card]), PlayerId.One, "TEST-P6-WHILE", 0));
        Assert.True(turn.CompletedTurn);
        Assert.Equal(28, turn.State.Players[0].HeroHealth);
        Assert.Equal(22, turn.State.Players[1].HeroHealth);
    }

    private static MatchState Ready(MatchState state)
    {
        foreach (var player in new[] { PlayerId.One, PlayerId.Two })
        { state = MatchCommandProcessor.Accept(state, new SubmitTurnCommand(player, state.Players.Single(value => value.Id == player).CommandRevision)).State; }
        return state;
    }
}
