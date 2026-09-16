using System.Collections.Immutable;
using Eota.Transport.Contracts;

namespace Eota.Client.AI;

public enum AiDifficulty { Easy, Normal, Hard }

// Public rule metadata only. The composition layer projects compiled card rules into these hints.
// Hints are heuristic estimates, not an alternate rules engine or a prediction of hidden plans.
public sealed record AiEffectHint(string Action, string Target, string Scope, double Amount,
    string Attribute = "", string Operation = "Add", double Weight = 1);
public sealed record AiCardProfile(string PrototypeId, ImmutableArray<AiEffectHint> Effects,
    bool PreventsAttacks = false, double FieldLifetime = 0);
public sealed record AiDecision(ClientPayload? Command, int Evaluations);

public sealed class AiPolicy
{
    public const string Version = "eota-ai/1";
    public const int MaximumEvaluations = 2048;
    private readonly AiDifficulty _difficulty;
    private readonly ulong _seed;
    private readonly ImmutableDictionary<string, AiCardProfile> _profiles;

    public AiPolicy(AiDifficulty difficulty, ulong seed, ImmutableArray<AiCardProfile> profiles)
    {
        if (!Enum.IsDefined(difficulty)) { throw new ArgumentOutOfRangeException(nameof(difficulty)); }
        _difficulty = difficulty; _seed = seed;
        _profiles = profiles.ToImmutableDictionary(value => value.PrototypeId, StringComparer.Ordinal);
    }

    public AiDecision Decide(ObserverView view, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(view);
        cancellationToken.ThrowIfCancellationRequested();
        var own = view.Private;
        if (own is null || view.Status != "Active" || view.Players.Single(player => player.PlayerId == own.PlayerId).Submitted)
        { return new(null, 0); }
        if (view.Stage == "Mulligan") { return new(new SubmitMulliganPayload(Mulligan(view)), 0); }
        if (view.Stage != "Planning") { return new(null, 0); }

        // Own pending placements are visible to this seat and consume real slots/resources. Include
        // them in estimates so later actions do not repeatedly replace a just-planned minion.
        foreach (var plan in own.Planning)
        { view = ProjectPlacement(view, new Candidate(plan.Card, plan.LaneId, 0)); }

        var cards = own.Hand.ToDictionary(card => card.CardInstanceId);
        var candidates = own.PlanOptions.Where(option => option.Allowed && cards.ContainsKey(option.CardInstanceId)
                && !own.Planning.Any(plan => plan.Card.CardInstanceId == option.CardInstanceId))
            .OrderBy(option => option.CardInstanceId).ThenBy(option => option.LaneId).Take(512)
            .Select(option => new Candidate(cards[option.CardInstanceId], option.LaneId,
                Tie(option.CardInstanceId, option.LaneId, view.Turn))).ToArray();
        if (candidates.Length == 0) { return new(new SubmitTurnPayload(), 0); }
        if (_difficulty == AiDifficulty.Easy)
        { return new(Payload(candidates.MinBy(candidate => candidate.Tie)!), candidates.Length); }

        var evaluations = 0;
        double Evaluate(ObserverView board, Candidate candidate)
        {
            cancellationToken.ThrowIfCancellationRequested(); evaluations++;
            return Score(board, candidate);
        }
        var ranked = candidates.Select(candidate => (Candidate: candidate, Score: Evaluate(view, candidate)))
            .OrderByDescending(value => value.Score).ThenBy(value => value.Candidate.Tie).ToArray();
        var best = ranked[0].Candidate;
        var bestScore = ranked[0].Score;
        if (_difficulty == AiDifficulty.Hard)
        {
            // Search at most three compatible plays. Keep several lanes per card so a prolific card
            // does not crowd cheaper combinations out of the beam. All work has a fixed step budget.
            var pool = ranked.GroupBy(value => value.Candidate.Card.CardInstanceId)
                .SelectMany(group => group.Take(3)).OrderByDescending(value => value.Score)
                .ThenBy(value => value.Candidate.Tie).Take(64).Select(value => value.Candidate).ToArray();
            var beam = new[] { new SearchNode(view, [], 0, 0) };
            for (var depth = 0; depth < 3 && evaluations < MaximumEvaluations; depth++)
            {
                var next = new List<SearchNode>();
                foreach (var node in beam)
                {
                    foreach (var candidate in pool)
                    {
                        if (evaluations >= MaximumEvaluations) { break; }
                        if (candidate.Card.Cost > own.AvailableCost - node.Cost || node.Picks.Any(pick =>
                                pick.Card.CardInstanceId == candidate.Card.CardInstanceId ||
                                (pick.Card.CardKind != "Spell" && pick.Card.CardKind == candidate.Card.CardKind && pick.Lane == candidate.Lane)))
                        { continue; }
                        var score = Evaluate(node.View, candidate);
                        // Repeated damage/buffs on an unchanged target are uncertain; do not count them twice at full value.
                        if (candidate.Card.CardKind == "Spell" && node.Picks.Any(pick => pick.Card.CardKind == "Spell" && pick.Lane == candidate.Lane))
                        { score *= 0.45; }
                        if (score <= 0) { continue; }
                        var picks = node.Picks.Add(candidate);
                        var total = node.Score + score;
                        if (total > bestScore) { bestScore = total; best = picks[0]; }
                        next.Add(new SearchNode(ProjectPlacement(node.View, candidate), picks, node.Cost + candidate.Card.Cost, total));
                    }
                }
                beam = next.OrderByDescending(node => node.Score).ThenBy(node => node.Picks[0].Tie).Take(8).ToArray();
                if (beam.Length == 0) { break; }
            }
        }
        return new(bestScore > 0 ? Payload(best) : new SubmitTurnPayload(), evaluations);
    }

