using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eota.Kernel.Commands;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;
using Eota.Server.Application;
using Eota.Transport.Contracts;

namespace Eota.Server.Infrastructure;

// A flushed, hash-chained write-ahead log plus atomic checkpoints. This directory is private server data.
public sealed partial class FileMatchJournal : IMatchJournal
{
    private sealed record Record(int Sequence, string Previous, string Data, string Hash);
    private sealed record Checkpoint(int Sequence, string LogHash, byte[] State);
    private readonly string _directory;
    private string _lastHash;
    private int _sequence;
    private int _compactedAt;
    private readonly Queue<StoredMatchCommand> _recent = [];
    private readonly string _initialHash;
    private readonly FileMatchJournalOptions _options;
    private string? _matchId;
    public MatchRecovery Recovery { get; }

    public FileMatchJournal(string directory, MatchCreationRequest request, FileMatchJournalOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        _directory = Path.GetFullPath(directory); Directory.CreateDirectory(_directory);
        _options = options ?? new();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.CompactAfterRecords, 16);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.CompactAfterRecords, 16384);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.RetainedCommandKeys, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.RetainedCommandKeys, 16384);
        var initial = MatchFactory.Create(request).State ?? throw new InvalidDataException("invalid-journal-request");
        _lastHash = MatchStateHasher.Compute(initial).ToString();
        _initialHash = _lastHash;
        var state = initial; var commands = new List<StoredMatchCommand>(); var hashes = new List<string>();
        try
        {
            state = LoadAnchor(request) ?? initial;
            var anchorSequence = _sequence; var anchorHash = _lastHash;
            var logPath = Path.Combine(_directory, "commands.jsonl");
            if (File.Exists(logPath))
            {
                if (new FileInfo(logPath).Length > 128 * 1024 * 1024) { throw new InvalidDataException("journal-size-budget"); }
                Record? skipped = null; var readRecords = 0;
                foreach (var line in File.ReadLines(logPath))
                {
                    if (++readRecords > 16384 || line.Length > 256 * 1024) { throw new InvalidDataException("journal-record-budget"); }
                    var record = JsonSerializer.Deserialize<Record>(line) ?? throw new InvalidDataException("empty-record");
                    if (record.Sequence <= anchorSequence)
                    {
                        if (record.Sequence < 1 || record.Hash != Hash(record.Sequence, record.Previous, record.Data)
                            || (skipped is not null && (record.Sequence != skipped.Sequence + 1 || record.Previous != skipped.Hash)))
                        { throw new InvalidDataException("journal-prefix-mismatch"); }
                        skipped = record; continue;
                    }
                    if (skipped is not null && (skipped.Sequence != anchorSequence || skipped.Hash != anchorHash))
                    { throw new InvalidDataException("journal-anchor-prefix-mismatch"); }
                    if (record.Sequence != _sequence + 1 || record.Previous != _lastHash || record.Hash != Hash(record.Sequence, record.Previous, record.Data))
                    { throw new InvalidDataException("journal-hash-mismatch"); }
                    var command = ContractJson.Deserialize<StoredMatchCommand>(record.Data);
                    BindMatchId(command.Envelope.MatchId);
                    commands.Add(command); hashes.Add(record.Hash); _lastHash = record.Hash; _sequence++;
                }
                if (skipped is not null && (skipped.Sequence != anchorSequence || skipped.Hash != anchorHash))
                { throw new InvalidDataException("journal-anchor-prefix-mismatch"); }
            }
            var from = 0;
            var checkpointPath = Path.Combine(_directory, "checkpoint.json");
            if (anchorSequence == 0 && File.Exists(checkpointPath))
            {
                if (new FileInfo(checkpointPath).Length > 128 * 1024 * 1024) { throw new InvalidDataException("checkpoint-size-budget"); }
                var checkpoint = JsonSerializer.Deserialize<Checkpoint>(File.ReadAllBytes(checkpointPath)) ?? throw new InvalidDataException("empty-checkpoint");
                if (checkpoint.Sequence < 1 || checkpoint.Sequence > commands.Count || checkpoint.LogHash != hashes[checkpoint.Sequence - 1])
                { throw new InvalidDataException("checkpoint-log-mismatch"); }
                state = MatchCheckpointCodec.Decode(checkpoint.State, request.Protocol, request.Content);
                if (MatchStateHasher.Compute(state).ToString() != commands[checkpoint.Sequence - 1].StateHash) { throw new InvalidDataException("checkpoint-command-mismatch"); }
                from = checkpoint.Sequence;
            }
            foreach (var stored in commands.Skip(from))
            {
                if (stored.Audience == Audience.Spectator) { throw new InvalidDataException("journal-seat-invalid"); }
                var seat = stored.Audience == Audience.PlayerOne ? PlayerId.One : PlayerId.Two;
                var command = stored.SystemTimeout is { } timeout
                    ? new SystemTimeoutCommand(seat, stored.Envelope.ExpectedPlayerRevision, timeout.Turn, Enum.Parse<MatchStage>(timeout.Stage))
                    : MatchActor.MapCommand(stored.Envelope, seat)
                    ?? throw new InvalidDataException("journal-command-invalid");
                var transition = MatchCommandProcessor.Accept(state, command); state = transition.State;
                if (transition.IsAccepted != stored.Ack.Accepted || transition.Receipt.RejectionReason.ToString() != stored.Ack.Code)
                { throw new InvalidDataException("journal-receipt-mismatch"); }
                if (state.Stage == MatchStage.ReadyToResolve) { state = TurnResolver.ResolveReadyTurn(state).State; }
                if (MatchStateHasher.Compute(state).ToString() != stored.StateHash) { throw new InvalidDataException("journal-state-mismatch"); }
            }
            foreach (var command in commands) { Retain(command); }
            Recovery = new MatchRecovery(state, _recent.ToArray(), _matchId);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or OverflowException)
        {
            File.WriteAllText(Path.Combine(_directory, "quarantined.txt"), error.GetType().Name + ": " + error.Message);
            throw new InvalidDataException("房间存档校验失败，已隔离；原始日志保留。", error);
        }
    }

    public void Commit(StoredMatchCommand command, MatchState state)
    {
        ArgumentNullException.ThrowIfNull(command); ArgumentNullException.ThrowIfNull(state);
        BindMatchId(command.Envelope.MatchId);
        if (_sequence == int.MaxValue) { throw new IOException("journal-sequence-exhausted"); }
        var data = ContractJson.Serialize(command);
        var record = new Record(_sequence + 1, _lastHash, data, Hash(_sequence + 1, _lastHash, data));
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record) + "\n");
        using (var file = new FileStream(Path.Combine(_directory, "commands.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            if (file.Length + bytes.Length > 128 * 1024 * 1024) { throw new IOException("journal-size-budget"); }
            file.Write(bytes); file.Flush(flushToDisk: true);
        }
        _sequence++; _lastHash = record.Hash;
        Retain(command);
        if (_sequence % 16 == 0)
        { AtomicFile.Write(Path.Combine(_directory, "checkpoint.json"), JsonSerializer.SerializeToUtf8Bytes(new Checkpoint(_sequence, _lastHash, MatchCheckpointCodec.Encode(state)))); }
        if (_sequence - _compactedAt >= _options.CompactAfterRecords) { Compact(state); }
    }

    private static string Hash(int sequence, string previous, string data) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" + previous + "\n" + data))).ToLowerInvariant();

    private void BindMatchId(string matchId)
    {
        if (string.IsNullOrWhiteSpace(matchId) || (_matchId is not null && _matchId != matchId)) { throw new InvalidDataException("journal-match-id-mismatch"); }
        _matchId = matchId;
    }
}
