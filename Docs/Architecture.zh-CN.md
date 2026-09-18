# Embers of the Arcane VNext 总体架构

> 状态：建议方案，作为重构实现的工程基线。  
> 规则与验收合同：[ADR 决策索引](ADR/README.md)；客户端功能与操作：[客户端功能与操作](GameplayGuide.zh-CN.md)。  
> 实施顺序：[ImplementationPlan.zh-CN.md](ImplementationPlan.zh-CN.md)。  
> 目标：规则内核使用 Pure C#/.NET；Godot Mono 只是客户端适配器之一；权威服务端使用 Headless .NET；协议允许后续接入浏览器或其他 Web Adapter。

## 1. 结论

VNext 采用六边形架构，并严格区分四类模型：

1. **权威规则模型**：只存在于 `Eota.Kernel`，决定唯一游戏结果。
2. **服务端应用模型**：管理房间、连接、鉴权、持久化和单局串行调度，不参与规则计算。
3. **传输契约模型**：版本化消息 DTO，只表达跨进程协议，不暴露内核对象。
4. **客户端投影模型**：只包含某个观察者有权看到的状态和表现事件，不是权威状态副本。

核心依赖方向固定为：

```text
                         authoring JSON
                               │
                               ▼
                    Eota.Content.Compiler
                               │ compiled rules/protocols
                               ▼
┌──────────────────────────────────────────────────────────────────┐
│                         Eota.Kernel                              │
│ commands → snapshots → intents → reducers → receipts/events     │
└──────────────────────────────────────────────────────────────────┘
                               ▲
                               │
                    Eota.Server.Application
                       ▲                 ▲
                       │                 │
        persistence / hosting       transport adapters
                       │                 │
                       └────────┬────────┘
                                │ versioned contracts
                         Eota.Client.Core
                           ▲           ▲
                           │           │
                    Godot adapter   future Web UI
```

最重要的工程决定是：**效果不再直接修改实体**。任何规则变化都必须先成为原子意图，再按完整冲突组归约并原子提交。Godot 动画、网络到达顺序、容器枚举顺序和系统时间都不能进入这条权威链路。

## 2. 文档与维护约定

### 2.1 规则文档优先级

规则与内容开发使用以下文档：

1. [ADR 决策索引](ADR/README.md)：规则和验收合同，各决策的 Accepted／Proposed 状态以正文为准。
2. 本文：上述规则的工程实现边界。
3. `GameplayGuide.zh-CN.md`：客户端功能、界面操作与存档位置；`CardJsonDesignGuide.zh-CN.md`：当前内容格式与 Mod 编写指南。
4. `CardTable.zh-CN.md`：从当前源内容生成的卡表，不反向定义引擎规则。
5. `ADR/`：规则与实现决策；用户明确修订的决定优先。

### 2.2 内部测试期的格式维护

当前协议尚未定稿，没有需要保留的真实对战记录。只维护当前格式，不保留旧版本读取分支、字段别名或自动升级入口。格式调整时同步修改文档、源内容、开发回放夹具和生成产物。规范编码、确定性哈希及恢复校验继续用于验证当前规则；测试应验证结算语义，不以维持旧格式字节为目标。

### 2.3 内容与表现来源

- `Content/Source/Cards` 保存卡牌 ID、职业、类型、数值、类别和效果定义；[当前卡表](CardTable.zh-CN.md)由这些源文件生成。
- `Content/Source/Localization` 和 `Content/Source/Art` 保存本地化文本及美术配方，生成资源位于 `Content/Generated`。
- `Client/Godot/Scenes` 和 `Client/Godot/GameTheme.tres` 定义界面布局与主题。
- 九阶段玩法流程及各阶段的结算规则见 [新手教程](BeginnerGuide.zh-CN.md)与 [ADR](ADR/README.md)。

### 2.4 内核实现边界

- 由界面控制器直接修改的共享领域对象。
- 动作中的 `TakeDamage`、`Heal`、召唤、回手等即时写状态逻辑。
- 同步递归广播触发器的调用方式。
- `ReferenceEquals`、手牌索引、对象地址或容器位置所代表的身份。
- 将权威规则结果与客户端动画事件混合的结算模型。
- `System.Random`、默认 `Dictionary`/`HashSet` 枚举顺序或随机 `Guid` 所参与的任何权威逻辑。
- 用数组顺序隐含执行顺序、类型不匹配时静默跳过的内容语义。
- 未通过当前 V2 schema 编译的卡牌定义。

规则效果在遍历时立即改变共享对象，会导致后执行的效果看到前一个效果的中间状态。引擎通过冻结快照、整组归约和原子提交避免这一问题；仅拆分目录或 `.csproj` 无法保证确定性。

