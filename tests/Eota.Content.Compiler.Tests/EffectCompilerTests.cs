using Eota.Kernel.Content;
using Eota.Kernel.Effects;
using System.Text.Json.Nodes;

namespace Eota.Content.Compiler.Tests;

public sealed class EffectCompilerTests
{
    [Theory]
    [InlineData("1.5")]
    [InlineData("1 +")]
    [InlineData("9223372036854775807 + 1")]
    [InlineData("1 / (2 - 2)")]
    [InlineData("Math.Random()")]
    public void InvalidArithmeticIsRejectedBeforeMatchCreation(string expression)
    {
        var result = CardContentCompiler.Compile([new ContentSourceDocument("bad.json", Card(expression))]);
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Path == "$.effects[0].body.amount");
    }

    [Fact]
    public void EffectRootOrderDoesNotChangeContentHashButDuplicateIdsAndBudgetsAreRejected()
    {
        var document = JsonNode.Parse(Card("1"))!;
        var effects = document["effects"]!.AsArray();
        var second = effects[0]!.DeepClone();
        second["id"] = "another";
        second["body"]!["amount"] = "2";
        effects.Add(second);
        var original = Compile(document);
        var first = effects[0]!;
        effects.RemoveAt(0);
        effects.Add(first);
        Assert.Equal(original.Content!.Rules.Hash, Compile(document).Content!.Rules.Hash);
        second["id"] = "entry-hit";
        Assert.Contains(Compile(document).Diagnostics, value => value.Code == "duplicate-effect-id");
        second["id"] = "another";
        var children = new JsonArray();
        for (var index = 0; index < 129; index++) { children.Add(first["body"]!.DeepClone()); }
        first["body"] = new JsonObject { ["kind"] = "parallel", ["children"] = children };
        Assert.Contains(Compile(document).Diagnostics, value => value.Code == "effect-budget-exceeded");
    }

    [Fact]
    public void ConstantExpressionsFoldWithPrecedenceAndInt64MinimumIsSupported()
    {
        var content = CardContentCompiler.Compile([new ContentSourceDocument("constant.json", Card("-(2 + 3) * 4 / 2"))]);
        Assert.True(content.IsSuccess);
        Assert.Equal(-10, Assert.IsType<ConstantExpression>(Assert.IsType<EmitEffect>(content.Content!.Rules.Cards[0].Effects[0].Body).Amount).Value);
        Assert.True(CardContentCompiler.Compile([new ContentSourceDocument("minimum.json", Card("-9223372036854775808"))]).IsSuccess);
    }

    [Fact]
    public void FieldKeywordEffectsAreTypedAndRejectMinionOnlyKeywords()
    {
        const string field = """
            {"schemaVersion":"eota.card/v2","source":"test","profession":"neutral","kind":"field","id":"TEST-P6-FIELD","cost":0,
             "lifetime":{"kind":"permanent"},"effects":[{"id":"entry","trigger":{"kind":"selfEntered"},"body":
              {"kind":"addKeyword","target":{"kind":"self"},"keyword":"replaceable"}}]}
            """;
        var turn = EffectRuntimeTests.Resolve(EffectRuntimeTests.Plan(EffectRuntimeTests.Create([field]), Eota.Kernel.Primitives.PlayerId.One, "TEST-P6-FIELD", 0));
        Assert.Equal(FieldKeywordKind.Replaceable, Assert.Single(Assert.IsType<Eota.Kernel.Matches.FieldEntityState>(Assert.Single(turn.State.Entities)).Keywords));
        var bad = CardContentCompiler.Compile([new ContentSourceDocument("field.json", field.Replace("replaceable", "swift", StringComparison.Ordinal))]);
        Assert.Contains(bad.Diagnostics, value => value.Code == "effect-target-type-mismatch");
    }

    private static ContentCompilationResult Compile(JsonNode value) =>
        CardContentCompiler.Compile([new ContentSourceDocument("card.json", value.ToJsonString())]);

    [Fact]
    public void CanonicallyEquivalentEffectIdsHaveIdenticalRuntimeIdentityAndRejectDuplicates()
    {
        var document = JsonNode.Parse(Card("1"))!;
        var effects = document["effects"]!.AsArray();
        effects[0]!["id"] = "e\u0301";
        var decomposed = Compile(document);
        effects[0]!["id"] = "\u00e9";
        var composed = Compile(document);
        Assert.True(decomposed.IsSuccess);
        Assert.True(composed.IsSuccess);
        Assert.Equal(composed.Content!.Rules.Hash, decomposed.Content!.Rules.Hash);
        Assert.Equal(composed.Content.Rules.Cards[0].Effects[0].Id, decomposed.Content.Rules.Cards[0].Effects[0].Id);
        var duplicate = effects[0]!.DeepClone();
        duplicate["id"] = "e\u0301";
        effects.Add(duplicate);
        Assert.Contains(Compile(document).Diagnostics, value => value.Code == "duplicate-effect-id");
    }

    [Fact]
    public void EntryDamageCompilesToTypedIrAndParticipatesInContentHash()
    {
        var first = CardContentCompiler.Compile([new ContentSourceDocument("hunter.json", Card("1"))]);
        var second = CardContentCompiler.Compile([new ContentSourceDocument("hunter.json", Card("2"))]);
        Assert.True(first.IsSuccess, string.Join(";", first.Diagnostics.Select(value => value.Message)));
        Assert.True(second.IsSuccess);
        var effect = Assert.Single(first.Content!.Rules.Cards[0].Effects);
        Assert.Equal(EffectTriggerKind.SelfEntered, effect.Trigger.Kind);
        Assert.IsType<EmitEffect>(effect.Body);
        Assert.NotEqual(first.Content.Rules.Hash, second.Content!.Rules.Hash);
    }

    [Theory]
    [InlineData("target.health", "invalid-variable-scope")]
    [InlineData("unknown.value", "unknown-variable")]
    [InlineData("1 / 0", "invalid-expression")]
    public void BadExpressionsHaveSourceDiagnostics(string amount, string expectedCode)
    {
        var result = CardContentCompiler.Compile([new ContentSourceDocument("hunter.json", Card(amount))]);
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, value => value.Code == expectedCode && value.SourceName == "hunter.json"
            && value.Path.StartsWith("$.effects", StringComparison.Ordinal));
    }

    [Fact]
    public void RetargetBindsTargetVariablesAndRejectsTargetTypeMismatch()
    {
        var body = """
            {"kind":"retarget","target":{"kind":"friendlyMinions","scope":"lane"},"body":
              {"kind":"damage","target":{"kind":"targets"},"amount":"target.attack + 1"}}
            """;
        var json = Card("1");
        var original = "{\"kind\":\"damage\",\"target\":{\"kind\":\"enemyMinions\",\"scope\":\"lane\"},\"amount\":\"1\"}";
        Assert.True(CardContentCompiler.Compile([new ContentSourceDocument("retarget.json", json.Replace(original, body, StringComparison.Ordinal))]).IsSuccess);
        var wrongType = json.Replace("enemyMinions", "enemyFields", StringComparison.Ordinal);
        var result = CardContentCompiler.Compile([new ContentSourceDocument("wrong.json", wrongType)]);
        Assert.Contains(result.Diagnostics, value => value.Code == "effect-target-type-mismatch");
    }

    internal static string Card(string amount) => """
        {"schemaVersion":"eota.card/v2","kind":"minion","id":"TEST-P6-HUNTER","source":"test","profession":"neutral",
         "cost":2,"attack":2,"health":1,"effects":[{"id":"entry-hit","trigger":{"kind":"selfEntered"},
         "body":{"kind":"damage","target":{"kind":"enemyMinions","scope":"lane"},"amount":"AMOUNT"}}]}
        """.Replace("AMOUNT", amount, StringComparison.Ordinal);
}
