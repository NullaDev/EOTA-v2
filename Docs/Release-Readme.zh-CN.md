# EOTA v2 Windows 测试版

将 ZIP 完整解压到一个文件夹，双击 `EOTA.exe` 启动。请保留同目录下的全部文件和文件夹。此包包含 Godot 和 .NET 运行组件，无需安装 Godot 编辑器或 .NET SDK。

支持 Windows 64 位系统，使用 OpenGL 兼容渲染。推荐窗口为 1600×1000，也可使用 1280×800。

在大厅中可进入本地双人对战、AI 对战、牌组工坊、卡牌图鉴和卡牌编辑器。“本机开服”可启动附带的独立服务端；另一位玩家通过邀请地址加入，双方使用兼容的卡牌规则。跨设备联机需要可达的网络地址及端口。

牌组、协议、自定义内容、回放和房间存档默认保存在 `%APPDATA%\Godot\app_userdata\EOTA v2\`。更新游戏时可以解压到新目录，存档仍保留。

初次游玩请阅读 `Docs/BeginnerGuide.zh-CN.md`，其中介绍开局操作、完整回合流程、战斗与资源规则。中文卡表见 `Docs/CardTable.zh-CN.md`，客户端各功能与操作见 `Docs/GameplayGuide.zh-CN.md`，卡牌编辑说明见 `Docs/CardJsonDesignGuide.zh-CN.md`。实际构建信息及卡牌规则哈希见 `release-manifest.json`。当前卡牌属于实验设计，尚未验证平衡性。

Copyright (C) 2026 EOTA contributors. 项目原创代码、工具、文档与游戏内容采用 GNU GPLv3（GPL-3.0-only），完整条款见包内根目录的 `LICENSE`。源码与构建说明见 [项目仓库](https://github.com/NullaDev/EOTA-v2)。欢迎通过 [Issues](https://github.com/NullaDev/EOTA-v2/issues) 分享卡牌、玩法与世界观想法，或提交代码和内容改进。

第三方组件和素材保留各自的版权与许可。本游戏使用 [Godot Engine](https://godotengine.org)，引擎及其第三方组件许可随包保存在 `Docs/Godot-LICENSES.txt`。[Godot 许可说明](https://godotengine.org/license/)可在线查阅；.NET 组件许可随运行库提供。Emoji Kitchen 融合图及生成卡图中包含的第三方图像部分不适用本项目的原创内容授权，相关来源与制作流程见项目仓库的 `tools/README.zh-CN.md`。
