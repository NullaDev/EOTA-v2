using System.Text.Json;
using Eota.Kernel.Matches;
using Eota.Server.Application;
using Eota.Transport.Contracts;

namespace Eota.Server.Infrastructure;

public sealed record FileMatchJournalOptions(int CompactAfterRecords = 512, int RetainedCommandKeys = 4096);

public sealed partial class FileMatchJournal
{
    private sealed record JournalAnchor(int Version, string InitialHash, string MatchId, int Sequence, string LogHash, byte[] State, StoredMatchCommand[] Commands);
    private sealed record AnchorEnvelope(string Data, string Hash);

    private void Retain(StoredMatchCommand command)
    {
        if (command.SystemTimeout is not null) { return; }
        _recent.Enqueue(command);
        while (_recent.Count > _options.RetainedCommandKeys) { _recent.Dequeue(); }
    }

    private MatchState? LoadAnchor(MatchCreationRequest request)
    {
        var path = Path.Combine(_directory, "journal-head.json");
        if (!File.Exists(path)) { return null; }
        if (new FileInfo(path).Length > 128 * 1024 * 1024) { throw new InvalidDataException("journal-head-size-budget"); }
        var wrapper = JsonSerializer.Deserialize<AnchorEnvelope>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("empty-journal-head");
        if (wrapper.Hash != Hash(0, _initialHash, wrapper.Data)) { throw new InvalidDataException("journal-head-hash-mismatch"); }
        var anchor = ContractJson.Deserialize<JournalAnchor>(wrapper.Data);
        if (anchor.Version != 1 || anchor.InitialHash != _initialHash || anchor.Sequence < 1 || !MatchHandshake.IsRuleContentHash(anchor.LogHash)
            || anchor.Commands is null || anchor.Commands.Length > 16384) { throw new InvalidDataException("invalid-journal-head"); }
        var state = MatchCheckpointCodec.Decode(anchor.State, request.Protocol, request.Content);
        BindMatchId(anchor.MatchId);
        foreach (var command in anchor.Commands) { BindMatchId(command.Envelope.MatchId); Retain(command); }
        _sequence = _compactedAt = anchor.Sequence; _lastHash = anchor.LogHash;
        return state;
    }

    private void Compact(MatchState state)
    {
        var anchor = new JournalAnchor(1, _initialHash, _matchId!, _sequence, _lastHash, MatchCheckpointCodec.Encode(state), _recent.ToArray());
        var data = ContractJson.Serialize(anchor);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new AnchorEnvelope(data, Hash(0, _initialHash, data)));
        if (bytes.Length > 128 * 1024 * 1024) { throw new IOException("journal-head-size-budget"); }
        // Commit the complete recovery root before atomically replacing the old segment. Either crash point can be loaded.
        AtomicFile.Write(Path.Combine(_directory, "journal-head.json"), bytes);
        AtomicFile.Write(Path.Combine(_directory, "commands.jsonl"), []);
        _compactedAt = _sequence;
    }
}
