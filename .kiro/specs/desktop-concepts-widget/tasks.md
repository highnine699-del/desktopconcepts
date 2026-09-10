# Implementation Tasks — Desktop Concepts Widget

Tasks are ordered by dependency. Each task is atomic — it touches the minimum set of files
required to close the corresponding gap or deliver the corresponding feature.

All tasks marked ✅ are verified complete (build passes, no warnings, behaviour confirmed via
code review). Tasks marked ☐ are pending.

---

## Already Implemented (verified ✅)

- [x] **REQ-1 · Daily concept generation** — `DailyConceptScheduler`, `ConceptGenerationBackgroundService`,
  `last_run.txt` date guard, `ForceRetryAsync` for retry path.

- [x] **REQ-2 · Weekday category rotation** — `WeekdayTopicMap`, `WeekdayTopicMapConverter`,
  default mapping in `AppSettings.Default()`, validation on load, topic grid in SettingsWindow.

- [x] **REQ-3 · Widget state machine** — `WidgetStateManager`, five legal transitions, illegal
  triggers silently ignored, `StateChanged` event, all trigger call sites in `WidgetWindow.xaml.cs`.

- [x] **REQ-4 · AI provider abstraction** — `OpenAiCompatibleProvider`, OpenAI-compatible POST,
  30s settings cache, `Authorization` header per-request, `QuotaExceededException` on 429,
  JSON fence stripping, empty-choices guard.

- [x] **REQ-5 · Settings persistence** — `JsonSettingsStore`, atomic temp-file write,
  `.bak` rename on corrupt file, `AppSettings.Default()` fallback, full round-trip fidelity.

- [x] **REQ-6 · History storage** — `MarkdownHistoryStore`, `AppendSetAsync` (single atomic write
  per set), `GetRecentTitlesAsync`, `GetMostRecentSetAsync` (heading-scan, not stride).

- [x] **REQ-7 · Performance** — async I/O throughout, pooled `HttpClient`, background threads
  for all schedulers, `CompactView` renders in first paint.

- [x] **REQ-8 · Error handling** — `ErrorView`, `QuotaView`, `GenerationFailed` / `QuotaExceeded`
  events, retry path (`ForceRetryAsync` / `RefillIfConnectedAsync`), dispatcher exception handler.

- [x] **REQ-9 · Config format** — indented JSON, case-insensitive read, unknown properties ignored,
  `WeekdayTopicMapConverter`, `DateOnlyConverter`.

- [x] **REQ-10 · Logging** — Serilog rolling daily files, 14-file retention, UTF-8, structured
  template, no prompt/response content at Info or above.

- [x] **REQ-11 · Layered architecture** — project references enforce Domain → Application →
  Infrastructure → UI dependency rules.

- [x] **REQ-12 · Compact view** — always-on-top, `WS_EX_TOOLWINDOW`, `Deactivated` fires
  `OutsideClick`, category dot + slot indicator + title + teaser.

- [x] **REQ-13 · Expanded view** — title, explanation, Read More / Copy / Next buttons,
  30s auto-collapse timer, pin toggle, slot dots.

- [x] **REQ-14 · Pinned view** — same XAML as Expanded, timer stopped, pin glyph yellow
  (`BrushAccent`), rotation skipped while pinned.

- [x] **REQ-16 · Theme system** — all colors as named `Brush*` tokens, no hex literals in
  control XAML, dark theme ships as default.

- [x] **REQ-17 · DI composition root** — hosted-service singleton pattern, pre-`StartAsync`
  event wiring, `Func<SettingsWindow>` factory, `ISettingsStore` pre-loaded before host build.

- [x] **REQ-18 · First-run experience** — `SetupChoiceView` on `IsFirstRun == true`,
  Local/Cloud cards, RAM check, model download flow, `IsFirstRun` cleared on completion.

- [x] **REQ-19 · Security / input validation** — `Mode` validated on load, topic blanks
  substituted, AI response fields validated, explanation rendered as plain `TextBlock` only.

- [x] **REQ-20 · Installer / distribution** — InnoSetup per-user install, uninstall leaves
  `%AppData%\Quire`, consent banner (`UpdateBanner`) before any download, `PerformUpdateAsync`
  called only on "Update Now" click.

- [x] **Audit fixes (all 14 debug-audit tasks)** — stride bug, asset name, stale config.json,
  semantic version compare, version normalisation, `GetParent` P/Invoke, event wire-up race,
  buffer dedup, settings close-after-dispose, `HttpClient` timeout registration, `ForceRetryAsync`,
  tray icon from exe, user-consent update banner, service locator removal.

---

## Pending Tasks

---

