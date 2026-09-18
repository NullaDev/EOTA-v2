using System.Collections.Immutable;
using Eota.Client.Desktop;
using Eota.Transport.Contracts;
using Godot;

namespace Eota.GodotClient;

public partial class GameApp : Control
{
    private DesktopCatalog _catalog = null!;
    private DesktopSession? _session;
    private VBoxContainer _root = null!;
    private Label _status = null!;
    private bool _busy;
    private readonly List<DesktopDeck> _decks = [];
    private readonly Dictionary<DesktopDeck, string> _deckFiles = [];
    private DesktopDeckStore _deckStore = null!;
    private readonly Dictionary<ulong, CardTile> _registry = [];
    private readonly Dictionary<(int, int, string), Control> _slots = [];
    private readonly Dictionary<int, HBoxContainer> _laneStates = [];
    private readonly Dictionary<(int Lane, int Player), EtherPips> _etherPips = [];
    private readonly List<LaneDrop> _drops = [];
    private HBoxContainer _hand = null!;
    private HBoxContainer _plans = null!;
    private VBoxContainer _detail = null!;
    private Label _matchInfo = null!;
    private HeroPanel[] _heroes = [];
    private Button _submit = null!;
    private ulong? _selected;
    private readonly HashSet<ulong> _mulligan = [];
    private readonly Dictionary<ulong, ulong> _handOrder = [];
    private ulong _handOrderNext;
    private readonly Queue<PresentationFramePayload> _frames = [];
    private ObserverView? _displayed;
    private ulong _revision;
    private bool _paused;
    private double _wait;
    private float _speed = 1;
    private bool _battle;
    private bool _syncing;

    public override void _Ready()
    {
        RenderingServer.SetDefaultClearColor(Ui.Background);
        _root = GetNode<VBoxContainer>("ScreenHost");
        GetTree().AutoAcceptQuit = false;
        try
        {
            // Exported PCK resources have no physical res:// root. Loose content
            // and the managed server are distributed next to the executable.
            var contentRoot = OS.HasFeature("editor") ? ProjectSettings.GlobalizePath("res://")
                : Path.GetDirectoryName(OS.GetExecutablePath())!;
            _catalog = new DesktopCatalog(contentRoot);
            CardTile.UseCatalog(_catalog);
            _serverLauncher = new LocalServerLauncher(_catalog.Root, ProjectSettings.GlobalizePath("user://rooms"));
            var contentSelection = ProjectSettings.GlobalizePath("user://content-selection.txt");
            if (File.Exists(contentSelection) && File.ReadAllText(contentSelection).Trim() is { Length: > 0 } packPath)
            {
                try { _catalog = new DesktopCatalog(_catalog.Root, packPath); CardTile.UseCatalog(_catalog); }
                catch (Exception error) { GD.PushWarning("自定义内容包未启用：" + error.Message); }
            }
            var protocolFile = ProjectSettings.GlobalizePath("user://protocol.json");
            if (File.Exists(protocolFile))
            { try { _protocol = DesktopProtocol.Parse(File.ReadAllText(protocolFile)); } catch (InvalidDataException) { GD.PushWarning("已保存的对战协议无效，使用默认值。"); } }
            foreach (var profession in Professions) { _decks.Add(_catalog.DefaultDeck(profession, _protocol)); }
            _deckStore = new DesktopDeckStore(ProjectSettings.GlobalizePath("user://decks"));
            foreach (var id in _deckStore.Ids)
            { try { var deck = _deckStore.Read(id); _decks.Add(deck); _deckFiles.Add(deck, id); } catch (Exception) { GD.PushWarning("无法载入牌组：" + id); } }
            BuildMenu();
            if (OS.GetCmdlineUserArgs().Contains("--smoke")) { _ = SmokeAsync(); }
            if (OS.GetCmdlineUserArgs().Contains("--smoke-remote")) { _ = SmokeRemoteAsync(); }
            if (OS.GetCmdlineUserArgs().Contains("--smoke-ui-review")) { _ = UiReviewSmokeAsync(); }
            if (OS.GetCmdlineUserArgs().Contains("--smoke-ai")) { _ = AiSmokeAsync(); }
            if (OS.GetCmdlineUserArgs().Contains("--smoke-scene-lifetime")) { _ = SceneLifetimeSmokeAsync(); }
            if (OS.GetCmdlineUserArgs().Contains("--smoke-interactions")) { _ = InteractionSmokeAsync(); }
            if (OS.GetCmdlineUserArgs().Contains("--smoke-presentation")) { _ = PresentationSmokeAsync(); }
            if (OS.GetCmdlineUserArgs().Contains("--smoke-card-status")) { _ = CardStatusSmokeAsync(); }
            if (OS.GetCmdlineUserArgs().Contains("--smoke-p10-editor")) { _ = ContentEditorSmokeAsync(); }
            if (OS.GetCmdlineUserArgs().Contains("--smoke-p10-decks")) { _ = DeckProtocolSmokeAsync(); }
            if (OS.GetCmdlineUserArgs().Contains("--smoke-p10-host")) { _ = HostedMatchSmokeAsync(true); }
            if (OS.GetCmdlineUserArgs().Contains("--smoke-p10-join")) { _ = HostedMatchSmokeAsync(false); }
        }
        catch (Exception error) { _root.AddChild(Ui.Text("内容载入失败：" + error.Message)); GD.PushError(error.ToString()); }
    }

    private async Task CloseSession()
    {
        // Stop frame processing before disposal yields to the next Godot frame.
        var session = _session; _session = null; _battle = false;
        if (GodotObject.IsInstanceValid(_animations)) { _animations.Clear(); }
        _frames.Clear(); _displayed = null; _selected = null; _mulligan.Clear();
        _handOrder.Clear(); _handOrderNext = 0;
        _replay = null; _replaySlider = null;
        if (session is not null) { await session.DisposeAsync(); }
    }

    private void Run(Func<Task> action)
    {
        if (_busy) { return; }
        _ = Execute();
        async Task Execute()
        {
            _busy = true;
            try { await action(); }
            catch (Exception error) { if (GodotObject.IsInstanceValid(_status)) { _status.Text = Ui.Reason(error.Message); } GD.PushWarning(error.ToString()); }
            finally { _busy = false; }
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest)
        {
            _roomWait?.Cancel();
            _ = ShutdownAsync();
        }
    }

    private bool _quitting;
    private async Task ShutdownAsync()
    {
        if (_quitting) { return; } _quitting = true;
        await CloseSession(); if (_serverLauncher is not null) { await _serverLauncher.DisposeAsync(); }
        GetTree().Quit();
    }

    public override void _ExitTree() => Ui.ReleaseResources();
}
