# AutoVolume 音量自动闪避

![AutoVolume 浅色界面](assets/screenshot-light.png)

![AutoVolume 深色界面](assets/screenshot-dark.png)

AutoVolume 是一个 Windows 托盘小工具：当它发现你**占用了麦克风**（语音输入、通话、录音）或**正在播放视频**时，会把音乐软件的音量自动压下去，或者干脆暂停；结束后再原样还回来。

不装驱动、不接管声卡、不修改系统设置，只操作 Windows 自带的「按应用音量」。

## 它解决什么问题

- 用豆包输入法、微信语音、会议软件说话时，酷狗还在放歌，听不清自己说什么。
- 在浏览器或 PotPlayer 看视频时，背景音乐干扰台词。
- 用完语音输入后，又要手动把音乐调回来。

## 功能

- **麦克风占用触发**：任何非系统进程占用麦克风时触发。按「谁占用了麦克风」判断，与说话音量无关，不会被音箱串音误触发。
- **视频触发**：浏览器 / PotPlayer 有声音输出时触发。
- **每个目标独立配置**：两条触发路径可以分别设为「不处理 / 压低音量 / 暂停播放」，压低比例单独可调。
- **实时反馈**：界面里每个应用名下面有一条底色进度条，显示当前音量或电平。
- **自动恢复**：触发结束、关闭程序、甚至程序被强杀后重新启动，都会把音量还原。
- **深色模式**：右上角一键切换浅色 / 深色，标题栏、滚动条、进度条一并跟随，选择写入配置。
- **记住窗口**：退出时保存窗口大小与位置，下次在原处打开。
- **便携**：程序是单个 exe，配置 `settings.json` 和日志 `log.txt` 都写在 exe 同级目录。

## 工作原理

| 环节 | 做法 |
|---|---|
| 调音量 | WASAPI 会话级音量（`ISimpleAudioVolume`），即系统「音量合成器」里每根应用滑块 |
| 检测麦克风占用 | 枚举录音设备上的 capture session，有别的进程处于 Active 即视为占用 |
| 检测视频播放 | 读取浏览器 / PotPlayer 会话的峰值电平，超过阈值并持续一段时间 |
| 暂停酷狗 | 模拟你在酷狗里配置的全局热键（酷狗不向系统注册媒体会话） |
| 暂停其他应用 | Windows 全局媒体会话（GSMTC），可精确暂停/恢复 |

## 系统要求

- Windows 10 / 11（x64）
- [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)（当前发布的是框架依赖版本）

## 快速开始

