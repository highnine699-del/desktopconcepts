# Requirements Document

## Introduction

DesktopConcepts is a lightweight Windows desktop widget (.NET 8, WPF) that delivers one AI-generated technical concept per day. The widget operates in three visible states (Compact, Expanded, Pinned) governed by a strict state machine. AI generation runs through a generic OpenAI-compatible HTTP endpoint, supporting local-first inference by default with an optional cloud upgrade. All data — settings, history, logs — resides in `%AppData%\DesktopConcepts`. The application must never crash on bad configuration or an offline AI provider, must start in under 300 ms, and must stay under 120 MB idle memory and near-zero idle CPU.

---

## Glossary

- **Widget**: The DesktopConcepts WPF window that sits on the desktop.
- **Concept**: A domain record containing a title, a 5–8 sentence explanation, a category, and the date generated.
- **ConceptProvider**: The component that calls an AI backend to generate a Concept; implements `IConceptProvider`.
- **HistoryStore**: The append-only Markdown file store for generated concepts; implements `IConceptHistoryStore`.
- **SettingsStore**: The JSON-backed persistent store for application settings; implements `ISettingsStore`.
- **StateManager**: The component that owns the widget's current `WidgetState` and enforces legal state transitions.
- **DailyScheduler**: The background service that triggers concept generation exactly once per calendar day.
- **RotationScheduler**: The background service that rotates the displayed concept card every N minutes when multiple concepts are queued.
- **RefreshScheduler**: The background service that checks for application updates.
- **WeekdayTopicMap**: The user-configurable mapping from `DayOfWeek` to category name.
- **AppSettings**: The runtime configuration record deserialized from `Settings.json`.
- **ProviderSettings**: The sub-record of `AppSettings` that holds `BaseUrl`, `Model`, and optional `ApiKey`.
- **FirstRunFlow**: The guided setup sequence shown when the application starts with no local model available.
- **Compact state**: The small always-on-top badge state of the Widget.
- **Expanded state**: The full-detail panel state of the Widget.
- **Pinned state**: The Expanded state that never auto-collapses.
- **LocalMode**: Operating mode where `ConceptProvider` calls a local OpenAI-compatible inference server.
- **CloudMode**: Operating mode where `ConceptProvider` calls a remote API (e.g., OpenAI, Anthropic).
- **AnimationSet**: The shared WPF resource dictionary of Fade, Scale, Slide, Expand, and Collapse animations.

---

## Requirements

### Requirement 1: Daily Concept Generation

**User Story:** As a developer, I want the widget to automatically generate one new technical concept each day, so that I learn something new without manual effort.

#### Acceptance Criteria

1. THE DailyScheduler SHALL trigger `IConceptProvider.GenerateConceptAsync` exactly once per calendar day.
2. WHEN `GenerateConceptAsync` completes successfully, THE DailyScheduler SHALL invoke `IConceptHistoryStore.AppendAsync` with the resulting Concept before raising the `ConceptGenerated` event.
3. WHEN the DailyScheduler checks whether today's concept has already been generated, THE DailyScheduler SHALL compare `DateOnly.FromDateTime(DateTime.Now)` against the last-run date persisted alongside `Settings.json`, not a 24-hour elapsed-time counter.
4. WHEN a Concept is generated, THE ConceptProvider SHALL populate the `Category` field using the value returned by `WeekdayTopicMap.CategoryFor(today.DayOfWeek)`.
5. THE ConceptProvider SHALL request a concept explanation of 5 to 8 sentences in length within the AI prompt.
6. WHEN `GetRecentTitlesAsync` returns one or more titles, THE ConceptProvider SHALL include no more than the 10 most recent titles in the AI prompt as an "avoid repeating these" instruction.
7. WHEN `GenerateConceptAsync` returns a response and the response is parsed as JSON, THE DailyScheduler SHALL verify that both `Title` and `Explanation` fields are non-empty strings before raising `ConceptGenerated`.
8. WHEN `IConceptProvider.GenerateConceptAsync` throws any exception, THE DailyScheduler SHALL NOT raise `ConceptGenerated`, SHALL NOT invoke `AppendAsync`, and SHALL mark the concept generation as eligible for retry later the same day.
9. WHEN `IConceptHistoryStore.AppendAsync` throws any exception after a successful `GenerateConceptAsync`, THE DailyScheduler SHALL NOT raise `ConceptGenerated`, SHALL log the error, and SHALL NOT attempt to retry the same concept for that day.

