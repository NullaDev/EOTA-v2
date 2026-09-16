using System.Text.Json.Nodes;
using Eota.Kernel.Commands;
using Eota.Kernel.Content;
using Eota.Kernel.Effects;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;
using Eota.Server.Infrastructure;

namespace Eota.Server.IntegrationTests;

public sealed class P7CheckpointTests
{
    [Fact]
    public void SerializedCheckpointsResumeEveryFrameIncludingFrozenSourcesAndForkJoin()
    {
        var request = Fixture.Load();
        var entry = new EffectDefinition("entry", new EffectTrigger(EffectTriggerKind.SelfEntered), null,
            new SequenceEffect([
                new ParallelEffect([
                    new EmitEffect(EffectAction.Damage, new EffectSelector(SelectorKind.EnemyHero), new ConstantExpression(1)),
                    new SequenceEffect([
                        new EmitEffect(EffectAction.Damage, new EffectSelector(SelectorKind.Self), new ConstantExpression(1)),
                        new LifecycleEffect(LifecycleOperation.Return, new EffectSelector(SelectorKind.Self), null)])]),
                new LoopEffect(new ConstantExpression(2),
                    new EmitEffect(EffectAction.Damage, new EffectSelector(SelectorKind.EnemyHero), new VariableExpression("source.attack"))) ]));
        var rules = RuleContentPack.Create(request.Content.SchemaVersion, request.Content.Cards.Select(card =>
            card is MinionCardDefinition ? card with { Effects = [entry] } : card));
        var initial = MatchFactory.Create(request with { Content = rules }).State!;
        var instance = initial.CardInstances.First(card => card.OwnerId == PlayerId.One && card.Zone == CardZone.Hand
            && rules.Cards.Single(value => value.Id == card.CurrentPrototypeId) is MinionCardDefinition);
        var planned = MatchCommandProcessor.Accept(initial, new PlanCardCommand(PlayerId.One, 0, instance.Id, new LaneId(0)));
        Assert.True(planned.IsAccepted);
        var state = planned.State;
        foreach (var player in state.Players)
        { state = MatchCommandProcessor.Accept(state, new SubmitTurnCommand(player.Id, player.CommandRevision)).State; }
        var complete = TurnResolver.ResolveReadyTurn(state);
        Assert.True(complete.CompletedTurn);
        Assert.Contains(complete.Frames, frame => frame.State.Execution?.Ready.Any(program => program.Invocation.Source.FrozenEntity is not null) == true);
        var checkpoints = new[] { state }.Concat(complete.Frames.Select(frame => frame.State)).ToArray();
        for (var index = 0; index < checkpoints.Length; index++)
        {
            var original = checkpoints[index];
            var restored = MatchCheckpointCodec.Decode(MatchCheckpointCodec.Encode(original), original.Protocol, original.Content);
            Assert.Equal(MatchStateHasher.Compute(original), MatchStateHasher.Compute(restored));
            if (original.Execution is null && original.Stage != MatchStage.ReadyToResolve) { continue; }
            var resumed = TurnResolver.ResolveReadyTurn(restored);
            Assert.Equal(complete.Frames.Skip(index).Select(frame => frame.AfterStateHash), resumed.Frames.Select(frame => frame.AfterStateHash));
            Assert.Equal(complete.Frames.Skip(index).Select(frame => frame.Events.Hash), resumed.Frames.Select(frame => frame.Events.Hash));
            Assert.Equal(complete.Frames.Skip(index).Select(frame => frame.Receipts.Hash), resumed.Frames.Select(frame => frame.Receipts.Hash));
        }
    }

    [Fact]
    public void CheckpointsRejectTamperingSchemaAndDifferentRules()
    {
        var state = MatchFactory.Create(Fixture.Load()).State!;
        var bytes = MatchCheckpointCodec.Encode(state);
        var json = JsonNode.Parse(bytes)!;
        json["State"]!["Turn"] = state.Turn + 1;
        Assert.Throws<InvalidDataException>(() => MatchCheckpointCodec.Decode(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()), state.Protocol, state.Content));
        json = JsonNode.Parse(bytes)!;
        json["SchemaVersion"] = 999;
        Assert.Throws<InvalidDataException>(() => MatchCheckpointCodec.Decode(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()), state.Protocol, state.Content));
        var protocol = Eota.Kernel.Protocols.CompiledGameProtocol.Compile(state.Protocol.Definition with { InitialHeroHealth = 31 });
        Assert.Throws<InvalidDataException>(() => MatchCheckpointCodec.Decode(bytes, protocol, state.Content));
    }
}
