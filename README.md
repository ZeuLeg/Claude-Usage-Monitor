## Claude Usage Monitor - Taskbar Widget + Popup Window 

A Windows tray app that shows your Claude.ai usage at a glance - including a widget floating just above the system tray. 

<img width="696" height="56" alt="image" src="https://github.com/user-attachments/assets/58a821ee-bb92-45d5-866c-9f85470c1f11" />

## How it works

The app reads the OAuth token that **Claude Code** stores in your Windows Credential Manager, then calls the Anthropic OAuth usage API. One HTTP request. No browser, no cookies, no WebView2, no manual configuration.

## Requirements:
You need [Claude Code](https://docs.anthropic.com/en/docs/claude-code) installed and logged in (`claude login`) 
and .NET 10 - zero external dependencies. 

## Setup

Use the `ClaudeUsageMonitor.exe` from the latest release 

Or build it yourself: 

```bash
git clone https://github.com/ZeuLeg/Claude-Usage-Monitor.git
cd Claude-Usage-Monitor
dotnet build -c Release
dotnet run
```

That's it. If you're logged into Claude Code, the tray icon should show your session usage within seconds.

## What you see

- **Taskbar widget** — floating just above the system tray, always on top. Shows two progress bars (`5h` session + `7d` weekly) with percentage, countdown (e.g. `91% - 2h 3m`), and a pace arrow (▲ ahead / ▼ under / • on pace). Bars have tick marks at 25%/50%/75% for reference. Colors shift green → yellow → red by utilization. Adapts to Windows light and dark themes. Updates automatically when the displayed countdown changes. Fades out when hovered (click-through when invisible).
- **Tray icon** — two concentric rings: the outer `5h` session ring shifts green → yellow → red as you approach the limit. The inner `7d` weekly ring is cyan by default (the app's weekly-reference color, matching the popup markers) and escalates to amber/red only when weekly usage gets critical. Dims to the last-known reading when offline.
- **Tooltip** with session %, weekly % (with pace), and reset timers
- **Right-click** menu: Details, Refresh, Copy Status Text, Open Log Folder, Start with Windows, Check for Updates, About, Exit
- **Popup window** — opens on startup and on double-click, always on top. Three progress bars: `5h` session, `7d` weekly, `extra usage` monthly — with colored pace markers and subtitles (e.g. Reset: 1h 23m | +12% ahead)

<img width="402" height="235" alt="Tray Usage Monitor" src="https://github.com/user-attachments/assets/0479ef8d-bcb8-445e-9b56-df71c411852c" />

If the taskbar is unavailable (unsupported shell), the app falls back gracefully to tray icon + popup only.

## Extras

- **Start with Windows** — toggle it in the tray menu (no installer; just a per-user registry Run entry).
- **Update notifications** — checks GitHub Releases about once a day and tells you when a newer version is out.
- **Sleep & offline resilient** — recovers within seconds of the connection returning after standby or a network drop, and keeps showing your last-known usage (dimmed, with an offline dot) while offline instead of an error.

## How it actually works (technically)

1. Reads `"Claude Code-credentials"` from Windows Credential Manager, then falls back to `%USERPROFILE%\.claude\.credentials.json` (and `%HOMEDRIVE%%HOMEPATH%\.claude\` as a second fallback)
2. Extracts the `claudeAiOauth.accessToken` 
3. Calls `GET https://api.anthropic.com/api/oauth/usage` with Bearer auth
4. Parses `five_hour`, `seven_day`, and `extra_usage` from JSON response
5. Updates tray icon every 2 minutes (every 60 seconds while the widget is open)

Inspired by [omachala's bash gist](https://gist.github.com/omachala/5ea5af4bfa0b194a1d48d6f2eedd6274) which does the same thing for macOS/CLI.

## Token expired?

Run `claude login` in your terminal. The app picks up the new token automatically on the next poll cycle.

## Project structure

```
├── Program.cs            # Entry point
├── MainForm.cs           # Tray icon, polling, UI orchestration
├── TaskbarWidget.cs      # Floating topmost overlay above the taskbar (layered window)
├── Win32Interop.cs       # P/Invoke declarations for Win32 APIs
├── UsageFetcher.cs       # Single HTTP call to Anthropic API
├── UsageData.cs          # Data model
└── CredentialReader.cs   # Reads OAuth token from Credential Manager / file
```

## License
MIT License – See [LICENSE](LICENSE) file