1. 从 [Releases](https://github.com/Lose-Memory/Automatic_Volume_Evasion/releases) 下载 `AutoVolume.exe`。
2. 放到一个**可写的普通目录**（不要放 `C:\Program Files`，否则无法写配置）。
3. 双击运行，程序缩到托盘。
4. 单击托盘图标打开设置页。

首次运行会在 exe 旁边生成 `settings.json` 和 `log.txt`。把 exe 和 `settings.json` 一起拷走即可迁移。

## 让酷狗能够被暂停（重要）

酷狗不向 Windows 注册媒体会话，所以只能通过它自己的全局热键来控制：

1. 打开酷狗 → 右上角菜单 → **设置** → **快捷键**
2. 找到 **播放 / 暂停**，设置为 `Ctrl + Shift + Alt + F8`
3. 在 AutoVolume 设置页的「酷狗暂停热键」里填同样的组合

如果不需要暂停功能，把目标的动作设为「压低音量」即可，无需配置热键。

## 参数说明

| 参数 | 含义 |
|---|---|
| 阈值 (dB) | 视频触发用的电平门限。低于它视为没在播放 |
| 持续 (ms) | 条件需要连续维持多久才算触发，防止误触 |
| 保持 (ms) | 条件消失后继续维持触发多久才解除，防止一句话中间的停顿导致音量抖动 |
| 压低到 | 触发时把音量降到当前值的百分之多少 |
| 电平刷新间隔 | 界面进度条和数字的刷新周期（默认 1000 ms，范围 100–5000） |

## 配置文件

`settings.json` 与 exe 同级，程序会自动保存。删除它即可恢复默认配置。

```jsonc
{
  "Mic":   { "Enabled": true,  "ThresholdDb": -45, "SustainMs": 200, "HoldMs": 400 },
  "Video": { "Enabled": true,  "ThresholdDb": -50, "SustainMs": 300, "HoldMs": 400 },
  "VideoSourceProcesses": ["msedge", "chrome", "PotPlayerMini64", "PotPlayerMini"],
  "KuGouPauseHotkey": { "Ctrl": true, "Shift": true, "Alt": true, "Key": "F8" },
  "Enabled": true,
  "RefreshMs": 1000,
  "Theme": "Light",
  "Window": { "Width": 1020, "Height": 720, "Left": 300, "Top": 160, "HasPosition": true }, 
  "Targets": [ /* 目标列表：进程名、触发动作、压低比例 */ ]
}
```

`Targets` 里的每一项对应界面中的一行，可以手动增删进程名来支持更多应用。

> 麦克风走的是「占用检测」，`Mic.ThresholdDb` 目前不参与判断，字段保留仅为兼容旧配置。

## 项目结构

```
Automatic_Volume_Evasion/
├── src/                     # 全部源码（C# + XAML）
├── assets/                  # README 用图
├── artifacts/               # 构建产物（git 忽略）
├── Directory.Build.props    # 统一把 bin/obj/publish 输出到 artifacts/
├── AutoVolume.slnx
├── README.md
└── LICENSE
```

## 从源码构建

```powershell
# 编译
dotnet build AutoVolume.slnx -c Release

# 发布单文件（框架依赖）
dotnet publish src/AutoVolume.csproj -c Release -r win-x64 `
  --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o artifacts/publish
```

产物为 `artifacts/publish/AutoVolume.exe`（约 26 MB，其中 25 MB 是 WinRT 投影库）。

依赖：

- [NAudio](https://github.com/naudio/NAudio) 2.2.1 — WASAPI 会话与电平
- `Microsoft.Windows.SDK.NET.Ref` — GSMTC 媒体会话控制

## 相关项目与致谢

### Degath's Ducker（闭源）

作者的站点是 `www.degathsducker.weebly.com`（写在程序内嵌字符串里），网上查不到公开仓库或源码；从 PDB 路径看，作者使用芬兰语环境开发。

逆向它的可执行文件可以确认：.NET Framework 4.5 + WinForms 的单文件托盘程序，用 NAudio 的 WASAPI 会话接口逐应用调音量；支持把「麦克风」或「某个应用的音频会话」作为触发器、把应用会话作为目标；内置多套模板（`TemplateDictionary`）、三段式音量包络（attack / sustain / release）、峰值滤波器，以及键盘和手柄热键。

它的致命问题是**配置保存有缺陷**：`SaveSettings()` 只在窗口移动、窗口尺寸变化、手动「保存」这几种情况下被调用，增删触发器/目标完全不落盘，于是配置完一重启就回到旧状态。这正是本项目启动的直接原因。

### DesktopMixer（开源，无许可证）

[github.com/CameronVigil/DesktopMixer](https://github.com/CameronVigil/DesktopMixer)，C# 编写的按应用音量混合器。

架构是三层：ASP.NET Core 8 + NAudio 的后端（`AudioEngine` 每 50 ms 一个 tick）、Vue 3 前端、WebView2 桌面壳。闪避模型是「阈值 + 压低比例 + attack / hold / release」，按 exe 名聚合会话，设置用防抖 + 原子写持久化，退出前还原音量。

本项目借鉴了它按 exe 名聚合会话、只在音量真正变化时才写入、退出前还原音量这几个做法；没有沿用它的三层架构——为了这个场景引入 Web 服务器和浏览器内核太重了。

> DesktopMixer 仓库没有 LICENSE 文件，按「保留所有权利」处理。本项目没有复制它的代码，只参考了设计思路。

## 已知限制

- 内置三个目标（酷狗、PotPlayer、浏览器）；增加其他应用需要编辑 `settings.json` 的 `Targets`。
- 应用使用 WASAPI **独占模式**时，外部无法修改它的音量（暂停仍然有效）。
- 以管理员权限运行的应用，普通权限的 AutoVolume 无法控制。
- 框架依赖版本需要目标机器安装 .NET 10 桌面运行时。

## 许可证

[MIT](LICENSE)

---

<sub>English: A Windows tray utility that automatically ducks (or pauses) your music while you use a microphone or play video, built on WASAPI session volumes. Requires the .NET 10 Desktop Runtime.</sub>

<sub>本项目的代码与文档由 AI 助手（Claude Code）辅助完成，作者负责需求、验证与审核。</sub>