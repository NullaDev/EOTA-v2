namespace Eota.Kernel.Rules;

public static class AuthoritativeNumbers
{
    public static long DisplayAttack(long authoritativeAttack) => NonNegativeView(authoritativeAttack);

    public static long DamageFromAttack(long authoritativeAttack) => NonNegativeView(authoritativeAttack);

    public static long DisplayCost(long authoritativeCost) => NonNegativeView(authoritativeCost);

    public static long PayableCost(long authoritativeCost) => NonNegativeView(authoritativeCost);

    public static long NonNegativeActionValue(long authoritativeValue) => NonNegativeView(authoritativeValue);

    public static bool IsHeroDead(long currentHealth) => currentHealth <= 0;

    public static bool IsMinionDead(long currentHealth, long maximumHealth, bool explicitlyKilled = false) =>
        explicitlyKilled || currentHealth <= 0 || maximumHealth <= 0;

    public static bool IsFiniteFieldDestroyed(long currentEnergy, bool explicitlyDestroyed = false) =>
        explicitlyDestroyed || currentEnergy <= 0;

    private static long NonNegativeView(long value) => value < 0 ? 0 : value;
}

public interface IFieldLifetimeState
{
    bool HasVisibleEnergy { get; }

    long? VisibleEnergy { get; }

    bool IsDestroyed { get; }

    IFieldLifetimeState ApplyEndTurnDecay(long decay);
}

public sealed record FiniteFieldLifetimeState(long Energy) : IFieldLifetimeState
{
    public bool HasVisibleEnergy => true;

    public long? VisibleEnergy => Energy;

    public bool IsDestroyed => AuthoritativeNumbers.IsFiniteFieldDestroyed(Energy);

    public IFieldLifetimeState ApplyEndTurnDecay(long decay)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decay);
        return new FiniteFieldLifetimeState(checked(Energy - decay));
    }
}

public sealed record PermanentFieldLifetimeState : IFieldLifetimeState
{
    public bool HasVisibleEnergy => false;

    public long? VisibleEnergy => null;

    public bool IsDestroyed => false;

    public IFieldLifetimeState ApplyEndTurnDecay(long decay)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decay);
        return this;
    }
}
