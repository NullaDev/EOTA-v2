using System.Collections.Immutable;
using System.Text.Json;
using Eota.Content.Compiler;
using Eota.Kernel.Content;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Protocols;

namespace Eota.Client.Desktop;

// Catalog projection of a card prototype. It contains no instance identity or current health.
public sealed record CardPresentation(string Id, string Name, string Description, string TexturePath,
    string Kind, string Profession, string Source, long Cost, long? Attack, long? Health, string? Speed, bool Global,
    ImmutableArray<string> Keywords = default)
{
    public long? Durability { get; init; }
    public ImmutableArray<string> Tags { get; init; } = [];
    public ImmutableDictionary<string, string> TagNames { get; init; } = ImmutableDictionary<string, string>.Empty;
    public string TypeLabel => Kind == "Minion" && !Tags.IsEmpty ? string.Join("·", Tags.Select(tag => TagNames.GetValueOrDefault(tag, TagLabel(tag)))) + "·随从"
        : Kind == "Minion" ? "随从" : Kind == "Field" ? "场地" : Global ? "全局法术" : "路线法术";
    public static string TagLabel(string tag) => tag;
}
public sealed record DeckCard(string Id, int Copies);
public sealed record DesktopDeck(string Name, string Profession, ImmutableArray<DeckCard> Cards);
public sealed record LocalMatchSettings(ulong Seed = 146, int LaneCount = 6, long HeroHealth = 30, bool Mulligan = true, string? ProtocolJson = null,
    DesktopAiSettings? Ai = null);