## 3. 架构原则

### 3.1 内核纯度

`Eota.Kernel` 是普通 .NET 类库，只依赖 BCL。它不得引用或读取：

- Godot、ASP.NET Core、数据库、文件系统、Socket 或平台 API。
- 系统时间、环境变量、当前区域性或线程调度结果。
- DI 容器、全局服务定位器、静态可变缓存。
- UI 节点、贴图、本地化文本、动画进度或网络会话。

协议、内容、种子和命令全部通过参数显式传入。内核返回新权威状态、回执、领域事件和哈希材料，不执行 I/O。

### 3.2 单一权威

- 联机模式下，Headless Server 是唯一权威；客户端不提交状态，只提交命令。
- 本地模式也通过相同的命令和消息边界运行，只是传输由进程内适配器完成。
- 客户端预测只能用于交互提示，不得作为权威结算结果。
- 回放通过正式内核重新执行命令，不以录制动画或状态补丁代替规则执行。

### 3.3 端口与适配器

- 内部定义用例端口，外部项目实现文件、网络、数据库、Godot 和 Web 细节。
- Transport DTO 与领域对象不共用类型，必须显式映射。
- 表现层只消费观察者投影和表现事件，不能持有可写的 `MatchState`。

### 3.4 确定性优先于便利性

- 权威数值首版只使用有检查的整数运算；未来定点数或规范有理数必须升级协议。
- 每个权威集合都有规则顺序或在使用前规范排序。
- 每一个随机调用、身份分配、冲突键和错误结果都可以回放和审计。
- 严禁用“遍历得恰好稳定”代替协议定义。

## 4. 建议的解决方案与目录

```text
/
├─ Eota.sln
├─ Directory.Build.props
├─ global.json
├─ project.godot
├─ Eota.Godot.csproj                 # 唯一引用 Godot SDK 的项目
├─ Docs/
│  ├─ Architecture.zh-CN.md
│  └─ ADR/
├─ Content/
│  ├─ Source/
│  │  ├─ Cards/
│  │  ├─ Protocols/
│  │  └─ Localization/
│  └─ Schemas/
├─ src/
│  ├─ Eota.Kernel/
│  ├─ Eota.Content.Compiler/
│  ├─ Eota.Transport.Contracts/
│  ├─ Eota.Server.Application/
│  ├─ Eota.Server.Infrastructure/
│  ├─ Eota.Server.Transport.WebSocket/
│  ├─ Eota.Server.Host/
│  ├─ Eota.Client.Core/
│  ├─ Eota.Client.AI/
│  ├─ Eota.Client.Desktop/
│  ├─ Eota.Client.Transport.WebSocket/
│  └─ Eota.Client.Transport.InProcess/
├─ Godot/
│  ├─ Scenes/
│  ├─ Scripts/
│  └─ Assets/
├─ tools/
│  ├─ Eota.ContentCli/
│  └─ Eota.ReplayCli/
└─ tests/
   ├─ Eota.Kernel.Tests/
   ├─ Eota.Kernel.Algebra.Tests/
   ├─ Eota.Content.Compiler.Tests/
   ├─ Eota.Replay.Tests/
   ├─ Eota.Server.IntegrationTests/
   ├─ Eota.Contract.Tests/
   └─ Eota.Architecture.Tests/
```

首版不建议继续拆小 `Eota.Kernel`。阶段、效果、归约、回放编码在语义上高度相关，过早拆成大量程序集会增加循环依赖风险。先在一个内核程序集内用命名空间和 `internal` 边界隔离；只有出现明确的独立发布或编译需求时再拆项目。

### 4.1 项目职责

| 项目 | 职责 | 可以依赖 |
|---|---|---|
| `Eota.Kernel` | 权威状态、命令、九阶段调度、效果 IR、意图、冲突归约、回执、事件、RNG、规范编码与状态哈希 | BCL |
| `Eota.Content.Compiler` | 读取 V2 作者格式，完成 schema、引用、类型和确定性校验，产出规范内容包与协议包 | Kernel、BCL |
| `Eota.Transport.Contracts` | Schema-first 的跨进程请求、响应、快照、增量和版本信息 | BCL；不得依赖 Kernel |
| `Eota.Server.Application` | 单局会话、命令用例、观察者投影、重连、幂等和服务端端口 | Kernel、Contracts |
| `Eota.Server.Infrastructure` | 内容仓库、命令日志、检查点、回放和具体持久化 | Server.Application、Kernel |
| `Eota.Server.Transport.WebSocket` | WebSocket 消息收发、鉴权上下文映射、限流和协议 DTO 映射 | Server.Application、Contracts |
| `Eota.Server.Host` | Headless .NET 进程、配置、DI、生命周期和可观测性装配 | Server 各项目 |
| `Eota.Client.Core` | UI 无关的客户端状态仓库、命令构造、确认/拒绝处理、事件队列 | Contracts |
| `Eota.Client.AI` | 三档本地策略、己方观察视图评分、可取消的普通客户端命令循环；独立种子和固定搜索预算 | Client.Core、Contracts；不得引用 Kernel 或 Server |
| `Eota.Client.Desktop` | 本地／远程会话装配、公开卡牌资料投影、AI 生命周期与回放文件 | Client.AI、Client.Core、客户端传输、Server.Infrastructure；权威类型只留在装配边界 |
| `Eota.Client.Transport.WebSocket` | 远程连接、断线重连、序列检查 | Client.Core、Contracts |
| `Eota.Client.Transport.InProcess` | 本地单机/测试的环回传输，仍走同一契约语义 | Client.Core、Server.Application、Contracts |
| `Eota.Godot` | Godot 节点、输入、场景、资源加载和动画 | Desktop 与 Contracts；不得直接调用 Kernel／Server，不得被其他项目引用 |

