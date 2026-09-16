using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Eota.Transport.Contracts;

public sealed record RoomDeckCard([property: JsonRequired] string Id, [property: JsonRequired] int Copies);
public sealed record RoomDeck([property: JsonRequired] string Profession, [property: JsonRequired] ImmutableArray<RoomDeckCard> Cards);
public sealed record RoomAdmission([property: JsonRequired] int Version, [property: JsonRequired] string RuleContentHash,
    [property: JsonRequired] string ProtocolHash, [property: JsonRequired] RoomDeck Deck, [property: JsonRequired] string RoomSettingsHash,
    [property: JsonRequired] ImmutableDictionary<string, string> CardRules);
public sealed record RoomDescription(int Version, string MatchId, string Name, string RuleContentHash, string ProtocolHash,
    string ProtocolJson, bool PlayerOneReady, bool PlayerTwoReady, bool Started, string? OwnDeckHash, RoomDeck? SuggestedDeck,
    [property: JsonRequired] RoomTiming Timing, [property: JsonRequired] string RoomSettingsHash);

// Host policy is confirmed separately from deterministic gameplay protocol; wall time never enters rule hashes.
public sealed record RoomTiming(int MulliganSeconds = 0, int PlanningSeconds = 0)
{
    public const string SettingsHashHeader = "X-Eota-Room-Settings-Hash";
    public bool IsLimited => MulliganSeconds != 0 || PlanningSeconds != 0;
    public bool IsValid => Valid(MulliganSeconds) && Valid(PlanningSeconds);
    private static bool Valid(int seconds) => seconds == 0 || seconds is >= 5 and <= 3600;
    public string ComputeHash(string ruleHash, string protocolHash)
    {
        if (!IsValid) { throw new InvalidDataException("invalid-room-timing"); }
        var canonical = FormattableString.Invariant($"eota.room-settings/1\n{ruleHash}\n{protocolHash}\n{MulliganSeconds}\n{PlanningSeconds}");
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
