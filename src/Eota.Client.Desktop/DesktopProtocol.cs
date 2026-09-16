using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Eota.Content.Compiler;
using Eota.Kernel.Protocols;

namespace Eota.Client.Desktop;

public sealed record ProtocolChoice(string Value, string Label);
public sealed record ProtocolField(string Key, string Label, string Group, string Value, bool Editable, string Kind, ImmutableArray<ProtocolChoice> Choices);

// The desktop exposes protocol authoring data; scene scripts never construct Kernel protocols.
public sealed class DesktopProtocol
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    public string Json { get; }
    public ImmutableArray<ProtocolField> Fields { get; }
    public string Summary { get; }
    internal GameProtocolDefinition Definition { get; }
    public int RequiredDeckSize => Definition.RequiredDeckSize;
    public int MinCopies => Definition.MinCopiesPerCard;
    public int MaxCopies => Definition.MaxCopiesPerCard;
    public string ConstructionLabel => Fields.Single(field => field.Key == "deckConstructionPolicy").Choices
        .FirstOrDefault(choice => choice.Value == Fields.Single(field => field.Key == "deckConstructionPolicy").Value)?.Label ?? "开发构筑";
    public static DesktopProtocol Default => new(JsonSerializer.Serialize(new
    { schemaVersion = "eota.protocol/v0", protocol = GameProtocolDefinition.DefaultV0 with { MulliganEnabled = true } }, Options));

    public static DesktopProtocol Parse(string json) => new(json);

    private DesktopProtocol(string json)
    {
        var definition = Compile(json).Definition;
        Definition = definition;
        Json = json;
        var values = JsonNode.Parse(json)!["protocol"]!.AsObject();
        Fields = Descriptions.Select(entry =>
        {
            var choices = Choices(entry.Key);
            var value = values[entry.Key]?.ToString() ?? "";
            var kind = entry.Key == "mulliganEnabled" ? "boolean" : choices.IsEmpty ? "number" : "choice";
            return new ProtocolField(entry.Key, entry.Label, entry.Group, value, entry.Editable, kind, choices);
        }).ToImmutableArray();
        Summary = $"{definition.LaneCount} 条路 · 英雄生命 {definition.InitialHeroHealth} · 起手 {definition.OpeningHandSize} 张\n"
            + $"牌组 {definition.RequiredDeckSize} 张 · 单卡 {definition.MinCopiesPerCard}–{definition.MaxCopiesPerCard} 张 · {ConstructionLabel}\n"
            + $"{Choices("deckExhaustionPolicy").Single(choice => choice.Value == values["deckExhaustionPolicy"]!.ToString()).Label} · 每回合抽 {definition.CardsDrawnPerTurn} 张 · 换牌{(definition.MulliganEnabled ? "开启" : "关闭")}";
    }

    public DesktopProtocol WithValues(IReadOnlyDictionary<string, string> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var document = JsonNode.Parse(Json)!;
        var values = document["protocol"]!.AsObject();
        foreach (var (key, text) in changes)
        {
            var field = Fields.FirstOrDefault(value => value.Key == key);
            if (field is not { Editable: true }) { throw new InvalidDataException("此协议项目不可修改：" + key); }
            if (field.Kind == "boolean")
            {
                if (!bool.TryParse(text, out var enabled)) { throw new InvalidDataException(field.Label + "必须为开或关。"); }
                values[key] = enabled;
            }
            else if (field.Kind == "choice")
            {
                if (!field.Choices.Any(choice => choice.Value == text)) { throw new InvalidDataException(field.Label + "选项无效。"); }
                values[key] = text;
            }
            else
            {
                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                { throw new InvalidDataException(field.Label + "必须为整数。"); }
                values[key] = number;
            }
        }
        return new DesktopProtocol(document.ToJsonString(Options));
    }

    internal static CompiledGameProtocol Compile(string json)
    {
        var result = ProtocolCompiler.Compile("local-protocol.json", json);
        if (!result.IsSuccess)
        {
            throw new InvalidDataException("协议设置不合法，请检查牌组张数（1–256）、副本上下限、起手／手牌上限、费用及路数。\n"
            + string.Join("\n", result.Diagnostics.Select(value => value.Message)));
        }
        return result.Protocol!;
    }

    private static ImmutableArray<ProtocolChoice> Choices(string key) => key switch
    {
        "movementConflictPolicy" => [new("centerFirst", "中央优先（偶数路）"), new("outsideFirst", "外侧优先（偶数路）"), new("allFail", "竞争者全部失败")],
        "movementDirectionPreference" => [new("outwardFirst", "优先向外"), new("inwardFirst", "优先向内")],
        "deckExhaustionPolicy" => [new("noFatigue", "不疲劳"), new("increasingDamage", "疲劳伤害 1、2、3…"), new("instantDeath", "疲劳即死")],
        "handOverflowPolicy" => [new("burnDrawnCard", "满手时销毁抽到的牌")],
        "handCapacityPriority" => [new("returnThenGenerateThenDraw", "回手 → 生成 → 抽牌")],
        "drawAllocationPolicy" => [new("sourceEffectOrdinal", "按来源效果序号分配")],
        "deckConstructionPolicy" => [new("coreAndProfessionOrNeutral", "正常（本职业＋中立）"), new("coreProfessionOnly", "仅本职业"), new("coreAnyProfession", "无限制（不限职业）")],
        _ => []
    };

    private static readonly (string Key, string Label, string Group, bool Editable)[] Descriptions =
    [
        ("laneCount", "战场路数", "对局与资源", true), ("initialHeroHealth", "英雄初始生命", "对局与资源", true),
        ("initialPlayerCost", "初始可用费用", "对局与资源", true), ("initialMaxCost", "初始费用上限", "对局与资源", true),
        ("maxCostLimit", "费用上限封顶", "对局与资源", true), ("maxCostGrowthPerTurn", "每回合费用上限增长", "对局与资源", true),
        ("minEtherActivation", "以太下限", "对局与资源", true), ("maxEtherActivation", "以太上限", "对局与资源", true),
        ("etherDecayPerTurn", "每回合以太衰减", "对局与资源", true), ("fieldEnergyDecayPerTurn", "每回合场地能量衰减", "对局与资源", true),
        ("effectDurationDecayPerTurn", "每回合效果时长衰减", "对局与资源", true),
        ("openingHandSize", "起手张数", "手牌与移动", true), ("mulliganEnabled", "起手换牌", "手牌与移动", true),
        ("handLimit", "手牌上限", "手牌与移动", true), ("cardsDrawnPerTurn", "每回合抽牌", "手牌与移动", true),
        ("movementConflictPolicy", "移动竞争规则", "手牌与移动", true), ("movementDirectionPreference", "自动移动方向", "手牌与移动", true),
        ("deckExhaustionPolicy", "疲劳规则", "构筑与疲劳", true), ("handOverflowPolicy", "满手规则", "手牌与移动", false),
        ("handCapacityPriority", "手牌容量分配顺序", "手牌与移动", false), ("drawAllocationPolicy", "抽牌分配", "手牌与移动", false),
        ("requiredDeckSize", "牌组张数", "构筑与疲劳", true), ("minCopiesPerCard", "每种已选卡最少张数", "构筑与疲劳", true),
        ("maxCopiesPerCard", "每种卡最多张数", "构筑与疲劳", true), ("deckConstructionPolicy", "构筑规则", "构筑与疲劳", true),
        ("protocolId", "协议标识", "版本与结算限制", false), ("protocolVersion", "协议版本", "版本与结算限制", false),
        ("randomAlgorithmVersion", "随机算法", "版本与结算限制", false), ("randomCallSchemaVersion", "随机调用版本", "版本与结算限制", false),
        ("canonicalStateVersion", "状态格式版本", "版本与结算限制", false), ("effectLanguageVersion", "效果语言版本", "版本与结算限制", false),
        ("defaultEffectLoopLimit", "效果循环上限", "版本与结算限制", false), ("maxEffectTriggerFramesPerTurn", "每回合效果触发帧上限", "版本与结算限制", false),
        ("maxEffectIntentsPerFrame", "每帧效果操作上限", "版本与结算限制", false)
    ];
}
