# P9 Godot Adapter 开发验收

> 2026-09-10。记录 P9 开发交付及本轮实际运行反馈的修复。前序：[P8 内容与工具](P8-Content-Review.zh-CN.md)。自动验证和截图检查的范围见下文，不代表最终体验与美术验收。

> 2026-09-11 更新：[第二轮实际反馈](P9-Scene-Review-2.zh-CN.md)已补充独立场景、职业卡池、单方以太图标和规则内容准入校验，当前全量测试为 564 项。本机开服仍为明确占位，人机对战已加入 [实施计划](../ImplementationPlan.zh-CN.md)。下文保留 2026-09-10 的交付与测试记录。

## 实际运行反馈修复（2026-09-10）

| 反馈 | 已实现的调整 |
|---|---|
| 卡牌展示不应显示当前血量 | 卡牌库、牌组浏览和右侧详情通过 `CreatePrototype` 绑定原型投影 `CardPresentation`，没有实例 ID 或当前生命；随从仅显示基础攻击与最大生命。手牌与战场仍绑定实例，战场随从显示当前／最大生命。 |
| 确认换牌按钮过黑 | [统一主题](../../Client/Godot/GameTheme.tres)明确设置普通、悬停、按下和禁用状态的底色与文字颜色；换牌／提交按钮使用青绿色底、白色文字。 |
| 大厅缺少完整对战协议入口 | 本地对局页新增“查看与调整对战协议”和摘要。协议页按四组列出全部 34 项参数，17 项可编辑；包含费用、抽牌、手牌、换牌、以太、衰减与移动设置，其余固定规则和版本限制只读展示。保存前通过正式协议编译器校验，失败时保留原设置。 |
| 对战布局扭曲、需要固定场景 | [BattleScreen.tscn](../../Client/Godot/Scenes/BattleScreen.tscn)固定工具栏、双方信息、战场、手牌、操作区及详情区。[Lane.tscn](../../Client/Godot/Scenes/Lane.tscn)固定双方随从／场地槽位。卡牌库、详情、战场随从与场地各有可编辑场景，脚本绑定数据、实例化重复节点与处理交互。 |

布局以 1600×1000 为基准，窗口保持比例缩放。更多路线与满手通过横向滚动访问，滚动条有独立空间。可直接在 Godot 编辑 `Client/Godot/Scenes` 下的场景和 `GameTheme.tres`；无需运行脚本才能编辑基础布局。另修复退出对局时继续同步已释放客户端的问题，以及回执先于快照到达时遗漏界面更新的问题。

协议保存在 `user://protocol.json`，用于新建本地对局，并随本地回放保存；已有回放仍按原始设置重建。构筑维持 40 张、单卡最多 3 张，相关协议项目当前只读；远程对局使用服务器协议。

## 交付

- 根项目使用 Godot 4.6.2 .NET，主场景为 `Client/Godot/Main.tscn`。界面包含本地双席位、WebSocket 玩家／观战连接、起手换牌、拖拽／点击规划、取消与提交、对局设置、卡牌搜索、牌组编辑保存和回放控制。
- 132 张卡从 VNext 生成内容载入，512×512 卡图保持比例。快速／慢速、冻结／锁闭和以太图标独立渲染。冻结与锁闭由服务端公开路状态驱动；费用与可放置路线提示也由服务端产生。
- 战场以 EntityId 注册和复用节点，处理移动、变形、离场及全量重建。表现帧驱动高亮、数值浮字、离场／抽牌提示和结果显示；暂停、倍速、跳过与回放进度不会调用规则入口。重新同步或跳过时清理已过时的表现队列。
- `Eota.Client.Desktop` 负责内容、牌组、本地会话和回放装配。Godot 场景仅使用该层及 Contracts；架构测试约束场景的依赖边界，禁止直接引用 Kernel 或 Server。两种传输均经过 Client.Core，进程内仍执行 JSON 往返。
- 通用 Frozen／Locked 状态、效果节点、持续时间清理、移动和生命周期校验、公开 DTO 与 JSON Schema 已补齐。具体开发语义及例外见 [ADR-027](../ADR/027-lane-statuses.md)。

## 自动验证

全量 **556 项测试通过**：Kernel 137、Compiler 357、Server Integration 55、Architecture 6、Algebra 1。Solution Release 和 Godot 项目均可构建，格式校验通过。本轮新增协议自定义值进入实际对局／保存回放后的恢复验证，以及不合法参数组合被拒绝的验证。

新增验证覆盖路状态冲突的全部 24 种排列、双方禁攻、战斗宣告后冻结、锁闭进出、自动移动候选过滤、部署复核、返回／替换限制、死亡／放逐／变形例外、持续时间、编译错误和 Sequence／Parallel 差异。

Desktop 集成测试以同一组命令运行本地和真实 WebSocket 双客户端，包含换牌、规划取消、提交、重新同步、重连及回放导出／重载，最终服务端权威哈希一致。三种 audience 的路状态投影完成 JSON 往返及 Schema 校验。

