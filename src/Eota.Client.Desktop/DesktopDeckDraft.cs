using System.Collections.Immutable;

namespace Eota.Client.Desktop;

public sealed class DesktopDeckDraft
{
    private readonly DesktopCatalog _catalog;
    private readonly Dictionary<string, int> _copies = new(StringComparer.Ordinal);
    public string Profession { get; }
    public string Name { get; set; } = "我的牌组";
    public ImmutableArray<CardPresentation> Pool { get; private set; }
    public DesktopProtocol Protocol { get; private set; }
    public DesktopDeckDraft(DesktopCatalog catalog, string profession, DesktopProtocol? protocol = null)
    {
        _catalog = catalog; Profession = profession; Protocol = protocol ?? DesktopProtocol.Default;
        Pool = catalog.ConstructibleCards(profession, Protocol);
    }
    public void UseProtocol(DesktopProtocol protocol)
    { Protocol = protocol; Pool = _catalog.ConstructibleCards(Profession, protocol); }
    public int Count => _copies.Values.Sum();
    public int Copies(string id) => _copies.GetValueOrDefault(id);
    public bool Add(string id)
    {
        if (!Pool.Any(card => card.Id == id) || Count >= Protocol.RequiredDeckSize || Copies(id) >= Protocol.MaxCopies) { return false; }
        _copies[id] = Copies(id) + 1; return true;
    }
    public void Remove(string id)
    {
        if (Copies(id) <= 1) { _copies.Remove(id); }
        else { _copies[id]--; }
    }
    public void Clear() => _copies.Clear();
    public void Load(DesktopDeck deck)
    {
        if (deck.Profession != Profession || deck.Cards.Any(entry => string.IsNullOrEmpty(entry.Id)
            || entry.Copies is < 1 or > 256) || deck.Cards.Sum(entry => entry.Copies) > 256
            || deck.Cards.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count() != deck.Cards.Length)
        { throw new InvalidDataException("牌组包含不适用的卡牌或副本数量。"); }
        _copies.Clear(); foreach (var entry in deck.Cards) { _copies.Add(entry.Id, entry.Copies); }
        Name = deck.Name;
    }
    public DesktopDeck Build() => new(Name.Trim(), Profession, _copies.OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => new DeckCard(pair.Key, pair.Value)).ToImmutableArray());
}
