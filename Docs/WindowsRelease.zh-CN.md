# Windows Release 打包

在仓库根目录使用 PowerShell 执行。构建机需要 .NET SDK 8.0.4xx、Godot 4.6.2 .NET 和对应的 Windows .NET 导出模板。

```powershell
./tools/Package-WindowsRelease.ps1 `
  -GodotPath 'D:/Program Files/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64_console.exe' `
  -Version '1.0.0-prerelease'
```

将 GodotPath 替换为本机路径。Version 默认读取 `project.godot` 的 `config/version`，当前为 `1.0.0-prerelease`；无需按打包日期或尝试次数修改版本。可显式传入 `-Version` 覆盖本次包的版本。默认输出为 `exports/EOTA-v2-<Version>-win-x64.zip`，同目录生成 `.zip.sha256` 校验文件，包内构建信息与 Godot 项目版本保持一致。已有同版本输出时脚本会停止；重复打同一版本可用 OutputDirectory 指定另一输出目录，保留原包。

脚本只复制 `Client`、`src` 和明确列出的工程文件到独立的 `artifacts/release-<Version>-<构建标识>/project`，在该临时工程中导入和导出；从 `Content/Source` 编译 230 张当前卡牌，复制已生成的 PNG。客户端和服务端均使用 Release 配置，并附带各自的 .NET 运行组件。包内还包含外置 `Content/Generated`、中文文档、引擎许可和构建信息。

发布版从 EXE 所在目录读取外置卡牌和服务端，启动不依赖当前工作目录。应分发完整 ZIP，用户解压后运行 `EOTA.exe`。

验证归档：

```powershell
./tools/Test-WindowsRelease.ps1 -ArchivePath './exports/EOTA-v2-1.0.0-prerelease-win-x64.zip'
```

验证脚本核对 SHA-256，解压到新的 `artifacts/release-test-*` 目录，使用随包 EXE 依次检查：从其他目录启动、本地对局及回放、垃圾回收期间反复创建卡牌场景、三档 AI、1280×800 界面、双进程开服加入／重连／补帧／恢复。运行环境的 PATH 不包含开发工具，并为进程指定独立的用户数据目录和空的全局 .NET 路径。双进程开服检查使用本机 5097 端口。排查单项时可用 `-Checks startup,local,scenes,ai,ui,hosting` 指定子集；正式验收默认运行全部检查。

日志和截图保存在验证目录。此检查覆盖当前 Windows 机器，不等同于在全新操作系统、所有显卡驱动或公网环境上完成验收。运行库随包齐全与实际验证结果均应记录后再上传 Release。

导出命令和预设格式参考 [Godot 4.6 导出文档](https://docs.godotengine.org/en/4.6/tutorials/export/exporting_projects.html)。
