using System.Collections.Immutable;
using Eota.Kernel.Effects;
using Eota.Kernel.Primitives;
using Eota.Kernel.Resolution;

namespace Eota.Content.Compiler.Tests;

public sealed class HealingCompilerTests
{
    [Theory]
    [InlineData("field", "selfHealed", "minion", "invalid-trigger-source")]
    [InlineData("minion", "selfHealed", "field", "invalid-trigger-subject")]
    [InlineData("minion", "friendlyHealed", "hero", "invalid-trigger-subject")]
    [InlineData("field", "friendlyHeroHealed", "minion", "invalid-trigger-subject")]
    public void InvalidHealingSourcesAndSubjectsFailCompilation(string kind, string trigger, string subject, string error)
    {
        var stats = kind == "minion" ? "\"attack\":1,\"health\":3" : "\"lifetime\":{\"kind\":\"finite\",\"energy\":3}";
        var json = $$$"""
            {"schemaVersion":"eota.card/v2","id":"TEST-HEAL-TYPE","source":"test","profession":"neutral","kind":"{{{kind}}}","cost":1,{{{stats}}},
             "effects":[{"id":"heal","trigger":{"kind":"{{{trigger}}}","subjectType":"{{{subject}}}"},"body":{"kind":"damage","target":{"kind":"enemyHero"},"amount":"event.amount"}}]}
            """;
        var result = CardContentCompiler.Compile([new("healing.json", json)]);
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == error);
    }

    [Fact]
    public void HeroAttachmentsObserveHealingAndExposeATypedHeroEventSubject()
    {
        var card = EffectRuntimeTests.Minion("HERO-HEAL-OBSERVER", """
            [{"id":"entry","trigger":{"kind":"selfEntered"},"body":{"kind":"grantEffect","target":{"kind":"friendlyHero"},
              "effect":{"id":"prayer","trigger":{"kind":"friendlyHeroHealed"},"body":{"kind":"parallel","children":[
                {"kind":"damage","target":{"kind":"enemyHero"},"amount":"event.amount"},
                {"kind":"damage","target":{"kind":"eventSubject"},"amount":1}]}}}}]
            """);
        var state = EffectRuntimeTests.Resolve(EffectRuntimeTests.Plan(EffectRuntimeTests.Create([card]), PlayerId.One, "TEST-P6-HERO-HEAL-OBSERVER", 0)).State;
        state = state with { Players = state.Players.SetItem(0, state.Players[0] with { HeroHealth = 20 }) };
        var frame = FrameResolver.Resolve(state, [new HealHeroIntent(state.NextIntentId, PlayerId.One, 2)]);
        var invocation = Assert.Single(EffectTriggers.FromEvents(state, frame));
        Assert.Equal(EffectTargetType.Hero, invocation.Effect.Trigger.SubjectType);
        var intents = EffectEvaluator.Evaluate(FrameSnapshot.Create(frame.State), [invocation], frame.State.NextIntentId);
        var result = FrameResolver.Resolve(frame.State, intents);
        Assert.Equal(21, result.State.Players[0].HeroHealth);
        Assert.Equal(28, result.State.Players[1].HeroHealth);
    }
}
