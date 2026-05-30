---
name: project-structure
description: MainForm refactor status — which classes have been extracted and where they live
metadata:
  type: project
---

MainForm god-class split is complete as of 2026-05-30. Flat namespace `ClaudeUsageMonitor;` throughout — folders do not affect namespaces.

Extracted classes (all `internal sealed`):
- `src/App/AutostartManager.cs` — registry autostart toggle
- `src/App/Palette.cs` — color constants
- `src/App/TrayIconRenderer.cs` — tray icon rendering + HICON lifecycle
- `src/Ui/DetailsWindow.cs` — floating details popup (`Show`, `Update`, `Render`, `AddBar`) + `ShowAbout()` static
- `src/Polling/UsagePoller.cs` — poll loop, exponential backoff, power/network recovery, events: `Updated`, `TokenMissing`, `AuthExpired`, `Failed`

`src/App/MainForm.cs` (~230 lines) is now a thin coordinator: subscribes poller events, owns tray icon, taskbar widget, update checker, WndProc.

Tests: `tests/ClaudeUsageMonitor.Tests/UsageDataTests.cs` — `PollPolicyTests` references `UsagePoller.NextBackoff` and `UsagePoller.ShouldShowStaleIcon` (moved from MainForm).

Build: `dotnet build ClaudeUsageMonitor.csproj -c Release` (csproj at repo root, not in src/).
Tests: `dotnet test tests/ClaudeUsageMonitor.Tests/ClaudeUsageMonitor.Tests.csproj`

**Why:** constructor order matters — `_poller` must be assigned before `BuildMenu()` is called (lambdas capture it), so `_poller = new UsagePoller(this)` comes first in the ctor.
**How to apply:** any further MainForm edits — keep `_poller` as the first assignment in the constructor body.