---

### Requirement 2: Weekday Category Rotation

**User Story:** As a developer, I want each day of the week to cover a different technical category, so that my learning spans multiple disciplines.

#### Acceptance Criteria

1. THE WeekdayTopicMap SHALL map each of the seven `DayOfWeek` values to exactly one non-empty category string of at most 100 characters.
2. WHEN `AppSettings.Default()` is called, THE result SHALL contain a WeekdayTopicMap with the following default mappings: Monday → "Programming", Tuesday → "Cybersecurity", Wednesday → "Networking", Thursday → "AI", Friday → "Operating Systems", Saturday → "Mathematics", Sunday → "Computer Engineering".
3. WHEN a user edits `Settings.json` and restarts the Widget, THE SettingsStore SHALL load the updated WeekdayTopicMap and apply it to all subsequent `CategoryFor` calls without recompiling the application.
4. IF a category string loaded from `Settings.json` is null, empty, or whitespace for any day, THEN THE SettingsStore SHALL substitute the default category for that day from `AppSettings.Default()` and SHALL log a warning identifying the affected day.
5. IF `WeekdayTopicMap.CategoryFor` is called with a `DayOfWeek` value not present in the map, THEN THE WeekdayTopicMap SHALL throw an `InvalidOperationException` with a message that identifies the missing day by name.

---

### Requirement 3: Widget State Machine

**User Story:** As a user, I want the widget to switch between Compact, Expanded, and Pinned modes through predictable interactions, so that it stays out of my way until I need it.

#### Acceptance Criteria

1. THE StateManager SHALL initialise with `WidgetState.Compact` on every application start, regardless of the state at the previous application exit.
2. WHEN `StateManager.Fire(WidgetTrigger.Click)` is called while the current state is `WidgetState.Compact`, THE StateManager SHALL transition to `WidgetState.Expanded`.
3. WHEN `StateManager.Fire(WidgetTrigger.OutsideClick)` is called while the current state is `WidgetState.Expanded`, THE StateManager SHALL transition to `WidgetState.Compact`.
4. WHEN `StateManager.Fire(WidgetTrigger.Timeout)` is called while the current state is `WidgetState.Expanded`, THE StateManager SHALL transition to `WidgetState.Compact`.
5. WHEN `StateManager.Fire(WidgetTrigger.Pin)` is called while the current state is `WidgetState.Expanded`, THE StateManager SHALL transition to `WidgetState.Pinned`.
6. WHEN `StateManager.Fire(WidgetTrigger.Unpin)` is called while the current state is `WidgetState.Pinned`, THE StateManager SHALL transition to `WidgetState.Compact`.
7. WHEN `StateManager.Fire` is called with any trigger that is not a legal transition for the current state, THE StateManager SHALL retain the current state, SHALL NOT throw an exception, and SHALL NOT raise the `StateChanged` event.
8. WHEN the state changes, THE StateManager SHALL raise the `StateChanged` event with both the previous `WidgetState` and the new `WidgetState` before returning from `Fire`.
9. WHILE the Widget is in `WidgetState.Pinned`, THE RotationScheduler SHALL not initiate any card-rotation action until the Widget leaves `WidgetState.Pinned`.
10. WHILE the Widget is in `WidgetState.Pinned`, THE DailyScheduler SHALL continue to generate concepts in the background.
11. WHEN `StateManager.Fire` is called concurrently from multiple threads, THE StateManager SHALL process each call serially such that no transition is lost or applied out of order.

---

### Requirement 4: AI Provider Abstraction

**User Story:** As a developer, I want all AI calls to go through a single interface, so that I can swap providers without changing any other part of the application.

