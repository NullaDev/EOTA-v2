using System.Collections.Immutable;
using System.Text.Json;
using Eota.Content.Compiler;

namespace Eota.Client.Desktop;

public sealed record DesktopPackageInfo(string Path, string Id, string Version, string Name, string Hash,
    ImmutableArray<string> Dependencies, int CardCount, bool BuiltIn, string? Error);
public sealed record DesktopPackageSelection(int Version, ImmutableArray<string> Enabled);

public sealed class DesktopContentPackages(string applicationRoot, string userDirectory)
{
    public string Root { get; } = Path.GetFullPath(applicationRoot);
    public string UserDirectory { get; } = Path.GetFullPath(userDirectory);
    public string SelectionPath => Path.Combine(UserDirectory, "content-selection.json");
    public string InstalledDirectory => Path.Combine(UserDirectory, "packs");

    public void MigrateLegacySelection(string legacySelectionPath)
    {
        if (File.Exists(SelectionPath) || !File.Exists(legacySelectionPath)) { return; }
        var path = File.ReadAllText(legacySelectionPath).Trim();
        if (path.Length == 0) { SaveSelection(DefaultSelection(Root)); return; }
        var package = DesktopContentEditor.ReadPackage(path);
        var destination = Path.Combine(InstalledDirectory, package.Id, package.Id + ".eotapack.json");
        if (!File.Exists(destination)) { Install(path); }
        SaveSelection([package.Id]);
    }
    public ImmutableArray<string> ReadSelection()
    {
        if (!File.Exists(SelectionPath)) { return DefaultSelection(Root); }
        var selected = JsonSerializer.Deserialize<DesktopPackageSelection>(File.ReadAllText(SelectionPath));
        if (selected is null || selected.Version != 1 || selected.Enabled.IsDefault || selected.Enabled.Length > 128
            || selected.Enabled.Any(id => !ContentPackages.IsId(id))
            || selected.Enabled.Distinct(StringComparer.Ordinal).Count() != selected.Enabled.Length)
        { throw new InvalidDataException("内容包启用配置无效，请在内容包管理中重新保存选择。"); }
        return selected.Enabled;
    }

