# Design Document — Desktop Concepts Widget

## Overview

This document maps every requirement from `requirements.md` to its concrete
implementation across the four-project solution (`Quire.Domain`, `Quire.Application`,
`Quire.Infrastructure`, `Quire.UI`). It describes what is already built, what diverges
from the initial brief (and why), and the remaining gaps that need to be closed.

The implementation follows a strict layered architecture:
- **Domain** — interfaces, records, enums. Zero external dependencies.
- **Application** — business logic, schedulers, state machine. References Domain only.
- **Infrastructure** — file-system stores, HTTP providers. References Domain only.
- **UI** — WPF composition root, code-behind, XAML. References Application + Infrastructure for DI only.

---

## Requirement 1 — Daily Concept Generation

**Implementation:** `DailyConceptScheduler` (Application) + `ConceptGenerationBackgroundService` (Application).

`ConceptGenerationBackgroundService.ExecuteAsync` runs the daily loop. It reads `last_run.txt`
via `ShouldSkipToday()` to determine whether generation is due, avoiding naive 24-hour timer
drift across laptop sleep cycles. When due it delegates to:

- **Local mode:** `DailyConceptScheduler.RunIfDueAsync` — calls `IConceptProvider.GenerateConceptAsync`
  three times (growing an intra-day avoid-list between calls), wraps the three `Concept` records
  in a `DailyConceptSet`, calls `IConceptHistoryStore.AppendSetAsync`, fires `ConceptSetGenerated`.
- **Cloud mode:** `CloudPrefetchService.TryConsumeAsync` — pops the next pre-fetched
  `DailyConceptSet` from `buffer.json`, appends to History.md via `AppendIfNotAlreadyPersistedAsync`
  (guards duplicate writes on ForceRetry), fires `ConceptSetReady`.

When already ran today, `ExecuteAsync` loads the most-recent set from
`IConceptHistoryStore.GetMostRecentSetAsync` and fires `ConceptSetReady` directly without
touching the AI provider or the buffer.

`DailyConceptScheduler` seeds the avoid-list from `IConceptHistoryStore.GetRecentTitlesAsync(30)`
and grows it with each freshly generated title so the three intra-day concepts never repeat
each other or the last 30 history entries.

Exception handling: any exception from `GenerateConceptAsync` is caught by `DailyConceptScheduler`,
which fires `GenerationFailed` instead of re-throwing. `QuotaExceededException` (HTTP 429) is
intercepted before `GenerationFailed` and routed to the separate `QuotaExceeded` event so the UI
shows a distinct friendly message rather than the generic error view.

`ForceRetryAsync` provides a bypass of `ShouldSkipToday` for the ErrorRetry UI path.

**Files:**
- `src/Quire.Application/Schedulers/DailyConceptScheduler.cs`
- `src/Quire.Application/Schedulers/ConceptGenerationBackgroundService.cs`

---

## Requirement 2 — Weekday Category Rotation

**Implementation:** `WeekdayTopicMap` (Domain) + `JsonSettingsStore` (Infrastructure).

`WeekdayTopicMap` is a `sealed record` wrapping `IReadOnlyDictionary<DayOfWeek, string>`.
`CategoryFor(DayOfWeek)` looks up the day and throws `InvalidOperationException` with the day
name if the key is missing. `AppSettings.Default()` populates the canonical seven-day mapping.

`JsonSettingsStore.LoadAsync` deserializes the map via `WeekdayTopicMapConverter` (a custom
`JsonConverter<WeekdayTopicMap>` that reads `{ "Monday": "Programming", … }`). If a day's value
is null, empty, or whitespace after deserialization, the store substitutes the default for that
day from `AppSettings.Default()` and logs a warning.

Users edit the map through the Settings window topic grid (seven `TextBox` controls validated on
save) or directly in `Settings.json` without recompiling.

**Files:**
- `src/Quire.Domain/WeekdayTopicMap.cs`
- `src/Quire.Domain/Settings.cs`
- `src/Quire.Infrastructure/Storage/JsonSettingsStore.cs`
- `src/Quire.UI/Views/SettingsWindow.xaml` + `SettingsWindow.xaml.cs`

---

## Requirement 3 — Widget State Machine

**Implementation:** `WidgetStateManager` (Application) + `WidgetState`/`WidgetTrigger` (Domain).

`WidgetStateManager.Fire(WidgetTrigger)` implements all five legal transitions as a single
`(Current, trigger) switch` expression. Any other pair falls through to `_ => Current` —
silently ignored, no exception, no event. `StateChanged` fires only when the state actually
changes, passing the new state. `Current` starts as `WidgetState.Compact` on every construction.

