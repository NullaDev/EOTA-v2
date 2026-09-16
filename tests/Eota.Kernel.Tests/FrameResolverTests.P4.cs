using System.Collections.Immutable;
using Eota.Kernel.Commands;
using Eota.Kernel.Content;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Kernel.Tests;

public sealed partial class FrameResolverTests
{
    [Fact]
    public void ReplayCommandsResolvesMultipleTurnsAndReproducesEveryFrame()
    {
        var initial = CreateMatch();
        var card = initial.CardInstances.First(value =>
            value.OwnerId == PlayerId.One && value.CurrentPrototypeId == MinionId);
        AuthoritativeCommand[] commands =
        [
            new PlanCardCommand(PlayerId.One, 0, card.Id, new LaneId(0)),
            new SubmitTurnCommand(PlayerId.One, 1),
            new SubmitTurnCommand(PlayerId.Two, 0),
            new SubmitTurnCommand(PlayerId.Two, 1),
            new SubmitTurnCommand(PlayerId.One, 2)
        ];

        var first = MatchReplay.ReplayCommands(initial, commands);
        var replay = MatchReplay.ReplayCommands(initial, first.State.CommandLog.Select(value => value.Command));

        Assert.True(first.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal(3, first.State.Turn);
        Assert.Equal(29, first.State.Players.Single(value => value.Id == PlayerId.Two).HeroHealth);
        Assert.Equal(MatchStateHasher.Compute(first.State), MatchStateHasher.Compute(replay.State));
        Assert.Equal(first.Frames.Select(FrameHashes), replay.Frames.Select(FrameHashes));
    }

    [Fact]
    public void ReplayRejectsTheFirstInvalidCommandWithoutConsumingLaterCommands()
    {
        var result = MatchReplay.ReplayCommands(CreateMatch(), InvalidCommands());

        Assert.False(result.IsSuccess);
        Assert.Equal("commands[0]", Assert.Single(result.Errors).Parameter);
        Assert.Empty(result.State.CommandLog);
        Assert.Empty(result.Frames);

        static IEnumerable<AuthoritativeCommand> InvalidCommands()
        {
            yield return new SubmitTurnCommand(PlayerId.One, 99);
            throw new InvalidOperationException("Replay must stop at the rejected command.");
        }
    }

    [Fact]
    public void ReorderingStateCollectionsAndSystemIntentsPreservesAllTurnHashes()
    {
        var state = PutMinion(CreateMatch(), PlayerId.One, 3, 5, 5, new LaneId(1),
            [new MinionKeywordDefinition(MinionKeywordKind.Pursuit), new(MinionKeywordKind.Swift)]);
        state = PutMinion(state, PlayerId.Two, 2, 5, 5, new LaneId(0));
        state = PutField(state, PlayerId.One, FiniteFieldId);
        var fast = state.CardInstances.First(value =>
            value.OwnerId == PlayerId.One && value.CurrentPrototypeId == FastSpellId);
        var slow = state.CardInstances.First(value =>
            value.OwnerId == PlayerId.Two && value.CurrentPrototypeId == SlowSpellId);
        var deployment = state.CardInstances.First(value => value.OwnerId == PlayerId.Two
            && value.CurrentPrototypeId == MinionId && value.Zone == CardZone.Hand);
        var field = state.CardInstances.First(value => value.OwnerId == PlayerId.Two
            && value.CurrentPrototypeId == PermanentFieldId && value.Zone == CardZone.Hand);
        state = MatchCommandProcessor.Accept(state, new PlanSpellCommand(PlayerId.One, 0, fast.Id, null)).State;
        state = MatchCommandProcessor.Accept(state, new PlanSpellCommand(PlayerId.Two, 0, slow.Id, null)).State;
        state = MatchCommandProcessor.Accept(state, new PlanCardCommand(PlayerId.Two, 1, deployment.Id, new LaneId(4))).State;
        state = MatchCommandProcessor.Accept(state, new PlanCardCommand(PlayerId.Two, 2, field.Id, new LaneId(5))).State;
        state = MatchCommandProcessor.Accept(state, new SubmitTurnCommand(PlayerId.One, 1)).State;
        state = MatchCommandProcessor.Accept(state, new SubmitTurnCommand(PlayerId.Two, 3)).State;
        var reordered = state with
        {
            Players = state.Players.Reverse().ToImmutableArray(),
            Lanes = state.Lanes.Reverse().ToImmutableArray(),
            Entities = state.Entities.Reverse().ToImmutableArray(),
            CardInstances = state.CardInstances.Reverse().ToImmutableArray()
        };

        var first = TurnResolver.ResolveReadyTurn(state);
        var second = TurnResolver.ResolveReadyTurn(reordered);

        Assert.True(first.CompletedTurn);
        Assert.Equal(first.Frames.Select(FrameHashes), second.Frames.Select(FrameHashes));
        var before = state;
        foreach (var frame in first.Frames)
        {
            var intents = frame.Plan.Groups.SelectMany(value => value.Intents).ToArray();
            var forward = FrameResolver.Resolve(before, intents);
            var backward = FrameResolver.Resolve(before, intents.Reverse());
            Assert.Equal(FrameHashes(forward), FrameHashes(backward));
            Assert.Equal(frame.Receipts.Hash, backward.Receipts.Hash);
            Assert.Equal(frame.Events.Hash, backward.Events.Hash);
            Assert.Equal(intents.Length, frame.Receipts.Receipts.Length);
            before = frame.State;
        }
    }

    [Fact]
    public void AllNineStagesHaveCommittedBoundaries()
    {
        var state = CreateMatch();
        state = MatchCommandProcessor.Accept(state, new SubmitTurnCommand(PlayerId.One, 0)).State;
        state = MatchCommandProcessor.Accept(state, new SubmitTurnCommand(PlayerId.Two, 0)).State;

        var result = TurnResolver.ResolveReadyTurn(state);

        MatchStage[] expected =
        [
            MatchStage.Deployment, MatchStage.EntryEffects, MatchStage.FastSpells,
            MatchStage.Movement, MatchStage.PreCombatCharge, MatchStage.Combat,
            MatchStage.SlowSpells, MatchStage.EndTurnEffects, MatchStage.Cleanup, MatchStage.Planning
        ];
        Assert.Equal(expected, result.Frames.SelectMany(value => value.Events.Events)
            .Where(value => value.Kind == DomainEventKind.MatchStageChanged)
            .Select(value => (MatchStage)value.CurrentValue!.Value));
    }

    [Fact]
    public void LethalCombatDiscardsSlowSpellsCleanupAndNewTurn()
    {
        var state = PutMinion(CreateMatch(), PlayerId.One, 30, 2, 2, new LaneId(0),
            [new MinionKeywordDefinition(MinionKeywordKind.Swift)]);
        state = PutField(state, PlayerId.Two, FiniteFieldId);
        var slow = state.CardInstances.First(value =>
            value.OwnerId == PlayerId.Two && value.CurrentPrototypeId == SlowSpellId);
        state = MatchCommandProcessor.Accept(state, new PlanSpellCommand(PlayerId.Two, 0, slow.Id, null)).State;
        state = MatchCommandProcessor.Accept(state, new SubmitTurnCommand(PlayerId.One, 0)).State;
        state = MatchCommandProcessor.Accept(state, new SubmitTurnCommand(PlayerId.Two, 1)).State;

        var result = TurnResolver.ResolveReadyTurn(state);

        Assert.False(result.CompletedTurn);
        Assert.Equal(MatchOutcome.PlayerOneWon, result.State.Outcome);
        Assert.Equal(1, result.State.Turn);
        Assert.Equal(CardZone.Planning, GetCard(result.State, slow.Id).Zone);
        Assert.Single(result.State.Entities.OfType<FieldEntityState>());
        Assert.DoesNotContain(result.Frames.SelectMany(value => value.Events.Events), value =>
            value.Kind is DomainEventKind.SpellResolved or DomainEventKind.TurnStarted);
        Assert.Equal(MatchStage.Combat, result.Frames[^1].State.Stage);
    }

    [Fact]
    public void SlowTwoBlocksBothAttackAndDefenseForExactlyTwoCombats()
    {
        var state = PutMinion(CreateMatch(), PlayerId.One, 4, 5, 5, new LaneId(0),
            [new MinionKeywordDefinition(MinionKeywordKind.Slow, 2), new(MinionKeywordKind.Swift)]);
        state = PutMinion(state, PlayerId.Two, 1, 5, 5, new LaneId(0),
            [new MinionKeywordDefinition(MinionKeywordKind.Swift)]);
        for (var turn = 0; turn < 3; turn++)
        {
            foreach (var playerId in new[] { PlayerId.One, PlayerId.Two })
            {
                var revision = state.Players.Single(value => value.Id == playerId).CommandRevision;
                state = MatchCommandProcessor.Accept(state, new SubmitTurnCommand(playerId, revision)).State;
            }

            state = TurnResolver.ResolveReadyTurn(state).State;
            var minion = state.Entities.OfType<MinionEntityState>().Single(value => value.ControllerId == PlayerId.One);
            Assert.Equal(Math.Max(0, 1 - turn), minion.SlowTurnsRemaining);
        }

        Assert.Equal(28, state.Players.Single(value => value.Id == PlayerId.One).HeroHealth);
        Assert.Equal(1, state.Entities.OfType<MinionEntityState>().Single(value => value.ControllerId == PlayerId.Two).CurrentHealth);
    }

    private static (string State, string Receipts, string Events) FrameHashes(FrameTransition frame) =>
        (frame.AfterStateHash.ToString(), frame.Receipts.Hash.ToString(), frame.Events.Hash.ToString());
}
