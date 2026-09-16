using Eota.Client.Desktop;
using Eota.Transport.Contracts;

namespace Eota.Server.IntegrationTests;

public sealed class P9ProtocolSettingsTests
{
    [Fact]
    public async Task SelectedProtocolDrivesMatchAndSurvivesSavedReplay()
    {
        var protocol = DesktopProtocol.Default.WithValues(new Dictionary<string, string>
        {
            ["initialHeroHealth"] = "42",
            ["laneCount"] = "4",
            ["openingHandSize"] = "6",
            ["initialPlayerCost"] = "3",
            ["initialMaxCost"] = "3",
            ["mulliganEnabled"] = "false"
        });
        var catalog = new DesktopCatalog(Fixture.Root);
        await using var session = await DesktopSession.LocalAsync(catalog, catalog.DefaultDeck("Guardian"), catalog.DefaultDeck("Arcanist"),
            new LocalMatchSettings(ProtocolJson: protocol.Json));
        var view = session.Client.Store.View!;
        Assert.Equal(4, view.Lanes.Length); Assert.Equal("Planning", view.Stage);
        Assert.All(view.Players, player => Assert.Equal(42, player.HeroMaximumHealth));
        Assert.Equal(6, view.Private!.Hand.Length); Assert.Equal(3, view.Private.AvailableCost);
        for (var seat = 0; seat < 2; seat++)
        { Assert.True((await session.SubmitAsync(new SubmitTurnPayload())).Accepted); session.SwitchSeat(); }
        var directory = Path.Combine(Fixture.Root, "artifacts", "P9ProtocolReplay");
        await session.SaveReplayAsync(directory);
        var replay = DesktopReplay.Load(catalog, Path.Combine(directory, "replay.json"));
        Assert.Equal(session.FinalStateHash, replay.StateHash);
        Assert.Equal(4, replay.Initial.Lanes.Length);
        Assert.All(replay.Initial.Players, player => Assert.Equal(42, player.HeroMaximumHealth));
    }

    [Fact]
    public void ProtocolRejectsInvalidCombinationsWithoutChangingTheActiveValues()
    {
        var original = DesktopProtocol.Default;
        Assert.Throws<InvalidDataException>(() => original.WithValues(new Dictionary<string, string> { ["openingHandSize"] = "99" }));
        Assert.Throws<InvalidDataException>(() => original.WithValues(new Dictionary<string, string> { ["laneCount"] = "5" }));
        Assert.Throws<InvalidDataException>(() => original.WithValues(new Dictionary<string, string> { ["initialHeroHealth"] = "0" }));
        Assert.Equal("30", original.Fields.Single(field => field.Key == "initialHeroHealth").Value);
        Assert.Equal("40", original.Fields.Single(field => field.Key == "requiredDeckSize").Value);
    }
}
