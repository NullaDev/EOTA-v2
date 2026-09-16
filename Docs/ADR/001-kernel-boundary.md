# ADR-001：Kernel 纯度与依赖方向

- 状态：Accepted
- 日期：2026-09-04
- 接受日期：2026-09-06

## 决定

`Eota.Kernel` 是 BCL-only 的普通 `net8.0` 类库。它不引用 Godot、ASP.NET、Transport DTO、文件系统、数据库、网络、系统时间、环境变量、日志框架或 DI 容器。

Kernel 的全部权威输入由 `MatchManifest`、已编译协议、已编译规则内容、种子和命令显式提供；输出是新状态、命令/意图回执、领域事件与规范哈希材料。文件、网络、动画、鉴权和持久化由外层 Adapter 完成。

## 理由

同一 Kernel 必须能在 Server、Replay CLI、测试进程和本地进程内宿主中得到相同结果。任何框架环境或隐式服务都会削弱这个保证。

## 约束

- Kernel 不公开可从外部修改的状态集合。
- 实现内部可以使用临时 mutable builder，但引用不能逃出一次调用。
- AI、视觉随机和超时均在 Kernel 外；最终结果以显式命令进入。
- Architecture Tests 必须阻止反向项目引用。
