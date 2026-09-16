using Eota.Transport.Contracts;
using Godot;

namespace Eota.GodotClient;

public partial class GameApp
{
    private async Task SceneLifetimeSmokeAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var collector = Task.Run(() =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                Thread.Sleep(10);
            }
        });
        var created = 0;
        Exception? failure = null;
        try
        {
            var cards = new[] { _catalog.Cards.First(card => card.Kind == "Minion"), _catalog.Cards.First(card => card.Kind == "Field") };
            for (var iteration = 0; iteration < 200; iteration++)
            {
                foreach (var card in cards)
                {
                    var view = new CardView(1, card.Id, card.Kind, card.Cost, card.Attack, card.Health, []);
                    Check(CardTile.CreatePrototype(card));
                    Check(CardTile.CreatePrototype(card, detail: true));
                    Check(CardTile.CreateThumbnail(card));
                    Check(CardTile.CreateInstance(card, view, compact: true));
                }
                if (iteration % 10 == 0) { await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
            }
            void Check(CardTile tile)
            {
                try { Require(tile.GetNodeOrNull<Panel>("Outline") is not null, "Card scene retains its nested outline"); created++; }
                finally { tile.Free(); }
            }
        }
        catch (Exception error) { failure = error; }
        finally { cancellation.Cancel(); await collector; }
        if (failure is not null) { GD.PushError(failure.ToString()); GetTree().Quit(1); return; }
        GD.Print($"SCENE_LIFETIME_SMOKE_OK instances={created} concurrent-gc=verified");
        GetTree().Quit();
    }
}
