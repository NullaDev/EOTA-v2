using System.Text;

namespace Eota.ContentCli;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length is 2 or 3 && args[0] is "build" or "check" or "table")
            {
                var catalog = ContentCatalog.Load(args[1]);
                if (args[0] == "check") { Console.WriteLine($"Validated {catalog.Bundle.Rules.Cards.Length} experimental cards. RuleHash: {catalog.Bundle.Rules.Hash}"); }
                else if (args[0] == "table")
                {
                    var output = args.Length == 3 ? args[2] : Path.Combine(args[1], "Docs", "CardTable.zh-CN.md");
                    Write(output, catalog.RenderTable()); Console.WriteLine($"Card table: {Path.GetFullPath(output)}");
                }
                else
                {
                    var output = args.Length == 3 ? args[2] : Path.Combine(args[1], "Content", "Generated");
                    catalog.Build(output); Console.WriteLine($"Built {catalog.Bundle.Rules.Cards.Length} cards: {Path.GetFullPath(output)}");
                }
                return 0;
            }
            if (args.Length is 3 or 5 && args[0] == "emoji")
            {
                Write(args[2], EmojiArt.Svg(args[1], args.Length == 5 ? args[3] : "#614493", args.Length == 5 ? args[4] : "#1d1836"));
                Console.WriteLine($"Emoji SVG: {Path.GetFullPath(args[2])}"); return 0;
            }
            Console.Error.WriteLine("Usage: Eota.ContentCli build|check|table <repository-root> [output]\n       Eota.ContentCli emoji <emoji> <output.svg> [#top #bottom]");
            return 1;
        }
        catch (Exception error) when (error is IOException or ArgumentException or System.Text.Json.JsonException or InvalidDataException)
        { Console.Error.WriteLine(error.Message); return 2; }
    }

    internal static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, text.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));
    }
}
