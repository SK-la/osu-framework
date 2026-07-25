<p align="center">
  <img width="500px" src="assets/o!f Logo Large FC.svg">
</p>

# osu!framework（Ez2Lazer）

中文 | English

[![Build status](https://github.com/SK-la/osu-framework/actions/workflows/ci.yml/badge.svg?branch=master&event=push)](https://github.com/SK-la/osu-framework/actions/workflows/ci.yml)
[![GitHub release](https://img.shields.io/github/release/SK-la/osu-framework.svg)](https://github.com/SK-la/osu-framework/releases/latest)

本仓库是 [ppy/osu-framework](https://github.com/ppy/osu-framework) 的 **Ez2Lazer 专用 fork**，为 [Ez2Lazer](https://github.com/SK-la/Ez2Lazer) 提供底层能力（低延迟音频、毛玻璃 UI、透视着色、多设备输入等）。  
日常开发与发布走 [SK-la/osu-framework](https://github.com/SK-la/osu-framework)；`upstream`（ppy）仅作只读同步源。

This repository is the **Ez2Lazer fork** of [ppy/osu-framework](https://github.com/ppy/osu-framework), powering [Ez2Lazer](https://github.com/SK-la/Ez2Lazer) with low-latency audio, acrylic UI, perspective shaders, multi-device input, and more. Development targets [SK-la/osu-framework](https://github.com/SK-la/osu-framework); `upstream` (ppy) is read-only.

NuGet 包名：`ez2lazer.Framework`（由 Ez2Lazer 主仓的 `Ez2Lazer.Dependencies.props` 引用）。

---

## 相对上游的改动 / What we add on top of upstream

### 音频 / Audio

- **ASIO 输出**：`EzAsioDeviceManager` 管理设备初始化、采样率/缓冲区、切换与释放；打包包含 `bass.asio`；支持外部 PCM / 直通（`AsioUseExternalPCM`）等配置。
- **WASAPI 独占**：在共享模式之外提供独占路径，便于低延迟出声。
- **输出模式枚举**：`AudioOutputMode`（Shared / Exclusive / ASIO），与游戏侧音频设置对接。
- **EzLatency**：输入–播放延迟采样与统计框架（`osu.Framework/Audio/EzLatency/`），供 Ez 侧延迟分析使用。
- **稳定性**：Mixer 未就绪防护、通道初始化顺序、设备列表与重配置生命周期等修复。

### 图形 / Graphics

- **局部毛玻璃** `BackdropBlurDrawable`：复用捕获源当帧 draw node，对指定子树做高斯虚化（如 Mania 双 stage）。
- **全局亚克力** `AcrylicBackdropDrawable`：内容无关地虚化「自身矩形下方已渲染内容」；D3D11 下支持 backbuffer region snapshot，避免闪烁/管线状态破坏。
- **透视着色**：`BufferedContainer` 真透视 shader（`sh_Perspective.fs`），用于轨道等 2.5D 透视效果。
- **无边框不锁帧**：Fullscreen / Borderless 允许 tearing，Unlimited 帧同步不再被合成器顶死。

### 输入 / Input

- **按设备摇杆轴** `JoystickDeviceAxis`：多手柄 / 多转盘按 InstanceId + GUID 隔离轴值（SDL2/SDL3），避免多设备互相覆盖——BMS 转盘等场景需要。

### 其它 / Misc

- 字体双字符（代理对）解析，基本 emoji 显示。
- SDL3 退出时 native 清理超时，减轻关进程后后台僵持。
- WaveformGraph、故事板/采样 LRU 与内存相关修复等维护性改动。
- CI / NuGet 可信发布面向 SK-la fork 调整。

上游常规能力（UI、输入、VisualTests、跨平台宿主等）仍保留；完整上游说明见 [ppy/osu-framework](https://github.com/ppy/osu-framework)。

---

## 与主仓的关系 / Relation to Ez2Lazer

```
SK-la/osu-framework  →  NuGet: ez2lazer.Framework
SK-la/osu-resources  →  NuGet: ez2lazer.Game.Resources
SK-la/Ez2Lazer       →  游戏本体（默认引用上述 NuGet；可切本地工程引用）
```

本地联调：在 Ez2Lazer 的 `Ez2Lazer.Dependencies.props` 中将 `UseEz2LazerNuGetPackages` 设为 `false`，并保持本仓库与主仓同级目录。

文档与功能总览：[Ez2Lazer Wiki](https://github.com/SK-la/Ez2Lazer/wiki)

---

## 构建 / Building

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download)
- Linux 需系统级 ffmpeg（视频解码）
- 推荐用 Visual Studio / Rider / VS Code，加载平台对应的 `.slnf`，组件开发优先跑 `VisualTests`

```bash
git clone https://github.com/SK-la/osu-framework
# 与 Ez2Lazer、osu-resources 同级克隆后按 Dependencies.props 切换引用
```

代码分析：`powershell ./InspectCode.ps1` 或 `./InspectCode.sh`

---

## 同步上游 / Syncing upstream

定期 `git fetch upstream` 后在本地 merge/rebase；**不要**向 `ppy/*` 推送或开 PR。发布与 PR 一律针对 `SK-la/osu-framework`。

---

## Licence

基于 [MIT](https://opensource.org/licenses/MIT)，见 [LICENCE](LICENCE)。BASS / BASS ASIO 为商业音频库：非商业免费；商业分发请自行取得 [un4seen 许可](http://www.un4seen.com/bass.html#license)。

上游版权归 [ppy Pty Ltd](https://github.com/ppy)；本 fork 的 Ez 专用改动同样以 MIT 发布。
