using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Eota.Kernel.Commands;
using Eota.Kernel.Content;
using Eota.Kernel.Effects;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Protocols;
using Eota.Kernel.Rules;

namespace Eota.Server.Infrastructure;

// Private persistence format. Never send this payload to an observer: it contains decks and rule RNG.
public static class MatchCheckpointCodec
{
    public const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static byte[] Encode(MatchState state) => JsonSerializer.SerializeToUtf8Bytes(
        new Checkpoint(SchemaVersion, MatchStateHasher.CanonicalSchemaVersion, MatchStateHasher.Compute(state).ToString(), state), Options);

    public static MatchState Decode(ReadOnlySpan<byte> bytes, CompiledGameProtocol protocol, RuleContentPack content)
    {
        var checkpoint = JsonSerializer.Deserialize<Checkpoint>(bytes, Options) ?? throw new InvalidDataException("Empty checkpoint.");
        if (checkpoint.SchemaVersion != SchemaVersion || checkpoint.CanonicalStateVersion != MatchStateHasher.CanonicalSchemaVersion)
        { throw new InvalidDataException("Unsupported checkpoint schema."); }
        var state = checkpoint.State with { Protocol = protocol, Content = content };
        if (state.Manifest.ProtocolHash != protocol.Hash || state.Manifest.RuleContentHash != content.Hash)
        { throw new InvalidDataException("Checkpoint protocol or content differs from the loaded rules."); }
        if (!StringComparer.Ordinal.Equals(checkpoint.StateHash, MatchStateHasher.Compute(state).ToString()))
        { throw new InvalidDataException("Checkpoint state hash mismatch."); }
        Eota.Server.Application.MatchRecoveryValidator.Validate(state);
        return state;
    }

    private sealed record Checkpoint(int SchemaVersion, int CanonicalStateVersion, string StateHash, MatchState State);

    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            // Rule packs are injected from the verified manifest, not duplicated in the checkpoint.
            if (info.Type == typeof(MatchState))
            {
                foreach (var property in info.Properties.Where(value => value.Name is "Protocol" or "Content").ToArray())
                { property.ShouldSerialize = (_, _) => false; }
            }
            Register(info, typeof(BattlefieldEntityState), typeof(MinionEntityState), typeof(FieldEntityState));
            Register(info, typeof(IFieldLifetimeState), typeof(FiniteFieldLifetimeState), typeof(PermanentFieldLifetimeState));
            Register(info, typeof(AuthoritativeCommand), typeof(SubmitMulliganCommand), typeof(PlanCardCommand),
                typeof(PlanSpellCommand), typeof(CancelPlanCommand), typeof(SubmitTurnCommand), typeof(SystemTimeoutCommand));
            Register(info, typeof(IntExpression), typeof(ConstantExpression), typeof(VariableExpression), typeof(BinaryExpression));
            Register(info, typeof(EffectCondition), typeof(CompareCondition), typeof(ExistsCondition), typeof(KeywordCondition),
                typeof(AllCondition), typeof(NotCondition), typeof(CardMatchesCondition));
            Register(info, typeof(EffectNode), typeof(ParallelEffect), typeof(IfElseEffect), typeof(RetargetEffect),
                typeof(LifecycleEffect), typeof(EmitEffect), typeof(SequenceEffect), typeof(LoopEffect), typeof(SummonEffect), typeof(EffectErrorNode), typeof(HandEffect), typeof(GrantEffect), typeof(LaneStatusEffect));
        });
        var options = new JsonSerializerOptions
        {
            TypeInfoResolver = resolver,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 256
        };
        options.Converters.Add(new PlayerConverter());
        options.Converters.Add(new LaneConverter());
        options.Converters.Add(new PrototypeConverter());
        options.Converters.Add(new PlanConverter());
        options.Converters.Add(new OptionalArrayConverterFactory());
        return options;
    }

    private static void Register(JsonTypeInfo info, Type baseType, params Type[] variants)
    {
        if (info.Type != baseType) { return; }
        info.PolymorphismOptions = new JsonPolymorphismOptions();
        foreach (var variant in variants) { info.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(variant, variant.Name)); }
    }

    private sealed class PlayerConverter : JsonConverter<PlayerId>
    {
        public override PlayerId Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(reader.GetByte());
        public override void Write(Utf8JsonWriter writer, PlayerId value, JsonSerializerOptions options) => writer.WriteNumberValue(value.Value);
    }
    private sealed class LaneConverter : JsonConverter<LaneId>
    {
        public override LaneId Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(reader.GetInt32());
        public override void Write(Utf8JsonWriter writer, LaneId value, JsonSerializerOptions options) => writer.WriteNumberValue(value.Value);
    }
    private sealed class PrototypeConverter : JsonConverter<CardPrototypeId>
    {
        public override CardPrototypeId Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(reader.GetString()!);
        public override void Write(Utf8JsonWriter writer, CardPrototypeId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
    }
    private sealed class PlanConverter : JsonConverter<PlanCommandId>
    {
        public override PlanCommandId Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return new PlanCommandId(new PlayerId(document.RootElement.GetProperty("PlayerId").GetByte()), document.RootElement.GetProperty("Ordinal").GetUInt64());
        }
        public override void Write(Utf8JsonWriter writer, PlanCommandId value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("PlayerId", value.PlayerId.Value);
            writer.WriteNumber("Ordinal", value.Ordinal);
            writer.WriteEndObject();
        }
    }
    private sealed class OptionalArrayConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>);
        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(OptionalArrayConverter<>).MakeGenericType(type.GetGenericArguments()[0]))!;
    }
    private sealed class OptionalArrayConverter<T> : JsonConverter<ImmutableArray<T>>
    {
        public override ImmutableArray<T> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? default : JsonSerializer.Deserialize<T[]>(ref reader, options)!.ToImmutableArray();
        public override void Write(Utf8JsonWriter writer, ImmutableArray<T> value, JsonSerializerOptions options)
        {
            if (value.IsDefault) { writer.WriteNullValue(); return; }
            writer.WriteStartArray();
            foreach (var item in value) { JsonSerializer.Serialize(writer, item, options); }
            writer.WriteEndArray();
        }
    }
}
