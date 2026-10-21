using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Eota.Kernel.Content;
using Eota.Kernel.Primitives;

namespace Eota.Content.Compiler;

public sealed record ContentDependency(string Id, string? Version = null, string? Hash = null);
public sealed record ContentDeckEntry(string Id, int Copies);
public sealed record ContentDeck(string Name, string Profession, ImmutableArray<ContentDeckEntry> Cards);
public sealed record ContentProfession(string Id, string Name, string Description, string Crest, string Color, bool Playable, int Order = 0);

public sealed record ContentPackage(
    string SchemaVersion, string Id, string Version, string Name, string Hash,
    ImmutableArray<ContentDependency> Dependencies, ImmutableArray<JsonElement> Cards,
    ImmutableDictionary<string, string> Texts, ImmutableArray<ContentDeck> Decks)
{
    public const string Schema = "eota.content-pack/v3";
    public ImmutableArray<ContentProfession> Professions { get; init; } = [];
    public ImmutableDictionary<string, string> Assets { get; init; } = ImmutableDictionary<string, string>.Empty;
    [JsonIgnore] public string? AssetDirectory { get; init; }
    [JsonIgnore] public ContentPackIdentity Identity => new(Id, Version, Hash);
}

public sealed record LoadedContentPackages(ImmutableArray<ContentPackage> Packages, CompiledContentBundle Content,
    ImmutableDictionary<string, string> Texts, ImmutableArray<ContentDeck> Decks);