    public void SaveSelection(IEnumerable<string> enabled)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        var ids = enabled.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
        if (ids.Length > 128 || ids.Any(id => !ContentPackages.IsId(id))) { throw new InvalidDataException("内容包选择无效。"); }
        // Persist the user's exact choice even when it is invalid; loading then blocks play until repaired.
        DesktopContentEditor.WriteAtomic(SelectionPath, JsonSerializer.Serialize(new DesktopPackageSelection(1, ids)));
    }

    public ImmutableArray<DesktopPackageInfo> Scan()
    {
        var entries = new List<DesktopPackageInfo>();
        foreach (var (directory, builtIn) in new[] { (Path.Combine(Root, "Content", "Packs"), true), (InstalledDirectory, false) })
        {
            if (!Directory.Exists(directory)) { continue; }
            foreach (var file in Directory.EnumerateFiles(directory, "*.eotapack.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                try
                {
                    var pack = ContentPackages.Read(file);
                    entries.Add(new(file, pack.Id, pack.Version, pack.Name, pack.Hash,
                        [.. pack.Dependencies.Select(d => d.Id + (d.Version is null ? "" : " @ " + d.Version) + (d.Hash is null ? "" : " # " + d.Hash))],
                        pack.Cards.Length, builtIn, null));
                }
                catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException)
                {
                    var id = Path.GetFileName(file).Replace(".eotapack.json", "", StringComparison.Ordinal);
                    var version = "未知";
                    try
                    {
                        if (new FileInfo(file).Length <= 8 * 1024 * 1024)
                        {
                            using var json = JsonDocument.Parse(File.ReadAllText(file));
                            if (json.RootElement.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String && ContentPackages.IsId(value.GetString())) { id = value.GetString()!; }
                            if (json.RootElement.TryGetProperty("version", out var number) && number.ValueKind == JsonValueKind.String) { version = number.GetString()!; }
                        }
                    }
                    catch (Exception detail) when (detail is IOException or JsonException or InvalidOperationException) { }
                    entries.Add(new(file, id, version, Path.GetFileName(file), "", [], 0, builtIn, error.Message));
                }
            }
        }
        return [.. entries.OrderBy(e => e.Id, StringComparer.Ordinal).ThenBy(e => e.Path, StringComparer.Ordinal)];
    }

    public DesktopCatalog LoadCatalog() => LoadCatalog(ReadSelection());

    public DesktopCatalog LoadCatalog(IEnumerable<string> enabled)
    {
        var entries = Scan(); var packs = new List<ContentPackage>();
        foreach (var id in enabled.Distinct(StringComparer.Ordinal))
        {
            var matching = entries.Where(e => e.Id == id).ToArray();
            if (matching.Length == 0) { throw new InvalidDataException($"已启用的内容包 {id} 未安装，请安装或取消启用。"); }
            if (matching.Length != 1) { throw new InvalidDataException($"内容包 ID 冲突：{id} 安装了多个副本，请移除重复包。"); }
            if (matching[0].Error is { } error) { throw new InvalidDataException($"{id} 无法加载：{error}"); }
            packs.Add(ContentPackages.Read(matching[0].Path));
        }
        return new DesktopCatalog(Root, packs);
    }

    public DesktopPackageInfo Install(string source, bool replace = false)
    {
        var package = DesktopContentEditor.ReadPackage(source);
        var current = Scan().Where(p => p.Id == package.Id).ToArray();
        if (current.Any(p => p.BuiltIn)) { throw new InvalidDataException("不能覆盖随游戏分发的内容包；请使用新的包 ID。"); }
        if (current.Length > 0 && !replace) { throw new InvalidDataException($"内容包 {package.Id} 已安装，需要确认替换。"); }
        var destination = Path.Combine(InstalledDirectory, package.Id, package.Id + ".eotapack.json");
        if (current.Any(p => Path.GetFullPath(p.Path) != Path.GetFullPath(destination)))
        { throw new InvalidDataException("同 ID 包位于其他目录，请先移除重复文件。"); }
        PackageAssets.CopyFiles(package, destination);
        DesktopContentEditor.WriteAtomic(destination, JsonSerializer.Serialize(package, ContentPackages.JsonOptions));
        return Scan().Single(p => p.Path == destination);
    }

    public void Remove(DesktopPackageInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var path = Path.GetFullPath(info.Path);
        if (info.BuiltIn || !path.StartsWith(InstalledDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        { throw new InvalidDataException("只能移除用户安装的内容包。"); }
        File.Delete(path);
    }


    public static ImmutableArray<string> DefaultSelection(string root)
    {
        var path = Path.Combine(root, "Content", "default-selection.json");
        var ids = JsonSerializer.Deserialize<ImmutableArray<string>>(File.ReadAllText(path));
        if (ids.IsDefault || ids.Any(id => !ContentPackages.IsId(id))) { throw new InvalidDataException("发行版默认内容包配置无效。"); }
        return ids;
    }

    internal static LoadedContentPackages LoadFile(string root, string? path)
    {
        var available = Directory.EnumerateFiles(Path.Combine(root, "Content", "Packs"), "*.eotapack.json", SearchOption.AllDirectories)
            .Select(ContentPackages.Read).ToDictionary(p => p.Id, StringComparer.Ordinal);
        var chosen = new Dictionary<string, ContentPackage>(StringComparer.Ordinal);
        void Include(ContentPackage pack)
        {
            if (!chosen.TryAdd(pack.Id, pack)) { return; }
            foreach (var dependency in pack.Dependencies)
            {
                if (!available.TryGetValue(dependency.Id, out var required)) { throw new InvalidDataException("缺少依赖：" + dependency.Id); }
                Include(required);
            }
        }
        if (path is not null) { Include(DesktopContentEditor.ReadPackage(path)); }
        else { foreach (var id in DefaultSelection(root)) { Include(available[id]); } }
        return ContentPackages.Load(chosen.Values);
    }
}