Godot 分别实际执行 headless 和 OpenGL 窗口测试；本地对局执行 **82 帧**到第 5 回合 Planning，导出后由 Replay CLI 独立进程验证。暂停、倍速、跳过队列、重建全部节点不改变最终状态：

`ff4220becdc29056b447cd6246fe62bad1ab344169fab5b137e55924715f8aea`

实际 Godot WebSocket 客户端向独立 Host 发送相同日志，最终玩家一视图与已验证的本地回放一致。初次日志为 `artifacts/p9-headless.log`、`artifacts/p9-render.log`、`artifacts/p9-remote-godot.log`。初次启动与渲染测试未充分发现本轮反馈中的视觉问题，不能据此认定界面体验已验收。

本轮 `--smoke-ui-review` 在实际 OpenGL 窗口执行，覆盖 1600×1000、1280×800 和 1280×720：检查 132 张展示卡均无实例身份及当前生命、主菜单协议入口可达、换牌按钮可用、四回合命令全部接受并进入第 5 回合、6／12 路槽位不溢出、10 张手牌不被滚动条裁切，且最后一路与最后一张牌均能滚动到达。日志为 `artifacts/p9-fixed-render.log`、`artifacts/p9-fixed-small.log`、`artifacts/p9-fixed-aspect.log`，均无错误或警告。

已查看实际截图：[大厅](../../artifacts/p9-fixed-1600x1000-menu.png)、[协议](../../artifacts/p9-fixed-1600x1000-protocol.png)、[卡牌库](../../artifacts/p9-fixed-1600x1000-collection.png)、[换牌](../../artifacts/p9-fixed-1600x1000-mulligan.png)、[对战](../../artifacts/p9-fixed-1600x1000-battle.png)、[宽屏滚动末端](../../artifacts/p9-fixed-1280x720-scroll-end.png)。本轮本地 smoke 与独立 Replay CLI 再次确认上面的 82 帧与最终状态哈希；真实 WebSocket smoke 也再次通过，日志为 `artifacts/p9-fixed-local.log`、`artifacts/p9-fixed-remote.log`。检查未穷举所有鼠标操作、卡牌组合和显示设备。

七份旧回放同步到 state canonical 8／effect language 4；重建前后逐帧比较，命令、回执、事件、RNG、阶段与结局不变。备份和比较报告位于 `artifacts/P9PreviousReplays`、`artifacts/P9RebuiltReplays/report.json`。P8 规则包哈希仍为 `23b038bef6b1451e23d9e813133237cc8290276a1716bf3a3d7177f84f9266cf`。另新增 [P9 路状态回放](../../tests/Fixtures/P9LaneStatuses/README.zh-CN.md)，全部 74 个检查点独立恢复通过。

## 使用

在根目录运行：

```powershell
dotnet build Eota.sln -c Release
dotnet build Eota.Godot.csproj
```

使用 Godot 4.6.2 .NET 打开根目录 `project.godot` 并运行主场景。大厅默认提供四职业牌组，可从“查看与调整对战协议”修改本地规则并保存；本地模式提交后切换席位，双方提交即结算。牌组与回放保存到 Godot `user://decks`、`user://replays`。

远程使用 132 张实验卡的开发对局：

```powershell
dotnet run --project src/Eota.Server.Host -c Release --no-build -- --fixture tests/Fixtures/P8Content --cards Content/Source/Cards --seed 146 --match-id p8-demo
```

在大厅连接 `ws://127.0.0.1:5075/matches/p8-demo/ws?audience=playerOne`，对局 ID 填 `p8-demo`。第二客户端使用 `playerTwo`；观战使用 `spectator`。未传参数时仍启动原有 P4 开发对局。

本地回放目录额外包含 Replay CLI 所需的协议、牌组和 commands.json，record-match 时使用创建对局时的种子。自动 smoke 可用 Godot 的 `-- --smoke` 参数执行；输出至 `artifacts/P9GodotReplay`。

界面回归可用 `--resolution 1280x720 -- --smoke-ui-review` 执行；有图形窗口时截图保存为 `artifacts/p9-fixed-{窗口尺寸}-{页面}.png`。Godot 视口截图只包含游戏画布，不包含窗口边框和宽屏留边。

## 当前边界

Contract V0 继续发送完整视图帧；没有引入独立 ObserverDelta 格式。断线后通过重新连接获取快照，自动重连、历史补发、鉴权与跨进程持久化由 P10 处理。

本地模式为同机轮换双席位，不含 AI。回放目前导出完整本地权威日志并投影为观战视图；远程席位不能导出其他玩家的隐藏信息。客户端的名称、描述和卡图使用本机内容包，联机双方应使用同一内容版本。牌组管理提供编辑和保存，卡牌平衡性与最终美术仍未验收。
