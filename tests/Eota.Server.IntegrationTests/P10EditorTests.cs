using System.Text.Json;
using System.Text.Json.Nodes;
using Eota.Client.Desktop;

namespace Eota.Server.IntegrationTests;

public sealed class P10EditorTests
{
    [Fact]
    public void ExportFollowsNestedGrantedEffectsAndTransitiveCyclicReferences()
    {
        var editor = new DesktopContentEditor(new DesktopCatalog(Fixture.Root));
        var a = editor.SaveCard(DesktopContentEditor.NewCard());
        var b = editor.SaveCard(DesktopContentEditor.NewCard());
        var c = editor.SaveCard(DesktopContentEditor.NewCard());
        foreach (var (id, child) in new[] { (a, b), (b, c), (c, b) })
        {
            var card = editor.Read(id); var json = JsonNode.Parse(card.Json)!;
            json["effects"] = JsonNode.Parse("""
                [{"id":"entry","trigger":{"kind":"selfEntered"},"body":{
                  "kind":"grantEffect","target":{"kind":"self"},"effect":{
                    "id":"grant","trigger":{"kind":"turnEnd"},"body":{
                      "kind":"sequence","steps":[{"kind":"loop","count":1,"body":{
                        "kind":"generate","target":{"kind":"friendlyHero"},"count":1,"prototype":"TARGET"}}]}}}}]
                """.Replace("TARGET", child, StringComparison.Ordinal));
            editor.SaveCard(card with { Json = json.ToJsonString() }, id);
        }
        var exported = editor.ExportCards([a]);
        Assert.Equal(new[] { a, b, c }.Order(StringComparer.Ordinal), exported.Cards.Select(card => card.GetProperty("id").GetString()));
    }

