using Eota.Kernel.Rules;

namespace Eota.Kernel.Tests;

public sealed class AuthoritativeNumberTests
{
    [Fact]
    public void NegativeAttackAndCostRemainAuthoritativeButActAsZero()
    {
        Assert.Equal(0, AuthoritativeNumbers.DisplayAttack(-5));
        Assert.Equal(0, AuthoritativeNumbers.DamageFromAttack(long.MinValue));
        Assert.Equal(0, AuthoritativeNumbers.DisplayCost(-2));
        Assert.Equal(0, AuthoritativeNumbers.PayableCost(long.MinValue));
        Assert.Equal(7, AuthoritativeNumbers.DamageFromAttack(7));
        Assert.Equal(3, AuthoritativeNumbers.PayableCost(3));
    }

    [Fact]
    public void DeathConditionsUseNonPositiveHealthOrFiniteEnergy()
    {
        Assert.True(AuthoritativeNumbers.IsHeroDead(0));
        Assert.True(AuthoritativeNumbers.IsMinionDead(2, 0));
        Assert.True(AuthoritativeNumbers.IsMinionDead(0, 2));
        Assert.False(AuthoritativeNumbers.IsMinionDead(1, 1));
        Assert.True(AuthoritativeNumbers.IsFiniteFieldDestroyed(-1));
    }

    [Fact]
    public void PermanentFieldHasNoVisibleEnergyAndIgnoresNaturalDecay()
    {
        var permanent = new PermanentFieldLifetimeState();
        var afterDecay = permanent.ApplyEndTurnDecay(1);

        Assert.False(permanent.HasVisibleEnergy);
        Assert.Null(permanent.VisibleEnergy);
        Assert.False(permanent.IsDestroyed);
        Assert.Same(permanent, afterDecay);
    }

    [Fact]
    public void FiniteFieldDecaysAndIsDestroyedAtZero()
    {
        var finite = new FiniteFieldLifetimeState(1);
        var afterDecay = finite.ApplyEndTurnDecay(1);

        Assert.True(finite.HasVisibleEnergy);
        Assert.Equal(1, finite.VisibleEnergy);
        Assert.True(afterDecay.IsDestroyed);
        Assert.Equal(0, afterDecay.VisibleEnergy);
    }
}
