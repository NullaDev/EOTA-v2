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
    string Kind, string Profession, string Source, long Cost, long? Attack, long? Health, string? Speed, bool Global)
{
    public long? Durability { get; init; }
    public ImmutableArray<string> Tags { get; init; } = [];
    public string TypeLabel => Kind == "Minion" && !Tags.IsEmpty ? string.Join("·", Tags.Select(TagLabel)) + "·随从"
        : Kind == "Minion" ? "随从" : Kind == "Field" ? "场地" : Global ? "全局法术" : "路线法术";
    public static string TagLabel(string tag) => tag switch
    {
        "beast" => "野兽", "mechanical" => "机械", "firearm" => "火器营", "hemomancer" => "血术师", _ => tag
    };
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
    public ImmutableArray<DesktopDeck> ArchetypeDecks { get; }
    internal JsonElement[] SourceCards { get; }
    internal Dictionary<string, string> Texts { get; }

    public DesktopCatalog(string root, string? contentPackPath = null)
    {
        Root = Path.GetFullPath(root);
        ContentPackPath = contentPackPath;
        var pack = contentPackPath is null ? null : DesktopContentEditor.LoadPack(contentPackPath);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "Content", "Generated", "cards.sources.json")));
        SourceCards = pack?.Cards ?? document.RootElement.EnumerateArray().Select(card => card.Clone()).ToArray();
        var sources = SourceCards.Select(card =>
            new ContentSourceDocument(card.GetProperty("id").GetString()!, card.GetRawText()));
        var compiled = CardContentCompiler.Compile(sources);
        if (!compiled.IsSuccess) { throw new InvalidDataException(string.Join("; ", compiled.Diagnostics.Select(value => value.Message))); }
        _rules = compiled.Content!.Rules;
        CardRules = _rules.Cards.ToImmutableDictionary(card => card.Id.Value, card => _rules.CardRuleHash(card.Id).ToString(), StringComparer.Ordinal);
        AiCards = ProjectAiCards(_rules);
        var texts = pack?.Texts ?? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Root, "Content", "Generated", "zh-CN.json")))!;
        Texts = texts;
        var presentation = compiled.Content.Presentation.Cards.ToDictionary(card => card.Id);
        foreach (var card in presentation.Values)
        {
            if (!card.TexturePath.StartsWith("Content/Generated/Art/", StringComparison.Ordinal)
                || !card.TexturePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || card.TexturePath.Contains("..", StringComparison.Ordinal)
                || card.TexturePath.Contains('\\') || !File.Exists(Path.Combine(Root, card.TexturePath)))
            { throw new InvalidDataException($"卡牌 {card.Id} 的插图不存在，请使用现有 Content/Generated/Art/*.png 资源。"); }
        }
        Cards = _rules.Cards.Select(card => new CardPresentation(card.Id.Value,
            texts.GetValueOrDefault(presentation[card.Id].NameLocalizationKey, card.Id.Value),
            texts.GetValueOrDefault(presentation[card.Id].DescriptionLocalizationKey, ""),
            presentation[card.Id].TexturePath, card.Kind.ToString(), card.Profession.ToString(), card.Source.ToString(), card.Cost,
            (card as MinionCardDefinition)?.Attack, (card as MinionCardDefinition)?.Health, (card as SpellCardDefinition)?.Speed.ToString(),
            card is SpellCardDefinition { TargetScope: SpellTargetScope.Global })
        { Tags = card.Tags, Durability = card is FieldCardDefinition { Lifetime: FiniteFieldLifetimeDefinition finite } ? finite.InitialEnergy : null })
            .OrderBy(card => card.Id, StringComparer.Ordinal).ToImmutableArray();
        BuildRelatedCards();
        ArchetypeDecks = JsonSerializer.Deserialize<ImmutableArray<DesktopDeck>>(File.ReadAllText(Path.Combine(Root, "Content", "Generated", "archetype-decks.json")))
            .Where(deck => deck.Cards.All(entry => _rules.Cards.Any(card => card.Id.Value == entry.Id && card.Source == CardSource.Core))).ToImmutableArray();
    }

    public ImmutableArray<CardPresentation> ConstructibleCards(string profession, DesktopProtocol protocol)
    {
        var allowed = _rules.Cards.Where(card => MatchFactory.IsCardAllowed(card, Enum.Parse<Profession>(profession), protocol.Definition.DeckConstructionPolicy))
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

    public ImmutableArray<string> ValidateDeck(DesktopDeck deck, DesktopProtocol? protocol = null) => MatchFactory.Create(CreateRequest(deck, deck, new LocalMatchSettings(ProtocolJson: protocol?.Json))).Errors
        .Select(value => value.DetailCode).Distinct(StringComparer.Ordinal).ToImmutableArray();

    private static DeckDefinition ToDeck(DesktopDeck deck) => DeckDefinition.Create(Enum.Parse<Profession>(deck.Profession),
        deck.Cards.Select(card => new DeckEntry(CardPrototypeId.Parse(card.Id), card.Copies)));

    public static void SaveDeck(string path, DesktopDeck deck) => File.WriteAllText(path,
        JsonSerializer.Serialize(deck, JsonOptions));
    public static DesktopDeck LoadDeck(string path) => JsonSerializer.Deserialize<DesktopDeck>(File.ReadAllText(path))
        ?? throw new InvalidDataException("Invalid deck file.");
}
