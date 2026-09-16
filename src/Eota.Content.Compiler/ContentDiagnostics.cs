using System.Collections.Immutable;
using Eota.Kernel.Content;

namespace Eota.Content.Compiler;

public enum ContentDiagnosticSeverity
{
    Error = 1,
    Warning = 2
}

public sealed record ContentDiagnostic(
    ContentDiagnosticSeverity Severity,
    string Code,
    string SourceName,
    string Path,
    string Message);

public sealed record ContentSourceDocument(string SourceName, string Json);

public sealed record ContentCompilationResult(
    CompiledContentBundle? Content,
    ImmutableArray<ContentDiagnostic> Diagnostics)
{
    public bool IsSuccess => Content is not null && Diagnostics.All(value => value.Severity != ContentDiagnosticSeverity.Error);
}
