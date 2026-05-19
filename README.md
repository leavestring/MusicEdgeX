<p align="center">
  <img src="Assets/icon.png" width="80" alt="MusicEdgeX" />
</p>

<h1 align="center">MusicEdgeX</h1>

<p align="center">
  <b>🎵 鼠标轻触边缘，音乐面板优雅滑出</b>
  <br/>
  <i>Hover the screen edge — your music panel glides out.</i>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/platform-Windows%2010%2F11-blue" />
  <img src="https://img.shields.io/badge/.NET-10.0-purple" />
  <img src="https://img.shields.io/badge/license-MIT-green" />
</p>

<p align="center">
  <a href="#chinese">中文</a> &nbsp;·&nbsp;
  <a href="#english">English</a>
</p>

---

<details open>
<summary><b>🇨🇳 中文</b></summary>

## 这是什么？

你是否有过这样的烦恼：QQ 音乐 / Spotify 挂在任务栏占地方，最小化到托盘又不方便切歌、收藏？

**MusicEdgeX** 把音乐控制栏"藏"在屏幕右边缘。平时完全隐形，鼠标轻轻靠近屏幕右侧，面板就会丝滑滑出——切歌、暂停、收藏、调音量一气呵成。鼠标离开后自动收回，不占任何屏幕空间。

> 主力适配 **QQ 音乐**，核心操控兼容 **Spotify · 网易云音乐 · Apple Music** 等所有接入 Windows SMTC 的播放器。

| 功能 | QQ 音乐 | Spotify | 网易云 | Apple Music |
|------|:--:|:--:|:--:|:--:|
| 歌名 / 歌手 / 封面 / 进度 | ✅ | ✅ | ✅ | ✅ |
| 播放 / 暂停 / 上首 / 下首 | ✅ | ✅ | ✅ | ✅ |
| 随机播放 / 音量 | ✅ | ✅ | ✅ | ✅ |
| 收藏 ❤️ / 打开播放器 🎧 | ✅ | ❌ | ❌ | ❌ |

## 效果演示

> *（请替换为你的 GIF 截图）*

```
鼠标靠近右边缘 → 48px 预览条滑出 → 悬停 0.3s → 360px 完整面板展开
播放 / 暂停 · 上一首 / 下一首 · 随机播放 · 收藏 · 音量调节
```

## 功能

- **边缘触发** — 鼠标接近屏幕右边缘自动浮现，离开自动隐藏
- **全屏检测** — 全屏看视频 / 打游戏时自动禁用，不打扰
- **托盘图标** — 右键菜单：显示面板 / 隐藏面板 / 设置 / 退出
- **实时同步** — 通过 Windows SMTC 读取当前歌曲、艺术家、专辑封面、播放进度
- **媒体键控制** — 模拟媒体键控制播放器（播放 / 暂停 / 上一首 / 下一首）
- **收藏歌曲** — 模拟 QQ 音乐 Ctrl+Alt+F 快捷键一键收藏
- **面板固定** — 点击 📌 固定面板，方便频繁操作
- **自定义设置** — 悬停延迟、隐藏延迟、开机自启

## 快速开始

