# ADR-005：GlobalRuleRng V1

- 状态：Accepted
- 日期：2026-09-04
- 接受日期：2026-09-06

## 决定

- 64 位 `MatchSeed` 使用 SplitMix64 展开为四个状态字。
- 样本生成使用 xoshiro256**，算法的位宽、旋转和无符号回绕作为 V1 合同固定。
- 有界选择使用 rejection sampling，禁止直接以 `% candidateCount` 产生有偏结果。
- RNG 状态和原始样本计数属于权威状态。
- 每类随机调用必须先完成调用顺序与候选集合的规范排序；冲突裁决取样前还须构造完整冲突组。随机调用记录稳定调用键、候选、计数和结果。
- 无候选、单候选或容量足够时不消费样本。
- 洗牌使用版本化 Fisher–Yates，并按稳定 PlayerId 顺序消费同一个 RNG。

AI 和视觉表现必须使用其他随机源。

## 调用语义 V2

`RandomCallSchemaVersion = 2` 增加奇数路线正中心随从的双向移动选择，触发条件见 [ADR-011](011-automatic-movement.md)。合法方向选择先按来源 EntityId、IntentId 排序，两个候选按 LaneId 排序，共享全局 RNG；方向选定后才按目标槽位构造冲突组。只有两个合格候选时才调用 NextIndex，零个或单个不消费样本。

选择记录以 `(FrameId, EntityId, IntentId)` 为稳定调用键，包含候选、结果、调用前计数与样本消费量，并随帧写入回放。帧规划只计算拟提交的 RNG 状态，成功提交后才成为权威状态；整帧失败时回滚 RNG。方向选定后的普通槽位争用失败不回滚已完成的方向选择，也不重选方向。

## 兼容性

算法、seed expansion、bounded mapping 或调用条件变化必须同步更新协议声明、ProtocolHash 和测试向量；内部测试期只维护当前实现。
