using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Eota.Transport.Contracts;

public sealed record RoomContentPack([property: JsonRequired] string Id, [property: JsonRequired] string Version,
    [property: JsonRequired] string Hash);

public static class RoomContentPacks
{
    public const string HashHeader = "X-Eota-Content-Packs-Hash";
    public static void Validate(ImmutableArray<RoomContentPack> packages)
    {
        if (packages.IsDefaultOrEmpty || packages.Length > 128
            || packages.Any(p => p is null || p.Id is not { Length: > 0 and <= 64 }
                || p.Id.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c is not ('-' or '_' or '.'))
                || p.Version is not { Length: > 0 and <= 96 } || p.Version.Any(char.IsControl)
                || !MatchHandshake.IsRuleContentHash(p.Hash))
            || packages.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != packages.Length)
        { throw new InvalidDataException("content-pack-manifest-invalid"); }
    }

    public static string Hash(ImmutableArray<RoomContentPack> packages)
    {
        Validate(packages);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ContractJson.Serialize(
            packages.OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => new { p.Id, p.Hash }))))).ToLowerInvariant();
    }

    public static void RequireMatch(ImmutableArray<RoomContentPack> host, ImmutableArray<RoomContentPack> local)
    {
        Validate(host); Validate(local);
        var expected = host.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var actual = local.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (var id in expected.Keys.Union(actual.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            expected.TryGetValue(id, out var h); actual.TryGetValue(id, out var l);
            if (h is not null && l is not null && h.Hash == l.Hash) { continue; }
            errors.Add($"{id}：主机版本 {h?.Version ?? "未启用"} / 本机版本 {l?.Version ?? "未启用"}"
                + (h is not null && l is not null ? $"（hash 不同：{h.Hash[..12]} / {l.Hash[..12]}）" : ""));
        }
        if (errors.Count > 0) { throw new InvalidDataException("content-pack-mismatch\n" + string.Join('\n', errors)); }
    }
}
