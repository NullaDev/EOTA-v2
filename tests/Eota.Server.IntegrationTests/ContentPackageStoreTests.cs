using System.Text.Json;
using Eota.Client.Desktop;
using Eota.Content.Compiler;

namespace Eota.Server.IntegrationTests;

public sealed class ContentPackageStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "eota-pack-store-" + Guid.NewGuid().ToString("N"));
    public ContentPackageStoreTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void SelectionPersistsAndInvalidSelectionNeverFallsBackToStandard()
    {
        var store = new DesktopContentPackages(Fixture.Root, _directory);
        Assert.Equal(["standard"], store.ReadSelection().ToArray());
        Assert.Equal(282, store.LoadCatalog().Cards.Length);
        store.SaveSelection([]);
        Assert.Empty(new DesktopContentPackages(Fixture.Root, _directory).ReadSelection());
        Assert.Throws<InvalidDataException>(() => store.LoadCatalog());
        store.SaveSelection(["missing"]);
        Assert.Throws<InvalidDataException>(() => store.LoadCatalog());
        store.SaveSelection(["standard"]);
        Assert.Equal(282, store.LoadCatalog().Cards.Length);
    }

    [Fact]
    public void ExtensionCanBeInstalledEnabledDisabledAndUninstalledWithoutReplacingStandard()
    {
        var store = new DesktopContentPackages(Fixture.Root, _directory);
        var standard = store.LoadCatalog();
        var editor = DesktopContentEditor.NewPackage(standard);
        editor.PackageId = "my-expansion"; editor.PackageVersion = "1.2.3-pre-release";
        editor.SaveCard(DesktopContentEditor.NewCard());
        var file = Path.Combine(_directory, "export.json"); editor.SavePack(file);
        var installed = store.Install(file);
        Assert.Equal(1, installed.CardCount);
        Assert.Equal(282, store.LoadCatalog().Cards.Length);
        store.SaveSelection(["my-expansion"]);
        Assert.Throws<InvalidDataException>(() => store.LoadCatalog());
        store.SaveSelection(["standard", "my-expansion"]);
        Assert.Equal(283, new DesktopContentPackages(Fixture.Root, _directory).LoadCatalog().Cards.Length);
        Assert.Throws<InvalidDataException>(() => store.Install(file));
        store.Install(file, replace: true);
        store.SaveSelection(["standard"]);
        // A disabled damaged pack cannot prevent loading valid enabled packs.
        File.WriteAllText(installed.Path, "{");
        Assert.Equal(282, store.LoadCatalog().Cards.Length);
        store.SaveSelection(["standard", "my-expansion"]);
        Assert.Throws<InvalidDataException>(() => store.LoadCatalog());
        store.Remove(installed);
        Assert.DoesNotContain(store.Scan(), p => p.Id == "my-expansion");
    }

    [Fact]
    public void DuplicateInstalledIdsAndModifiedHashesBlockLoading()
    {
        var store = new DesktopContentPackages(Fixture.Root, _directory);
        var package = ContentPackages.Read(Path.Combine(Fixture.Root, "Content", "Packs", "standard", "Generated", "standard.eotapack.json"));
        var file = Path.Combine(_directory, "standard.json"); File.WriteAllText(file, JsonSerializer.Serialize(package, ContentPackages.JsonOptions));
        Assert.Throws<InvalidDataException>(() => store.Install(file));
        Directory.CreateDirectory(store.InstalledDirectory);
        File.Copy(file, Path.Combine(store.InstalledDirectory, "duplicate.eotapack.json"));
        Assert.Throws<InvalidDataException>(() => store.LoadCatalog());
    }

    [Fact]
    public void OldReplacementPackageRequiresUpgradeAndPreservesOriginalFile()
    {
        var catalog = new DesktopCatalog(Fixture.Root);
        var legacyPath = Path.Combine(_directory, "active.json");
        var old = JsonSerializer.Serialize(new DesktopContentEditor(catalog).Export())
            .Replace("Content/Packs/standard/Generated/", "Content/Generated/", StringComparison.Ordinal);
        File.WriteAllText(legacyPath, old);
        var selection = Path.Combine(_directory, "content-selection.txt"); File.WriteAllText(selection, legacyPath);
        var store = new DesktopContentPackages(Fixture.Root, Path.Combine(_directory, "content"));
        Assert.Contains("升级", Assert.Throws<InvalidDataException>(() => store.MigrateLegacySelection(selection)).Message, StringComparison.Ordinal);
        Assert.False(File.Exists(store.SelectionPath));
        Assert.Equal(old, File.ReadAllText(legacyPath));
        store.SaveSelection(["standard"]); store.MigrateLegacySelection(selection);
        Assert.Equal(["standard"], store.ReadSelection().ToArray());
    }

    [Fact]
    public async Task MissingPackageBlocksEditingAndMatchesWithoutDeletingSavedDeckOrDraft()
    {
        var store = new DesktopContentPackages(Fixture.Root, Path.Combine(_directory, "content"));
        var standard = store.LoadCatalog();
        var editor = DesktopContentEditor.NewPackage(standard);
        editor.PackageId = "expansion";
        var extraId = editor.SaveCard(DesktopContentEditor.NewCard());
        var packPath = Path.Combine(_directory, "extra.json"); editor.SavePack(packPath); store.Install(packPath);
        store.SaveSelection(["standard", "expansion"]); var enabled = store.LoadCatalog();
        var original = enabled.DefaultDeck("Guardian");
        var deck = original with { Name = "扩展包牌组", Cards = [new(extraId, original.Cards[0].Copies), .. original.Cards.Skip(1)] };
        Assert.Empty(enabled.ValidateDeck(deck));
        var decks = new DesktopDeckStore(Path.Combine(_directory, "decks")); var id = decks.Save(deck);
        var savedBytes = File.ReadAllBytes(Path.Combine(_directory, "decks", id + ".json"));
        var draft = new DesktopDeckDraft(enabled, "Guardian"); draft.Load(deck);
        store.SaveSelection(["standard"]); var disabled = store.LoadCatalog(); draft.UseCatalog(disabled);
        Assert.Equal([extraId], disabled.MissingCards(decks.Read(id)).ToArray());
        Assert.Throws<InvalidDataException>(() => new DesktopDeckDraft(disabled, "Guardian").Load(deck));
        Assert.Throws<InvalidDataException>(() => draft.Remove(extraId));
        Assert.Throws<InvalidDataException>(() => draft.Clear());
        Assert.Throws<InvalidDataException>(() => draft.Add(original.Cards[0].Id));
        await Assert.ThrowsAsync<InvalidDataException>(() => DesktopSession.LocalAsync(disabled, deck, disabled.DefaultDeck("Hunter"), new()));
        Assert.Equal(savedBytes, File.ReadAllBytes(Path.Combine(_directory, "decks", id + ".json")));
        Assert.Equal(deck.Cards.OrderBy(c => c.Id, StringComparer.Ordinal), draft.Build().Cards);
        store.SaveSelection(["standard", "expansion"]); var restored = new DesktopContentPackages(Fixture.Root, store.UserDirectory).LoadCatalog();
        Assert.Empty(restored.MissingCards(decks.Read(id))); Assert.Empty(restored.ValidateDeck(decks.Read(id)));
        draft.UseCatalog(restored); draft.Remove(extraId);
        Assert.Single(decks.Ids);
        decks.Delete(id); Assert.Empty(decks.Ids);
    }
}
