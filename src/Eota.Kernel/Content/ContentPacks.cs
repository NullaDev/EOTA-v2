using System.Collections.Immutable;
using System.Security.Cryptography;
using Eota.Kernel.Determinism;
using Eota.Kernel.Primitives;
using Eota.Kernel.Effects;

namespace Eota.Kernel.Content;

public sealed class RuleContentPack
{
    public const ushort CanonicalSchemaVersion = 6;

    private readonly Dictionary<CardPrototypeId, CardDefinition> _cardsById;

    private RuleContentPack(string schemaVersion, ImmutableArray<CardDefinition> cards, Hash256 hash)
    {
        SchemaVersion = schemaVersion;
        Cards = cards;
        Hash = hash;
        _cardsById = cards.ToDictionary(card => card.Id);
    }

    public string SchemaVersion { get; }

    public ImmutableArray<CardDefinition> Cards { get; }

    public Hash256 Hash { get; }

    public static RuleContentPack Create(string schemaVersion, IEnumerable<CardDefinition> cards)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaVersion);
        ArgumentNullException.ThrowIfNull(cards);

        var orderedCards = cards.Select(NormalizeCard).OrderBy(card => card.Id).ToImmutableArray();
        if (orderedCards.Length == 0)
        {
            throw new ArgumentException("A rule content pack must contain at least one card.", nameof(cards));
        }

        for (var index = 1; index < orderedCards.Length; index++)
        {
            if (orderedCards[index - 1].Id == orderedCards[index].Id)
            {
                throw new ArgumentException($"Duplicate card prototype ID '{orderedCards[index].Id}'.", nameof(cards));
            }
        }

        ValidateCards(orderedCards);
        EffectReferences.Validate(orderedCards);
        var hash = ComputeHash(schemaVersion, orderedCards);
        return new RuleContentPack(schemaVersion, orderedCards, hash);
    }

    public bool TryGetCard(CardPrototypeId id, out CardDefinition? card) => _cardsById.TryGetValue(id, out card);

    // Hash a normalized rule definition, excluding art and localized text. Cross-card dependencies
    // are checked separately against the closure of both decks.
    public Hash256 CardRuleHash(CardPrototypeId id) => ComputeHash(SchemaVersion, [_cardsById[id]]);

    private static void ValidateCards(ImmutableArray<CardDefinition> cards)
    {
        foreach (var card in cards)
        {
            if (card.Effects.Length > 32 || card.Effects.Any(effect => string.IsNullOrWhiteSpace(effect.Id) || effect.Id.Length > 96)
                || card.Effects.Select(effect => effect.Id).Distinct(StringComparer.Ordinal).Count() != card.Effects.Length)
            { throw new ArgumentException("Invalid or duplicate effect identities.", nameof(cards)); }
            switch (card)
            {
                case MinionCardDefinition minion when minion.Health <= 0:
                    throw new ArgumentException($"Minion '{card.Id}' must have positive health.", nameof(cards));
                case MinionCardDefinition minion when minion.Keywords.Any(keyword =>
                    keyword.Kind == MinionKeywordKind.Slow ? keyword.Parameter <= 0 : keyword.Parameter != 0):
                    throw new ArgumentException($"Minion '{card.Id}' has an invalid keyword parameter.", nameof(cards));
                case FieldCardDefinition { Lifetime: null }:
                    throw new ArgumentException($"Field '{card.Id}' must declare a lifetime.", nameof(cards));
                case FieldCardDefinition field when field.Keywords.Any(keyword => !Enum.IsDefined(keyword)):
                    throw new ArgumentException($"Field '{card.Id}' has an unsupported keyword.", nameof(cards));
            }
        }
    }

    private static CardDefinition NormalizeCard(CardDefinition card)
    {
        ArgumentNullException.ThrowIfNull(card);
        card = card with
        {
            Effects = (card.Effects.IsDefault ? [] : card.Effects).OrderBy(value => value.Id, StringComparer.Ordinal).ToImmutableArray(),
            Tags = (card.Tags.IsDefault ? [] : card.Tags).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray()
        };
        return card switch
        {
            MinionCardDefinition minion => minion with
            {
                Keywords = (minion.Keywords.IsDefault
                        ? ImmutableArray<MinionKeywordDefinition>.Empty
                        : minion.Keywords)
                    .Distinct()
                    .OrderBy(keyword => keyword.Kind)
                    .ThenBy(keyword => keyword.Parameter)
                    .ToImmutableArray()
            },
            FieldCardDefinition field => field with
            {
                Keywords = (field.Keywords.IsDefault ? ImmutableArray<FieldKeywordKind>.Empty : field.Keywords)
                    .Distinct().OrderBy(keyword => keyword).ToImmutableArray(),
                Lifetime = field.Lifetime switch
                {
                    FiniteFieldLifetimeDefinition finite => new FiniteFieldLifetimeDefinition(finite.InitialEnergy),
                    PermanentFieldLifetimeDefinition => new PermanentFieldLifetimeDefinition(),
                    _ => throw new ArgumentException($"Unsupported field lifetime type '{field.Lifetime?.GetType().Name}'.", nameof(card))
                }
            },
            SpellCardDefinition => card,
            _ => throw new ArgumentException($"Unsupported card definition type '{card.GetType().Name}'.", nameof(card))
        };
    }

    private static Hash256 ComputeHash(string schemaVersion, ImmutableArray<CardDefinition> cards)
        => Hash256.FromBytes(SHA256.HashData(Encode(schemaVersion, cards)));

    public byte[] EncodeCanonical() => Encode(SchemaVersion, Cards);

    private static byte[] Encode(string schemaVersion, ImmutableArray<CardDefinition> cards)
    {
        var writer = new CanonicalWriter("eota.rule-content", CanonicalSchemaVersion);
        writer.WriteString(schemaVersion);
        writer.WriteCount(cards.Length);
        foreach (var card in cards)
        {
            writer.WriteString(card.Id.Value);
            writer.WriteInt32((int)card.Kind);
            writer.WriteInt32((int)card.Source);
            writer.WriteInt32((int)card.Profession);
            writer.WriteInt64(card.Cost);
            writer.WriteInt64(card.StoredCharge);
            writer.WriteCount(card.Tags.Length);
            foreach (var tag in card.Tags) { writer.WriteString(tag); }
            writer.WriteCount(card.Effects.Length);
            foreach (var effect in card.Effects) { EffectCanonical.Write(writer, effect); }

            switch (card)
            {
                case MinionCardDefinition minion:
                    writer.WriteInt64(minion.Attack);
                    writer.WriteInt64(minion.Health);
                    writer.WriteCount(minion.Keywords.Length);
                    foreach (var keyword in minion.Keywords
                                 .OrderBy(value => value.Kind)
                                 .ThenBy(value => value.Parameter))
                    {
                        writer.WriteInt32((int)keyword.Kind);
                        writer.WriteInt64(keyword.Parameter);
                    }

                    break;
                case FieldCardDefinition field:
                    writer.WriteInt32((int)field.Lifetime.Kind);
                    if (field.Lifetime is FiniteFieldLifetimeDefinition finite)
                    {
                        writer.WriteInt64(finite.InitialEnergy);
                    }

                    writer.WriteBoolean(field.PreventsActiveAttacksInLane);
                    writer.WriteCount(field.Keywords.Length);
                    foreach (var keyword in field.Keywords.OrderBy(value => value))
                    {
                        writer.WriteInt32((int)keyword);
                    }

                    break;
                case SpellCardDefinition spell:
                    writer.WriteInt32((int)spell.Speed);
                    writer.WriteInt32((int)spell.TargetScope);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported card definition type '{card.GetType().Name}'.");
            }
        }
        return writer.ToArray();
    }
}

public sealed record PresentationPack(ImmutableArray<PresentationCardDefinition> Cards)
{
    public static PresentationPack Create(IEnumerable<PresentationCardDefinition> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        return new PresentationPack(cards.OrderBy(card => card.Id).ToImmutableArray());
    }
}

public sealed record CompiledContentBundle(RuleContentPack Rules, PresentationPack Presentation);
