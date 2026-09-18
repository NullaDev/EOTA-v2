using System.Security.Cryptography;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Eota.ContentCli;
using Eota.Kernel.Content;
using Eota.Kernel.Commands;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Content.Compiler.Tests;

public sealed class P8ToolTests
{
    private static string Root => Enumerable.Range(0, 10).Select(n => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, string.Join('/', Enumerable.Repeat("..", n)))))
        .First(path => File.Exists(Path.Combine(path, "Eota.sln")));

    [Fact]
    public void BaselineIdsStatsLocalizationAndEveryEffectCardHaveScenarios()
    {
        var catalog = ContentCatalog.Load(Root);
        using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "Content", "Design", "card-baseline.json")));
        using var scenarios = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "tests", "Fixtures", "P8Content", "scenarios.json")));
        Assert.Equal(282, catalog.Bundle.Rules.Cards.Length);
        var ids = baseline.RootElement.GetProperty("cards").EnumerateArray().Select(value => value.GetProperty("id").GetString()).Order(StringComparer.Ordinal);
        Assert.Equal(ids, catalog.Bundle.Rules.Cards.Select(value => value.Id.Value));
        var exercised = scenarios.RootElement.EnumerateArray().Select(value => value.GetProperty("cardId").GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.All(catalog.Bundle.Rules.Cards.Where(card => !card.Effects.IsEmpty), card => Assert.Contains(card.Id.Value, exercised));
        // Independent static-card expectations transcribed from the design text, including charge and slow.
        (string Id, string Keywords, long Charge)[] statics =
        [
            ("CORE-NEU-MIN-001", "", 0), ("CORE-NEU-MIN-004", "Skirmisher", 0), ("CORE-NEU-MIN-005", "Guard", 0), ("CORE-NEU-MIN-007", "Guard", 0),
            ("CORE-GUA-MIN-001", "Guard,Replaceable", 0), ("CORE-GUA-MIN-002", "FirstStrike,Guard", 0), ("CORE-GUA-MIN-003", "Guard,Slow:1", 0),
            ("CORE-GUA-MIN-005", "FirstStrike", 0), ("CORE-GUA-MIN-006", "Pursuit", 0), ("CORE-GUA-MIN-010", "Execute", 0), ("CORE-GUA-MIN-016", "Guard,Replaceable,Skirmisher", 0),
            ("CORE-GUA-MIN-025", "Guard,Lifesteal", 0),
            ("CORE-ART-MIN-003", "Swift", 0), ("CORE-ART-MIN-006", "Guard,Replaceable", 0), ("CORE-ART-MIN-010", "Guard", 1), ("CORE-ART-MIN-011", "", 1), ("CORE-ART-MIN-025", "Lifesteal", 1), ("CORE-ART-FLD-004", "", 2),
            ("CORE-HUN-MIN-003", "FirstStrike,Pursuit", 0), ("CORE-HUN-MIN-004", "Execute,Slow:1", 0), ("CORE-HUN-MIN-009", "Lifesteal,Skirmisher,Swift", 0), 
            ("CORE-HUN-MIN-012", "Pursuit", 0), ("CORE-HUN-MIN-015", "Guard", 0), ("CORE-HUN-MIN-026", "Execute,Pursuit", 0), ("CORE-NEU-MIN-008", "", 0),
            ("TOKEN-ARC-MIN-001", "Guard", 0), ("TOKEN-SOU-MIN-002", "Guard", 0),
            ("CORE-GUA-MIN-020", "Slow:1", 0), ("CORE-GUA-MIN-021", "FirstStrike,Slow:1", 0), ("CORE-GUA-MIN-022", "Swift", 0),
            ("CORE-ART-MIN-021", "Slow:4", 0), ("CORE-ART-MIN-022", "Slow:3", 2),
            ("CORE-HUN-MIN-021", "", 0),  ("CORE-HUN-MIN-023", "Pursuit,Swift", 0), 
            ("CORE-NEU-MIN-011", "", 0), ("CORE-NEU-MIN-013", "", 0),
            ("CORE-NEU-MIN-014", "", 0),  ("CORE-NEU-MIN-016", "FirstStrike", 0), ("CORE-NEU-MIN-017", "Slow:1", 0), ("CORE-NEU-MIN-018", "Slow:1", 0),
            ("TOKEN-ART-MIN-001", "Guard,Replaceable", 0), ("TOKEN-GUA-MIN-001", "Replaceable", 0), ("TOKEN-HUN-MIN-001", "Pursuit", 0)
        ];
        Assert.Equal(catalog.Bundle.Rules.Cards.Count(card => card.Effects.IsEmpty), statics.Length);
        foreach (var expected in statics)
        {
            var card = catalog.Bundle.Rules.Cards.Single(value => value.Id.Value == "EOTA-" + expected.Id);
            Assert.Equal(expected.Charge, card.StoredCharge);
            var keywords = card is MinionCardDefinition minion ? string.Join(',', minion.Keywords.Select(value => value.Kind == MinionKeywordKind.Slow
                ? FormattableString.Invariant($"Slow:{value.Parameter}") : value.Kind.ToString()).Order(StringComparer.Ordinal)) : "";
            Assert.Equal(expected.Keywords, keywords);
        }
        var changes = catalog.RenderBaselineDiff().Split('\n').Where(line => line.StartsWith("- EOTA-", StringComparison.Ordinal));
        Assert.Empty(changes);
        Assert.Equal(catalog.Bundle.Rules.Hash.ToString(), Convert.ToHexString(SHA256.HashData(catalog.Bundle.Rules.EncodeCanonical())).ToLowerInvariant());
        Assert.Contains("实验设计", catalog.RenderTable(), StringComparison.Ordinal);
        Assert.Contains("| 岩甲龟 | 野兽 | 3 | 5/4 |", catalog.RenderTable(), StringComparison.Ordinal);
        Assert.Contains("| 封存泰坦 | 机械 | 6 | 10/12 |", catalog.RenderTable(), StringComparison.Ordinal);
        Assert.Contains("| 以太稽查员 | — | 3 | 3/3 |", catalog.RenderTable(), StringComparison.Ordinal);
        Assert.Contains("| 荒原投石兽 | 野兽 | 8 | 5/6 |", catalog.RenderTable(), StringComparison.Ordinal);
        Assert.Equal(282, catalog.RenderTable().Split('\n').Count(line => line.Contains("| `EOTA-", StringComparison.Ordinal)));
        using var recipes = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "Content", "Source", "Art", "emoji-recipes.json")));
        var fused = recipes.RootElement.EnumerateObject().Where(value => value.Value.TryGetProperty("fusion", out _)).ToArray();
        Assert.Equal(199, fused.Length);
        Assert.Equal(83, recipes.RootElement.EnumerateObject().Count(value => !value.Value.TryGetProperty("fusion", out _)));
        Assert.All(recipes.RootElement.EnumerateObject().Where(value => !value.Value.TryGetProperty("fusion", out _)),
            value => Assert.Equal(1, new StringInfo(value.Value.GetProperty("emoji").GetString()!).LengthInTextElements));
        Assert.All(fused, value =>
        {
            Assert.Equal($"Fusions/{value.Name}.png", value.Value.GetProperty("fusion").GetString());
            Assert.StartsWith("https://www.gstatic.com/android/keyboard/emojikitchen/", value.Value.GetProperty("fusionSource").GetString(), StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(Root, "Content", "Source", "Art", "Fusions", value.Name + ".png")));
        });
    }

    [Fact]
    public void ContentBuildIsByteIdenticalAndExportedSourcesRecompileToTheSameRules()
    {
        var temp = Path.Combine(Path.GetTempPath(), "eota-content-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = ContentCatalog.Load(Root); var first = Path.Combine(temp, "first"); var second = Path.Combine(temp, "second");
            catalog.Build(first); ContentCatalog.Load(Root).Build(second);
            foreach (var file in Directory.EnumerateFiles(first, "*", SearchOption.AllDirectories))
            { Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(second, Path.GetRelativePath(first, file)))); }
            using var sources = JsonDocument.Parse(File.ReadAllText(Path.Combine(first, "cards.sources.json")));
            var compiled = CardContentCompiler.Compile(sources.RootElement.EnumerateArray().Select((value, i) => new ContentSourceDocument($"{i}.json", value.GetRawText())));
            Assert.True(compiled.IsSuccess);
            Assert.Equal(catalog.Bundle.Rules.Hash, compiled.Content!.Rules.Hash);
            var altered = sources.RootElement.EnumerateArray().Select(value => System.Text.Json.Nodes.JsonNode.Parse(value.GetRawText())!).ToArray();
            foreach (var card in altered) { card["texturePath"] = "different.png"; }
            Assert.Equal(catalog.Bundle.Rules.Hash, CardContentCompiler.Compile(altered.Select((value, i) => new ContentSourceDocument($"{i}.json", value.ToJsonString()))).Content!.Rules.Hash);
        }
        finally { if (Directory.Exists(temp)) { Directory.Delete(temp, true); } }
    }

    [Fact]
    public void CompleteCatalogReplayPinsEveryFrameAndRepresentativeEffectFacts()
    {
        var folder = Path.Combine(Root, "tests", "Fixtures", "P8Content");
        var catalog = ContentCatalog.Load(Root);
        var protocol = ProtocolCompiler.Compile("p8", File.ReadAllText(Path.Combine(folder, "protocol-v0.json"))).Protocol!;
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "match.replay.json")));
        var replay = json.RootElement; var header = replay.GetProperty("initial");
        var state = MatchFactory.Create(new(protocol, catalog.Bundle.Rules, header.GetProperty("matchSeed").GetUInt64(),
            Deck(header.GetProperty("playerOneDeck")), Deck(header.GetProperty("playerTwoDeck")))).State!;
        Assert.Equal(header.GetProperty("ruleContentHash").GetString(), catalog.Bundle.Rules.Hash.ToString());
        Assert.Equal(header.GetProperty("expectedInitialStateHash").GetString(), MatchStateHasher.Compute(state).ToString());
        var result = MatchReplay.ReplayCommands(state, replay.GetProperty("commands").EnumerateArray().Select(Command));
        Assert.True(result.IsSuccess);
        Assert.Equal(MatchStatus.Active, result.State.Status);
        Assert.Equal(4, result.State.Turn); Assert.Equal(70, result.Frames.Length);
        Assert.Equal(replay.GetProperty("expectedFinalStateHash").GetString(), MatchStateHasher.Compute(result.State).ToString());
        var expected = replay.GetProperty("expectedFrames").EnumerateArray().ToArray();
        Assert.Equal(expected.Length, result.Frames.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            var actual = result.Frames[i];
            Assert.Equal(expected[i].GetProperty("beforeStateHash").GetString(), actual.BeforeStateHash.ToString());
            Assert.Equal(expected[i].GetProperty("afterStateHash").GetString(), actual.AfterStateHash.ToString());
            Assert.Equal(expected[i].GetProperty("receiptHash").GetString(), actual.Receipts.Hash.ToString());
            Assert.Equal(expected[i].GetProperty("eventHash").GetString(), actual.Events.Hash.ToString());
            Assert.Equal(expected[i].GetProperty("rngSampleCount").GetUInt64(), actual.State.RuleRng.SampleCount);
        }
        var facts = result.Frames.SelectMany(frame => frame.Events.Events).Select(value => value.Kind).ToHashSet();
        Assert.Contains(DomainEventKind.EntityHealthLost, facts);
        Assert.Contains(DomainEventKind.CardModified, facts);
        Assert.Contains(DomainEventKind.CardReturned, facts);
        static DeckDefinition Deck(JsonElement deck) => DeckDefinition.Create(Profession.Neutral, deck.GetProperty("cards").EnumerateArray()
            .Select(card => new DeckEntry(new(card.GetProperty("id").GetString()!), card.GetProperty("copies").GetInt32())));
        static AuthoritativeCommand Command(JsonElement command)
        {
            var player = new PlayerId(command.GetProperty("playerId").GetByte()); var revision = command.GetProperty("expectedPlayerRevision").GetUInt64();
            if (command.GetProperty("kind").GetString() == "submitTurn") { return new SubmitTurnCommand(player, revision); }
            var card = new CardInstanceId(command.GetProperty("cardInstanceId").GetUInt64());
            LaneId? lane = command.TryGetProperty("laneId", out var value) && value.ValueKind == JsonValueKind.Number ? new(value.GetInt32()) : null;
            return command.GetProperty("kind").GetString() == "planCard" ? new PlanCardCommand(player, revision, card, lane!.Value) : new PlanSpellCommand(player, revision, card, lane);
        }
    }

    [Fact]
    public void EmojiSvgEscapesTextAndRejectsInjectedColors()
    {
        var svg = EmojiArt.Svg("<script>&🐸", "#112233", "#445566");
        var document = XDocument.Parse(svg);
        Assert.Empty(document.Descendants("script"));
        Assert.Contains("&lt;script&gt;&amp;", svg, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => EmojiArt.Svg("🐸", "red\" onload=\"bad", "#ffffff"));
        Assert.DoesNotContain("<rect", EmojiArt.Svg("🌊", "#ffffff", "#ffffff", "icon"), StringComparison.Ordinal);
        var png = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };
        var fused = EmojiArt.Svg("🌊🗿", "#112233", "#445566", fusionPng: png);
        Assert.Contains("<image", fused, StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64,", fused, StringComparison.Ordinal);
        Assert.DoesNotContain("🌊🗿", fused, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => EmojiArt.Svg("🌊🗿", "#112233", "#445566", fusionPng: [1, 2, 3]));
    }
}
