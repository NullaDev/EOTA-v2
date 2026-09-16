namespace Eota.Transport.Contracts;

public static class MatchHandshake
{
    public const string RuleContentHashHeader = "X-Eota-Rule-Content-Hash";
    public const string ProtocolHashHeader = "X-Eota-Protocol-Hash";
    public static bool IsRuleContentHash(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
