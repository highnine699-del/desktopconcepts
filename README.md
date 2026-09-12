# Quire

**Desktop concepts. Every day.**

A lightweight Windows desktop widget that delivers three AI-generated technical concepts every day, rotating through them every 7 minutes. Lives quietly in the system tray — always-on-top, no taskbar entry, no account required.

> *"Putting desktop concepts on your screen."*

---

## Install (end users)

Download the latest `Quire-Setup.exe` from the [Releases page](https://github.com/highnine699-del/Quire/releases).

- **No admin required** — installs per-user to `%LocalAppData%\Programs\Quire`
- **No .NET required** — self-contained single executable
- **No account, no API key** — cloud mode works immediately out of the box

### First run

On first launch Quire shows a setup screen asking you to choose:

- **Local AI** — free, fully offline, requires a running local AI server (e.g. [LM Studio](https://lmstudio.ai/) or [Ollama](https://ollama.ai/))
- **Cloud AI** — free, no setup, no API key, no account. Works immediately via a shared cloud service. A daily limit applies; the app will notify you if it's reached.

You can change this later from **Settings → AI & Content**.

### SmartScreen warning

Because the installer is not yet code-signed, Windows SmartScreen may show a blue *"Windows protected your PC"* prompt on first install. This is expected for unsigned software.

To proceed: click **"More info"** → **"Run anyway"**.

The app is open source and the installer is built directly from this repository. You can verify the build yourself using the steps in the Development section below.

### Uninstall

Use **Windows Settings → Apps → Quire** or the uninstaller in the Start Menu. Your concepts, settings, and history in `%AppData%\Quire\` are preserved on uninstall. To remove them completely, delete that folder manually after uninstalling.

---

## Using Quire

The widget appears at the top-right of your screen by default. You can drag it anywhere — position is saved.

| State    | How to enter                          | How to leave                        |
|----------|---------------------------------------|-------------------------------------|
| Compact  | Default / click outside / 30s timeout | Click the widget                   |
| Expanded | Click compact view                    | Click outside, 30s timeout, or Pin |
| Pinned   | Click the 📌 button                   | Click 📌 again                      |

**Keyboard shortcuts (when widget is focused):**

| Key            | Action                        |
|----------------|-------------------------------|
| Space / Enter  | Expand from compact           |
| Escape         | Collapse / unpin              |
| → or ↓         | Next concept                  |
| P              | Toggle pin                    |

**Buttons in expanded view:**
- **Read More** — opens a web search for the concept
- **Copy** — copies title + explanation to clipboard
- **Next ▸** — skip to the next concept immediately

**Tray icon:**
- Left-click — show / hide widget
- Right-click — Show/Hide · Settings · Quit

---

## Data & privacy

Quire stores everything locally under `%AppData%\Quire\`:

```
Settings.json     — your configuration
History.md        — append-only log of all generated concepts
last_run.txt      — date of last generation (prevents duplicate daily runs)
buffer.json       — cloud mode: pre-fetched upcoming concepts
Models\           — local AI model files (local mode only)
Logs\             — rolling daily logs, 14-day retention
```

**What is sent over the network (cloud mode only):**

When cloud mode is active, the app sends the following to a shared Cloudflare Worker proxy that forwards requests to Groq's AI API:

- The **topic category** for the day (e.g. "Networking", "AI") — set by you in Settings
- A list of **recent concept titles** to avoid repetition — titles only, no personal data

No account information, no device identifiers, no usage analytics, and no personal data are ever transmitted. The Cloudflare Worker and Groq receive only what is needed to generate a concept.

If you use **local mode**, nothing leaves your machine at all.

---

## Development

### Prerequisites

- Windows 10 / 11 (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- For local AI mode: a running OpenAI-compatible server (e.g. [LM Studio](https://lmstudio.ai/) or [Ollama](https://ollama.ai/))

### Build and run

```powershell
git clone https://github.com/highnine699-del/Quire.git
cd Quire
dotnet build
dotnet run --project src/Quire.UI
```

On first run the app creates `%AppData%\Quire\Settings.json` with defaults. Cloud mode works immediately — no configuration needed.

For local mode, point Settings → AI & Content → Local AI at your server (default: `http://localhost:1234/v1`).

### Run tests

```powershell
dotnet test
```

82 tests. No network or AI server needed — fakes cover all external dependencies.

### Build a release binary

```powershell
dotnet publish src/Quire.UI/Quire.UI.csproj -p:PublishProfile=win-x64-release -c Release
```

Output: `publish/win-x64/Quire.exe` — single self-contained executable, no runtime required.

### Build the installer

Requires [Inno Setup 6](https://jrsoftware.org/isdl.php).

```powershell
"C:\Users\<you>\AppData\Local\Programs\Inno Setup 6\ISCC.exe" installer\Quire.iss
```

Output: `installer/Output/Quire-Setup.exe`

Or use the automated release script which does everything in one step:

```powershell
.\release.ps1
```

This bumps the version, runs tests, publishes, builds the installer, commits, and creates the GitHub release.

---

## Project structure

```
src/
  Quire.Domain/         Pure domain models and interfaces (no external deps)
  Quire.Application/    State machine, schedulers, background services
  Quire.Infrastructure/ AI provider, settings store, history store
  Quire.UI/             WPF widget, tray icon, theme, animations
tests/
  Quire.Tests/          xUnit tests
installer/
  Quire.iss             Inno Setup script
```

---

## Default weekday topic schedule

| Day       | Topic                |
|-----------|----------------------|
| Monday    | Programming          |
| Tuesday   | Cybersecurity        |
| Wednesday | Networking           |
| Thursday  | AI                   |
| Friday    | Operating Systems    |
| Saturday  | Mathematics          |
| Sunday    | Computer Engineering |

Fully configurable in **Settings → AI & Content → Daily topic schedule**.

---

## Cloud mode — how it works

Cloud mode uses a shared Cloudflare Worker proxy that forwards requests to Groq's API using a server-held key. **You never need to provide an API key or create an account.**

- Concepts for the next 7 days are pre-fetched and stored locally in `buffer.json`
- The widget works offline for up to 7 days in cloud mode
- If the daily shared limit is reached, the app shows a friendly notice and resumes the next day automatically
- Power users can override with their own provider (Settings → AI & Content → Advanced)

---

## Links

- [Releases](https://github.com/highnine699-del/Quire/releases)
- [Issues / bug reports](https://github.com/highnine699-del/Quire/issues)
- [Source code](https://github.com/highnine699-del/Quire)
