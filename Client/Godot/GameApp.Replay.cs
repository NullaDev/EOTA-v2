using Eota.Client.Desktop;
using Godot;

namespace Eota.GodotClient;

public partial class GameApp
{
    private DesktopReplay? _replay;
    private int _replayIndex;
    private HSlider? _replaySlider;

    private void BuildReplayMenu(TabContainer tabs)
    {
        var page = tabs.GetNode<Control>("Replay");
        var path = page.GetNode<LineEdit>("Path");
        var directory = ProjectSettings.GlobalizePath("user://replays"); Directory.CreateDirectory(directory);
        var files = Directory.EnumerateFiles(directory, "replay.json", SearchOption.AllDirectories).OrderDescending().ToArray();
        var choices = page.GetNode<OptionButton>("Choices"); foreach (var file in files) { choices.AddItem(Path.GetFileName(Path.GetDirectoryName(file))); }
        choices.ItemSelected += index => path.Text = files[index];
        if (files.Length > 0) { path.Text = files[0]; }
        page.GetNode<Button>("Load").Pressed += () => Run(async () =>
        {
            var replay = await Task.Run(() => DesktopReplay.Load(_catalog, path.Text));
            await CloseSession(); _replay = replay; _replayIndex = 0; BuildBattle(replay.Initial);
            _replaySlider!.MaxValue = replay.Frames.Length; _replaySlider.Show();
            _replaySlider.ValueChanged += value =>
            {
                _replayIndex = (int)value; _wait = 0;
                _animations.Clear();
                Render(_replayIndex == 0 ? replay.Initial : _replayIndex == replay.Frames.Length ? replay.Final : replay.Frames[_replayIndex - 1].View);
            };
            _submit.Disabled = true; _status.Text = "回放已通过权威状态校验；拖动进度条可跳转。";
        });
    }

    private void ProcessReplay(double delta)
    {
        if (_replay is null) { return; }
        _submit.Disabled = true;
        if (_paused || Animating) { return; }
        _wait -= delta * _speed;
        if (_wait > 0) { return; }
        if (_replayIndex >= _replay.Frames.Length)
        {
            if (!ReferenceEquals(_displayed, _replay.Final)) { Render(_replay.Final); _status.Text = "回放结束，状态校验一致。"; }
            return;
        }
        Present(_replay.Frames[_replayIndex++]); _wait = 0.22;
        _replaySlider?.SetValueNoSignal(_replayIndex);
    }
}
