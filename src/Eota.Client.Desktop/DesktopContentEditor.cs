using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eota.Content.Compiler;
using Eota.Kernel.Content;

namespace Eota.Client.Desktop;

// A complete, recompilable V2 pack. Saving a draft never overwrites the bundled card set.
public sealed record DesktopContentPack(int Version, string RuleHash, JsonElement[] Cards, Dictionary<string, string> Texts);
public sealed record EditableCard(string Json, string Name, string Description);

public sealed class DesktopContentEditor
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly string[] AbilityKeys = ["effects", "keywords", "storedCharge", "preventsActiveAttacksInLane"];
    private readonly SortedDictionary<string, JsonObject> _cards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _texts;
    public IEnumerable<string> CardIds => _cards.Keys;

    public DesktopContentEditor(DesktopCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        foreach (var card in catalog.SourceCards) { _cards.Add(card.GetProperty("id").GetString()!, JsonNode.Parse(card.GetRawText())!.AsObject()); }
        _texts = new(catalog.Texts, StringComparer.Ordinal);
    }

    public EditableCard Read(string id)
    {
        var card = _cards[id];
        return new(card.ToJsonString(Options), _texts.GetValueOrDefault(card["nameLocalizationKey"]?.GetValue<string>() ?? $"card.{id}.name", id),
            _texts.GetValueOrDefault(card["descriptionLocalizationKey"]?.GetValue<string>() ?? $"card.{id}.description", ""));
    }

    public static EditableCard NewCard(string kind = "minion")
    {
        var id = "EOTA-USER-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var card = new JsonObject { ["schemaVersion"] = "eota.card/v2", ["id"] = id, ["source"] = "core", ["profession"] = "neutral",
            ["kind"] = kind, ["cost"] = 1, ["effects"] = new JsonArray(), ["texturePath"] = "Content/Generated/Art/EOTA-CORE-NEU-MIN-001.png" };
        ChangeKind(card, kind);
        return new(card.ToJsonString(Options), "新卡牌", "");
    }

    public static void ChangeKind(JsonObject card, string kind)
    {
        ArgumentNullException.ThrowIfNull(card);
        foreach (var key in new[] { "attack", "health", "keywords", "speed", "targetScope", "lifetime", "preventsActiveAttacksInLane", "storedCharge" }) { card.Remove(key); }
        card["kind"] = kind;
        switch (kind)
        {
            case "minion": card["attack"] = 1; card["health"] = 1; card["keywords"] = new JsonArray(); break;
            case "spell": card["speed"] = "fast"; card["targetScope"] = "lane"; break;
            case "field": card["lifetime"] = new JsonObject { ["kind"] = "finite", ["energy"] = 3 }; break;
            default: throw new ArgumentException("Unknown card kind.", nameof(kind));
        }
    }

    public static string AbilitiesJson(JsonObject card)
    {
        var abilities = new JsonObject { ["effects"] = card["effects"]?.DeepClone() ?? new JsonArray() };
        if (card["kind"]!.GetValue<string>() != "spell")
        { abilities["keywords"] = card["keywords"]?.DeepClone() ?? new JsonArray(); abilities["storedCharge"] = card["storedCharge"]?.DeepClone() ?? JsonValue.Create(0); }
        if (card["kind"]!.GetValue<string>() == "field")
        { abilities["preventsActiveAttacksInLane"] = card["preventsActiveAttacksInLane"]?.DeepClone() ?? JsonValue.Create(false); }
        return abilities.ToJsonString(Options);
    }

    public static void ApplyAbilities(JsonObject card, string json)
    {
        var element = Eota.Transport.Contracts.ContractJson.Deserialize<JsonElement>(json);
        if (element.ValueKind != JsonValueKind.Object) { throw new InvalidDataException("效果 JSON 应是包含 effects 等能力字段的对象。"); }
        foreach (var property in element.EnumerateObject())
        { if (!AbilityKeys.Contains(property.Name, StringComparer.Ordinal)) { throw new InvalidDataException($"效果 JSON 不支持属性：{property.Name}"); } }
        foreach (var key in AbilityKeys) { card.Remove(key); }
        foreach (var property in element.EnumerateObject()) { card[property.Name] = JsonNode.Parse(property.Value.GetRawText()); }
    }

    public ImmutableArray<string> Validate(EditableCard value, string? replacingId = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        // Compile the raw document before parsing to a mutable DOM, preserving duplicate-key diagnostics.
        var documents = _cards.Where(pair => pair.Key != replacingId).Select(pair => new ContentSourceDocument(pair.Key, pair.Value.ToJsonString()))
            .Append(new ContentSourceDocument("编辑中的卡牌", value.Json));
        var result = CardContentCompiler.Compile(documents);
        return result.Diagnostics.Select(d => $"{d.SourceName} {d.Path}: {d.Code} — {d.Message}").ToImmutableArray();
    }

    public string SaveCard(EditableCard value, string? replacingId = null)
    {
        var errors = Validate(value, replacingId);
        if (!errors.IsEmpty) { throw new InvalidDataException(string.Join('\n', errors)); }
        var card = JsonNode.Parse(value.Json)!.AsObject(); var id = card["id"]!.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 120 || value.Description.Length > 8192)
        { throw new InvalidDataException("卡名不能为空且不能超过 120 字，说明不能超过 8192 字。"); }
        card["nameLocalizationKey"] = $"card.{id}.name"; card["descriptionLocalizationKey"] = $"card.{id}.description";
        if (replacingId is not null && replacingId != id) { _cards.Remove(replacingId); }
        _cards[id] = card; _texts[$"card.{id}.name"] = value.Name; _texts[$"card.{id}.description"] = value.Description;
        return id;
    }

    public CardPresentation Preview(EditableCard value, string? replacingId = null)
    {
        var errors = Validate(value, replacingId);
        if (!errors.IsEmpty) { throw new InvalidDataException(string.Join('\n', errors)); }
        var single = CardContentCompiler.Compile([new ContentSourceDocument("preview", value.Json)]);
        // Cross-card references need the complete pack during compilation.
        var compiled = single.IsSuccess ? single : CardContentCompiler.Compile(_cards.Where(pair => pair.Key != replacingId)
            .Select(pair => new ContentSourceDocument(pair.Key, pair.Value.ToJsonString())).Append(new ContentSourceDocument("preview", value.Json)));
        using var json = JsonDocument.Parse(value.Json); var id = json.RootElement.GetProperty("id").GetString()!;
        var card = compiled.Content!.Rules.Cards.Single(c => c.Id.Value == id);
        var art = compiled.Content.Presentation.Cards.Single(c => c.Id == card.Id).TexturePath;
        return new(id, value.Name, value.Description, art, card.Kind.ToString(), card.Profession.ToString(), card.Source.ToString(), card.Cost,
            (card as MinionCardDefinition)?.Attack, (card as MinionCardDefinition)?.Health,
            (card as SpellCardDefinition)?.Speed.ToString(), card is SpellCardDefinition { TargetScope: SpellTargetScope.Global })
        { Tags = card.Tags, Durability = card is FieldCardDefinition { Lifetime: FiniteFieldLifetimeDefinition finite } ? finite.InitialEnergy : null };
    }

    public void DeleteCard(string id)
    {
        var remaining = _cards.Where(pair => pair.Key != id).Select(pair => new ContentSourceDocument(pair.Key, pair.Value.ToJsonString()));
        var result = CardContentCompiler.Compile(remaining);
        if (!result.IsSuccess) { throw new InvalidDataException(string.Join('\n', result.Diagnostics.Select(d => d.Message))); }
        _cards.Remove(id); _texts.Remove($"card.{id}.name"); _texts.Remove($"card.{id}.description");
    }

    public DesktopContentPack Export()
    {
        var result = CardContentCompiler.Compile(_cards.Select(pair => new ContentSourceDocument(pair.Key, pair.Value.ToJsonString())));
        if (!result.IsSuccess) { throw new InvalidDataException(string.Join('\n', result.Diagnostics.Select(d => d.Message))); }
        return new(1, result.Content!.Rules.Hash.ToString(), _cards.Values.Select(card => JsonSerializer.SerializeToElement(card)).ToArray(), new(_texts));
    }

    public void SavePack(string path) => WriteAtomic(path, JsonSerializer.Serialize(Export(), Options));

    public DesktopContentPack ExportCards(IEnumerable<string> ids)
    {
        var complete = Export();
        var compiled = CardContentCompiler.Compile(complete.Cards.Select(card => new ContentSourceDocument(card.GetProperty("id").GetString()!, card.GetRawText()))).Content!;
        var scope = CardRuleScope.Closure(compiled.Rules, ids.Select(Eota.Kernel.Primitives.CardPrototypeId.Parse)).ToHashSet();
        if (scope.Count == 0) { throw new InvalidDataException("请先选择要导出的卡牌。"); }
        var cards = complete.Cards.Where(card => scope.Contains(Eota.Kernel.Primitives.CardPrototypeId.Parse(card.GetProperty("id").GetString()!))).ToArray();
        var rules = RuleContentPack.Create(compiled.Rules.SchemaVersion, compiled.Rules.Cards.Where(card => scope.Contains(card.Id)));
        var keys = compiled.Presentation.Cards.Where(card => scope.Contains(card.Id))
            .SelectMany(card => new[] { card.NameLocalizationKey, card.DescriptionLocalizationKey }).ToHashSet(StringComparer.Ordinal);
        return new(1, rules.Hash.ToString(), cards, _texts.Where(pair => keys.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }

    public void SaveCards(string path, IEnumerable<string> ids) => WriteAtomic(path, JsonSerializer.Serialize(ExportCards(ids), Options));

    // A card export can be merged into the current draft without replacing unrelated cards.
    public void ImportCards(string path)
    {
        var imported = LoadPack(path);
        var combined = new SortedDictionary<string, JsonObject>(_cards, StringComparer.Ordinal);
        foreach (var card in imported.Cards) { combined[card.GetProperty("id").GetString()!] = JsonNode.Parse(card.GetRawText())!.AsObject(); }
        if (combined.Count > 2048) { throw new InvalidDataException("内容包最多支持 2048 张卡牌。"); }
        var compiled = CardContentCompiler.Compile(combined.Select(pair => new ContentSourceDocument(pair.Key, pair.Value.ToJsonString())));
        if (!compiled.IsSuccess) { throw new InvalidDataException(string.Join('\n', compiled.Diagnostics.Select(d => d.Message))); }
        _cards.Clear(); foreach (var pair in combined) { _cards.Add(pair.Key, pair.Value); }
        foreach (var pair in imported.Texts) { _texts[pair.Key] = pair.Value; }
    }

    public static DesktopContentPack LoadPack(string path)
    {
        if (new FileInfo(path).Length > 8 * 1024 * 1024) { throw new InvalidDataException("内容包超过 8 MB。"); }
        var pack = JsonSerializer.Deserialize<DesktopContentPack>(File.ReadAllText(path)) ?? throw new InvalidDataException("无效内容包。");
        if (pack.Version != 1 || pack.Cards is not { Length: > 0 and <= 2048 } || pack.Texts is null) { throw new InvalidDataException("不支持的内容包。"); }
        var compiled = CardContentCompiler.Compile(pack.Cards.Select((card, index) => new ContentSourceDocument(index.ToString(System.Globalization.CultureInfo.InvariantCulture), card.GetRawText())));
        if (!compiled.IsSuccess || compiled.Content!.Rules.Hash.ToString() != pack.RuleHash)
        { throw new InvalidDataException("内容包校验失败：" + string.Join('\n', compiled.Diagnostics.Select(d => $"{d.Path}: {d.Message}"))); }
        return pack;
    }

    internal static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, text); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }
}
