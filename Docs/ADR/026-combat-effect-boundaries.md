# ADR-026：战斗阶段效果、对撞和额外伤害

- 状态：Proposed
- 日期：2026-09-09
- 承接：ADR-014、ADR-018

## 决定

combatStage 在进入战斗阶段时为场上来源收集，不要求来源具有攻击资格。阶段效果完整执行后冻结对战宣告；其后 combat/attack 根分别对应实际参战/主动攻击事实。minionCombat 只对应随从之间对撞，eventTarget 绑定宣告中的具体对手，不把“敌方有迟缓随从但实际攻击英雄”误当对撞。

迫击炮类能力使用 combatDamage 根，将额外英雄伤害直接并入当前战斗伤害帧。只在来源具有随从伤害资格、调整后实际伤害为正时求值；event.amount 为这份伤害值。根只允许条件分支、Parallel 和英雄 Damage，禁止在此时点引入续执行或任意生命周期操作。因此同归于尽时贡献仍在同帧结算，之后的普通触发受终局短路限制。

loseHealth 直接减少随从生命，不应用承伤修正，不触发受伤监听；仍可导致正常死亡。

场地强度即耐久，数值效果使用 `fieldEnergy`，见 [ADR-028](028-field-durability-and-deck-rule-scope.md)。
