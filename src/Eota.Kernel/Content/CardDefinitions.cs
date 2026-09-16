using System.Collections.Immutable;
using Eota.Kernel.Primitives;
using Eota.Kernel.Effects;

namespace Eota.Kernel.Content;

public enum CardKind
{
    Minion = 1,
    Field = 2,
    Spell = 3
}

public enum CardSource
{
    Core = 1,
    Token = 2,
    Test = 3
}

public enum Profession
{
    Neutral = 0,
    Arcanist = 1,
    Guardian = 2,
    Hunter = 3,
    Artisan = 4,
    Soulbinder = 5
}

public enum SpellSpeed
{
    Fast = 1,
    Slow = 2
}

public enum SpellTargetScope
{
    Global = 1,
    Lane = 2
}

public enum MinionKeywordKind
{
    Swift = 1,
    Guard = 2,
    Slow = 3,
    Replace = 4,
    Replaceable = 5,
    Lifesteal = 6,
    Skirmisher = 7,
    Pursuit = 8,
    FirstStrike = 9,
    Execute = 10
}

public readonly record struct MinionKeywordDefinition(MinionKeywordKind Kind, long Parameter = 0);

public enum FieldKeywordKind
{
    Replace = 1,
    Replaceable = 2
}

public enum FieldLifetimeKind
{
    Finite = 1,
    Permanent = 2
}

public interface IFieldLifetimeDefinition
{
    FieldLifetimeKind Kind { get; }
}

public sealed record FiniteFieldLifetimeDefinition(long InitialEnergy) : IFieldLifetimeDefinition
{
    public FieldLifetimeKind Kind => FieldLifetimeKind.Finite;
}

public sealed record PermanentFieldLifetimeDefinition : IFieldLifetimeDefinition
{
    public FieldLifetimeKind Kind => FieldLifetimeKind.Permanent;
}

public abstract record CardDefinition(
    CardPrototypeId Id,
    CardKind Kind,
    CardSource Source,
    Profession Profession,
    long Cost)
{
    public ImmutableArray<EffectDefinition> Effects { get; init; } = [];
    public ImmutableArray<string> Tags { get; init; } = [];
    public long StoredCharge { get; init; }
}

public sealed record MinionCardDefinition(
    CardPrototypeId Id,
    CardSource Source,
    Profession Profession,
    long Cost,
    long Attack,
    long Health,
    ImmutableArray<MinionKeywordDefinition> Keywords)
    : CardDefinition(Id, CardKind.Minion, Source, Profession, Cost);

public sealed record FieldCardDefinition(
    CardPrototypeId Id,
    CardSource Source,
    Profession Profession,
    long Cost,
    IFieldLifetimeDefinition Lifetime,
    bool PreventsActiveAttacksInLane,
    ImmutableArray<FieldKeywordKind> Keywords = default)
    : CardDefinition(Id, CardKind.Field, Source, Profession, Cost)
{
}

public sealed record SpellCardDefinition(
    CardPrototypeId Id,
    CardSource Source,
    Profession Profession,
    long Cost,
    SpellSpeed Speed,
    SpellTargetScope TargetScope)
    : CardDefinition(Id, CardKind.Spell, Source, Profession, Cost);

public sealed record PresentationCardDefinition(
    CardPrototypeId Id,
    string NameLocalizationKey,
    string DescriptionLocalizationKey,
    string TexturePath);
