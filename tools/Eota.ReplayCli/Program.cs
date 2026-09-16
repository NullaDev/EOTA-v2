using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eota.Content.Compiler;
using Eota.Kernel.Content;
using Eota.Kernel.Determinism;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Protocols;

namespace Eota.ReplayCli;

internal static partial class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            return PrintUsage();
        }

        try
        {
            return args[0] switch
            {
                "record" => Record(args),
                "verify" => Verify(args),
                "record-match" => RecordMatch(args),
                "verify-match" => VerifyMatch(args),
                "record-checkpoints" => RecordCheckpoints(args),
                "verify-checkpoints" => VerifyCheckpoints(args),
                "rng-vector" => PrintRngVector(args),
                _ => PrintUsage()
            };
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"I/O error: {exception.Message}");
            return 2;
        }
        catch (UnauthorizedAccessException exception)
        {
            Console.Error.WriteLine($"Access error: {exception.Message}");
            return 2;
        }
        catch (JsonException exception)
        {
            Console.Error.WriteLine($"JSON error: {exception.Message}");
            return 2;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Invalid replay input: {exception.Message}");
            return 2;
        }
    }

    private static int Record(string[] args)
    {
        if (args.Length != 7 || !ulong.TryParse(args[5], NumberStyles.None, CultureInfo.InvariantCulture, out var seed))
        {
            return PrintUsage();
        }

        if (!TryLoadContent(args[1], out var content)
            || !TryLoadProtocol(args[2], out var protocol)
            || !TryLoadDeck(args[3], out var playerOneDeck)
            || !TryLoadDeck(args[4], out var playerTwoDeck))
        {
            return 2;
        }

        var result = MatchFactory.Create(new MatchCreationRequest(
            protocol!,
            content!.Rules,
            seed,
            playerOneDeck!,
            playerTwoDeck!));
        if (!result.IsSuccess || result.State is null)
        {
            PrintKernelErrors(result.Errors);
            return 3;
        }

        var stateHash = MatchStateHasher.Compute(result.State);
        var replay = new InitialReplayFile(
            1,
            result.State.Manifest.ProtocolId,
            result.State.Manifest.ProtocolVersion,
            result.State.Manifest.ProtocolHash.ToString(),
            result.State.Manifest.ContentSchemaVersion,
            result.State.Manifest.RuleContentHash.ToString(),
            seed,
            ToReplayDeck(result.State.Manifest.PlayerOneDeck),
            ToReplayDeck(result.State.Manifest.PlayerTwoDeck),
            stateHash.ToString(),
            result.State.RuleRng.SampleCount);

        var outputPath = Path.GetFullPath(args[6]);
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        File.WriteAllText(outputPath, JsonSerializer.Serialize(replay, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
        Console.WriteLine($"Recorded initial replay: {outputPath}");
        Console.WriteLine($"StateHash: {stateHash}");
        return 0;
    }

    private static int PrintRngVector(string[] args)
    {
        if (args.Length != 3
            || !ulong.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seed)
            || !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            || count < 0)
        {
            return PrintUsage();
        }

        var state = GlobalRuleRng.Create(seed);
        Console.WriteLine($"state={state.S0:x16},{state.S1:x16},{state.S2:x16},{state.S3:x16}");
        for (var index = 0; index < count; index++)
        {
            var sample = GlobalRuleRng.NextUInt64(state);
            state = sample.State;
            Console.WriteLine($"{index}:{sample.Value:x16}");
        }

        Console.WriteLine($"sampleCount={state.SampleCount}");
        return 0;
    }

    private static int Verify(string[] args)
    {
        if (args.Length != 4
            || !TryLoadContent(args[1], out var content)
            || !TryLoadProtocol(args[2], out var protocol))
        {
            return args.Length == 4 ? 2 : PrintUsage();
        }

        var replay = JsonSerializer.Deserialize<InitialReplayFile>(
            File.ReadAllText(args[3], Encoding.UTF8),
            JsonOptions);
        if (replay is null || replay.FormatVersion != 1)
        {
            Console.Error.WriteLine("Unsupported or empty replay file.");
            return 2;
        }

        if (!string.Equals(replay.ProtocolId, protocol!.Definition.ProtocolId, StringComparison.Ordinal)
            || replay.ProtocolVersion != protocol.Definition.ProtocolVersion
            || !string.Equals(replay.ProtocolHash, protocol.Hash.ToString(), StringComparison.Ordinal)
            || !string.Equals(replay.ContentSchemaVersion, content!.Rules.SchemaVersion, StringComparison.Ordinal)
            || !string.Equals(replay.RuleContentHash, content.Rules.Hash.ToString(), StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Manifest mismatch: protocol or rule content differs from the replay.");
            return 4;
        }

        var result = MatchFactory.Create(new MatchCreationRequest(
            protocol,
            content.Rules,
            replay.MatchSeed,
            FromReplayDeck(replay.PlayerOneDeck),
            FromReplayDeck(replay.PlayerTwoDeck)));
        if (!result.IsSuccess || result.State is null)
        {
            PrintKernelErrors(result.Errors);
            return 3;
        }

        var actualHash = MatchStateHasher.Compute(result.State).ToString();
        if (!string.Equals(actualHash, replay.ExpectedInitialStateHash, StringComparison.Ordinal)
            || result.State.RuleRng.SampleCount != replay.ExpectedRngSampleCount)
        {
            Console.Error.WriteLine($"ReplayHashMismatch: expected {replay.ExpectedInitialStateHash}, actual {actualHash}.");
            return 5;
        }

        Console.WriteLine($"Verified initial replay: {Path.GetFullPath(args[3])}");
        Console.WriteLine($"StateHash: {actualHash}");
        return 0;
    }

    private static bool TryLoadContent(string directory, out CompiledContentBundle? content)
    {
        var documents = Directory
            .EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new ContentSourceDocument(
                Path.GetRelativePath(directory, path).Replace('\\', '/'),
                File.ReadAllText(path, Encoding.UTF8)));
        var result = CardContentCompiler.Compile(documents);
        if (!result.IsSuccess || result.Content is null)
        {
            PrintContentDiagnostics(result.Diagnostics);
            content = null;
            return false;
        }

        content = result.Content;
        return true;
    }

    private static bool TryLoadProtocol(string path, out CompiledGameProtocol? protocol)
    {
        var result = ProtocolCompiler.Compile(path, File.ReadAllText(path, Encoding.UTF8));
        if (!result.IsSuccess || result.Protocol is null)
        {
            PrintContentDiagnostics(result.Diagnostics);
            protocol = null;
            return false;
        }

        protocol = result.Protocol;
        return true;
    }

    private static bool TryLoadDeck(string path, out DeckDefinition? deck)
    {
        var source = JsonSerializer.Deserialize<DeckSourceFile>(File.ReadAllText(path, Encoding.UTF8), JsonOptions);
        if (source is null)
        {
            Console.Error.WriteLine($"Deck file '{path}' is empty.");
            deck = null;
            return false;
        }

        deck = DeckDefinition.Create(
            source.Profession,
            source.Cards.Select(entry => new DeckEntry(CardPrototypeId.Parse(entry.Id), entry.Copies)));
        return true;
    }

    private static ReplayDeck ToReplayDeck(DeckDefinition deck) => new(
        deck.Profession,
        deck.Entries
            .OrderBy(entry => entry.CardId)
            .Select(entry => new ReplayDeckEntry(entry.CardId.Value, entry.Copies))
            .ToImmutableArray());

    private static DeckDefinition FromReplayDeck(ReplayDeck deck) => DeckDefinition.Create(
        deck.Profession,
        deck.Cards.Select(entry => new DeckEntry(CardPrototypeId.Parse(entry.Id), entry.Copies)));

    private static void PrintContentDiagnostics(IEnumerable<ContentDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            Console.Error.WriteLine($"{diagnostic.SourceName}{diagnostic.Path}: {diagnostic.Code}: {diagnostic.Message}");
        }
    }

    private static void PrintKernelErrors(IEnumerable<KernelError> errors)
    {
        foreach (var error in errors)
        {
            Console.Error.WriteLine($"{error.Code}: {error.Parameter}: {error.DetailCode}");
        }
    }

    private static int PrintUsage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  Eota.ReplayCli record <cards-dir> <protocol.json> <deck-one.json> <deck-two.json> <seed> <output.replay.json>");
        Console.Error.WriteLine("  Eota.ReplayCli verify <cards-dir> <protocol.json> <replay.json>");
        Console.Error.WriteLine("  Eota.ReplayCli record-match <cards-dir> <protocol.json> <deck-one.json> <deck-two.json> <seed> <commands.json> <output.replay.json>");
        Console.Error.WriteLine("  Eota.ReplayCli verify-match <cards-dir> <protocol.json> <replay.json>");
        Console.Error.WriteLine("  Eota.ReplayCli record-checkpoints <cards-dir> <protocol.json> <replay.json> <private-output-dir>");
        Console.Error.WriteLine("  Eota.ReplayCli verify-checkpoints <cards-dir> <protocol.json> <replay.json> <private-output-dir>");
        Console.Error.WriteLine("  Eota.ReplayCli rng-vector <seed> <count>");
        return 1;
    }

    private sealed record DeckSourceFile(Profession Profession, ImmutableArray<ReplayDeckEntry> Cards);

    private sealed record InitialReplayFile(
        int FormatVersion,
        string ProtocolId,
        int ProtocolVersion,
        string ProtocolHash,
        string ContentSchemaVersion,
        string RuleContentHash,
        ulong MatchSeed,
        ReplayDeck PlayerOneDeck,
        ReplayDeck PlayerTwoDeck,
        string ExpectedInitialStateHash,
        ulong ExpectedRngSampleCount);

    private sealed record ReplayDeck(Profession Profession, ImmutableArray<ReplayDeckEntry> Cards);

    private sealed record ReplayDeckEntry(string Id, int Copies);
}
