using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eota.Kernel.Commands;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.ReplayCli;

internal static partial class Program
{
    private static int RecordMatch(string[] args)
    {
        if (args.Length != 8 || !ulong.TryParse(args[5], NumberStyles.None, CultureInfo.InvariantCulture, out var seed))
        {
            return PrintUsage();
        }

        if (!TryLoadContent(args[1], out var content)
            || !TryLoadProtocol(args[2], out var protocol)
            || !TryLoadDeck(args[3], out var playerOneDeck)
            || !TryLoadDeck(args[4], out var playerTwoDeck))
        {
            return 2;
        }

        var creation = MatchFactory.Create(new MatchCreationRequest(protocol!, content!.Rules, seed, playerOneDeck!, playerTwoDeck!));
        if (!creation.IsSuccess || creation.State is null)
        {
            PrintKernelErrors(creation.Errors);
            return 3;
        }

        var commands = JsonSerializer.Deserialize<ImmutableArray<ReplayCommand>>(
            File.ReadAllText(args[6], Encoding.UTF8), JsonOptions);
        if (commands.IsDefault)
        {
            throw new ArgumentException("Commands must be a JSON array.");
        }

        var result = MatchReplay.ReplayCommands(creation.State, commands.Select(FromReplayCommand));
        if (!result.IsSuccess)
        {
            PrintKernelErrors(result.Errors);
            return 3;
        }

        var initial = creation.State;
        var header = new InitialReplayFile(1, initial.Manifest.ProtocolId, initial.Manifest.ProtocolVersion,
            initial.Manifest.ProtocolHash.ToString(), initial.Manifest.ContentSchemaVersion,
            initial.Manifest.RuleContentHash.ToString(), seed,
            ToReplayDeck(initial.Manifest.PlayerOneDeck), ToReplayDeck(initial.Manifest.PlayerTwoDeck),
            MatchStateHasher.Compute(initial).ToString(), initial.RuleRng.SampleCount);
        var replay = new MatchReplayFile(2, header, commands, result.Frames.Select(ToReplayFrame).ToImmutableArray(),
            MatchStateHasher.Compute(result.State).ToString(), result.State.Status, result.State.Outcome);
        var outputPath = Path.GetFullPath(args[7]);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(replay, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
        Console.WriteLine($"Recorded match replay: {outputPath}");
        PrintMatchResult(result);
        return 0;
    }

    private static int VerifyMatch(string[] args)
    {
        if (args.Length != 4)
        {
            return PrintUsage();
        }

        if (!TryLoadContent(args[1], out var content) || !TryLoadProtocol(args[2], out var protocol))
        {
            return 2;
        }

        var replay = JsonSerializer.Deserialize<MatchReplayFile>(File.ReadAllText(args[3], Encoding.UTF8), JsonOptions);
        if (replay is null || replay.FormatVersion != 2 || replay.Initial is not { FormatVersion: 1 }
            || replay.Commands.IsDefault || replay.ExpectedFrames.IsDefault)
        {
            throw new ArgumentException("Unsupported or incomplete match replay.");
        }

        var header = replay.Initial;
        if (header.ProtocolId != protocol!.Definition.ProtocolId
            || header.ProtocolVersion != protocol.Definition.ProtocolVersion
            || header.ProtocolHash != protocol.Hash.ToString()
            || header.ContentSchemaVersion != content!.Rules.SchemaVersion
            || header.RuleContentHash != content.Rules.Hash.ToString())
        {
            Console.Error.WriteLine("Manifest mismatch: protocol or rule content differs from the replay.");
            return 4;
        }

        var creation = MatchFactory.Create(new MatchCreationRequest(protocol, content.Rules, header.MatchSeed,
            FromReplayDeck(header.PlayerOneDeck), FromReplayDeck(header.PlayerTwoDeck)));
        if (!creation.IsSuccess || creation.State is null)
        {
            PrintKernelErrors(creation.Errors);
            return 3;
        }

        if (header.ExpectedInitialStateHash != MatchStateHasher.Compute(creation.State).ToString()
            || header.ExpectedRngSampleCount != creation.State.RuleRng.SampleCount)
        {
            Console.Error.WriteLine("ReplayHashMismatch: initial state or RNG sample count differs.");
            return 5;
        }

        var result = MatchReplay.ReplayCommands(creation.State, replay.Commands.Select(FromReplayCommand));
        if (!result.IsSuccess)
        {
            PrintKernelErrors(result.Errors);
            return 3;
        }

        for (var index = 0; index < Math.Min(result.Frames.Length, replay.ExpectedFrames.Length); index++)
        {
            var actual = ToReplayFrame(result.Frames[index]);
            if (!ReplayFramesMatch(actual, replay.ExpectedFrames[index]))
            {
                Console.Error.WriteLine($"ReplayHashMismatch: first differing frame {actual.FrameId} (index {index}).");
                Console.Error.WriteLine($"Expected: {JsonSerializer.Serialize(replay.ExpectedFrames[index], JsonOptions)}");
                Console.Error.WriteLine($"Actual: {JsonSerializer.Serialize(actual, JsonOptions)}");
                return 5;
            }
        }

        if (result.Frames.Length != replay.ExpectedFrames.Length)
        {
            Console.Error.WriteLine($"ReplayHashMismatch: frame count expected {replay.ExpectedFrames.Length}, actual {result.Frames.Length}.");
            return 5;
        }

        if (replay.ExpectedFinalStateHash != MatchStateHasher.Compute(result.State).ToString()
            || replay.ExpectedStatus != result.State.Status || replay.ExpectedOutcome != result.State.Outcome)
        {
            Console.Error.WriteLine("ReplayHashMismatch: final state or outcome differs.");
            return 5;
        }

        Console.WriteLine($"Verified match replay: {Path.GetFullPath(args[3])}");
        PrintMatchResult(result);
        return 0;
    }

    private static AuthoritativeCommand FromReplayCommand(ReplayCommand source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var playerId = new PlayerId(source.PlayerId);
        var revision = source.ExpectedPlayerRevision;
        if (source.Kind != AuthoritativeCommandKind.SystemTimeout && (source.ExpectedTurn is not null || source.ExpectedStage is not null))
        { throw new ArgumentException("System context is only valid for timeout commands."); }
        return source.Kind switch
        {
            AuthoritativeCommandKind.SystemTimeout when source.ExpectedTurn is { } turn && source.ExpectedStage is MatchStage.Mulligan or MatchStage.Planning
                && source.CardInstanceId is null && source.LaneId is null && source.PlanOrdinal is null && source.ReplacedCards is null =>
                new SystemTimeoutCommand(playerId, revision, turn, source.ExpectedStage.Value),
            AuthoritativeCommandKind.PlanCard when source.CardInstanceId is { } card && source.LaneId is { } lane
                && source.PlanOrdinal is null && source.ReplacedCards is null =>
                new PlanCardCommand(playerId, revision, new CardInstanceId(card), new LaneId(lane)),
            AuthoritativeCommandKind.PlanSpell when source.CardInstanceId is { } card
                && source.PlanOrdinal is null && source.ReplacedCards is null =>
                new PlanSpellCommand(playerId, revision, new CardInstanceId(card),
                    source.LaneId is { } lane ? new LaneId(lane) : null),
            AuthoritativeCommandKind.CancelPlan when source.PlanOrdinal is { } ordinal
                && source.CardInstanceId is null && source.LaneId is null && source.ReplacedCards is null =>
                new CancelPlanCommand(playerId, revision, new PlanCommandId(playerId, ordinal)),
            AuthoritativeCommandKind.SubmitTurn when source.CardInstanceId is null && source.LaneId is null
                && source.PlanOrdinal is null && source.ReplacedCards is null =>
                new SubmitTurnCommand(playerId, revision),
            AuthoritativeCommandKind.SubmitMulligan when source.CardInstanceId is null && source.LaneId is null
                && source.PlanOrdinal is null =>
                new SubmitMulliganCommand(playerId, revision,
                    source.ReplacedCards?.Select(value => new CardInstanceId(value)).ToImmutableArray()
                    ?? ImmutableArray<CardInstanceId>.Empty),
            _ => throw new ArgumentException($"Invalid fields for command '{source.Kind}'.")
        };
    }

    private static ReplayFrame ToReplayFrame(FrameTransition frame) => new(
        frame.Plan.FrameId.Value, frame.State.Turn, frame.State.Stage,
        frame.BeforeStateHash.ToString(), frame.AfterStateHash.ToString(),
        frame.Receipts.Hash.ToString(), frame.Events.Hash.ToString(), frame.State.RuleRng.SampleCount,
        frame.Plan.MovementRandomChoices.Select(value => new ReplayMovementChoice(
            value.IntentId.Value, value.EntityId.Value, value.Candidates[0].Value, value.Candidates[1].Value,
            value.SelectedLaneId.Value, value.SampleCountBefore, value.SamplesConsumed)).ToImmutableArray(),
        frame.Plan.NumericSetChoices.Select(value => new ReplayNumericSetChoice(
            value.ConflictKey.Kind, value.ConflictKey.StableTargetId, value.Attribute,
            value.Candidates.Select(id => id.Value).ToImmutableArray(), value.SelectedIntentId.Value,
            value.SampleCountBefore, value.SamplesConsumed)).ToImmutableArray(),
        frame.Plan.ConflictRandomChoices.Select(value => new ReplayConflictChoice(value.Domain, value.TargetId,
            value.Candidates.Select(id => id.Value).ToImmutableArray(), value.SelectedIntentId.Value,
            value.SampleCountBefore, value.SamplesConsumed)).ToImmutableArray());

    private static bool ReplayFramesMatch(ReplayFrame actual, ReplayFrame expected) =>
        actual with { MovementRandomChoices = default, NumericSetChoices = default, ConflictRandomChoices = default } == expected with { MovementRandomChoices = default, NumericSetChoices = default, ConflictRandomChoices = default }
        && (actual.MovementRandomChoices.IsDefault ? ImmutableArray<ReplayMovementChoice>.Empty : actual.MovementRandomChoices)
        .SequenceEqual(expected.MovementRandomChoices.IsDefault ? ImmutableArray<ReplayMovementChoice>.Empty : expected.MovementRandomChoices)
        && NumericChoicesMatch(actual.NumericSetChoices, expected.NumericSetChoices)
        && ConflictChoicesMatch(actual.ConflictRandomChoices, expected.ConflictRandomChoices);

    private static bool ConflictChoicesMatch(ImmutableArray<ReplayConflictChoice> actual, ImmutableArray<ReplayConflictChoice> expected)
    {
        if (actual.IsDefault) { actual = []; }
        if (expected.IsDefault) { expected = []; }
        return actual.Length == expected.Length && actual.Zip(expected).All(pair =>
            pair.First with { Candidates = default } == pair.Second with { Candidates = default }
            && !pair.First.Candidates.IsDefault && !pair.Second.Candidates.IsDefault
            && pair.First.Candidates.SequenceEqual(pair.Second.Candidates));
    }

    private static bool NumericChoicesMatch(ImmutableArray<ReplayNumericSetChoice> actual, ImmutableArray<ReplayNumericSetChoice> expected)
    {
        if (actual.IsDefault) { actual = []; }
        if (expected.IsDefault) { expected = []; }
        return actual.Length == expected.Length && actual.Zip(expected).All(pair =>
            pair.First with { Candidates = default } == pair.Second with { Candidates = default }
            && !pair.First.Candidates.IsDefault && !pair.Second.Candidates.IsDefault
            && pair.First.Candidates.SequenceEqual(pair.Second.Candidates));
    }

    private static void PrintMatchResult(MatchReplayResult result)
    {
        Console.WriteLine($"Frames: {result.Frames.Length}; Turn: {result.State.Turn}; Status: {result.State.Status}; Outcome: {result.State.Outcome}");
        Console.WriteLine($"StateHash: {MatchStateHasher.Compute(result.State)}");
    }

    private sealed record MatchReplayFile(
        [property: JsonRequired] int FormatVersion,
        [property: JsonRequired] InitialReplayFile Initial,
        [property: JsonRequired] ImmutableArray<ReplayCommand> Commands,
        [property: JsonRequired] ImmutableArray<ReplayFrame> ExpectedFrames,
        [property: JsonRequired] string ExpectedFinalStateHash,
        [property: JsonRequired] MatchStatus ExpectedStatus,
        [property: JsonRequired] MatchOutcome ExpectedOutcome);

    private sealed record ReplayCommand(
        [property: JsonRequired] AuthoritativeCommandKind Kind,
        [property: JsonRequired] byte PlayerId,
        [property: JsonRequired] ulong ExpectedPlayerRevision,
        ulong? CardInstanceId = null,
        int? LaneId = null,
        ulong? PlanOrdinal = null,
        ImmutableArray<ulong>? ReplacedCards = null,
        int? ExpectedTurn = null, MatchStage? ExpectedStage = null);

    private sealed record ReplayFrame(
        [property: JsonRequired] ulong FrameId,
        [property: JsonRequired] int Turn,
        [property: JsonRequired] MatchStage Stage,
        [property: JsonRequired] string BeforeStateHash,
        [property: JsonRequired] string AfterStateHash,
        [property: JsonRequired] string ReceiptHash,
        [property: JsonRequired] string EventHash,
        [property: JsonRequired] ulong RngSampleCount,
        ImmutableArray<ReplayMovementChoice> MovementRandomChoices = default,
        ImmutableArray<ReplayNumericSetChoice> NumericSetChoices = default,
        ImmutableArray<ReplayConflictChoice> ConflictRandomChoices = default);

    private sealed record ReplayConflictChoice(string Domain, ulong TargetId, ImmutableArray<ulong> Candidates,
        ulong SelectedIntentId, ulong SampleCountBefore, ulong SamplesConsumed);

    private sealed record ReplayNumericSetChoice(
        [property: JsonRequired] ConflictKind ConflictKind,
        [property: JsonRequired] ulong TargetId,
        [property: JsonRequired] Eota.Kernel.Effects.NumericProperty Attribute,
        [property: JsonRequired] ImmutableArray<ulong> Candidates,
        [property: JsonRequired] ulong SelectedIntentId,
        [property: JsonRequired] ulong SampleCountBefore,
        [property: JsonRequired] ulong SamplesConsumed);

    private sealed record ReplayMovementChoice(
        [property: JsonRequired] ulong IntentId,
        [property: JsonRequired] ulong EntityId,
        [property: JsonRequired] int LeftLaneId,
        [property: JsonRequired] int RightLaneId,
        [property: JsonRequired] int SelectedLaneId,
        [property: JsonRequired] ulong SampleCountBefore,
        [property: JsonRequired] ulong SamplesConsumed);
}
