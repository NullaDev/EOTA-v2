using Eota.Kernel.Content;

namespace Eota.Content.Compiler.Tests;

public sealed class CardContentCompilerTests
{
    private const string MinionJson = """
        {
          "schemaVersion": "eota.card/v2",
          "kind": "minion",
          "id": "TEST-MINION",
          "source": "test",
          "profession": "neutral",
          "cost": 1,
          "attack": 1,
          "health": 2,
          "keywords": [{ "kind": "slow", "turns": 1 }, "guard"],
          "effects": []
        }
        """;

    private const string FieldJson = """
        {
          "schemaVersion": "eota.card/v2",
          "kind": "field",
          "id": "TEST-FIELD",
          "source": "test",
          "profession": "neutral",
          "cost": 1,
          "lifetime": { "kind": "finite", "energy": 2 },
          "preventsActiveAttacksInLane": true,
          "effects": []
        }
        """;

    private const string PermanentFieldJson = """
        {
          "schemaVersion": "eota.card/v2",
          "kind": "field",
          "id": "TEST-PERMANENT-FIELD",
          "source": "test",
          "profession": "neutral",
          "cost": 1,
          "lifetime": { "kind": "permanent" },
          "effects": []
        }
        """;

    private const string SpellJson = """
        {
          "schemaVersion": "eota.card/v2",
          "kind": "spell",
          "id": "TEST-SPELL",
          "source": "test",
          "profession": "neutral",
          "cost": 1,
          "speed": "fast",
          "targetScope": "lane",
          "effects": []
        }
        """;

    [Fact]
    public void CompilesAllMinimalCardKindsAndNormalizesInputOrder()
    {
        ContentSourceDocument[] forward =
        [
            new("minion.json", MinionJson),
            new("field.json", FieldJson),
            new("spell.json", SpellJson)
        ];

        var first = CardContentCompiler.Compile(forward);
        var second = CardContentCompiler.Compile(forward.Reverse());

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(first.Content!.Rules.Hash, second.Content!.Rules.Hash);
        Assert.Equal(3, first.Content.Rules.Cards.Length);
        var minion = Assert.IsType<MinionCardDefinition>(first.Content.Rules.Cards.Single(card => card.Kind == CardKind.Minion));
        Assert.Contains(minion.Keywords, keyword => keyword is { Kind: MinionKeywordKind.Slow, Parameter: 1 });
    }

    [Fact]
    public void CompilesPermanentFieldWithoutAnEnergySentinel()
    {
        var result = CardContentCompiler.Compile([new ContentSourceDocument("permanent-field.json", PermanentFieldJson)]);

        Assert.True(result.IsSuccess);
        var field = Assert.IsType<FieldCardDefinition>(Assert.Single(result.Content!.Rules.Cards));
        Assert.IsType<PermanentFieldLifetimeDefinition>(field.Lifetime);
        Assert.Empty(field.Keywords);
    }

    [Fact]
    public void FieldReplacementKeywordsAreNormalizedAndChangeTheRuleHash()
    {
        var first = CardContentCompiler.Compile([new ContentSourceDocument("field.json",
            FieldJson.Replace("\"effects\": []", "\"keywords\": [\"replace\",\"replaceable\"], \"effects\": []", StringComparison.Ordinal))]);
        var second = CardContentCompiler.Compile([new ContentSourceDocument("field.json",
            FieldJson.Replace("\"effects\": []", "\"keywords\": [\"replaceable\",\"replace\",\"replace\"], \"effects\": []", StringComparison.Ordinal))]);
        var plain = CardContentCompiler.Compile([new ContentSourceDocument("field.json", FieldJson)]);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.True(plain.IsSuccess);
        Assert.Equal(first.Content!.Rules.Hash, second.Content!.Rules.Hash);
        Assert.NotEqual(plain.Content!.Rules.Hash, first.Content.Rules.Hash);
        var field = Assert.IsType<FieldCardDefinition>(Assert.Single(first.Content.Rules.Cards));
        Assert.Equal<FieldKeywordKind>([FieldKeywordKind.Replace, FieldKeywordKind.Replaceable], field.Keywords);
    }

    [Theory]
    [InlineData("[\"swift\"]")]
    [InlineData("[\"unknown\"]")]
    [InlineData("[{\"kind\":\"slow\",\"turns\":1}]")]
    public void FieldsRejectKeywordsThatDoNotApplyToThem(string keywords)
    {
        var result = CardContentCompiler.Compile([new ContentSourceDocument("field.json",
            FieldJson.Replace("\"effects\": []", $"\"keywords\": {keywords}, \"effects\": []", StringComparison.Ordinal))]);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, value => value.Code == "invalid-keyword");
    }

    [Fact]
    public void KeywordOrderAndDuplicatesDoNotChangeTheRuleHash()
    {
        const string original = "[{ \"kind\": \"slow\", \"turns\": 1 }, \"guard\"]";
        var reordered = MinionJson.Replace(original,
            "[\"guard\", {\"kind\":\"slow\",\"turns\":1}, \"guard\"]", StringComparison.Ordinal);
        var first = CardContentCompiler.Compile([new ContentSourceDocument("a.json", MinionJson)]);
        var second = CardContentCompiler.Compile([new ContentSourceDocument("b.json", reordered)]);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(first.Content!.Rules.Hash, second.Content!.Rules.Hash);
    }

    [Theory]
    [InlineData("[\"slow\"]")]
    [InlineData("[{\"kind\":\"slow\",\"turns\":0}]")]
    [InlineData("[{\"kind\":\"slow\",\"turns\":-1}]")]
    [InlineData("[\"unknown\"]")]
    public void RejectsInvalidP4Keywords(string keywords)
    {
        var json = MinionJson.Replace("[{ \"kind\": \"slow\", \"turns\": 1 }, \"guard\"]", keywords, StringComparison.Ordinal);

        var result = CardContentCompiler.Compile([new ContentSourceDocument("invalid.json", json)]);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, value => value.Code == "invalid-keyword");
    }

    [Theory]
    [InlineData("\"unknown\": true", "unknown-property")]
    [InlineData("\"effects\": [{\"kind\":\"not-yet-supported\"}]", "unknown-property")]
    public void RejectsUnknownFieldsAndMalformedEffects(string replacement, string expectedCode)
    {
        var json = MinionJson.Replace("\"effects\": []", replacement, StringComparison.Ordinal);

        var result = CardContentCompiler.Compile([new ContentSourceDocument("bad.json", json)]);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    [Fact]
    public void RejectsDuplicateCardIds()
    {
        var result = CardContentCompiler.Compile(
        [
            new ContentSourceDocument("a.json", MinionJson),
            new ContentSourceDocument("b.json", MinionJson)
        ]);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "duplicate-card-id");
    }

    [Fact]
    public void RejectsDuplicateJsonPropertiesBeforeDeserialization()
    {
        var json = MinionJson.Replace(
            "\"cost\": 1,",
            "\"cost\": 1,\n  \"cost\": 2,",
            StringComparison.Ordinal);

        var result = CardContentCompiler.Compile([new ContentSourceDocument("duplicate-property.json", json)]);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "duplicate-property");
    }
}