### 4.2 必须由自动化检查的依赖规则

```text
Kernel ─X→ Godot / ASP.NET / Infrastructure / Transport.Contracts
Transport.Contracts ─X→ Kernel / Godot
Server.Application ─X→ Godot / 具体数据库 / 具体网络框架
Client.Core ─X→ Kernel / Server / Godot
任何非 Godot 项目 ─X→ Godot
```

`Eota.Architecture.Tests` 应检查项目引用以及禁止命名空间，避免边界随着开发逐渐失效。

### 4.3 工具链基线

- 首版所有普通 .NET 项目统一目标框架为 `net8.0`，与当前工作区已安装的 .NET 8 SDK 对齐。
- 用 `global.json` 固定 SDK feature band；升级 SDK 或 Godot Mono 版本必须显式提交并跑完整回放语料库。
- `Directory.Build.props` 统一开启 nullable、确定性构建、分析器和警告即错误，禁止各项目悄悄放宽规则。
- `Eota.Godot.csproj` 使用与实际 Godot Mono 编辑器完全一致的 SDK 版本；普通项目不能继承 Godot SDK。
- NuGet 依赖集中锁定并提交 lock file。Kernel 首版保持 BCL-only，测试和外围适配器可以使用经过锁定的包。

## 5. 内核公开边界

内核应提供很小的公开 API。建议按“接收一条命令”和“推进一个结算帧”分开：

```csharp
public interface IMatchKernel
{
    MatchCreationResult Create(MatchManifest manifest);
    CommandTransition Accept(MatchState state, AuthoritativeCommand command);
    FrameTransition Step(MatchState state);
}
```

- `Create` 校验清单并创建开局状态。
- `Accept` 只验证和接受一条权威命令，返回命令回执及更新后的状态。
- 当双方提交后，状态进入可推进的结算边界。
- `Step` 严格推进一个结算帧，返回该帧的意图摘要、完整回执、领域事件和哈希。
- `KernelRunner.RunUntilBoundary` 是纯 .NET 便利封装，循环调用 `Step`，直到等待玩家输入、比赛结束或触发预算错误。

把单帧作为正式 API 可以支持逐帧诊断、检查点、属性测试和回放定位。服务端通常运行到下一个输入边界；Godot 是否播完动画不影响推进。

实现可在一次调用内部使用受控的 mutable builder 以降低分配，但可变引用不得逃出内核调用，输入状态不得被调用者从外部修改。

### 5.1 权威状态

`MatchState` 至少包含：

```text
MatchState
├─ manifest reference: protocol/content/effect-language versions
├─ turn, macro stage, causal substage, frame, revision
├─ match status and result
├─ two PlayerState
│  ├─ health and resources
│  ├─ ordered deck of CardInstanceId
│  ├─ unordered-by-rule hand keyed by CardInstanceId
│  └─ planning area and submission state
├─ ordered LaneState[]
│  └─ fixed player slots, ether values and decay guards
├─ card instances, entities, buffs and active effects by stable ID
├─ ready WorkItems and Continuations
├─ live Receipt references and Tombstones
├─ deterministic ID counters
└─ GlobalRuleRng state and call counter
```

手牌在规则语义上无序，但存储和编码仍按 `CardInstanceId` 规范排序。牌库是有序序列。玩家固定为两个稳定 `PlayerId`，实现中不能把先遍历到的玩家当作优先方。

### 5.2 稳定身份

为以下对象定义强类型 `readonly record struct` ID：

- `PlayerId`、`CardPrototypeId`、`CardInstanceId`、`EntityId`。
- `CommandId`、`EffectId`、`WorkItemId`、`IntentId`、`ConflictGroupId`。
- `ReceiptId`、`EventId`、`TombstoneId`、`FrameId`。