- [ ] **Task 1 — REQ-15 gap: Wire AnimScaleIn / AnimScaleOut to state transitions**

  **File:** `src/Quire.UI/Views/WidgetWindow.xaml.cs`

  `AnimScaleIn` and `AnimScaleOut` are defined in `Animations.xaml` and their target property
  paths are set up against the window's root `ScaleTransform` (`CenterX=160`). They are never
  retrieved or played. The Compact↔Expanded transition currently uses `AnimFadeOut`/`AnimFadeIn`
  only, missing the scale effect.

  **Changes:**

  In `ShowExpanded()` — currently plays `AnimSlideInUp` on `ExpandedView`. Add `AnimScaleIn` on
  the root `RenderTransform` immediately after showing the expanded panel:

  ```csharp
  // After: ExpandedView.Visibility = Visibility.Visible;
  var scaleIn = (Storyboard)FindResource("AnimScaleIn");
  scaleIn.Begin(this);
  ```

  In `ShowCompact()` — currently plays `AnimFadeOut` on `ExpandedView` then shows `CompactView`.
  Chain `AnimScaleOut` first, then swap visibility in its `Completed` handler:

  ```csharp
  var scaleOut = (Storyboard)FindResource("AnimScaleOut");
  scaleOut.Completed += (_, _) =>
  {
      ExpandedView.Visibility = Visibility.Collapsed;
      CompactView.Visibility  = Visibility.Visible;
      var fadeIn = (Storyboard)FindResource("AnimFadeIn");
      fadeIn.Begin(CompactView);
  };
  scaleOut.Begin(this);
  ```

  The existing `AnimFadeOut` call on `ExpandedView` should be removed from `ShowCompact` to
  avoid competing animations on the same element.

  Verify: expand → collapse the widget; both transitions show the scale + fade effect with no
  visual glitches and no duplicate animation warnings in the debug output.

---

- [ ] **Task 2 — Create `Quire.Tests` xUnit project**

  **New files:**
  - `tests/Quire.Tests/Quire.Tests.csproj`
  - `tests/Quire.Tests/WidgetStateManagerTests.cs`
  - `tests/Quire.Tests/RefreshSchedulerVersionTests.cs`
  - `tests/Quire.Tests/MarkdownHistoryStoreTests.cs`
  - `tests/Quire.Tests/WeekdayTopicMapTests.cs`
  - `tests/Quire.Tests/DailyConceptSchedulerTests.cs`

  The `tests/Quire.Tests` folder is already declared in `Quire.slnx`. The csproj and source
  files do not yet exist.

  **`Quire.Tests.csproj`** — xUnit 2.9, `net8.0`, references `Quire.Domain`,
  `Quire.Application`, `Quire.Infrastructure`. No WPF reference.

  **`WidgetStateManagerTests.cs`** — covers:
  - All five legal transitions produce the correct `Next` state.
  - All five legal transitions raise `StateChanged` with the correct new state.
  - Every illegal `(state, trigger)` pair leaves `Current` unchanged and does NOT raise
    `StateChanged` (exhaustive: 15 state×trigger combinations minus the 5 legal ones = 10 checks).
  - `Current` initialises to `WidgetState.Compact`.

  **`RefreshSchedulerVersionTests.cs`** — covers `IsNewerVersion` via a testable subclass or
  internal-visible helper:
  - `"1.0.10"` > `"1.0.9"` → true (the ordinal regression case)
  - `"1.0.9"` > `"1.0.10"` → false
  - `"2.0.0"` > `"1.9.9"` → true
  - `"1.0.0"` > `"1.0.0"` → false (equal is not newer)
  - `"1.0.1"` > `"1.0.0"` → true
  - Malformed string pair falls back to ordinal and returns a result without throwing.

  **`MarkdownHistoryStoreTests.cs`** — covers:
  - `AppendSetAsync` then `GetMostRecentSetAsync` round-trips all three concepts with correct
    title, category, and explanation (uses a temp file path via constructor override).
  - `GetMostRecentSetAsync` on a file with two full sets returns only the most-recent set's
    concepts.
  - `GetMostRecentSetAsync` on a file with fewer than 3 concepts for the last date returns what
    was found (no exception) and logs a warning.
  - `GetMostRecentSetAsync` on a missing file returns `null`.
  - `GetRecentTitlesAsync(n)` returns the correct last-n titles in file-appearance order.
  - `GetRecentTitlesAsync(n)` on a file with fewer than `n` entries returns all entries without
    throwing.
  - `GetRecentTitlesAsync` on a missing file returns an empty list.

  **`WeekdayTopicMapTests.cs`** — covers:
  - `CategoryFor` returns the correct string for all seven `DayOfWeek` values using
    `AppSettings.Default().Topics`.
  - `CategoryFor` with a day not in the map throws `InvalidOperationException` containing the
    day name.

  **`DailyConceptSchedulerTests.cs`** — 30-day simulation:
  - Stub `IConceptProvider` returns unique concepts.
  - Stub `IConceptHistoryStore` accumulates appended sets in memory.
  - Run `RunIfDueAsync` for 30 distinct dates.
  - Assert: 90 concepts appended (3×30), no title repeats within the same day's set, all
    `Category` values are non-empty strings.

---

- [ ] **Task 3 — Build verification**

  Run `dotnet build Quire.slnx --configuration Debug` and confirm:
  - 0 errors, 0 warnings.

  Run `dotnet test tests/Quire.Tests/Quire.Tests.csproj --no-build` after building and confirm:
  - All tests pass, 0 failures, 0 skipped.
