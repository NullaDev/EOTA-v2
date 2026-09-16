using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eota.Client.Desktop;
using Eota.Content.Compiler;
using Eota.Kernel.Commands;
using Eota.Kernel.Content;
using Eota.Kernel.Effects;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;
using Eota.Server.Infrastructure;

namespace Eota.Server.IntegrationTests;

public sealed class ModAuthoringDocumentationTests
{
    private static string Examples => Path.Combine(Fixture.Root, "Docs", "Content", "Examples");
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    [Fact]
    public void GuideJsonCompilesAndExamplePackCanBeImportedWithItsTextsAndReferences()
    {
        var sources = Sources(); var compiled = Compile(sources);
        var texts = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Examples, "zh-CN.json")))!;
        Assert.Equal(9, compiled.Rules.Cards.Length);
        foreach (var card in compiled.Presentation.Cards)
        {
            Assert.False(string.IsNullOrWhiteSpace(texts[card.NameLocalizationKey]));
            Assert.False(string.IsNullOrWhiteSpace(texts[card.DescriptionLocalizationKey]));
            Assert.True(File.Exists(Path.Combine(Fixture.Root, card.TexturePath)), card.TexturePath);
        }

        var guide = File.ReadAllText(Path.Combine(Fixture.Root, "Docs", "CardJsonDesignGuide.zh-CN.md"));
        var snippets = guide.Split("<!-- mod-example: ", StringSplitOptions.None).Skip(1).ToArray();
        Assert.Equal(guide.Split("```json", StringSplitOptions.None).Length - 1, snippets.Length);
        foreach (var snippet in snippets)
        {
            var kind = snippet[..snippet.IndexOf(" -->", StringComparison.Ordinal)];
            var start = snippet.IndexOf("```json", StringComparison.Ordinal) + 7;
            var end = snippet.IndexOf("```", start, StringComparison.Ordinal);
            var json = snippet[start..end];
            if (kind == "texts")
            {
                foreach (var pair in JsonSerializer.Deserialize<Dictionary<string, string>>(json)!) { Assert.Equal(texts[pair.Key], pair.Value); }
                continue;
            }
            JsonObject card;
            if (kind == "card")
            {
                card = JsonNode.Parse(json)!.AsObject();
                Assert.True(JsonNode.DeepEquals(card, JsonNode.Parse(sources.Single(source => source.SourceName == card["id"]!.GetValue<string>()).Json)));
            }
            else
            {
                Assert.True(kind is "minion-abilities" or "spell-abilities" or "global-spell-abilities" or "field-abilities");
                var id = kind switch { "minion-abilities" => "MOD-DEMO-SENTINEL", "field-abilities" => "MOD-DEMO-WORKSHOP", _ => "MOD-DEMO-FROST" };
                card = JsonNode.Parse(sources.Single(source => source.SourceName == id).Json)!.AsObject();
                if (kind == "global-spell-abilities") { card["targetScope"] = "global"; }
                DesktopContentEditor.ApplyAbilities(card, json);
            }
            var replacing = card["id"]!.GetValue<string>();
            Compile(sources.Where(source => source.SourceName != replacing).Append(new ContentSourceDocument(replacing, card.ToJsonString())));
        }

        var directory = Path.Combine(Fixture.Root, "artifacts", "ModAuthoringExamples"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "demo.eotapack.json");
        var pack = new DesktopContentPack(1, compiled.Rules.Hash.ToString(), sources.Select(source => JsonSerializer.Deserialize<JsonElement>(source.Json)).ToArray(), texts);
        File.WriteAllText(path, JsonSerializer.Serialize(pack, Pretty));
        var catalog = new DesktopCatalog(Fixture.Root, path);
        Assert.Equal(pack.RuleHash, catalog.RuleHash);
        Assert.Equal(9, catalog.Cards.Length);
        var editor = new DesktopContentEditor(new DesktopCatalog(Fixture.Root));
        editor.ImportCards(path);
        Assert.Equal(pack.RuleHash, editor.ExportCards(sources.Select(source => source.SourceName)).RuleHash);
        Assert.Contains("EOTA-CORE-GUA-MIN-001", editor.CardIds);
        var dependencyExport = editor.ExportCards(["MOD-DEMO-WORKSHOP"]);
        Assert.Contains(dependencyExport.Cards, card => card.GetProperty("id").GetString() == "MOD-DEMO-DRONE");
        Assert.Equal(texts["card.MOD-DEMO-DRONE.name"], dependencyExport.Texts["card.MOD-DEMO-DRONE.name"]);
    }

    [Fact]
    public void EveryTeachingCardCanBePlannedAndTheEffectsResolveTogether()
    {
        var state = Create();
        foreach (var player in new[] { PlayerId.One, PlayerId.Two })
        {
            foreach (var id in state.Players[player.Value].Hand.ToArray())
            {
                var prototype = state.CardInstances.Single(card => card.Id == id).CurrentPrototypeId;
                state.Content.TryGetCard(prototype, out var card);
                var lane = new LaneId(prototype.Value switch
                {
                    "MOD-DEMO-SENTINEL" => 0, "MOD-DEMO-FROST" or "MOD-DEMO-ECHO" => 1,
                    "MOD-DEMO-DEPLOY" => 2, "MOD-DEMO-WORKSHOP" => 3, "MOD-DEMO-PERMANENT" => 4, _ => 5
                });
                var revision = state.Players[player.Value].CommandRevision;
                AuthoritativeCommand command = card is SpellCardDefinition spell
                    ? new PlanSpellCommand(player, revision, id, spell.TargetScope == SpellTargetScope.Global ? null : lane)
                    : new PlanCardCommand(player, revision, id, lane);
                var accepted = MatchCommandProcessor.Accept(state, command);
                Assert.True(accepted.IsAccepted, prototype.Value); state = accepted.State;
            }
        }
        var result = Resolve(state);
        Assert.True(result.CompletedTurn); Assert.Equal(MatchStatus.Active, result.State.Status);
        Assert.Contains(result.State.CardInstances, card => card.CurrentPrototypeId.Value == "MOD-DEMO-DRONE");
        Assert.Contains(result.Frames.SelectMany(frame => frame.Events.Events), value => value.Kind == DomainEventKind.CardGenerated);
        Assert.Contains(result.Frames.SelectMany(frame => frame.Events.Events), value => value.Kind == DomainEventKind.LaneStatusChanged);
        Assert.DoesNotContain(result.Frames.SelectMany(frame => frame.Receipts.Receipts), value => value.Status == IntentReceiptStatus.Error);
    }

    [Fact]
    public void DrawAndImproveExampleModifiesOnlyTheMechanicalMinionActuallyDrawn()
    {
        var state = Enumerable.Range(1, 256).Select(seed => Create(1, (ulong)seed))
            .First(state => state.CardInstances.Single(card => card.Id == state.Players[0].Hand.Single()).CurrentPrototypeId.Value == "MOD-DEMO-REPAIR");
        var plan = MatchCommandProcessor.Accept(state, new PlanSpellCommand(PlayerId.One, state.Players[0].CommandRevision, state.Players[0].Hand.Single(), null));
        Assert.True(plan.IsAccepted);
        var result = Resolve(plan.State);
        Assert.True(result.CompletedTurn);
        var drawn = result.State.CardInstances.Single(card => card.OwnerId == PlayerId.One && card.CurrentPrototypeId.Value == "MOD-DEMO-DEPLOY");
        Assert.Contains(drawn.Id, result.State.Players[0].Hand);
        var definition = Assert.IsType<MinionCardDefinition>(CardInstanceRules.Definition(result.State, drawn));
        Assert.Equal(3, definition.Attack); Assert.Equal(3, definition.Health);
        var other = result.State.CardInstances.Single(card => card.OwnerId == PlayerId.Two && card.CurrentPrototypeId == drawn.CurrentPrototypeId);
        Assert.Equal(2, Assert.IsType<MinionCardDefinition>(CardInstanceRules.Definition(result.State, other)).Attack);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 4)]
    public void DocumentedParallelAndSequenceReadDifferentSnapshots(bool sequential, long expectedDamage)
    {
        var abilities = GuideAbilities("charge-and-strike");
        if (sequential)
        {
            var body = abilities["effects"]![0]!["body"]!.AsObject();
            body["kind"] = "sequence"; body["steps"] = body["children"]!.DeepClone(); body.Remove("children");
        }
        var state = CreateExample("MOD-DEMO-SENTINEL", abilities);
        var card = state.CardInstances.Single(card => card.OwnerId == PlayerId.One && card.CurrentPrototypeId.Value == "MOD-DEMO-SENTINEL");
        var planned = MatchCommandProcessor.Accept(state, new PlanCardCommand(PlayerId.One, state.Players[0].CommandRevision, card.Id, new LaneId(0)));
        Assert.True(planned.IsAccepted);
        var result = Resolve(planned.State);
        var damageFrame = Assert.Single(result.Frames, frame => frame.Receipts.Receipts.Any(receipt => receipt.DetailCode == "damage" && receipt.PlayerId == PlayerId.Two));
        Assert.Equal(state.Players[1].HeroHealth - expectedDamage, damageFrame.State.Players[1].HeroHealth);
        Assert.Equal(4, Assert.IsType<MinionEntityState>(Assert.Single(result.State.Entities)).Attack);
    }

    [Fact]
    public void DocumentedHeroExecutionResumesFromEveryFrameCheckpoint()
    {
        var state = CreateExample("MOD-DEMO-FROST", GuideAbilities("execute-hero"));
        state = state with { Players = state.Players.SetItem(1, state.Players[1] with { HeroHealth = 10 }) };
        var card = state.CardInstances.Single(card => card.OwnerId == PlayerId.One && card.CurrentPrototypeId.Value == "MOD-DEMO-FROST");
        var planned = MatchCommandProcessor.Accept(state, new PlanSpellCommand(PlayerId.One, state.Players[0].CommandRevision, card.Id, new LaneId(0)));
        Assert.True(planned.IsAccepted); state = planned.State;
        foreach (var player in state.Players)
        { state = MatchCommandProcessor.Accept(state, new SubmitTurnCommand(player.Id, player.CommandRevision)).State; }
        var complete = TurnResolver.ResolveReadyTurn(state);
        Assert.Equal(MatchOutcome.PlayerOneWon, complete.State.Outcome);
        Assert.Equal(0, complete.State.Players[1].HeroHealth);
        var checkpoints = new[] { state }.Concat(complete.Frames.Select(frame => frame.State)).ToArray();
        for (var index = 0; index < checkpoints.Length; index++)
        {
            var original = checkpoints[index];
            var restored = MatchCheckpointCodec.Decode(MatchCheckpointCodec.Encode(original), original.Protocol, original.Content);
            Assert.Equal(MatchStateHasher.Compute(original), MatchStateHasher.Compute(restored));
            if (restored.Status != MatchStatus.Active) { continue; }
            var resumed = TurnResolver.ResolveReadyTurn(restored);
            Assert.Equal(complete.Frames.Skip(index).Select(frame => frame.AfterStateHash), resumed.Frames.Select(frame => frame.AfterStateHash));
            Assert.Equal(complete.Frames.Skip(index).Select(frame => frame.Receipts.Hash), resumed.Frames.Select(frame => frame.Receipts.Hash));
            Assert.Equal(complete.Frames.Skip(index).Select(frame => frame.Events.Hash), resumed.Frames.Select(frame => frame.Events.Hash));
        }
    }

    private static JsonObject GuideAbilities(string effectId)
    {
        var guide = File.ReadAllText(Path.Combine(Fixture.Root, "Docs", "CardJsonDesignGuide.zh-CN.md"));
        return guide.Split("<!-- mod-example: ", StringSplitOptions.None).Skip(1).Select(snippet =>
        {
            var start = snippet.IndexOf("```json", StringComparison.Ordinal) + 7;
            return JsonNode.Parse(snippet[start..snippet.IndexOf("```", start, StringComparison.Ordinal)])!.AsObject();
        }).Single(value => value["effects"] is JsonArray effects && effects.Any(effect => effect?["id"]?.GetValue<string>() == effectId));
    }

    [Fact]
    public void DocumentedFailedReturnTriggersDeathAfterCheckpointRestore()
    {
        var state = CreateExample("MOD-DEMO-SENTINEL", GuideAbilities("homeward"), handLimit: 8);
        var original = state.CardInstances.Single(card => card.OwnerId == PlayerId.One && card.CurrentPrototypeId.Value == "MOD-DEMO-SENTINEL");
        var planned = MatchCommandProcessor.Accept(state, new PlanCardCommand(PlayerId.One, state.Players[0].CommandRevision, original.Id, new LaneId(0)));
        Assert.True(planned.IsAccepted);
        var result = Resolve(planned.State);
        var deathFrame = Assert.Single(result.Frames, frame => frame.Events.Events.Any(fact => fact.Kind == DomainEventKind.EntityDied));
        Assert.Empty(deathFrame.State.Entities);
        Assert.Equal(EntityRemovalReason.Death, Assert.Single(deathFrame.State.Tombstones).Reason);
        Assert.Contains(original.Id, deathFrame.State.Players[0].Discard);
        Assert.Equal(state.Protocol.Definition.HandLimit, deathFrame.State.Players[0].Hand.Length);
        Assert.Equal(state.Players[1].HeroHealth, deathFrame.State.Players[1].HeroHealth);
        Assert.DoesNotContain(result.Frames.SelectMany(frame => frame.Events.Events), fact => fact.Kind == DomainEventKind.CardReturned);
        var damageFrame = Assert.Single(result.Frames, frame => frame.Receipts.Receipts.Any(receipt => receipt.DetailCode == "damage"));
        Assert.Equal(state.Players[1].HeroHealth - 2, damageFrame.State.Players[1].HeroHealth);
        var restored = MatchCheckpointCodec.Decode(MatchCheckpointCodec.Encode(deathFrame.State), deathFrame.State.Protocol, deathFrame.State.Content);
        var resumed = TurnResolver.ResolveReadyTurn(restored);
        var tail = result.Frames.Skip(result.Frames.IndexOf(deathFrame) + 1).ToArray();
        Assert.Equal(MatchStateHasher.Compute(result.State), MatchStateHasher.Compute(resumed.State));
        Assert.Equal(tail.Select(frame => frame.AfterStateHash), resumed.Frames.Select(frame => frame.AfterStateHash));
        Assert.Equal(tail.Select(frame => frame.Receipts.Hash), resumed.Frames.Select(frame => frame.Receipts.Hash));
        Assert.Equal(tail.Select(frame => frame.Events.Hash), resumed.Frames.Select(frame => frame.Events.Hash));
    }

    private static MatchState CreateExample(string prototype, JsonObject abilities, int? handLimit = null)
    {
        var sources = Sources();
        var original = sources.Single(source => source.SourceName == prototype);
        var card = JsonNode.Parse(original.Json)!.AsObject();
        DesktopContentEditor.ApplyAbilities(card, abilities.ToJsonString());
        return Create(sources: sources.Where(source => source != original).Append(new(prototype, card.ToJsonString())).ToArray(), handLimit: handLimit);
    }

    private static MatchState Create(int? opening = null, ulong seed = 146, ContentSourceDocument[]? sources = null, int? handLimit = null)
    {
        var protocol = ProtocolCompiler.Compile("teaching-protocol", File.ReadAllText(Path.Combine(Examples, "protocol.json")));
        Assert.True(protocol.IsSuccess, string.Join('\n', protocol.Diagnostics.Select(value => value.Message)));
        var compiled = protocol.Protocol!;
        if (opening is { } count) { compiled = Eota.Kernel.Protocols.CompiledGameProtocol.Compile(compiled.Definition with { OpeningHandSize = count }); }
        if (handLimit is { } limit) { compiled = Eota.Kernel.Protocols.CompiledGameProtocol.Compile(compiled.Definition with { HandLimit = limit }); }
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Examples, "deck.json")));
        var deck = DeckDefinition.Create(Enum.Parse<Profession>(document.RootElement.GetProperty("profession").GetString()!, true),
            document.RootElement.GetProperty("cards").EnumerateArray().Select(card => new DeckEntry(new CardPrototypeId(card.GetProperty("id").GetString()!), card.GetProperty("copies").GetInt32())));
        var creation = MatchFactory.Create(new(compiled, Compile(sources ?? Sources()).Rules, seed, deck, deck));
        Assert.True(creation.IsSuccess, string.Join(',', creation.Errors.Select(error => error.Code))); return creation.State!;
    }

    private static TurnResolutionResult Resolve(MatchState state)
    {
        foreach (var player in state.Players)
        { state = MatchCommandProcessor.Accept(state, new SubmitTurnCommand(player.Id, player.CommandRevision)).State; }
        return TurnResolver.ResolveReadyTurn(state);
    }

    private static ContentSourceDocument[] Sources() => Directory.EnumerateFiles(Path.Combine(Examples, "Cards"), "*.json")
        .Order(StringComparer.Ordinal).Select(path => new ContentSourceDocument(Path.GetFileNameWithoutExtension(path), File.ReadAllText(path))).ToArray();

    private static CompiledContentBundle Compile(IEnumerable<ContentSourceDocument> sources)
    {
        var result = CardContentCompiler.Compile(sources);
        Assert.True(result.IsSuccess, string.Join('\n', result.Diagnostics.Select(error => $"{error.SourceName} {error.Path}: {error.Code} {error.Message}")));
        return result.Content!;
    }
}
