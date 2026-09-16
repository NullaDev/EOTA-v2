using Eota.Client.Core;
using Eota.Client.AI;
using Eota.Client.Transport.InProcess;
using Eota.Client.Transport.WebSocket;
using Eota.Server.Application;
using Eota.Transport.Contracts;

namespace Eota.Client.Desktop;

public sealed partial class DesktopSession : IAsyncDisposable
{
    private readonly MatchActor? _actor;
    private readonly GameClient[] _clients;
    private int _seat;
    private Eota.Kernel.Matches.MatchCreationRequest? _request;
    private DesktopDeck? _one;
    private DesktopDeck? _two;
    private LocalMatchSettings? _settings;
    private AiPlayer? _ai;
    private int _disposed;
    public GameClient Client => Volatile.Read(ref _clients[_seat]);
    public bool IsLocal => _actor is not null;
    public bool CanSwitchSeat => IsLocal && _ai is null;
    public DesktopAiSettings? AiSettings => _settings?.Ai;
    public AiPlayerStatus? AiStatus => _ai?.Status;
    public string? FinalStateHash { get; private set; }

    private DesktopSession(MatchActor? actor, GameClient[] clients) { _actor = actor; _clients = clients; }
    public static async Task<DesktopSession> JoinRoomAsync(DesktopRoomClient room, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(room);
        var description = room.Description ?? throw new InvalidOperationException("请先核对房间协议。");
        var owned = new DesktopRoomClient(room.Invitation);
        try
        {
            var inspected = await owned.InspectAsync(token).ConfigureAwait(false);
            if (inspected.RuleContentHash != description.RuleContentHash || inspected.ProtocolHash != description.ProtocolHash || inspected.RoomSettingsHash != description.RoomSettingsHash)
            { throw new InvalidDataException("房间协议或时限已改变，请重新核对。"); }
            var session = new DesktopSession(null, [await owned.ConnectAsync(token).ConfigureAwait(false)])
            { _remoteRoom = owned, _remoteRuleHash = description.RuleContentHash, _remoteProtocolHash = description.ProtocolHash,
                _remoteSettingsHash = description.RoomSettingsHash };
            session.StartRemoteMonitor(); return session;
        }
        catch { owned.Dispose(); throw; }
    }

    public static async Task<DesktopSession> LocalAsync(DesktopCatalog catalog, DesktopDeck one, DesktopDeck two, LocalMatchSettings settings)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var request = catalog.CreateRequest(one, two, settings);
        var actor = new MatchActor("local", request);
        var clients = new List<GameClient>();
        try
        {
            foreach (var seat in new[] { Audience.PlayerOne, Audience.PlayerTwo })
            {
                var client = new GameClient(await InProcessGameTransport.ConnectAsync(actor, seat).ConfigureAwait(false));
                clients.Add(client); await client.SynchronizeAsync().ConfigureAwait(false);
            }
            var session = new DesktopSession(actor, clients.ToArray()) { _request = request, _one = one, _two = two, _settings = settings };
            if (settings.Ai is { } ai)
            {
                if (ai.PolicyVersion != AiPolicy.Version || !Enum.TryParse<AiDifficulty>(ai.Difficulty, out var difficulty) || !Enum.IsDefined(difficulty))
                { throw new ArgumentException("Unsupported AI policy or difficulty.", nameof(settings)); }
                session._ai = new AiPlayer(clients[1], new AiPolicy(difficulty, ai.Seed, catalog.AiCards));
            }
            return session;
        }
        catch
        {
            foreach (var client in clients) { await client.DisposeAsync().ConfigureAwait(false); }
            await actor.DisposeAsync().ConfigureAwait(false); throw;
        }
    }

    public static async Task<DesktopSession> RemoteAsync(Uri endpoint, string matchId, string ruleContentHash)
    {
        var client = new GameClient(await WebSocketGameTransport.ConnectAsync(endpoint, matchId, ruleContentHash).ConfigureAwait(false));
        try
        {
            await client.SynchronizeAsync().ConfigureAwait(false);
            if (client.Store.View?.RuleContentHash != ruleContentHash) { throw new InvalidDataException("rule-content-mismatch"); }
            return new DesktopSession(null, [client]);
        }
        catch { await client.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public void SwitchSeat()
    {
        if (!CanSwitchSeat) { throw new InvalidOperationException("This match has a fixed player seat."); }
        _seat = 1 - _seat;
    }

    public void DiscardInactiveFrames()
    {
        for (var index = 0; index < _clients.Length; index++)
        { if (index != _seat) { _clients[index].Store.DrainPresentationFrames(); } }
    }

    public async Task SaveReplayAsync(string directory)
    {
        if (_actor is null) { throw new InvalidOperationException("Only local matches can export a complete authoritative command log."); }
        var capture = await _actor.CaptureReplayAsync().ConfigureAwait(false);
        FinalStateHash = capture.StateHash;
        DesktopReplay.Save(directory, _request!, _settings!, _one!, _two!, capture.Commands, capture.StateHash);
    }

    public async Task<CommandAckPayload> SubmitAsync(ClientPayload payload)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var client = Client;
        var own = client.Store.View?.Private ?? throw new InvalidOperationException("This connection is read only.");
        var key = Guid.NewGuid().ToString("N");
        CommandAckPayload ack;
        try { ack = await client.SubmitAsync(payload, own.CommandRevision, key, _remoteLifetime.Token).ConfigureAwait(false); }
        catch (IOException) when (_remoteRoom is not null && Volatile.Read(ref _disposed) == 0)
        {
            await ReconnectCoreAsync(client, _remoteLifetime.Token).ConfigureAwait(false);
            // Retry the same command ID and expected revision. A lost ACK must never duplicate a play.
            ack = await Client.SubmitAsync(payload, own.CommandRevision, key, _remoteLifetime.Token).ConfigureAwait(false);
        }
        if (_actor is not null) { FinalStateHash = (await _actor.GetDiagnosticsAsync().ConfigureAwait(false)).StateHash; }
        return ack;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        await _remoteLifetime.CancelAsync().ConfigureAwait(false);
        if (_remoteMonitor is not null) { await _remoteMonitor.ConfigureAwait(false); }
        await _reconnectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_ai is not null) { await _ai.DisposeAsync().ConfigureAwait(false); }
            foreach (var client in _clients) { await client.DisposeAsync().ConfigureAwait(false); }
            if (_actor is not null) { await _actor.DisposeAsync().ConfigureAwait(false); }
            _remoteRoom?.Dispose(); _remoteLifetime.Dispose();
        }
        finally { _reconnectGate.Release(); }
    }

    public async Task<DesktopSession> RestartAsync(DesktopCatalog catalog)
    {
        if (!IsLocal) { throw new InvalidOperationException("Only local matches can restart."); }
        await DisposeAsync().ConfigureAwait(false);
        return await LocalAsync(catalog, _one!, _two!, _settings!).ConfigureAwait(false);
    }
}
