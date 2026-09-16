using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Eota.Kernel.Content;
using Eota.Kernel.Effects;
using Eota.Kernel.Primitives;

namespace Eota.Content.Compiler;

internal sealed class EffectCompiler(CardKind cardKind, bool hasLane)
{
    private int _nodes;
    private EffectTriggerKind _trigger;
    private EffectTargetType? _subjectType;
    private bool _previousAvailable;
    private bool _loopAvailable;

    public static ImmutableArray<EffectDefinition> Compile(JsonElement root, CardDefinition card, string source,
        ImmutableArray<ContentDiagnostic>.Builder diagnostics, bool attached = false)
    {
        if (!root.TryGetProperty("effects", out var effects)) { return []; }
        var result = ImmutableArray.CreateBuilder<EffectDefinition>();
        try
        {
            if (effects.ValueKind != JsonValueKind.Array || effects.GetArrayLength() > 32)
            { throw new EffectCompileException("invalid-effects", "$.effects", "Effects must be an array with at most 32 entries."); }
            var compiler = new EffectCompiler(card.Kind, card is not SpellCardDefinition { TargetScope: SpellTargetScope.Global });
            var index = 0;
            foreach (var value in effects.EnumerateArray())
            {
                var path = $"$.effects[{index++}]";
                Properties(value, path, "id", "trigger", "condition", "body", "charge");
                var id = String(value, "id", path).Normalize(NormalizationForm.FormC);
                if (id.Length > 96) { throw new EffectCompileException("invalid-effect-id", path + ".id", "Effect IDs may have at most 96 characters."); }
                var trigger = Required(value, "trigger", path);
                Properties(trigger, path + ".trigger", "kind", "scope", "subjectType", "otherOnly");
                compiler._trigger = EnumValue<EffectTriggerKind>(String(trigger, "kind", path + ".trigger"), path + ".trigger.kind");
                compiler._subjectType = trigger.TryGetProperty("subjectType", out _) ? EnumValue<EffectTargetType>(String(trigger, "subjectType", path), path + ".trigger.subjectType")
                    : compiler._trigger is EffectTriggerKind.SelfDamaged or EffectTriggerKind.Combat or EffectTriggerKind.Attack
                        or EffectTriggerKind.FriendlyCombat or EffectTriggerKind.EnemyCombat or EffectTriggerKind.EnemyAttack or EffectTriggerKind.CombatDamage or EffectTriggerKind.MinionCombat ? EffectTargetType.Minion
                    : compiler._trigger is EffectTriggerKind.SelfEntered or EffectTriggerKind.SelfDied or EffectTriggerKind.SelfLeft or EffectTriggerKind.ReplacementEntered
                        ? card.Kind == CardKind.Minion ? EffectTargetType.Minion : EffectTargetType.Field : null;
                var otherOnly = trigger.TryGetProperty("otherOnly", out var otherJson) && otherJson.ValueKind == JsonValueKind.True;
                if (trigger.TryGetProperty("otherOnly", out otherJson) && otherJson.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                { throw new EffectCompileException("invalid-type", path + ".trigger.otherOnly", "Expected boolean."); }
                if (!attached && (compiler._trigger == EffectTriggerKind.SelfSpellCast) != (card.Kind == CardKind.Spell)
                    && (compiler._trigger == EffectTriggerKind.SelfSpellCast || card.Kind == CardKind.Spell))
                { throw new EffectCompileException("invalid-trigger-source", path + ".trigger", "Spells use selfSpellCast; battlefield triggers need an entity source."); }
                if (compiler._trigger is EffectTriggerKind.SelfDamaged or EffectTriggerKind.CombatDamage && card.Kind != CardKind.Minion)
                { throw new EffectCompileException("invalid-trigger-source", path + ".trigger", "Self damage requires a minion."); }
                var scope = Scope(trigger, path + ".trigger");
                if (scope is not (SelectionScope.Lane or SelectionScope.All))
                { throw new EffectCompileException("invalid-trigger-scope", path + ".trigger.scope", "Trigger scopes are lane or all; adjacent and otherLanes are selector scopes."); }
                var condition = value.TryGetProperty("condition", out var conditionJson)
                    ? compiler.Condition(conditionJson, path + ".condition", null, 0) : null;
                long? charge = value.TryGetProperty("charge", out var chargeJson) && chargeJson.ValueKind == JsonValueKind.Number && chargeJson.TryGetInt64(out var amount) && amount >= 0 ? amount : null;
                if ((compiler._trigger is EffectTriggerKind.PreCombatCharge or EffectTriggerKind.EndTurnCharge) != charge.HasValue
                    || (value.TryGetProperty("charge", out _) && charge is null))
                { throw new EffectCompileException("invalid-charge-requirement", path + ".charge", "Charge timing requires a nonnegative Int64 charge requirement."); }
                var body = compiler.Node(Required(value, "body", path), path + ".body", null, 0);
                if (compiler._trigger == EffectTriggerKind.CombatDamage && !CombatContribution(body))
                { throw new EffectCompileException("invalid-combat-contribution", path + ".body", "Combat damage contributions must emit hero damage in the same frame."); }
                result.Add(new EffectDefinition(id, new EffectTrigger(compiler._trigger, scope, compiler._subjectType, otherOnly), condition, body, charge));
            }
            if (result.Select(effect => effect.Id).Distinct(StringComparer.Ordinal).Count() != result.Count)
            { throw new EffectCompileException("duplicate-effect-id", "$.effects", "Effect IDs must be unique within a card."); }
            ValidateCombinedBudget(result);
        }
        catch (EffectCompileException error)
        {
            diagnostics.Add(new ContentDiagnostic(ContentDiagnosticSeverity.Error, error.Code, source, error.Path, error.Message));
        }
        return result.OrderBy(value => value.Id, StringComparer.Ordinal).ToImmutableArray();
    }

    private static void ValidateCombinedBudget(IEnumerable<EffectDefinition> effects)
    {
        var nodes = 0;
        foreach (var effect in effects) { Definition(effect, 0); }
        void Check(int depth)
        {
            if (++nodes > 128 || depth > 16)
            { throw new EffectCompileException("effect-budget-exceeded", "$.effects", "The complete graph, including granted effects, supports at most 128 nodes and depth 16."); }
        }
        void Definition(EffectDefinition effect, int depth)
        { if (effect.Condition is { } condition) { Condition(condition, depth); } Node(effect.Body, depth); }
        void Condition(EffectCondition condition, int depth)
        {
            Check(depth);
            if (condition is AllCondition all) { foreach (var child in all.Children) { Condition(child, depth + 1); } }
            if (condition is NotCondition not) { Condition(not.Child, depth + 1); }
        }
        void Node(EffectNode node, int depth)
        {
            Check(depth);
            switch (node)
            {
                case ParallelEffect parallel: foreach (var child in parallel.Children) { Node(child, depth + 1); } break;
                case SequenceEffect sequence: foreach (var child in sequence.Steps) { Node(child, depth + 1); } break;
                case LoopEffect loop: Node(loop.Body, depth + 1); if (loop.While is { } condition) { Condition(condition, depth + 1); } break;
                case RetargetEffect retarget: Node(retarget.Body, depth + 1); break;
                case IfElseEffect branch:
                    Condition(branch.Condition, depth + 1); Node(branch.Then, depth + 1);
                    if (branch.Else is { } otherwise) { Node(otherwise, depth + 1); }
                    break;
                case GrantEffect grant: Definition(grant.Definition, depth + 1); break;
            }
        }
    }

    private static bool CombatContribution(EffectNode node) => node switch
    {
        EmitEffect { Action: EffectAction.Damage, Selector.Kind: SelectorKind.FriendlyHero or SelectorKind.EnemyHero, Duration: null } => true,
        ParallelEffect parallel => parallel.Children.All(CombatContribution),
        IfElseEffect branch => CombatContribution(branch.Then) && (branch.Else is null || CombatContribution(branch.Else)),
        _ => false
    };

    private EffectNode Node(JsonElement value, string path, EffectTargetType? targetType, int depth)
    {
        Budget(path, depth);
        var kind = String(value, "kind", path);
        switch (kind)
        {
            case "laneStatus":
                Properties(value, path, "kind", "target", "status", "remove", "duration");
                var laneTarget = Selector(Required(value, "target", path), path + ".target", targetType);
                if (laneTarget.Type != EffectTargetType.Lane)
                { throw new EffectCompileException("effect-target-type-mismatch", path + ".target", "Lane statuses require lane targets."); }
                var hasRemove = value.TryGetProperty("remove", out var removeValue);
                if (hasRemove && removeValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                { throw new EffectCompileException("invalid-lane-status-remove", path + ".remove", "Remove must be a boolean."); }
                var removeStatus = hasRemove && removeValue.GetBoolean();
                if (removeStatus && value.TryGetProperty("duration", out _))
                { throw new EffectCompileException("invalid-lane-status-duration", path, "Removal cannot specify a duration."); }
                return new LaneStatusEffect(laneTarget.Selector, EnumValue<LaneStatusKind>(String(value, "status", path), path + ".status"), removeStatus,
                    value.TryGetProperty("duration", out var statusDuration) ? Expression(statusDuration, path + ".duration", targetType) : null);
            case "grantEffect":
                Properties(value, path, "kind", "target", "effect", "duration");
                var receiver = Selector(Required(value, "target", path), path + ".target", targetType);
                if (receiver.Type is not (EffectTargetType.Minion or EffectTargetType.Field or EffectTargetType.Hero))
                { throw new EffectCompileException("effect-target-type-mismatch", path + ".target", "Effects attach to entities or players."); }
                var targetCard = receiver.Type switch
                {
                    EffectTargetType.Minion => (CardDefinition)new MinionCardDefinition(new CardPrototypeId("embedded"), CardSource.Test, Profession.Neutral, 0, 1, 1, []),
                    EffectTargetType.Field => new FieldCardDefinition(new CardPrototypeId("embedded"), CardSource.Test, Profession.Neutral, 0, new PermanentFieldLifetimeDefinition(), false),
                    _ => new SpellCardDefinition(new CardPrototypeId("embedded"), CardSource.Test, Profession.Neutral, 0, SpellSpeed.Fast, hasLane ? SpellTargetScope.Lane : SpellTargetScope.Global)
                };
                using (var embedded = JsonDocument.Parse("{\"effects\":[" + Required(value, "effect", path).GetRawText() + "]}"))
                {
                    var errors = ImmutableArray.CreateBuilder<ContentDiagnostic>();
                    var definitions = Compile(embedded.RootElement, targetCard, "embedded", errors, true);
                    if (errors.Count != 0)
                    { throw new EffectCompileException(errors[0].Code, path + ".effect" + errors[0].Path.Replace("$.effects[0]", "", StringComparison.Ordinal), errors[0].Message); }
                    if (definitions[0].Trigger.Kind is EffectTriggerKind.PreCombatCharge or EffectTriggerKind.EndTurnCharge or EffectTriggerKind.SelfSpellCast or EffectTriggerKind.CombatDamage)
                    { throw new EffectCompileException("invalid-attached-trigger", path + ".effect.trigger", "Attached effects use battlefield or player observation triggers."); }
                    if (receiver.Type == EffectTargetType.Hero && definitions[0].Trigger.Kind is not (EffectTriggerKind.FriendlyDied or EffectTriggerKind.EnemyDied
                        or EffectTriggerKind.FriendlyLeft or EffectTriggerKind.EnemyLeft or EffectTriggerKind.FriendlyEntered or EffectTriggerKind.EnemyEntered
                        or EffectTriggerKind.EnemyAttack or EffectTriggerKind.FriendlySpellCast or EffectTriggerKind.FriendlyCombat or EffectTriggerKind.EnemyCombat or EffectTriggerKind.TurnEnd))
                    { throw new EffectCompileException("invalid-player-trigger", path + ".effect.trigger", "Player attachments require an observer or turn-end trigger."); }
                    return new GrantEffect(receiver.Selector, definitions[0], value.TryGetProperty("duration", out var attachedDuration)
                        ? Expression(attachedDuration, path + ".duration", targetType) : null);
                }
            case "draw":
            case "generate":
                Properties(value, path, kind == "draw" ? ["kind", "target", "count", "filter"] : ["kind", "target", "count", "prototype"]);
                var handTarget = Selector(Required(value, "target", path), path + ".target", targetType);
                if (handTarget.Type != EffectTargetType.Hero)
                { throw new EffectCompileException("effect-target-type-mismatch", path + ".target", "Hand requests require a player hero target."); }
                CardPrototypeId? generated = null;
                if (kind == "generate")
                {
                    if (!CardPrototypeId.TryParse(String(value, "prototype", path), out var generatedId))
                    { throw new EffectCompileException("invalid-card-reference", path + ".prototype", "Invalid prototype identity."); }
                    generated = generatedId;
                }
                return new HandEffect(kind == "draw" ? HandRequestKind.Draw : HandRequestKind.Generate, handTarget.Selector,
                    Expression(Required(value, "count", path), path + ".count", targetType), generated,
                    value.TryGetProperty("filter", out var drawFilter) ? Filter(drawFilter, path + ".filter") : null);
            case "summon":
                Properties(value, path, "kind", "target", "prototype", "slot");
                var summonTarget = Selector(Required(value, "target", path), path + ".target", targetType);
                if (summonTarget.Type != EffectTargetType.Lane)
                { throw new EffectCompileException("effect-target-type-mismatch", path + ".target", "Summon requires lane targets."); }
                if (!CardPrototypeId.TryParse(String(value, "prototype", path), out var summonPrototype))
                { throw new EffectCompileException("invalid-card-reference", path + ".prototype", "Invalid prototype identity."); }
                var slot = value.TryGetProperty("slot", out _) ? EnumValue<Eota.Kernel.Matches.BattlefieldSlotKind>(String(value, "slot", path), path + ".slot")
                    : Eota.Kernel.Matches.BattlefieldSlotKind.Minion;
                return new SummonEffect(summonTarget.Selector, summonPrototype, slot);
            case "sequence":
                Properties(value, path, "kind", "steps");
                var sequenceSteps = ImmutableArray.CreateBuilder<EffectNode>();
                var outerPrevious = _previousAvailable;
                var stepIndex = 0;
                foreach (var step in Array(value, "steps", path))
                {
                    _previousAvailable = outerPrevious || stepIndex > 0;
                    sequenceSteps.Add(Node(step, path + $".steps[{stepIndex++}]", targetType, depth + 1));
                }
                _previousAvailable = outerPrevious;
                return new SequenceEffect(sequenceSteps.ToImmutable());
            case "loop":
                Properties(value, path, "kind", "count", "body", "while");
                var count = Expression(Required(value, "count", path), path + ".count", targetType);
                var outerLoop = _loopAvailable;
                _loopAvailable = true;
                var loopBody = Node(Required(value, "body", path), path + ".body", targetType, depth + 1);
                var loopWhile = value.TryGetProperty("while", out var whileJson) ? Condition(whileJson, path + ".while", targetType, depth + 1) : null;
                _loopAvailable = outerLoop;
                return new LoopEffect(count, loopBody, loopWhile);
            case "return":
            case "banish":
            case "transform":
            case "replace":
                var lifecycleOperation = EnumValue<LifecycleOperation>(kind, path + ".kind");
                var needsPrototype = lifecycleOperation is LifecycleOperation.Transform or LifecycleOperation.Replace;
                Properties(value, path, needsPrototype ? ["kind", "target", "prototype"] : ["kind", "target"]);
                var lifeTarget = Selector(Required(value, "target", path), path + ".target", targetType);
                if (lifeTarget.Type is not (EffectTargetType.Minion or EffectTargetType.Field))
                { throw new EffectCompileException("effect-target-type-mismatch", path + ".target", "Lifecycle requires battlefield entity targets."); }
                CardPrototypeId? prototype = null;
                if (needsPrototype)
                {
                    if (!CardPrototypeId.TryParse(String(value, "prototype", path), out var parsed))
                    { throw new EffectCompileException("invalid-card-reference", path + ".prototype", "Invalid prototype identity."); }
                    prototype = parsed;
                }
                return new LifecycleEffect(lifecycleOperation, lifeTarget.Selector, prototype);
            case "parallel":
                Properties(value, path, "kind", "children");
                return new ParallelEffect(Array(value, "children", path).Select((child, index) =>
                    Node(child, $"{path}.children[{index}]", targetType, depth + 1)).ToImmutableArray());
            case "ifElse":
                Properties(value, path, "kind", "condition", "then", "else");
                return new IfElseEffect(Condition(Required(value, "condition", path), path + ".condition", targetType, depth + 1),
                    Node(Required(value, "then", path), path + ".then", targetType, depth + 1),
                    value.TryGetProperty("else", out var otherwise) ? Node(otherwise, path + ".else", targetType, depth + 1) : null);
            case "forEach":
            case "retarget":
                Properties(value, path, "kind", "target", "body");
                var retarget = Selector(Required(value, "target", path), path + ".target", targetType);
                return new RetargetEffect(retarget.Selector, Node(Required(value, "body", path), path + ".body", retarget.Type, depth + 1));
        }
        var action = EnumValue<EffectAction>(kind, path + ".kind");
        var properties = action switch
        {
            EffectAction.Damage or EffectAction.Heal or EffectAction.LoseHealth or EffectAction.ReduceSlow => new[] { "kind", "target", "amount" },
            EffectAction.Kill or EffectAction.PreventEtherDecay or EffectAction.ClearEther => new[] { "kind", "target" },
            EffectAction.ModifyNumber => new[] { "kind", "target", "amount", "attribute", "operation" },
            EffectAction.AddKeyword => new[] { "kind", "target", "keyword", "amount" },
            EffectAction.RemoveKeyword => new[] { "kind", "target", "keyword" },
            _ => throw new EffectCompileException("unknown-effect-kind", path, "Unsupported effect.")
        };
        Properties(value, path, properties.Concat(["duration"]).ToArray());
        var selector = Selector(Required(value, "target", path), path + ".target", targetType);
        var attribute = action == EffectAction.ModifyNumber ? EnumValue<NumericProperty>(String(value, "attribute", path), path + ".attribute") : NumericProperty.Attack;
        var operation = action == EffectAction.ModifyNumber ? EnumValue<NumericOperation>(String(value, "operation", path), path + ".operation") : NumericOperation.Add;
        var keyword = action is EffectAction.AddKeyword or EffectAction.RemoveKeyword
            ? EnumValue<MinionKeywordKind>(String(value, "keyword", path), path + ".keyword") : MinionKeywordKind.Swift;
        if (action == EffectAction.ModifyNumber && attribute is NumericProperty.ChargeRequirement or NumericProperty.BaseAttack && operation != NumericOperation.Set)
        { throw new EffectCompileException("invalid-charge-operation", path + ".operation", "Charge requirement overrides use Set."); }
        var compatible = action switch
        {
            EffectAction.Damage or EffectAction.Heal => selector.Type is EffectTargetType.Hero or EffectTargetType.Minion,
            EffectAction.LoseHealth or EffectAction.ReduceSlow => selector.Type == EffectTargetType.Minion,
            EffectAction.Kill => selector.Type is EffectTargetType.Hero or EffectTargetType.Minion or EffectTargetType.Field,
            EffectAction.AddKeyword or EffectAction.RemoveKeyword => selector.Type is EffectTargetType.Minion or EffectTargetType.Card
                || (selector.Type == EffectTargetType.Field && keyword is MinionKeywordKind.Replace or MinionKeywordKind.Replaceable),
            EffectAction.PreventEtherDecay or EffectAction.ClearEther => selector.Type == EffectTargetType.Lane,
            EffectAction.ModifyNumber => NumericRules.Supports(selector.Type, attribute),
            _ => false
        };
        if (!compatible) { throw new EffectCompileException("effect-target-type-mismatch", path + ".target", "Target type is incompatible with this action/property."); }
        var needsAmount = action is EffectAction.Damage or EffectAction.Heal or EffectAction.ModifyNumber or EffectAction.LoseHealth or EffectAction.ReduceSlow
            || (action == EffectAction.AddKeyword && keyword == MinionKeywordKind.Slow);
        var amount = needsAmount ? Expression(Required(value, "amount", path), path + ".amount", targetType) : null;
        if (!needsAmount && value.TryGetProperty("amount", out _))
        { throw new EffectCompileException("unexpected-property", path + ".amount", "This keyword/action has no numeric parameter."); }
        if (amount is ConstantExpression constant && ((operation == NumericOperation.Divide && constant.Value == 0)
            || (action == EffectAction.AddKeyword && keyword == MinionKeywordKind.Slow && constant.Value <= 0)
            || (action == EffectAction.ReduceSlow && constant.Value <= 0)))
        { throw new EffectCompileException("invalid-effect-amount", path + ".amount", "Division requires a nonzero divisor; slow changes require a positive count."); }
        var duration = value.TryGetProperty("duration", out var durationJson) ? Expression(durationJson, path + ".duration", targetType) : null;
        var timedCharge = selector.Type is EffectTargetType.Minion or EffectTargetType.Field && action == EffectAction.ModifyNumber
            && attribute == NumericProperty.ChargeRequirement && operation == NumericOperation.Set;
        var timedBase = selector.Type == EffectTargetType.Minion && action == EffectAction.ModifyNumber && attribute == NumericProperty.BaseAttack && operation == NumericOperation.Set;
        if (duration is not null && !timedCharge && !timedBase && (selector.Type != EffectTargetType.Minion || action is not (EffectAction.ModifyNumber or EffectAction.AddKeyword or EffectAction.RemoveKeyword)
            || action == EffectAction.ModifyNumber && (operation != NumericOperation.Add || attribute is not (NumericProperty.Attack or NumericProperty.MaximumHealth or NumericProperty.IncomingDamageAdjustment))
            || action is EffectAction.AddKeyword or EffectAction.RemoveKeyword && keyword == MinionKeywordKind.Slow))
        { throw new EffectCompileException("unsupported-duration-operation", path + ".duration", "This duration form supports minion stat additions and non-slow keywords."); }
        return new EmitEffect(action, selector.Selector, amount, attribute, operation, keyword, duration);
    }

    private EffectCondition Condition(JsonElement value, string path, EffectTargetType? targetType, int depth)
    {
        Budget(path, depth);
        switch (String(value, "kind", path))
        {
            case "compare":
                Properties(value, path, "kind", "left", "operator", "right");
                return new CompareCondition(Expression(Required(value, "left", path), path + ".left", targetType),
                    EnumValue<ComparisonOperation>(String(value, "operator", path), path + ".operator"),
                    Expression(Required(value, "right", path), path + ".right", targetType));
            case "exists":
                Properties(value, path, "kind", "target");
                return new ExistsCondition(Selector(Required(value, "target", path), path + ".target", targetType).Selector);
            case "hasKeyword":
                Properties(value, path, "kind", "target", "keyword");
                var selected = Selector(Required(value, "target", path), path + ".target", targetType);
                var keyword = EnumValue<MinionKeywordKind>(String(value, "keyword", path), path + ".keyword");
                if (selected.Type is not (EffectTargetType.Minion or EffectTargetType.Card)
                    && !(selected.Type == EffectTargetType.Field && keyword is MinionKeywordKind.Replace or MinionKeywordKind.Replaceable))
                { throw new EffectCompileException("effect-target-type-mismatch", path, "Keyword is incompatible with target type."); }
                return new KeywordCondition(selected.Selector, keyword);
            case "cardMatches":
                Properties(value, path, "kind", "subject", "filter");
                var subject = EnumValue<EffectCardReference>(String(value, "subject", path), path + ".subject");
                if (subject == EffectCardReference.Event && _subjectType is null
                    || subject == EffectCardReference.Replaced && _trigger != EffectTriggerKind.ReplacementEntered
                    || subject == EffectCardReference.Target && targetType is not (EffectTargetType.Card or EffectTargetType.Minion or EffectTargetType.Field))
                { throw new EffectCompileException("unavailable-card-reference", path, "Card reference is unavailable in this context."); }
                return new CardMatchesCondition(subject, Filter(Required(value, "filter", path), path + ".filter"));
            case "all":
                Properties(value, path, "kind", "children");
                return new AllCondition(Array(value, "children", path).Select((child, index) =>
                    Condition(child, $"{path}.children[{index}]", targetType, depth + 1)).ToImmutableArray());
            case "not":
                Properties(value, path, "kind", "condition");
                return new NotCondition(Condition(Required(value, "condition", path), path + ".condition", targetType, depth + 1));
            default: throw new EffectCompileException("unknown-condition", path, "Unknown condition kind.");
        }
    }

    private (EffectSelector Selector, EffectTargetType Type) Selector(JsonElement value, string path, EffectTargetType? targetType)
    {
        Properties(value, path, "kind", "scope", "targetType", "filter");
        var kind = EnumValue<SelectorKind>(String(value, "kind", path), path + ".kind");
        var scope = Scope(value, path);
        var previous = kind is SelectorKind.PreviousAffected or SelectorKind.PreviousCreated or SelectorKind.PreviousRemoved;
        EffectTargetType? filter = value.TryGetProperty("targetType", out _)
            ? EnumValue<EffectTargetType>(String(value, "targetType", path), path + ".targetType") : null;
        if (previous && (!_previousAvailable || filter is null))
        { throw new EffectCompileException("invalid-selector-scope", path, "Previous outputs require an earlier Sequence step and explicit targetType."); }
        if (!previous && filter is not null)
        { throw new EffectCompileException("unexpected-property", path + ".targetType", "targetType is for previous-result selectors."); }
        EffectTargetType? type = kind switch
        {
            SelectorKind.Self => cardKind == CardKind.Minion ? EffectTargetType.Minion : cardKind == CardKind.Field ? EffectTargetType.Field : null,
            SelectorKind.FriendlyHero or SelectorKind.EnemyHero => EffectTargetType.Hero,
            SelectorKind.FriendlyMinions or SelectorKind.EnemyMinions or SelectorKind.AllMinions => EffectTargetType.Minion,
            SelectorKind.FriendlyFields or SelectorKind.EnemyFields => EffectTargetType.Field,
            SelectorKind.FriendlyLanes or SelectorKind.EnemyLanes => EffectTargetType.Lane,
            SelectorKind.FriendlyHand or SelectorKind.EnemyHand or SelectorKind.FriendlyDeck or SelectorKind.EnemyDeck => EffectTargetType.Card,
            SelectorKind.Targets => targetType,
            SelectorKind.EventSubject => _subjectType,
            SelectorKind.EventTarget => _trigger == EffectTriggerKind.MinionCombat ? EffectTargetType.Minion : null,
            SelectorKind.PreviousAffected or SelectorKind.PreviousCreated or SelectorKind.PreviousRemoved => filter,
            _ => null
        };
        if (type is null) { throw new EffectCompileException("invalid-selector-scope", path, "Selector has no typed source/target in this context."); }
        if (!hasLane && scope != SelectionScope.All && !previous && type != EffectTargetType.Card && kind is not (SelectorKind.FriendlyHero or SelectorKind.EnemyHero or SelectorKind.Targets))
        { throw new EffectCompileException("invalid-selector-scope", path, "Global spells must select all lanes explicitly."); }
        var cardFilter = value.TryGetProperty("filter", out var filterJson) ? Filter(filterJson, path + ".filter") : null;
        if (cardFilter is not null && type is not (EffectTargetType.Minion or EffectTargetType.Field or EffectTargetType.Card))
        { throw new EffectCompileException("effect-target-type-mismatch", path + ".filter", "Card filters require cards or battlefield entities."); }
        return (new EffectSelector(kind, scope, filter, cardFilter), type.Value);
    }

    private static CardFilter Filter(JsonElement value, string path)
    {
        Properties(value, path, "kind", "profession", "tag");
        var tag = value.TryGetProperty("tag", out _) ? String(value, "tag", path) : null;
        if (tag is not null && !CardPrototypeId.TryParse(tag, out _))
        { throw new EffectCompileException("invalid-tag", path + ".tag", "Tags use stable ASCII identifiers."); }
        return new CardFilter(value.TryGetProperty("kind", out _) ? EnumValue<CardKind>(String(value, "kind", path), path + ".kind") : null,
            value.TryGetProperty("profession", out _) ? EnumValue<Profession>(String(value, "profession", path), path + ".profession") : null, tag);
    }

    private IntExpression Expression(JsonElement value, string path, EffectTargetType? targetType)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) { return new ConstantExpression(number); }
        if (value.ValueKind != JsonValueKind.String) { throw new EffectCompileException("invalid-expression", path, "Use an Int64 literal or expression string."); }
        return new EffectExpressionParser(value.GetString()!, path, name =>
        {
            if (name.StartsWith("target.", StringComparison.Ordinal) && targetType is null)
            { throw new EffectCompileException("invalid-variable-scope", path, "target variables require a Retarget binding."); }
            var valid = name switch
            {
                "owner.health" or "owner.maxHealth" or "owner.cost" or "owner.maxCost" or "owner.resonatingLanes" => true,
                "previous.scalar" or "previous.affectedCount" or "previous.createdCount" or "previous.removedCount" => _previousAvailable,
                "loop.index" => _loopAvailable,
                "lane.ether" => hasLane,
                "event.amount" => _trigger is EffectTriggerKind.SelfDamaged or EffectTriggerKind.CombatDamage,
                "event.attack" or "event.health" or "event.maxHealth" or "event.slow" => _subjectType == EffectTargetType.Minion,
                "event.energy" => _subjectType == EffectTargetType.Field,
                "replaced.attack" or "replaced.health" or "replaced.maxHealth" => _trigger == EffectTriggerKind.ReplacementEntered && cardKind == CardKind.Minion,
                "source.attack" or "source.health" or "source.maxHealth" or "source.slow" => cardKind == CardKind.Minion,
                "source.energy" => cardKind == CardKind.Field,
                "target.attack" => targetType is EffectTargetType.Minion or EffectTargetType.Card,
                "target.slow" => targetType == EffectTargetType.Minion,
                "target.health" or "target.maxHealth" => targetType is EffectTargetType.Minion or EffectTargetType.Hero or EffectTargetType.Card,
                "target.cost" => targetType == EffectTargetType.Card,
                "target.energy" => targetType == EffectTargetType.Field,
                "target.ether" => targetType == EffectTargetType.Lane,
                _ => false
            };
            if (!valid) { throw new EffectCompileException("unknown-variable", path, $"Unknown or incompatible variable '{name}'."); }
        }).Parse();
    }