// Composition boundary: file content and Kernel types stay outside the scene scripts.
public sealed partial class DesktopCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly RuleContentPack _rules;
    public ImmutableArray<CardPresentation> Cards { get; }
    public string RuleHash => _rules.Hash.ToString();
    public ImmutableDictionary<string, string> CardRules { get; }
    public string Root { get; }
    public string? ContentPackPath { get; }
    public ImmutableArray<ContentPackage> Packages { get; }
    public ImmutableArray<Eota.Transport.Contracts.RoomContentPack> ContentPacks =>
        [.. Packages.Select(p => new Eota.Transport.Contracts.RoomContentPack(p.Id, p.Version, p.Hash))];
    public ImmutableArray<DesktopDeck> ArchetypeDecks { get; }
    public ImmutableArray<ContentProfession> Professions { get; }
    public string ResolveTexture(string reference)
    {
        if (reference.Length == 0 || reference == PackageAssets.Placeholder) { return "res://Client/Assets/card-placeholder.svg"; }
        var (id, path) = PackageAssets.Split(reference);
        var pack = Packages.SingleOrDefault(p => p.Id == id) ?? throw new InvalidDataException("未启用资源所属包：" + id);
        if (!pack.Assets.ContainsKey(path)) { throw new InvalidDataException("未登记的资源：" + reference); }
        return PackageAssets.FilePath(pack, path);
    }
    internal JsonElement[] SourceCards { get; }
    internal Dictionary<string, string> Texts { get; }

    public DesktopCatalog(string root, string? contentPackPath = null)
        : this(root, DesktopContentPackages.LoadFile(root, contentPackPath), contentPackPath) { }

    public DesktopCatalog(string root, IEnumerable<ContentPackage> packages)
        : this(root, ContentPackages.Load(packages), null) { }

    private DesktopCatalog(string root, LoadedContentPackages loaded, string? contentPackPath)
    {
        Root = Path.GetFullPath(root);
        ContentPackPath = contentPackPath;
        Packages = loaded.Packages;
        foreach (var package in Packages) { PackageAssets.ValidateFiles(package); }
        Professions = [.. Packages.SelectMany(p => p.Professions).OrderBy(p => p.Order).ThenBy(p => p.Id, StringComparer.Ordinal)];
        SourceCards = loaded.Packages.SelectMany(p => p.Cards).ToArray();
        _rules = loaded.Content.Rules;
        CardRules = _rules.Cards.ToImmutableDictionary(card => card.Id.Value, card => _rules.CardRuleHash(card.Id).ToString(), StringComparer.Ordinal);
        AiCards = ProjectAiCards(_rules);
        var texts = loaded.Texts.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        Texts = texts;
        var presentation = loaded.Content.Presentation.Cards.ToDictionary(card => card.Id);
        var owners = Packages.SelectMany(p => p.Cards.Select(c => (Id: c.GetProperty("id").GetString()!, Owner: p.Id)))
            .ToDictionary(p => p.Id, p => p.Owner, StringComparer.Ordinal);
        foreach (var card in presentation.Values) { _ = ResolveTexture(card.TexturePath); }
        Cards = _rules.Cards.Select(card => new CardPresentation(card.Id.Value,
            texts.GetValueOrDefault(presentation[card.Id].NameLocalizationKey, card.Id.Value),
            texts.GetValueOrDefault(presentation[card.Id].DescriptionLocalizationKey, ""),
            presentation[card.Id].TexturePath, card.Kind.ToString(), card.Profession.ToString(), card.Source.ToString(), card.Cost,
            (card as MinionCardDefinition)?.Attack, (card as MinionCardDefinition)?.Health, (card as SpellCardDefinition)?.Speed.ToString(),
            card is SpellCardDefinition { TargetScope: SpellTargetScope.Global })
        { Tags = card.Tags, TagNames = card.Tags.ToImmutableDictionary(tag => tag, tag => texts.GetValueOrDefault("tag." + owners[card.Id.Value] + "." + tag, tag), StringComparer.Ordinal),
            Keywords = KeywordBriefs(card), Durability = card is FieldCardDefinition { Lifetime: FiniteFieldLifetimeDefinition finite } ? finite.InitialEnergy : null })
            .OrderBy(card => card.Id, StringComparer.Ordinal).ToImmutableArray();
        BuildRelatedCards();
        ArchetypeDecks = loaded.Decks.Select(deck => new DesktopDeck(deck.Name, deck.Profession, [.. deck.Cards.Select(c => new DeckCard(c.Id, c.Copies))]))
            .Where(deck => deck.Cards.All(entry => _rules.Cards.Any(card => card.Id.Value == entry.Id && card.Source == CardSource.Core))).ToImmutableArray();
    }

    public ImmutableArray<CardPresentation> ConstructibleCards(string profession, DesktopProtocol protocol)
    {
        if (!Professions.Any(p => p.Id == profession && p.Playable)) { return []; }
        var allowed = _rules.Cards.Where(card => MatchFactory.IsCardAllowed(card, Profession.Parse(profession), protocol.Definition.DeckConstructionPolicy))
            .Select(card => card.Id.Value).ToHashSet(StringComparer.Ordinal);
        return [.. Cards.Where(card => allowed.Contains(card.Id)).OrderBy(card => card.Cost).ThenBy(card => card.Id, StringComparer.Ordinal)];
    }

    public DesktopDeck DefaultDeck(string profession, DesktopProtocol? protocol = null)
    {
        protocol ??= DesktopProtocol.Default;
        var curated = ArchetypeDecks.FirstOrDefault(deck => deck.Profession == profession && ValidateDeck(deck, protocol).IsEmpty);
        if (curated is not null) { return curated with { Name = profession }; }
        var choices = ConstructibleCards(profession, protocol);
        var entries = new Dictionary<string, int>(StringComparer.Ordinal);
        var count = Enumerable.Range(1, choices.Length).LastOrDefault(count => count * protocol.MinCopies <= protocol.RequiredDeckSize
            && count * protocol.MaxCopies >= protocol.RequiredDeckSize);
        if (count == 0) { return new DesktopDeck(profession, profession, []); }
        foreach (var card in choices.Take(count)) { entries[card.Id] = protocol.MinCopies; }
        for (var index = 0; entries.Values.Sum() < protocol.RequiredDeckSize; index++)
        {
            var card = choices[index % count];
            if (entries[card.Id] < protocol.MaxCopies) { entries[card.Id]++; }
        }
        return new DesktopDeck(profession, profession, entries.Select(value => new DeckCard(value.Key, value.Value)).ToImmutableArray());
    }

    internal MatchCreationRequest CreateRequest(DesktopDeck one, DesktopDeck two, LocalMatchSettings settings)
    {
        var protocol = settings.ProtocolJson is { } json ? DesktopProtocol.Compile(json) : CompiledGameProtocol.Compile(GameProtocolDefinition.DefaultV0 with
        {
            LaneCount = settings.LaneCount,
            InitialHeroHealth = settings.HeroHealth,
            MulliganEnabled = settings.Mulligan,
            MovementConflictPolicy = settings.LaneCount % 2 == 0 ? MovementConflictPolicy.CenterFirst : MovementConflictPolicy.AllFail
        });
        return new MatchCreationRequest(protocol, _rules, settings.Seed, ToDeck(one), ToDeck(two));
    }

    public ImmutableArray<string> MissingCards(DesktopDeck deck)
    {
        ArgumentNullException.ThrowIfNull(deck);
        return [.. deck.Cards.Select(c => c.Id).Where(id => !CardRules.ContainsKey(id)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    public ImmutableArray<string> MissingContent(DesktopDeck deck) =>
        Professions.Any(p => p.Id == deck.Profession && p.Playable) ? MissingCards(deck) : ["职业：" + deck.Profession, .. MissingCards(deck)];

    public void RequireAvailableDeck(DesktopDeck deck)
    {
        var missing = MissingCards(deck);
        if (!Professions.Any(p => p.Id == deck.Profession && p.Playable))
        { throw new InvalidDataException($"“{deck.Name}”的职业 {deck.Profession} 未启用；牌组已保留，暂不能编辑或对战。"); }
        if (!missing.IsEmpty)
        { throw new InvalidDataException($"“{deck.Name}”缺少 {missing.Length} 种卡牌，暂不能编辑或对战。请启用对应内容包；牌组仍保留。\n" + string.Join("、", missing)); }
    }

    public ImmutableArray<string> ValidateDeck(DesktopDeck deck, DesktopProtocol? protocol = null)
    {
        if (!Professions.Any(p => p.Id == deck.Profession && p.Playable)) { return ["missing-profession: " + deck.Profession]; }
        var missing = MissingCards(deck);
        if (!missing.IsEmpty) { return ["missing-content: " + string.Join(", ", missing)]; }
        return MatchFactory.Create(CreateRequest(deck, deck, new LocalMatchSettings(ProtocolJson: protocol?.Json))).Errors
            .Select(value => value.DetailCode).Distinct(StringComparer.Ordinal).ToImmutableArray();
    }

    private static DeckDefinition ToDeck(DesktopDeck deck) => DeckDefinition.Create(Profession.Parse(deck.Profession),
        deck.Cards.Select(card => new DeckEntry(CardPrototypeId.Parse(card.Id), card.Copies)));

    // Baseline keywords as displayed on the card face, so the client can explain them without
    // maintaining its own copy of the English keyword list. Assembly-wide because the content
    // editor's preview builds a CardPresentation too.
    internal static ImmutableArray<string> KeywordBriefs(CardDefinition card)
    {
        var briefs = new List<string>();
        if (card is MinionCardDefinition minion) { briefs.AddRange(minion.Keywords.Select(MinionKeywordBrief)); }
        else if (card is FieldCardDefinition field) { briefs.AddRange(field.Keywords.Select(FieldKeywordBrief)); }
        if (card.StoredCharge > 0) { briefs.Add("蓄能 " + card.StoredCharge); }
        return [.. briefs];
    }

    private static string MinionKeywordBrief(MinionKeywordDefinition keyword) => keyword.Kind switch
    {
        MinionKeywordKind.Slow => "迟缓 " + keyword.Parameter,
        _ => MinionKeywordName(keyword.Kind)
    };

    private static string MinionKeywordName(MinionKeywordKind kind) => kind switch
    {
        MinionKeywordKind.Swift => "迅捷", MinionKeywordKind.Guard => "守备", MinionKeywordKind.Slow => "迟缓",
        MinionKeywordKind.Replace => "替换", MinionKeywordKind.Replaceable => "可替换", MinionKeywordKind.Lifesteal => "吸血",
        MinionKeywordKind.Skirmisher => "游击", MinionKeywordKind.Pursuit => "追猎", MinionKeywordKind.FirstStrike => "先攻",
        MinionKeywordKind.Execute => "斩杀", _ => kind.ToString()
    };

    private static string FieldKeywordBrief(FieldKeywordKind kind) => kind switch
    {
        FieldKeywordKind.Replace => "替换", FieldKeywordKind.Replaceable => "可替换", _ => kind.ToString()
    };

    public static void SaveDeck(string path, DesktopDeck deck) => File.WriteAllText(path,
        JsonSerializer.Serialize(deck, JsonOptions));
    public static DesktopDeck LoadDeck(string path) => JsonSerializer.Deserialize<DesktopDeck>(File.ReadAllText(path))
        ?? throw new InvalidDataException("Invalid deck file.");
}
