using Eota.Kernel.Determinism;

namespace Eota.Kernel.Protocols;

public enum DeckExhaustionPolicy
{
    NoFatigue = 1,
    IncreasingDamage = 2,
    InstantDeath = 3
}

public enum HandOverflowPolicy
{
    BurnDrawnCard = 1
}

public enum HandCapacityPriority
{
    ReturnThenGenerateThenDraw = 1
}

public enum DrawAllocationPolicy
{
    SourceEffectOrdinal = 2
}

public enum MovementConflictPolicy
{
    CenterFirst = 1,
    OutsideFirst = 2,
    AllFail = 3
}

public enum MovementDirectionPreference
{
    OutwardFirst = 1,
    InwardFirst = 2
}

public enum DeckConstructionPolicy
{
    CoreAndProfessionOrNeutral = 1,
    DevelopmentAnySource = 2,
    CoreProfessionOnly = 3,
    CoreAnyProfession = 4
}

public sealed record GameProtocolDefinition(
    string ProtocolId,
    int ProtocolVersion,
    long InitialHeroHealth,
    long InitialPlayerCost,
    long InitialMaxCost,
    int RequiredDeckSize,
    int MinCopiesPerCard,
    int MaxCopiesPerCard,
    int OpeningHandSize,
    bool MulliganEnabled,
    int HandLimit,
    int LaneCount,
    long MaxCostLimit,
    long MaxCostGrowthPerTurn,
    int CardsDrawnPerTurn,
    long FieldEnergyDecayPerTurn,
    long EffectDurationDecayPerTurn,
    long MinEtherActivation,
    long MaxEtherActivation,
    long EtherDecayPerTurn,
    int DefaultEffectLoopLimit,
    DeckExhaustionPolicy DeckExhaustionPolicy,
    HandOverflowPolicy HandOverflowPolicy,
    HandCapacityPriority HandCapacityPriority,
    DrawAllocationPolicy DrawAllocationPolicy,
    DeckConstructionPolicy DeckConstructionPolicy,
    string RandomAlgorithmVersion,
    int RandomCallSchemaVersion,
    int CanonicalStateVersion,
    int EffectLanguageVersion,
    MovementConflictPolicy MovementConflictPolicy = MovementConflictPolicy.CenterFirst,
    MovementDirectionPreference MovementDirectionPreference = MovementDirectionPreference.OutwardFirst,
    int MaxEffectTriggerFramesPerTurn = 256,
    int MaxEffectIntentsPerFrame = 4096)
{
    public static GameProtocolDefinition DefaultV0 { get; } = new(
        "eota.default",
        0,
        30,
        1,
        1,
        40,
        1,
        3,
        4,
        false,
        9,
        6,
        10,
        1,
        1,
        1,
        1,
        0,
        3,
        1,
        10,
        DeckExhaustionPolicy.NoFatigue,
        HandOverflowPolicy.BurnDrawnCard,
        HandCapacityPriority.ReturnThenGenerateThenDraw,
        DrawAllocationPolicy.SourceEffectOrdinal,
        DeckConstructionPolicy.CoreAndProfessionOrNeutral,
        GlobalRuleRng.AlgorithmVersion,
        GlobalRuleRng.CallSchemaVersion,
        MatchStateCanonicalVersion,
        SupportedEffectLanguageVersion);

    public const int MatchStateCanonicalVersion = Matches.MatchStateHasher.CanonicalSchemaVersion;
    public const int SupportedEffectLanguageVersion = 4;
}

public sealed record CompiledGameProtocol(GameProtocolDefinition Definition, Hash256 Hash)
{
    public const ushort CanonicalSchemaVersion = 3;

    public static CompiledGameProtocol Compile(GameProtocolDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Validate(definition);
        var hash = CanonicalHash.Compute("eota.protocol", CanonicalSchemaVersion, writer => Write(writer, definition));
        return new CompiledGameProtocol(definition, hash);
    }