#### Acceptance Criteria

1. THE ConceptProvider SHALL implement `IConceptProvider` and SHALL send all AI requests as HTTP POST to `{BaseUrl.TrimEnd('/')}/chat/completions` with a JSON body containing `model`, `messages`, and `temperature` fields matching the OpenAI chat completion request schema.
2. WHEN `ProviderSettings.ApiKey` is non-null and non-empty, THE ConceptProvider SHALL include an `Authorization: Bearer {ApiKey}` header on every request.
3. WHEN the HTTP response status code is not in the 2xx range OR when a network or transport-level exception occurs, THE ConceptProvider SHALL propagate the exception to the caller without swallowing or logging it internally.
4. WHEN the AI response body cannot be deserialized into a valid `ConceptPayload`, THE ConceptProvider SHALL throw an `InvalidOperationException` whose message identifies the field that is absent or unparseable.
5. THE ConceptProvider SHALL be instantiable with only `BaseUrl`, `Model`, and an optional `ApiKey`; no other provider-specific configuration SHALL be required.
6. WHEN `CloudMode` is active, THE ConceptProvider SHALL exhibit the same observable behavior as in `LocalMode`, differing only in the values of `ProviderSettings.BaseUrl` and `ProviderSettings.ApiKey`.
7. WHEN the AI response body contains an empty `choices` array, THE ConceptProvider SHALL throw an `InvalidOperationException` with a message indicating that no choices were returned.

---

### Requirement 5: Settings Persistence and Resilience

**User Story:** As a user, I want my configuration saved on disk and surviving corruption, so that the app always starts even if I hand-edited the settings file badly.

#### Acceptance Criteria

1. THE SettingsStore SHALL read and write `Settings.json` at `%AppData%\DesktopConcepts\Settings.json`.
2. WHEN `Settings.json` does not exist, THE SettingsStore SHALL return `AppSettings.Default()` without throwing an exception.
3. IF `Settings.json` is present but fails JSON deserialization for any reason, THEN THE SettingsStore SHALL rename the file to `Settings.json.bak`, return `AppSettings.Default()`, and SHALL NOT throw an exception to the caller.
4. WHEN `SettingsStore.SaveAsync` is called, THE SettingsStore SHALL write to a temporary file first and then atomically replace `Settings.json`, using UTF-8 encoding without BOM, after creating the `%AppData%\DesktopConcepts` directory if it does not already exist.
5. FOR ALL valid `AppSettings` values, serializing then deserializing `Settings.json` SHALL produce an `AppSettings` where every public property, including nested objects, is equal by value to the original.

---

### Requirement 6: Concept History Storage

**User Story:** As a user, I want every generated concept stored in a readable history file, so that I can review past concepts and so the system avoids repeating topics.

#### Acceptance Criteria

1. THE HistoryStore SHALL append each new Concept to `%AppData%\DesktopConcepts\History.md` in the format `\n## {GeneratedOn:yyyy-MM-dd} — {Title}\n*Category: {Category}*\n\n{Explanation}\n`, where `GeneratedOn` uses the UTC date at the time of the `AppendAsync` call.
2. WHEN `GetRecentTitlesAsync(count)` is called with a positive integer `count`, THE HistoryStore SHALL return the last `count` concept titles parsed from lines beginning with `## ` in `History.md`, ordered from oldest to newest by file appearance.
3. WHEN `GetRecentTitlesAsync(count)` is called and `History.md` contains fewer than `count` entries, THE HistoryStore SHALL return all available titles without throwing an exception.
4. WHEN `History.md` does not exist, THE HistoryStore SHALL return an empty list from `GetRecentTitlesAsync` without throwing an exception.
5. IF the `%AppData%\DesktopConcepts` directory does not exist before an `AppendAsync` call, THEN THE HistoryStore SHALL create it without overwriting any existing file content.
6. FOR ALL sequences of Concept values written via `AppendAsync`, calling `GetRecentTitlesAsync` with a count equal to the number of appended entries SHALL return all appended titles in the order they were written.
7. WHEN an I/O exception occurs during `AppendAsync` or `GetRecentTitlesAsync`, THE HistoryStore SHALL propagate the exception to the caller and SHALL NOT leave a partial or corrupted entry in `History.md`.

