using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eota.Server.Infrastructure;

namespace Eota.Client.Desktop;

public sealed class LocalServerLauncher(string applicationRoot, string roomsDirectory) : ILocalServerLauncher
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private LocalServerState _state;
    private string _message = "服务器已停止";
    private string Executable => Path.Combine(applicationRoot, "Server", "win-x64", "Eota.Server.Host.exe");
    public bool IsAvailable => File.Exists(Executable);
    public LocalServerState State
    {
        get
        {
            if (_state == LocalServerState.Joinable && _process is { HasExited: true })
            { _state = LocalServerState.Failed; _message = "服务器意外退出。存档已保留，可重试启动。"; }
            return _state;
        }
    }
    public string StatusMessage { get { _ = State; return _message; } }
    public LocalServerStartResult? Room { get; private set; } = LoadLastRoom(roomsDirectory);

    private static LocalServerStartResult? LoadLastRoom(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "last-room.json"); if (!File.Exists(path)) { return null; }
            var room = JsonSerializer.Deserialize<LocalServerStartResult>(File.ReadAllText(path));
            if (room is null || string.IsNullOrWhiteSpace(room.MatchId) || new[] { room.Endpoint, room.PlayerTwoEndpoint, room.SpectatorEndpoint }
                .Any(uri => uri is null || !uri.Fragment.StartsWith("#token=", StringComparison.Ordinal)
                    || !Eota.Transport.Contracts.MatchHandshake.IsRuleContentHash(uri.Fragment[7..]))) { return null; }
            return room?.Directory is { } saved && Path.GetFullPath(saved).StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(saved, "room.json")) ? room : null;
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException) { return null; }
    }

    public async Task<LocalServerStartResult> StartAsync(LocalServerOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == LocalServerState.Joinable) { return Room!; }
            await StopProcessAsync().ConfigureAwait(false);
            _state = LocalServerState.Starting; _message = "正在启动服务器…";
            if (!IsAvailable) { throw new InvalidDataException("缺少随游戏分发的服务器程序。"); }
            if (options.MatchName is not { Length: > 0 and <= 80 } || options.Port is < 1024 or > 65535
                || options.Catalog is null)
            { throw new InvalidDataException("请填写房间名、1024–65535 端口并选择内容包。"); }
            if (options.Resume && Room?.Directory is null) { throw new InvalidDataException("没有可恢复的房间。"); }
            var id = options.Resume ? Room!.MatchId! : "room-" + Guid.NewGuid().ToString("N")[..12];
            var directory = options.Resume ? Room!.Directory! : Path.Combine(roomsDirectory, id); Directory.CreateDirectory(directory);
            var tokens = options.Resume ? new[] { Room!.Endpoint!, Room.PlayerTwoEndpoint!, Room.SpectatorEndpoint! }.Select(uri => uri.Fragment[7..]).ToArray()
                : Enumerable.Range(0, 3).Select(_ => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()).ToArray();
            var configPath = Path.Combine(directory, "room.json");
            if (!options.Resume)
            {
                var protocol = DesktopProtocol.Parse(options.ProtocolJson ?? DesktopProtocol.Default.Json);
                foreach (var deck in new[] { options.PlayerOneDeck, options.PlayerTwoDeck }.OfType<DesktopDeck>())
                {
                    var deckErrors = options.Catalog.ValidateDeck(deck, protocol);
                    if (!deckErrors.IsEmpty) { throw new InvalidDataException("参考牌组与当前协议不兼容：" + string.Join(", ", deckErrors)); }
                }
                var hashes = tokens.Select(value => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()).ToArray();
                var config = new HostedRoomConfiguration(1, id, options.MatchName, options.Seed, options.Catalog.RuleHash,
                    options.ProtocolJson ?? DesktopProtocol.Default.Json, options.Catalog.SourceCards, hashes[0], hashes[1], hashes[2],
                    options.PlayerOneDeck is null ? null : DesktopRoomClient.ToRoomDeck(options.PlayerOneDeck),
                    options.PlayerTwoDeck is null ? null : DesktopRoomClient.ToRoomDeck(options.PlayerTwoDeck), options.Timing);
                _ = (options.Timing ?? new Eota.Transport.Contracts.RoomTiming()).ComputeHash(options.Catalog.RuleHash, DesktopProtocol.Compile(protocol.Json).Hash.ToString());
                DesktopContentEditor.WriteAtomic(configPath, JsonSerializer.Serialize(config));
            }
            var start = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
            foreach (var value in new[] { "--room", configPath, "--urls", $"http://{(options.AllowLan ? "0.0.0.0" : "127.0.0.1")}:{options.Port}", "--managed", "true", "--Logging:LogLevel:Default", "Warning" })
            { start.ArgumentList.Add(value); }
            var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
            _process = new Process { StartInfo = start };
            void Capture(object sender, DataReceivedEventArgs args)
            { if (args.Data is { } data) { errors.Enqueue(data); while (errors.Count > 32) { errors.TryDequeue(out _); } } }
            _process.OutputDataReceived += Capture; _process.ErrorDataReceived += Capture;
            if (!_process.Start()) { throw new IOException("无法启动服务器进程。"); }
            _process.BeginOutputReadLine(); _process.BeginErrorReadLine();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(20));
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (_process.HasExited)
                { throw new IOException(errors.Any(line => line.Contains("address", StringComparison.OrdinalIgnoreCase) || line.Contains("10048", StringComparison.Ordinal))
                    ? "端口已被占用，请更换端口后重试。" : "服务器启动失败：" + string.Join(" ", errors.TakeLast(3))); }
                try
                {
                    var health = await http.GetFromJsonAsync<JsonElement>($"http://127.0.0.1:{options.Port}/health", timeout.Token).ConfigureAwait(false);
                    if (health.TryGetProperty("matchId", out var match) && match.GetString() == id) { break; }
                }
                catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException) { }
                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }
            var address = options.AllowLan ? LanAddress() : "127.0.0.1";
            Uri Invite(string role, int index) => new($"ws://{address}:{options.Port}/matches/{id}/ws?audience={role}#token={tokens[index]}");
            _state = LocalServerState.Joinable; _message = "房间已开放 · 等待双方确认牌组";
            Room = new(true, _message, Invite("playerOne", 0), Invite("playerTwo", 1), Invite("spectator", 2), id, directory);
            DesktopContentEditor.WriteAtomic(Path.Combine(roomsDirectory, "last-room.json"), JsonSerializer.Serialize(Room));
            return Room;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            await StopProcessAsync().ConfigureAwait(false); _state = LocalServerState.Failed;
            _message = error is OperationCanceledException ? "启动超时或已取消，可重试。" : error.Message;
            return new(false, _message);
        }
        finally { _gate.Release(); }
    }

    private static string LanAddress() => NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up
        && n.NetworkInterfaceType != NetworkInterfaceType.Loopback).SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a))?.ToString() ?? "127.0.0.1";

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { _state = LocalServerState.Stopping; _message = "正在关闭服务器…"; await StopProcessAsync().ConfigureAwait(false); _state = LocalServerState.Stopped; _message = "服务器已停止"; }
        finally { _gate.Release(); }
    }

    private async Task StopProcessAsync()
    {
        var process = _process; _process = null;
        if (process is null) { return; }
        try
        {
            if (!process.HasExited)
            {
                try { await process.StandardInput.WriteLineAsync("stop").ConfigureAwait(false); } catch (IOException) { }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { if (!process.HasExited) { process.Kill(entireProcessTree: true); } await process.WaitForExitAsync().ConfigureAwait(false); }
            }
        }
        finally { process.Dispose(); }
    }

    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); _gate.Dispose(); }
}
