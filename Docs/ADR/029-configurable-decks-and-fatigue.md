# ADR-029：可配置构筑与独立玩家疲劳

- 状态：Proposed（按用户反馈用于开发版，公开协议尚未冻结）
- 日期：2026-09-13
- 澄清：2026-09-14，疲劳即死在抽空牌库后的下一次抽牌尝试斩杀，当前帧结束后统一判定胜负；现有实现语义不变。
- 关联：ADR-020 手牌分配窗口、ADR-028 对局定义范围校验

## 协议与构筑

将已有 RequiredDeckSize、MinCopiesPerCard、MaxCopiesPerCard 暴露给客户端。总张数为精确数量，限制 1–256；最少副本只约束已选卡种，为正且不超过总张数；最大副本不少于最少值且不超过 256。起手仍不能超过牌组或手牌上限。每回合抽牌数限制为 0–256。

DeckConstructionPolicy 包含正常（CoreAndProfessionOrNeutral）、仅本职业（CoreProfessionOnly）、无限制（CoreAnyProfession）及开发专用（DevelopmentAnySource）。玩家界面提供前三项，均要求 Core 来源；“无限制”只取消职业限制，不将衍生卡混入基础构筑。DesktopCatalog 和 MatchFactory 使用同一个 IsCardAllowed 判断，服务端不信任前端筛选结果。

卡组草稿切换协议保留原卡牌，显示不合法项并允许移除，不隐式删卡。保存和准入均调用选定协议的正式构筑校验。开服配置不再要求对方参考牌组，由该席位加入时提交。两位玩家确认后才创建 MatchActor；规则比对继续使用双方牌组并集及其递归依赖。

## 疲劳归约

DeckExhaustionPolicy 包含 NoFatigue、IncreasingDamage、InstantDeath。每位 PlayerState 保存 Int64 FatigueCount，初始为 0。

有效 Draw 请求在牌库身份分配时按现有 PlayerId、AllocationKey、IntentId 顺序执行。若实际牌库为空：NoFatigue 返回原 draw-no-match；另外两项先令该玩家计数加 1，递增规则将英雄生命减去新计数，即死规则将生命设为 min(当前值, 0)。生成不触发疲劳；牌库非空但筛选失败也不触发。

抽走或因满手烧掉最后一张牌不触发即死；下一次空牌库抽牌尝试直接斩杀，可以发生在同一帧的后续抽牌请求中。即死采用上述直接致死处理，不作为普通伤害数值抵消同帧治疗；其他手牌分配仍完成，统一在当前帧结束后判定结果。

疲劳在 CompleteHandRequests 中执行，位于该帧普通英雄数值归约之后、最终状态提交与死亡判定之前。计数和生命使用前一条请求归约后的结果，产生 Applied 回执 fatigue-damage／fatigue-death 和 HeroHealthChanged 事件。两席位都完成该帧归约后再判定胜负，不因处理顺序提前结束而遮蔽同帧平局。

满手烧掉实际抽出的身份，后续空抽仍可疲劳。BeginNextTurn 在启用疲劳时发出完整 CardsDrawnPerTurn 次请求；不疲劳时最多抽取剩余牌库张数。效果抽牌复用同一路径。

## 持久化

状态规范编码在玩家 NextTurnCost 后编码 FatigueCount；观察者公开玩家投影包含该计数。恢复校验拒绝负数及不留分配余量的极端计数。断点和命令回放恢复相同计数，下一次抽牌继续递增。

GameProtocolDefinition.MatchStateCanonicalVersion 直接绑定 MatchStateHasher.CanonicalSchemaVersion。菜单、房间、回放和检查点统一校验当前格式。

测试覆盖三种疲劳、效果抽牌、满手烧牌、筛选失败、双方同帧死亡、计数哈希、断点后的连续疲劳和桌面回放。