    private ImmutableArray<ulong> Mulligan(ObserverView view)
    {
        if (_difficulty == AiDifficulty.Easy) { return []; }
        var hand = view.Private!.Hand;
        var keep = hand.Where(card => card.Cost <= 2).OrderBy(card => card.Cost)
            .ThenBy(card => card.CardKind == "Minion" ? 0 : 1).ThenBy(card => card.CardInstanceId).ToArray();
        return hand.Where(card => card.Cost > 3 || (_difficulty == AiDifficulty.Hard &&
                ((card.Cost == 3 && keep.Length == 0) || (card.CardKind == "Spell" && keep.Count(value => value.CardKind == "Spell") > 1
                    && keep.First(value => value.CardKind == "Spell").CardInstanceId != card.CardInstanceId))))
            .Select(card => card.CardInstanceId).Order().ToImmutableArray();
    }

    private double Score(ObserverView view, Candidate candidate)
    {
        var card = candidate.Card; var own = view.Private!.PlayerId;
        var lane = view.Lanes.FirstOrDefault(value => value.LaneId == candidate.Lane);
        var friend = view.Entities.FirstOrDefault(entity => entity.ControllerId == own && entity.LaneId == candidate.Lane && entity.Card.CardKind == "Minion");
        var enemy = view.Entities.FirstOrDefault(entity => entity.ControllerId != own && entity.LaneId == candidate.Lane && entity.Card.CardKind == "Minion");
        var profile = _profiles.GetValueOrDefault(card.PrototypeId);
        var score = -0.35 * card.Cost;
        if (card.CardKind == "Minion")
        {
            var attack = (double)(card.Attack ?? 0); var health = (double)(card.MaximumHealth ?? 0);
            score += attack * 1.5 + health * 0.85 + 1.5;
            if (friend is not null) { score -= (friend.Attack ?? 0) * 1.5 + (friend.CurrentHealth ?? 0) * 0.85 + 2; }
            if (enemy is not null)
            {
                var threat = (double)(enemy.Attack ?? 0);
                var hero = view.Players.Single(player => player.PlayerId == own);
                if (friend is null) { score += Math.Min(threat, health) * (hero.HeroHealth <= threat * 2 ? 2.8 : 0.9); }
                if (attack >= (enemy.CurrentHealth ?? 0)) { score += 2 + threat * 0.4; }
                if (health > threat) { score += 1.5; }
                if (Has(card.Keywords, "FirstStrike") && attack >= (enemy.CurrentHealth ?? 0)) { score += threat; }
            }
            else
            {
                var enemyHero = view.Players.Single(player => player.PlayerId != own);
                score += attack * 0.65;
                if (attack >= enemyHero.HeroHealth && Has(card.Keywords, "Swift")) { score += 80; }
            }
            if (Has(card.Keywords, "Slow")) { score -= attack * 0.55; }
            if (Has(card.Keywords, "Swift")) { score += attack * 0.45; }
            if (Has(card.Keywords, "Lifesteal"))
            { score += Math.Min(attack, view.Players.Single(player => player.PlayerId == own).HeroMaximumHealth - view.Players.Single(player => player.PlayerId == own).HeroHealth); }
            if (lane?.Frozen == true || view.Entities.Any(entity => entity.LaneId == candidate.Lane && entity.PreventsActiveAttacksInLane))
            { score -= attack * 1.3; }
        }
        else if (card.CardKind == "Field")
        {
            score += 1 + Math.Min(profile?.FieldLifetime ?? 1, 4) * 0.5;
            var previous = view.Entities.FirstOrDefault(entity => entity.ControllerId == own && entity.LaneId == candidate.Lane && entity.Card.CardKind == "Field");
            if (previous is not null) { score -= 2 + Math.Min(previous.FieldEnergy ?? 4, 4); }
            if (profile?.PreventsAttacks == true) { score += ((enemy?.Attack ?? 0) - (friend?.Attack ?? 0)) * 1.4; }
        }
        if (profile is not null)
        { foreach (var hint in profile.Effects) { score += EffectScore(view, candidate, hint) * hint.Weight; } }
        return score;
    }