局内身份由清单、规范命令序号和状态中的确定性计数器产生。`Guid.NewGuid()`、对象哈希、系统时间和进程随机值不得成为权威身份来源。外部房间号可以由服务端生成 GUID，但它只用于路由，不参与规则结果与局内 ID。

意图 ID 推荐由以下稳定路径构成：

```text
FrameId / WorkItemId / CompiledEffectPath / TargetId / EmissionOrdinal
```

其中 `CompiledEffectPath` 来自内容编译结果，而不是运行时集合的遍历位置。

## 6. 权威结算管线

每一帧只有一条合法管线：

```text
1. Freeze       冻结本帧状态与观察者集合
2. Plan         从就绪工作项创建效果执行计划
3. Evaluate     选择目标、求条件和表达式，只读快照
4. Emit         产生带稳定 ID 的原子意图
5. Group        按 ConflictSchema 和 ConflictKey 构造完整冲突组
6. Reduce       归约完整冲突组；必要时按稳定组序消费 GlobalRuleRng
7. Commit       一次性提交全部结果
8. Receipt      每个 IntentId 恰好写入一个不可变回执
9. Events       从提交事实创建领域事件与 Tombstone
10. Schedule    用冻结观察者和冻结来源创建下一帧 WorkItem/Continuation
11. Hash        规范编码状态、回执和事件并记录摘要
```

阶段调度器只决定何时创建根工作项；效果求值器无权推进阶段，归约器无权调用 UI 或网络，提交器无权临时执行新触发器。

### 6.1 快照、工作项与延续

- `FrameSnapshot` 是该帧所有并列效果的共同只读视图。
- `WorkItem` 描述一个已具备执行资格的效果，携带来源、拥有者、路线、触发事件和冻结来源引用。
- `Continuation` 表示显式顺序效果的下一步，只能读取前一步回执公开的输出。
- 新事件产生的触发器只能进入后续帧，不得递归插入当前调用栈。
- `ResolutionBudgets` 限制总帧数、工作项数、事件数和循环次数；超限返回稳定错误并进入协议规定状态。

### 6.2 效果 IR

运行时不直接解释作者 JSON。编译后的效果 IR 至少包含：

- `Trigger` 与可选发动要求。
- 类型化 `Selector`、`Condition` 和 `Expression`。
- `Parallel`：所有子节点读取同一帧快照。
- `Sequence`：后续节点在新帧中读取前一步回执。
- `IfElse`、`ForEach`、`BoundedLoop`、`Retarget`。
- 叶节点 `EmitIntent`。

V2 作者格式直接使用显式 `parallel`/`sequence`，不能依赖数组书写顺序猜测语义。类型不匹配、未定义变量和非法表达式必须在内容编译期报错；只有真正依赖运行时状态的问题才产生运行时稳定错误。

### 6.3 意图、冲突组与归约器

每种新原子意图都必须同时定义：

1. 输入和目标类型。
2. `ConflictKey` 与结算窗口。
3. 交换、结合且有单位元的合并表示。
4. 与其他意图的冲突策略。
5. 完整回执 schema 与稳定原因码。
6. 产生的领域事件。
7. 可供 `Sequence` 消费的对象集合或标量输出。
8. 协议版本影响、排列测试和回放样例。

Reducer 只能接收完整冲突组。随机选择不得发生在 `Merge(A, B)` 期间，而应在组已经规范化后、按稳定冲突组顺序进行。

VNext 文档第 14 节的默认冲突表应逐项实现为独立 reducer 或同一生命周期/属性协调 reducer；不要把优先级散落在动作类中。

### 6.4 回执与事件不是动画

- `Receipt` 是规则事实与顺序效果数据接口，信息完整且可能包含秘密数据。
- `DomainEvent` 是已提交的规则事实，用于触发规划、回放与诊断。
- `PresentationEvent` 是按观察者脱敏后的客户端消息，用于动画、声音和日志。

三者必须分型。内核不得创建 `DamageAnimationEvent` 之类的表现类型；Server Projection 层负责把一组领域事件映射为某个玩家可见的表现事件。

## 7. 内容与全局对局协议

### 7.1 两阶段内容管线

```text
作者 JSON + schema + localization metadata
               │
               ▼
      Content Compiler / Validator
               │
               ├─ RuleContentPack       权威、规范、参与内容哈希
               └─ PresentationPack      非权威、可本地化、可替换资源
```

`RuleContentPack` 包含卡牌规则原型、类型化效果 IR、关键词参数、稳定引用和效果语言版本。`PresentationPack` 包含名称键、描述键、图片路径和音效等。贴图或翻译变更不应使规则回放失效；规则变化必须改变 `RuleContentHash`。

