using Eota.Transport.Contracts;

namespace Eota.Client.Desktop;

public sealed partial class DesktopSession
{
    public async Task SaveDiagnosticsAsync(string path, CancellationToken cancellationToken = default)
    {
        var client = Client; SupportDiagnostics? server = null; string? failure = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try { server = await client.GetSupportDiagnosticsAsync(timeout.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        { failure = error.GetType().Name; }
        cancellationToken.ThrowIfCancellationRequested();
        var state = client.Store.State;
        var report = new
        {
            version = 1, audience = state.View?.Audience, state.MatchRevision, state.ServerSequence, state.ObserverViewHash, state.NeedsSnapshot,
            state.View?.RuleContentHash, state.View?.ProtocolHash, state.View?.Status, state.View?.Stage, state.View?.Turn,
            connectionState = ConnectionStatus.State, connectionGeneration = ConnectionGeneration,
            client.TransportFailureCode, serverRequestFailure = failure, failures = client.Store.CaptureFailures(), server
        };
        DesktopContentEditor.WriteAtomic(path, ContractJson.Serialize(report));
    }
}