    private static double EffectScore(ObserverView view, Candidate candidate, AiEffectHint hint)
    {
        var own = view.Private!.PlayerId;
        var friendly = hint.Target.StartsWith("Friendly", StringComparison.Ordinal) || hint.Target == "Self";
        var enemy = hint.Target.StartsWith("Enemy", StringComparison.Ordinal);
        var sign = enemy ? -1 : 1;
        var amount = Math.Clamp(hint.Amount, -100, 100);
        bool InScope(int lane) => hint.Scope == "All" || candidate.Lane is null || (hint.Scope switch
        {
            "Adjacent" => Math.Abs(lane - candidate.Lane.Value) == 1,
            "OtherLanes" => lane != candidate.Lane.Value,
            _ => lane == candidate.Lane.Value
        });
        if (hint.Action == "Draw") { return sign * Math.Max(0, amount) * Math.Max(0, 3 - view.Private.Hand.Length * 0.2); }
        if (hint.Action == "Summon")
        {
            var empty = view.Lanes.Count(lane => InScope(lane.LaneId) && (friendly == (own == 0) ? lane.PlayerOne : lane.PlayerTwo).MinionEntityId is null);
            return sign * Math.Min(empty, Math.Max(1, amount)) * 3;
        }
        if (hint.Target.EndsWith("Hero", StringComparison.Ordinal))
        {
            var player = view.Players.Single(player => (player.PlayerId == own) == !enemy);
            return hint.Action switch
            {
                "Damage" or "LoseHealth" => -sign * (Math.Min(amount, player.HeroHealth) * 1.8 + (amount >= player.HeroHealth ? 100 : 0)),
                "Heal" => sign * Math.Min(amount, player.HeroMaximumHealth - player.HeroHealth) * 1.3,
                "ModifyNumber" => sign * amount * (hint.Attribute is "MaximumCost" or "NextTurnCost" or "CurrentCost" ? 2 : 0.5),
                _ => 0
            };
        }
        if (hint.Target.EndsWith("Lanes", StringComparison.Ordinal))
        {
            var lanes = view.Lanes.Where(lane => InScope(lane.LaneId));
            return lanes.Sum(lane =>
            {
                var side = friendly == (own == 0) ? lane.PlayerOne : lane.PlayerTwo;
                return hint.Action switch
                {
                    "ClearEther" => -sign * side.EtherActivation * 1.8,
                    "PreventEtherDecay" => sign * (side.EtherActivation > 0 ? 1.8 : 0),
                    "ModifyNumber" when hint.Attribute == "EtherActivation" => sign * amount * (side.MinionEntityId is not null ? 2.2 : 1),
                    _ => 0.3
                };
            });
        }
        var targets = view.Entities.Where(entity => InScope(entity.LaneId) && (!friendly && !enemy || (entity.ControllerId == own) == friendly)
            && (hint.Target.EndsWith("Fields", StringComparison.Ordinal) ? entity.Card.CardKind == "Field" : entity.Card.CardKind == "Minion"));
        double Value(EntityView entity)
        {
            var relation = entity.ControllerId == own ? 1 : -1;
            var health = (double)(entity.CurrentHealth ?? entity.FieldEnergy ?? 1);
            return hint.Action switch
            {
                "Damage" or "LoseHealth" => -relation * (Math.Min(health, amount) + (amount >= health ? 2 + (entity.Attack ?? 0) * 0.65 : 0)),
                "Kill" or "Banish" or "Return" => -relation * (health * 0.8 + (entity.Attack ?? 0) * 1.2 + 2),
                "Heal" => relation * Math.Min(amount, (entity.MaximumHealth ?? health) - health) * 1.2,
                "ModifyNumber" => relation * (hint.Operation == "Set" ? amount - (hint.Attribute == "Attack" ? entity.Attack ?? 0 : 0) : amount)
                    * (hint.Attribute is "Attack" or "BaseAttack" ? 1.4 : hint.Attribute == "DamageTaken" ? -1 : 0.8),
                "AddKeyword" => relation * 1.5,
                "RemoveKeyword" => -relation,
                "ReduceSlow" => relation * Math.Min(amount, entity.SlowTurnsRemaining) * 1.8,
                _ => 0
            };
        }
        if (hint.Target == "Self" && candidate.Card.CardKind != "Spell")
        {
            var preview = PreviewEntity(view, candidate);
            return Value(preview);
        }
        return targets.Sum(Value);
    }

