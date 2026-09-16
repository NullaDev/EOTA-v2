using System.Collections.Immutable;
using Eota.Client.Desktop;
using Eota.Transport.Contracts;
using Godot;

namespace Eota.GodotClient;

public partial class SpellPlans : Control
{
    private ImmutableArray<PlanView> _plans = [];
    [Export] public Texture2D FastTexture { get; set; } = null!;
    [Export] public Texture2D SlowTexture { get; set; } = null!;
    public int FastCount => _plans.Count(plan => plan.ActionKind == "FastSpell");
    public int SlowCount => _plans.Count(plan => plan.ActionKind == "SlowSpell");
    public Func<CardView, CardPresentation>? Presentation { get; set; }
    public Action<CardPresentation>? Inspected { get; set; }
    public Action? InspectionEnded { get; set; }
    public Action<ulong>? CancelRequested { get; set; }

    public void Bind(ImmutableArray<PlanView> plans, bool canCancel)
    {
        var row = GetNode<HBoxContainer>("Scroll/Icons");
        if (!_plans.SequenceEqual(plans))
        {
            InspectionEnded?.Invoke(); Ui.Clear(row); _plans = plans;
            foreach (var plan in plans)
            {
                var button = Ui.Instantiate<TextureButton>("SpellPlanIcon");
                button.Name = "Plan" + plan.PlanCommandId;
                button.TextureNormal = plan.ActionKind == "FastSpell" ? FastTexture : SlowTexture;
                row.AddChild(button);
                button.MouseEntered += () => { if (Presentation is not null) { Inspected?.Invoke(Presentation(plan.Card)); } };
                button.MouseExited += () => InspectionEnded?.Invoke();
                button.Pressed += () => { InspectionEnded?.Invoke(); CancelRequested?.Invoke(plan.PlanCommandId); };
            }
        }
        foreach (var plan in plans)
        {
            var button = row.GetNode<TextureButton>("Plan" + plan.PlanCommandId);
            button.Disabled = !canCancel;
            button.TooltipText = (Presentation?.Invoke(plan.Card).Name ?? plan.Card.PrototypeId)
                + "\n" + (plan.ActionKind == "FastSpell" ? "快速法术" : "慢速法术")
                + (canCancel ? " · 点击撤回" : " · 已提交");
        }
    }
}
