using Eota.Kernel.Effects;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Content.Compiler.Tests;

public sealed class P9LaneStatusTests
{
    [Fact]
    public void CompiledFreezeAppliesToWholeLaneAndExpiresAtCleanup()
    {
        var freezer = EffectRuntimeTests.Minion("FREEZER", """
            [{"id":"freeze","trigger":{"kind":"selfEntered"},"body":
              {"kind":"laneStatus","target":{"kind":"friendlyLanes","scope":"all"},"status":"frozen","duration":1}}]
            """, attack: 3);
        var enemy = EffectRuntimeTests.Minion("ENEMY", "[]", attack: 3);
        var state = EffectRuntimeTests.Plan(EffectRuntimeTests.Create([freezer, enemy]), PlayerId.One, "TEST-P6-FREEZER", 0);
        state = EffectRuntimeTests.Plan(state, PlayerId.Two, "TEST-P6-ENEMY", 0);
        var turn = EffectRuntimeTests.Resolve(state);
        Assert.True(turn.CompletedTurn);
        var frozen = turn.Frames.First(frame => frame.Events.Events.Any(value => value.Kind == DomainEventKind.LaneStatusChanged));
        Assert.All(frozen.State.Lanes, lane => Assert.True(LaneStatusRules.IsFrozen(frozen.State, lane.Id)));
        Assert.All(turn.State.Lanes, lane => Assert.Empty(lane.Statuses));
        Assert.All(turn.State.Entities.OfType<MinionEntityState>(), entity => Assert.Equal(5, entity.CurrentHealth));
        Assert.Contains(turn.Frames, frame => frame.State.Stage == MatchStage.Cleanup
            && frame.Events.Events.Any(value => value.Kind == DomainEventKind.LaneStatusChanged));
    }

    [Fact]
    public void SequenceUnlocksBeforeSummoningWhileParallelUsesLockedSnapshot()
    {
        foreach (var sequence in new[] { false, true })
        {
            var operations = """
                [{"kind":"laneStatus","target":{"kind":"friendlyLanes","scope":"all"},"status":"locked","remove":true},
                 {"kind":"summon","target":{"kind":"friendlyLanes","scope":"all"},"prototype":"TEST-P6-TOKEN"}]
                """;
            var body = sequence ? "{\"kind\":\"sequence\",\"steps\":" + operations + "}"
                : "{\"kind\":\"parallel\",\"children\":" + operations + "}";
            var caster = EffectRuntimeTests.Minion("UNLOCK", "[{\"id\":\"unlock\",\"trigger\":{\"kind\":\"turnEnd\"},\"body\":" + body + "}]");
            var token = EffectRuntimeTests.Minion("TOKEN", "[]");
            var state = EffectRuntimeTests.Resolve(EffectRuntimeTests.Plan(EffectRuntimeTests.Create([caster, token]), PlayerId.One, "TEST-P6-UNLOCK", 0)).State;
            // Clear the first turn's tokens so the second turn starts with empty summon slots.
            foreach (var entity in state.Entities.Where(value => value.LaneId.Value != 0).ToArray())
            { state = FrameResolver.Resolve(state, [new KillMinionIntent(state.NextIntentId, entity.Id)]).State; }
            foreach (var lane in state.Lanes)
            { state = FrameResolver.Resolve(state, [new ChangeLaneStatusIntent(state.NextIntentId, lane.Id, PlayerId.Two, LaneStatusKind.Locked, false, null)]).State; }
            var turn = EffectRuntimeTests.Resolve(state);
            Assert.True(turn.CompletedTurn);
            Assert.Equal(sequence ? state.Lanes.Length : 1, turn.State.Entities.Length);
            Assert.All(turn.State.Lanes, lane => Assert.False(LaneStatusRules.IsLocked(turn.State, lane.Id)));
        }
    }

    [Theory]
    [InlineData("{\"kind\":\"laneStatus\",\"target\":{\"kind\":\"self\"},\"status\":\"frozen\"}")]
    [InlineData("{\"kind\":\"laneStatus\",\"target\":{\"kind\":\"friendlyLanes\"},\"status\":\"unknown\"}")]
    [InlineData("{\"kind\":\"laneStatus\",\"target\":{\"kind\":\"friendlyLanes\"},\"status\":\"locked\",\"remove\":true,\"duration\":1}")]
    public void InvalidLaneStatusEffectsFailCompilation(string body)
    {
        var card = EffectRuntimeTests.Minion("INVALID", "[{\"id\":\"bad\",\"trigger\":{\"kind\":\"selfEntered\"},\"body\":" + body + "}]");
        Assert.False(CardContentCompiler.Compile([new ContentSourceDocument("invalid.json", card)]).IsSuccess);
    }
}