    private static void Validate(GameProtocolDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.ProtocolId);
        if (definition.ProtocolVersion < 0
            || definition.InitialHeroHealth <= 0
            || definition.InitialPlayerCost < 0
            || definition.InitialMaxCost <= 0
            || definition.RequiredDeckSize is <= 0 or > 256
            || definition.MinCopiesPerCard <= 0
            || definition.MaxCopiesPerCard < definition.MinCopiesPerCard
            || definition.MaxCopiesPerCard > 256
            || definition.MinCopiesPerCard > definition.RequiredDeckSize
            || definition.OpeningHandSize < 0
            || definition.OpeningHandSize > definition.RequiredDeckSize
            || definition.OpeningHandSize > definition.HandLimit
            || definition.HandLimit <= 0
            || definition.LaneCount < 2
            || definition.MaxCostLimit < definition.InitialMaxCost
            || definition.MaxCostGrowthPerTurn < 0
            || definition.CardsDrawnPerTurn is < 0 or > 256
            || definition.FieldEnergyDecayPerTurn < 0
            || definition.EffectDurationDecayPerTurn < 0
            || definition.MaxEtherActivation < definition.MinEtherActivation
            || definition.EtherDecayPerTurn < 0
            || definition.DefaultEffectLoopLimit <= 0
            || definition.RandomCallSchemaVersion != GlobalRuleRng.CallSchemaVersion
            || definition.CanonicalStateVersion != GameProtocolDefinition.MatchStateCanonicalVersion
            || definition.EffectLanguageVersion != GameProtocolDefinition.SupportedEffectLanguageVersion
            || definition.MaxEffectTriggerFramesPerTurn is <= 0 or > 100000
            || definition.MaxEffectIntentsPerFrame is <= 0 or > 65536)
        {
            throw new ArgumentException("The game protocol contains one or more invalid values.", nameof(definition));
        }

        if (!string.Equals(definition.RandomAlgorithmVersion, GlobalRuleRng.AlgorithmVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException("The protocol requests an unsupported random algorithm.", nameof(definition));
        }

        if (!Enum.IsDefined(definition.DeckExhaustionPolicy)
            || !Enum.IsDefined(definition.HandOverflowPolicy)
            || !Enum.IsDefined(definition.HandCapacityPriority)
            || !Enum.IsDefined(definition.DrawAllocationPolicy)
            || !Enum.IsDefined(definition.DeckConstructionPolicy)
            || !Enum.IsDefined(definition.MovementConflictPolicy)
            || !Enum.IsDefined(definition.MovementDirectionPreference))
        {
            throw new ArgumentException("The protocol requests an unsupported rule policy.", nameof(definition));
        }

        if (definition.MovementConflictPolicy != MovementConflictPolicy.AllFail && definition.LaneCount % 2 != 0)
        {
            throw new ArgumentException("Positional movement conflict priority requires an even lane count.", nameof(definition));
        }
    }

    private static void Write(CanonicalWriter writer, GameProtocolDefinition value)
    {
        writer.WriteString(value.ProtocolId);
        writer.WriteInt32(value.ProtocolVersion);
        writer.WriteInt64(value.InitialHeroHealth);
        writer.WriteInt64(value.InitialPlayerCost);
        writer.WriteInt64(value.InitialMaxCost);
        writer.WriteInt32(value.RequiredDeckSize);
        writer.WriteInt32(value.MinCopiesPerCard);
        writer.WriteInt32(value.MaxCopiesPerCard);
        writer.WriteInt32(value.OpeningHandSize);
        writer.WriteBoolean(value.MulliganEnabled);
        writer.WriteInt32(value.HandLimit);
        writer.WriteInt32(value.LaneCount);
        writer.WriteInt64(value.MaxCostLimit);
        writer.WriteInt64(value.MaxCostGrowthPerTurn);
        writer.WriteInt32(value.CardsDrawnPerTurn);
        writer.WriteInt64(value.FieldEnergyDecayPerTurn);
        writer.WriteInt64(value.EffectDurationDecayPerTurn);
        writer.WriteInt64(value.MinEtherActivation);
        writer.WriteInt64(value.MaxEtherActivation);
        writer.WriteInt64(value.EtherDecayPerTurn);
        writer.WriteInt32(value.DefaultEffectLoopLimit);
        writer.WriteInt32((int)value.DeckExhaustionPolicy);
        writer.WriteInt32((int)value.HandOverflowPolicy);
        writer.WriteInt32((int)value.HandCapacityPriority);
        writer.WriteInt32((int)value.DrawAllocationPolicy);
        writer.WriteInt32((int)value.MovementConflictPolicy);
        writer.WriteInt32((int)value.MovementDirectionPreference);
        writer.WriteInt32((int)value.DeckConstructionPolicy);
        writer.WriteString(value.RandomAlgorithmVersion);
        writer.WriteInt32(value.RandomCallSchemaVersion);
        writer.WriteInt32(value.CanonicalStateVersion);
        writer.WriteInt32(value.EffectLanguageVersion);
        writer.WriteInt32(value.MaxEffectTriggerFramesPerTurn);
        writer.WriteInt32(value.MaxEffectIntentsPerFrame);
    }
}
