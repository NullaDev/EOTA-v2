using Eota.Kernel.Matches;
using Eota.Kernel.Primitives;
using Eota.Server.Infrastructure;

namespace Eota.Server.IntegrationTests;

public sealed class P10RecoveryBoundsTests
{
    [Theory]
    [InlineData("revision")]
    [InlineData("turn")]
    [InlineData("card")]
    [InlineData("entity")]
    [InlineData("command")]
    [InlineData("frame")]
    [InlineData("work")]
    [InlineData("intent")]
    [InlineData("conflict")]
    [InlineData("receipt")]
    [InlineData("event")]
    [InlineData("tombstone")]
    [InlineData("program")]
    [InlineData("plan")]
    [InlineData("player")]
    [InlineData("rng")]
    public void CorrectlyHashedButExhaustedCheckpointIsRejected(string counter)
    {
        var state = MatchFactory.Create(Fixture.Load()).State!;
        state = counter switch
        {
            "revision" => state with { Revision = ulong.MaxValue }, "turn" => state with { Turn = int.MaxValue },
            "card" => state with { NextCardInstanceId = new CardInstanceId(ulong.MaxValue) },
            "entity" => state with { NextEntityId = new EntityId(ulong.MaxValue) },
            "command" => state with { NextCommandId = new CommandId(ulong.MaxValue) },
            "frame" => state with { NextFrameId = new FrameId(ulong.MaxValue) },
            "work" => state with { NextWorkItemId = new WorkItemId(ulong.MaxValue) },
            "intent" => state with { NextIntentId = new IntentId(ulong.MaxValue) },
            "conflict" => state with { NextConflictGroupId = new ConflictGroupId(ulong.MaxValue) },
            "receipt" => state with { NextReceiptId = new ReceiptId(ulong.MaxValue) },
            "event" => state with { NextEventId = new EventId(ulong.MaxValue) },
            "tombstone" => state with { NextTombstoneId = new TombstoneId(ulong.MaxValue) },
            "program" => state with { NextEffectProgramId = ulong.MaxValue },
            "plan" => state with { Players = state.Players.SetItem(0, state.Players[0] with { NextPlanOrdinal = ulong.MaxValue }) },
            "player" => state with { Players = state.Players.SetItem(0, state.Players[0] with { CommandRevision = ulong.MaxValue }) },
            "rng" => state with { RuleRng = state.RuleRng with { SampleCount = ulong.MaxValue } },
            _ => throw new InvalidOperationException()
        };
        var error = Assert.Throws<InvalidDataException>(() => MatchCheckpointCodec.Decode(MatchCheckpointCodec.Encode(state), state.Protocol, state.Content));
        Assert.Equal("checkpoint-counter-exhausted", error.Message);
    }

    [Fact]
    public void NextCardIdCannotReuseAnExistingIdentityEvenWithAValidHash()
    {
        var state = MatchFactory.Create(Fixture.Load()).State! with { NextCardInstanceId = new CardInstanceId(1) };
        var error = Assert.Throws<InvalidDataException>(() => MatchCheckpointCodec.Decode(MatchCheckpointCodec.Encode(state), state.Protocol, state.Content));
        Assert.Equal("checkpoint-id-reuse", error.Message);
    }
}
