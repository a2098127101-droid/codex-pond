# Codex Pond Implementation and Verification

Status date: 2026-10-21

## Implemented

- Windows .NET 8 WPF desktop pond with six scene layouts, seasonal/time-of-day presentation, 20 fish sprites, a fish book, accessories, local random events, and standalone fishing mode.
- Generic Agent lifecycle contracts and launch adapters for Codex App Server, DeepSeek Harness headless CLI, and Claude Code with Work Buddy MCP.
- Conservative fish-award rules: a verified successful Agent turn earns one fish; failure, cancellation, disconnection, unknown terminal state, or duplicate turn IDs do not.
- Local fish-book/settings persistence, corruption protection, anonymous activity-pulse option, Steamworks.NET integration scaffolding, and SteamPipe preparation scripts.

## Verification performed

- Release build of the WPF solution completed with 0 warnings and 0 errors in the implementation workspace.
- Smoke suite passed using fake Codex App Server, DSH CLI, Claude CLI, Work Buddy MCP, and Steam interfaces. This checks protocol/state handling and privacy boundaries; it is not a real-provider end-to-end test.
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
