using System.Diagnostics.CodeAnalysis;

namespace Eota.Kernel.Primitives;

public readonly record struct PlayerId : IComparable<PlayerId>
{
    public static PlayerId One { get; } = new(0);

    public static PlayerId Two { get; } = new(1);

    public byte Value { get; }

    public PlayerId(byte value)
    {
        if (value > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "A match has exactly two rule seats.");
        }

        Value = value;
    }

    public PlayerId Opponent => Value == 0 ? Two : One;

    public int CompareTo(PlayerId other) => Value.CompareTo(other.Value);

    public static bool operator <(PlayerId left, PlayerId right) => left.CompareTo(right) < 0;

    public static bool operator <=(PlayerId left, PlayerId right) => left.CompareTo(right) <= 0;

    public static bool operator >(PlayerId left, PlayerId right) => left.CompareTo(right) > 0;

    public static bool operator >=(PlayerId left, PlayerId right) => left.CompareTo(right) >= 0;

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public readonly record struct CardPrototypeId : IComparable<CardPrototypeId>
{
    private const int MaximumLength = 96;

    public string Value { get; }

    public CardPrototypeId(string value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                "Card prototype IDs must be 1-96 characters of ASCII letters, digits, '.', '_' or '-'.",
                nameof(value));
        }

        Value = value;
    }

    public static bool TryParse(string? value, out CardPrototypeId id)
    {
        if (!IsValid(value))
        {
            id = default;
            return false;
        }

        id = new CardPrototypeId(value!);
        return true;
    }

    public static CardPrototypeId Parse(string value) => new(value);

    public int CompareTo(CardPrototypeId other) => StringComparer.Ordinal.Compare(Value, other.Value);

    public static bool operator <(CardPrototypeId left, CardPrototypeId right) => left.CompareTo(right) < 0;

    public static bool operator <=(CardPrototypeId left, CardPrototypeId right) => left.CompareTo(right) <= 0;

    public static bool operator >(CardPrototypeId left, CardPrototypeId right) => left.CompareTo(right) > 0;

    public static bool operator >=(CardPrototypeId left, CardPrototypeId right) => left.CompareTo(right) >= 0;

    public override string ToString() => Value ?? string.Empty;

    private static bool IsValid([NotNullWhen(true)] string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaximumLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            var valid = character is >= 'A' and <= 'Z'
                        or >= 'a' and <= 'z'
                        or >= '0' and <= '9'
                        or '.' or '_' or '-';
            if (!valid)
            {
                return false;
            }
        }

        return true;
    }
}