---

### Requirement 7: Performance Targets

**User Story:** As a user, I want the widget to start instantly and consume minimal resources, so that it does not noticeably slow my machine.

#### Acceptance Criteria

1. THE Widget SHALL reach a responsive UI state — defined as the first interactive window rendered and accepting user input — within 300 milliseconds of OS process launch on a machine with at least a quad-core 1.8 GHz CPU, 8 GB RAM, and an SSD.
2. WHILE no concept generation is in progress, THE Widget SHALL consume less than 120 MB of private working-set memory.
3. WHILE no concept generation is in progress, THE Widget SHALL consume less than 1% of CPU averaged over any 5-second window.
4. WHILE concept generation is in progress, THE Widget SHALL consume no more than 15% of CPU averaged over any 5-second window.
5. WHILE concept generation is in progress, THE Widget SHALL consume no more than 300 MB of private working-set memory.
6. THE Widget SHALL NOT require an active internet connection during `LocalMode` operation, including startup, all state machine transitions, and concept display.

---

### Requirement 8: Error Handling and User Feedback

**User Story:** As a user, I want to see a clear, actionable message when AI generation fails, so that I know what happened and how to fix it.

#### Acceptance Criteria

1. WHEN `IConceptProvider.GenerateConceptAsync` throws any exception, THE DailyScheduler SHALL raise the `GenerationFailed` event with the caught exception without re-throwing.
2. WHEN `GenerationFailed` is raised, THE Widget UI SHALL display a plain-language error message and present two actions: "Retry" and "Open AI Settings".
3. IF the user is in `CloudMode` and has no active internet connection, THEN THE Widget UI SHALL display a plain-language message stating that cloud generation requires internet access, and SHALL display the last cached concept if one exists in `History.md`.
4. WHEN an unhandled exception reaches the WPF application dispatcher, THE Widget SHALL log the exception at Critical level and SHALL remain running; it SHALL NOT show a raw stack trace to the user.
5. THE Widget SHALL NOT terminate the process in response to any single concept-generation failure.

---

### Requirement 9: Configuration File Format

**User Story:** As a developer, I want all configurable values stored in a single JSON file, so that I can change AI provider, theme, or topics without recompiling.

#### Acceptance Criteria

1. THE SettingsStore SHALL deserialize `Settings.json` into an `AppSettings` record containing `Mode`, `Theme`, `Provider` (with `BaseUrl`, `Model`, and optional `ApiKey`), and `Topics` (a day-to-category map).
2. WHEN `SettingsStore.SaveAsync` is called with an `AppSettings` value, THE SettingsStore SHALL write indented JSON to `Settings.json` such that the file is human-readable without a JSON formatter.
3. THE SettingsStore SHALL treat unrecognized JSON properties in `Settings.json` as ignorable rather than raising a deserialization error.

---

### Requirement 10: Logging

**User Story:** As a developer, I want structured, rolling log files, so that I can diagnose issues without wading through a single massive log.

#### Acceptance Criteria

