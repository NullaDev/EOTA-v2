# EOTA v2 文档导航

## 玩家

| 文档 | 内容 |
| --- | --- |
| [新手教程](BeginnerGuide.zh-CN.md) | 第一局操作、回合流程、战斗、关键词和构筑规则 |
| [客户端功能与操作](GameplayGuide.zh-CN.md) | 游戏模式、界面操作、联机、回放与存档位置 |
| [核心世界观](Worldbuilding.zh-CN.md) | 世界背景与各方势力 |
| [中文卡表](CardTable.zh-CN.md) | 当前卡牌数值与效果，由内容工具生成 |
| [十五套体系牌组](Content/ArchetypeDecks.zh-CN.md) | 职业体系与示范构筑 |
| [Windows 发布包使用说明](Release-Readme.zh-CN.md) | 安装、启动、运行组件与许可 |

## 内容作者

| 文档 | 内容 |
| --- | --- |
| [内容与 Mod 文档](Content/README.zh-CN.md) | 内容制作入口与参考资料 |
| [卡牌 JSON 指南](CardJsonDesignGuide.zh-CN.md) | 自定义卡牌、效果、协议与内容包 |
| [完整教学示例](Content/Examples/README.zh-CN.md) | 可编译的卡牌、文本、协议与牌组 |
| [卡牌费用模型](Content/CardCostModel.zh-CN.md) | 卡牌设计与职业边界的比较尺度 |
| [开发工具与内容制作](../tools/README.zh-CN.md) | 内容编译、卡表生成、回放与卡图制作 |

## 代码贡献者

| 文档 | 内容 |
| --- | --- |
| [总体架构](Architecture.zh-CN.md) | 项目依赖、规则内核、服务端与客户端边界 |
| [规则决策记录](ADR/README.md) | 规则语义与决策状态 |
| [传输契约](Transport/README.zh-CN.md) | 消息格式、开发连接与房间模式 |
| [界面设计规范](UIGuideline.zh-CN.md) | 配色、文字、控件与页面布局 |
| [Godot 场景说明](../Client/Godot/Scenes/README.zh-CN.md) | 场景职责、数据绑定与交互入口 |
| [Windows 打包说明](WindowsRelease.zh-CN.md) | 从源码构建与验证发布包 |

## 项目目录

| 目录或入口 | 职责 |
| --- | --- |
| `project.godot`、`Eota.Godot.csproj`、`Client/Godot/` | Godot 客户端工程、场景、主题与界面脚本 |
| `Eota.sln`、`src/` | 纯 C# 规则内核、内容编译器、消息契约、客户端和服务端各层 |
| `Content/Source/`、`Content/Schemas/` | 卡牌、协议、牌组、本地化、美术源文件与格式定义 |
| `Content/Generated/` | 客户端读取的生成内容与美术资源，纳入版本控制 |
| `tests/` | 内核、代数性质、内容编译、架构和服务端集成测试；`Fixtures/` 保存固定输入 |
| `tools/` | 内容、回放、卡图、发布和验证工具 |
| `Docs/` | 随仓库公开的玩家说明、内容指南与技术参考 |
| `artifacts/`、`exports/` | 可重新生成的验证产物和发布输出，由 Git 忽略 |
| `.local/Docs/` | 仅保存在本机的实施计划、审查记录与工作笔记，由 Git 忽略 |

## 文档维护约定

玩家使用说明、Mod 格式、架构边界、规则决策和可复用的工具说明保存在公开文档中。实施排期、阶段完成度、临时测试结果、代码审查记录和一次性操作笔记放入 `.local/Docs/`；公开文档不链接这些本地文件。

日志、截图与临时内容包放在 `artifacts/`，不要用它保存唯一一份工作笔记。正式测试输入放在 `tests/Fixtures/`，生成内容通过对应工具更新。Windows 打包复制公开的 `Docs/`，本地笔记目录不随包分发。
