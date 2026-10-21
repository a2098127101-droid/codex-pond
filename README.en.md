# Codex Pond

[简体中文](README.md) · [English](README.en.md)

**A small pixel-art pond that turns completed agent turns into fish for your collection.** Codex Pond is a Windows desktop companion with an independent fishing mode and optional local Agent connectors.

[Download the Windows x64 portable preview](https://github.com/a2098127101-droid/codex-pond/releases/download/v0.1.0-preview.1/CodexPond-win-x64.zip) · [SHA-256 checksum](https://github.com/a2098127101-droid/codex-pond/releases/download/v0.1.0-preview.1/SHA256SUMS.txt) · [Source ZIP](https://github.com/a2098127101-droid/codex-pond/archive/refs/heads/main.zip) · [All releases](https://github.com/a2098127101-droid/codex-pond/releases)

![Static preview of six original pond themes](docs/images/theme-preview.png)

> This is a static design preview, not a recording of runtime interaction or a desktop acceptance test.

## What it does

- Keeps a compact, draggable pixel pond on the Windows desktop, with six original scene layouts, a fish book, seasonal palettes, local weather, and occasional events.
- Includes a standalone fishing mode that works without an Agent.
- Implements connectors for Codex App Server, DeepSeek Harness headless CLI, and Claude Code with a configured Work Buddy MCP gateway. One confirmed successful Agent turn earns one fish. Failed, cancelled, disconnected, or uncertain turns do not.
- Uses original fish and scene art. Seasonal ambience follows local time and month; the app does not fetch weather data.
- Can count anonymous keyboard/mouse activity pulses during an active Agent turn to affect fish rarity. Input contents, coordinates, target applications, and per-input history are not saved. The option can be disabled.
- Stores the fish book and settings locally. When configured, Steam cloud data is limited to game progress and settings; Agent prompts, conversations, working directories, logs, and credentials are excluded.

## Download and run

1. Download `CodexPond-win-x64.zip` from the link above.
2. Extract it and run `CodexPond.exe`.
3. For Agent mode, install and sign in to the selected provider separately. Codex Pond does not bundle provider CLIs or credentials.

The preview package is a self-contained Windows x64 build. The executable is currently unsigned, so Windows may display a publisher or SmartScreen warning. Review the source and the published SHA-256 checksum before running it.

### Source-level “Cyber Buddha” mark

Run `CodexPond.exe --cyber-buddha-stamp` to view the source mark ID, version, and SHA-256 fingerprint of the current executable. Compare the fingerprint only with the checksum manifest for the matching version in the [official GitHub Releases](https://github.com/a2098127101-droid/codex-pond/releases); locally built binaries normally have different hashes from release binaries. The currently published preview package does not include this option; it takes effect with a build that contains this change. The mark is not drawn in the pond. A locally displayed hash is not a digital signature and cannot prevent a binary from being modified.

## Provider and Steam status

The Codex, DeepSeek Harness, and Work Buddy connector implementations are included. Automated smoke coverage uses fake CLI/App Server/MCP event streams. Real provider end-to-end acceptance is still pending and requires the user's own CLI, sign-in, MCP setup, and approvals. No provider credentials are included.

Steamworks.NET integration code and SteamPipe preparation scripts are included. Live Steam achievements and cloud saves have not been verified. A Steamworks redistributable (`steam_api64.dll`) and Steamworks App/Depot configuration are still required; this preview is not a Steam release.

Transparent-region click-through, multi-monitor DPI behavior, and hands-on desktop interaction have not been fully accepted on target hardware.

## Build from source

Requirements: Windows 10/11 x64 and the .NET 8 SDK.

```powershell
dotnet restore .\CodexPond.sln -p:RestoreSources=https://api.nuget.org/v3/index.json -p:NuGetAudit=false
dotnet build .\CodexPond.sln -c Release
```

The smoke project is in `tests/CodexPond.Smoke`. Fake provider tests do not prove real provider authentication or runtime compatibility.

GitHub Actions runs dependency restore, a Release build, and the smoke suite on pushes to `main`, pull requests targeting `main`, and manual dispatches, using a Windows runner.

## Privacy and permissions

- Provider CLIs use their own local sign-in, sandbox, and approval settings.
- Codex Pond does not read credential files or auto-approve provider requests.
- The activity option counts temporary anonymous pulses only while an Agent turn is active; it does not persist the count.
- Local fish-book and settings files are stored under `%LOCALAPPDATA%\CodexPond`.

## Project license

No license has been selected for the Codex Pond project yet. Third-party notices are in [`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt) and [`THIRD-PARTY-NOTICES-DOTNET.txt`](THIRD-PARTY-NOTICES-DOTNET.txt). Please review them separately.

## Inspiration and attribution

The calm seasonal mood takes inspiration from pixel-art life-simulation games, and the work-to-visible-reward loop takes inspiration from [喵了个菜！](https://store.steampowered.com/app/4201710?l=schinese). All Codex Pond scenes, interface elements, and fish sprites are original; no game art or interface assets are included.