1. THE Widget SHALL write log entries at four severity levels: Information, Warning, Error, and Critical.
2. THE Widget SHALL write log files to `%AppData%\DesktopConcepts\Logs\` with one log file per calendar day and the filename pattern `log-{yyyy-MM-dd}.txt`.
3. WHEN a log entry is at Debug level, THE Widget SHALL write it only when debug logging is explicitly enabled in `AppSettings`; Debug entries SHALL NOT appear in production log files by default.
4. THE Widget SHALL NOT log full AI prompt text or full AI response text at Information level or above.

---

### Requirement 11: Layered Architecture

**User Story:** As a developer, I want the codebase split into Domain, Application, Infrastructure, and UI layers with enforced dependency rules, so that each layer can be tested and replaced independently.

#### Acceptance Criteria

1. THE `DesktopConcepts.Domain` project SHALL contain zero references to `System.Windows`, `System.Net.Http`, or any WPF assembly.
2. THE `DesktopConcepts.Application` project SHALL reference only `DesktopConcepts.Domain` and SHALL contain zero direct references to `System.Net.Http` or any WPF assembly.
3. THE `DesktopConcepts.Infrastructure` project SHALL reference `DesktopConcepts.Domain` and SHALL NOT reference `DesktopConcepts.Application` or any WPF assembly.
4. THE `DesktopConcepts.UI` project SHALL reference `DesktopConcepts.Application` and `DesktopConcepts.Infrastructure` for DI wiring only; business logic SHALL reside in Application or Domain, not in code-behind files.
5. THE `DesktopConcepts.Tests` project SHALL be able to test Domain and Application layers without instantiating any WPF UI component.

---

### Requirement 12: Compact Widget View

**User Story:** As a user, I want a small, unobtrusive badge on my desktop, so that I always know there is a concept available without it getting in my way.

#### Acceptance Criteria

1. WHILE the Widget is in `WidgetState.Compact`, THE Widget SHALL display a `Border` containing a `TextBlock` that shows a short label (e.g., "Today's AI").
2. WHILE the Widget is in `WidgetState.Compact`, THE Widget SHALL remain topmost using `Topmost="True"` and SHALL NOT appear in the taskbar or Alt-Tab switcher.
3. WHEN the Widget window loses focus in `WidgetState.Expanded`, THE Widget SHALL fire `WidgetTrigger.OutsideClick` via the `Window.Deactivated` event handler, not a global mouse hook.

---

### Requirement 13: Expanded Widget View

**User Story:** As a user, I want to see the full concept title, explanation, and action buttons when I click the badge, so that I can read and act on the concept.

#### Acceptance Criteria

1. WHILE the Widget is in `WidgetState.Expanded`, THE Widget SHALL display the current Concept's `Title`, `Explanation`, and three action buttons labelled "Read More", "Copy", and "Next".
2. WHEN the user activates the "Copy" button, THE Widget SHALL copy the current Concept's `Explanation` text to the system clipboard.
3. WHEN the user activates the "Next" button in `WidgetState.Expanded`, THE Widget SHALL display the next queued concept if one exists, or display a "No more concepts today" message if the queue is empty.
4. WHILE the Widget is in `WidgetState.Expanded`, THE Widget SHALL fire `WidgetTrigger.Timeout` after a configurable auto-collapse duration if the user performs no interaction within that period.
5. WHILE the Widget is in `WidgetState.Expanded`, THE Widget SHALL display a pin toggle button that fires `WidgetTrigger.Pin` when activated.

---

### Requirement 14: Pinned Widget View

**User Story:** As a user, I want to pin the expanded widget so it stays visible while I work, so that I can reference the concept without repeatedly reopening it.

#### Acceptance Criteria

1. WHILE the Widget is in `WidgetState.Pinned`, THE Widget SHALL display the same content as `WidgetState.Expanded` and SHALL NOT auto-collapse on timeout or focus loss.
2. WHILE the Widget is in `WidgetState.Pinned`, THE Widget SHALL display an active pin toggle button that fires `WidgetTrigger.Unpin` when activated.

---

### Requirement 15: Animation System

**User Story:** As a user, I want smooth, consistent transitions between widget states, so that the widget feels polished rather than jarring.

#### Acceptance Criteria

1. THE AnimationSet SHALL define Fade, Scale, Slide, Expand, and Collapse animations in a single WPF `ResourceDictionary` named `Animations.xaml`.
2. THE AnimationSet SHALL set the duration of every animation to a value between 200 ms and 250 ms inclusive.
3. THE AnimationSet SHALL use a single easing function applied to all animations so that all transitions share the same acceleration curve.
4. WHEN the Widget transitions between any two states, THE Widget SHALL play the corresponding animation from `Animations.xaml` rather than an animation defined inline.

---

### Requirement 16: Theme System

**User Story:** As a developer, I want all colors expressed as named tokens rather than literal hex values, so that themes can be swapped without touching individual controls.

#### Acceptance Criteria

1. THE Widget UI SHALL define color values exclusively through the following named tokens: `Primary`, `Secondary`, `Accent`, `Background`, `Surface`, `Text`, and `Border`.
2. WHEN `AppSettings.Theme` is changed to a valid theme name and the application is restarted, THE Widget SHALL apply the corresponding token values without recompiling.
3. THE Widget UI SHALL ship with a `dark` theme as the default, providing values for all seven color tokens.

---

### Requirement 17: Dependency Injection and Composition Root

**User Story:** As a developer, I want all dependencies wired in a single composition root, so that the object graph is explicit and testable.

#### Acceptance Criteria

1. THE `App.xaml.cs` composition root SHALL register all services using `Microsoft.Extensions.Hosting` and SHALL NOT use the `new` operator to construct any `IConceptProvider`, `IConceptHistoryStore`, `ISettingsStore`, `StateManager`, or `DailyScheduler` outside of the DI container.
2. WHEN the application starts, THE composition root SHALL call `ISettingsStore.LoadAsync` before constructing `OpenAiCompatibleProvider`, so that `ProviderSettings` is sourced from the loaded config and not hardcoded.
3. THE composition root SHALL register `DailyScheduler` as a `Microsoft.Extensions.Hosting.BackgroundService` so that it runs on a managed background thread.

---

### Requirement 18: First-Run Experience

**User Story:** As a new user, I want guided setup when no local model is present, so that I am not left with a silent failure on first launch.

#### Acceptance Criteria

1. WHEN the application starts and `LocalMode` is active and no local inference server is reachable at `ProviderSettings.BaseUrl`, THE Widget SHALL display the FirstRunFlow setup screen instead of the Compact state.
2. WHILE the FirstRunFlow is displayed, THE Widget SHALL present the user with at least two options: download or configure a local model, or switch to CloudMode.
3. WHEN the user completes the FirstRunFlow, THE Widget SHALL persist the resulting `AppSettings` via `SettingsStore.SaveAsync` and SHALL transition to `WidgetState.Compact`.
4. IF the FirstRunFlow is dismissed without completing setup, THEN THE Widget SHALL display the Compact state with the error-state message indicating that no AI provider is configured.

---

### Requirement 19: Security and Input Validation

**User Story:** As a developer, I want all external input validated and sanitized, so that bad data from the config file, history file, or AI provider cannot destabilize the application.

#### Acceptance Criteria

1. WHEN `SettingsStore.LoadAsync` reads `Settings.json`, THE SettingsStore SHALL validate that `Mode` is one of the allowed values (`"local"`, `"cloud"`) and SHALL substitute `AppSettings.Default()` values for any field that fails validation.
2. WHEN `ConceptProvider` receives an AI response, THE ConceptProvider SHALL parse the response body as JSON and SHALL reject it if `title` or `explanation` fields are absent or empty, throwing an `InvalidOperationException`.
3. WHEN the Widget renders a Concept's `Explanation` text in the UI, THE Widget SHALL treat the text as plain text and SHALL NOT evaluate it as HTML, XAML, or any executable markup.
4. THE HistoryStore SHALL read `History.md` as plain text and SHALL NOT execute, evaluate, or interpret any content from the file.

---

### Requirement 20: Installer and Distribution

**User Story:** As a public user, I want a clean install and uninstall experience, so that the app does not leave orphaned files on my machine.

#### Acceptance Criteria

1. THE installer SHALL place all application binaries outside `%AppData%\DesktopConcepts`.
2. WHEN the application is uninstalled, THE uninstaller SHALL remove all files placed by the installer and SHALL NOT remove the `%AppData%\DesktopConcepts` directory automatically (preserving user data).
3. THE installer SHALL NOT require administrator privileges for a per-user installation.
4. WHEN the Widget detects an available update via the RefreshScheduler, THE Widget SHALL notify the user with an in-widget banner and SHALL NOT apply the update silently without user consent.