**环境要求：** Windows 10 1809+ 或 Windows 11 · [.NET 10.0 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

**下载：** 前往 [Releases](https://github.com/leavestring/MusicEdgeX/releases) 下载 `MusicEdgeX.exe`，双击运行。

**从源码构建：**
```bash
git clone https://github.com/leavestring/MusicEdgeX.git
cd MusicEdgeX
dotnet build -c Release
```

## 常见问题

**Q: 面板不出来？** — 确保鼠标移到屏幕最右侧边缘。全屏应用会自动禁用。也可右键托盘 → "显示面板"。

**Q: 收藏无效？** — 收藏模拟的是 QQ 音乐 `Ctrl+Alt+F` 快捷键，确保 QQ 音乐正在运行。

**Q: 专辑封面不显示？** — 确保播放器正在播放，且 SMTC 能读取封面（QQ 音乐 / Spotify / 网易云均可）。

## 路线图

- [ ] 支持左边缘吸附
- [ ] 桌面歌词显示
- [ ] 自定义热键
- [ ] 频谱可视化
- [ ] 多播放器切换
- [ ] 迷你模式

</details>

<details>
<summary><b>🇺🇸 English</b></summary>

## What is this?

Taskbar too cluttered, but minimizing to tray means you can't skip tracks — every music lover knows this trade-off.

**MusicEdgeX** hides a sleek control panel at your screen's right edge. Hover your mouse near the edge and it glides out smoothly. Skip, pause, favorite, adjust volume — all in one fluid motion. Pull away and it vanishes, leaving your desktop perfectly clean.

> Built for **QQ Music** with full feature parity; core controls work across **Spotify, NetEase Music, Apple Music** and any SMTC-compatible player.

| Feature | QQ Music | Spotify | NetEase | Apple Music |
|---------|:--:|:--:|:--:|:--:|
| Track info / album art / progress | ✅ | ✅ | ✅ | ✅ |
| Play / pause / next / previous | ✅ | ✅ | ✅ | ✅ |
| Shuffle / volume | ✅ | ✅ | ✅ | ✅ |
| Favorite ❤️ / Open player 🎧 | ✅ | ❌ | ❌ | ❌ |

## Demo

> *(Replace with your GIF)*

```
Mouse near right edge → 48px preview slides out → hover 0.3s → full 360px panel
Play / Pause · Previous / Next · Shuffle · Favorite · Volume
```

## Features

- **Edge Trigger** — panel glides out when mouse approaches the right screen edge; auto-hides when you leave
- **Fullscreen Detection** — automatically disabled during games / fullscreen video
- **System Tray** — right-click menu: Show / Hide / Settings / Exit
- **Real-time Sync** — reads track, artist, album art, and progress via Windows SMTC
- **Media Keys** — simulates media keys to control any player
- **Favorite** — toggles QQ Music like via `Ctrl+Alt+F`
- **Pin Panel** — click 📌 to keep the panel open
- **Settings** — customizable hover delay, hide delay, auto-start

## Getting Started

**Requirements:** Windows 10 1809+ or Windows 11 · [.NET 10.0 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

**Download:** Grab `MusicEdgeX.exe` from [Releases](https://github.com/leavestring/MusicEdgeX/releases), double-click to run.

**Build from source:**
```bash
git clone https://github.com/leavestring/MusicEdgeX.git
cd MusicEdgeX
dotnet build -c Release
```

## FAQ

**Q: Panel won't show?** — Move mouse to the far right edge of the screen. Fullscreen apps auto-disable the panel. Or right-click tray → "Show Panel".

**Q: Favorite doesn't work?** — It sends QQ Music's `Ctrl+Alt+F` shortcut. Make sure QQ Music is running with default hotkeys.

**Q: No album art?** — Ensure a track is playing and SMTC can read its thumbnail (works with QQ Music, Spotify, NetEase).

## Roadmap

- [ ] Left-edge support
- [ ] Desktop lyrics overlay
- [ ] Custom hotkeys
- [ ] Audio spectrum visualizer
- [ ] Multi-player switching
- [ ] Mini mode (cover art + progress bar only)

</details>

---

## 技术栈 / Tech Stack

| Layer | Tech |
|-------|------|
| UI | WPF (.NET 10) |
| Window management | Win32 P/Invoke |
| Animation | `CompositionTarget.Rendering` (native refresh rate, 120Hz/144Hz) |
| Track data | Windows SMTC |
| Media control | `keybd_event` API |
| Edge detection | `GetCursorPos` polling |

## 项目结构 / Project Structure

```
MusicEdgeX/
├── App.xaml.cs              # Entry point + tray icon
├── MainWindow.xaml / .cs    # Panel UI + animation engine
├── SettingsWindow.xaml / .cs
├── Services/
│   ├── EdgeDetector.cs       # Mouse edge detection
│   ├── SMTCMusicService.cs   # SMTC track info
│   ├── MediaKeyService.cs    # Media key simulation
│   └── QQMusicHotkeyService.cs
├── Helpers/
│   └── Win32Interop.cs       # P/Invoke declarations
└── Models/
    └── TrackInfo.cs
```

## License

MIT © 2025
