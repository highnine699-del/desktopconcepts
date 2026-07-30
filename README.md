# DesktopConcepts

A lightweight Windows desktop widget that delivers three AI-generated technical concepts every day, rotating through them every 7 minutes. Runs quietly in the system tray — always-on-top, no taskbar entry.

---

## Quick start (development)

### Prerequisites
- Windows 10 / 11 (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- A running local AI server compatible with the OpenAI `/v1/chat/completions` API  
  (e.g. [LM Studio](https://lmstudio.ai/) or [Ollama](https://ollama.ai/) in OpenAI-compat mode)

### 1. Clone and build
```powershell
git clone <your-repo-url>
cd kevwe
dotnet build
```

### 2. Configure
On first run the app creates `%AppData%\DesktopConcepts\Settings.json` with defaults.  
Edit it to point at your local AI server:

```json
{
  "Mode": "local",
  "Theme": "dark",
  "Provider": {
    "BaseUrl": "http://localhost:1234/v1",
    "Model": "phi-3-mini",
    "ApiKey": null
  },
  "CloudProvider": {
    "BaseUrl": "https://api.anthropic.com/v1",
    "Model": "claude-haiku-4-5",
    "ApiKey": "YOUR_ANTHROPIC_KEY"
  }
}
```

`Mode` can be `"local"` (free, default) or `"cloud"` (requires API key).

### 3. Run
```powershell
dotnet run --project src/DesktopConcepts.UI
```

The widget appears at the bottom-right of your screen.  
A tray icon appears in the system notification area — right-click it for options.

---

## Building a release binary

```powershell
dotnet publish src/DesktopConcepts.UI/DesktopConcepts.UI.csproj `
  -p:PublishProfile=win-x64-release `
  -c Release
```

Output: `publish/win-x64/DesktopConcepts.exe`  
Single self-contained executable — no .NET runtime required on the target machine.

---

## Running tests

```powershell
dotnet test
```

All tests are in `tests/DesktopConcepts.Tests/`. No network or AI server needed — fakes cover everything.

---

## Widget behaviour

| State    | How to reach                         | How to leave                         |
|----------|--------------------------------------|--------------------------------------|
| Compact  | Default / outside-click / 30s timeout | Click the widget                    |
| Expanded | Click Compact                         | Outside-click, 30s timeout, or Pin  |
| Pinned   | Click the 📌 pin button               | Click 📌 again                       |

- **Next ▸** — skip to the next concept immediately (doesn't wait for the 7-min timer)
- **Copy** — copies title + explanation to clipboard
- **Read More** — opens a web search for the concept

---

## Project structure

```
src/
  DesktopConcepts.Domain/         Pure domain models and interfaces (no external deps)
  DesktopConcepts.Application/    State machine, schedulers, background services
  DesktopConcepts.Infrastructure/ AI provider, settings store, history store, download
  DesktopConcepts.UI/             WPF widget, tray icon, theme, animations
tests/
  DesktopConcepts.Tests/          xUnit tests — Domain, Application, Infrastructure
```

---

## Data stored on disk

Everything lives under `%AppData%\DesktopConcepts\` — nothing under `Program Files`.

```
%AppData%\DesktopConcepts\
  Settings.json      — user configuration
  History.md         — append-only log of all generated concepts (3 per day)
  last_run.txt       — date of last successful generation (prevents re-running on same day)
  Models\            — downloaded local model files (if applicable)
  Logs\              — rolling daily log files (14-day retention)
```

Uninstalling is manual for v1: delete `%AppData%\DesktopConcepts\` and the exe.

---

## Weekday topic schedule

| Day       | Category           |
|-----------|--------------------|
| Monday    | Programming        |
| Tuesday   | Cybersecurity      |
| Wednesday | Networking         |
| Thursday  | AI                 |
| Friday    | Operating Systems  |
| Saturday  | Mathematics        |
| Sunday    | Computer Engineering |

Configurable in `Settings.json` → `Topics`.

---

## Cloud mode (optional)

Set `"Mode": "cloud"` and provide an `ApiKey` under `CloudProvider`.  
Default cloud model: **Claude Haiku 4.5** (Anthropic) — cheap enough at this app's usage volume that cost is not a real constraint.

---

## v2 roadmap items (explicitly out of v1)

- Pin behind desktop icons (WorkerW layer) — deferred; relies on undocumented Windows internals
- Multiple widgets, favorites, search history, Markdown export
- Voice narration, daily notifications, multi-language support
- Cloud sync, plugin system, community prompt packs