    private void Budget(string path, int depth)
    {
        if (++_nodes > 128 || depth > 16) { throw new EffectCompileException("effect-budget-exceeded", path, "At most 128 nodes and depth 16 are supported per card."); }
    }
    private static SelectionScope Scope(JsonElement value, string path) => value.TryGetProperty("scope", out _)
        ? EnumValue<SelectionScope>(String(value, "scope", path), path + ".scope") : SelectionScope.Lane;
    private static T EnumValue<T>(string name, string path) where T : struct, Enum
    {
        foreach (var value in Enum.GetValues<T>())
        {
            var text = value.ToString();
            if (name == char.ToLowerInvariant(text[0]) + text[1..]) { return value; }
        }
        throw new EffectCompileException("invalid-enum", path, $"Unsupported value '{name}'.");
    }
    private static JsonElement Required(JsonElement value, string name, string path) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child)
        ? child : throw new EffectCompileException("missing-property", path + "." + name, "Required property missing.");
    private static string String(JsonElement value, string name, string path)
    {
        var child = Required(value, name, path);
        return child.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(child.GetString())
            ? child.GetString()! : throw new EffectCompileException("invalid-type", path + "." + name, "Expected a nonempty string.");
    }
    private static JsonElement.ArrayEnumerator Array(JsonElement value, string name, string path)
    {
        var child = Required(value, name, path);
        return child.ValueKind == JsonValueKind.Array ? child.EnumerateArray()
            : throw new EffectCompileException("invalid-type", path + "." + name, "Expected array.");
    }
    private static void Properties(JsonElement value, string path, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) { throw new EffectCompileException("invalid-type", path, "Expected object."); }
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            { throw new EffectCompileException("unknown-property", path + "." + property.Name, "Unsupported property."); }
        }
    }
}