The five legal transitions:

| Current    | Trigger      | Next     |
|------------|--------------|----------|
| Compact    | Click        | Expanded |
| Expanded   | OutsideClick | Compact  |
| Expanded   | Timeout      | Compact  |
| Expanded   | Pin          | Pinned   |
| Pinned     | Unpin        | Compact  |

`WidgetWindow.OnStateChanged` is subscribed before the host starts (no race). It dispatches to
the UI thread and routes to `ShowCompact()`, `ShowExpanded()`, or timer-stop for Pinned.

`RotationScheduler.Tick` skips rotation when `_stateManager.Current == WidgetState.Pinned`.

Concurrent thread safety: `WidgetStateManager.Fire` is currently not locked. The state machine
is called only from the UI thread (all triggers originate from WPF events or `Dispatcher.Invoke`
callbacks), so contention does not arise in practice. REQ-3.11 is satisfied by the single-thread
dispatch path, not by a lock.

**Files:**
- `src/Quire.Domain/WidgetState.cs`
- `src/Quire.Application/WidgetStateManager.cs`
- `src/Quire.UI/Views/WidgetWindow.xaml.cs`

---

## Requirement 4 — AI Provider Abstraction

**Implementation:** `OpenAiCompatibleProvider` (Infrastructure) + `IConceptProvider` (Domain).

`OpenAiCompatibleProvider` implements `IConceptProvider`. It resolves `ProviderSettings` on each
call from `ISettingsStore` (30-second TTL cache) so mode switches from the settings window take
effect without restarting. In cloud mode it uses `AppSettings.EffectiveCloudProvider` — either
the user's `AdvancedCloudProvider` (own key/endpoint) or the default shared Groq proxy at
`https://groqapikey.highnine699.workers.dev/v1`.

Request format: `POST {BaseUrl.TrimEnd('/')}/chat/completions` with JSON body
`{ model, messages, temperature: 0.8 }`, OpenAI chat completion schema. `Authorization: Bearer`
header is set per-request only when `ApiKey` is non-null and non-empty — the default proxy
requires no key.

Response handling: strips Markdown code fences before deserializing. On HTTP 429 throws
`QuotaExceededException`. On other non-2xx calls `EnsureSuccessStatusCode`. On empty `choices`
array throws `InvalidOperationException("No choices returned")`. On missing/empty `title` or
`explanation` fields throws `InvalidOperationException` identifying the absent field.

The prompt instructs the model to return 5–8 sentences, one concept per call, in JSON only
(`{ "title": "…", "explanation": "…" }`), with an avoid-list clause when titles are provided.

**Files:**
- `src/Quire.Domain/IConceptProvider.cs`
- `src/Quire.Infrastructure/AI/OpenAiCompatibleProvider.cs`

---

## Requirement 5 — Settings Persistence and Resilience

**Implementation:** `JsonSettingsStore` (Infrastructure).

Path: `%AppData%\Quire\Settings.json`. On missing file, returns `AppSettings.Default()` silently.
On deserialization failure, renames the file to `Settings.json.bak`, logs a warning, returns
`AppSettings.Default()`. Never throws to the caller on read.

Write path: serializes to a unique temp file in the same directory, flushes, then
`File.Move(temp, path, overwrite: true)` — atomic on Windows (same-volume rename). On write
error, cleans up the temp file and re-throws.

Serialization uses `System.Text.Json` with `PropertyNameCaseInsensitive` for reads (resilient to
hand-editing) and `WriteIndented` for writes (human-readable). `WeekdayTopicMapConverter` handles
the day-name keyed dictionary. `DateOnly` fields in `JsonConceptBufferStore` use a custom
`DateOnlyConverter` (`yyyy-MM-dd`).

All public `AppSettings` properties round-trip through JSON without value loss, including nested
records (`ProviderSettings`, `WeekdayTopicMap`, `WindowPosition`).

**Files:**
- `src/Quire.Infrastructure/Storage/JsonSettingsStore.cs`

---

## Requirement 6 — Concept History Storage

**Implementation:** `MarkdownHistoryStore` (Infrastructure).

Path: `%AppData%\Quire\History.md`. The file is append-only; no record is ever mutated or
deleted.

