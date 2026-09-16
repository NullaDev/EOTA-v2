using System.Collections.Immutable;
using System.Text.Json;
using Eota.Kernel.Content;
using Eota.Kernel.Primitives;

namespace Eota.Content.Compiler;

public static class CardContentCompiler
{
    public const string SupportedSchemaVersion = "eota.card/v2";

    private static readonly HashSet<string> SharedProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "kind",
        "id",
        "source",
        "profession",
        "cost",
        "effects",
        "tags",
        "storedCharge",
        "nameLocalizationKey",
        "descriptionLocalizationKey",
        "texturePath"
    };

    public static ContentCompilationResult Compile(IEnumerable<ContentSourceDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var diagnostics = ImmutableArray.CreateBuilder<ContentDiagnostic>();
        var rules = new List<CardDefinition>();
        var presentation = new List<PresentationCardDefinition>();

        foreach (var document in documents.OrderBy(value => value.SourceName, StringComparer.Ordinal))
        {
            CompileDocument(document, diagnostics, rules, presentation);
        }

        foreach (var duplicateGroup in rules.GroupBy(card => card.Id).Where(group => group.Count() > 1))
        {
            diagnostics.Add(new ContentDiagnostic(
                ContentDiagnosticSeverity.Error,
                "duplicate-card-id",
                duplicateGroup.Key.Value,
                "$.id",
                $"Card prototype ID '{duplicateGroup.Key}' is defined more than once."));
        }

        if (diagnostics.Any(value => value.Severity == ContentDiagnosticSeverity.Error))
        {
            return new ContentCompilationResult(null, SortDiagnostics(diagnostics));
        }

        try
        {
            var rulePack = RuleContentPack.Create(SupportedSchemaVersion, rules);
            var presentationPack = PresentationPack.Create(presentation);
            return new ContentCompilationResult(
                new CompiledContentBundle(rulePack, presentationPack),
                SortDiagnostics(diagnostics));
        }
        catch (ArgumentException exception)
        {
            diagnostics.Add(new ContentDiagnostic(
                ContentDiagnosticSeverity.Error,
                "invalid-rule-content",
                "content-pack",
                "$",
                exception.Message));
            return new ContentCompilationResult(null, SortDiagnostics(diagnostics));
        }
    }

    private static void CompileDocument(
        ContentSourceDocument document,
        ImmutableArray<ContentDiagnostic>.Builder diagnostics,
        List<CardDefinition> rules,
        List<PresentationCardDefinition> presentation)
    {
        try
        {
            using var json = JsonDocument.Parse(document.Json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });

            if (json.RootElement.ValueKind != JsonValueKind.Object)
            {
                AddError(diagnostics, document.SourceName, "$", "expected-object", "A card document must be a JSON object.");
                return;
            }

            if (JsonAuthoringValidator.TryFindDuplicateProperty(json.RootElement, "$", out var duplicatePath))
            {
                AddError(diagnostics, document.SourceName, duplicatePath, "duplicate-property", "Duplicate JSON properties are not allowed.");
                return;
            }

            var root = json.RootElement;
            var schemaVersion = ReadRequiredString(root, "schemaVersion", document.SourceName, diagnostics);
            var kindText = ReadRequiredString(root, "kind", document.SourceName, diagnostics);
            var idText = ReadRequiredString(root, "id", document.SourceName, diagnostics);
            if (schemaVersion is null || kindText is null || idText is null)
            {
                return;
            }

            if (!string.Equals(schemaVersion, SupportedSchemaVersion, StringComparison.Ordinal))
            {
                AddError(diagnostics, document.SourceName, "$.schemaVersion", "unsupported-schema", $"Expected '{SupportedSchemaVersion}'.");
                return;
            }

            if (!TryParseCardKind(kindText, out var kind))
            {
                AddError(diagnostics, document.SourceName, "$.kind", "invalid-enum", $"Unknown card kind '{kindText}'.");
                return;
            }

            ValidateProperties(root, kind, document.SourceName, diagnostics);

            if (!CardPrototypeId.TryParse(idText, out var id))
            {
                AddError(diagnostics, document.SourceName, "$.id", "invalid-card-id", "The card ID has an invalid format.");
                return;
            }

            var sourceText = ReadOptionalString(root, "source", document.SourceName, diagnostics) ?? "core";
            var professionText = ReadOptionalString(root, "profession", document.SourceName, diagnostics) ?? "neutral";
            var cost = ReadOptionalInt64(root, "cost", 0, document.SourceName, diagnostics);
            if (!TryParseCardSource(sourceText, out var source))
            {
                AddError(diagnostics, document.SourceName, "$.source", "invalid-enum", $"Unknown card source '{sourceText}'.");
                return;
            }

            if (!TryParseProfession(professionText, out var profession))
            {
                AddError(diagnostics, document.SourceName, "$.profession", "invalid-enum", $"Unknown profession '{professionText}'.");
                return;
            }

            CardDefinition? card = kind switch
            {
                CardKind.Minion => CompileMinion(root, id, source, profession, cost, document.SourceName, diagnostics),
                CardKind.Field => CompileField(root, id, source, profession, cost, document.SourceName, diagnostics),
                CardKind.Spell => CompileSpell(root, id, source, profession, cost, document.SourceName, diagnostics),
                _ => null
            };

            if (card is null)
            {
                return;
            }

            card = card with { Effects = EffectCompiler.Compile(root, card, document.SourceName, diagnostics) };
            var storedCharge = ReadOptionalInt64(root, "storedCharge", 0, document.SourceName, diagnostics);
            if (storedCharge < 0 || (storedCharge != 0 && card.Kind == CardKind.Spell))
            { AddError(diagnostics, document.SourceName, "$.storedCharge", "invalid-stored-charge", "Stored charge requires a battlefield card and a nonnegative value."); }
            else { card = card with { StoredCharge = storedCharge }; }
            if (root.TryGetProperty("tags", out var tags))
            {
                if (tags.ValueKind != JsonValueKind.Array || tags.GetArrayLength() > 32
                    || tags.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String || !CardPrototypeId.TryParse(value.GetString(), out _)))
                { AddError(diagnostics, document.SourceName, "$.tags", "invalid-tags", "Tags must be at most 32 stable ASCII identifiers."); }
                else { card = card with { Tags = tags.EnumerateArray().Select(value => value.GetString()!).ToImmutableArray() }; }
            }
            rules.Add(card);
            presentation.Add(new PresentationCardDefinition(
                id,
                ReadOptionalString(root, "nameLocalizationKey", document.SourceName, diagnostics) ?? DefaultNameKey(kind, id),
                ReadOptionalString(root, "descriptionLocalizationKey", document.SourceName, diagnostics) ?? DefaultDescriptionKey(kind, id),
                ReadOptionalString(root, "texturePath", document.SourceName, diagnostics) ?? string.Empty));
        }
        catch (JsonException exception)
        {
            AddError(
                diagnostics,
                document.SourceName,
                "$",
                "invalid-json",
                $"Invalid JSON at line {exception.LineNumber}, byte {exception.BytePositionInLine}.");
        }
    }

    private static MinionCardDefinition? CompileMinion(
        JsonElement root,
        CardPrototypeId id,
        CardSource source,
        Profession profession,
        long cost,
        string sourceName,
        ImmutableArray<ContentDiagnostic>.Builder diagnostics)
    {
        var attack = ReadRequiredInt64(root, "attack", sourceName, diagnostics);
        var health = ReadRequiredInt64(root, "health", sourceName, diagnostics);
        var keywords = ReadKeywords(root, sourceName, diagnostics);
        return attack is null || health is null || keywords is null
            ? null
            : new MinionCardDefinition(id, source, profession, cost, attack.Value, health.Value, keywords.Value);
    }

    private static FieldCardDefinition? CompileField(
        JsonElement root,
        CardPrototypeId id,
        CardSource source,
        Profession profession,
        long cost,
        string sourceName,
        ImmutableArray<ContentDiagnostic>.Builder diagnostics)
    {
        var lifetime = ReadFieldLifetime(root, sourceName, diagnostics);
        var preventsAttacks = ReadOptionalBoolean(root, "preventsActiveAttacksInLane", false, sourceName, diagnostics);
        var keywords = ReadFieldKeywords(root, sourceName, diagnostics);
        return lifetime is null || keywords is null
            ? null
            : new FieldCardDefinition(id, source, profession, cost, lifetime, preventsAttacks, keywords.Value);
    }

    private static ImmutableArray<FieldKeywordKind>? ReadFieldKeywords(
        JsonElement root,
        string sourceName,
        ImmutableArray<ContentDiagnostic>.Builder diagnostics)
    {
        if (!root.TryGetProperty("keywords", out var value))
        {
            return ImmutableArray<FieldKeywordKind>.Empty;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            AddError(diagnostics, sourceName, "$.keywords", "invalid-type", "Keywords must be an array.");
            return null;
        }

        var result = ImmutableArray.CreateBuilder<FieldKeywordKind>();
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            FieldKeywordKind? keyword = item.ValueKind == JsonValueKind.String
                ? item.GetString() switch
                {
                    "replace" => FieldKeywordKind.Replace,
                    "replaceable" => FieldKeywordKind.Replaceable,
                    _ => null
                }
                : null;
            if (keyword is { } kind)
            {
                result.Add(kind);
            }
            else
            {
                AddError(diagnostics, sourceName, $"$.keywords[{index}]", "invalid-keyword",
                    "Fields support the string keywords 'replace' and 'replaceable'.");
            }

            index++;
        }

        return result.Distinct().OrderBy(keyword => keyword).ToImmutableArray();
    }

    private static IFieldLifetimeDefinition? ReadFieldLifetime(
        JsonElement root,
        string sourceName,
        ImmutableArray<ContentDiagnostic>.Builder diagnostics)
    {
        if (!root.TryGetProperty("lifetime", out var lifetime))
        {
            AddError(diagnostics, sourceName, "$.lifetime", "missing-property", "A field must declare its lifetime.");
            return null;
        }

        if (lifetime.ValueKind != JsonValueKind.Object)
        {
            AddError(diagnostics, sourceName, "$.lifetime", "invalid-type", "Field lifetime must be an object.");
            return null;
        }

        foreach (var property in lifetime.EnumerateObject())
        {
            if (property.Name is not ("kind" or "energy"))
            {
                AddError(diagnostics, sourceName, $"$.lifetime.{property.Name}", "unknown-property", $"Unknown field lifetime property '{property.Name}'.");
            }
        }

        var kind = ReadRequiredString(lifetime, "kind", sourceName, diagnostics, "$.lifetime");
        if (string.Equals(kind, "finite", StringComparison.Ordinal))
        {
            var energy = ReadRequiredInt64(lifetime, "energy", sourceName, diagnostics, "$.lifetime");
            return energy is null ? null : new FiniteFieldLifetimeDefinition(energy.Value);
        }

        if (string.Equals(kind, "permanent", StringComparison.Ordinal))
        {
            if (lifetime.TryGetProperty("energy", out _))
            {
                AddError(diagnostics, sourceName, "$.lifetime.energy", "unexpected-property", "Permanent fields do not have energy.");
                return null;
            }

            return new PermanentFieldLifetimeDefinition();
        }

        if (kind is not null)
        {
            AddError(diagnostics, sourceName, "$.lifetime.kind", "invalid-enum", $"Unknown field lifetime kind '{kind}'.");
        }

        return null;
    }

    private static SpellCardDefinition? CompileSpell(
        JsonElement root,
        CardPrototypeId id,
        CardSource source,
        Profession profession,
        long cost,
        string sourceName,
        ImmutableArray<ContentDiagnostic>.Builder diagnostics)
    {
        var speedText = ReadOptionalString(root, "speed", sourceName, diagnostics) ?? "fast";
        var targetScopeText = ReadOptionalString(root, "targetScope", sourceName, diagnostics) ?? "global";
        if (!TryParseSpellSpeed(speedText, out var speed))
        {
            AddError(diagnostics, sourceName, "$.speed", "invalid-enum", $"Unknown spell speed '{speedText}'.");
            return null;
        }

        if (!TryParseTargetScope(targetScopeText, out var targetScope))
        {
            AddError(diagnostics, sourceName, "$.targetScope", "invalid-enum", $"Unknown target scope '{targetScopeText}'.");
            return null;
        }

        return new SpellCardDefinition(id, source, profession, cost, speed, targetScope);
    }

    private static ImmutableArray<MinionKeywordDefinition>? ReadKeywords(
        JsonElement root,
        string sourceName,
        ImmutableArray<ContentDiagnostic>.Builder diagnostics)
    {
        if (!root.TryGetProperty("keywords", out var value))
        {
            return ImmutableArray<MinionKeywordDefinition>.Empty;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            AddError(diagnostics, sourceName, "$.keywords", "invalid-type", "Keywords must be an array.");
            return null;
        }

        var result = ImmutableArray.CreateBuilder<MinionKeywordDefinition>();
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String
                && TryParseKeyword(item.GetString(), out var simpleKeyword)
                && simpleKeyword != MinionKeywordKind.Slow)
            {
                result.Add(new MinionKeywordDefinition(simpleKeyword));
            }
            else if (item.ValueKind == JsonValueKind.Object
                     && item.TryGetProperty("kind", out var kindElement)
                     && kindElement.ValueKind == JsonValueKind.String
                     && TryParseKeyword(kindElement.GetString(), out var parameterizedKeyword)
                     && parameterizedKeyword == MinionKeywordKind.Slow
                     && item.TryGetProperty("turns", out var turnsElement)
                     && turnsElement.TryGetInt64(out var turns)
                     && turns > 0)
            {
                result.Add(new MinionKeywordDefinition(parameterizedKeyword, turns));
            }
            else
            {
                AddError(
                    diagnostics,
                    sourceName,
                    $"$.keywords[{index}]",
                    "invalid-keyword",
                    "Use a supported string keyword, or {\"kind\":\"slow\",\"turns\":N} for slow.");
            }

            index++;
        }

        return result
            .Distinct()
            .OrderBy(keyword => keyword.Kind)
            .ThenBy(keyword => keyword.Parameter)
            .ToImmutableArray();
    }

    private static void ValidateProperties(
        JsonElement root,
        CardKind kind,
        string sourceName,
        ImmutableArray<ContentDiagnostic>.Builder diagnostics)
    {
        var allowed = new HashSet<string>(SharedProperties, StringComparer.Ordinal);
        switch (kind)
        {
            case CardKind.Minion:
                allowed.UnionWith(["attack", "health", "keywords"]);
                break;
            case CardKind.Field:
                allowed.UnionWith(["lifetime", "preventsActiveAttacksInLane", "keywords"]);
                break;
            case CardKind.Spell:
                allowed.UnionWith(["speed", "targetScope"]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        foreach (var property in root.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                AddError(diagnostics, sourceName, $"$.{property.Name}", "unknown-property", $"Unknown property '{property.Name}'.");
            }
        }
    }

    private static string? ReadRequiredString(
        JsonElement root,
        string propertyName,
        string sourceName,
        ImmutableArray<ContentDiagnostic>.Builder diagnostics,
        string parentPath = "$")
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            AddError(diagnostics, sourceName, $"{parentPath}.{propertyName}", "missing-property", $"Property '{propertyName}' is required.");
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            AddError(diagnostics, sourceName, $"{parentPath}.{propertyName}", "invalid-type", $"Property '{propertyName}' must be a non-empty string.");
            return null;
        }

        return value.GetString();
    }

    private static string? ReadOptionalString(
        JsonElement root,
        string propertyName,
        string sourceName,
        ImmutableArray<ContentDiagnostic>.Builder diagnostics)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            AddError(diagnostics, sourceName, $"$.{propertyName}", "invalid-type", $"Property '{propertyName}' must be a string.");
            return null;
        }

        return value.GetString();
    }

    private static long? ReadRequiredInt64(
        JsonElement root,
        string propertyName,
        string sourceName,
        ImmutableArray<ContentDiagnostic>.Builder diagnostics,
        string parentPath = "$")
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            AddError(diagnostics, sourceName, $"{parentPath}.{propertyName}", "missing-property", $"Property '{propertyName}' is required.");
            return null;
        }

        if (!value.TryGetInt64(out var result))
        {
            AddError(diagnostics, sourceName, $"{parentPath}.{propertyName}", "invalid-type", $"Property '{propertyName}' must be a 64-bit integer.");
            return null;
        }

        return result;
    }

    private static long ReadOptionalInt64(
        JsonElement root,
        string propertyName,
        long defaultValue,
        string sourceName,
        ImmutableArray<ContentDiagnostic>.Builder diagnostics)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return defaultValue;
        }

        if (!value.TryGetInt64(out var result))
        {
            AddError(diagnostics, sourceName, $"$.{propertyName}", "invalid-type", $"Property '{propertyName}' must be a 64-bit integer.");
            return defaultValue;
        }

        return result;
    }

    private static bool ReadOptionalBoolean(
        JsonElement root,
        string propertyName,
        bool defaultValue,
        string sourceName,
        ImmutableArray<ContentDiagnostic>.Builder diagnostics)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return defaultValue;
        }

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            AddError(diagnostics, sourceName, $"$.{propertyName}", "invalid-type", $"Property '{propertyName}' must be a boolean.");
            return defaultValue;
        }

        return value.GetBoolean();
    }

    private static bool TryParseCardKind(string value, out CardKind result) =>
        TryParseEnum(value, out result, ("minion", CardKind.Minion), ("field", CardKind.Field), ("spell", CardKind.Spell));

    private static bool TryParseCardSource(string value, out CardSource result) =>
        TryParseEnum(value, out result, ("core", CardSource.Core), ("token", CardSource.Token), ("test", CardSource.Test));

    private static bool TryParseProfession(string value, out Profession result) =>
        TryParseEnum(
            value,
            out result,
            ("neutral", Profession.Neutral),
            ("arcanist", Profession.Arcanist),
            ("guardian", Profession.Guardian),
            ("hunter", Profession.Hunter),
            ("artisan", Profession.Artisan),
            ("soulbinder", Profession.Soulbinder));

    private static bool TryParseSpellSpeed(string value, out SpellSpeed result) =>
        TryParseEnum(value, out result, ("fast", SpellSpeed.Fast), ("slow", SpellSpeed.Slow));

    private static bool TryParseTargetScope(string value, out SpellTargetScope result) =>
        TryParseEnum(value, out result, ("global", SpellTargetScope.Global), ("lane", SpellTargetScope.Lane));

    private static bool TryParseKeyword(string? value, out MinionKeywordKind result) =>
        TryParseEnum(
            value,
            out result,
            ("swift", MinionKeywordKind.Swift),
            ("guard", MinionKeywordKind.Guard),
            ("slow", MinionKeywordKind.Slow),
            ("replace", MinionKeywordKind.Replace),
            ("replaceable", MinionKeywordKind.Replaceable),
            ("lifesteal", MinionKeywordKind.Lifesteal),
            ("skirmisher", MinionKeywordKind.Skirmisher),
            ("pursuit", MinionKeywordKind.Pursuit),
            ("firstStrike", MinionKeywordKind.FirstStrike),
            ("execute", MinionKeywordKind.Execute));

    private static bool TryParseEnum<T>(string? value, out T result, params (string Name, T Value)[] candidates)
        where T : struct
    {
        foreach (var candidate in candidates)
        {
            if (string.Equals(value, candidate.Name, StringComparison.Ordinal))
            {
                result = candidate.Value;
                return true;
            }
        }

        result = default;
        return false;
    }

    private static string DefaultNameKey(CardKind kind, CardPrototypeId id) => $"{KindPrefix(kind)}.{id.Value}.name";

    private static string DefaultDescriptionKey(CardKind kind, CardPrototypeId id) => $"{KindPrefix(kind)}.{id.Value}.description";

    private static string KindPrefix(CardKind kind) => kind switch
    {
        CardKind.Minion => "minion",
        CardKind.Field => "field",
        CardKind.Spell => "spell",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static void AddError(
        ImmutableArray<ContentDiagnostic>.Builder diagnostics,
        string sourceName,
        string path,
        string code,
        string message)
    {
        diagnostics.Add(new ContentDiagnostic(ContentDiagnosticSeverity.Error, code, sourceName, path, message));
    }

    private static ImmutableArray<ContentDiagnostic> SortDiagnostics(
        IEnumerable<ContentDiagnostic> diagnostics)
    {
        return diagnostics
            .OrderBy(value => value.SourceName, StringComparer.Ordinal)
            .ThenBy(value => value.Path, StringComparer.Ordinal)
            .ThenBy(value => value.Code, StringComparer.Ordinal)
            .ToImmutableArray();
    }
}
