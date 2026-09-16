using System.Text.Json;
using Eota.Kernel.Commands;
using Eota.Kernel.Content;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Content.Compiler.Tests;

public sealed class P4ReplayFixtureTests
{
    [Theory]
    [InlineData("P4Match", 4, 73, MatchStatus.Finished, MatchOutcome.PlayerOneWon)]
    [InlineData("P4CenterMovement", 2, 20, MatchStatus.Active, MatchOutcome.None)]
    public void CompiledNoEffectReplayReproducesEveryPinnedFrame(
        string fixtureName, int expectedTurn, int expectedFrameCount, MatchStatus expectedStatus, MatchOutcome expectedOutcome)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Eota.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var fixture = Path.Combine(directory.FullName, "tests", "Fixtures", fixtureName);
        var cardDirectory = Path.Combine(directory.FullName, "tests", "Fixtures", "P4Match", "Cards");
        var content = CardContentCompiler.Compile(Directory.EnumerateFiles(cardDirectory, "*.json")
            .Select(path => new ContentSourceDocument(Path.GetFileName(path), File.ReadAllText(path))));
        var protocol = ProtocolCompiler.Compile("protocol-v0.json", File.ReadAllText(Path.Combine(fixture, "protocol-v0.json")));
        Assert.True(content.IsSuccess);
        Assert.True(protocol.IsSuccess);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture, "match.replay.json")));
        using var script = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture, "commands.json")));
        var replay = document.RootElement;
        var header = replay.GetProperty("initial");
        var creation = MatchFactory.Create(new MatchCreationRequest(protocol.Protocol!, content.Content!.Rules,
            header.GetProperty("matchSeed").GetUInt64(),
            ReadDeck(header.GetProperty("playerOneDeck")), ReadDeck(header.GetProperty("playerTwoDeck"))));
        Assert.True(creation.IsSuccess);
        var initial = Assert.IsType<MatchState>(creation.State);
        Assert.Equal(header.GetProperty("protocolHash").GetString(), protocol.Protocol!.Hash.ToString());
        Assert.Equal(header.GetProperty("ruleContentHash").GetString(), content.Content.Rules.Hash.ToString());
        Assert.Equal(header.GetProperty("expectedInitialStateHash").GetString(), MatchStateHasher.Compute(initial).ToString());
        Assert.Equal(header.GetProperty("expectedRngSampleCount").GetUInt64(), initial.RuleRng.SampleCount);

        var result = MatchReplay.ReplayCommands(initial, replay.GetProperty("commands").EnumerateArray().Select(ReadCommand));
        var fromScript = MatchReplay.ReplayCommands(initial, script.RootElement.EnumerateArray().Select(ReadCommand));

        Assert.True(result.IsSuccess);
        Assert.True(fromScript.IsSuccess);
        Assert.Equal(MatchStateHasher.Compute(result.State), MatchStateHasher.Compute(fromScript.State));
        Assert.Equal(expectedStatus, result.State.Status);
        Assert.Equal(expectedOutcome, result.State.Outcome);
        Assert.Equal(expectedTurn, result.State.Turn);
        Assert.Equal(expectedFrameCount, result.Frames.Length);
        Assert.Equal(replay.GetProperty("expectedFinalStateHash").GetString(), MatchStateHasher.Compute(result.State).ToString());
        var expectedFrames = replay.GetProperty("expectedFrames").EnumerateArray().ToArray();
        Assert.Equal(expectedFrames.Length, result.Frames.Length);
        for (var index = 0; index < expectedFrames.Length; index++)
        {
            var expected = expectedFrames[index];
            var actual = result.Frames[index];
            Assert.Equal(expected.GetProperty("frameId").GetUInt64(), actual.Plan.FrameId.Value);
            Assert.Equal(expected.GetProperty("beforeStateHash").GetString(), actual.BeforeStateHash.ToString());
            Assert.Equal(expected.GetProperty("afterStateHash").GetString(), actual.AfterStateHash.ToString());
            Assert.Equal(expected.GetProperty("receiptHash").GetString(), actual.Receipts.Hash.ToString());
            Assert.Equal(expected.GetProperty("eventHash").GetString(), actual.Events.Hash.ToString());
            Assert.Equal(expected.GetProperty("rngSampleCount").GetUInt64(), actual.State.RuleRng.SampleCount);
            var choices = expected.GetProperty("movementRandomChoices").EnumerateArray().ToArray();
            Assert.Equal(choices.Length, actual.Plan.MovementRandomChoices.Length);
            for (var choiceIndex = 0; choiceIndex < choices.Length; choiceIndex++)
            {
                var choice = choices[choiceIndex];
                var actualChoice = actual.Plan.MovementRandomChoices[choiceIndex];
                Assert.Equal(choice.GetProperty("intentId").GetUInt64(), actualChoice.IntentId.Value);
                Assert.Equal(choice.GetProperty("entityId").GetUInt64(), actualChoice.EntityId.Value);
                Assert.Equal(choice.GetProperty("leftLaneId").GetInt32(), actualChoice.Candidates[0].Value);
                Assert.Equal(choice.GetProperty("rightLaneId").GetInt32(), actualChoice.Candidates[1].Value);
                Assert.Equal(choice.GetProperty("selectedLaneId").GetInt32(), actualChoice.SelectedLaneId.Value);
                Assert.Equal(choice.GetProperty("sampleCountBefore").GetUInt64(), actualChoice.SampleCountBefore);
                Assert.Equal(choice.GetProperty("samplesConsumed").GetUInt64(), actualChoice.SamplesConsumed);
            }
        }

        var events = result.Frames.SelectMany(value => value.Events.Events).ToArray();
        Assert.Contains(events, value => value.Kind == DomainEventKind.EntityMoved);
        if (fixtureName == "P4Match")
        {
            Assert.Equal(2, events.Count(value => value.Kind == DomainEventKind.SpellResolved));
            Assert.Equal(2, events.Count(value => value.Kind == DomainEventKind.MinionSlowChanged));
            Assert.Equal(2, result.State.Tombstones.Length);
            Assert.Equal(3, events.Count(value => value.Kind == DomainEventKind.TurnStarted));
        }
        else
        {
            Assert.Single(result.Frames.SelectMany(value => value.Plan.MovementRandomChoices));
            Assert.Equal(initial.RuleRng.SampleCount + 1, result.State.RuleRng.SampleCount);
            Assert.Equal(MatchStage.Planning, result.State.Stage);
        }
    }

    private static DeckDefinition ReadDeck(JsonElement value) => DeckDefinition.Create(Profession.Neutral,
        value.GetProperty("cards").EnumerateArray().Select(card => new DeckEntry(
            CardPrototypeId.Parse(card.GetProperty("id").GetString()!), card.GetProperty("copies").GetInt32())));

    private static AuthoritativeCommand ReadCommand(JsonElement value)
    {
        var player = new PlayerId(value.GetProperty("playerId").GetByte());
        var revision = value.GetProperty("expectedPlayerRevision").GetUInt64();
        return value.GetProperty("kind").GetString() switch
        {
            "planCard" => new PlanCardCommand(player, revision,
                new CardInstanceId(value.GetProperty("cardInstanceId").GetUInt64()),
                new LaneId(value.GetProperty("laneId").GetInt32())),
            "planSpell" => new PlanSpellCommand(player, revision,
                new CardInstanceId(value.GetProperty("cardInstanceId").GetUInt64()), null),
            "submitTurn" => new SubmitTurnCommand(player, revision),
            _ => throw new InvalidOperationException("Unsupported command in the P4 fixture.")
        };
    }
}