    private static ObserverView ProjectPlacement(ObserverView view, Candidate candidate)
    {
        if (candidate.Card.CardKind is not ("Minion" or "Field")) { return view; }
        var entity = PreviewEntity(view, candidate);
        return view with { Entities = view.Entities.Where(value => !(value.ControllerId == entity.ControllerId &&
            value.LaneId == entity.LaneId && value.Card.CardKind == entity.Card.CardKind)).Append(entity).ToImmutableArray() };
    }

    private static EntityView PreviewEntity(ObserverView view, Candidate candidate) => new(
        ulong.MaxValue - candidate.Card.CardInstanceId, candidate.Card, view.Private!.PlayerId, view.Private.PlayerId,
        candidate.Lane ?? 0, candidate.Card.Attack, candidate.Card.MaximumHealth, candidate.Card.MaximumHealth, null, false, false, 0, candidate.Card.Keywords);
    private static bool Has(ImmutableArray<KeywordView> keywords, string kind) => !keywords.IsDefaultOrEmpty && keywords.Any(keyword => keyword.Kind == kind);
    private static ClientPayload Payload(Candidate candidate) => candidate.Card.CardKind == "Spell"
        ? new PlanSpellPayload(candidate.Card.CardInstanceId, candidate.Lane)
        : new PlanCardPayload(candidate.Card.CardInstanceId, candidate.Lane!.Value);
    private ulong Tie(ulong card, int? lane, int turn)
    {
        // SplitMix-style mixing, independent of authoritative RNG and process-randomized string hashes.
        unchecked
        {
            var value = _seed ^ (card * 0x9e3779b97f4a7c15UL) ^ ((ulong)(lane + 2 ?? 0) << 32) ^ (uint)turn;
            value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
            value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
            return value ^ (value >> 31);
        }
    }
    private sealed record Candidate(CardView Card, int? Lane, ulong Tie);
    private sealed record SearchNode(ObserverView View, ImmutableArray<Candidate> Picks, long Cost, double Score);
}