编译器接受 UTF-8 字节或流，不在内核中扫描目录。具体目录读取分别由 CLI、Server Infrastructure 和 Godot Resource Adapter 完成。

### 7.2 V2 作者格式

- 顶层包含明确的 `schemaVersion`，例如 `eota.card/v2`。
- `id` 全局唯一，枚举和字段采用固定大小写规则。
- 参数化关键词使用结构化值，例如 `{ "kind": "slow", "turns": 1 }`。
- 并列与顺序效果显式书写。
- 每个 selector、condition、expression、effect node 和 intent 都有类型。
- JSON 反序列化只发生在编译器；Kernel 只接收不可变、已验证的编译结果。
- 运行时不允许卡牌携带任意 C#、脚本或反射类型名。

V2 内容输入位于 `Content/Source/Cards`，作者格式见 [JSON 编写指南](CardJsonDesignGuide.zh-CN.md)。每张新内容都必须独立通过编译、规则测试和回放测试，再生成 [当前卡表](CardTable.zh-CN.md)。

### 7.3 协议编译

`GameProtocol` 与内容使用类似流程：作者配置经规范化和交叉校验后生成 `CompiledGameProtocol`。其中必须冻结：

- 参数和 policies。
- `StageSchema`、因果子阶段和充能时机。
- `ConflictSchema`、窗口、key 版本和 reducer policy。
- 数值、规范编码、状态哈希和随机协议版本。
- 全部安全预算。

建立比赛前，`ProtocolId`、`ProtocolVersion`、`ProtocolHash`、效果语言版本和 `RuleContentHash` 必须完全匹配。

已冻结的规则边界以根目录 `Docs/ADR/` 中的 Accepted 决策为准：起手和换牌属于 bootstrap；Return 创建全新手牌实例；Transform 原位保留实体身份；Death 与 Leave 是不同触发事实；Banish 不触发二者。权威攻击和费用可以为负，但显示、伤害与支付使用非负派生值。有限场地与永久场地使用分型生命周期，不能用数值哨兵模拟永久。

### 7.4 规范编码与哈希

不要对普通 `System.Text.Json` 输出直接做权威哈希。内核实现版本化的 Canonical Encoder：

- 固定字段编号与字段顺序。
- UTF-8、显式长度、显式整数位宽与字节序。
- 枚举使用协议规定整数值。
- 固定槽按规则顺序，集合和字典按稳定键排序。
- 不编码 null 的多种等价形式、区域性文本或运行时类型名。
- 权威整数使用 `checked`；未来数值格式必须升级编码版本。

协议、规则内容、状态、回执批次和事件批次统一使用 SHA-256 摘要，但各自带 domain separator 和 schema version，避免不同对象的字节流被混用。

## 8. 确定性随机

内核自行实现并版本化 `GlobalRuleRng`，不得把 .NET `System.Random` 的实现当作协议。

首版已经固定：

- `SplitMix64` 展开比赛种子。
- `xoshiro256**` 产生 64 位样本。
- 有界整数使用明确的 rejection sampling，禁止有偏 `sample % count`。
- 全部位宽、溢出和字节序写入 `RandomAlgorithmVersion` 测试向量。

每次真正需要随机裁决时记录调用前后计数、`RandomCallKey`、规范候选、样本和获胜候选。候选只有一个或根本没有冲突时不得消费随机数。

AI 和视觉随机使用独立随机源。AI 只向内核提交最终命令，回放不会重新运行 AI。

## 9. Headless Server

### 9.1 单局 Actor

每场比赛由一个 `MatchActor` 独占 `MatchState`：

```text
many network connections
          │
          ▼
 bounded mailbox per match
          │
          ▼
 one logical reader: validate → kernel → persist → project → publish
```

- 同一比赛内始终串行执行；不同比赛可以在线程池上并行。
- 禁止用锁让多个请求同时进入同一个 Kernel 状态。
- 邮箱有界并具备背压；恶意或故障客户端不能无限堆积命令。
- 双方提交后 Actor 运行 Kernel 至下一输入边界，不等待任何客户端动画。
- 网络先后提交不会形成规则优势；双方最终已接受计划在统一阶段进入相同快照。

### 9.2 服务端用例

`Eota.Server.Application` 建议提供：

- `CreateMatch`、`JoinMatch`、`SubmitGameCommand`。
- `GetObserverSnapshot`、`ResumeFromRevision`。
- `LoadReplay`、`VerifyReplay`、`ExportReplay`。
- `AttachSpectator`（可后置实现，但投影边界首版就保留）。

