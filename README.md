<p align="center">
  <img src="Assets/icon.png" width="80" alt="MusicEdgeX" />
</p>

<h1 align="center">MusicEdgeX</h1>

<p align="center">
  <b>🎵 隐藏在屏幕边缘的音乐控制面板</b>
  <br/>
  <i>鼠标靠近 → 面板浮现 · 鼠标离开 → 自动隐藏</i>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/platform-Windows%2010%2F11-blue" />
  <img src="https://img.shields.io/badge/.NET-10.0-purple" />
  <img src="https://img.shields.io/badge/license-MIT-green" />
</p>

---

## 这是什么？

你是否有过这样的烦恼：QQ 音乐 / Spotify 挂在任务栏占地方，最小化到托盘又不方便切歌、收藏？

**MusicEdgeX** 把音乐控制栏"藏"在屏幕右边缘。平时完全隐形，鼠标轻轻靠近屏幕右侧，面板就会丝滑滑出——切歌、暂停、收藏、调音量一气呵成。鼠标离开后自动收回，不占任何屏幕空间。

> 支持 **QQ 音乐 · Spotify · 网易云音乐 · Apple Music** 等所有接入 Windows SMTC 的播放器。

---

## 效果演示

> *（请替换为你的 GIF 截图）*

```
鼠标靠近右边缘 → 48px 预览条滑出 → 悬停 0.3s → 360px 完整面板展开

播放 / 暂停 · 上一首 / 下一首 · 随机播放 · 收藏 ❤️ · 音量调节
```

---

## 功能

- **边缘触发** — 鼠标接近屏幕右边缘自动浮现，离开自动隐藏
- **全屏检测** — 全屏看视频 / 打游戏时自动禁用，不打扰
- **托盘图标** — 右键菜单：显示面板 / 隐藏面板 / 设置 / 退出
- **实时同步** — 通过 Windows SMTC 读取当前歌曲、艺术家、专辑封面、播放进度
- **全局快捷键** — 模拟媒体键控制播放器（播放 / 暂停 / 上一首 / 下一首）
- **收藏歌曲** — 模拟 QQ 音乐 Ctrl+Alt+F 快捷键一键收藏
- **面板固定** — 点击 📌 固定面板，方便频繁操作
- **自定义设置** — 悬停延迟、隐藏延迟、开机自启

---

## 快速开始

### 环境要求

- Windows 10 1809+ 或 Windows 11
- [.NET 10.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)（或更高版本）

### 下载

> 前往 [Releases](https://github.com/leavestring/MusicEdgeX/releases) 下载最新 `MusicEdgeX.exe`，双击运行即可。

### 从源码构建

```bash
git clone https://github.com/leavestring/MusicEdgeX.git
cd MusicEdgeX
dotnet build -c Release
# 输出在 bin/Release/net10.0-windows10.0.19041.0/MusicEdgeX.exe
```

---

## 技术栈

| 层 | 技术 |
|---|------|
| UI 框架 | WPF (.NET 10) |
| 窗口管理 | Win32 P/Invoke (`SetWindowLong`, `SetWindowPos`) |
| 动画引擎 | `CompositionTarget.Rendering`（原生刷新率，120Hz/144Hz） |
| 音乐数据 | Windows SystemMediaTransportControls (SMTC) |
| 全局控制 | `keybd_event` 媒体键模拟 |
| 边缘检测 | DispatcherTimer 轮询 + `GetCursorPos` |

---

## 项目结构

```
MusicEdgeX/
├── App.xaml.cs          # 入口 + 托盘图标 (Shell_NotifyIcon)
├── MainWindow.xaml      # 主面板 UI（矢量图标 + 渐变主题）
├── MainWindow.xaml.cs   # 面板逻辑 + 动画引擎 + SMTC 同步
├── SettingsWindow.*     # 设置窗口
├── Services/
│   ├── EdgeDetector.cs       # 鼠标边缘检测
│   ├── SMTCMusicService.cs   # SMTC 歌曲信息读取
│   ├── MediaKeyService.cs    # 媒体键模拟
│   └── QQMusicHotkeyService.cs # QQ 音乐专属快捷键
├── Helpers/
│   └── Win32Interop.cs  # Windows API P/Invoke 声明
└── Models/
    └── TrackInfo.cs     # 歌曲信息模型
```

---

## 常见问题

**Q: 面板不出来？**  
A: 确保鼠标移到屏幕**最右侧边缘**。如果在全屏应用中（游戏 / 视频），面板会自动禁用。也可以右键托盘图标 → "显示面板"。

**Q: 专辑封面不显示？**  
A: 确保播放器正在播放歌曲，且 Windows SMTC 能读取到封面数据（QQ 音乐 / Spotify / 网易云均可）。

**Q: 收藏按钮无效？**  
A: 收藏功能模拟的是 QQ 音乐的 `Ctrl+Alt+F` 快捷键。确保 QQ 音乐正在运行且快捷键未被修改。

---

## 路线图

- [ ] 支持左边缘吸附
- [ ] 桌面歌词显示
- [ ] 自定义热键
- [ ] 频谱可视化
- [ ] 多播放器切换
- [ ] 迷你模式（仅显示封面 + 进度条）

---

## License

MIT © 2025
