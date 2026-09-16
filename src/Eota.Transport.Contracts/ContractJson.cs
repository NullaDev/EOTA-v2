using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eota.Transport.Contracts;

public static class ContractJson
{
    public const int Version = 0;
    public const int MaximumClientMessageBytes = 65536;
    public const int MaximumServerMessageBytes = 1048576;
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        RejectDuplicateProperties(document.RootElement);
        return JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("Null message.");
    }

    public static T RoundTrip<T>(T value) => Deserialize<T>(Serialize(value));

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 32
        };
        options.Converters.Add(new JsonStringEnumConverter<Audience>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new UInt64StringConverter());
        options.Converters.Add(new Int64StringConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException("Duplicate property.");
                }

                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in value.EnumerateArray())
            {
                RejectDuplicateProperties(element);
            }
        }
    }

    private sealed class UInt64StringConverter : JsonConverter<ulong>
    {
        public override ulong Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String
                || !ulong.TryParse(reader.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                || reader.GetString() != value.ToString(CultureInfo.InvariantCulture))
            {
                throw new JsonException("UInt64 must be a decimal string.");
            }

            return value;
        }

        public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }

    private sealed class Int64StringConverter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String
                || !long.TryParse(reader.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                || reader.GetString() != value.ToString(CultureInfo.InvariantCulture))
            {
                throw new JsonException("Int64 must be a decimal string.");
            }

            return value;
        }

        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
}

public static class ObserverViewHasher
{
    public static string Compute(ObserverView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var bytes = Encoding.UTF8.GetBytes("eota.observer-view/v0\n" + ContractJson.Serialize(view));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