其端口包括 `IProtocolRegistry`、`IContentRegistry`、`ICommandLogStore`、`ICheckpointStore`、`IReplayStore` 和 `IClientPublisher`。具体文件、数据库、对象存储或消息系统由 Infrastructure 实现。

### 9.3 幂等、顺序和恢复

客户端命令至少携带：

- `MatchId`、已认证用户/席位。
- `ClientCommandId` 与该连接的单调序号。
- `ExpectedRevision`。
- 使用稳定对象 ID 的 payload。

服务端以 `(MatchId, SeatId, ClientCommandId)` 去重。重复命令返回原确认，过期 revision 返回稳定拒绝和恢复提示。断线重连先获取指定观察者的完整快照，再从 `ServerSequence` 补发后续消息。

恢复时加载最近检查点，重放其后的已接受命令，并逐记录校验哈希。服务端超时来自 `TimeProvider`，但超时结果必须转换成显式 `SystemCommand` 并写入命令日志；Kernel 自身不读取时间。

## 10. 传输契约与 Web Adapter

### 10.1 契约原则

- 协议 schema 独立版本化，不直接序列化 C# 领域类。
- 所有字段有稳定名称、类型和兼容规则；未知消息与未知字段有明确处理方式。
- 命令引用 `CardInstanceId`、`EntityId` 和 `LaneId`，不引用手牌下标、Godot Node 或服务端对象。
- DomainEvent 和 Receipt 不直接过网，由投影器转换为可见消息。
- 网络错误、命令拒绝和规则错误分开编码。

首个实时适配器建议使用 WebSocket；管理类用例可使用 HTTP。以后增加 REST/SSE、gRPC、原生客户端协议或浏览器前端时，只需实现新的 Transport Adapter，不改变 Kernel 和 Server Application。

### 10.2 建议消息

```text
Client → Server
  JoinMatch
  RequestSnapshot
  PlanCard(CardInstanceId, LaneId/GlobalTarget)
  CancelPlan(PlanCommandId)
  SubmitTurn
  AdditionalChoice(...)          # 为未来效果选择预留

Server → Client
  Joined
  CommandAccepted / CommandRejected
  ObserverSnapshot
  ObserverDelta
  PresentationFrame
  PublicHashCheckpoint
  MatchEnded
  ResyncRequired
```

每个服务端 envelope 包含 `ContractVersion`、`MatchId`、`ServerSequence`、`MatchRevision` 和 payload discriminator。建议从契约生成 JSON Schema/OpenAPI/TypeScript 类型，使未来 Web 客户端无需引用 C# 程序集。

### 10.3 隐藏信息

Server 必须按 audience 生成投影：玩家一、玩家二、观战者和管理员不能复用同一个序列化对象再临时删字段。

- 对手手牌只暴露数量和允许公开的信息。
- 牌库顺序、秘密选择、完整回执和 RNG 状态不下发普通客户端。
- 抽牌事件对本人可含 `CardInstanceId`/原型，对手只收到公开版本。
- 客户端校验的是 `ObserverViewHash`；完整权威 `StateHash` 只在服务端、回放验证和可信诊断中使用。

这既是网络安全要求，也是防止将来 Web 客户端通过开发者工具读取秘密状态的必要边界。

## 11. Godot Mono 客户端

Godot 是一个 UI Adapter，不是规则宿主的特殊版本。

```text
Godot input / signals
        │
        ▼
BattlePresenter / ScreenController
        │ creates client command
        ▼
Eota.Client.Core ── IGameClientTransport ── remote or in-process
        │
        ▼
ObserverStore + PresentationEventQueue
        │
        ▼
Godot view nodes / animation timeline
```

### 11.1 Godot 层允许做的事

- 把拖拽、点击和菜单操作转换为客户端命令。
- 根据 `ObserverSnapshot`/`ObserverDelta` 创建和更新 Node。
- 按 `PresentationFrame` 播放动画、音效和战斗日志。
- 从 `PresentationPack` 解析 Godot 资源路径与本地化键。
- 在主线程消费已排队的网络消息。

### 11.2 Godot 层禁止做的事

- 直接增减英雄生命、移动实体或判定死亡。
- 通过动画结束 signal 驱动 Server 或 Kernel 继续结算。
- 把 Node 引用、数组下标或场景树路径放进权威命令。
- 根据本地容器顺序猜测牌库、手牌或触发顺序。
- 将客户端预览状态回写成权威状态。

动画较慢时消息可以缓冲、倍速或跳过；若缓冲超过上限，客户端请求新快照并重新投影。Node 的生命周期由投影差异驱动，不与内核 `Entity` 对象共享实例。

### 11.3 本地单机

