using System.Collections.Immutable;
using Eota.Client.Desktop;
using Eota.Transport.Contracts;

namespace Eota.FixtureRecorder;

// One-off authoring helper: plays a legal local match through the desktop client and saves the
// client-format replay that the Godot remote smoke (--smoke-remote) replays against a live host.
// P9GodotReplay pins concrete hand instance ids, so it must be refreshed whenever content changes.
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var root = args.Length > 0 ? args[0] : ".";
        var directory = args.Length > 1 ? args[1] : Path.Combine("artifacts", "P9GodotReplay");
        var turns = args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 4;

        var catalog = new DesktopCatalog(root);
        // Match the host exactly: it starts the room from the fixture's protocol and seed, so the
        // client must build the same bootstrap state or the scripted hand ids will not line up.
        var protocolPath = Path.Combine(directory, "protocol-v0.json");
        var protocolJson = File.ReadAllText(protocolPath);
        var definition = System.Text.Json.JsonDocument.Parse(protocolJson).RootElement.GetProperty("protocol");
        var seed = definition.TryGetProperty("matchSeed", out var seedElement) ? seedElement.GetUInt64() : 123456789UL;
        var settings = new LocalMatchSettings(Seed: seed, Mulligan: true, ProtocolJson: protocolJson);
        var one = catalog.DefaultDeck("Guardian");
        var two = catalog.DefaultDeck("Arcanist");
        await using var session = await DesktopSession.LocalAsync(catalog, one, two, settings);

        for (var turn = 0; turn < turns && session.Client.Store.View is { Status: "Active" }; turn++)
        {
            foreach (var seat in new byte[] { 0, 1 })
            {
                session.SwitchSeat();
                await session.Client.SynchronizeAsync();
                var view = session.Client.Store.View!;
                var own = view.Private;
                if (own is null) { Console.Error.WriteLine("no private view"); return 3; }
                var submitted = view.Players.Single(player => player.PlayerId == own.PlayerId).Submitted;
                if (view.Stage == "Mulligan")
                {
                    if (submitted) { continue; }
                    var mulligan = await session.SubmitAsync(new SubmitMulliganPayload(ImmutableArray<ulong>.Empty));
                    if (!mulligan.Accepted) { Console.Error.WriteLine($"mulligan rejected: {mulligan.Code}"); return 3; }
                    continue;
                }
                if (view.Stage != "Planning" || submitted) { continue; }
                var option = own.PlanOptions.FirstOrDefault(value => value.Allowed);
                if (option is not null)
                {
                    var card = own.Hand.Single(value => value.CardInstanceId == option.CardInstanceId);
                    ClientPayload plan = card.CardKind == "Spell"
                        ? new PlanSpellPayload(option.CardInstanceId, option.LaneId)
                        : new PlanCardPayload(option.CardInstanceId, option.LaneId!.Value);
                    var ack = await session.SubmitAsync(plan);
                    if (!ack.Accepted) { Console.Error.WriteLine($"plan rejected: {ack.Code}"); return 3; }
                    // The client rebases its revision from the authoritative state before the next command.
                    await session.Client.SynchronizeAsync();
                }
                var submit = await session.SubmitAsync(new SubmitTurnPayload());
                if (!submit.Accepted) { Console.Error.WriteLine($"submit rejected: {submit.Code}"); return 3; }
                await session.Client.SynchronizeAsync();
            }
            session.SwitchSeat();
        }

        await session.SaveReplayAsync(directory);
        // The host rebuilds the same card instances from these decks, so they must match the recorded match.
        WriteDeck(directory, "deck-one.json", one);
        WriteDeck(directory, "deck-two.json", two);
        Console.WriteLine($"saved client replay to {Path.GetFullPath(directory)} after {turns} turns; hash={session.FinalStateHash}");
        return 0;
    }

    private static readonly System.Text.Json.JsonSerializerOptions DeckJson = new()
    { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };

    private static void WriteDeck(string directory, string name, DesktopDeck deck)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { profession = deck.Profession.ToLowerInvariant(), cards = deck.Cards }, DeckJson);
        File.WriteAllText(Path.Combine(directory, name), json + "\n");
    }
}