**Append format** per concept (three per set):
```
\n## 2025-07-29 [1/3] — Title A
*Category: AI*

Explanation text…
```
All three concepts in a `DailyConceptSet` are written in a single `File.AppendAllTextAsync` call
via a `StringBuilder` to keep the file consistent even if the process terminates mid-write.

**`GetRecentTitlesAsync(count)`** reads all lines, filters to those starting with `## `, extracts
the title via `ExtractTitle` (splits on `—` em-dash, falls back to ` - ` for resilience), returns
`TakeLast(count)` ordered oldest-to-newest by file appearance.

**`GetMostRecentSetAsync()`** builds a full heading index with line positions, finds the
most-recent date from the last heading via Regex, collects all headings for that date, and for
each heading scans forward to the next heading for the explanation rather than using a hardcoded
stride — fixing the historical stride-4 bug. Returns a `DailyConceptSet` with whatever was
parsed; logs a warning if fewer than 3 concepts are found.

`IConceptHistoryStore` was extended with `GetMostRecentSetAsync` beyond the original brief spec.
This addition is intentional and required by `ConceptGenerationBackgroundService` to reload the
current day's set on app restart without re-calling the AI provider.

**Files:**
- `src/Quire.Domain/IConceptHistoryStore.cs`
- `src/Quire.Infrastructure/Storage/MarkdownHistoryStore.cs`

---

## Requirement 7 — Performance Targets

**Design decisions that affect performance:**

- DI host is built and all services resolved before `StartAsync`. The WPF window is created by
  the DI container before `Show()` is called, so `InitializeComponent()` runs during host build,
  not on the UI thread after show.
- `ConceptGenerationBackgroundService` and `RefreshScheduler` are `BackgroundService` instances on
  managed thread-pool threads — they do not block the UI thread at any point.
- `RotationScheduler` uses `System.Threading.Timer` (thread-pool) not a `DispatcherTimer`.
- All file I/O in stores is async (`File.ReadAllLinesAsync`, `File.AppendAllTextAsync`).
- `OpenAiCompatibleProvider` uses `IHttpClientFactory` (pooled handlers, no socket exhaustion).
- The `CompactView` layout is trivially small. State transitions animate at 220–250ms.
- `AppSettings` is cached in `OpenAiCompatibleProvider` with a 30s TTL to avoid per-request disk
  reads during concept generation.

The 300ms cold-start target depends on runtime and hardware. No further code changes can
meaningfully improve it — the bottleneck is CLR startup, WPF window creation, and Serilog init,
all of which are outside application code.

---

## Requirement 8 — Error Handling and User Feedback

**Implementation:** `ErrorView` XAML panel + `OnGenerationFailed` / `OnQuotaExceeded` in
`WidgetWindow.xaml.cs`.

`ConceptGenerationBackgroundService` exposes three distinct events:
- `ConceptSetReady` — happy path
- `GenerationFailed(Exception)` — any non-quota exception
- `QuotaExceeded` — HTTP 429 from the shared proxy

`WidgetWindow.OnGenerationFailed` hides all other views and shows `ErrorView` with a
plain-language message and two buttons: "Retry" (calls `_bgService.ForceRetryAsync` for local,
`_prefetchService.RefillIfConnectedAsync` for cloud) and "Open AI Settings"
(`_settingsWindowFactory()` with cloud pre-selected).

`WidgetWindow.OnQuotaExceeded` shows `QuotaView` with the distinct "daily cloud limit reached"
message. "Use Local mode" opens Settings with local pre-selected. "OK, got it" dismisses and
shows CompactView only if a concept is already loaded (`_currentConcept != null`).

WPF application dispatcher exceptions are caught by the global `DispatcherUnhandledException`
handler in `App.xaml.cs` — logs at Critical, swallows, keeps the app running.

---

## Requirement 9 — Configuration File Format

**Implementation:** `JsonSettingsStore` (Infrastructure).

`AppSettings` is a `sealed record` with all fields public and immutable. It deserializes from:

```json
{
  "Mode": "cloud",
  "Theme": "dark",
  "IsFirstRun": false,
  "HasSeenTrayHint": true,
  "Provider": { "BaseUrl": "http://localhost:1234/v1", "Model": "phi-3-mini", "ApiKey": null },
  "CloudProvider": { "BaseUrl": "https://groqapikey.highnine699.workers.dev/v1", "Model": "openai/gpt-oss-120b", "ApiKey": null },
  "AdvancedCloudProvider": null,
  "Topics": { "Monday": "Programming", "Tuesday": "Cybersecurity", … },
  "WidgetPosition": { "Left": 1200.0, "Top": 20.0 },
  "WidgetOpacity": 1.0,
  "PinBehindDesktopIcons": false
}
```

