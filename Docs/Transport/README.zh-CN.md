# Contract V0 与 P5 Server/Client 使用说明

本契约承接 [ADR-008](../ADR/008-transport-and-projections.md)。消息定义见 [JSON Schema](contract-v0.schema.json)，实现位于 `Eota.Transport.Contracts`；该项目不引用 Kernel。

P9 继续使用开发 Contract V0：LaneView 新增必填 frozen、locked、statuses，私有视图新增 planOptions（CardInstanceId、LaneId、Allowed、Reason）。路状态对双方与观战者公开，规划提示仅发送给对应玩家。Godot 使用完整视图帧并在重新连接时全量重建；没有独立 delta 格式。客户端与服务端须同步更新。Godot 启动、实验卡远程 Host 参数及验收见 [P9](../Review/P9-Godot-Review.zh-CN.md)。

P6 继续使用开发 Contract V0，私有视图增加必填十进制字符串 nextTurnCost，事件投影增加关键词和资源变化。完整状态、Set 随机选择和回执仍不进入网络消息；可用 `--fixture tests/Fixtures/P6Effects` 运行代表卡对局。详见 [P6 检查点](../Review/P6-Effects-Review.zh-CN.md)。

## 启动与连接

在仓库根目录构建并运行：

```powershell
dotnet build Eota.sln -c Release
dotnet run --project src/Eota.Server.Host -c Release --no-build -- --fixture tests/Fixtures/P4Match
```

Host 默认监听 `http://127.0.0.1:5075`，创建内存对局 `p4-demo`，使用 P4Match 无效果测试内容与种子 `123456789`。可通过 `--urls` 指定监听地址，`--fixture` 指定包含 Cards、protocol-v0.json 和两副 deck JSON 的目录。

- 健康检查：`GET /health`。
- 玩家一：`ws://127.0.0.1:5075/matches/p4-demo/ws?audience=playerOne`。
- 玩家二：同一路径，使用 `audience=playerTwo`。
- 观战者：同一路径，使用 `audience=spectator`。

连接建立时自动发送该 audience 的完整快照。席位由连接上下文绑定，命令正文没有 playerId 字段。这里的 query 参数属于 P5 开发连接方式，尚未实现身份认证；生产鉴权和可信席位分配在 P10 接入。Host 重启会丢失内存对局。

### 卡牌规则一致性（2026-09-11）

WebSocket Upgrade 请求现在必须带 `X-Eota-Rule-Content-Hash`，值为本地编译规则包的 64 位小写十六进制 SHA-256。服务器在创建观察者连接和发送任何快照前，将该值与对局 manifest 的 RuleContentHash 比较：缺失／格式错误返回 HTTP 400 `rule-content-hash-required`，不匹配返回 HTTP 409 `rule-content-mismatch`。玩家一、玩家二和观战者都执行同一检查。

该检查覆盖卡牌列表、数值及效果定义；不要求双方的构筑牌组相同。卡图和翻译不属于规则内容哈希。玩法参数的 ProtocolHash 与规则包哈希含义不同，当前远程席位使用服务器已经创建的协议。

`WebSocketGameTransport.ConnectAsync(endpoint, matchId, ruleContentHash, token)` 负责提交请求头；Godot 从 DesktopCatalog 传入当前规则哈希，并在首份快照后再次核对。缺少该请求头的旧客户端需要同步更新。本次改变发生在 HTTP 准入阶段，未改变 JSON envelope／玩法协议／状态编码版本。InProcess 对局由同一个 DesktopCatalog 装配双方席位。

