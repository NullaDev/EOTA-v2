using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Eota.Kernel.Commands;
using Eota.Kernel.Matches;
using Eota.Kernel.Resolution;
using Eota.Server.Infrastructure;

namespace Eota.ReplayCli;

internal static partial class Program
{
    private sealed record CheckpointEntry(string File, int NextFrame);

    private static int RecordCheckpoints(string[] args)
    {
        if (args.Length != 5) { return PrintUsage(); }
        var verified = VerifyMatch(["verify-match", args[1], args[2], args[3]]);
        if (verified != 0) { return verified; }
        if (!TryLoadContent(args[1], out var content) || !TryLoadProtocol(args[2], out var protocol)) { return 2; }
        var replay = JsonSerializer.Deserialize<MatchReplayFile>(File.ReadAllText(args[3]), JsonOptions)!;
        var state = MatchFactory.Create(new MatchCreationRequest(protocol!, content!.Rules, replay.Initial.MatchSeed,
            FromReplayDeck(replay.Initial.PlayerOneDeck), FromReplayDeck(replay.Initial.PlayerTwoDeck))).State!;
        var directory = Path.GetFullPath(args[4]);
        Directory.CreateDirectory(directory);
        var entries = new List<CheckpointEntry>();
        var nextFrame = 0;
        Save();
        foreach (var command in replay.Commands)
        {
            var accepted = MatchCommandProcessor.Accept(state, FromReplayCommand(command));
            if (!accepted.IsAccepted) { throw new InvalidDataException("Verified command unexpectedly rejected."); }
            state = accepted.State;
            Save();
            while (state.Status == MatchStatus.Active && (state.Stage == MatchStage.ReadyToResolve || state.Execution is not null))
            {
                state = TurnResolver.Step(state).State;
                nextFrame++;
                Save();
            }
        }
        File.WriteAllText(Path.Combine(directory, "index.json"), JsonSerializer.Serialize(entries, JsonOptions));
        Console.WriteLine($"Recorded {entries.Count} private checkpoints.");
        return 0;

        void Save()
        {
            var name = entries.Count.ToString("D5", CultureInfo.InvariantCulture) + ".checkpoint.json";
            File.WriteAllBytes(Path.Combine(directory, name), MatchCheckpointCodec.Encode(state));
            entries.Add(new CheckpointEntry(name, nextFrame));
        }
    }

    private static int VerifyCheckpoints(string[] args)
    {
        if (args.Length != 5) { return PrintUsage(); }
        var verified = VerifyMatch(["verify-match", args[1], args[2], args[3]]);
        if (verified != 0) { return verified; }
        if (!TryLoadContent(args[1], out var content) || !TryLoadProtocol(args[2], out var protocol)) { return 2; }
        var replay = JsonSerializer.Deserialize<MatchReplayFile>(File.ReadAllText(args[3]), JsonOptions)!;
        var directory = Path.GetFullPath(args[4]);
        var entries = JsonSerializer.Deserialize<ImmutableArray<CheckpointEntry>>(File.ReadAllText(Path.Combine(directory, "index.json")), JsonOptions);
        if (entries.IsDefaultOrEmpty) { throw new InvalidDataException("Empty checkpoint index."); }
        foreach (var entry in entries)
        {
            if (Path.GetFileName(entry.File) != entry.File || entry.NextFrame < 0 || entry.NextFrame > replay.ExpectedFrames.Length)
            { throw new InvalidDataException("Invalid checkpoint index entry."); }
            var state = MatchCheckpointCodec.Decode(File.ReadAllBytes(Path.Combine(directory, entry.File)), protocol!, content!.Rules);
            var frames = ImmutableArray.CreateBuilder<FrameTransition>();
            if (state.Status == MatchStatus.Active && (state.Execution is not null || state.Stage == MatchStage.ReadyToResolve))
            {
                var turn = TurnResolver.ResolveReadyTurn(state);
                state = turn.State;
                frames.AddRange(turn.Frames);
            }
            var rest = MatchReplay.ReplayCommands(state, replay.Commands.Skip(state.CommandLog.Length).Select(FromReplayCommand));
            frames.AddRange(rest.Frames);
            var expected = replay.ExpectedFrames.Skip(entry.NextFrame).ToArray();
            if (!rest.IsSuccess || MatchStateHasher.Compute(rest.State).ToString() != replay.ExpectedFinalStateHash
                || frames.Count != expected.Length || frames.Select(ToReplayFrame).Where((frame, index) => !ReplayFramesMatch(frame, expected[index])).Any())
            { Console.Error.WriteLine($"Checkpoint continuation differs: {entry.File}"); return 5; }
        }
        Console.WriteLine($"Verified {entries.Length} checkpoints, including remaining commands and frame/receipt/event/RNG results.");
        return 0;
    }
}