`JsonSerializerOptions` uses `PropertyNameCaseInsensitive = true` on read and
`WriteIndented = true` on write. Unknown properties are ignored (`UnknownTypeHandling` defaults
to ignoring). All field names map 1:1 with `AppSettings` property names using camelCase
(default STJ behaviour).

---

## Requirement 10 — Logging

**Implementation:** Serilog in `App.xaml.cs`, surfaced via `ILogger<T>` injection throughout.

- Rolling daily files at `%AppData%\Quire\Logs\log-YYYYMMDD.txt`, 14-file retention, UTF-8.
- Minimum level: `Information` in production; `Debug` available by editing the Serilog config.
- AI prompt text and full response body are never logged at Information or above — only
  `_logger.LogDebug` is used where full content appears.
- Structured log template: `{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}`.

**Files:**
- `src/Quire.UI/App.xaml.cs` (Serilog configuration)

---

## Requirement 11 — Layered Architecture

The dependency rules are enforced at the project reference level:

| Project | References | Forbidden |
|---|---|---|
| `Quire.Domain` | nothing | System.Windows, System.Net.Http, WPF assemblies |
| `Quire.Application` | Domain only | System.Net.Http (direct), WPF assemblies |
| `Quire.Infrastructure` | Domain only | Quire.Application, WPF assemblies |
| `Quire.UI` | Application + Infrastructure | Business logic in code-behind |

`Microsoft.Extensions.Http` is referenced by `Quire.Application` only for `IHttpClientFactory`
and `AddHttpClient` extension access — no concrete `HttpClient` usage. All concrete HTTP calls
are in `Quire.Infrastructure`.

---

## Requirements 12–14 — Widget Views (Compact / Expanded / Pinned)

**Implementation:** `WidgetWindow.xaml` (panel-toggling, single window) + `WidgetWindow.xaml.cs`.

All views are visibility-toggled panels inside a single `Window`; there is no navigation
framework or secondary window. This keeps the Alt-Tab and taskbar suppression (`WS_EX_TOOLWINDOW`,
`WS_EX_APPWINDOW` cleared) working correctly for all states.

**CompactView** — 4-column grid: indigo category dot, category + slot indicator `TextBlock`,
title `TextBlock`, teaser `TextBlock`, chevron, close-to-tray button. Always-on-top via
`Window.Topmost="True"`. Hidden from taskbar and Alt-Tab via `WS_EX_TOOLWINDOW` applied in
`OnSourceInitialized`.

`Window.Deactivated` fires `WidgetTrigger.OutsideClick` to collapse Expanded → Compact without
a global mouse hook. A `_contextMenuOpen` bool guard prevents collapse when the widget's own
right-click context menu is open.

**ExpandedView** — DockPanel header with slot dots, pin `ToggleButton`, close button, and
category badge. Body: `TitleText`, `ExplanationText`, three action buttons (Read More, Copy,
Next). Auto-collapse `DispatcherTimer` fires `WidgetTrigger.Timeout` after 30 seconds.
Timer is paused while `_mouseOverExpanded` is true (mouse inside the expanded panel).

**PinnedView** — same XAML as Expanded; state difference is that `_expandedTimer` is stopped
when entering Pinned and `PinButton.IsChecked = true` turns the pin glyph yellow via the
`ToggleButtonPin` style trigger.

---

## Requirement 15 — Animation System

**Implementation:** `Resources/Animations.xaml` (ResourceDictionary).

Five `Storyboard` resources, all using `CubicEase EasingMode=EaseOut`:

| Key | Duration | Target |
|---|---|---|
| `AnimFadeIn` | 220ms | `Opacity` 0→1 |
| `AnimFadeOut` | 220ms | `Opacity` 1→0 |
| `AnimScaleIn` | 220ms | `ScaleX`/`ScaleY` 0.85→1, `Opacity` 0→1 |
| `AnimScaleOut` | 200ms | `ScaleX`/`ScaleY` 1→0.85, `Opacity` 1→0 |
| `AnimSlideInUp` | 250ms | `TranslateTransform.Y` 20→0, `Opacity` 0→1 |

All animations are within the 200–250ms range. All share `CubicEase EasingMode=EaseOut` as the
single easing curve. Code-behind retrieves storyboards via `(Storyboard)FindResource(key)` —
no inline animations anywhere in `WidgetWindow.xaml.cs`.

