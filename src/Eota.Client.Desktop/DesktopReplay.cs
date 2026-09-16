using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eota.Kernel.Commands;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;
using Eota.Server.Application;
using Eota.Transport.Contracts;

namespace Eota.Client.Desktop;

public sealed record DesktopReplayCommand(string Kind, byte PlayerId, ulong ExpectedPlayerRevision,
    ulong? CardInstanceId = null, int? LaneId = null, ulong? PlanOrdinal = null, ImmutableArray<ulong>? ReplacedCards = null,
    int? ExpectedTurn = null, string? ExpectedStage = null);
public sealed record DesktopReplayFile(int FormatVersion, string RuleHash, LocalMatchSettings Settings, DesktopDeck PlayerOne,
    DesktopDeck PlayerTwo, ImmutableArray<DesktopReplayCommand> Commands, string FinalStateHash);
public sealed record DesktopReplay(ObserverView Initial, ImmutableArray<PresentationFramePayload> Frames, ObserverView Final, string StateHash)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal static void Save(string directory, MatchCreationRequest request, LocalMatchSettings settings, DesktopDeck one, DesktopDeck two,
        ImmutableArray<AcceptedCommandRecord> log, string hash)
    {
        Directory.CreateDirectory(directory);
        var commands = log.Select(value => ToRecord(value.Command)).ToImmutableArray();
        File.WriteAllText(Path.Combine(directory, "replay.json"), JsonSerializer.Serialize(new DesktopReplayFile(1, request.Content.Hash.ToString(), settings, one, two, commands, hash), JsonOptions));
        // These companion files can be consumed directly by Replay CLI record-match.
        File.WriteAllText(Path.Combine(directory, "commands.json"), JsonSerializer.Serialize(commands, JsonOptions));
        File.WriteAllText(Path.Combine(directory, "protocol-v0.json"), JsonSerializer.Serialize(new { schemaVersion = "eota.protocol/v0", protocol = request.Protocol.Definition }, JsonOptions));
        foreach (var (deck, name) in new[] { (one, "deck-one.json"), (two, "deck-two.json") })
        { File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(new { profession = deck.Profession.ToLowerInvariant(), cards = deck.Cards }, JsonOptions)); }
    }

    public static DesktopReplay Load(DesktopCatalog catalog, string path, Audience audience = Audience.Spectator)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var file = JsonSerializer.Deserialize<DesktopReplayFile>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("Invalid replay.");
        if (file.FormatVersion != 1 || file.RuleHash != catalog.RuleHash || file.Commands.IsDefault) { throw new InvalidDataException("Replay version or content mismatch."); }
        var initial = MatchFactory.Create(catalog.CreateRequest(file.PlayerOne, file.PlayerTwo, file.Settings)).State
            ?? throw new InvalidDataException("Invalid replay match inputs.");
        var replay = MatchReplay.ReplayCommands(initial, file.Commands.Select(ToCommand));
        var hash = MatchStateHasher.Compute(replay.State).ToString();
        if (!replay.IsSuccess || hash != file.FinalStateHash) { throw new InvalidDataException("Replay failed authoritative verification."); }
        return new DesktopReplay(ObserverProjector.Project(initial, audience), replay.Frames.Select(frame =>
        {
            var view = ObserverProjector.Project(frame.State, audience);
            return new PresentationFramePayload(frame.Plan.FrameId.Value, view, ObserverViewHasher.Compute(view), ObserverProjector.ProjectEvents(frame, audience));
        }).ToImmutableArray(), ObserverProjector.Project(replay.State, audience), hash);
    }

    private static DesktopReplayCommand ToRecord(AuthoritativeCommand command) => command switch
    {
        PlanCardCommand card => new("planCard", command.PlayerId.Value, command.ExpectedPlayerRevision, card.CardInstanceId.Value, card.LaneId.Value),
        PlanSpellCommand spell => new("planSpell", command.PlayerId.Value, command.ExpectedPlayerRevision, spell.CardInstanceId.Value, spell.TargetLaneId?.Value),
        CancelPlanCommand cancel => new("cancelPlan", command.PlayerId.Value, command.ExpectedPlayerRevision, PlanOrdinal: cancel.PlanCommandId.Ordinal),
        SubmitMulliganCommand mulligan => new("submitMulligan", command.PlayerId.Value, command.ExpectedPlayerRevision, ReplacedCards: mulligan.ReplacedCards.Select(value => value.Value).ToImmutableArray()),
        SubmitTurnCommand => new("submitTurn", command.PlayerId.Value, command.ExpectedPlayerRevision),
        SystemTimeoutCommand timeout => new("systemTimeout", command.PlayerId.Value, command.ExpectedPlayerRevision,
            ExpectedTurn: timeout.ExpectedTurn, ExpectedStage: timeout.ExpectedStage.ToString().ToLowerInvariant()),
        _ => throw new InvalidDataException("Unsupported replay command.")
    };

    private static AuthoritativeCommand ToCommand(DesktopReplayCommand command)
    {
        var player = new PlayerId(command.PlayerId);
        return command.Kind switch
        {
            "planCard" => new PlanCardCommand(player, command.ExpectedPlayerRevision, new CardInstanceId(command.CardInstanceId!.Value), new LaneId(command.LaneId!.Value)),
            "planSpell" => new PlanSpellCommand(player, command.ExpectedPlayerRevision, new CardInstanceId(command.CardInstanceId!.Value), command.LaneId is { } lane ? new LaneId(lane) : null),
            "cancelPlan" => new CancelPlanCommand(player, command.ExpectedPlayerRevision, new PlanCommandId(player, command.PlanOrdinal!.Value)),
            "submitMulligan" => new SubmitMulliganCommand(player, command.ExpectedPlayerRevision, (command.ReplacedCards ?? []).Select(value => new CardInstanceId(value)).ToImmutableArray()),
            "submitTurn" => new SubmitTurnCommand(player, command.ExpectedPlayerRevision),
            "systemTimeout" when command.ExpectedTurn is { } turn && Enum.TryParse<MatchStage>(command.ExpectedStage, true, out var stage)
                && stage is MatchStage.Mulligan or MatchStage.Planning => new SystemTimeoutCommand(player, command.ExpectedPlayerRevision, turn, stage),
            _ => throw new InvalidDataException("Unsupported replay command.")
        };
    }
}