开发 Host 默认使用 P4 测试卡表，与 Godot 的 132 张实验卡不同；Godot 联机应使用 [P9 实验内容启动参数](../Review/P9-Godot-Review.zh-CN.md#使用)。本机开服页面当前明确标注“暂未实现”，实际进程启动与房间管理列入 P10。

## 消息规则

ContractVersion 固定为 0，与玩法 ProtocolVersion 独立。客户端 envelope 包含 matchId、clientCommandId、clientSequence、expectedPlayerRevision 和 payload；服务端 envelope 包含 matchId、serverSequence、matchRevision 和 payload。

所有 UInt64 身份/计数和 Int64 数值使用**十进制字符串**，避免 JavaScript Number 丢失精度。32 位路线编号、回合和数量仍使用 JSON number。拒绝前导零、超范围数值、未知字段、重复字段、未知 kind 和缺失必填字段。可空字段必须显式写 null；payload 的 kind 放在首个属性。

例如玩家提交当前回合：

```json
{
  "contractVersion": 0,
  "matchId": "p4-demo",
  "clientCommandId": "turn-1-submit",
  "clientSequence": "1",
  "expectedPlayerRevision": "0",
  "payload": { "kind": "submitTurn" }
}
```

| 客户端 kind | payload 的其他字段 |
|---|---|
| requestSnapshot | 无；响应的 requestId 对应 clientCommandId |
| planCard | cardInstanceId、laneId |
| planSpell | cardInstanceId、laneId；全局目标用 null |
| cancelPlan | planCommandId；为当前席位内的计划序号 |
| submitTurn | 无 |
| submitMulligan | replacedCards；不换牌使用空数组 |

| 服务端 kind | 用途 |
|---|---|
| commandAck | accepted、稳定 code、玩家 revision、结果 match revision、可选 planCommandId、resyncRequired |
| observerSnapshot | 完整观察者视图、ObserverViewHash、可选 requestId |
| presentationFrame | frameId、该帧提交后的完整观察者视图、ObserverViewHash 和脱敏事件 |

P5 每次已接受命令后广播完整快照；双方提交后立即运行 Kernel 至下一输入边界，逐帧发送 presentationFrame。客户端不确认动画，也不驱动规则继续。当前没有 delta 或历史补发协议；断线后新建连接和 GameClient，以完整快照恢复。

## 序号、幂等与恢复

- expectedPlayerRevision 指当前席位的命令 revision，不是全局 matchRevision；对手操作不会使自己的合法计划过期。
- clientSequence 在连接内递增。相同席位、相同 clientCommandId 的 Kernel 命令重发返回原 commandAck；内容不同则拒绝为 client-command-id-conflict。去重在连接重建后仍有效，但只保存在当前服务端进程内。
- Kernel 接受或拒绝的命令都保留原确认；传输格式、版本或序号错误不占用命令键。取消等待不代表已经入邮箱的命令被撤销，重试必须沿用原 clientCommandId。
- serverSequence 对每条连接独立递增，从 1 开始；定向确认不会使其他观察者出现序号缺口。重复命令的原确认放入新的 envelope，不回退当前 matchRevision。
- Client.Core 后台接收消息，验证序号、matchId、audience 和 ObserverViewHash。丢帧、失配或断线使 NeedsSnapshot 为 true；SynchronizeAsync 请求完整快照。UI 读取 Store.State 的不可变快照，并消费有界表现队列。

ObserverViewHash 是 UTF-8 编码的 `eota.observer-view/v0\n` 与 ContractJson 规范输出拼接后的 SHA-256 小写十六进制；换行是一个 LF 字节。字段按 DTO 声明顺序输出，公开集合和私有集合均按稳定 ID 排序。envelope 的序号和 revision 不属于此哈希，隐藏命令引起的全局 revision 变化不会混入可见内容哈希。

## 可见性

双方可见英雄、公开资源、战场、弃牌和提交标记。每个玩家只获得自己的手牌、计划、可用费用、命令 revision 与换牌选择；观战者没有 private 数据。公开手牌数量包含尚未揭示的规划卡牌，避免从规划时手牌减少或费用预留推断对手选择。

两方牌库都只暴露数量。RNG、种子、完整 StateHash、IntentReceipt、Tombstone 和命令日志不会进入普通网络 DTO。抽牌/烧牌事件仅向本人附带卡实例与原型；其他观察者只收到无卡牌身份的公开事件。完整哈希仅由服务端诊断 API 和 Replay CLI 使用，没有对应公共 HTTP 路由。

## 资源边界

默认每局邮箱 64 项，满时等待可用容量；只有一个逻辑读者进入 Kernel。每连接最多缓冲 512 条输出，慢观察者超限时断开并重新获取快照，不阻塞比赛。每局最多 32 个观察者和 16,384 个幂等命令键；键满后拒绝新命令，保留旧确认。客户端表现队列最多 256 帧，超限清空并要求新快照。

客户端消息上限 64 KiB，服务端消息上限 1 MiB，JSON 最大深度 32。InProcess 也执行 JSON 往返及大小限制；两种 transport 使用同一套对局、取消、隐私、幂等和版本拒绝测试。

实现参考：[.NET 有界 Channel](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels)、[ASP.NET Core WebSocket](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/websockets?view=aspnetcore-8.0)。
