using System.Text.Json;
using System.Text.Json.Nodes;
using Eota.Client.Desktop;
using Eota.Content.Compiler;

namespace Eota.Server.IntegrationTests;

public sealed class ContentPackageIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eota-isolation-" + Guid.NewGuid().ToString("N"));
    public ContentPackageIsolationTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);
    private static ContentPackage Tutorial() => ContentPackages.Read(Path.Combine(Fixture.Root, "Content", "Packs", "tutorial", "Generated", "tutorial.eotapack.json"));

    [Fact]
    public async Task TutorialRunsWithNoStandardFilesAndUsesItsOwnProfessionDeckAndArt()
    {
        var source = Tutorial();
        var file = Path.Combine(_root, "Content", "Packs", "tutorial", "tutorial.eotapack.json");
        PackageAssets.CopyFiles(source, file);
        File.WriteAllText(file, JsonSerializer.Serialize(source, ContentPackages.JsonOptions));
        File.WriteAllText(Path.Combine(_root, "Content", "default-selection.json"), "[\"tutorial\"]");
        var store = new DesktopContentPackages(_root, Path.Combine(_root, "user"));
        var catalog = store.LoadCatalog();
        Assert.False(Directory.Exists(Path.Combine(_root, "Content", "Packs", "standard")));
        Assert.Equal("Wayfarer", Assert.Single(catalog.Professions).Id);
        Assert.Equal(15, catalog.Cards.Length);
        Assert.All(catalog.Cards, c => Assert.True(File.Exists(catalog.ResolveTexture(c.TexturePath))));
        var deck = catalog.DefaultDeck("Wayfarer"); Assert.Equal(40, deck.Cards.Sum(c => c.Copies));
        Assert.Empty(catalog.ValidateDeck(deck));
        await using var session = await DesktopSession.LocalAsync(catalog, deck, deck, new());
        Assert.NotNull(session.Client.Store.View);
        var editor = DesktopContentEditor.NewPackage(catalog); editor.PackageId = "my-extension";
        editor.SaveCard(DesktopContentEditor.NewCard());
        Assert.Equal("tutorial", Assert.Single(editor.ExportPackage().Dependencies).Id);
    }

    [Fact]
    public void TutorialCloneImportsItsFilesAndCanReplaceItsOriginalWithoutHiddenDependency()
    {
        var catalog = new DesktopCatalog(Fixture.Root, [Tutorial()]);
        var editor = new DesktopContentEditor(catalog) { PackageId = "my-world", PackageVersion = "2.0.0" };
        var path = Path.Combine(_root, "export", "my-world.eotapack.json"); editor.SavePack(path);
        var store = new DesktopContentPackages(_root, Path.Combine(_root, "user"));
        var installed = store.Install(path); store.SaveSelection(["my-world"]);
        var loaded = store.LoadCatalog();
        Assert.Empty(Assert.Single(loaded.Packages).Dependencies);
        Assert.All(loaded.Cards, c => Assert.StartsWith("pack://my-world/", c.TexturePath, StringComparison.Ordinal));
        Assert.Empty(loaded.ValidateDeck(loaded.DefaultDeck("Wayfarer")));
        var resource = loaded.ResolveTexture(loaded.Cards[0].TexturePath);
        File.AppendAllText(resource, "tampered");
        Assert.Contains("hash", Assert.Throws<InvalidDataException>(() => store.LoadCatalog()).Message, StringComparison.Ordinal);
        Assert.True(File.Exists(installed.Path));
    }

    [Fact]
    public void ProfessionAndAssetDefinitionsParticipateInHashAndDependencyValidation()
    {
        var pack = Tutorial();
        Assert.NotEqual(pack.Hash, ContentPackages.Seal(pack with { Professions = [pack.Professions[0] with { Description = "Changed" }] }).Hash);
        Assert.NotEqual(pack.Hash, ContentPackages.Seal(pack with { Assets = pack.Assets.SetItem(pack.Assets.Keys.First(), new string('0', 64)) }).Hash);
        var collision = ContentPackages.Seal(pack with { Id = "other", Cards = [], Texts = pack.Texts.Clear(), Decks = [], Assets = pack.Assets.Clear() });
        Assert.Contains("职业 ID 冲突", Assert.Throws<InvalidDataException>(() => ContentPackages.Load([pack, collision])).Message, StringComparison.Ordinal);
        var extra = JsonNode.Parse(DesktopContentEditor.NewCard().Json)!;
        extra["profession"] = "wayfarer";
        var expansion = ContentPackages.Seal(new(ContentPackage.Schema, "addon", "1.0.0", "addon", "", [],
            [JsonSerializer.SerializeToElement(extra)], pack.Texts.Clear(), []));
        Assert.Contains("职业", Assert.Throws<InvalidDataException>(() => ContentPackages.Load([pack, expansion])).Message, StringComparison.Ordinal);
        expansion = ContentPackages.Seal(expansion with { Dependencies = [new("tutorial")] });
        Assert.Equal(16, ContentPackages.Load([pack, expansion]).Content.Rules.Cards.Length);
        Assert.Throws<InvalidDataException>(() => ContentPackages.Seal(pack with { Assets = pack.Assets.Add("../escape.png", new string('0', 64)) }));
    }

    [Fact]
    public void DistributionContainsOnlyTheTwoDeclaredPackagesAndEnablesOnlyStandard()
    {
        var store = new DesktopContentPackages(Fixture.Root, _root);
        Assert.Equal(["standard", "tutorial"], store.Scan().Select(p => p.Id));
        Assert.Equal(["standard"], store.ReadSelection().ToArray());
        var pack = ContentPackages.Read(store.Scan().Single(p => p.Id == "standard").Path);
        Assert.Equal(6, pack.Professions.Length); Assert.Equal(282, pack.Cards.Length);
        Assert.Equal(282, pack.Assets.Count); Assert.All(pack.Assets.Keys, path => Assert.StartsWith("Art/", path, StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(Fixture.Root, "Content", "Packs", "standard", "Generated", "Icons")));
        Assert.True(File.Exists(Path.Combine(Fixture.Root, "Client", "Assets", "Icons", "ether.png")));
    }
}
