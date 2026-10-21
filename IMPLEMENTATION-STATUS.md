# Codex Pond Implementation and Verification

Status date: 2026-10-21

## Implemented

- Windows .NET 8 WPF desktop pond with six scene layouts, seasonal/time-of-day presentation, 20 fish sprites, a fish book, accessories, local random events, and standalone fishing mode.
- Generic Agent lifecycle contracts and launch adapters for Codex App Server, DeepSeek Harness headless CLI, and Claude Code with Work Buddy MCP.
- Agent event routing validates the active provider instance, session ID, and turn ID; Codex App Server disconnect events are scoped to their originating process.
- Conservative fish-award rules: a verified successful Agent turn earns one fish; failure, cancellation, disconnection, unknown terminal state, or duplicate turn IDs do not.
- Corrupt settings are moved to a timestamped backup before defaults are used; unreadable settings are protected from writes. The startup notice reports the recovery result.
- Agent and independent catches report Steam stats from the same local fish ledger; cloud merges reconcile the stats after deduplicating records.
- Local fish-book/settings persistence, anonymous activity-pulse option, Steamworks.NET integration scaffolding, and SteamPipe preparation scripts.

## Verification performed

- Release build of the WPF solution completed with 0 warnings and 0 errors in the implementation workspace.
- Smoke suite passed using fake Codex App Server, DSH CLI, Claude CLI, Work Buddy MCP, and Steam interfaces. It covers settings recovery, provider/session/turn event isolation, shared fish stats, and the existing protocol and privacy checks. This is not a real-provider end-to-end test.
- Added `.github/workflows/windows-validation.yml` to run Windows .NET restore, Release build, and smoke validation on pushes and pull requests to `main`, plus manual dispatch. The workflow has not yet run on GitHub.
- DSH approval bridge JavaScript syntax check passed.
- Windows x64 self-contained staging was generated successfully. The executable is unsigned. The Steamworks native redistributable was not present in that staging.

## Not yet verified

- Real authenticated Codex App Server, DeepSeek Harness headless CLI, and Claude Code/Work Buddy tasks and approvals have not completed end-to-end acceptance. Provider authentication and credentials remain user-managed; no credentials are included.
- The real DeepSeek CLI was unavailable in the validation environment. A local Work Buddy MCP health/tool-list check passed, but no real Work Buddy agent workflow was invoked.
- Steam achievements and cloud saves require a Steamworks App ID/Depot configuration and Valve's native redistributable; live Steam behavior was not tested.
- Cross-process click-through, interactive DPI and multi-monitor behavior, tray actions, accessibility, and long-running desktop use need manual acceptance on target Windows systems.
- Codex App Server is an experimental protocol; adapter compatibility should be checked after upstream changes.

## Release boundary

This is an early portable preview, not a Steam-ready release. See the two README files for setup, download, privacy, and provider requirements. The source includes no provider credentials or personal session data.

## Third-party components

Steamworks.NET is distributed under its MIT license. See [`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt). No project-wide license has been selected yet.

## GitHub preview

- Public source repository: https://github.com/a2098127101-droid/codex-pond
- Preview release: https://github.com/a2098127101-droid/codex-pond/releases/tag/v0.1.0-preview.1
- Portable Windows x64 asset: https://github.com/a2098127101-droid/codex-pond/releases/download/v0.1.0-preview.1/CodexPond-win-x64.zip
- Archive size: 72,158,788 bytes. SHA-256: `38e60797be670351572483c31f1c482404e0f7dbb027ecefc1a54754cfbfcba6`.
- GitHub release metadata reports the asset uploaded with the same SHA-256; anonymous HTTP HEAD to the direct asset URL returned 200. The source upload excludes build caches, local audit notes, and machine-specific paths. The portable archive excludes PDB files and `steam_api64.dll`.