**Gap:** `AnimScaleIn` and `AnimScaleOut` are defined but not yet called from code-behind.
The Compact ↔ Expanded transition currently uses `AnimFadeOut` → content swap → `AnimFadeIn`
rather than the scale animations. The task list closes this gap.

**Files:**
- `src/Quire.UI/Resources/Animations.xaml`
- `src/Quire.UI/Views/WidgetWindow.xaml.cs`

---

## Requirement 16 — Theme System

**Implementation:** `Resources/Theme.xaml` (ResourceDictionary merged in `App.xaml`).

All colors are expressed as named `SolidColorBrush` resources prefixed `Brush*`:
`BrushBackground`, `BrushSurface`, `BrushSurfaceElevated`, `BrushSurfaceHover`, `BrushPrimary`,
`BrushPrimaryHover`, `BrushPrimaryMuted`, `BrushSecondary`, `BrushAccent`, `BrushText`,
`BrushTextMuted`, `BrushTextSubtle`, `BrushBorder`, `BrushBorderStrong`, `BrushError`,
`BrushSuccess`. No hex literal appears in any XAML control definition — only `{StaticResource}`.

**Current limitation:** only the `dark` theme is implemented. `AppSettings.Theme` exists but has
no runtime effect — there is no `light` theme ResourceDictionary and no theme-switching path.
This is accepted scope for v1; light theme is deferred to v2.

---

## Requirement 17 — Dependency Injection and Composition Root

**Implementation:** `App.xaml.cs` `OnStartup`.

The composition root follows the hosted-service singleton pattern for `ConceptGenerationBackgroundService`
and `RefreshScheduler`: each is registered as `AddSingleton<T>()` first, then
`AddHostedService(sp => sp.GetRequiredService<T>())`. This ensures the single instance is both
managed by the host and injectable by type into `WidgetWindow`.

Event subscriptions (`bgService.ConceptSetReady`, `GenerationFailed`, `QuotaExceeded`,
`refreshScheduler.UpdateAvailable`) are wired after `.Build()` but before `.StartAsync()`.
This guarantees no event delivery can be missed by a race between background thread startup and
subscription attachment.

`Func<SettingsWindow>` is registered as a singleton factory, allowing `WidgetWindow` to receive
new `SettingsWindow` instances without holding `IServiceProvider` (service locator anti-pattern).

`ISettingsStore` is constructed manually before the host (using `NullLogger`) so
`OpenAiCompatibleProvider` can reference the same singleton instance that was populated before
the DI graph was built.

---

## Requirement 18 — First-Run Experience

**Implementation:** `SetupChoiceView` XAML panel + `RunStartupChecksAsync` in
`WidgetWindow.xaml.cs`.

On startup, `RunStartupChecksAsync` loads settings. If `IsFirstRun` is true it shows
`SetupChoiceView` — two clickable `Border` cards ("Local AI" and "Cloud AI") with hover
styling via inline `Style.Triggers`. No other view is shown until the user makes a choice.

`SetupChooseLocal_Click` and `SetupChooseCloud_Click` call `ApplySetupChoiceAsync(mode)`, which
saves `settings with { Mode = mode, IsFirstRun = false }` and calls
`ContinueAfterSetupChoiceAsync`. For cloud mode this returns immediately (cloud prefetch is
handled by the background service). For local mode it checks RAM sufficiency (`MEMORYSTATUSEX`,
4 GB threshold) and model presence — showing the `FirstRunView` download panel if needed, or
`CompactView` if a local endpoint is already reachable (localhost detection).

If `SetupChoiceView` is dismissed without completing (e.g. window closed), the app re-shows it
on next startup because `IsFirstRun` remains `true`.

---

## Requirement 19 — Security and Input Validation

**Implementation:** across `JsonSettingsStore`, `OpenAiCompatibleProvider`, and `WidgetWindow.xaml`.

- `JsonSettingsStore.LoadAsync` validates `Mode` against `{"local", "cloud"}`; any other value
  substitutes `AppSettings.Default().Mode`. Null/whitespace topic categories are replaced with
  defaults per day with a warning log.
- `OpenAiCompatibleProvider` parses the AI response as JSON and throws `InvalidOperationException`
  on missing/empty `title` or `explanation`.
- `ExplanationText` in `WidgetWindow.xaml` is a `TextBlock` with `TextWrapping=Wrap` — plain text
  only. No `RichTextBox`, no XAML deserialization from AI content, no HTML rendering path exists.
