using System.Collections.Immutable;
using Eota.Kernel.Content;
using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Kernel.Protocols;

namespace Eota.Kernel.Tests;

public sealed class MatchFactoryTests
{
    private static readonly CardPrototypeId CardA = new("TEST-A");
    private static readonly CardPrototypeId CardB = new("TEST-B");

    [Fact]
    public void InitialStateIsIndependentOfDeckEntryInsertionOrder()
    {
        var first = CreateMatch(
            [new DeckEntry(CardB, 2), new DeckEntry(CardA, 2)],
            [new DeckEntry(CardA, 2), new DeckEntry(CardB, 2)],
            1234);
        var second = CreateMatch(
            [new DeckEntry(CardA, 2), new DeckEntry(CardB, 2)],
            [new DeckEntry(CardB, 2), new DeckEntry(CardA, 2)],
            1234);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(MatchStateHasher.Compute(first.State!), MatchStateHasher.Compute(second.State!));
        Assert.Equal(6UL, first.State!.RuleRng.SampleCount);
        Assert.Equal(Enumerable.Range(1, 8).Select(value => (ulong)value), first.State.CardInstances.Select(card => card.Id.Value));
    }

    [Fact]
    public void DifferentSeedChangesInitialStateHash()
    {
        var first = CreateMatch(DefaultEntries(), DefaultEntries(), 1);
        var second = CreateMatch(DefaultEntries(), DefaultEntries(), 2);

        Assert.NotEqual(MatchStateHasher.Compute(first.State!), MatchStateHasher.Compute(second.State!));
    }

    [Fact]
    public void InvalidCopyCountReturnsStableDeckError()
    {
        var result = CreateMatch(
            [new DeckEntry(CardA, 3), new DeckEntry(CardB, 1)],
            DefaultEntries(),
            1);

        var error = Assert.Single(result.Errors.Where(value => value.DetailCode == "copy-count-out-of-range"));
        Assert.Equal(KernelErrorCode.InvalidDeck, error.Code);
        Assert.Null(result.State);
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void ExtremeInvalidCopyCountsReturnDiagnosticsWithoutOverflow(int copies)
    {
        var result = CreateMatch([new DeckEntry(CardA, copies), new DeckEntry(CardB, copies)], DefaultEntries(), 1);
        Assert.Null(result.State);
        Assert.Equal(2, result.Errors.Count(value => value.DetailCode == "copy-count-out-of-range"));
        Assert.Contains(result.Errors, value => value.DetailCode == "incorrect-deck-size");
    }

    private static MatchCreationResult CreateMatch(
        IEnumerable<DeckEntry> playerOne,
        IEnumerable<DeckEntry> playerTwo,
        ulong seed)
    {
        var content = RuleContentPack.Create(
            "eota.card/v2",
            [
                new MinionCardDefinition(CardA, CardSource.Test, Profession.Neutral, 1, 1, 2, ImmutableArray<MinionKeywordDefinition>.Empty),
                new MinionCardDefinition(CardB, CardSource.Test, Profession.Neutral, 2, 2, 3, ImmutableArray<MinionKeywordDefinition>.Empty)
            ]);
        var protocol = CompiledGameProtocol.Compile(GameProtocolDefinition.DefaultV0 with
        {
            ProtocolId = "eota.test",
            RequiredDeckSize = 4,
            MaxCopiesPerCard = 2,
            OpeningHandSize = 2,
            HandLimit = 4,
            DeckConstructionPolicy = DeckConstructionPolicy.DevelopmentAnySource
        });

        return MatchFactory.Create(new MatchCreationRequest(
            protocol,
            content,
            seed,
            DeckDefinition.Create(Profession.Neutral, playerOne),
            DeckDefinition.Create(Profession.Neutral, playerTwo)));
    }

    private static DeckEntry[] DefaultEntries() => [new(CardA, 2), new(CardB, 2)];
}
