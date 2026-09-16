using System.Collections.Immutable;

namespace Eota.Transport.Contracts;

// Support data is an allowlist of metadata. No observer views, payloads, card IDs, names, tokens or RNG state.
public sealed record OperationTrace(string TraceId, string Kind, string Result, ulong Revision, double Milliseconds);
public sealed record SupportDiagnostics(int Version, string MatchId, Audience Audience, ulong MatchRevision,
    string ObserverViewHash, string ProtocolHash, string RuleContentHash, string Status, string Stage, int Turn,
    ImmutableArray<OperationTrace> Operations);
public sealed record RequestDiagnosticsPayload : ClientPayload;
public sealed record ServerDiagnosticsPayload(string RequestId, SupportDiagnostics Report) : ServerPayload;
