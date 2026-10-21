using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Eota.Content.Compiler;

namespace Eota.ContentCli;

public static class PackageBuilder
{
    private static readonly JsonSerializerOptions ReadOptions = new(ContentPackages.JsonOptions) { PropertyNameCaseInsensitive = true };
    public static ContentPackage Build(string directory)
    {
        var root = Path.GetFullPath(directory);
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
        var info = metadata.RootElement;
        var generated = Path.Combine(root, "Generated");
        T Read<T>(string relative) => JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(root, "Source", relative)),
            ReadOptions)!;
        var cards = Directory.EnumerateFiles(Path.Combine(root, "Source", "Cards"), "*.json", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path))).ToImmutableArray();
        var assets = Directory.Exists(Path.Combine(generated, "Art"))
            ? Directory.EnumerateFiles(Path.Combine(generated, "Art"), "*.png", SearchOption.AllDirectories)
                .ToImmutableDictionary(path => Path.GetRelativePath(generated, path).Replace('\\', '/'),
                    path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), StringComparer.Ordinal)
            : ImmutableDictionary<string, string>.Empty;
        var pack = ContentPackages.Seal(new(ContentPackage.Schema, info.GetProperty("id").GetString()!,
            info.GetProperty("version").GetString()!, info.GetProperty("name").GetString()!, "",
            info.GetProperty("dependencies").Deserialize<ImmutableArray<ContentDependency>>(ContentPackages.JsonOptions), cards,
            Read<ImmutableDictionary<string, string>>("Localization/zh-CN.json"), Read<ImmutableArray<ContentDeck>>("Decks/archetypes.json"))
        { Professions = Read<ImmutableArray<ContentProfession>>("professions.json"), Assets = assets, AssetDirectory = generated });
        PackageAssets.ValidateFiles(pack);
        // Dependencies are validated when loaded together; standalone packages can be checked here in full.
        if (pack.Dependencies.IsEmpty) { _ = ContentPackages.Load([pack]); }
        Program.Write(Path.Combine(generated, pack.Id + ".eotapack.json"), JsonSerializer.Serialize(pack, ContentPackages.JsonOptions) + "\n");
        return pack;
    }
}