`Eota.Client.Transport.InProcess` 在进程内启动 `Server.Application` 的最小组合，并通过同一 contracts envelope 通信。Godot 不直接调用 `MatchState.TakeDamage` 一类内部方法。

本地适配器与 WebSocket 适配器必须通过同一套 contract tests；集成测试可额外强制一次序列化往返，以发现进程内调用掩盖的协议问题。

## 12. 回放、检查点与持久化

建议回放包包含：

```text
manifest.json
commands.ndjson
expected-hashes.ndjson
expected-receipts.bin       # 可选，但验证模式建议保存
expected-events.bin         # 可选，但验证模式建议保存
checkpoints/                # 可选加速数据
```

- 事实来源永远是比赛清单、种子和已接受命令。
- 回执、事件和哈希是预期输出，用于验证，不能反向驱动规则。
- 检查点包含完整规范状态、调度器、活跃回执引用、Tombstone、确定性计数器和 RNG 状态。
- Tombstone 和回执账本在权威状态中只保留仍被 WorkItem/Continuation 引用的部分；完整历史由追加日志持久化，避免长局内存无限增长。
- 每个宏观阶段和回合结束必须有哈希；开发/诊断模式可逐帧记录子树哈希。

持久化介质不是 Kernel 决策。首版可使用本地追加文件和原子替换检查点，未来改数据库或对象存储不影响规则层。

## 13. 客户端查询与投影

不要把整个 `MatchState` 暴露给客户端或 UI。`ObserverProjector` 根据权威状态、事件和 audience 产生：

- `ObserverSnapshot`：重连或首次加载的完整可见状态。
- `ObserverDelta`：以稳定 ID 表达的增量变化。
- `PresentationFrame`：一帧内可并列表现的事件，以及跨帧因果边界。

客户端 `ObserverStore` 只应用服务端序列严格递增的投影；检测到丢包、重复或基线不一致时停止应用 delta 并请求快照。UI 查询（合法落点、高亮、卡牌详情）由客户端投影计算提示；服务端仍对命令执行最终验证。

为了避免客户端复制规则，Server 可在投影中附带当前玩家的 `LegalActionHints`。这些提示不属于权威输入，过期提示不会让非法命令生效。

## 14. 错误与可观测性

### 14.1 稳定规则错误

`InvalidCommand`、`InvalidTarget`、`InvalidContent`、`VersionMismatch`、`EffectConflict`、`ResolutionLimitExceeded`、`ArithmeticError`、`ReplayHashMismatch` 等错误使用稳定码和规范参数。异常消息、堆栈和本地化文本不参与权威哈希。

预期非法命令返回值，不用异常控制流程；内容损坏或内核不变量破坏可以抛出进程异常，但 Server 必须隔离单局故障并保留诊断材料。

### 14.2 非权威遥测

Server 日志和指标可记录 `MatchId`、revision、turn/stage/frame、耗时、意图数、冲突组数和预算使用量。墙钟耗时、trace id 和日志顺序不进入状态哈希。

发生失同步时生成包含最近命令、工作项、冲突组、回执、Tombstone、RNG 调用和首个差异状态子树的诊断包，同时按访问权限清理玩家秘密。

## 15. 测试策略

### 15.1 内核测试

- 基础规则示例测试：牌组、费用、部署、移动、战斗、清理和全部关键词。
- 每个 reducer 的单位元、交换律、结合律属性测试。
- 随机排列同一意图集合，比较状态、回执、事件、错误和哈希。
- 触发链、离场观察窗口、冻结来源和顺序 continuation 测试。
- 预算、溢出、除零、非法类型和空目标的稳定错误测试。
- RNG 官方测试向量、随机调用计数和无冲突不消费随机测试。

### 15.2 回放测试

- 相同输入跨进程多次执行得到相同哈希。
- 从每个合法检查点恢复与从头执行一致。
- 修改协议、内容、种子或命令必然被检测。
- 每个已发布协议保留不可变 golden replay corpus。
- Debug/Release、Windows/Linux Server 和 Godot 支持的平台运行相同内核语料库。

### 15.3 边界与集成测试

- Architecture Tests 阻止非法项目引用和框架依赖。
- Contract Tests 同时套用 WebSocket 与 InProcess transport。
- 两客户端联机、断线重连、重复命令、过期 revision、乱序消息测试。
- 玩家一、玩家二和观战者投影的隐藏信息泄漏测试。
- Content Compiler 对当前全部卡牌进行 schema、引用、类型和确定性校验。
- Headless Server 多局并发与单局邮箱背压测试。

## 16. 实施顺序

### 里程碑 0：工程护栏

- 建立 solution、项目引用、统一编译选项和 architecture tests。
- 冻结强类型 ID、错误码、协议/内容/状态规范编码版本。
- 项目依赖限定在本仓库声明的项目和 NuGet 包中。

