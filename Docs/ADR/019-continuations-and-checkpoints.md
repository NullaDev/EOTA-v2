# ADR-019：效果续执行与检查点

- 状态：Proposed
- 日期：2026-09-09
- 承接：ADR-006、ADR-018

## 决定

Sequence 每一步使用前一步已经提交的回执结果；Parallel 的子分支读取共同快照，所有分支结束后才继续外层 Sequence。Applied、PartiallyApplied、NoOp 继续；Rejected、Error 停止当前顺序分支。条件不成立与空选择产生 NoOp。

Loop 的次数在进入循环时冻结，并受协议上限约束；while 只在每次迭代开始前判断，已经开始的迭代可以执行完整 Sequence。嵌套循环各有自己的零起始 index。ForEach 冻结本次选中的身份，后续动作仍校验对象是否可用。

每个效果程序使用稳定 ProgramId、显式游标树、目标绑定、上一结果、循环位置与等待提交的 IntentId。状态同时保存系统阶段游标、待执行根、延迟入场根、必要的 Receipt Ledger、战斗宣告快照和迟缓应用代数。所有字段进入状态 canonical encoding；不保存闭包、迭代器或进程堆栈。

结果集合按类型和稳定身份去重排序；标量按子结果相加。结果传递溢出不会撤销已经完成的帧，而是在下一因果帧提交稳定错误。达到终局后清空待执行程序。

## 私有持久化边界

检查点由 Infrastructure 的白名单 JSON codec 编解码，规则包从已验证的 manifest 注入；保存 schema 和完整状态哈希。还原后通过同一 TurnResolver 继续，再接收剩余命令。该文件包含双方牌库与 RNG，不是 Transport Contract，不能发给玩家或观战端。