/// <summary>Validates package files before merging; no package may silently replace another package's definitions.</summary>
public static partial class ContentPackages
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex PackageIdPattern();
    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\\.[0-9A-Za-z-]+)*)?\\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    public static bool IsId(string? value) => value is not null && PackageIdPattern().IsMatch(value);
    public static bool IsVersion(string? value) => value is { Length: <= 96 } && VersionPattern().IsMatch(value);
    public static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static ContentPackage Read(string path)
    {
        if (new FileInfo(path).Length > 8 * 1024 * 1024) { throw new InvalidDataException("内容包超过 8 MiB：" + Path.GetFileName(path)); }
        return Parse(File.ReadAllText(path)) with { AssetDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) };
    }

    public static ContentPackage Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (JsonAuthoringValidator.TryFindDuplicateProperty(document.RootElement, "$", out var duplicate))
        { throw new InvalidDataException("内容包包含重复 JSON 属性：" + duplicate); }
        var pack = document.RootElement.Deserialize<ContentPackage>(JsonOptions) ?? throw new InvalidDataException("无效内容包。");
        Validate(pack);
        return pack;
    }

    public static ContentPackage Seal(ContentPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        ValidateStructure(package);
        return package with { Hash = ComputeHash(package) };
    }

    public static void Validate(ContentPackage package)
    {
        ValidateStructure(package);
        if (!IsHash(package.Hash) || package.Hash != ComputeHash(package))
        { throw new InvalidDataException($"内容包 {package.Id} {package.Version} 的 hash 校验失败，文件可能已被修改。"); }
    }

    private static void ValidateStructure(ContentPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.SchemaVersion != ContentPackage.Schema || !IsId(package.Id) || !IsVersion(package.Version)
            || package.Name is not { Length: > 0 and <= 120 } || package.Dependencies.IsDefault || package.Cards.IsDefault
            || package.Texts is null || package.Decks.IsDefault || package.Cards.Length > 2048 || package.Dependencies.Length > 128
            || package.Texts.Count > 16384 || package.Decks.Length > 256 || package.Professions.IsDefault || package.Professions.Length > 128
            || package.Assets is null || package.Assets.Count > 4096)
        { throw new InvalidDataException("内容包格式无效：需要合法的 id、版本、名称及 cards/texts/decks/dependencies。"); }
        if (package.Professions.Any(p => p is null || !Profession.TryParse(p.Id, out var id) || id.ToString() != p.Id
                || p.Name is not { Length: > 0 and <= 120 } || p.Description is not { Length: <= 8192 }
                || p.Crest is not { Length: > 0 and <= 16 } || p.Color is not { Length: 7 } || p.Color[0] != '#'
                || p.Color[1..].Any(c => !Uri.IsHexDigit(c)) || p.Playable && id == Profession.Neutral)
            || package.Professions.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != package.Professions.Length)
        { throw new InvalidDataException($"内容包 {package.Id} 的职业定义无效或重复。"); }
        if (package.Assets.Any(p => !PackageAssets.IsPath(p.Key) || !IsHash(p.Value)))
        { throw new InvalidDataException($"内容包 {package.Id} 的资源路径或 SHA-256 无效。"); }
        if (package.Dependencies.Any(d => d is null || !IsId(d.Id) || d.Id == package.Id
                || d.Version is not null && !IsVersion(d.Version) || d.Hash is not null && !IsHash(d.Hash))
            || package.Dependencies.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() != package.Dependencies.Length)
        { throw new InvalidDataException($"内容包 {package.Id} 的依赖无效、重复或引用了自身。"); }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in package.Cards)
        {
            if (JsonAuthoringValidator.TryFindDuplicateProperty(card, "$", out var duplicate))
            { throw new InvalidDataException($"内容包 {package.Id} 包含重复 JSON 属性：{duplicate}"); }
            if (card.ValueKind != JsonValueKind.Object || !card.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                || !CardPrototypeId.TryParse(id.GetString(), out _) || !ids.Add(id.GetString()!))
            { throw new InvalidDataException($"内容包 {package.Id} 包含无效或重复的卡牌 ID。"); }
        }
        if (package.Texts.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 256 || p.Value is null || p.Value.Length > 8192))
        { throw new InvalidDataException($"内容包 {package.Id} 的本地化文本无效。"); }
        if (package.Decks.Any(d => d is null || d.Name is not { Length: > 0 and <= 120 }
                || !Profession.TryParse(d.Profession, out var profession) || profession == Profession.Neutral
                || d.Cards.IsDefaultOrEmpty || d.Cards.Length > 256
                || d.Cards.Any(c => c is null || !CardPrototypeId.TryParse(c.Id, out _) || c.Copies is < 1 or > 256)))
        { throw new InvalidDataException($"内容包 {package.Id} 的牌组模板无效。"); }
    }

    public static string ComputeHash(ContentPackage package)
    {
        // Object order and formatting do not matter; array order inside card effects remains significant.
        var payload = JsonSerializer.SerializeToElement(new
        {
            package.SchemaVersion, package.Id, package.Version, package.Name,
            Dependencies = package.Dependencies.OrderBy(d => d.Id, StringComparer.Ordinal),
            Cards = package.Cards.OrderBy(c => c.GetProperty("id").GetString(), StringComparer.Ordinal),
            package.Texts, package.Decks, package.Professions, package.Assets
        }, JsonOptions);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) { WriteCanonical(writer, payload); }
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) { WriteCanonical(writer, item); } writer.WriteEndArray(); }
        else { value.WriteTo(writer); }
    }

    public static LoadedContentPackages Load(IEnumerable<ContentPackage> packages)
    {
        ArgumentNullException.ThrowIfNull(packages);
        var all = packages.OrderBy(p => p.Id, StringComparer.Ordinal).ToArray();
        if (all.Length is < 1 or > 128) { throw new InvalidDataException("请启用至少一个有效内容包（最多 128 个）。"); }
        foreach (var pack in all) { Validate(pack); }
        var duplicate = all.GroupBy(p => p.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) { throw new InvalidDataException("内容包 ID 冲突：" + duplicate.Key); }
        var byId = all.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var ordered = new List<ContentPackage>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new List<string>();
        void Visit(ContentPackage pack)
        {
            if (visited.Contains(pack.Id)) { return; }
            if (visiting.Contains(pack.Id, StringComparer.Ordinal))
            { throw new InvalidDataException("内容包循环依赖：" + string.Join(" → ", visiting.Append(pack.Id))); }
            visiting.Add(pack.Id);
            foreach (var dep in pack.Dependencies.OrderBy(d => d.Id, StringComparer.Ordinal))
            {
                if (!byId.TryGetValue(dep.Id, out var required))
                { throw new InvalidDataException($"{pack.Id} {pack.Version} 需要启用 {dep.Id}" + (dep.Version is null ? "。" : $" {dep.Version}。")); }
                if (dep.Version is not null && dep.Version != required.Version || dep.Hash is not null && dep.Hash != required.Hash)
                { throw new InvalidDataException($"{pack.Id} 的依赖不兼容：{dep.Id} 需要版本 {dep.Version ?? "任意"}、hash {dep.Hash ?? "任意"}；已启用版本 {required.Version}、hash {required.Hash}。"); }
                Visit(required);
            }
            visiting.RemoveAt(visiting.Count - 1); visited.Add(pack.Id); ordered.Add(pack);
        }
        foreach (var pack in all) { Visit(pack); }
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var textOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var professionOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var texts = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var pack in ordered)
        {
            foreach (var profession in pack.Professions)
            {
                if (!professionOwners.TryAdd(profession.Id, pack.Id))
                { throw new InvalidDataException($"职业 ID 冲突：{profession.Id} 同时属于 {professionOwners[profession.Id]} 和 {pack.Id}。"); }
            }
            foreach (var card in pack.Cards)
            {
                var id = card.GetProperty("id").GetString()!;
                if (owners.TryGetValue(id, out var owner)) { throw new InvalidDataException($"卡牌 ID 冲突：{id} 同时属于 {owner} 和 {pack.Id}。"); }
                owners.Add(id, pack.Id);
            }
            foreach (var (key, text) in pack.Texts)
            {
                if (textOwners.TryGetValue(key, out var owner)) { throw new InvalidDataException($"本地化键冲突：{key} 同时属于 {owner} 和 {pack.Id}。"); }
                textOwners.Add(key, pack.Id); texts.Add(key, text);
            }
        }
        if (owners.Count is < 1 or > 2048) { throw new InvalidDataException("已启用的内容包必须合计包含 1–2048 张卡牌。"); }
        var compiled = CardContentCompiler.Compile(ordered.SelectMany(p => p.Cards.Select(c => new ContentSourceDocument(p.Id + "/" + c.GetProperty("id").GetString(), c.GetRawText()))));
        if (!compiled.IsSuccess) { throw new InvalidDataException(string.Join('\n', compiled.Diagnostics.Select(d => $"{d.SourceName}: {d.Message}"))); }
        foreach (var pack in ordered)
        {
            var allowed = new HashSet<string>(StringComparer.Ordinal) { pack.Id };
            void Include(ContentPackage item)
            { foreach (var dep in item.Dependencies) { if (allowed.Add(dep.Id)) { Include(byId[dep.Id]); } } }
            Include(pack);
            void RequireProfession(string id)
            {
                var normalized = Profession.Parse(id).ToString();
                if (normalized == "Neutral") { return; }
                if (!professionOwners.TryGetValue(normalized, out var owner) || !allowed.Contains(owner))
                { throw new InvalidDataException($"{pack.Id} 引用了缺失或未声明依赖的职业 {id}。"); }
            }
            void CheckFilters(JsonElement value)
            {
                if (value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in value.EnumerateObject())
                    { if (property.Name == "profession" && property.Value.ValueKind == JsonValueKind.String) { RequireProfession(property.Value.GetString()!); } else { CheckFilters(property.Value); } }
                }
                else if (value.ValueKind == JsonValueKind.Array) { foreach (var child in value.EnumerateArray()) { CheckFilters(child); } }
            }
            foreach (var card in pack.Cards)
            {
                CheckFilters(card);
                var presentation = compiled.Content!.Presentation.Cards.Single(p => p.Id.Value == card.GetProperty("id").GetString());
                if (presentation.TexturePath.Length > 0 && presentation.TexturePath != PackageAssets.Placeholder)
                {
                    var (assetOwner, assetPath) = PackageAssets.Split(presentation.TexturePath);
                    if (!allowed.Contains(assetOwner) || !byId[assetOwner].Assets.ContainsKey(assetPath))
                    { throw new InvalidDataException($"{pack.Id} 的插图 {presentation.TexturePath} 缺失或未声明资源所属包的依赖。"); }
                }
                foreach (var key in new[] { presentation.NameLocalizationKey, presentation.DescriptionLocalizationKey })
                {
                    if (textOwners.TryGetValue(key, out var owner) && !allowed.Contains(owner))
                    { throw new InvalidDataException($"{pack.Id} 引用了 {owner} 的文本 {key}，但未声明该依赖。"); }
                }
                foreach (var id in CardRuleScope.Closure(compiled.Content!.Rules, [CardPrototypeId.Parse(card.GetProperty("id").GetString()!)]))
                { if (!allowed.Contains(owners[id.Value])) { throw new InvalidDataException($"{pack.Id} 引用了 {owners[id.Value]} 的卡牌 {id}，但未声明该依赖。"); } }
            }
            foreach (var deck in pack.Decks)
            {
                RequireProfession(deck.Profession);
                if (!ordered.SelectMany(p => p.Professions).Any(p => p.Id == Profession.Parse(deck.Profession).ToString() && p.Playable))
                { throw new InvalidDataException($"牌组 {deck.Name} 的职业不能用于对战。"); }
                foreach (var entry in deck.Cards)
                { if (!owners.TryGetValue(entry.Id, out var owner) || !allowed.Contains(owner)) { throw new InvalidDataException($"{pack.Id} 的牌组 {deck.Name} 引用了缺失或未声明依赖的卡牌 {entry.Id}。"); } }
            }
        }
        return new([.. ordered], compiled.Content!, texts.ToImmutable(), [.. ordered.SelectMany(p => p.Decks)]);
    }
}
