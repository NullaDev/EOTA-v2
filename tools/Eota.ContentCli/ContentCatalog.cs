using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Eota.Content.Compiler;
using Eota.Kernel.Content;
using Eota.Kernel.Effects;

namespace Eota.ContentCli;

public sealed class ContentCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string _root;
    private readonly ImmutableArray<ContentSourceDocument> _sources;
    private readonly SortedDictionary<string, string> _localization;
    private readonly SortedDictionary<string, ArtRecipe> _art;
    private readonly SortedDictionary<string, ArtRecipe> _icons;
    public CompiledContentBundle Bundle { get; }

    private ContentCatalog(string root, ImmutableArray<ContentSourceDocument> sources, CompiledContentBundle bundle,
        SortedDictionary<string, string> localization, SortedDictionary<string, ArtRecipe> art, SortedDictionary<string, ArtRecipe> icons)
    { _root = root; _sources = sources; Bundle = bundle; _localization = localization; _art = art; _icons = icons; }

    public static ContentCatalog Load(string repositoryRoot)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var sources = Directory.EnumerateFiles(Path.Combine(root, "Content", "Source", "Cards"), "*.json", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => new ContentSourceDocument(Path.GetRelativePath(root, path), File.ReadAllText(path))).ToImmutableArray();
        var compiled = CardContentCompiler.Compile(sources);
        if (!compiled.IsSuccess) { throw new InvalidDataException(string.Join("\n", compiled.Diagnostics.Select(value => $"{value.SourceName}:{value.Path} {value.Code}: {value.Message}"))); }
        var language = new SortedDictionary<string, string>(Read<Dictionary<string, string>>(Path.Combine(root, "Content", "Source", "Localization", "zh-CN.json")), StringComparer.Ordinal);
        var recipes = new SortedDictionary<string, ArtRecipe>(Read<Dictionary<string, ArtRecipe>>(Path.Combine(root, "Content", "Source", "Art", "emoji-recipes.json")), StringComparer.Ordinal);
        var palettes = Read<Dictionary<string, ProfessionPalette>>(Path.Combine(root, "Content", "Source", "Art", "profession-palettes.json"));
        var icons = new SortedDictionary<string, ArtRecipe>(Read<Dictionary<string, ArtRecipe>>(Path.Combine(root, "Content", "Source", "Art", "ui-icons.json")), StringComparer.Ordinal);
        foreach (var card in compiled.Content!.Presentation.Cards)
        {
            if (!language.TryGetValue(card.NameLocalizationKey, out var name) || string.IsNullOrWhiteSpace(name)
                || !language.TryGetValue(card.DescriptionLocalizationKey, out var description) || string.IsNullOrWhiteSpace(description))
            { throw new InvalidDataException($"Missing localization for {card.Id}."); }
            if (!recipes.TryGetValue(card.Id.Value, out var recipe)) { throw new InvalidDataException($"Missing artwork recipe for {card.Id}."); }
            var profession = compiled.Content.Rules.Cards.Single(rule => rule.Id == card.Id).Profession.ToString().ToLowerInvariant();
            if (recipe.Profession != profession || !palettes.TryGetValue(profession, out var palette)
                || !string.Equals(recipe.Top, palette.Top, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(recipe.Bottom, palette.Bottom, StringComparison.OrdinalIgnoreCase))
            { throw new InvalidDataException($"Artwork palette for {card.Id} must match profession {profession}. Run node tools/EmojiArt/sync-profession-colors.mjs."); }
            _ = EmojiArt.Svg(recipe.Emoji, recipe.Top, recipe.Bottom, recipe.Style, ReadFusion(root, card.Id.Value, recipe));
        }
        foreach (var (id, recipe) in icons)
        {
            if (id.Length == 0 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            { throw new InvalidDataException("Invalid icon ID."); }
            _ = EmojiArt.Svg(recipe.Emoji, recipe.Top, recipe.Bottom, recipe.Style);
        }
        return new ContentCatalog(root, sources, compiled.Content, language, recipes, icons);
    }

    public string RenderTable()
    {
        var lines = new List<string> { "# VNext 测试卡表", "", "> 自动生成自 Content/Source。实验设计，尚未验证平衡性；数值可在后续测试中调整。", "" };
        foreach (var group in Bundle.Rules.Cards.GroupBy(card => (card.Source, card.Profession)).OrderBy(value => value.Key.Source).ThenBy(value => value.Key.Profession))
        {
            lines.Add($"## {group.Key.Source} / {group.Key.Profession}"); lines.Add("");
            lines.Add("| 类型 | 卡名 | 种族／标签 | 费用 | 身材／耐久 | 效果 | ID |"); lines.Add("|---|---|---|---:|---|---|---|");
            foreach (var card in group.OrderBy(value => value.Kind).ThenBy(value => value.Cost).ThenBy(value => value.Id))
            {
                var presentation = Bundle.Presentation.Cards.Single(value => value.Id == card.Id);
                var stats = card switch
                {
                    MinionCardDefinition minion => FormattableString.Invariant($"{minion.Attack}/{minion.Health}"),
                    FieldCardDefinition { Lifetime: FiniteFieldLifetimeDefinition finite } => FormattableString.Invariant($"{finite.InitialEnergy} 回合"),
                    FieldCardDefinition => "永久",
                    _ => "—"
                };
                var tags = card.Tags.IsEmpty ? "—" : string.Join("、", card.Tags.Select(tag => tag switch
                {
                    "beast" => "野兽", "mechanical" => "机械", "firearm" => "火器营", "hemomancer" => "血术师", "undead" => "亡灵", _ => tag
                }));
                lines.Add($"| {card.Kind switch { CardKind.Minion => "随从", CardKind.Field => "场地", _ => "法术" }} | {Escape(_localization[presentation.NameLocalizationKey])} | {Escape(tags)} | {card.Cost.ToString(CultureInfo.InvariantCulture)} | {stats} | {Escape(_localization[presentation.DescriptionLocalizationKey])} | `{card.Id}` |");
            }
            lines.Add("");
        }
        return string.Join('\n', lines);
    }

    public void Build(string outputDirectory)
    {
        var output = Path.GetFullPath(outputDirectory);
        var sourceRoot = Path.Combine(_root, "Content", "Source") + Path.DirectorySeparatorChar;
        if ((output + Path.DirectorySeparatorChar).StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase)
            || (sourceRoot).StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        { throw new ArgumentException("Build output must be separate from authored sources."); }
        Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Combine(output, "rules.bin"), Bundle.Rules.EncodeCanonical());
        var sourceCards = _sources.Select(value => JsonNode.Parse(value.Json)!).OrderBy(value => value["id"]!.GetValue<string>(), StringComparer.Ordinal).ToArray();
        // Recompilable sources accompany the canonical compiled bytes; no polymorphic IR data is lost.
        WriteJson("cards.sources.json", sourceCards);
        WriteJson("presentation.json", Bundle.Presentation.Cards.Select(value => new
        {
            id = value.Id.Value,
            nameKey = value.NameLocalizationKey,
            descriptionKey = value.DescriptionLocalizationKey,
            texturePath = value.TexturePath,
            emojiPreviewPath = $"Art/{value.Id}.svg"
        }));
        WriteJson("zh-CN.json", _localization);
        WriteJson("emoji-recipes.json", _art);
        WriteJson("ui-icons.json", _icons);
        Program.Write(Path.Combine(output, "archetype-decks.json"), File.ReadAllText(Path.Combine(_root, "Content", "Source", "Decks", "archetypes.json")));
        foreach (var (id, recipe) in _art.Where(pair => Bundle.Rules.Cards.Any(card => card.Id.Value == pair.Key)))
        { Program.Write(Path.Combine(output, "Art", id + ".svg"), EmojiArt.Svg(recipe.Emoji, recipe.Top, recipe.Bottom, recipe.Style, ReadFusion(_root, id, recipe))); }
        foreach (var (id, recipe) in _icons)
        { Program.Write(Path.Combine(output, "Icons", id + ".svg"), EmojiArt.Svg(recipe.Emoji, recipe.Top, recipe.Bottom, recipe.Style)); }
        Program.Write(Path.Combine(output, "CardTable.zh-CN.md"), RenderTable());
        Program.Write(Path.Combine(output, "Coverage.zh-CN.md"), RenderCoverage());
        Program.Write(Path.Combine(output, "BaselineDiff.zh-CN.md"), RenderBaselineDiff());
        WriteJson("manifest.json", new
        {
            schema = "eota.experimental-content/v1",
            designStatus = "experimental",
            balanceValidated = false,
            count = Bundle.Rules.Cards.Length,
            ruleHash = Bundle.Rules.Hash.ToString(),
            canonicalVersion = RuleContentPack.CanonicalSchemaVersion,
            presentationHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            { cards = Bundle.Presentation.Cards, localization = _localization, art = _art, icons = _icons }, JsonOptions))).ToLowerInvariant()
        });
        void WriteJson<T>(string name, T value) => Program.Write(Path.Combine(output, name), JsonSerializer.Serialize(value, JsonOptions) + "\n");
    }

    public string RenderBaselineDiff()
    {
        using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "Content", "Design", "card-baseline.json")));
        var previous = baseline.RootElement.GetProperty("cards").EnumerateArray().Select(value => value.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
        var current = Bundle.Rules.Cards.Select(value => value.Id.Value).ToHashSet(StringComparer.Ordinal);
        var lines = new List<string> { "# 测试设计基线差异", "", $"原始清单 {previous.Count} 张；当前 {current.Count} 张。基线仅保存 CardTable 文本元数据，不读取旧卡牌 JSON。", "",
            "新增：" + string.Join(", ", current.Except(previous).Order(StringComparer.Ordinal)),
            "缺失：" + string.Join(", ", previous.Except(current).Order(StringComparer.Ordinal)), "", "数值和文本变更：" };
        foreach (var old in baseline.RootElement.GetProperty("cards").EnumerateArray())
        {
            var id = old.GetProperty("id").GetString()!;
            var card = Bundle.Rules.Cards.SingleOrDefault(value => value.Id.Value == id);
            if (card is null) { continue; }
            var p = Bundle.Presentation.Cards.Single(value => value.Id == card.Id);
            var stats = card switch { MinionCardDefinition m => FormattableString.Invariant($"{m.Attack}/{m.Health}"), FieldCardDefinition { Lifetime: FiniteFieldLifetimeDefinition f } => FormattableString.Invariant($"{f.InitialEnergy}回合"), _ => "-" };
            if (old.GetProperty("cost").GetInt64() != card.Cost || old.GetProperty("stats").GetString() != stats
                || old.GetProperty("name").GetString() != _localization[p.NameLocalizationKey]
                || (old.GetProperty("description").GetString() is { Length: > 0 } description && description != _localization[p.DescriptionLocalizationKey]))
            { lines.Add($"- {id}：费用、身材或文本与初始测试设计不同，请复核。允许为平衡测试修改，不强制回写旧值。"); }
        }
        return string.Join('\n', lines) + "\n";
    }

    public string RenderCoverage()
    {
        var uses = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var card in Bundle.Rules.Cards)
        {
            foreach (var effect in card.Effects) { Add("trigger/" + effect.Trigger.Kind, card.Id.Value); Walk(effect.Body, card.Id.Value); }
            if (card.Effects.IsEmpty) { Add("static/card", card.Id.Value); }
        }
        var lines = new List<string> { "# 内容效果能力覆盖", "", "按编译后的 IR 生成；静态卡另列。此矩阵表示使用关系，不代替运行场景断言。", "", "| 能力 | 使用卡牌 |", "|---|---|" };
        foreach (var (key, values) in uses) { lines.Add($"| {key} | {string.Join(", ", values)} |"); }
        lines.Add(""); lines.Add("未使用的根触发器：" + string.Join(", ", Enum.GetValues<EffectTriggerKind>().Select(value => "trigger/" + value).Where(value => !uses.ContainsKey(value))));
        lines.Add("未使用的叶动作：" + string.Join(", ", Enum.GetValues<EffectAction>().Where(action => !uses.Keys.Any(key => key == "intent/" + action || key.StartsWith("intent/" + action + "/", StringComparison.Ordinal)))));
        lines.Add("未使用的效果节点：" + string.Join(", ", typeof(EffectNode).Assembly.GetTypes().Where(type => !type.IsAbstract && type.IsAssignableTo(typeof(EffectNode)))
            .Select(type => type.Name).Where(name => !uses.ContainsKey("node/" + name)).Order(StringComparer.Ordinal)));
        return string.Join('\n', lines) + "\n";
        void Add(string key, string id) { if (!uses.TryGetValue(key, out var ids)) { ids = new(StringComparer.Ordinal); uses.Add(key, ids); } ids.Add(id); }
        void Walk(EffectNode node, string id)
        {
            Add("node/" + node.GetType().Name, id);
            switch (node)
            {
                case EmitEffect emit: Add("intent/" + emit.Action + (emit.Action == EffectAction.ModifyNumber ? "/" + emit.Attribute : ""), id); break;
                case HandEffect hand: Add("intent/Hand/" + hand.Kind, id); break;
                case LifecycleEffect life: Add("intent/Lifecycle/" + life.Operation, id); break;
                case SummonEffect: Add("intent/Summon", id); break;
                case ParallelEffect parallel: foreach (var child in parallel.Children) { Walk(child, id); } break;
                case SequenceEffect sequence: foreach (var step in sequence.Steps) { Walk(step, id); } break;
                case LoopEffect loop: Walk(loop.Body, id); break;
                case RetargetEffect retarget: Walk(retarget.Body, id); break;
                case IfElseEffect condition: Walk(condition.Then, id); if (condition.Else is { } other) { Walk(other, id); } break;
                case GrantEffect grant: Add("trigger/" + grant.Definition.Trigger.Kind, id); Walk(grant.Definition.Body, id); break;
            }
        }
    }

    private static byte[]? ReadFusion(string root, string id, ArtRecipe recipe)
    {
        var graphemes = new StringInfo(recipe.Emoji).LengthInTextElements;
        if (recipe.Fusion is null && graphemes != 1)
        { throw new InvalidDataException($"Non-fused artwork for {id} must contain exactly one emoji grapheme."); }
        if (recipe.Fusion is not null && graphemes != 2)
        { throw new InvalidDataException($"Fused artwork for {id} must retain exactly two source emoji graphemes."); }
        if (recipe.Fusion is null)
        {
            if (recipe.FusionSource is not null) { throw new InvalidDataException($"Fusion source without a local image for {id}."); }
            return null;
        }
        if (recipe.Fusion != $"Fusions/{id}.png" || recipe.Fusion.Contains('\\') || recipe.Fusion.Contains("..", StringComparison.Ordinal))
        { throw new InvalidDataException($"Invalid fusion image path for {id}."); }
        if (recipe.FusionSource is null || !Uri.TryCreate(recipe.FusionSource, UriKind.Absolute, out var source)
            || source.Scheme != Uri.UriSchemeHttps || source.Host != "www.gstatic.com"
            || !source.AbsolutePath.StartsWith("/android/keyboard/emojikitchen/", StringComparison.Ordinal))
        { throw new InvalidDataException($"Missing HTTPS fusion source for {id}."); }
        var artRoot = Path.GetFullPath(Path.Combine(root, "Content", "Source", "Art")) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(artRoot, recipe.Fusion.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(artRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        { throw new InvalidDataException($"Missing fusion image for {id}."); }
        return File.ReadAllBytes(path);
    }

    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), ReadOptions) ?? throw new InvalidDataException(path);
    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", "<br>", StringComparison.Ordinal);
}

public sealed record ArtRecipe(string Emoji, string Top, string Bottom, string Style = "card",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Fusion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FusionSource = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Profession = null);

public sealed record ProfessionPalette(string Label, string Top, string Bottom);
