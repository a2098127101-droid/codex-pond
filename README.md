# Codex Pond

[简体中文](README.md) · [English](README.en.md)

**一方像素池塘，把完成的 Agent 任务回合变成鱼册中的收获。** Codex Pond 提供 Windows 桌面池塘和 macOS 独立窗口版本；两端都支持离线独立钓鱼与本机 Agent 任务面板。

[下载 Windows x64 便携预览版](https://github.com/a2098127101-droid/codex-pond/releases/download/v0.1.0-preview.1/`CodexPond-win-x64.zip`) · [SHA-256 校验值](https://github.com/a2098127101-droid/codex-pond/releases/download/v0.1.0-preview.1/SHA256SUMS.txt) · [源码 ZIP](https://github.com/a2098127101-droid/codex-pond/archive/refs/heads/main.zip) · [全部发行版本](https://github.com/a2098127101-droid/codex-pond/releases)

![六款原创池塘主题的静态预览](docs/images/theme-preview.png)

> 上图是静态设计预览，不是应用运行过程的录屏，也不能证明桌面交互验收已完成。

## 功能简介

- 在 Windows 桌面显示一方紧凑、可拖动的像素池塘，包含六种原创场景布局、鱼册、季节配色、本地天气和偶发环境事件。
- macOS 提供独立池塘窗口，可使用鱼册、六种外观、装备与相同的独立钓鱼循环；暂不提供 Windows 的透明桌面穿透覆盖层。
- 提供无需 Agent 即可游玩的独立钓鱼模式：咬钩后用按键或点击控制鱼线张力，并可选择宽松辅助。Agent 模式依旧严格按每个成功 turn 收一条鱼，不受小游戏结果影响。
- 鱼册列出全部 20 种鱼，未发现鱼显示剪影、季节/时段线索并支持筛选；季节事件会再次出现，没有连续签到或错过惩罚。
- 首次钓鱼、发现四季鱼和发现全部鱼种会推进本地装饰解锁；Windows Steamworks 统计支持相应里程碑，Mac 版目前只保存本地进度。
- 支持连接 Codex App Server、DeepSeek Harness headless CLI，或通过已配置的 Work Buddy MCP 网关连接 Claude Code。只有确认成功完成的 Agent 回合才会钓起一条鱼；失败、取消、断线或结果不明确时不会记鱼。
- 池塘场景和鱼类美术均为原创。季节氛围随本地时间和月份变化；应用不会获取联网天气数据。
- Agent 回合进行期间，可选择统计匿名键鼠活动脉冲，以影响鱼类稀有度。应用不会保存输入内容、鼠标坐标、目标应用或逐次输入历史；此功能可以关闭。
- 鱼册和设置保存在本地。用户配置 Steam 云存档后，云端数据范围仅限游戏进度和设置；不会上传 Agent 提示词、对话、工作目录、日志或凭据。

## 下载与运行

1. 下载上方链接中的 `CodexPond-win-x64.zip`。
2. 解压后运行 `CodexPond.exe`。
3. 使用 Agent 模式时，请另行安装并登录对应的提供方。Codex Pond 不捆绑 Agent CLI 或凭据。

预览包是 Windows x64 自包含构建。目前的可执行文件尚未签名，Windows 可能显示发布者或 SmartScreen 警告。运行前请检查源码并核对已发布的 SHA-256 校验值。

### 源码级“赛博大佛”印记

运行 `CodexPond.exe --cyber-buddha-stamp` 可查看源码印记编号、版本和当前可执行文件的 SHA-256 指纹。仅将指纹与对应同版本的[官方 GitHub Release 校验清单](https://github.com/a2098127101-droid/codex-pond/releases)对照；本地源码构建与发行版哈希通常不同。当前公开预览包尚未包含此参数；该功能从包含本次改动的构建开始生效。印记不显示在池塘场景中；本地哈希本身不是数字签名，也不能阻止二进制文件被修改。

佛像本体是一段直接写在共享源文件 `CyberBuddhaSeal.cs` 的 ASCII 字符画；Windows 可运行上面的命令查看，Mac 客户端提供“源码印记”按钮。它是程序员彩蛋，不是无 Bug 或安全保证。

## macOS 版本

Mac 客户端位于 `macos/CodexPond.Mac`，使用 Avalonia 桌面窗口与共享 .NET 玩法/Agent 组件。目标为 macOS 14+；使用 .NET 8 SDK 生成 `osx-arm64` 或 `osx-x64` 自包含应用包：

~~~bash
bash macos/build-macos.sh osx-arm64
# 或 Intel Mac：
bash macos/build-macos.sh osx-x64
~~~

脚本在 `artifacts/macos/` 下生成 `.app`。macOS GitHub Actions 会对 ARM64 与 Intel x64 分别生成 30 天可下载的未签名 zip；打开对应 Actions 构建记录，在 Artifacts 区下载。开发包未签名或公证，macOS 可能阻止直接打开；公开分发前需要 Apple Developer ID 签名与 notarization。代码构建不等于 Mac 硬件验收：Agent CLI 登录、审批 MCP、显示缩放、签名和公证仍需在目标 Mac 上实际验证。Steamworks、Windows 透明桌面穿透层和托盘行为仍为 Windows 端功能。

## Agent 与 Steam 状态

仓库包含 Codex、DeepSeek Harness 和 Work Buddy 连接器实现。自动化冒烟验证使用模拟 CLI、App Server 和 MCP 事件流。真实提供方的端到端验收仍待完成，需要用户自行配置 CLI、登录、MCP 和审批；仓库不包含提供方凭据。

仓库包含 Steamworks.NET 集成代码和 SteamPipe 准备脚本。Steam 成就和云存档尚未经过真实 Steam 环境验证。仍需提供 Steamworks 可再发行文件（`steam_api64.dll`）并配置 Steam App/Depot；此预览版不是 Steam 商店发行版。

透明区域鼠标穿透、多显示器 DPI 行为以及桌面实机交互尚未完成全面验收。

## 从源码构建

Windows 构建要求：Windows 10/11 x64 和 .NET 8 SDK。Mac 构建要求：macOS 14+ 和 .NET 8 SDK。

~~~powershell
dotnet restore .\CodexPond.sln -p:RestoreSources=https://api.nuget.org/v3/index.json -p:NuGetAudit=false
dotnet build .\CodexPond.sln -c Release
~~~

冒烟验证项目位于 [tests/CodexPond.Smoke](tests/CodexPond.Smoke)。模拟提供方测试不能证明真实提供方的登录流程或运行时兼容性。

GitHub Actions 会在推送到 `main`、针对 `main` 的拉取请求，以及手动触发时，在 Windows runner 上执行依赖还原、Release 构建和冒烟验证。

独立的 macOS runner workflow 会为 ARM64 和 Intel x64 发布自包含 `.app` 下载产物；它验证对应目标的打包过程，不替代 Mac 上的登录、窗口和权限流程验收。

## 隐私与权限

- 各提供方 CLI 使用其自身的本地登录、沙箱和审批设置。
- Codex Pond 不读取凭据文件，也不会自动批准提供方请求。
- 活动统计只在 Agent 回合进行时临时累计匿名脉冲，不会持久化计数。
- Windows 鱼册和设置保存在 `%LOCALAPPDATA%\CodexPond`；macOS 保存在 `~/Library/Application Support/CodexPond`。

## 项目许可证

Codex Pond 项目尚未选定许可证。第三方声明见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) 和 [THIRD-PARTY-NOTICES-DOTNET.txt](THIRD-PARTY-NOTICES-DOTNET.txt)，请分别查阅。

## 灵感与版权说明

本项目的轻松季节氛围受到像素风生活模拟游戏启发，“工作转化为可见收获”的反馈逻辑参考了[《喵了个菜！》](https://store.steampowered.com/app/4201710?l=schinese)。Codex Pond 的场景、界面元素和鱼类精灵均为原创，不包含其他游戏的美术或界面素材。
