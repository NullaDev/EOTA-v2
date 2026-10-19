# 传输契约与服务端连接

传输边界见 [ADR-008](../ADR/008-transport-and-projections.md)，消息格式见 [Contract V0 JSON Schema](contract-v0.schema.json) 和 [契约实现](../../src/Eota.Transport.Contracts)。`Eota.Transport.Contracts` 不引用规则内核；客户端与服务端通过版本化消息交换命令和观察者可见状态。

## 开发夹具模式

在仓库根目录构建并运行：

```powershell
dotnet build Eota.sln -c Release
dotnet run --project src/Eota.Server.Host -c Release --no-build -- --fixture tests/Fixtures/P4Match
```

该命令创建内存对局 `p4-demo`，使用 P4Match 无效果测试内容与默认种子 `123456789`，默认监听 `http://127.0.0.1:5075`。`--urls` 指定监听地址，`--match-id` 指定对局名，`--seed` 指定种子，`--cards` 可覆盖卡牌目录。夹具文件及回放用法见 [P4Match 说明](../../tests/Fixtures/P4Match/README.zh-CN.md)。

- 健康检查：`GET /health`。
- 玩家一：`ws://127.0.0.1:5075/matches/p4-demo/ws?audience=playerOne`。
- 玩家二：同一路径，使用 `audience=playerTwo`。
- 观战者：同一路径，使用 `audience=spectator`。

夹具模式通过 query 参数选择观察席位，不做房间身份认证；重启进程后内存对局丢失。Host 启动时必须显式指定 `--fixture` 或 `--room`。开发客户端需要使用与夹具匹配的规则内容；Godot 远程冒烟测试的准备步骤见 [开发工具说明](../../tools/README.zh-CN.md#客户端冒烟测试)。

## 玩家房间模式

游戏内“本机开服”通过 `LocalServerLauncher` 启动独立 Host，并传入 `--room <配置文件>`。玩家操作见 [客户端功能与操作](../GameplayGuide.zh-CN.md#本机开服联机)；房间配置、席位、内容和持久化由 [HostedRoom](../../src/Eota.Server.Infrastructure/HostedRoom.cs) 管理。

房间提供查询、准备和对局连接接口：`GET /matches/{matchId}/room`、`POST /matches/{matchId}/ready`、`GET /matches/{matchId}/ws`。这些入口通过 `Authorization: Bearer <凭据>` 校验对应席位。WebSocket 连接还校验协议哈希和房间设置哈希；双方通过牌组准入后才建立权威对局。具体路由见 [RoomHost](../../src/Eota.Server.Host/RoomHost.cs)。

## 卡牌规则一致性

WebSocket Upgrade 请求必须带 `X-Eota-Rule-Content-Hash`，值为服务器对局规则包的 64 位小写十六进制 SHA-256。缺失或格式错误返回 HTTP 400 `rule-content-hash-required`，不匹配返回 HTTP 409 `rule-content-mismatch`。玩家和观战者均执行检查，见 [WebSocket 接入](../../src/Eota.Server.Transport.WebSocket/WebSocketMatchEndpoint.cs)。

房间准备阶段按双方牌组及其相关卡牌核对逐卡规则，见 [ADR-028](../ADR/028-field-durability-and-deck-rule-scope.md)。客户端完成准入后，使用房间公布的规则包、协议和设置哈希建立连接。卡图和翻译不参与规则哈希，双方不必选择相同牌组。

## 消息规则

`ContractVersion` 为 0，与玩法 `ProtocolVersion` 独立。客户端 envelope 包含 `matchId`、`clientCommandId`、`clientSequence`、`expectedPlayerRevision` 和 `payload`；服务端 envelope 包含 `matchId`、`serverSequence`、`matchRevision` 和 `payload`。

所有 UInt64 身份、计数和 Int64 数值使用十进制字符串，避免 JavaScript Number 丢失精度；32 位路线编号、回合和数量使用 JSON number。序列化与验证由 `ContractJson` 统一处理。

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

| 消息 | 用途 |
| --- | --- |
| `planCard`、`planSpell`、`cancelPlan` | 规划卡牌、法术与撤回计划 |
| `submitMulligan`、`submitTurn` | 提交换牌或当前回合 |
| `requestSnapshot` | 请求完整快照，可附带恢复游标 |
| `requestDiagnostics` | 请求诊断元数据 |
| `commandAck` | 命令确认或拒绝、revision 与同步提示 |
| `observerSnapshot` | 完整观察者视图与视图哈希 |
| `presentationFrame` | 结算帧的完整可见视图与脱敏事件 |
| `observerResume` | 恢复基准视图及可补发的表现帧 |
| `matchTimer`、`serverDiagnostics` | 房间计时与诊断元数据 |

字段定义见 [Messages.cs](../../src/Eota.Transport.Contracts/Messages.cs) 与 [Diagnostics.cs](../../src/Eota.Transport.Contracts/Diagnostics.cs)。双方提交后，服务端运行内核至下一输入边界，逐帧发送表现消息；客户端动画不驱动规则结算。

## 序号、幂等与恢复

`expectedPlayerRevision` 是当前席位的命令版本，对手操作不会使自己的合法计划过期。`clientSequence` 在连接内递增，`serverSequence` 对每条连接独立递增。同席位、同 `clientCommandId` 的命令在去重窗口内重发时返回原确认；同键不同内容被拒绝。取消等待不代表已经进入邮箱的命令被撤销。

`Client.Core` 校验消息顺序、对局、观察席位与视图哈希。断线恢复可提交 `ObserverResumeCursor`，服务端在保留窗口内返回基准视图和表现帧，超出窗口则回退到完整快照。房间模式使用命令日志和检查点恢复权威对局。实现见 [恢复处理](../../src/Eota.Server.Application/MatchActor.Resume.cs)、[客户端核心](../../src/Eota.Client.Core/GameClient.cs) 与 [房间基础设施](../../src/Eota.Server.Infrastructure)。

## 可见性与资源边界

公开视图包含英雄、公开资源、战场、弃牌和提交标记；玩家私有视图包含自己的手牌、计划、费用、换牌选择与合法规划提示。对手手牌和双方牌库只暴露数量，观战者没有玩家私有数据。规则 RNG、种子、完整状态哈希、回执和命令日志不进入普通对局消息。

单局 Actor 串行处理命令，使用有界邮箱、观察者输出队列、命令去重窗口和表现历史。具体限制见 [MatchActorOptions](../../src/Eota.Server.Application/MatchActor.cs)；消息大小与 JSON 限制见 [ContractJson](../../src/Eota.Transport.Contracts/ContractJson.cs)。InProcess 和 WebSocket 使用同一套契约与观察者投影。
