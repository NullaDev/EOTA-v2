using System.Text;
using System.Text.Json.Nodes;
using Eota.Kernel.Protocols;

namespace Eota.Content.Compiler.Tests;

public sealed class ProtocolCompilerTests
{
    [Fact]
    public void UnsupportedRuleSchemaVersionsCannotRunUnderCurrentSemantics()
    {
        var original = GameProtocolDefinition.DefaultV0;
        Assert.Throws<ArgumentException>(() => CompiledGameProtocol.Compile(original with { RandomCallSchemaVersion = 2 }));
        Assert.Throws<ArgumentException>(() => CompiledGameProtocol.Compile(original with { CanonicalStateVersion = 5 }));
        Assert.Throws<ArgumentException>(() => CompiledGameProtocol.Compile(original with { EffectLanguageVersion = 1 }));
    }

    [Fact]
    public void EffectBudgetsAreValidatedAndPinnedInProtocolHash()
    {
        var original = CompiledGameProtocol.Compile(GameProtocolDefinition.DefaultV0);
        Assert.NotEqual(original.Hash, CompiledGameProtocol.Compile(original.Definition with { MaxEffectTriggerFramesPerTurn = 100 }).Hash);
        Assert.NotEqual(original.Hash, CompiledGameProtocol.Compile(original.Definition with { MaxEffectIntentsPerFrame = 100 }).Hash);
        Assert.Throws<ArgumentException>(() => CompiledGameProtocol.Compile(original.Definition with { MaxEffectTriggerFramesPerTurn = 0 }));
        Assert.Throws<ArgumentException>(() => CompiledGameProtocol.Compile(original.Definition with { MaxEffectIntentsPerFrame = int.MaxValue }));
    }

    [Fact]
    public void CompilesCheckedInDefaultProtocol()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "Content", "Source", "Protocols", "default-v0.json");

        var result = ProtocolCompiler.Compile(path, File.ReadAllText(path, Encoding.UTF8));

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Protocol);
        Assert.Equal("eota.default", result.Protocol.Definition.ProtocolId);
        Assert.Equal(MovementConflictPolicy.CenterFirst, result.Protocol.Definition.MovementConflictPolicy);
        Assert.Equal(MovementDirectionPreference.OutwardFirst, result.Protocol.Definition.MovementDirectionPreference);
    }

    [Theory]
    [InlineData("centerFirst", false)]
    [InlineData("outsideFirst", false)]
    [InlineData("allFail", true)]
    public void PositionalMovementPriorityRequiresEvenLaneCount(string policy, bool allowed)
    {
        var document = DefaultDocument();
        document["protocol"]!["laneCount"] = 5;
        document["protocol"]!["movementConflictPolicy"] = policy;

        var result = ProtocolCompiler.Compile("odd-lanes.json", document.ToJsonString());

        Assert.Equal(allowed, result.IsSuccess);
        if (!allowed)
        {
            Assert.Contains(result.Diagnostics, value => value.Code == "invalid-protocol");
        }
    }

    [Fact]
    public void OmittedMovementOptionsUseDefaultsAndEachOptionChangesTheProtocolHash()
    {
        var document = DefaultDocument();
        var original = ProtocolCompiler.Compile("original.json", document.ToJsonString());
        var protocol = document["protocol"]!.AsObject();
        protocol.Remove("movementConflictPolicy");
        protocol.Remove("movementDirectionPreference");
        var defaults = ProtocolCompiler.Compile("defaults.json", document.ToJsonString());
        Assert.True(defaults.IsSuccess);
        Assert.Equal(original.Protocol!.Hash, defaults.Protocol!.Hash);

        protocol["movementConflictPolicy"] = "allFail";
        var conflict = ProtocolCompiler.Compile("conflict.json", document.ToJsonString());
        Assert.True(conflict.IsSuccess);
        Assert.NotEqual(defaults.Protocol.Hash, conflict.Protocol!.Hash);
        protocol["movementConflictPolicy"] = "centerFirst";
        protocol["movementDirectionPreference"] = "inwardFirst";
        var direction = ProtocolCompiler.Compile("direction.json", document.ToJsonString());
        Assert.True(direction.IsSuccess);
        Assert.NotEqual(defaults.Protocol.Hash, direction.Protocol!.Hash);
    }

    [Fact]
    public void UnsupportedMovementEnumsAreRejectedByTheKernel()
    {
        Assert.Throws<ArgumentException>(() => CompiledGameProtocol.Compile(GameProtocolDefinition.DefaultV0 with
        {
            MovementConflictPolicy = (MovementConflictPolicy)99
        }));
        Assert.Throws<ArgumentException>(() => CompiledGameProtocol.Compile(GameProtocolDefinition.DefaultV0 with
        {
            MovementDirectionPreference = (MovementDirectionPreference)99
        }));
    }

    [Theory]
    [InlineData("deckExhaustionPolicy")]
    [InlineData("handOverflowPolicy")]
    [InlineData("handCapacityPriority")]
    [InlineData("drawAllocationPolicy")]
    [InlineData("deckConstructionPolicy")]
    public void MissingOrUnsupportedRequiredPoliciesCannotSilentlyUseUnspecifiedRules(string property)
    {
        var document = DefaultDocument();
        document["protocol"]!.AsObject().Remove(property);
        var result = ProtocolCompiler.Compile("missing-policy.json", document.ToJsonString());
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, value => value.Code == "invalid-protocol");

        var original = GameProtocolDefinition.DefaultV0;
        var unsupported = property switch
        {
            "deckExhaustionPolicy" => original with { DeckExhaustionPolicy = (DeckExhaustionPolicy)99 },
            "handOverflowPolicy" => original with { HandOverflowPolicy = (HandOverflowPolicy)99 },
            "handCapacityPriority" => original with { HandCapacityPriority = (HandCapacityPriority)99 },
            "drawAllocationPolicy" => original with { DrawAllocationPolicy = (DrawAllocationPolicy)99 },
            "deckConstructionPolicy" => original with { DeckConstructionPolicy = (DeckConstructionPolicy)99 },
            _ => throw new InvalidOperationException()
        };
        Assert.Throws<ArgumentException>(() => CompiledGameProtocol.Compile(unsupported));
    }

    private static JsonNode DefaultDocument() => JsonNode.Parse(File.ReadAllText(
        Path.Combine(FindRepositoryRoot(), "Content", "Source", "Protocols", "default-v0.json")))!;

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Eota.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
