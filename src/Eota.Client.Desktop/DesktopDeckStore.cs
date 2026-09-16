using System.Collections.Immutable;

namespace Eota.Client.Desktop;

// Storage identity is independent of the displayed name, so renaming and deleting target one file.
public sealed class DesktopDeckStore
{
    private readonly string _directory;
    public DesktopDeckStore(string directory)
    { _directory = Path.GetFullPath(directory); Directory.CreateDirectory(_directory); }
    public ImmutableArray<string> Ids => [.. Directory.EnumerateFiles(_directory, "*.json")
        .Select(path => Path.GetFileNameWithoutExtension(path)).Order(StringComparer.Ordinal)];
    public DesktopDeck Read(string id)
    {
        var path = Resolve(id);
        if (new FileInfo(path).Length > 1024 * 1024) { throw new InvalidDataException("牌组文件过大。"); }
        var deck = DesktopCatalog.LoadDeck(path);
        if (string.IsNullOrWhiteSpace(deck.Name) || deck.Cards.IsDefault) { throw new InvalidDataException("牌组文件不完整。"); }
        return deck;
    }
    public string Save(DesktopDeck deck, string? id = null)
    {
        if (string.IsNullOrWhiteSpace(deck.Name) || deck.Name.Length > 120) { throw new InvalidDataException("牌组名称需为 1–120 字。"); }
        id ??= Guid.NewGuid().ToString("N");
        DesktopContentEditor.WriteAtomic(Resolve(id), System.Text.Json.JsonSerializer.Serialize(deck));
        return id;
    }
    public void Delete(string id) => File.Delete(Resolve(id));
    private string Resolve(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id is "." or ".." || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || id.Contains('/') || id.Contains('\\')) { throw new InvalidDataException("无效牌组文件标识。"); }
        var path = Path.GetFullPath(Path.Combine(_directory, id + ".json"));
        if (!string.Equals(Path.GetDirectoryName(path), _directory, StringComparison.OrdinalIgnoreCase))
        { throw new InvalidDataException("牌组文件超出保存目录。"); }
        return path;
    }
}