public readonly record struct CardInstanceId(ulong Value) : IComparable<CardInstanceId>
{
    public int CompareTo(CardInstanceId other) => Value.CompareTo(other.Value);

    public static bool operator <(CardInstanceId left, CardInstanceId right) => left.CompareTo(right) < 0;

    public static bool operator <=(CardInstanceId left, CardInstanceId right) => left.CompareTo(right) <= 0;

    public static bool operator >(CardInstanceId left, CardInstanceId right) => left.CompareTo(right) > 0;

    public static bool operator >=(CardInstanceId left, CardInstanceId right) => left.CompareTo(right) >= 0;

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public readonly record struct LaneId : IComparable<LaneId>
{
    public int Value { get; }

    public LaneId(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
    }

    public int CompareTo(LaneId other) => Value.CompareTo(other.Value);

    public static bool operator <(LaneId left, LaneId right) => left.CompareTo(right) < 0;

    public static bool operator <=(LaneId left, LaneId right) => left.CompareTo(right) <= 0;

    public static bool operator >(LaneId left, LaneId right) => left.CompareTo(right) > 0;

    public static bool operator >=(LaneId left, LaneId right) => left.CompareTo(right) >= 0;

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public readonly record struct PlanCommandId : IComparable<PlanCommandId>
{
    public PlayerId PlayerId { get; }

    public ulong Ordinal { get; }

    public PlanCommandId(PlayerId playerId, ulong ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfZero(ordinal);
        PlayerId = playerId;
        Ordinal = ordinal;
    }

    public int CompareTo(PlanCommandId other)
    {
        var playerComparison = PlayerId.CompareTo(other.PlayerId);
        return playerComparison != 0 ? playerComparison : Ordinal.CompareTo(other.Ordinal);
    }

    public static bool operator <(PlanCommandId left, PlanCommandId right) => left.CompareTo(right) < 0;

    public static bool operator <=(PlanCommandId left, PlanCommandId right) => left.CompareTo(right) <= 0;

    public static bool operator >(PlanCommandId left, PlanCommandId right) => left.CompareTo(right) > 0;

    public static bool operator >=(PlanCommandId left, PlanCommandId right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"{PlayerId}:{Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}

public readonly record struct EntityId(ulong Value) : IComparable<EntityId>
{
    public int CompareTo(EntityId other) => Value.CompareTo(other.Value);

    public static bool operator <(EntityId left, EntityId right) => left.CompareTo(right) < 0;

    public static bool operator <=(EntityId left, EntityId right) => left.CompareTo(right) <= 0;

    public static bool operator >(EntityId left, EntityId right) => left.CompareTo(right) > 0;

    public static bool operator >=(EntityId left, EntityId right) => left.CompareTo(right) >= 0;
}

public readonly record struct CommandId(ulong Value) : IComparable<CommandId>
{
    public int CompareTo(CommandId other) => Value.CompareTo(other.Value);

    public static bool operator <(CommandId left, CommandId right) => left.CompareTo(right) < 0;

    public static bool operator <=(CommandId left, CommandId right) => left.CompareTo(right) <= 0;

    public static bool operator >(CommandId left, CommandId right) => left.CompareTo(right) > 0;

    public static bool operator >=(CommandId left, CommandId right) => left.CompareTo(right) >= 0;
}

public readonly record struct FrameId(ulong Value) : IComparable<FrameId>
{
    public int CompareTo(FrameId other) => Value.CompareTo(other.Value);

    public static bool operator <(FrameId left, FrameId right) => left.CompareTo(right) < 0;

    public static bool operator <=(FrameId left, FrameId right) => left.CompareTo(right) <= 0;

    public static bool operator >(FrameId left, FrameId right) => left.CompareTo(right) > 0;

    public static bool operator >=(FrameId left, FrameId right) => left.CompareTo(right) >= 0;
}

public readonly record struct WorkItemId(ulong Value) : IComparable<WorkItemId>
{
    public int CompareTo(WorkItemId other) => Value.CompareTo(other.Value);

    public static bool operator <(WorkItemId left, WorkItemId right) => left.CompareTo(right) < 0;

    public static bool operator <=(WorkItemId left, WorkItemId right) => left.CompareTo(right) <= 0;

    public static bool operator >(WorkItemId left, WorkItemId right) => left.CompareTo(right) > 0;

    public static bool operator >=(WorkItemId left, WorkItemId right) => left.CompareTo(right) >= 0;
}

public readonly record struct EffectId(ulong Value) : IComparable<EffectId>
{
    public int CompareTo(EffectId other) => Value.CompareTo(other.Value);

    public static bool operator <(EffectId left, EffectId right) => left.CompareTo(right) < 0;

    public static bool operator <=(EffectId left, EffectId right) => left.CompareTo(right) <= 0;

    public static bool operator >(EffectId left, EffectId right) => left.CompareTo(right) > 0;

    public static bool operator >=(EffectId left, EffectId right) => left.CompareTo(right) >= 0;
}

public readonly record struct IntentId(ulong Value) : IComparable<IntentId>
{
    public int CompareTo(IntentId other) => Value.CompareTo(other.Value);

    public static bool operator <(IntentId left, IntentId right) => left.CompareTo(right) < 0;

    public static bool operator <=(IntentId left, IntentId right) => left.CompareTo(right) <= 0;

    public static bool operator >(IntentId left, IntentId right) => left.CompareTo(right) > 0;

    public static bool operator >=(IntentId left, IntentId right) => left.CompareTo(right) >= 0;
}

public readonly record struct ConflictGroupId(ulong Value) : IComparable<ConflictGroupId>
{
    public int CompareTo(ConflictGroupId other) => Value.CompareTo(other.Value);

    public static bool operator <(ConflictGroupId left, ConflictGroupId right) => left.CompareTo(right) < 0;

    public static bool operator <=(ConflictGroupId left, ConflictGroupId right) => left.CompareTo(right) <= 0;

    public static bool operator >(ConflictGroupId left, ConflictGroupId right) => left.CompareTo(right) > 0;

    public static bool operator >=(ConflictGroupId left, ConflictGroupId right) => left.CompareTo(right) >= 0;
}

public readonly record struct ReceiptId(ulong Value) : IComparable<ReceiptId>
{
    public int CompareTo(ReceiptId other) => Value.CompareTo(other.Value);

    public static bool operator <(ReceiptId left, ReceiptId right) => left.CompareTo(right) < 0;

    public static bool operator <=(ReceiptId left, ReceiptId right) => left.CompareTo(right) <= 0;

    public static bool operator >(ReceiptId left, ReceiptId right) => left.CompareTo(right) > 0;

    public static bool operator >=(ReceiptId left, ReceiptId right) => left.CompareTo(right) >= 0;
}

public readonly record struct EventId(ulong Value) : IComparable<EventId>
{
    public int CompareTo(EventId other) => Value.CompareTo(other.Value);

    public static bool operator <(EventId left, EventId right) => left.CompareTo(right) < 0;

    public static bool operator <=(EventId left, EventId right) => left.CompareTo(right) <= 0;

    public static bool operator >(EventId left, EventId right) => left.CompareTo(right) > 0;

    public static bool operator >=(EventId left, EventId right) => left.CompareTo(right) >= 0;
}

public readonly record struct TombstoneId(ulong Value) : IComparable<TombstoneId>
{
    public int CompareTo(TombstoneId other) => Value.CompareTo(other.Value);

    public static bool operator <(TombstoneId left, TombstoneId right) => left.CompareTo(right) < 0;

    public static bool operator <=(TombstoneId left, TombstoneId right) => left.CompareTo(right) <= 0;

    public static bool operator >(TombstoneId left, TombstoneId right) => left.CompareTo(right) > 0;

    public static bool operator >=(TombstoneId left, TombstoneId right) => left.CompareTo(right) >= 0;
}
