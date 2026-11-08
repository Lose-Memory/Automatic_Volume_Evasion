# AutoVolume 音量自动闪避

> Windows 上的按应用音量闪避工具：用麦克风时自动压低音乐，看视频时自动让音乐让路。

AutoVolume 常驻托盘，监听两件事——**谁在占用麦克风**、**谁在播放视频**——然后按你设定的比例降低目标应用的音量，或者直接暂停它。

## 它解决什么问题

- 用豆包输入法、微信语音、会议软件说话时，酷狗还在放歌，听不清自己说什么。
- 在浏览器或 PotPlayer 看视频时，背景音乐干扰台词。
- 用完语音输入后，又要手动把音乐调回来。

## 功能

- **麦克风占用触发**：任何非系统进程占用麦克风（语音输入、通话、录音）时触发，与说话音量无关，不会被音箱串音误触发。
- **视频触发**：浏览器 / PotPlayer 有声音输出时触发。
- **每个目标独立配置**：两条触发路径可以分别设为「不处理 / 压低音量 / 暂停播放」，压低比例单独可调。
- **实时反馈**：界面里每个应用名下面有一条底色进度条，显示当前音量或电平。
- **自动恢复**：触发结束、关闭程序、甚至程序被强杀后重新启动，都会把音量还原。
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
  "Targets": [ /* 目标列表：进程名、触发动作、压低比例 */ ]
}
```

`Targets` 里的每一项对应界面中的一行，可以手动增删进程名来支持更多应用。

> 麦克风走的是「占用检测」，`Mic.ThresholdDb` 目前不参与判断，字段保留仅为兼容旧配置。

## 已知限制

- 内置三个目标（酷狗、PotPlayer、浏览器）；增加其他应用需要编辑 `settings.json` 的 `Targets`。
- 应用使用 WASAPI **独占模式**时，外部无法修改它的音量（暂停仍然有效）。
- 以管理员权限运行的应用，普通权限的 AutoVolume 无法控制。
- 框架依赖版本需要目标机器安装 .NET 10 桌面运行时。

## 从源码构建

```powershell
cd AutoVolume
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o publish
```

产物是单个 `publish\AutoVolume.exe`（约 26 MB，其中 25 MB 是 WinRT 投影库）。

依赖：

- [NAudio](https://github.com/naudio/NAudio) 2.2.1 — WASAPI 会话与电平
- `Microsoft.Windows.SDK.NET.Ref` — GSMTC 媒体会话控制

## 许可证

[MIT](LICENSE)

---

<sub>English: A tray utility for Windows that ducks (or pauses) your music automatically while you use a microphone or play video, built on WASAPI session volumes. Requires the .NET 10 Desktop Runtime.</sub>