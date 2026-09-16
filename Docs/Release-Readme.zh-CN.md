# EOTA v2 Windows 测试版

将 ZIP 完整解压到一个文件夹，双击 `EOTA.exe` 启动。请保留同目录下的全部文件和文件夹。此包包含 Godot 和 .NET 运行组件，无需安装 Godot 编辑器或 .NET SDK。

支持 Windows 64 位系统，使用 OpenGL 兼容渲染。推荐窗口为 1600×1000，也可使用 1280×800。

在大厅中可进入本地双人对战、AI 对战、牌组工坊、卡牌图鉴和卡牌编辑器。“本机开服”可启动附带的独立服务端；另一位玩家通过邀请地址加入，双方使用兼容的卡牌规则。跨设备联机需要可达的网络地址及端口。

牌组、协议、自定义内容、回放和房间存档默认保存在 `%APPDATA%\Godot\app_userdata\EOTA v2\`。更新游戏时可以解压到新目录，存档仍保留。

中文卡表见 `Docs/CardTable.zh-CN.md`，玩法见 `Docs/GameplayGuide.zh-CN.md`，卡牌编辑说明见 `Docs/CardJsonDesignGuide.zh-CN.md`。实际构建信息及卡牌规则哈希见 `release-manifest.json`。当前卡牌属于实验设计，尚未验证平衡性。

本游戏使用 [Godot Engine](https://godotengine.org)，引擎及其第三方组件许可随包保存在 `Docs/Godot-LICENSES.txt`。[Godot 许可说明](https://godotengine.org/license/)可在线查阅；.NET 组件许可随运行库提供。
