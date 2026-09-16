using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eota.Kernel.Content;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;

namespace Eota.Content.Compiler.Tests;

public sealed class InitialReplayFixtureTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    [Fact]
    public void CheckedInReplayRebuildsThePinnedInitialState()
    {
        var fixtureDirectory = Path.Combine(FindRepositoryRoot(), "tests", "Fixtures", "InitialReplay");
        var cardDirectory = Path.Combine(fixtureDirectory, "Cards");
        var contentResult = CardContentCompiler.Compile(
            Directory.EnumerateFiles(cardDirectory, "*.json")
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => new ContentSourceDocument(
                    Path.GetFileName(path),
                    File.ReadAllText(path, Encoding.UTF8))));
        var protocolResult = ProtocolCompiler.Compile(
            "protocol-v0.json",
            File.ReadAllText(Path.Combine(fixtureDirectory, "protocol-v0.json"), Encoding.UTF8));
        var replay = JsonSerializer.Deserialize<InitialReplayFixture>(
            File.ReadAllText(Path.Combine(fixtureDirectory, "initial.replay.json"), Encoding.UTF8),
            JsonOptions);

        Assert.True(contentResult.IsSuccess);
        Assert.True(protocolResult.IsSuccess);
        Assert.NotNull(replay);
        var content = Assert.IsType<CompiledContentBundle>(contentResult.Content);
        var protocol = Assert.IsType<Eota.Kernel.Protocols.CompiledGameProtocol>(protocolResult.Protocol);

        var creationResult = MatchFactory.Create(new MatchCreationRequest(
            protocol,
            content.Rules,
            replay.MatchSeed,
            ToDeck(replay.PlayerOneDeck),
            ToDeck(replay.PlayerTwoDeck)));

        Assert.True(creationResult.IsSuccess);
        var state = Assert.IsType<MatchState>(creationResult.State);
        Assert.Equal(replay.ProtocolHash, protocol.Hash.ToString());
        Assert.Equal(replay.RuleContentHash, content.Rules.Hash.ToString());
        Assert.Equal(replay.ExpectedInitialStateHash, MatchStateHasher.Compute(state).ToString());
        Assert.Equal(replay.ExpectedRngSampleCount, state.RuleRng.SampleCount);
    }

    private static DeckDefinition ToDeck(ReplayDeck deck) => DeckDefinition.Create(
        deck.Profession,
        deck.Cards.Select(entry => new DeckEntry(CardPrototypeId.Parse(entry.Id), entry.Copies)));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Eota.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private sealed record InitialReplayFixture(
        int FormatVersion,
        string ProtocolId,
        int ProtocolVersion,
        string ProtocolHash,
        string ContentSchemaVersion,
        string RuleContentHash,
        ulong MatchSeed,
        ReplayDeck PlayerOneDeck,
        ReplayDeck PlayerTwoDeck,
        string ExpectedInitialStateHash,
        ulong ExpectedRngSampleCount);

    private sealed record ReplayDeck(Profession Profession, ImmutableArray<ReplayDeckEntry> Cards);

    private sealed record ReplayDeckEntry(string Id, int Copies);
}
