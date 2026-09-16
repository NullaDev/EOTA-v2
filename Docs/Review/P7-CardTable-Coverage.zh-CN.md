# P7 CardTable 能力映射审计

本文保留 P7 阶段首批 132 张实验卡的能力映射，所需机制和对应 IR／Intent 路径直接列在下表。该阶段的映射不表示卡牌已逐张完成运行验收；后续录入见 [P8 验收](P8-Content-Review.zh-CN.md)，当前定义和数值见 [230 张卡表](../CardTable.zh-CN.md)。组合测试及边界测试见 [P7 检查点](P7-Complex-Effects-Review.zh-CN.md)。

时序需要显式区分 Parallel 与 Sequence；目标条件通过 Retarget/ForEach 绑定。静态身材和关键词沿用 P4，未在每行重复列出。手牌窗口和临时基础攻击采用 ADR-020、ADR-024 的开发提案。

| ID | 卡名 | IR / 规则能力 |
|---|---|---|
| `EOTA-CORE-ARC-MIN-008` | 以太信使 | 入场触发；If/Compare；EtherActivation Add/Set/Clear |
| `EOTA-CORE-ARC-MIN-001` | 奥术学徒 | combat；lane.ether 条件；Numeric Attack；duration=1；Numeric/Keyword |
| `EOTA-CORE-ARC-MIN-002` | 符文见习生 | turnEnd；lane.ether 条件；Draw + 可选 Filter |
| `EOTA-CORE-ARC-MIN-007` | 远古残响 | selfDied/Frozen Source；EtherActivation Add/Set/Clear |
| `EOTA-CORE-ARC-MIN-003` | 奥术蓄积者 | turnEnd；lane.ether 条件；Numeric/Keyword |
| `EOTA-CORE-ARC-MIN-004` | 秘法决斗者 | combat；lane.ether 条件；Numeric/Keyword |
| `EOTA-CORE-ARC-MIN-005` | 流光术士 | friendlySpellCast；If/Compare/Exists；EtherActivation Add/Set/Clear |
| `EOTA-CORE-ARC-MIN-009` | 奥术锚点 | turnEnd；lane.ether 条件；EtherActivation Add/Set/Clear；PreventEtherDecay；Damage |
| `EOTA-CORE-ARC-MIN-006` | 协会管理员 | combat；EtherActivation Add/Set/Clear；Numeric Attack；duration=1；Numeric/Keyword |
| `EOTA-CORE-ARC-SPL-001` | 奥术火花 | lane.ether 条件；Damage |
| `EOTA-CORE-ARC-SPL-002` | 引导以太 | EtherActivation Add/Set/Clear；Draw + 可选 Filter |
| `EOTA-CORE-ARC-SPL-003` | 秘法飞弹 | lane.ether 条件；Damage |
| `EOTA-CORE-ARC-SPL-007` | 以太回流 | Sequence(Draw，ClearEther，Draw count=previous.scalar) |
| `EOTA-CORE-ARC-SPL-010` | 秘能释放 | lane.ether 条件；Damage |
| `EOTA-CORE-ARC-SPL-004` | 火球术 | lane.ether 条件；Damage |
| `EOTA-CORE-ARC-SPL-011` | 以太灌注 | EtherActivation Add/Set/Clear |
| `EOTA-CORE-ARC-SPL-008` | 奥术爆震 | 首轮 Parallel Damage；ClearEther 后按 previous.scalar 有界 Loop Damage |
| `EOTA-CORE-ARC-SPL-005` | 陨星术 | lane.ether 条件；Damage；Kill |
| `EOTA-CORE-ARC-SPL-006` | 烈焰风暴 | 本路 Damage；共鸣条件下相邻路线 Damage，英雄贡献显式列出 |
| `EOTA-CORE-ARC-SPL-009` | 以太风暴 | 全场随从 Damage(3 + owner.resonatingLanes) |
| `EOTA-CORE-ARC-SPL-012` | 唤醒地脉 | EtherActivation Add/Set/Clear；scope=all |
| `EOTA-CORE-ARC-FLD-001` | 微光法阵 | 入场触发；EtherActivation Add/Set/Clear |
| `EOTA-CORE-ARC-FLD-003` | 暴风雪 | 静态 preventsActiveAttacksInLane；selfEntered 共鸣分支 → FieldEnergy Add |
| `EOTA-CORE-ARC-FLD-004` | 静滞法阵 | 入场触发；lane.ether 条件；Numeric Attack；duration=2；Numeric/Keyword |
| `EOTA-CORE-ARC-FLD-006` | 不稳定裂隙 | 入场触发；selfDied/Frozen Source；EtherActivation Add/Set/Clear |
| `EOTA-CORE-ARC-FLD-002` | 聚能法阵 | selfEntered Ether Add；turnEnd PreventEtherDecay |
| `EOTA-CORE-ARC-FLD-005` | 奥术尖塔 | turnEnd；lane.ether 条件；Damage |
| `EOTA-CORE-ARC-FLD-008` | 大图书馆的投影 | 入场触发；turnEnd；lane.ether 条件；EtherActivation Add/Set/Clear；Draw + 可选 Filter |
| `EOTA-CORE-ARC-FLD-007` | 破碎星门 | 入场触发；selfDied/Frozen Source；EtherActivation Add/Set/Clear；Kill |
| `EOTA-CORE-ART-MIN-001` | 发条侦察机 | 充能整批判断；Numeric Attack；duration=1；Numeric/Keyword |
| `EOTA-CORE-ART-MIN-003` | 机械犬 | 基础数值与内建关键词 |
| `EOTA-CORE-ART-MIN-004` | 废料搬运机 | selfDied/Frozen Source；Summon |
| `EOTA-CORE-ART-MIN-005` | 螺栓投射器 | 充能整批判断；Damage |
| `EOTA-CORE-ART-MIN-010` | 轻型反应炉 | storedCharge |
| `EOTA-CORE-ART-MIN-002` | 蒸汽堡垒 | preCombatCharge → baseAttack Set(duration=1)；guard |
| `EOTA-CORE-ART-MIN-006` | 产线巡逻车 | 基础数值与内建关键词 |
| `EOTA-CORE-ART-MIN-007` | 履带运输机 | 充能整批判断；Draw + 可选 Filter |
| `EOTA-CORE-ART-MIN-014` | 魔能试验机 | selfDied/Frozen Source；Damage |
| `EOTA-CORE-ART-MIN-017` | 过载核心 | selfDied/Frozen Source；storedCharge；CardCost/PlayerResource |
| `EOTA-CORE-ART-MIN-015` | 不稳定的魔能机甲 | attack；Damage |
| `EOTA-CORE-ART-MIN-008` | 遗迹守护者 | preCombatCharge → baseAttack Set(duration=1)；slow |
| `EOTA-CORE-ART-MIN-011` | 反应炉机兵 | storedCharge |
| `EOTA-CORE-ART-MIN-012` | 太阳能战车 | 充能整批判断；storedCharge；duration=1；Numeric/Keyword |
| `EOTA-CORE-ART-MIN-016` | 失控的魔能巨像 | turnEnd；Kill |
| `EOTA-CORE-ART-MIN-009` | 移动要塞 | preCombatCharge → Parallel(临时移除 guard，baseAttack Set)；期限清理 |
| `EOTA-CORE-ART-MIN-013` | 自持式堡垒 | 充能整批判断；storedCharge；Numeric MaximumHealth/DamageTaken；Numeric/Keyword |
| `EOTA-CORE-ART-SPL-003` | 紧急维修 | If/Compare/Exists；CardFilter(mechanical)；Heal；Numeric MaximumHealth/DamageTaken |
| `EOTA-CORE-ART-SPL-004` | 紧急拆除 | If/Compare/Exists；CardFilter(mechanical)；Summon；Kill |
| `EOTA-CORE-ART-SPL-009` | 危险试运行 | 机械筛选 → Parallel(临时 firstStrike，临时 GrantEffect turnEnd Damage self) |
| `EOTA-CORE-ART-SPL-005` | 回收利用 | If/Compare/Exists；CardFilter(mechanical)；Draw + 可选 Filter；Kill |
| `EOTA-CORE-ART-SPL-006` | 魔能灌涌 | Parallel(永久 chargeRequirement Set 0，GrantEffect turnEnd Damage self) |
| `EOTA-CORE-ART-SPL-007` | 能源传输 | CardCost/PlayerResource |
| `EOTA-CORE-ART-SPL-010` | 图纸分析 | Sequence(Draw filter.tag=mechanical，previousAffected/card → +1/+1) |
| `EOTA-CORE-ART-SPL-001` | 制造反应核心 | CardCost/PlayerResource |
| `EOTA-CORE-ART-SPL-008` | 不稳定引爆 | If/Compare/Exists；CardFilter(mechanical)；Damage；Kill |
| `EOTA-CORE-ART-SPL-011` | 电击 | If/Compare/Exists；CardFilter(mechanical)；Damage；Numeric/Keyword |
| `EOTA-CORE-ART-SPL-002` | 考古式研发 | Parallel(全场友方随从、手牌、牌库)机械过滤 → attack/maximumHealth Add |
| `EOTA-CORE-ART-SPL-012` | 蒸汽喷发 | CardFilter(mechanical)；Damage；scope=all |
| `EOTA-CORE-ART-FLD-001` | 备用电池组 | 充能整批判断；CardCost/PlayerResource |
| `EOTA-CORE-ART-FLD-002` | 检修车间 | 充能整批判断；If/Compare/Exists；CardFilter(mechanical)；Heal；Numeric MaximumHealth/DamageTaken |
| `EOTA-CORE-ART-FLD-003` | 垃圾场 | friendlyDied/minion → CardMatches(event, mechanical) → Summon |
| `EOTA-CORE-ART-FLD-004` | 临时锅炉 | storedCharge |
| `EOTA-CORE-ART-FLD-005` | 废土发电站 | entryStage → chargeRequirement Set 0(duration=1) |
| `EOTA-CORE-GUA-MIN-001` | 城防士兵 | 基础数值与内建关键词 |
| `EOTA-CORE-GUA-MIN-006` | 郊区巡警 | 基础数值与内建关键词 |
| `EOTA-CORE-GUA-MIN-008` | 民兵小队长 | selfDied → Generate；Frozen Source 保留来源 |
| `EOTA-CORE-GUA-MIN-016` | 战场逃兵 | 基础数值与内建关键词 |
| `EOTA-CORE-GUA-MIN-004` | 民兵征募官 | 入场触发；Generate |
| `EOTA-CORE-GUA-MIN-005` | 火枪列兵 | 基础数值与内建关键词 |
| `EOTA-CORE-GUA-MIN-009` | 战斗英雄 | combat；Generate |
| `EOTA-CORE-GUA-MIN-012` | 鲁莽掷弹兵 | 入场触发；Damage |
| `EOTA-CORE-GUA-MIN-002` | 城防枪手 | 基础数值与内建关键词 |
| `EOTA-CORE-GUA-MIN-003` | 城防重炮手 | 基础数值与内建关键词 |
| `EOTA-CORE-GUA-MIN-007` | 矿区爆破手 | combatStage → If !Exists(enemyMinions) → Kill(enemyFields) |
| `EOTA-CORE-GUA-MIN-010` | 清道夫 | 基础数值与内建关键词 |
| `EOTA-CORE-GUA-MIN-013` | 盾墙教官 | turnEnd；Numeric MaximumHealth/DamageTaken；Numeric/Keyword |
| `EOTA-CORE-GUA-MIN-014` | 民兵大队长 | selfDied → Sequence(Generate×2，previousCreated/card → cardCost Set 0) |
| `EOTA-CORE-GUA-MIN-011` | 迫击炮小队 | combatDamage → 同帧英雄 Damage(event.amount)；slow |
| `EOTA-CORE-GUA-MIN-015` | 城邦总指挥 | selfEntered → friendlyMinions/otherLanes + filter.profession=guardian → +1/+1 |
| `EOTA-CORE-GUA-SPL-001` | 举起盾牌！ | If/Compare；Numeric MaximumHealth/DamageTaken；Numeric/Keyword |
| `EOTA-CORE-GUA-SPL-004` | 紧急动员 | Summon |
| `EOTA-CORE-GUA-SPL-012` | 空投补给 | Retarget → 条件 attack Set；maximumHealth 下限 + damageTaken Set 表达当前生命下限 |
| `EOTA-CORE-GUA-SPL-013` | 整训队伍 | ForEach friendlyHand/minion → HasKeyword(replaceable) → Parallel(+1/+1，RemoveKeyword) |
| `EOTA-CORE-GUA-SPL-002` | 换防 | Sequence(Return，Summon)；Rejected 回手停止，成功创建新手牌身份 |
| `EOTA-CORE-GUA-SPL-006` | 火力支援 | Damage |
| `EOTA-CORE-GUA-SPL-009` | 紧急征召 | Draw + 可选 Filter |
| `EOTA-CORE-GUA-SPL-011` | 破片手雷 | Damage |
| `EOTA-CORE-GUA-SPL-005` | 据点加固 | Numeric MaximumHealth/DamageTaken；FieldEnergy；Numeric/Keyword |
| `EOTA-CORE-GUA-SPL-010` | 征兵令 | Generate |
| `EOTA-CORE-GUA-SPL-007` | 火力覆盖 | Damage；Kill |
| `EOTA-CORE-GUA-SPL-003` | 冲锋！ | Numeric Attack；duration=1；Numeric/Keyword；scope=all |
| `EOTA-CORE-GUA-SPL-008` | 坚守阵线 | Summon；scope=all |
| `EOTA-CORE-GUA-FLD-001` | 临时街垒 | 入场触发；Numeric MaximumHealth/DamageTaken；Numeric/Keyword |
| `EOTA-CORE-GUA-FLD-002` | 城防箭塔 | 入场触发；If/Compare/Exists；Damage |
| `EOTA-CORE-GUA-FLD-003` | 城防哨站 | 入场触发；combat；Numeric Attack；Numeric/Keyword |
| `EOTA-CORE-GUA-FLD-004` | 兵器库 | 入场触发；If/Compare/Exists；Numeric Attack |
| `EOTA-CORE-GUA-FLD-005` | 征兵广场 | turnEnd → If Exists(friendlyMinions) Generate else Summon |
| `EOTA-CORE-HUN-MIN-001` | 沼泽蟾蜍 | minionCombat → GrantEffect(eventTarget, turnEnd LoseHealth 2) |
| `EOTA-CORE-HUN-MIN-005` | 驯兽师 | replacementEntered → CardMatches(replaced, beast) → attack Add replaced.attack |
| `EOTA-CORE-HUN-MIN-002` | 棘背豪猪 | selfDamaged；Damage |
| `EOTA-CORE-HUN-MIN-004` | 沼泽巨蟒 | 基础数值与内建关键词 |
| `EOTA-CORE-HUN-MIN-006` | 猎犬饲养员 | turnEnd；Generate |
| `EOTA-CORE-HUN-MIN-007` | 陷阱大师 | selfEntered → FieldPower Set 3；与 lifetime.energy 分离 |
| `EOTA-CORE-HUN-MIN-008` | 饥饿狼群 | combat；If/Compare；Damage |
| `EOTA-CORE-HUN-MIN-011` | 运输驮马 | 入场触发；Draw + 可选 Filter |
| `EOTA-CORE-HUN-MIN-003` | 密林追踪者 | 基础数值与内建关键词 |
| `EOTA-CORE-HUN-MIN-009` | 吸血蝠 | 基础数值与内建关键词 |
| `EOTA-CORE-HUN-MIN-010` | 远古猛犸象 | 基础数值与内建关键词 |
| `EOTA-CORE-HUN-SPL-001` | 迅捷射击 | Damage |
| `EOTA-CORE-HUN-SPL-002` | 翻滚闪避 | If/Compare/Exists；duration=1；Numeric/Keyword |
| `EOTA-CORE-HUN-SPL-003` | 放出猎犬 | If Exists → 追猎和攻击增益；否则 Sequence(Summon，previousCreated/minion → swift) |
| `EOTA-CORE-HUN-SPL-004` | 猎人标记 | Damage；duration=1 |
| `EOTA-CORE-HUN-SPL-005` | 驯服野兽 | CardFilter(beast)；Draw + 可选 Filter |
| `EOTA-CORE-HUN-SPL-008` | 收集战利品 | GrantEffect(friendlyHero, enemyDied/minion → Draw, duration=1) |
| `EOTA-CORE-HUN-SPL-006` | 兽群本能 | ForEach 全场友方兽类 → +1 attack；HasKeyword(pursuit) 额外 +1 |
| `EOTA-CORE-HUN-SPL-007` | 荒野围猎 | Summon；scope=all |
| `EOTA-CORE-HUN-FLD-001` | 夹子陷阱 | enemyEntered/minion → Parallel(Damage eventSubject，自身 Kill) |
| `EOTA-CORE-HUN-FLD-002` | 毒雾陷阱 | enemyEntered/minion → Parallel(GrantEffect 永久 turnEnd LoseHealth，自身 Kill) |
| `EOTA-CORE-HUN-FLD-003` | 尖刺陷阱 | enemyAttack → Parallel(Damage eventSubject，自身 Kill) |
| `EOTA-CORE-HUN-FLD-004` | 爆炸陷阱 | enemyAttack → Parallel(Kill 双方随从，Kill self) |
| `EOTA-CORE-NEU-MIN-001` | 哥布林雇佣兵 | 基础数值与内建关键词 |
| `EOTA-CORE-NEU-MIN-002` | 赏金猎人 | 入场触发；Damage |
| `EOTA-CORE-NEU-MIN-003` | 战争商人 | 入场触发；Draw + 可选 Filter |
| `EOTA-CORE-NEU-MIN-006` | 流浪医师 | 入场触发；Heal；Numeric MaximumHealth/DamageTaken |
| `EOTA-CORE-NEU-MIN-007` | 雇佣盾卫 | 基础数值与内建关键词 |
| `EOTA-CORE-NEU-MIN-004` | 荒野向导 | 基础数值与内建关键词 |
| `EOTA-CORE-NEU-MIN-005` | 重甲保镖 | 基础数值与内建关键词 |
| `EOTA-CORE-NEU-SPL-001` | 悬赏令 | Sequence(Damage，If previous.removedCount>0 → Draw) |
| `EOTA-CORE-NEU-SPL-002` | 处决令 | Kill |
| `EOTA-TOKEN-ART-MIN-001` | 破烂堆 | 基础数值与内建关键词 |
| `EOTA-TOKEN-GUA-MIN-001` | 民兵 | 基础数值与内建关键词 |
| `EOTA-TOKEN-HUN-MIN-001` | 猎犬 | 基础数值与内建关键词 |
