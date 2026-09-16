using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eota.Kernel.Protocols;

namespace Eota.Content.Compiler;

public sealed record ProtocolCompilationResult(
    CompiledGameProtocol? Protocol,
    ImmutableArray<ContentDiagnostic> Diagnostics)
{
    public bool IsSuccess => Protocol is not null && Diagnostics.IsEmpty;
}

public static class ProtocolCompiler
{
    public const string SupportedSchemaVersion = "eota.protocol/v0";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static ProtocolCompilationResult Compile(string sourceName, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using var validationDocument = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
            if (JsonAuthoringValidator.TryFindDuplicateProperty(validationDocument.RootElement, "$", out var duplicatePath))
            {
                return Failure(sourceName, "duplicate-property", $"Duplicate JSON property at '{duplicatePath}'.");
            }

            var document = JsonSerializer.Deserialize<ProtocolAuthoringDocument>(json, JsonOptions);
            if (document is null)
            {
                return Failure(sourceName, "empty-protocol", "The protocol document is empty.");
            }

            if (!string.Equals(document.SchemaVersion, SupportedSchemaVersion, StringComparison.Ordinal))
            {
                return Failure(sourceName, "unsupported-schema", $"Expected schemaVersion '{SupportedSchemaVersion}'.");
            }

            try
            {
                return new ProtocolCompilationResult(
                    CompiledGameProtocol.Compile(document.Protocol),
                    ImmutableArray<ContentDiagnostic>.Empty);
            }
            catch (ArgumentException exception)
            {
                return Failure(sourceName, "invalid-protocol", exception.Message);
            }
        }
        catch (JsonException exception)
        {
            return Failure(
                sourceName,
                "invalid-json",
                $"Invalid protocol JSON at {exception.Path ?? "$"}, line {exception.LineNumber}, byte {exception.BytePositionInLine}: {exception.Message}");
        }
    }

    private static ProtocolCompilationResult Failure(string sourceName, string code, string message)
    {
        return new ProtocolCompilationResult(
            null,
            ImmutableArray.Create(new ContentDiagnostic(
                ContentDiagnosticSeverity.Error,
                code,
                sourceName,
                "$",
                message)));
    }

    private sealed record ProtocolAuthoringDocument(
        [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
        [property: JsonPropertyName("protocol")] GameProtocolDefinition Protocol);
}
