# ADR-008：Transport Contract 与观察者投影

- 状态：Accepted
- 日期：2026-09-04
- 接受日期：2026-09-06

## 决定

- Transport Contract 是 schema-first 的独立版本，不直接序列化 Kernel 领域类型。
- 客户端只提交命令；联机 MatchState 只由 Headless Server 持有。
- PlayerOne、PlayerTwo、Spectator 和 Admin 使用独立 audience projector。
- 普通客户端不接收对手手牌、牌库顺序、RNG 状态、完整 Receipt 或完整 StateHash。
- 客户端使用 `ServerSequence`、`MatchRevision` 和 `ObserverViewHash` 检测丢失与失配。
- InProcess transport 与远程 transport 使用相同 envelope 和 contract tests。
- DomainEvent 先经脱敏投影为 PresentationEvent，Godot/Web 不直接消费领域事件。

## 理由

Contract V0 的具体 wire schema、连接/序号约定及开发入口见 [传输契约说明](../Transport/README.zh-CN.md)；实施与验收状态见 [P5 检查点](../Review/P5-Server-Client-Review.zh-CN.md)。

该边界既防止隐藏信息泄漏，也让未来 Web、gRPC 或其他前端无需引用 Godot 或 Kernel 程序集。
