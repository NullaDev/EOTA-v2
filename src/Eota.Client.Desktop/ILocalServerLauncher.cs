namespace Eota.Client.Desktop;

public sealed record LocalServerOptions(string MatchName, int Port, bool AllowLan, DesktopCatalog? Catalog = null,
    DesktopDeck? PlayerOneDeck = null, DesktopDeck? PlayerTwoDeck = null, string? ProtocolJson = null, ulong Seed = 146, bool Resume = false,
    Eota.Transport.Contracts.RoomTiming? Timing = null);
public sealed record LocalServerStartResult(bool Started, string Message, Uri? Endpoint = null, Uri? PlayerTwoEndpoint = null,
    Uri? SpectatorEndpoint = null, string? MatchId = null, string? Directory = null);
public enum LocalServerState { Stopped, Starting, Joinable, Stopping, Failed }

public interface ILocalServerLauncher : IAsyncDisposable
{
    bool IsAvailable { get; }
    LocalServerState State { get; }
    string StatusMessage { get; }
    LocalServerStartResult? Room { get; }
    Task<LocalServerStartResult> StartAsync(LocalServerOptions options, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