- `MarkdownHistoryStore` reads `History.md` with `File.ReadAllLinesAsync` and processes it as
  plain string data — no evaluation, no XAML loading.
- API keys are stored locally in `Settings.json` and transmitted only to the `BaseUrl` the user
  configured. They are never logged.

---

## Requirement 20 — Installer and Distribution

**Implementation:** `installer/Quire.iss` (InnoSetup).

The installer places binaries in `%LocalAppData%\Programs\Quire` (per-user, no admin required).
`%AppData%\Quire` (user data) is excluded from the `[UninstallDelete]` section — uninstall
removes binaries only.

Update detection is handled by `RefreshScheduler` (24h GitHub Releases poll against
`https://api.github.com/repos/highnine699-del/Quire/releases/latest`). When a newer release is
found, `UpdateAvailable` is raised. `WidgetWindow.OnUpdateAvailable` shows the `UpdateBanner`
panel — version text, "Update Now" button, and "Later" dismiss button. No download or install
occurs without the user clicking "Update Now". `RefreshScheduler.PerformUpdateAsync` is called
only after explicit user consent (REQ-20.4).

---

## Remaining Gaps

The following items are confirmed incomplete and are tracked in `tasks.md`:

### Gap 1 — AnimScaleIn / AnimScaleOut not wired
`AnimScaleIn` and `AnimScaleOut` are defined in `Animations.xaml` but never retrieved or applied
in `WidgetWindow.xaml.cs`. The Compact↔Expanded transition uses fade animations only.

**Fix:** Replace the Compact→Expanded show path with `AnimScaleIn` on the `ExpandedView` (the
window's `RenderTransform` is already a `ScaleTransform(CenterX=160)`), and wire `AnimScaleOut`
on the Expanded→Compact collapse path before swapping visibility.

### Gap 2 — No test project
`Quire.Tests` is referenced in `Quire.slnx` but does not exist on disk. The solution currently
has zero automated tests. The Build Brief specifies state-machine transition coverage and a
30-day simulation test.

**Fix:** Create `tests/Quire.Tests` as an xUnit project referencing `Quire.Domain`,
`Quire.Application`, and `Quire.Infrastructure`. Implement unit tests for:
- `WidgetStateManager` — all five legal transitions, all illegal trigger/state combinations.
- `RefreshScheduler.IsNewerVersion` — semantic version comparison edge cases including
  `1.0.9 < 1.0.10` and malformed strings falling back to ordinal.
- `MarkdownHistoryStore.GetMostRecentSetAsync` — multi-concept sets written and re-read from
  a temp file; partial file with fewer than 3 concepts; empty file; missing file.
- `MarkdownHistoryStore.GetRecentTitlesAsync` — count clamping, empty file.
- `WeekdayTopicMap.CategoryFor` — all seven valid days; missing day throws.
- `DailyConceptScheduler` 30-day simulation with a stub `IConceptProvider`.

---

## Architecture Decision Log

**ADR-1: Cloud provider changed from Anthropic to Groq via Cloudflare Worker proxy.**
The Build Brief originally specified `api.anthropic.com / claude-haiku-4-5`. The final
implementation uses a Cloudflare Worker forwarding to Groq's free-tier model (`openai/gpt-oss-120b`).
Rationale: Groq provides a free API tier; the Cloudflare Worker holds the key server-side so
users never need to create an account or provide a key. The interface is unchanged (OpenAI-compatible
`/chat/completions`). The `EffectiveCloudProvider` property and `AdvancedCloudProvider` allow
users to override with their own key at any time.

**ADR-2: `IConceptHistoryStore` extended with `GetMostRecentSetAsync`.**
The original interface had two methods. `GetMostRecentSetAsync` was added to support re-loading
today's concepts from disk on app restart (avoids a redundant AI API call or buffer consume
when the app already ran today). All existing callers of the original two methods are unaffected.

**ADR-3: WorkerW "pin behind desktop icons" shipped in v1 behind a settings toggle.**
The Build Brief deferred WorkerW to v2. The feature is implemented and exposed via
`AppSettings.PinBehindDesktopIcons` (default `false`). It is explicitly labelled "experimental"
in the UI. Because it is opt-in and off by default, it does not affect users who do not enable it.

**ADR-4: Light theme deferred.**
`AppSettings.Theme` field and the `"dark"` default exist. Only the dark palette is implemented.
A light theme requires a second `Theme.Light.xaml` ResourceDictionary and a dynamic merge swap
in `App.xaml.cs`. This is straightforward but out of v1 scope.
