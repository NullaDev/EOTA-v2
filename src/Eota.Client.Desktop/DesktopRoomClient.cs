using System.Net.Http.Headers;
using System.Text;
using Eota.Client.Core;
using Eota.Client.Transport.WebSocket;
using Eota.Server.Infrastructure;
using Eota.Transport.Contracts;

namespace Eota.Client.Desktop;

public sealed class DesktopRoomClient : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 256 * 1024 };
    private readonly string _credential;
    public Uri Endpoint { get; }
    public Uri Invitation { get; }
    public string MatchId { get; }
    public bool IsSpectator => Endpoint.Query == "?audience=spectator";
    public RoomDescription? Description { get; private set; }

    public DesktopRoomClient(Uri invitation)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        Invitation = invitation;
        var parts = invitation.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (invitation.Scheme is not ("ws" or "wss") || parts.Length != 3 || parts[0] != "matches" || parts[2] != "ws"
            || invitation.Query is not ("?audience=playerOne" or "?audience=playerTwo" or "?audience=spectator")
            || !invitation.Fragment.StartsWith("#token=", StringComparison.Ordinal) || !MatchHandshake.IsRuleContentHash(invitation.Fragment[7..]))
        { throw new InvalidDataException("请粘贴房主提供的完整邀请地址（包含席位凭据）。"); }
        _credential = invitation.Fragment[7..]; MatchId = parts[1]; Endpoint = new UriBuilder(invitation) { Fragment = "" }.Uri;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _credential);
    }

    private Uri Address(string action) => new UriBuilder(Endpoint) { Scheme = Endpoint.Scheme == "wss" ? "https" : "http", Path = $"/matches/{MatchId}/{action}" }.Uri;

    public async Task<RoomDescription> InspectAsync(CancellationToken token = default)
    {
        using var response = await _http.GetAsync(Address("room"), token).ConfigureAwait(false);
        var next = await ReadAsync(response, token).ConfigureAwait(false);
        ValidateDescription(next); Description = next;
        return Description;
    }

    private void ValidateDescription(RoomDescription next)
    {
        if (next.Version != 1 || next.MatchId != MatchId) { throw new InvalidDataException("房间版本或标识不兼容。"); }
        if (!MatchHandshake.IsRuleContentHash(next.RuleContentHash)) { throw new InvalidDataException("房间规则指纹无效。"); }
        if (DesktopProtocol.Compile(next.ProtocolJson).Hash.ToString() != next.ProtocolHash) { throw new InvalidDataException("房间协议校验失败。"); }
        if (next.Timing is null || next.Timing.ComputeHash(next.RuleContentHash, next.ProtocolHash) != next.RoomSettingsHash)
        { throw new InvalidDataException("房间时限设置校验失败。"); }
        if (Description is { } previous && (previous.RuleContentHash != next.RuleContentHash || previous.ProtocolHash != next.ProtocolHash
            || previous.RoomSettingsHash != next.RoomSettingsHash))
        { throw new InvalidDataException("房间协议或时限已改变，请重新核对房间。"); }
    }

    public async Task<RoomDescription> ReadyAsync(DesktopDeck deck, DesktopCatalog catalog, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(deck);
        ArgumentNullException.ThrowIfNull(catalog);
        var description = Description ?? throw new InvalidOperationException("请先核对房间协议。");
        var admission = new RoomAdmission(1, catalog.RuleHash, description.ProtocolHash, ToRoomDeck(deck), description.RoomSettingsHash, catalog.CardRules);
        using var content = new StringContent(ContractJson.Serialize(admission), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(Address("ready"), content, token).ConfigureAwait(false);
        var next = await ReadAsync(response, token).ConfigureAwait(false); ValidateDescription(next);
        if (next.OwnDeckHash != HostedRoom.DeckHash(admission.Deck)) { throw new InvalidDataException("服务器确认的牌组与提交内容不一致。"); }
        Description = next;
        return Description;
    }

    public async Task<GameClient> ConnectAsync(CancellationToken token = default)
    {
        var description = Description ?? throw new InvalidOperationException("请先核对房间协议。");
        var client = new GameClient(await WebSocketGameTransport.ConnectAuthenticatedAsync(Endpoint, MatchId, description.RuleContentHash,
            description.ProtocolHash, _credential, description.RoomSettingsHash, token).ConfigureAwait(false));
        try
        {
            await client.SynchronizeAsync(token).ConfigureAwait(false);
            if (client.Store.View?.RuleContentHash != description.RuleContentHash || client.Store.View.ProtocolHash != description.ProtocolHash)
            { throw new InvalidDataException("对战快照与确认的房间不一致。"); }
            return client;
        }
        catch { await client.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public static RoomDeck ToRoomDeck(DesktopDeck deck) => new(deck.Profession, [.. deck.Cards.Select(c => new RoomDeckCard(c.Id, c.Copies))]);

    private static async Task<RoomDescription> ReadAsync(HttpResponseMessage response, CancellationToken token)
    {
        var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if ((int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        { throw new HttpRequestException($"server-temporarily-unavailable ({(int)response.StatusCode})", null, response.StatusCode); }
        if (!response.IsSuccessStatusCode)
        { throw new InvalidDataException(response.StatusCode == System.Net.HttpStatusCode.Unauthorized ? "席位凭据无效，请重新复制邀请地址。" : $"房间校验失败 ({(int)response.StatusCode})：{text}"); }
        return ContractJson.Deserialize<RoomDescription>(text);
    }

    public void Dispose() => _http.Dispose();
}
