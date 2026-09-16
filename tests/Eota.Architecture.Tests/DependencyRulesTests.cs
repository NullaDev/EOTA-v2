using System.Xml.Linq;

namespace Eota.Architecture.Tests;

public sealed class DependencyRulesTests
{
    private static readonly string[] AllowedApplicationReferences =
        ["..\\Eota.Kernel\\Eota.Kernel.csproj", "..\\Eota.Transport.Contracts\\Eota.Transport.Contracts.csproj"];
    private static readonly string[] AllowedAiReferences =
        ["../Eota.Client.Core/Eota.Client.Core.csproj", "../Eota.Transport.Contracts/Eota.Transport.Contracts.csproj"];

    [Fact]
    public void AiOnlyReferencesClientCoreAndObserverContracts()
    {
        var root = FindRepositoryRoot();
        var project = XDocument.Load(Path.Combine(root, "src", "Eota.Client.AI", "Eota.Client.AI.csproj"));
        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Empty(project.Descendants("FrameworkReference"));
        Assert.All(project.Descendants("ProjectReference"), reference => Assert.Contains(reference.Attribute("Include")!.Value,
            AllowedAiReferences));
        foreach (var source in Directory.EnumerateFiles(Path.Combine(root, "src", "Eota.Client.AI"), "*.cs"))
        {
            var text = File.ReadAllText(source);
            Assert.DoesNotContain("Eota.Kernel", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Eota.Server", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void KernelIsBclOnly()
    {
        var root = FindRepositoryRoot();
        var project = XDocument.Load(Path.Combine(root, "src", "Eota.Kernel", "Eota.Kernel.csproj"));

        Assert.Empty(project.Descendants("ProjectReference"));
        Assert.Empty(project.Descendants("PackageReference"));
    }

    [Fact]
    public void TransportContractsDoNotReferenceKernelOrFrameworkAdapters()
    {
        var root = FindRepositoryRoot();
        var project = XDocument.Load(Path.Combine(root, "src", "Eota.Transport.Contracts", "Eota.Transport.Contracts.csproj"));

        Assert.Empty(project.Descendants("ProjectReference"));
        Assert.Empty(project.Descendants("PackageReference"));
    }

    [Fact]
    public void ClientCoreOnlyReferencesContractsAndServerApplicationAvoidsNetworkFrameworks()
    {
        var root = FindRepositoryRoot();
        var client = XDocument.Load(Path.Combine(root, "src", "Eota.Client.Core", "Eota.Client.Core.csproj"));
        var clientReference = Assert.Single(client.Descendants("ProjectReference"));
        Assert.Contains("Eota.Transport.Contracts", clientReference.Attribute("Include")!.Value, StringComparison.Ordinal);
        Assert.Empty(client.Descendants("PackageReference"));
        Assert.Empty(client.Descendants("FrameworkReference"));

        var server = XDocument.Load(Path.Combine(root, "src", "Eota.Server.Application", "Eota.Server.Application.csproj"));
        Assert.Empty(server.Descendants("PackageReference"));
        Assert.Empty(server.Descendants("FrameworkReference"));
        Assert.All(server.Descendants("ProjectReference"), reference =>
            Assert.Contains(reference.Attribute("Include")!.Value,
                AllowedApplicationReferences));
        foreach (var source in Directory.EnumerateFiles(Path.Combine(root, "src", "Eota.Server.Application"), "*.cs", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("using Microsoft.AspNetCore", File.ReadAllText(source), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void VnextProjectsDoNotReferencePrototypeOrGodot()
    {
        var root = FindRepositoryRoot();
        var projectFiles = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "tools"), "*.csproj", SearchOption.AllDirectories));

        foreach (var projectFile in projectFiles)
        {
            var text = File.ReadAllText(projectFile);
            Assert.DoesNotContain("Prototype", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Godot", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void KernelSourcesDoNotImportForbiddenFrameworks()
    {
        var root = FindRepositoryRoot();
        var sourceFiles = Directory.EnumerateFiles(
            Path.Combine(root, "src", "Eota.Kernel"),
            "*.cs",
            SearchOption.AllDirectories);
        string[] forbidden = ["using Godot", "using Microsoft.AspNetCore", "using Microsoft.EntityFrameworkCore"];

        foreach (var sourceFile in sourceFiles)
        {
            var text = File.ReadAllText(sourceFile);
            foreach (var value in forbidden)
            {
                Assert.DoesNotContain(value, text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void GodotScenesUseDesktopAndContractsWithoutAuthorityOrPrototypeCode()
    {
        var root = FindRepositoryRoot();
        var project = XDocument.Load(Path.Combine(root, "Eota.Godot.csproj"));
        Assert.Equal("false", Assert.Single(project.Descendants("EnableDefaultCompileItems")).Value);
        Assert.Equal("Client/Godot/**/*.cs", Assert.Single(project.Descendants("Compile")).Attribute("Include")!.Value);
        Assert.Contains("Eota.Client.Desktop", Assert.Single(project.Descendants("ProjectReference")).Attribute("Include")!.Value, StringComparison.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "Client", "Godot"), "*.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Eota.Kernel", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Eota.Server", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Prototype/", text, StringComparison.Ordinal);
        }
    }

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