    [Fact]
    public void SingleCardExportIncludesDependenciesAndMergesWithoutDroppingOtherCards()
    {
        var catalog = new DesktopCatalog(Fixture.Root); var editor = new DesktopContentEditor(catalog);
        const string id = "EOTA-CORE-GUA-SPL-002"; const string tokenId = "EOTA-TOKEN-GUA-MIN-001";
        var card = editor.Read(tokenId); var json = JsonNode.Parse(card.Json)!; json["attack"] = 7;
        editor.SaveCard(card with { Json = json.ToJsonString(), Name = "导出的衍生卡" }, tokenId);
        var exported = editor.ExportCards([id]);
        Assert.Equal(new[] { id, tokenId }, exported.Cards.Select(value => value.GetProperty("id").GetString()));
        Assert.Equal(4, exported.Texts.Count);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".eotapack.json");
        try
        {
            editor.SaveCards(path, [id]);
            var small = new DesktopCatalog(Fixture.Root, path);
            Assert.Equal(2, small.Cards.Length); Assert.Equal(7, small.Cards.Single(value => value.Id == tokenId).Attack);
            var recipient = new DesktopContentEditor(catalog); recipient.ImportCards(path);
            Assert.Equal(282, recipient.CardIds.Count()); Assert.Equal("导出的衍生卡", recipient.Read(tokenId).Name);
            Assert.Equal(editor.Export().RuleHash, recipient.Export().RuleHash);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AbilityJsonRoundTripsPassivesAndEffectsAndRejectsDuplicateProperties()
    {
        var editor = new DesktopContentEditor(new DesktopCatalog(Fixture.Root)); var value = DesktopContentEditor.NewCard("field");
        var card = JsonNode.Parse(value.Json)!.AsObject();
        DesktopContentEditor.ApplyAbilities(card, """
            {"storedCharge":3,"preventsActiveAttacksInLane":true,"keywords":[],"effects":[
              {"id":"charge","trigger":{"kind":"preCombatCharge"},"charge":2,
               "body":{"kind":"damage","target":{"kind":"enemyHero"},"amount":1}}]}
            """);
        Assert.Empty(editor.Validate(value with { Json = card.ToJsonString() }));
        var abilities = DesktopContentEditor.AbilitiesJson(card); var copy = JsonNode.Parse(value.Json)!.AsObject();
        DesktopContentEditor.ApplyAbilities(copy, abilities); Assert.True(JsonNode.DeepEquals(card, copy));
        Assert.Throws<JsonException>(() => DesktopContentEditor.ApplyAbilities(copy, "{\"storedCharge\":1,\"storedCharge\":9}"));
        Assert.Throws<InvalidDataException>(() => DesktopContentEditor.ApplyAbilities(copy, "{\"cost\":0}"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FieldsRejectUnknownPowerProperty(bool permanent)
    {
        var editor = new DesktopContentEditor(new DesktopCatalog(Fixture.Root)); var value = DesktopContentEditor.NewCard("field");
        var card = JsonNode.Parse(value.Json)!; card["power"] = 3;
        if (permanent) { card["lifetime"] = new JsonObject { ["kind"] = "permanent" }; }
        Assert.Contains(editor.Validate(value with { Json = card.ToJsonString() }), error => error.Contains("unknown-property", StringComparison.Ordinal));
    }

    [Fact]
    public void EditingAndExportingChangesRuleHashWithoutChangingBundledContent()
    {
        var catalog = new DesktopCatalog(Fixture.Root); var editor = new DesktopContentEditor(catalog);
        const string id = "EOTA-CORE-GUA-MIN-001"; var value = editor.Read(id); var json = JsonNode.Parse(value.Json)!;
        json["attack"] = 999999;
        var edited = value with { Json = json.ToJsonString(), Name = "自定义强力随从" };
        Assert.Empty(editor.Validate(edited, id)); Assert.Equal(999999, editor.Preview(edited, id).Attack);
        editor.SaveCard(edited, id); Assert.NotEqual(catalog.RuleHash, editor.Export().RuleHash);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".eotapack.json");
        try
        {
            editor.SavePack(path); var custom = new DesktopCatalog(Fixture.Root, path);
            Assert.Equal(999999, custom.Cards.Single(c => c.Id == id).Attack); Assert.Equal("自定义强力随从", custom.Cards.Single(c => c.Id == id).Name);
            Assert.Equal(catalog.RuleHash, new DesktopCatalog(Fixture.Root).RuleHash);
            var bytes = File.ReadAllText(path); File.WriteAllText(path, bytes.Replace("999999", "999998", StringComparison.Ordinal));
            Assert.Throws<InvalidDataException>(() => DesktopContentEditor.LoadPack(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LocalizationDoesNotChangeRulesButDuplicatesAndBrokenReferencesCannotBeSaved()
    {
        var catalog = new DesktopCatalog(Fixture.Root); var editor = new DesktopContentEditor(catalog);
        const string id = "EOTA-CORE-GUA-MIN-001"; var value = editor.Read(id);
        editor.SaveCard(value with { Name = "翻译名称", Description = "翻译说明" }, id);
        Assert.Equal(catalog.RuleHash, editor.Export().RuleHash);
        Assert.Throws<InvalidDataException>(() => editor.SaveCard(value));
        Assert.Throws<InvalidDataException>(() => editor.DeleteCard("EOTA-TOKEN-GUA-MIN-001"));
        Assert.Contains(editor.Validate(value with { Json = value.Json.Replace("\"cost\": 2", "\"cost\": 2, \"cost\": 999", StringComparison.Ordinal) }, id), e => e.Contains("duplicate-property", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("minion")]
    [InlineData("spell")]
    [InlineData("field")]
    public void NewCardsUseV2SchemaAndPreserveEffectsAcrossRoundTrip(string kind)
    {
        var editor = new DesktopContentEditor(new DesktopCatalog(Fixture.Root)); var value = DesktopContentEditor.NewCard(kind);
        var initialCount = editor.CardIds.Count();
        Assert.Empty(editor.Validate(value)); var id = editor.SaveCard(value);
        Assert.Contains(id, editor.CardIds); var pack = editor.Export();
        using var document = JsonDocument.Parse(editor.Read(id).Json);
        Assert.Equal("eota.card/v2", document.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(kind, document.RootElement.GetProperty("kind").GetString()); Assert.Equal(initialCount + 1, pack.Cards.Length);
    }
}
