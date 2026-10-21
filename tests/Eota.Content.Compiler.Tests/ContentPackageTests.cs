using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eota.Content.Compiler;

namespace Eota.Content.Compiler.Tests;

public sealed class ContentPackageTests
{
    private static ContentPackage Pack(string id, string card = "CARD-A", params ContentDependency[] dependencies) =>
        ContentPackages.Seal(new(ContentPackage.Schema, id, "1.0.0", id, "", [.. dependencies],
            card.Length == 0 ? [] : [JsonSerializer.SerializeToElement(new { schemaVersion = "eota.card/v2", id = card,
                source = "core", profession = "neutral", kind = "minion", cost = 1, attack = 1, health = 1,
                effects = Array.Empty<object>() })], ImmutableDictionary<string, string>.Empty, []));

    [Fact]
    public void HashIgnoresFormattingAndObjectOrderButDetectsPayloadTampering()
    {
        var pack = Pack("standard");
        var json = JsonSerializer.Serialize(pack, ContentPackages.JsonOptions);
        var document = JsonNode.Parse(json)!.AsObject();
        var reordered = new JsonObject();
        foreach (var pair in document.Reverse()) { reordered.Add(pair.Key, pair.Value?.DeepClone()); }
        Assert.Equal(pack.Hash, ContentPackages.Parse(reordered.ToJsonString()).Hash);
        document["cards"]![0]!["attack"] = 2;
        Assert.Throws<InvalidDataException>(() => ContentPackages.Parse(document.ToJsonString()));
        Assert.Throws<InvalidDataException>(() => ContentPackages.Parse(json.Replace("\"id\": \"standard\"", "\"id\": \"standard\", \"id\": \"other\"", StringComparison.Ordinal)));
        Assert.NotEqual(pack.Hash, ContentPackages.Seal(pack with { Version = "1.0.1" }).Hash);
    }

    [Fact]
    public void DependenciesAreOrderedAndMustBeExplicitlyEnabled()
    {
        var standard = Pack("standard"); var expansion = Pack("expansion", "CARD-B", new ContentDependency("standard", "1.0.0", standard.Hash));
        Assert.Throws<InvalidDataException>(() => ContentPackages.Load([expansion]));
        var loaded = ContentPackages.Load([expansion, standard]);
        Assert.Equal(["standard", "expansion"], loaded.Packages.Select(p => p.Id));
        Assert.Equal(2, loaded.Content.Rules.Cards.Length);
        Assert.Throws<InvalidDataException>(() => ContentPackages.Load([expansion, ContentPackages.Seal(standard with { Version = "1.1.0" })]));
        var changed = ContentPackages.Seal(standard with { Texts = standard.Texts.Add("extra", "changed") });
        Assert.Throws<InvalidDataException>(() => ContentPackages.Load([expansion, changed]));
    }

    [Fact]
    public void CyclesDuplicateIdsCardsAndLocalizationAreConflicts()
    {
        var a = Pack("a", "CARD-A", new ContentDependency("b")); var b = Pack("b", "CARD-B", new ContentDependency("a"));
        Assert.Contains("循环依赖", Assert.Throws<InvalidDataException>(() => ContentPackages.Load([a, b])).Message, StringComparison.Ordinal);
        var original = Pack("standard");
        Assert.Throws<InvalidDataException>(() => ContentPackages.Load([original, original]));
        Assert.Contains("卡牌 ID 冲突", Assert.Throws<InvalidDataException>(() => ContentPackages.Load([original, Pack("duplicate")])).Message, StringComparison.Ordinal);
        a = ContentPackages.Seal(original with { Texts = original.Texts.Add("shared", "A") });
        b = ContentPackages.Seal(Pack("other", "CARD-B") with { Texts = original.Texts.Add("shared", "B") });
        Assert.Contains("本地化键冲突", Assert.Throws<InvalidDataException>(() => ContentPackages.Load([a, b])).Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => ContentPackages.Load([]));
    }

    [Fact]
    public void CrossPackageDeckReferencesRequireDependencyAndSurviveOrdering()
    {
        var standard = ContentPackages.Seal(Pack("standard") with { Professions = [new("Guardian", "Test", "Test", "G", "#abcdef", true)] });
        var deck = ContentPackages.Seal(Pack("decks", "") with { Decks = [new("Sample", "Guardian", [new("CARD-A", 1)])] });
        Assert.Throws<InvalidDataException>(() => ContentPackages.Load([standard, deck]));
        deck = ContentPackages.Seal(deck with { Dependencies = [new("standard")] });
        Assert.Single(ContentPackages.Load([deck, standard]).Decks);
    }

    [Fact]
    public void CardReferencesAcrossPackagesRequireDeclaredDependency()
    {
        var standard = Pack("standard");
        var expansion = Pack("expansion", "CARD-B");
        var card = JsonNode.Parse(expansion.Cards[0].GetRawText())!;
        card["effects"] = JsonNode.Parse("""
            [{"id":"spawn","trigger":{"kind":"selfEntered"},"body":{
              "kind":"generate","target":{"kind":"friendlyHero"},"count":1,"prototype":"CARD-A"}}]
            """);
        expansion = ContentPackages.Seal(expansion with { Cards = [JsonSerializer.SerializeToElement(card)] });
        Assert.Contains("未声明", Assert.Throws<InvalidDataException>(() => ContentPackages.Load([standard, expansion])).Message, StringComparison.Ordinal);
        expansion = ContentPackages.Seal(expansion with { Dependencies = [new("standard")] });
        Assert.Equal(2, ContentPackages.Load([expansion, standard]).Content.Rules.Cards.Length);
    }
}
