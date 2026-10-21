# SteamPipe 本地构建准备

本目录只准备 Windows x64 自包含构建和 SteamPipe VDF；脚本不会登录 Steam、上传构建、设置 live 分支或发布商店页面。

## 生成本地构建

在 PowerShell 中从本目录运行：

```powershell
.\prepare-steam.ps1
```

每次运行会生成新的 `staging\win-x64-<UTC 时间>` 目录，不覆盖旧构建。发布目标为 `win-x64`、`.NET 8` 自包含目录构建。可以通过 `STEAM_API64_DLL` 指向从 Valve Steamworks SDK redistributable 取得的 `steam_api64.dll`；脚本会复制到该次 staging 目录。仓库不包含 Valve 的原生运行库，也不自动下载 SDK。

没有 App ID / Depot ID 时，脚本仍能生成本地自包含包，但不会生成可提交给 SteamPipe 的 VDF。取得 Steamworks 后台分配的纯数字 ID 后，在 PowerShell 设置：

```powershell
$env:STEAM_APP_ID = '你的 App ID'
$env:STEAM_DEPOT_ID = '你的 Windows Depot ID'
.\prepare-steam.ps1
```

VDF 会生成在唯一的 `generated\<UTC 时间>` 目录，其中 `app_build.vdf` 的 `SetLive` 留空。上传仍需用户自行配置并显式运行 SteamCMD；本脚本不会触发上传。

## Steamworks 后台配置

在 Steamworks 中将产品类型设置为 **Game**，为 Windows x64 建立 depot，并创建与代码一致的统计项和成就：

- Stats：`POND_FISH_CAUGHT`、`POND_SPECIES_DISCOVERED`（整数）
- Achievements：`POND_FIRST_CATCH`、`POND_CATCH_20`、`POND_ALL_FISH`
- 为应用启用 Steam Cloud，并按 Valve 文档为 `codex-pond-save-v1.json` 配置允许文件及额度。

Steamworks API 与账户 App ID 未配置时，应用仍使用本地鱼册和设置。云档只包括鱼获、外观、装备、解锁与最近模式；不包含提示词、对话、工作目录、运行日志或凭据。跨设备时鱼获按 turn/记录 ID 去重，设置按修改时间取新值。

## 当前发行边界

- Steamworks.NET 通过 NuGet 引用；其 MIT 许可见项目根目录 `THIRD-PARTY-NOTICES.txt`。
- 当前预览构建未包含 Valve SDK 的 `steam_api64.dll`、Steam App ID、Depot ID 或 Steamworks 测试账户。商店审核、上传、真实成就、真实云存档和商店页面资格都未验证。
- Steam Direct/Steam 商店最终接收类型和发布材料以 Valve 后台审核为准；本地配置不代表已获上架批准。

