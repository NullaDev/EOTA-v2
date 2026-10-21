using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eota.Kernel.Content;

/// <summary>A stable, case-insensitive content identifier; presentation belongs to content packages.</summary>
[JsonConverter(typeof(ProfessionJsonConverter))]
public readonly record struct Profession : IComparable<Profession>
{
    private readonly string? _value;
    private Profession(string value) => _value = value;
    public static Profession Neutral => default;
    public static bool TryParse(string? value, out Profession result)
    {
        result = default;
        if (value is not { Length: > 0 and <= 64 } || !char.IsAsciiLetter(value[0])
            || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.')) { return false; }
        var normalized = char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();
        result = normalized == "Neutral" ? Neutral : new(normalized); return true;
    }
    public static Profession Parse(string value) => TryParse(value, out var result) ? result
        : throw new FormatException("Invalid profession identifier: " + value);
    public override string ToString() => _value ?? "Neutral";
    public int CompareTo(Profession other) => StringComparer.Ordinal.Compare(ToString(), other.ToString());
    public static bool operator <(Profession left, Profession right) => left.CompareTo(right) < 0;
    public static bool operator >(Profession left, Profession right) => left.CompareTo(right) > 0;
    public static bool operator <=(Profession left, Profession right) => left.CompareTo(right) <= 0;
    public static bool operator >=(Profession left, Profession right) => left.CompareTo(right) >= 0;
}

public sealed class ProfessionJsonConverter : JsonConverter<Profession>
{
    public override Profession Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && Profession.TryParse(reader.GetString(), out var value)
            ? value : throw new JsonException("Expected a profession identifier.");
    public override void Write(Utf8JsonWriter writer, Profession value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}