完成标准：Kernel 可以在没有 Godot 的普通 `dotnet test` 中构建；反向依赖会使测试失败。

### 里程碑 1：确定性地基

- 实现 Manifest、Protocol、RuleContentPack、稳定 ID、Canonical Encoder、SHA-256 和 GlobalRuleRng。
- 实现最小 MatchState、命令日志、状态哈希和回放 CLI。
- 只支持洗牌、起手和空白回合。

完成标准：独立进程重放得到相同牌序、ID、RNG 计数和哈希。

### 里程碑 2：无效果基础对局

- 规划/取消/提交命令、九阶段状态机。
- 随从/场地部署、移动、基础战斗、费用和清理。
- 单帧 API、领域事件和观察者投影雏形。

完成标准：无卡牌效果的完整比赛可回放，双方/路线遍历置乱不改变结果。

### 里程碑 3：意图与归约

- 实现快照、Effect IR、WorkItem、Intent、Conflict Group、Reducer、Commit、Receipt。
- 先完成数值、伤害、治疗、关键词、击杀和资源 reducer。
- 建立所有代数性质测试。

完成标准：每个 Intent 恰好一份回执；基础冲突矩阵全部通过排列测试。

### 里程碑 4：触发链与复杂区域

- Tombstone、冻结观察者、Frozen Source、Continuation。
- 召唤、移动、替换、变形、回手、放逐、抽牌、检索、生成和容量窗口。
- `Parallel`、`Sequence`、分支与有界循环。

完成标准：VNext 文档的复杂冲突、离场观察和结果传递案例全部成为 golden replay。

### 里程碑 5：内容重新录入

- 定义 V2 schema 和编译器。
- 在 `Content/Source/Cards` 中编写 V2 卡牌定义，并生成 `Docs/CardTable.zh-CN.md`。
- 在 `Content/Source/Localization` 和 `Content/Source/Art` 中维护本地化与资源元数据。
- 对卡表描述无法唯一确定的 VNext 语义建立人工确认清单。

完成标准：卡表中的全部卡牌已重新录入并通过编译；规则内容和表现内容分别生成稳定 manifest。

### 里程碑 6：Server 与 Godot Adapter

- MatchActor、持久化端口、WebSocket transport、重连和隐藏信息投影。
- Client.Core、InProcess/Remote transport。
- Godot 战斗 UI 只绑定投影和表现事件。

完成标准：Headless Server、进程内本地模式和 Replay CLI 对同一命令日志产生相同最终权威哈希；Godot 暂停或跳过动画不改变结果。

## 17. 首版明确不做

- 不让客户端进行 lockstep 权威计算或接受客户端状态上传。
- 不在卡牌内容中加载任意 C# 插件或脚本。
- 只接受当前 V2 schema 的卡牌定义，不提供其他格式的转换入口。
- 不把数据库、WebSocket 或 Godot 抽象塞进 Kernel。
- 不为了动画逐步播放而暂停服务端结算。
- 不在首版建立微服务；一个 Headless Host 内按单局 Actor 隔离即可。
- 不提前实现浏览器 UI，但保证 schema、投影和 transport 端口不依赖 Godot。

## 18. 架构验收清单

开始大规模实现前和每个里程碑结束时检查：

- [ ] `Eota.Kernel` 可由普通 .NET 测试进程独立运行。
- [ ] Kernel 不引用 Godot、网络、存储、时间或本地化。
- [ ] 同帧效果只读同一快照，动作只产生 Intent。
- [ ] Conflict Grouper 在随机前得到完整、规范冲突组。
- [ ] 每个 Intent 无论成功与否都恰好生成一个 Receipt。
- [ ] 触发器只由提交后的 DomainEvent 创建并进入后续帧。
- [ ] 离场观察者与 Frozen Source 不查询提交后的战场猜测历史。
- [ ] 稳定 ID、RNG、规范编码和哈希均有版本与测试向量。
- [ ] Server 是联机唯一权威，同一比赛只有一个逻辑写者。
- [ ] 网络只交换版本化契约、命令和观察者投影。
- [ ] Godot 只产生命令并消费投影/表现事件。
- [ ] 玩家投影不会泄漏对手手牌、牌库顺序、RNG 或完整回执。
- [ ] 本地、Server 和 Replay CLI 使用同一 Kernel 路径。
- [ ] 新 Transport 或 Web UI 无需修改 Kernel。
- [ ] 规则、内容和协议变更会使对应版本/hash 改变。

满足以上边界后，VNext 才真正具备“Pure C# 内核 + Headless Server + Godot/Web 可替换前端”的架构，而不是把原型的即时可变模型换一个目录继续使用。
