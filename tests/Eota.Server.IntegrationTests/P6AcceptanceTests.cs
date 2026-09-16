using System.Text.Json;
using Eota.Client.Core;
using Eota.Kernel.Commands;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;
using Eota.Server.Infrastructure;
using Eota.Transport.Contracts;

namespace Eota.Server.IntegrationTests;

public sealed class P6AcceptanceTests
{
    [Theory]
    [InlineData(false, "P6Effects", 61)]
    [InlineData(true, "P6Effects", 61)]
    [InlineData(false, "P6Set", 21)]
    [InlineData(true, "P6Set", 21)]
    [InlineData(false, "P7Complex", 76)]
    [InlineData(true, "P7Complex", 76)]
    public async Task CompiledEffectsReplayThroughBothClientsAndSpectator(bool remote, string fixtureName, int frameCount)
    {
        var path = Path.Combine(Fixture.Root, "tests", "Fixtures", fixtureName);
        await using var match = await TestMatch.StartAsync(remote, path);
        await using var one = new GameClient(await match.ConnectAsync(Audience.PlayerOne));
        await using var two = new GameClient(await match.ConnectAsync(Audience.PlayerTwo));
        await using var spectator = new GameClient(await match.ConnectAsync(Audience.Spectator));
        using var commands = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "commands.json")));
        foreach (var command in commands.RootElement.EnumerateArray())
        {
            var client = command.GetProperty("playerId").GetInt32() == 0 ? one : two;
            var ack = await client.SubmitAsync(Fixture.ReadPayload(command), command.GetProperty("expectedPlayerRevision").GetUInt64());
            Assert.True(ack.Accepted, ack.Code);
        }
        using var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "match.replay.json")));
        Assert.Equal(golden.RootElement.GetProperty("expectedFinalStateHash").GetString(), (await match.Actor.GetDiagnosticsAsync()).StateHash);
        foreach (var client in new[] { one, two, spectator })
        {
            await client.SynchronizeAsync();
            Assert.False(client.Store.NeedsSnapshot);
            Assert.Equal("Planning", client.Store.View!.Stage);
            Assert.Equal(ObserverViewHasher.Compute(client.Store.View), client.Store.ObserverViewHash);
            var frames = client.Store.DrainPresentationFrames();
            Assert.Equal(frameCount, frames.Length);
            var wire = ContractJson.Serialize(frames);
            foreach (var secret in new[] { "intentReceipt", "detailCode", "ruleRng", "sampleCount", "numericSetChoices", "tombstone" })
            { Assert.DoesNotContain(secret, wire, StringComparison.OrdinalIgnoreCase); }
            if (fixtureName == "P6Effects")
            {
                Assert.Empty(client.Store.View.Entities);
                Assert.All(client.Store.View.Players, player => Assert.Equal(6, player.Discard.Length));
                Assert.Contains("EntityDamaged", wire, StringComparison.Ordinal);
                Assert.Contains("EntityStatsChanged", wire, StringComparison.Ordinal);
                Assert.Contains("SpellResolved", wire, StringComparison.Ordinal);
            }
            else if (fixtureName == "P6Set") { Assert.Contains("HeroMaximumHealthChanged", wire, StringComparison.Ordinal); }
            else
            {
                foreach (var fact in new[] { "EntityHealthLost", "AttachedEffectsChanged", "CardReturned", "CardGenerated", "EntityBanished", "CardModified" })
                {
                    if (fact == "CardModified" && client == spectator) { continue; }
                    if (fact == "CardModified" && client == two) { continue; }
                    Assert.Contains(fact, wire, StringComparison.Ordinal);
                }
            }
        }
        Assert.Null(spectator.Store.View!.Private);
        Assert.Equal(0, one.Store.View!.Private!.NextTurnCost);
        Assert.Equal(0, two.Store.View!.Private!.NextTurnCost);
    }

    [Theory]
    [InlineData("P6Effects")]
    [InlineData("P6Set")]
    [InlineData("P7Complex")]
    [InlineData("P9LaneStatuses")]
    public void EveryKernelFrameAndSetDecisionMatchesThePinnedReplay(string fixtureName)
    {
        var path = Path.Combine(Fixture.Root, "tests", "Fixtures", fixtureName);
        using var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "match.replay.json")));
        var initial = MatchFactory.Create(FileMatchLoader.Load(path, 123456789)).State!;
        var commands = golden.RootElement.GetProperty("commands").EnumerateArray().Select(ReadCommand);
        var replay = MatchReplay.ReplayCommands(initial, commands);
        Assert.True(replay.IsSuccess);
        Assert.Equal(golden.RootElement.GetProperty("expectedFinalStateHash").GetString(), MatchStateHasher.Compute(replay.State).ToString());
        var expected = golden.RootElement.GetProperty("expectedFrames").EnumerateArray().ToArray();
        Assert.Equal(expected.Length, replay.Frames.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            var frame = replay.Frames[index];
            Assert.Equal(expected[index].GetProperty("beforeStateHash").GetString(), frame.BeforeStateHash.ToString());
            Assert.Equal(expected[index].GetProperty("afterStateHash").GetString(), frame.AfterStateHash.ToString());
            Assert.Equal(expected[index].GetProperty("receiptHash").GetString(), frame.Receipts.Hash.ToString());
            Assert.Equal(expected[index].GetProperty("eventHash").GetString(), frame.Events.Hash.ToString());
            Assert.Equal(expected[index].GetProperty("rngSampleCount").GetUInt64(), frame.State.RuleRng.SampleCount);
            var choices = expected[index].GetProperty("numericSetChoices").EnumerateArray().ToArray();
            Assert.Equal(choices.Length, frame.Plan.NumericSetChoices.Length);
            for (var choiceIndex = 0; choiceIndex < choices.Length; choiceIndex++)
            {
                var choice = frame.Plan.NumericSetChoices[choiceIndex];
                Assert.Equal(choices[choiceIndex].GetProperty("candidates").EnumerateArray().Select(value => value.GetUInt64()), choice.Candidates.Select(value => value.Value));
                Assert.Equal(choices[choiceIndex].GetProperty("selectedIntentId").GetUInt64(), choice.SelectedIntentId.Value);
                Assert.Equal(choices[choiceIndex].GetProperty("sampleCountBefore").GetUInt64(), choice.SampleCountBefore);
                Assert.Equal(choices[choiceIndex].GetProperty("samplesConsumed").GetUInt64(), choice.SamplesConsumed);
            }
        }
        if (fixtureName == "P6Set") { Assert.Equal(initial.RuleRng.SampleCount + 2, replay.State.RuleRng.SampleCount); }
    }

    private static AuthoritativeCommand ReadCommand(JsonElement value)
    {
        var player = new PlayerId(value.GetProperty("playerId").GetByte());
        var revision = value.GetProperty("expectedPlayerRevision").GetUInt64();
        return value.GetProperty("kind").GetString() switch
        {
            "planCard" => new PlanCardCommand(player, revision, new CardInstanceId(value.GetProperty("cardInstanceId").GetUInt64()), new LaneId(value.GetProperty("laneId").GetInt32())),
            "planSpell" => new PlanSpellCommand(player, revision, new CardInstanceId(value.GetProperty("cardInstanceId").GetUInt64()), new LaneId(value.GetProperty("laneId").GetInt32())),
            "submitTurn" => new SubmitTurnCommand(player, revision),
            _ => throw new InvalidOperationException("Unsupported P6 replay command.")
        };
    }
}
