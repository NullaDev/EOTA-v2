using Eota.Client.Desktop;

namespace Eota.Server.IntegrationTests;

public sealed class DeckDraftTests
{
    [Theory]
    [InlineData("Guardian")]
    [InlineData("Arcanist")]
    [InlineData("Artisan")]
    [InlineData("Hunter")]
    public void ProfessionPoolOnlyOffersConstructibleCardsAndBuildsAValidDeck(string profession)
    {
        var catalog = new DesktopCatalog(Fixture.Root);
        var draft = new DesktopDeckDraft(catalog, profession);
        Assert.Equal(catalog.Cards.Count(card => card.Source == "Core" && (card.Profession == profession || card.Profession == "Neutral")), draft.Pool.Length);
        Assert.Contains(draft.Pool, card => card.Profession == "Neutral");
        Assert.DoesNotContain(draft.Pool, card => card.Source == "Token");
        var other = catalog.Cards.First(card => card.Profession is not "Neutral" && card.Profession != profession);
        Assert.False(draft.Add(other.Id));
        draft.Load(catalog.DefaultDeck(profession));
        Assert.Equal(40, draft.Count); Assert.Empty(catalog.ValidateDeck(draft.Build()));
        Assert.False(draft.Add(draft.Pool[0].Id));
        var entry = draft.Build().Cards[0]; draft.Remove(entry.Id);
        Assert.Equal(39, draft.Count); Assert.True(draft.Add(entry.Id));
        Assert.Empty(catalog.ValidateDeck(draft.Build()));
    }
}
