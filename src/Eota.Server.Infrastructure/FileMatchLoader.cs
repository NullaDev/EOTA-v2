using System.Text.Json;
using Eota.Content.Compiler;
using Eota.Kernel.Content;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;

namespace Eota.Server.Infrastructure;

public static class FileMatchLoader
{
    public static MatchCreationRequest Load(string directory, ulong seed, string? cardDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var cards = cardDirectory ?? Path.Combine(directory, "Cards");
        var content = CardContentCompiler.Compile(Directory.EnumerateFiles(cards, "*.json", SearchOption.AllDirectories)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Select(path => new ContentSourceDocument(Path.GetFileName(path), File.ReadAllText(path))));
        var protocol = ProtocolCompiler.Compile("protocol-v0.json", File.ReadAllText(Path.Combine(directory, "protocol-v0.json")));
        if (!content.IsSuccess || !protocol.IsSuccess)
        { throw new InvalidDataException(string.Join("; ", content.Diagnostics.Concat(protocol.Diagnostics).Select(value => $"{value.SourceName}:{value.Path} {value.Code}: {value.Message}"))); }
        return new MatchCreationRequest(protocol.Protocol!, content.Content!.Rules, seed,
            ReadDeck(Path.Combine(directory, "deck-one.json")), ReadDeck(Path.Combine(directory, "deck-two.json")));
    }

    private static DeckDefinition ReadDeck(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (!Enum.TryParse<Profession>(root.GetProperty("profession").GetString(), ignoreCase: true, out var profession)
            || !Enum.IsDefined(profession)) { throw new InvalidDataException("Unknown profession."); }
        return DeckDefinition.Create(profession, root.GetProperty("cards").EnumerateArray().Select(value => new DeckEntry(
            CardPrototypeId.Parse(value.GetProperty("id").GetString()!), value.GetProperty("copies").GetInt32())));
    }
}
