using System.Text.Json.Nodes;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Content.Compiler.Tests;

public sealed class ReturnCapacityDeathTests
{
    [Theory]
    [InlineData("minion")]
    [InlineData("finite")]
    [InlineData("permanent")]
    public void FailedReturnsTriggerDeathAndLeaveAndRemovedObserversSeeEachOther(string kind)
    {
        const string effects = """
            [{"id":"return","trigger":{"kind":"selfEntered"},"body":{"kind":"sequence","steps":[
               {"kind":"generate","target":{"kind":"friendlyHero"},"prototype":"TEST-P6-TOKEN","count":1},
               {"kind":"return","target":{"kind":"self"}},
               {"kind":"damage","target":{"kind":"enemyHero"},"amount":1000}]}},
             {"id":"death","trigger":{"kind":"selfDied"},"body":{"kind":"damage","target":{"kind":"enemyHero"},"amount":2}},
             {"id":"leave","trigger":{"kind":"selfLeft"},"body":{"kind":"damage","target":{"kind":"enemyHero"},"amount":1}},
             {"id":"observe","trigger":{"kind":"enemyDied","scope":"all","subjectType":"SUBJECT"},
              "body":{"kind":"damage","target":{"kind":"enemyHero"},"amount":4}}]
            """;
        var source = JsonNode.Parse(EffectRuntimeTests.Minion("RETURN", effects.Replace("SUBJECT", kind == "minion" ? "minion" : "field", StringComparison.Ordinal), 2, 3))!.AsObject();
        if (kind != "minion")
        {
            source["kind"] = "field"; source.Remove("attack"); source.Remove("health");
            source["lifetime"] = kind == "finite" ? new JsonObject { ["kind"] = "finite", ["energy"] = 3 } : new JsonObject { ["kind"] = "permanent" };
        }
        var state = EffectRuntimeTests.Create([source.ToJsonString(), EffectRuntimeTests.Minion("TOKEN", "[]")]);
        foreach (var player in new[] { PlayerId.One, PlayerId.Two })
        { state = EffectRuntimeTests.Plan(state, player, "TEST-P6-RETURN", 0); }
        var turn = EffectRuntimeTests.Resolve(state);
        Assert.True(turn.CompletedTurn); Assert.Equal(MatchStatus.Active, turn.State.Status);
        Assert.Empty(turn.State.Entities);
        Assert.Equal(2, turn.State.Tombstones.Length);
        Assert.All(turn.State.Tombstones, tombstone => Assert.Equal(EntityRemovalReason.Death, tombstone.Reason));
        Assert.All(turn.State.Players, player =>
        {
            Assert.Equal(23, player.HeroHealth);
            Assert.Equal(2, player.Hand.Length);
            Assert.Single(player.Discard);
        });
        var deathFrame = Assert.Single(turn.Frames, frame => frame.Events.Events.Any(fact => fact.Kind == DomainEventKind.EntityDied));
        Assert.All(deathFrame.State.Players, player => Assert.Equal(30, player.HeroHealth));
        Assert.Equal(2, deathFrame.Events.Events.Count(fact => fact.Kind == DomainEventKind.EntityDied));
        Assert.Equal(2, deathFrame.Events.Events.Count(fact => fact.Kind == DomainEventKind.EntityLeft));
        Assert.Equal(2, deathFrame.Receipts.Receipts.Count(receipt => receipt.Status == IntentReceiptStatus.Rejected && receipt.DetailCode == "hand-capacity"));
        Assert.DoesNotContain(turn.Frames.SelectMany(frame => frame.Events.Events), fact => fact.Kind == DomainEventKind.CardReturned);
        Assert.DoesNotContain(turn.Frames.SelectMany(frame => frame.Receipts.Receipts), receipt => receipt.AppliedValue == 1000);
    }
}
