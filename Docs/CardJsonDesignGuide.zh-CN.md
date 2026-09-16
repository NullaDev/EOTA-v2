# Mod 开发者卡牌 JSON 编写指南

更新于 2026-09-14。面向使用当前 VNext 卡牌编辑器及内容编译器的 Mod 作者。卡牌格式为 `eota.card/v2`，效果语言为 4；教学卡按当前卡池收紧了费用与持续收益；正式设计可对照 [四职业体系与强度约束](Content/CardSet-Redesign.zh-CN.md) 和 [完整卡表](CardTable.zh-CN.md)，再用实际牌组测试组合强度。

从零开始可先读第 1–3 节，再按效果需要查阅第 4–10 节。可复制源文件见 [完整示例目录](Content/Examples/README.zh-CN.md)，玩家构筑与疲劳规则见 [玩法指南](GameplayGuide.zh-CN.md)，素材制作和编辑器操作见 [内容工具](Content/Authoring-Tools.zh-CN.md)。

## 目录

1. [创建与发布第一张卡](#1-创建与发布第一张卡)
2. [JSON 约定](#2-json-约定)
3. [卡牌公共字段与三种类型](#3-卡牌公共字段与三种类型)
4. [效果根与全部触发器](#4-效果根与全部触发器)
5. [目标选择器与过滤](#5-目标选择器与过滤)
6. [条件](#6-条件)
7. [动作与可修改属性](#7-动作与可修改属性)
8. [组合、顺序与临时效果](#8-组合顺序与临时效果)
9. [整数表达式](#9-整数表达式)
10. [以太、冻结与锁闭](#10-以太冻结与锁闭)
11. [本地化、插图与内容包](#11-本地化插图与内容包)
12. [协议、卡组与问题排查](#12-协议卡组与问题排查)

## 1. 创建与发布第一张卡

在客户端打开“卡牌编辑器”，新建随从，在“完整 JSON”中粘贴下例，再填写基本属性中的卡名与说明。它是一张 3 费 2/3 守备随从，入场对敌方英雄造成 1 点伤害。

<!-- mod-example: card -->
```json
{
  "schemaVersion": "eota.card/v2",
  "kind": "minion",
  "id": "MOD-DEMO-SENTINEL",
  "source": "core",
  "profession": "guardian",
  "cost": 3,
  "attack": 2,
  "health": 3,
  "keywords": [
    "guard"
  ],
  "tags": [
    "soldier"
  ],
  "nameLocalizationKey": "card.MOD-DEMO-SENTINEL.name",
  "descriptionLocalizationKey": "card.MOD-DEMO-SENTINEL.description",
  "texturePath": "Content/Generated/Art/EOTA-CORE-GUA-MIN-001.png",
  "effects": [
    {
      "id": "entry-hit",
      "trigger": {
        "kind": "selfEntered"
      },
      "body": {
        "kind": "damage",
        "target": {
          "kind": "enemyHero"
        },
        "amount": 1
      }
    }
  ]
}
```

依次点击“校验预览”“保存卡牌”。保存的是独立草稿，需在“内容包管理”中启用，新的构筑和对局才会使用它。发布时点击“导出卡牌”生成 `.eotapack.json`，接收者通过“导入并合并卡牌”导入，再启用。引用了衍生卡时，先创建并保存被引用卡，再保存引用它的卡；导出会自动附带递归依赖。

“完整 JSON”接受上面的整张卡；“效果 JSON”接受包含 `effects` 等能力字段的对象，不能把整张卡或单独的效果数组粘进这个区域。以下可直接作为随从的效果 JSON：

<!-- mod-example: minion-abilities -->
```json
{
  "keywords": [
    "guard"
  ],
  "storedCharge": 0,
  "effects": [
    {
      "id": "entry-hit",
      "trigger": {
        "kind": "selfEntered"
      },
      "body": {
        "kind": "damage",
        "target": {
          "kind": "enemyHero"
        },
        "amount": 1
      }
    }
  ]
}
```

能力对象整体替换原能力字段；省略某项会清除其旧值并使用默认值。场地还可包含 `preventsActiveAttacksInLane`；法术通常只填写 `effects`。文本不会自动生成效果，修改说明也不会改变规则。

## 2. JSON 约定

- 使用 UTF-8 JSON 对象。一份卡牌源文件只定义一张卡；不能有注释、尾逗号、重复属性。
- 属性名区分大小写，枚举按本文所列英文 `camelCase` 填写。不要依赖大小写宽容处理。
- `id` 为 1–96 字符的稳定标识，只用 ASCII 字母、数字、`.`、`_`、`-`，区分大小写；在整个启用包中必须唯一。建议使用自己的 Mod 前缀，避免意外替换同名卡。
- 普通数值使用 JSON 整数；表达式字段另可使用字符串。无小数、布尔转数值、任意脚本或函数调用。
- 来源 `core` 表示可参与基础构筑，不表示一定是官方卡。`token` 为衍生卡，`test` 为开发测试卡。
- 编译成功只说明定义合法；是否可入组还取决于房间协议的职业、来源和数量要求。

## 3. 卡牌公共字段与三种类型

### 公共字段

| 字段 | 类型／默认值 | 说明 |
| --- | --- | --- |
| `schemaVersion` | 字符串，必填 | 固定为 `eota.card/v2` |
| `kind` | 必填 | `minion`、`field`、`spell` |
| `id` | 字符串，必填 | 卡牌原型 ID；对战实例 ID 由服务器创建 |
| `source` | `core` | `core`、`token`、`test` |
| `profession` | `neutral` | `neutral`、`guardian`、`arcanist`、`artisan`、`hunter`、`soulbinder` |
| `cost` | 整数，0 | 原始费用；权威值可为负，支付使用非负费用 |
| `tags` | 字符串数组，`[]` | 自定义过滤标签，如 `mechanical`、`beast`；重复值规范化去重 |
| `effects` | 数组，`[]` | 最多 32 个效果根 |
| `storedCharge` | 非负整数，0 | 随从或场地提供的蓄能；法术不能设为非零 |
| `nameLocalizationKey` | 字符串，可省略 | 名称键，详见第 11 节 |
| `descriptionLocalizationKey` | 字符串，可省略 | 规则说明键 |
| `texturePath` | 字符串，空 | 插图资源路径，不参与规则哈希 |

`soulbinder` 在内容枚举中存在，但当前客户端的新建职业入口仅提供守卫、奥术师、工匠和猎人。编译一种职业不等于已经提供完整客户端职业玩法。

### 随从 `minion`

`attack`、`health` 是必填整数，初始 `health` 必须大于 0；不填写“当前血量”。`keywords` 可省略。

| 关键词 | 名称 | 初始 JSON 值 |
| --- | --- | --- |
| swift | 迅捷 | `"swift"` |
| guard | 守备 | `"guard"` |
| slow | 迟缓 | `{"kind":"slow","turns":N}`，N 为正整数 |
| replace / replaceable | 替换／可替换 | `"replace"` / `"replaceable"` |
| lifesteal | 吸血 | `"lifesteal"` |
| skirmisher | 游击 | `"skirmisher"` |
| pursuit | 追猎 | `"pursuit"` |
| firstStrike | 先攻 | `"firstStrike"` |
| execute | 斩杀 | `"execute"` |

普通随从入场当回合不能主动攻击，但能防守反击。迅捷取消入场攻击限制；守备禁止主动攻击，保留防守；迟缓次数大于 0 时连防守也禁止，敌人会绕过它攻击英雄。迅捷不能覆盖守备、迟缓、路线冻结或场地禁攻。战斗开始时已有的迟缓在战斗结束后减 1，即使没有交战也会消耗；战斗中刚获得或移除后重新获得的迟缓不在当次扣减。

单方先攻先造成随从伤害，目标死亡便不能反击；双方都有先攻时在最终伤害帧同时伤害。斩杀关键词在最终战斗伤害帧消灭对撞随从，攻击为 0 也有效，但来源若已死于先攻便不能再贡献斩杀。它不会让打脸自动斩杀英雄；对英雄的明确斩杀效果使用第 7 节的 `kill`。吸血只由对英雄的战斗伤害产生，对随从伤害不吸血；同帧伤害与治疗合并后再判定英雄死亡。

游击在本路有敌方随从时尝试移向没有敌方随从的邻路；追猎在本路没有敌方随从时尝试移向有敌方随从的邻路。目的地必须允许移动且本方随从槽为空，方向选择和多人争位由对战协议解决。替换与可替换用于允许覆盖本方已占据槽位，具体见第 7 节。

**卡牌案例：守门石像**，2 费 2/5，守备、迟缓 1。入场当回合敌人可以绕过它；迟缓清除后它能反击，但仍不会主动攻击。在随从的效果 JSON 中填写：

<!-- mod-example: minion-abilities -->
```json
{
  "keywords": [
    "guard",
    {
      "kind": "slow",
      "turns": 1
    }
  ],
  "effects": []
}
```

不能任意创造新关键词；可用 `tags` 做分类，再组合已有效果。

### 场地 `field`

| 字段 | 默认值 | 说明 |
| --- | --- | --- |
| `lifetime` | 必填 | 有限为 `{"kind":"finite","energy":3}`；永久为 `{"kind":"permanent"}` |
| `preventsActiveAttacksInLane` | false | 阻止所在路线双方随从主动攻击 |
| `keywords` | `[]` | 只支持 `"replace"`、`"replaceable"` |

耐久就是强度，有限场地只维护 `lifetime.energy` 这一份值，通常设置为正整数；非正值不会让场地永久存在。永久场地禁止填写 `energy`，也不能用效果修改其耐久。

**卡牌案例：静修营地**，4 费、耐久 3，禁止本路主动攻击并提供蓄能 2。基本属性选择有限场地并填写耐久 3，效果 JSON 如下。禁攻影响双方，本方蓄能只服务本方充能；耐久在回合清理时按协议消耗，耗尽后离场。

<!-- mod-example: field-abilities -->
```json
{
  "preventsActiveAttacksInLane": true,
  "storedCharge": 2,
  "effects": []
}
```

### 法术 `spell`

| 字段 | 默认值 | 说明 |
| --- | --- | --- |
| `speed` | `fast` | `fast` 快速／`slow` 慢速 |
| `targetScope` | `global` | `lane` 出牌时选一路，`global` 不选路 |

法术只使用 `selfSpellCast` 根；`self` 不代表施法者英雄，要选择英雄请用 `friendlyHero`。全局法术没有来源路线，不能用 `lane.ether`，跨路线选择必须明确 `scope: "all"`。`targetScope` 不提供点击某个随从的任意目标系统：效果仍按 JSON 选择器取目标。

**卡牌案例：流星雨**，5 费快速全局法术，对所有敌方随从造成 2 点伤害。基本属性选择 `fast`、`global`，效果 JSON 使用全场选择器：

<!-- mod-example: spell-abilities -->
```json
{
  "effects": [
    {
      "id": "meteor",
      "trigger": {
        "kind": "selfSpellCast"
      },
      "body": {
        "kind": "damage",
        "target": {
          "kind": "enemyMinions",
          "scope": "all"
        },
        "amount": 2
      }
    }
  ]
}
```

## 4. 效果根与全部触发器

每个根有 `id`、`trigger`、`body` 三个必填字段；`condition` 是可选的整根条件；充能根还必须有非负整数 `charge`。效果 ID 在卡内唯一，最长 96 字符，按 Unicode NFC 规范化。多个根按 ID 排序，调换 `effects` 数组位置不能代替顺序效果。

`trigger` 可含 `kind`、`scope`、`subjectType`、`otherOnly`。默认范围 `lane`，只可改成 `all`。观察随从／场地事件时用 `subjectType: "minion"` 或 `"field"` 明确事件类型；`otherOnly: true` 排除来源自身。没有事件主体的阶段根不要填写主体类型。

| `trigger.kind` | 触发含义与约束 |
| --- | --- |
| `selfEntered` | 自身实体入场 |
| `friendlyEntered` / `enemyEntered` | 友方／敌方实体入场 |
| `selfSpellCast` | 当前法术结算，法术的唯一普通根 |
| `friendlySpellCast` | 友方路线法术结算；全局法术无路线，不触发此观察 |
| `selfDamaged` | 自身随从本帧受到正伤害；不能用于场地 |
| `selfDied` / `selfLeft` | 自身死亡／普通离场；死亡也会产生普通离场事实，放逐除外 |
| `friendlyDied` / `enemyDied` | 友方／敌方实体死亡 |
| `friendlyLeft` / `enemyLeft` | 友方／敌方实体普通离场，包含死亡、回手、替换离场，不含放逐 |
| `replacementEntered` | 自身作为替换者入场，可读取 `replaced.*` |
| `entryStage` | 入场阶段的阶段效果，不等于自身刚入场 |
| `combatStage` | 战斗阶段效果，在冻结参战关系前执行；不要求来源具备攻击资格 |
| `combat` | 自身随从实际参与战斗 |
| `minionCombat` | 自身参与随从对撞，`eventTarget` 为本次对撞随从；打脸不触发 |
| `attack` | 自身随从主动攻击 |
| `friendlyCombat` / `enemyCombat` | 观察友方／敌方随从参战 |
| `enemyAttack` | 观察敌方随从主动攻击 |
| `combatDamage` | 随从战斗伤害贡献，`event.amount` 为正战斗伤害值；只能通过英雄 `damage`、`parallel`、`ifElse` 贡献同帧伤害 |
| `preCombatCharge` | 战斗前充能，必须有根字段 `charge` |
| `endTurnCharge` | 回合结束充能，必须有根字段 `charge` |
| `turnEnd` | 随从／场地回合结束效果 |

观察者的触发范围与动作的选取范围独立。例如根写 `scope: "all"`、动作写 `enemyMinions` 而省略 scope，含义仍是全场事件触发后，影响来源本路敌方随从。

**卡牌案例：战地教官**，5 费 2/4：其他友方随从在任意路入场时，令该随从攻击 +1。`scope: "all"` 扩大观察范围，`eventSubject` 精确指向刚入场的那一个随从：

<!-- mod-example: minion-abilities -->
```json
{
  "effects": [
    {
      "id": "train",
      "trigger": {
        "kind": "friendlyEntered",
        "scope": "all",
        "subjectType": "minion",
        "otherOnly": true
      },
      "body": {
        "kind": "modifyNumber",
        "target": {
          "kind": "eventSubject"
        },
        "attribute": "attack",
        "operation": "add",
        "amount": 1
      }
    }
  ]
}
```

死亡会同时产生 died 与 left 两类事实，同时定义两种根会分别触发；banish 使用独立放逐事实，不触发这两类观察。实体上的 friendlySpellCast 只观察有路线的法术；附给玩家英雄的观察根若使用 scope=all，也可以观察全局法术。

**卡牌案例：余烬侍者**，3 费 3/1：死亡时，对敌方英雄造成等于自身攻击的伤害。`source.attack` 可读取死亡时冻结的攻击；此时再选 `self` 已找不到可修改的在场实体。改成 `selfLeft` 会连回手、替换离场一起触发，放逐仍不触发：

<!-- mod-example: minion-abilities -->
```json
{
  "effects": [
    {
      "id": "last-flame",
      "trigger": {
        "kind": "selfDied"
      },
      "body": {
        "kind": "damage",
        "target": {
          "kind": "enemyHero"
        },
        "amount": "source.attack"
      }
    }
  ]
}
```

### 对撞与战斗伤害贡献

`minionCombat` 用于已经确定的随从对撞，可通过 `eventTarget` 选择具体对手。`combatDamage` 用于把额外英雄伤害并入本次战斗伤害帧，不能在其中召唤、斩杀、抽牌或启动 sequence。

**卡牌案例：散射炮手**，5 费 3/4：造成正的随从战斗伤害时，同时对敌方英雄造成等量伤害。若本次随从伤害贡献为 3，英雄也受到 3 点；炮手在这同一帧被反击消灭不会撤销已产生的贡献：

<!-- mod-example: minion-abilities -->
```json
{
  "effects": [
    {
      "id": "splash",
      "trigger": {
        "kind": "combatDamage"
      },
      "body": {
        "kind": "damage",
        "target": {
          "kind": "enemyHero"
        },
        "amount": "event.amount"
      }
    }
  ]
}
```

### 充能与蓄能

`storedCharge` 是实体提供的蓄能；`charge` 是某个效果的充能需求。按同一玩家、同一充能时机汇总需求，使用其未用费用与友方在场实体蓄能判断，这一批效果全部满足才发动；不会扣除这些资源。时机明确选择战斗前或回合结束；效果根写 `charge`，修改实体充能需求时才使用属性 `chargeRequirement`。

**卡牌案例：蓄能射手**，3 费 1/3，自带蓄能 1，战斗前充能 2 时对本路敌方随从造成 1 点伤害。若本方只有这一条充能根，剩余费用 1 加蓄能 1 就够；若另有同阶段充能 2 的根，合计需求变为 4，资源只有 2 时两条都不会触发：

<!-- mod-example: minion-abilities -->
```json
{
  "storedCharge": 1,
  "effects": [
    {
      "id": "charged-hit",
      "trigger": {
        "kind": "preCombatCharge"
      },
      "charge": 2,
      "body": {
        "kind": "damage",
        "target": {
          "kind": "enemyMinions"
        },
        "amount": 1
      }
    }
  ]
}
```

## 5. 目标选择器与过滤

动作的 `target` 是选择器对象，例如 `{"kind":"enemyMinions","scope":"all"}`。目标必须与动作兼容，空目标不会自动转为敌方英雄。

| `target.kind` | 返回类型 | 用途／可用上下文 |
| --- | --- | --- |
| `self` | minion / field | 在场的来源实体，法术无此目标 |
| `friendlyHero` / `enemyHero` | hero | 来源控制方／对方英雄 |
| `friendlyMinions` / `enemyMinions` / `allMinions` | minion | 友方／敌方／双方随从 |
| `friendlyFields` / `enemyFields` | field | 对应方场地 |
| `friendlyLanes` / `enemyLanes` | lane | 对应方的路线资源；以太按玩家独立 |
| `friendlyHand` / `enemyHand` | card | 对应方手牌实例 |
| `friendlyDeck` / `enemyDeck` | card | 对应方牌库中的卡牌实例 |
| `eventSubject` | 已声明的事件主体类型 | 事件主体仍在场时可作为目标；观察事件时建议明确 subjectType |
| `eventTarget` | minion | 仅 `minionCombat` 的具体对撞对手 |
| `targets` | 当前绑定类型 | `forEach` / `retarget` 内绑定的当前目标 |
| `previousAffected` | 显式 targetType | Sequence 前一步实际影响的目标 |
| `previousCreated` | 显式 targetType | Sequence 前一步创建的对象 |
| `previousRemoved` | 显式 targetType | 前一步移除对象的身份；不能据此复活或继续修改已离场实体 |

路线与场上实体集合支持 scope：`lane`（默认，本路）、`all`（所有路）、`adjacent`（左右邻路）、`otherLanes`（除本路外）。位置基准始终为原始来源／法术选择的路线，`retarget` 不把来源改成目标，也不将“本路”自动挪到目标所在路。英雄、手牌和牌库没有路线筛选。

目标按稳定身份排序；手牌／牌库选择器不是抽牌操作，不会改变牌库顺序。`eventSubject` 与 `self` 只能选择仍存在的实体；需要读取已死亡对象数据时用冻结的 `event.*`、`source.*` 或 `cardMatches`。

### 卡牌过滤 `filter`

过滤对象可组合 `kind`（minion/field/spell）、`profession`、`tag`，多个条件为 AND。可用于场上随从／场地、手牌／牌库及 `draw.filter`，不能用于英雄或路线。`tag` 对应卡牌顶层 tags 的一项，区分大小写。没有费用、名称、ID 或自定义表达式过滤字段。

示例：`{"kind":"friendlyHand","filter":{"kind":"minion","tag":"mechanical"}}` 选择所有友方机械随从手牌。要修改攻击或生命，应加 `kind: "minion"`，否则选到法术时不能读取或修改随从属性。

**卡牌案例：备件检修**，3 费全局法术，让手中的机械随从费用 -1；不影响非机械随从或机械标签的法术。费用的权威值可为负，实际支付最低为 0：

<!-- mod-example: spell-abilities -->
```json
{
  "effects": [
    {
      "id": "repair-discount",
      "trigger": {
        "kind": "selfSpellCast"
      },
      "body": {
        "kind": "modifyNumber",
        "target": {
          "kind": "friendlyHand",
          "filter": {
            "kind": "minion",
            "tag": "mechanical"
          }
        },
        "attribute": "cardCost",
        "operation": "add",
        "amount": -1
      }
    }
  ]
}
```

## 6. 条件

根的 `condition` 或 `ifElse.condition` 使用以下结构。条件不成立时不会执行对应 body；不能在字符串里直接写 `a > b`。

| `kind` | 必填字段 | 含义 |
| --- | --- | --- |
| `compare` | `left`、`operator`、`right` | 比较两个整数／表达式 |
| `exists` | `target` | 选择器至少选到一个对象 |
| `hasKeyword` | `target`、`keyword` | 至少一个选中对象有指定关键词；场地仅支持替换类关键词 |
| `cardMatches` | `subject`、`filter` | 按卡牌定义检查类型／职业／标签 |
| `all` | `children` 条件数组 | 所有条件成立 |
| `not` | `condition` | 对内部条件取反 |

比较符为 `equal`、`notEqual`、`less`、`lessOrEqual`、`greater`、`greaterOrEqual`。没有直接的 `any`／`or` 节点；可用“并非所有条件都不成立”组合，或嵌套 ifElse。

`cardMatches.subject` 为 `source`、`event`、`replaced`、`target`。event 要求可用且有类型的事件主体；replaced 仅用于 replacementEntered；target 需要先绑定卡牌或实体。event 和 replaced 的定义引用保留离场时的身份。

**卡牌案例：部族祭司**，3 费 2/3：其他友方野兽在任意路死亡时，若祭司本路没有敌方随从，治疗本方英雄 2 点。死亡对象的标签从冻结的 event 定义读取，不需要它仍留在场上；两个条件以 all 组合：

<!-- mod-example: minion-abilities -->
```json
{
  "effects": [
    {
      "id": "mourn-beast",
      "trigger": {
        "kind": "friendlyDied",
        "scope": "all",
        "subjectType": "minion",
        "otherOnly": true
      },
      "condition": {
        "kind": "all",
        "children": [
          {
            "kind": "cardMatches",
            "subject": "event",
            "filter": {
              "tag": "beast"
            }
          },
          {
            "kind": "not",
            "condition": {
              "kind": "exists",
              "target": {
                "kind": "enemyMinions"
              }
            }
          }
        ]
      },
      "body": {
        "kind": "heal",
        "target": {
          "kind": "friendlyHero"
        },
        "amount": 2
      }
    }
  ]
}
```

**卡牌案例：共鸣使者**，3 费 1/3，入场时检查本方至少有两条共鸣路，有则对敌方英雄打 3，否则打 1：

<!-- mod-example: minion-abilities -->
```json
{
  "effects": [
    {
      "id": "resonant-hit",
      "trigger": {
        "kind": "selfEntered"
      },
      "body": {
        "kind": "ifElse",
        "condition": {
          "kind": "compare",
          "left": "owner.resonatingLanes",
          "operator": "greaterOrEqual",
          "right": 2
        },
        "then": {
          "kind": "damage",
          "target": {
            "kind": "enemyHero"
          },
          "amount": 3
        },
        "else": {
          "kind": "damage",
          "target": {
            "kind": "enemyHero"
          },
          "amount": 1
        }
      }
    }
  ]
}
```

## 7. 动作与可修改属性

### 基础动作

| `body.kind` | 字段 | 目标与行为 |
| --- | --- | --- |
| `damage` / `heal` | `target`、`amount` | 英雄或随从；非正动作值按 0 处理 |
| `loseHealth` | `target`、`amount` | 随从直接失去生命，不触发受伤效果 |
| `kill` | `target` | 斩杀英雄，或消灭随从／场地；无 amount |
| `modifyNumber` | `target`、`attribute`、`operation`、`amount` | 按下表修改数值 |
| `addKeyword` | `target`、`keyword` | 随从／随从卡；slow 另需正数 `amount`，其他关键词不要写 amount；场地只支持替换类 |
| `removeKeyword` | `target`、`keyword` | 同上，不写 amount |
| `reduceSlow` | `target`、正数 `amount` | 将在场随从剩余迟缓减少指定回合，最低为 0；归零时同步移除迟缓关键词 |
| `draw` | `target`、`count`，可选 `filter` | 目标是英雄代表的玩家，按牌库次序抽取符合条件的卡 |
| `generate` | `target`、`count`、`prototype` | 向玩家手牌生成指定卡牌 |
| `summon` | `target`、`prototype`，可选 `slot` | 目标是路线；slot 默认为 minion，也可为 field，必须与原型类型相同 |
| `return` | `target` | 在场随从／场地回手，创建新手牌实例 |
| `banish` | `target` | 放逐在场随从／场地，使用独立放逐事实，不触发 died／left 观察 |
| `transform` | `target`、`prototype` | 变形为同类型原型 |
| `replace` | `target`、`prototype` | 效果替换，要求同类型且满足替换／可替换条件 |
| `clearEther` | `target` | 清空所选玩家路线的以太，结果 scalar 为实际清除量 |
| `preventEtherDecay` | `target` | 保护所选玩家路线的下一次自然衰减，不累计保护次数 |
| `laneStatus` | `target`、`status`，可选 `duration` 或 `remove` | 整路冻结／锁闭，详见第 10 节 |
| `grantEffect` | `target`、`effect`，可选 `duration` | 向实体／玩家附加一个完整效果根，详见第 8 节 |

`removeKeyword` 对 slow 会清除全部剩余迟缓；`reduceSlow` 用于逐级预热。同帧多个 `reduceSlow` 的数值相加并在 0 截断，双方提交顺序不影响结果。它不能获得迅捷；新入场随从即使减到 0，也仍须遵守通常的入场攻击限制。

**卡牌案例：预热调校**，2 费快速路线法术，使本路友方机械减少 1 回合迟缓并恢复 2 点生命。迟缓 4 的机械在该次效果后剩迟缓 3；若同一回合进入正常战斗后迟缓再自然减少 1，则回合结束时剩迟缓 2：

<!-- mod-example: spell-abilities -->
```json
{
  "effects": [
    {
      "id": "preheat",
      "trigger": { "kind": "selfSpellCast" },
      "body": {
        "kind": "parallel",
        "children": [
          {
            "kind": "reduceSlow",
            "target": {
              "kind": "friendlyMinions",
              "filter": { "tag": "mechanical" }
            },
            "amount": 1
          },
          {
            "kind": "heal",
            "target": {
              "kind": "friendlyMinions",
              "filter": { "tag": "mechanical" }
            },
            "amount": 2
          }
        ]
      }
    }
  ]
}
```

### 英雄斩杀

`kill` 对英雄直接产生致死结果，同帧治疗或最大生命增加不能挽救。当前帧其他操作仍结算，帧结束后统一判定胜负；双方同帧被斩杀为平局。若这时已终局，后续 sequence 步骤及触发帧不再执行。它不等于造成一个很大的伤害数值，也不触发随从的死亡／离场观察。

**卡牌案例：终焉裁决**，10 费慢速全局法术，若敌方英雄生命不高于 10，斩杀该英雄。这里用生命门槛限制终结范围；`kill` 动作本身可以斩杀任意生命的英雄。内置卡池目前没有这张卡，但 Mod 可以使用此能力；在法术效果 JSON 中填写：

<!-- mod-example: spell-abilities -->
```json
{
  "effects": [
    {
      "id": "execute-hero",
      "trigger": {
        "kind": "selfSpellCast"
      },
      "body": {
        "kind": "forEach",
        "target": {
          "kind": "enemyHero"
        },
        "body": {
          "kind": "ifElse",
          "condition": {
            "kind": "compare",
            "left": "target.health",
            "operator": "lessOrEqual",
            "right": 10
          },
          "then": {
            "kind": "kill",
            "target": {
              "kind": "targets"
            }
          }
        }
      }
    }
  ]
}
```

### 召唤、回手、变形与替换

`prototype` 必须是同一编译包中的有效卡牌 ID，缺少定义会编译失败。召唤需要合法空槽；场地召唤要明确写 `slot: "field"`。生成和成功回手会创建新卡牌实例，重新使用原型属性，不携带原来的实例增益。

**卡牌案例：无人机投放**，2 费路线法术，在本方所选路线召唤教学卡 `MOD-DEMO-DRONE`（1/2、迅捷、机械）。先把该衍生卡定义放进同一内容包。本方该路已有随从时，召唤失败；并列两个召唤争同一空槽时只会按冲突规则选一个成功者：

<!-- mod-example: spell-abilities -->
```json
{
  "effects": [
    {
      "id": "deploy-drone",
      "trigger": {
        "kind": "selfSpellCast"
      },
      "body": {
        "kind": "summon",
        "target": {
          "kind": "friendlyLanes"
        },
        "slot": "minion",
        "prototype": "MOD-DEMO-DRONE"
      }
    }
  ]
}
```

`transform` 保留实体与卡牌实例身份，改为指定同类型原型的属性，清除实例修正、持续增益及附加效果，保留入场回合，不重新触发入场。`replace` 创建新实体和实例，触发被替换者离场及替换者入场；它要求新原型有 replace 或被替换对象有 replaceable。随从不能变成场地，场地也不能变成随从。

**卡牌案例：机械化**，3 费路线法术，将敌方本路随从变成上述 1/2 无人机。原来即使有 +5/+5，也按无人机原型重置；不触发一次无人机入场。如果把动作改为 replace，目标不满足替换条件时会被拒绝：

<!-- mod-example: spell-abilities -->
```json
{
  "effects": [
    {
      "id": "mechanize",
      "trigger": {
        "kind": "selfSpellCast"
      },
      "body": {
        "kind": "transform",
        "target": {
          "kind": "enemyMinions"
        },
        "prototype": "MOD-DEMO-DRONE"
      }
    }
  ]
}
```

同一实体的生命周期冲突统一按放逐 > 消灭／自然死亡 > 回手 > 替换 > 变形处理。相同优先级仍有多个替换／变形候选时，先选目标控制方发出的候选，再按稳定身份排序并使用规则随机选一个；没有本方候选时从全部候选中选。列表顺序不能保证某个效果获胜。

手牌满时，真实抽牌会被烧毁；回手因容量不足失败时，原实体死亡，并触发死亡与普通离场效果。抽空牌库后继续抽牌遵守协议中的疲劳规则，包括帧末判定的“疲劳即死”；生成不触发疲劳。详见 [抽牌与疲劳](GameplayGuide.zh-CN.md#抽牌与疲劳)。

### `modifyNumber`

operation 为 `set`、`add`、`multiply`、`divide`；减法用负数 add。倍率节点的 amount 是整数，除数不能为 0。同帧同一属性统一计算 `(基准值 + 所有 add 之和) × 所有 multiply 的乘积 ÷ 所有 divide 的乘积`，最后向零取整，不按列表逐项取整。基准值取该帧原值；存在 set 时，优先从目标控制方的 set 中选一个，没有本方候选才从全部候选中选，同级使用规则随机裁决。需要前后依赖时使用 sequence。

| 目标类型 | 可用 attribute |
| --- | --- |
| 随从 | `attack`、`baseAttack`、`maximumHealth`、`damageTaken`、`incomingDamageAdjustment`、`chargeRequirement`、`storedCharge` |
| 英雄／玩家 | `maximumHealth`、`maximumCost`、`currentCost`、`nextTurnCost` |
| 场地 | `fieldEnergy`、`chargeRequirement`、`storedCharge` |
| 玩家路线 | `etherActivation` |
| 手／牌库中的卡牌 | `cardCost`；随从卡另支持 `attack`、`maximumHealth` |

baseAttack 和 chargeRequirement 只支持 set。fieldEnergy 只适用于有限场地。maximumHealth 改变上限时，当前生命同步增加或减少相同差值；治疗应使用 heal。damageTaken 表示已损失生命，其修改不触发伤害／治疗事实。incomingDamageAdjustment 对每份正伤害单独加减，再把有效伤害限制为非负；它不影响 loseHealth。

`baseAttack` 修改基础攻击层，保留基础层外的增益。例如原型基础攻击 0、已有永久 +1，临时把 baseAttack 设为 4 得到 5，期限结束回到 1。普通 `attack set` 作用于当前攻击数值；永久基础 set 修改底层，多个有效临时基础覆盖则显示最近一次已提交的层，到期后露出仍有效的前一层。

**卡牌案例：巨像之力**，2 费路线法术，本回合将本路友方随从的基础攻击设为 4。它正对应上面的“保留额外 +1，临时变成 5 攻”场景：

<!-- mod-example: spell-abilities -->
```json
{
  "effects": [
    {
      "id": "colossus",
      "trigger": {
        "kind": "selfSpellCast"
      },
      "body": {
        "kind": "modifyNumber",
        "target": {
          "kind": "friendlyMinions"
        },
        "attribute": "baseAttack",
        "operation": "set",
        "amount": 4,
        "duration": 1
      }
    }
  ]
}
```

## 8. 组合、顺序与临时效果

### 双方没有先后手：同帧操作的可交换规则

**双方玩家没有先后手，拥有相同的规则权限。** 同一帧里，任何一方都不会因为玩家编号、先点击提交或客户端先处理了自己的效果，就获得优先结算权。

引擎已经通过统一优先级、数值合并和同级冲突裁决等规则，让**同帧操作的加法构成可交换幺半群**。这里“加法”指把操作合在一起结算：加入空操作不改变结果，交换收集顺序不改变结果，改变分组方式也不改变结果。Modder 使用这些规则编写卡牌，无需自行实现一套先后手或冲突处理机制。

请注意，可交换不表示冲突操作都会成功。例如同帧的斩杀压过治疗，关键词移除压过添加；多个召唤争同一个空槽，只能按规则选一个成功。优先级来自操作类型与目标关系，双方遵守同一套规则。不要依赖 JSON 列表顺序让某个效果“抢先”或“最后覆盖”，卡牌描述也应考虑目标离场、容量不足及冲突失败的结果。

**卡牌案例：迅捷祝福与迟滞封印**，前者为某随从添加迅捷，后者移除迅捷。如果两者在同一帧结算，最终迅捷被移除；无论哪位玩家施放祝福、哪位玩家先提交，结果都遵循相同的移除优先规则。

需要“先做 A，再依据 A 的结果做 B”时，明确使用 `sequence`，让 B 进入后续帧。每一帧内双方就绪的操作仍共同结算。两个独立的攻击 +1 则会合成 +2，可交换不表示重复效果可以删掉。

| 节点 | 结构 | 执行规则 |
| --- | --- | --- |
| `parallel` | `children` 数组 | 分支同帧开始，兄弟节点看不到彼此本帧尚未提交的结果；分支含 sequence 时等待全部分支完成 |
| `sequence` | `steps` 数组 | 前一步提交后，下一因果帧再执行下一步 |
| `forEach` / `retarget` | `target`、`body` | 同义；逐个绑定目标供 targets／target.* 使用，各目标分支并列执行 |
| `ifElse` | `condition`、`then`、可选 `else` | 条件分支 |
| `loop` | `count`、`body`、可选 `while` 条件 | 按次数逐次提交；每次开始检查 while，loop.index 从 0 开始 |

sequence／loop 在前一步为 Rejected 或 Error 时停止；普通 NoOp 并不必然停止。嵌套分支的部分成功结果不能简单等同于全部失败。loop 超过协议 defaultEffectLoopLimit 会产生错误，不会自动截成最大次数。

### 并列效果读取同一快照

**卡牌案例：蓄力术士**，5 费 2/4：入场时自身攻击 +2，同时按自身攻击对敌方英雄造成伤害。下面写法读取入场时的 2 攻，因此英雄受 2 点伤害，术士变为 4 攻：

<!-- mod-example: minion-abilities -->
```json
{
  "effects": [
    {
      "id": "charge-and-strike",
      "trigger": {
        "kind": "selfEntered"
      },
      "body": {
        "kind": "parallel",
        "children": [
          {
            "kind": "modifyNumber",
            "target": {
              "kind": "self"
            },
            "attribute": "attack",
            "operation": "add",
            "amount": 2
          },
          {
            "kind": "damage",
            "target": {
              "kind": "enemyHero"
            },
            "amount": "source.attack"
          }
        ]
      }
    }
  ]
}
```

若设计文本是“先获得 +2 攻击，然后造成等于自身攻击的伤害”，把该节点改为 `sequence`、`children` 改为 `steps`，后一步在下一帧读取 4 攻，于是造成 4 点伤害。再例如同帧对原攻击 4 的目标提交 +1、×3、÷2，统一得到 `(4+1)×3÷2=7`；分成 sequence 则每步独立提交和取整，可能得到不同结果。

### 先抽牌，再强化那张牌

**卡牌案例：检索维修**，3 费慢速全局法术，先抽一个机械随从，再令实际抽到的那张卡获得 +1/+1。满手烧掉的牌不会出现在 previousAffected 中；牌库中没有机械随从时，也不会改为强化任意手牌。

<!-- mod-example: spell-abilities -->
```json
{
  "effects": [
    {
      "id": "draw-and-improve",
      "trigger": {
        "kind": "selfSpellCast"
      },
      "body": {
        "kind": "sequence",
        "steps": [
          {
            "kind": "draw",
            "target": {
              "kind": "friendlyHero"
            },
            "count": 1,
            "filter": {
              "kind": "minion",
              "tag": "mechanical"
            }
          },
          {
            "kind": "parallel",
            "children": [
              {
                "kind": "modifyNumber",
                "target": {
                  "kind": "previousAffected",
                  "targetType": "card"
                },
                "attribute": "attack",
                "operation": "add",
                "amount": 1
              },
              {
                "kind": "modifyNumber",
                "target": {
                  "kind": "previousAffected",
                  "targetType": "card"
                },
                "attribute": "maximumHealth",
                "operation": "add",
                "amount": 1
              }
            ]
          }
        ]
      }
    }
  ]
}
```

previous 表示同一 sequence 的紧前一步结果；不能跨效果根共享，后续步骤会覆盖它。previousAffected／Created／Removed 必须明确 targetType：`hero`、`minion`、`field`、`lane` 或 `card`。例如回手得到新手牌可用 previousCreated + card，召唤得到在场随从可用 previousCreated + minion。已经移除的旧身份不能再次作为活实体使用。

同帧的手牌容量先分配给回手，再给生成，最后给抽牌；同类争用剩余容量时统一选取。较晚 sequence 帧的请求不会追溯抢占前一帧已提交的手牌。满手抽牌会烧掉抽出的那张牌，生成失败不会挤掉已有手牌；回手因容量不足失败时，原实体死亡，触发死亡与普通离场效果，不创建回手牌。该回手步骤仍算失败，依赖它的 sequence 后续步骤停止，死亡触发则正常进入后续帧。

### 回手失败也会触发死亡效果

**卡牌案例：归乡信使**，3 费 2/3：入场时生成一张教学无人机到手牌，然后自身回手；死亡时对敌方英雄造成 2 点伤害。若原本满手，打出信使空出的一个位置会先被无人机填满；随后回手失败，信使死亡，死亡效果在后续帧造成 2 点伤害。如果手牌还有额外空位，信使正常回手，不触发死亡效果。

<!-- mod-example: minion-abilities -->
```json
{
  "effects": [
    {
      "id": "homeward",
      "trigger": {
        "kind": "selfEntered"
      },
      "body": {
        "kind": "sequence",
        "steps": [
          {
            "kind": "generate",
            "target": {
              "kind": "friendlyHero"
            },
            "prototype": "MOD-DEMO-DRONE",
            "count": 1
          },
          {
            "kind": "return",
            "target": {
              "kind": "self"
            }
          }
        ]
      }
    },
    {
      "id": "farewell",
      "trigger": {
        "kind": "selfDied"
      },
      "body": {
        "kind": "damage",
        "target": {
          "kind": "enemyHero"
        },
        "amount": 2
      }
    }
  ]
}
```

### 按每个目标自身数值计算

**卡牌案例：巡回医师**，5 费 1/4，入场时把其他路的友方随从分别治疗至满血。直接给动作写 `target` 不会绑定表达式的 `target.*`；先 forEach，再在 body 中使用 targets。例如两个目标分别缺 1、3 点生命，就分别请求治疗 1、3 点：

<!-- mod-example: minion-abilities -->
```json
{
  "effects": [
    {
      "id": "heal-others",
      "trigger": {
        "kind": "selfEntered"
      },
      "body": {
        "kind": "forEach",
        "target": {
          "kind": "friendlyMinions",
          "scope": "otherLanes"
        },
        "body": {
          "kind": "heal",
          "target": {
            "kind": "targets"
          },
          "amount": "target.maxHealth - target.health"
        }
      }
    }
  ]
}
```

### 按次数重复，逐次检查条件

**卡牌案例：谨慎汲能**，3 费全局法术，最多重复 3 次：“若本方英雄生命大于 2，对本方英雄造成 1 点伤害并抽 1 张牌。”每次循环先检查 while，同次的伤害与抽牌并列；下一次读取上次已提交的生命。例如从 4 点生命开始，牌库充足且没有其他效果介入时只执行 2 次便停止：

<!-- mod-example: spell-abilities -->
```json
{
  "effects": [
    {
      "id": "careful-siphon",
      "trigger": {
        "kind": "selfSpellCast"
      },
      "body": {
        "kind": "loop",
        "count": 3,
        "while": {
          "kind": "compare",
          "left": "owner.health",
          "operator": "greater",
          "right": 2
        },
        "body": {
          "kind": "parallel",
          "children": [
            {
              "kind": "damage",
              "target": {
                "kind": "friendlyHero"
              },
              "amount": 1
            },
            {
              "kind": "draw",
              "target": {
                "kind": "friendlyHero"
              },
              "count": 1
            }
          ]
        }
      }
    }
  ]
}
```

牌库为空时仍可能因疲劳死亡；while 检查的是迭代开始快照，不会预知该次抽牌的结果。`count` 在循环开始时求值一次，while 每次迭代重新检查；需要按前一步消耗的资源决定次数，可参考第 10 节“以太回响”。

### duration 与附加效果

duration 是整数／表达式，正数在回合结束效果之后的清理阶段按协议衰减；默认协议下 duration=1 表示持续至本回合清理。非正值不产生有效持续效果。不是任意动作都能添加 duration：

- 随从 attack、maximumHealth、incomingDamageAdjustment 的 **add**。
- 随从非 slow 关键词的添加／移除。
- 随从 baseAttack 的 **set**，随从／场地 chargeRequirement 的 **set**。
- grantEffect、laneStatus；这两者省略 duration 表示永久持续到正常移除。

slow 使用独立迟缓计数，不能再套临时 duration；用 `reduceSlow` 减少剩余回合，或用 `removeKeyword` 全部移除。伤害、治疗、卡牌费用和场地耐久等操作不接受临时 duration。

**卡牌案例：临阵奋勇**，2 费快速路线法术，本回合令本路友方随从攻击 +2 并获得迅捷。每项贡献分别带期限，默认清理阶段移除；即使在慢速法术阶段获得 duration=1，也会在本回合清理时到期。迅捷仍不能覆盖守备、迟缓或禁攻：

<!-- mod-example: spell-abilities -->
```json
{
  "effects": [
    {
      "id": "rally",
      "trigger": {
        "kind": "selfSpellCast"
      },
      "body": {
        "kind": "parallel",
        "children": [
          {
            "kind": "modifyNumber",
            "target": {
              "kind": "friendlyMinions"
            },
            "attribute": "attack",
            "operation": "add",
            "amount": 2,
            "duration": 1
          },
          {
            "kind": "addKeyword",
            "target": {
              "kind": "friendlyMinions"
            },
            "keyword": "swift",
            "duration": 1
          }
        ]
      }
    }
  ]
}
```

临时增益到期只移除自身贡献，不把整项属性恢复成施放前快照。临时最大生命到期会同步减少当前生命，可能导致死亡。关键词临时修改保存永久基线，同帧移除优先；永久移除会清理相应临时添加，避免期限结束后重新出现。

`grantEffect.effect` 填写与普通 effects 元素相同的完整根。承载者成为附加效果的来源：附给随从时，内部 self 就是该随从。不可附加 selfSpellCast、充能根或 combatDamage。附给英雄时，支持 turnEnd，以及 friendlyDied、enemyDied、friendlyLeft、enemyLeft、friendlyEntered、enemyEntered、enemyAttack、friendlySpellCast、friendlyCombat、enemyCombat 这些观察根。

**卡牌案例：侵血毒剂**，3 费慢速全局法术，使敌方英雄连续三个回合结束时受到 2 点伤害。外层 enemyHero 选择敌人，附加效果内部的 friendlyHero 则指中毒英雄自己，不能再次写 enemyHero，否则会伤到施毒方。英雄不使用 self；英雄毒伤用 damage，loseHealth 仍只接受随从目标：

<!-- mod-example: global-spell-abilities -->
```json
{
  "effects": [
    {
      "id": "apply-blood-poison",
      "trigger": { "kind": "selfSpellCast" },
      "body": {
        "kind": "grantEffect",
        "target": { "kind": "enemyHero" },
        "duration": 3,
        "effect": {
          "id": "bloodPoison",
          "trigger": { "kind": "turnEnd" },
          "body": {
            "kind": "damage",
            "target": { "kind": "friendlyHero" },
            "amount": 2
          }
        }
      }
    }
  ]
}
```

英雄的 turnEnd 附加效果与随从回合末效果在同一批收集，然后才收集场地回合末效果。双方英雄的毒伤同时产生并在帧末判定胜负；同帧治疗可以抵消伤害，较晚的场地治疗不能挽救已在此前帧中死亡的英雄。duration 在触发后统一衰减，因此施毒当回合算第一次，共触发三次。重复施毒分别附加、分别计时，会叠加伤害；效果不依赖原法术或施毒随从继续留场。

**卡牌案例：恢复教官**，4 费 2/4，其他友方随从入场后，使其获得“本回合结束时治疗自身 1 点”。外层 eventSubject 是新入场随从，内层 self 则是承载能力的该随从；不是治疗教官：

<!-- mod-example: minion-abilities -->
```json
{
  "effects": [
    {
      "id": "teach-recovery",
      "trigger": {
        "kind": "friendlyEntered",
        "scope": "all",
        "subjectType": "minion",
        "otherOnly": true
      },
      "body": {
        "kind": "grantEffect",
        "target": {
          "kind": "eventSubject"
        },
        "duration": 1,
        "effect": {
          "id": "recover",
          "trigger": {
            "kind": "turnEnd"
          },
          "body": {
            "kind": "heal",
            "target": {
              "kind": "self"
            },
            "amount": 1
          }
        }
      }
    }
  ]
}
```

回合结束触发先执行，然后清理移除这个临时能力；因此 duration=1 的恢复可以正常发动一次。省略 duration 则能力持续保留，直到承载实体离场、变形或其他规则将其移除。

## 9. 整数表达式

amount、count、duration 和 compare 两侧可写整数或算术字符串。例如 `2`、`"source.attack + 1"`、`"(target.maxHealth - target.health) / 2"`。支持括号、一元负号、`+ - * /`；整数除法向零取整。没有 `%`、函数、随机选择、字符串运算、赋值或用户定义变量。

| 变量 | 可用位置与含义 |
| --- | --- |
| `owner.health` / `owner.maxHealth` | 来源控制方英雄当前／最大生命 |
| `owner.cost` / `owner.maxCost` | 来源控制方当前费用／费用上限 |
| `owner.resonatingLanes` | 来源控制方以太等级至少为 1 的路线数量 |
| `lane.ether` | 来源控制方本路以太，需要来源路线；全局法术不能用 |
| `source.attack` / `source.health` / `source.maxHealth` / `source.slow` | 随从来源；离场后可读冻结来源数据 |
| `source.energy` | 有限场地来源的耐久值，永久场地读取会报错 |
| `event.amount` | selfDamaged／combatDamage 的实际事件伤害量 |
| `event.attack` / `event.health` / `event.maxHealth` / `event.slow` | 事件主体为随从且当前事件确有主体 |
| `event.energy` | 事件主体为有限场地 |
| `replaced.attack` / `replaced.health` / `replaced.maxHealth` | 随从 replacementEntered 的被替换者冻结数据 |
| `target.attack` | 绑定随从或随从卡 |
| `target.health` / `target.maxHealth` | 绑定英雄／随从／随从卡；卡牌读取原型加实例修正后的生命 |
| `target.slow` | 绑定在场随从 |
| `target.cost` | 绑定手牌／牌库卡牌 |
| `target.energy` | 绑定有限场地 |
| `target.ether` | 绑定某一玩家的路线资源 |
| `previous.scalar` | 前一步回执合并后的实际数值结果，含义由动作决定 |
| `previous.affectedCount` / `previous.createdCount` / `previous.removedCount` | 前一步结果集合的数量 |
| `loop.index` | 当前循环迭代索引，从 0 开始 |

target.* 必须在 forEach／retarget 绑定内使用。source 与 owner 不会因绑定目标而改变。某些变量虽能通过类型检查，运行时仍可能缺少对象，例如用事件数据处理一个没有实体主体的阶段事件；应选择正确触发器，而不是假设缺失数据自动变成 0。

**卡牌案例：反击枪手**，5 费 2/4，自身受伤后，对敌方英雄造成受伤值一半向上取整的伤害。这里只处理正整数，用 `(event.amount + 1) / 2` 表达；本帧受伤 3 点就反击 2 点。`event.amount` 来自已经提交的受伤事实，不从当前剩余生命反推：

<!-- mod-example: minion-abilities -->
```json
{
  "effects": [
    {
      "id": "retaliate",
      "trigger": {
        "kind": "selfDamaged"
      },
      "body": {
        "kind": "damage",
        "target": {
          "kind": "enemyHero"
        },
        "amount": "(event.amount + 1) / 2"
      }
    }
  ]
}
```

## 10. 以太、冻结与锁闭

以太属于“某位玩家的某条路”。使用 friendlyLanes 改本方以太，enemyLanes 改对方；不是对双方一起活化。数值受协议 minEtherActivation／maxEtherActivation 限制，当前默认 0–3。

**卡牌案例：潮汐护符**，2 费路线法术，令己方本路以太 +1 并保护下一次自然衰减。对方同一路的以太保持原值：

<!-- mod-example: spell-abilities -->
```json
{
  "effects": [
    {
      "id": "activate",
      "trigger": {
        "kind": "selfSpellCast"
      },
      "body": {
        "kind": "parallel",
        "children": [
          {
            "kind": "modifyNumber",
            "target": {
              "kind": "friendlyLanes"
            },
            "attribute": "etherActivation",
            "operation": "add",
            "amount": 1
          },
          {
            "kind": "preventEtherDecay",
            "target": {
              "kind": "friendlyLanes"
            }
          }
        ]
      }
    }
  ]
}
```

### 消耗以太后按实际消耗重复

**卡牌案例：以太回响**，2 费慢速路线法术，清空本方本路以太，每清除 1 级便对本路敌方随从造成 1 点伤害。先 clearEther，再让 loop 的 count 读取 previous.scalar；清除 3 级会逐次打 3 次，各次之间可以触发受伤效果。若没有以太则不打击：

<!-- mod-example: spell-abilities -->
```json
{
  "effects": [
    {
      "id": "ether-echo",
      "trigger": {
        "kind": "selfSpellCast"
      },
      "body": {
        "kind": "sequence",
        "steps": [
          {
            "kind": "clearEther",
            "target": {
              "kind": "friendlyLanes"
            }
          },
          {
            "kind": "loop",
            "count": "previous.scalar",
            "body": {
              "kind": "damage",
              "target": {
                "kind": "enemyMinions"
              },
              "amount": 1
            }
          }
        ]
      }
    }
  ]
}
```

若在 clearEther 与 loop 之间插入其他步骤，previous 就会变为那一步的结果，不再代表清空的以太。

### 冻结与锁闭影响双方

冻结／锁闭属于**整条路线**，无论目标选 friendlyLanes 还是 enemyLanes，均影响双方。laneStatus.status 为 `frozen`（无法攻击）或 `locked`（无法进出）；目标必须是路线。省略 duration 永久生效，移除写 `remove: true` 且不能同时写 duration。同状态叠加取最大持续时长，永久覆盖有限；同帧移除优先。

**卡牌案例：寒霜符文**，2 费快速路线法术，本回合冻结所选路线，并令本方该路以太 +1。冻结影响双方，以太增加只影响自己：

<!-- mod-example: spell-abilities -->
```json
{
  "effects": [
    {
      "id": "frost-rune",
      "trigger": {
        "kind": "selfSpellCast"
      },
      "body": {
        "kind": "parallel",
        "children": [
          {
            "kind": "laneStatus",
            "target": {
              "kind": "friendlyLanes"
            },
            "status": "frozen",
            "duration": 1
          },
          {
            "kind": "modifyNumber",
            "target": {
              "kind": "friendlyLanes"
            },
            "attribute": "etherActivation",
            "operation": "add",
            "amount": 1
          }
        ]
      }
    }
  ]
}
```

### 解锁后再进入

**卡牌案例：破封增援**，2 费路线法术，解除本路锁闭，再召唤教学无人机。解锁需要先提交，故使用 sequence；写成 parallel 时召唤读取的仍是锁闭快照，会失败。若随从槽已经占满，解锁成功也不能强行覆盖：

<!-- mod-example: spell-abilities -->
```json
{
  "effects": [
    {
      "id": "break-seal",
      "trigger": {
        "kind": "selfSpellCast"
      },
      "body": {
        "kind": "sequence",
        "steps": [
          {
            "kind": "laneStatus",
            "target": {
              "kind": "friendlyLanes"
            },
            "status": "locked",
            "remove": true
          },
          {
            "kind": "summon",
            "target": {
              "kind": "friendlyLanes"
            },
            "prototype": "MOD-DEMO-DRONE"
          }
        ]
      }
    }
  ]
}
```

## 11. 本地化、插图与内容包

### 名称与说明

卡牌顶层不放 `name` 或 `description`。编辑器保存会写入 `card.{id}.name` 与 `card.{id}.description`，并更新文本字典。

直接调用编译器时，省略键的默认规则仍为 `minion.{id}.name`、`field.{id}.name`、`spell.{id}.name`，description 同理。为使源文件和编辑器一致，本文例子全部显式写 `card.{id}.*`。

文本字典示例：

<!-- mod-example: texts -->
```json
{
  "card.MOD-DEMO-SENTINEL.name": "教学哨兵",
  "card.MOD-DEMO-SENTINEL.description": "守备。入场时：对敌方英雄造成1点伤害。"
}
```

源码工程中的文本位于 `Content/Source/Localization/zh-CN.json`；独立内容包放在 Texts 字典。当前编辑器要求卡名非空、最多 120 字，说明最多 8192 字。文本不会参与规则哈希，也不会被解析为效果。

### 插图

texturePath 使用客户端能读取的资源，如 `Content/Generated/Art/EOTA-CORE-GUA-MIN-001.png`。规则编译器允许省略图片，但当前客户端启用目录会检查路径必须位于 `Content/Generated/Art/`、以 .png 结尾、实际存在，且不得含 `..` 或反斜杠。因此面向游戏发布的卡牌应填写有效路径。图片不参与规则哈希，缺失图片不会由编译器自动创建。emoji PNG 制作方法见 [素材工具](Content/Authoring-Tools.zh-CN.md)。当前 `.eotapack.json` 不嵌入外部图片，接收者仍需拥有对应资源；最稳妥的起步方式是引用游戏已有图片。

### 三种 JSON 容器不要混用

| 格式 | 顶层 | 用途 |
| --- | --- | --- |
| 单卡源文件 | schemaVersion、kind、id 等 | 编辑器“完整 JSON”或内容编译器输入 |
| 编辑器能力对象 | effects、keywords、storedCharge、preventsActiveAttacksInLane 中适用的字段 | 编辑器“效果 JSON”，没有独立规则哈希 |
| `.eotapack.json` | **Version、RuleHash、Cards、Texts** | 可导入、可启用的内容包；包外层当前使用 PascalCase |

内容包 Version 当前为 1，Cards 是完整单卡对象数组，Texts 是文本字典，RuleHash 由编译后的规则自动计算。不要手填哈希，也不要改完导出包中的规则却保留原哈希；应编辑源卡并重新导出。包载入上限 8 MiB、1–2048 张卡，合并后也不超过 2048 张。

单卡导出会包含递归生成、召唤、变形、替换及嵌套附加效果的引用目标；同 ID 导入会替换原定义，其他卡保留。启用与保存分开，已启动房间继续使用原规则快照。双方联机只核对本局双方牌组并集及其依赖定义，未使用卡可以不同；这不代表程序会自动判断卡牌是否超模。

## 12. 协议、卡组与问题排查

### 对战协议与卡组文件

卡牌 JSON 定义卡牌；牌组和协议是另两种文件。协议源格式为 `{"schemaVersion":"eota.protocol/v0","protocol":{…}}`，完整可编译样例见 [教学协议](Content/Examples/protocol.json)，也可从 [默认协议](../Content/Source/Protocols/default-v0.json) 开始修改。

| protocol 字段 | 可用值与约束 |
| --- | --- |
| requiredDeckSize | 精确总张数，1–256 |
| minCopiesPerCard / maxCopiesPerCard | 已选卡种的副本下限／上限，下限为正且不超过总张数；上限不低于下限且不超过 256 |
| deckConstructionPolicy | coreAndProfessionOrNeutral、coreProfessionOnly、coreAnyProfession；都只开放 core，developmentAnySource 仅供内部开发，不在玩家菜单中 |
| deckExhaustionPolicy | noFatigue、increasingDamage、instantDeath |
| openingHandSize / handLimit | 起手非负、不超过总张数或手牌上限；手牌上限为正 |
| cardsDrawnPerTurn | 0–256 |
| laneCount | 至少 2；centerFirst／outsideFirst 移动竞争要求偶数路 |
| movementConflictPolicy | centerFirst、outsideFirst、allFail |
| movementDirectionPreference | outwardFirst、inwardFirst |
| maxEffectTriggerFramesPerTurn / maxEffectIntentsPerFrame | 默认 256／4096，分别允许 1–100000／1–65536；不是卡牌字段 |

其他资源、衰减参数及格式字段参照完整样例，版本号不是玩法开关。可使用当前客户端保存的 `protocol.json`，或复制本指南的教学协议后修改玩法参数。默认协议源关闭换牌，当前客户端默认打开换牌。

Replay CLI 的牌组文件是 `profession` 与 `cards`，cards 每项为 `id`、`copies`，见 [教学牌组](Content/Examples/deck.json)。客户端自建牌组的保存文件使用 `Name`、`Profession`、`Cards`，内部项 `Id`、`Copies`；建议在牌组工坊保存，不把 CLI 文件直接当作客户端存档。当前卡牌包管理不负责导入协议，玩家在“对战协议”页设置规则。

### 预算与诊断

| 报错／现象 | 优先检查 |
| --- | --- |
| unsupported-schema | schemaVersion 是否与当前卡牌／协议模板一致 |
| unknown-property / duplicate-property | 拼写、大小写、重复键、类型不适用的字段 |
| invalid-keyword | slow 是否用正数 turns 对象；场地是否误填随从关键词 |
| invalid-trigger-source / invalid-charge-requirement | 法术是否只用 selfSpellCast；充能时机与 charge 是否成对 |
| invalid-selector-scope / invalid-variable-scope | 全局法术是否缺少 all；target.* 是否有 forEach 绑定；previous 是否有前一步 |
| effect-target-type-mismatch | 英雄、随从、场地、卡牌、路线是否与动作／属性相符 |
| invalid-rule-content | 重复卡牌 ID、缺少引用目标、错误原型类型或非法初始生命等包级问题 |
| unsupported-duration-operation | 此动作／属性／操作是否支持 duration |
| 内容包校验失败 | 是否手改了导出包规则而没有重新导出、版本是否匹配 |
| 编译成功却不能入组 | source、profession、总张数和副本数量是否符合当前协议 |
| 运行时 empty-target / condition-false | 当前帧确实无目标／条件不成立；通常是正常 NoOp |
| 运行时 expression-arithmetic-error / effect-loop-budget-exceeded | 除零、Int64 溢出或循环次数超过协议上限 |

每卡最多 32 个根、128 个效果／条件节点，深度不超过 16，包含 grantEffect 内嵌图；每表达式最多 512 字符、64 节点，解析嵌套预算 24。运行时还有每回合触发帧、单帧意图与求值工作量限制。合法空目标和条件不成立可无操作，算术或预算错误可能回滚当前帧并使对局失败，编译预览不能替代实际出牌验证。

### 验证示例

在仓库根目录运行：

```powershell
dotnet test tests/Eota.Server.IntegrationTests -c Release --filter FullyQualifiedName~ModAuthoringDocumentationTests
```

验证会编译本文标记的 JSON 示例与独立卡牌、校验教学协议和牌组，并输出 `artifacts/ModAuthoringExamples/demo.eotapack.json` 供编辑器试导入。修改内置源码工程时可另运行 `dotnet run --project tools/Eota.ContentCli -c Release -- check .`；该命令检查 Content/Source 工程，不是读取任意单张 JSON 的命令。

当前字段的实现依据为 [CardContentCompiler](../src/Eota.Content.Compiler/CardContentCompiler.cs)、[EffectCompiler](../src/Eota.Content.Compiler/EffectCompiler.cs)、[EffectDefinitions](../src/Eota.Kernel/Effects/EffectDefinitions.cs) 与 [DesktopContentEditor](../src/Eota.Client.Desktop/DesktopContentEditor.cs)。
