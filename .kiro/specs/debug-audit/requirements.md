# Debug Audit — Requirements Document

## Introduction

This document captures every confirmed defect in the DesktopConcepts codebase as formal
requirements. Each requirement states the *correct* behaviour the code must exhibit, paired with
the broken behaviour that was observed. All issues were identified through static analysis and
full source-code audit of the v1.0.8 codebase.

Issues are grouped by severity: Critical → High → Medium → Low → Architectural.

---

## REQ-C1: MarkdownHistoryStore Must Reconstruct Sets Using Heading Index, Not Line Stride

**Broken:** `GetMostRecentSetAsync` locates the first concept heading correctly, then uses
`startIndex + (i * 4)` to find concepts 2 and 3. The actual format written by `AppendSetAsync`
is 5 lines per concept block (blank, heading, category, blank, explanation), so concept 2 lands
on the *explanation* line of concept 1 and concept 3 is beyond the file length. Every app restart
that loads today's concepts from history silently displays wrong or incomplete concepts.

**Required behaviour:**
1. `GetMostRecentSetAsync` SHALL collect all heading lines that match the most-recent date.
2. For each heading line, the method SHALL locate the `*Category: …*` line and explanation by
   scanning forward to the next heading, not by using a hardcoded offset.
3. The method SHALL return a `DailyConceptSet` with all concepts that were successfully parsed;
   if fewer than 3 are found, it SHALL log a warning and return what was found.

---

## REQ-C2: RefreshScheduler Must Search for the Correct Installer Asset Name

**Broken:** `DownloadAndInstallUpdateAsync` searches GitHub Release assets for a file named
`"Setup.exe"`. The actual output file produced by InnoSetup is `"Quire-Setup.exe"`.
The asset lookup always returns `null`, `_logger.LogWarning("no Setup.exe asset found")` fires,
and the update is never installed — even when a newer version exists and the version check passes.

**Required behaviour:**
1. `RefreshScheduler` SHALL search for an asset whose name contains `"Quire-Setup"` and
   ends with `".exe"` (case-insensitive), not an exact match on `"Setup.exe"`.

---

## REQ-C3: config.json Must Not Exist in the UI Project

**Broken:** `config.json` in `Quire.UI/` is never loaded by any code path. It contains
a `CloudProvider` pointing to `api.anthropic.com / claude-haiku-4-5`, which contradicts the actual
runtime default (the Groq Cloudflare Worker proxy). It misleads contributors into believing the
cloud provider is Anthropic. No runtime behaviour depends on it.

**Required behaviour:**
1. `config.json` SHALL be deleted from the UI project.
2. No reference to `api.anthropic.com` SHALL exist anywhere in source files unless intentionally
   supported as a user-configurable override.

---

## REQ-H1: RefreshScheduler Must Use Semantic Version Comparison

**Broken:** `CheckForUpdateAsync` compares versions with `string.Compare(Ordinal)`. For version
strings like `"1.0.9"` vs `"1.0.10"`, ordinal comparison returns `'9' > '1'`, so v1.0.9 is
considered newer than v1.0.10. The auto-updater permanently stops firing for any version whose
minor or patch component exceeds a single digit.

**Required behaviour:**
1. `RefreshScheduler` SHALL parse both the current and latest version strings with
   `System.Version.TryParse` before comparing.
2. If either string fails to parse, the scheduler SHALL fall back to ordinal comparison and log a
   warning.
3. The comparison SHALL use `Version.CompareTo` so that `1.0.10 > 1.0.9` evaluates correctly.

---

## REQ-H2: RefreshScheduler Must Normalise the Current Version to Three Parts

**Broken:** `GetCurrentVersion()` returns the `AssemblyInformationalVersion` attribute value
(`"1.0.8"`) when present, but falls back to `assembly.GetName().Version?.ToString()` which
returns a four-part string (`"1.0.8.0"`). GitHub tags are three-part (`"v1.0.8"`). If the
informational version is absent, the comparison `"1.0.8.0"` vs `"1.0.8"` always returns that
the GitHub version is *newer*, triggering a download on every check cycle.

**Required behaviour:**
1. `GetCurrentVersion()` SHALL prefer `AssemblyInformationalVersionAttribute` (already stripped of
   build metadata like `+abc123` suffixes) over `AssemblyName.Version`.
2. If neither is available, the fallback SHALL be `"0.0.0"` (triggers update rather than crashing).
3. The returned string SHALL never include a fourth version component when the informational version
   is present and is a valid three-part version.

---

## REQ-H3: WidgetWindow Must Use Win32 GetParent, Not GWL_HWNDPARENT

**Broken:** `GetParent(IntPtr hWnd)` is implemented as `GetWindowLong(hWnd, -8)`.
`GWL_HWNDPARENT` (`-8`) returns the *owner* window handle, not the *parent*. For a top-level
window with no explicit owner, this returns `IntPtr.Zero`. `_originalParent` is therefore always
`IntPtr.Zero`, `RestoreNormalParentAsync` calls `SetParent(hwnd, IntPtr.Zero)` which is incidental
correct behaviour but wrong intent. The WorkerW watchdog's `currentParent != workerW` comparison
also misfires because both sides evaluate to `IntPtr.Zero`.

**Required behaviour:**
1. `WidgetWindow` SHALL declare a P/Invoke for the Win32 `GetParent` function from `user32.dll`.
2. The private `GetParent(IntPtr hWnd)` helper SHALL call the real Win32 `GetParent` API, not
   `GetWindowLong`.
3. `_originalParent` SHALL be set to the actual parent handle returned by the Win32 call before
   `SetParent` is invoked for WorkerW reparenting.

---

## REQ-H4: App.xaml.cs Must Prevent Event Drop During Host Start Race

**Broken:** `_host.StartAsync()` launches `ConceptGenerationBackgroundService.ExecuteAsync` on a
background thread. The event wire-up (`bgService.ConceptSetReady += ...`) happens *after*
`StartAsync` returns, on the UI thread. If `ExecuteAsync` runs fast enough (e.g. `ShouldSkipToday`
is true and `GetMostRecentSetAsync` returns immediately), `ConceptSetReady` may fire before the
subscription is attached, silently dropping the first concept delivery.

**Required behaviour:**
1. The `ConceptSetReady`, `GenerationFailed`, and `QuotaExceeded` event subscriptions SHALL be
   attached to `ConceptGenerationBackgroundService` *before* `_host.StartAsync()` is called.
2. The host SHALL be built (`.Build()`) before subscriptions are attached, but
   `.StartAsync()` SHALL be called only after subscriptions are in place.

---

## REQ-M1: CloudPrefetchService Must Use Buffered Titles in the Avoid-List

**Broken:** `FillToTargetAsync` calls `_buffer.PeekDatesAsync` and assigns the result to
`bufferedDates`. This variable is never used. The comment says buffered titles should be added to
the avoid-list, but the implementation is missing. If `FillToTargetAsync` is called while the
buffer still contains sets from a previous partial fill, the new batch may generate concepts with
the same titles as those already in the buffer.

**Required behaviour:**
1. `IConceptBufferStore` SHALL expose a `PeekConceptsAsync` method that returns all concepts
   currently in the buffer without consuming them.
2. `FillToTargetAsync` SHALL add titles from all buffered concepts to the avoid-list before
   generating any new concepts.
3. The `bufferedDates` variable SHALL be removed; date calculations SHALL use `buffer.CountAsync`
   (already done) for the `startDate` offset.

---

## REQ-M2: SettingsWindow Save Must Not Call Close on a Disposed Window

**Broken:** `Save_Click` is `async void`. After `await _settingsStore.SaveAsync(...)` succeeds,
it sets `SaveStatusText.Text`, then does `await Task.Delay(2500)`. If the user closes the window
manually during those 2500 ms (e.g. by clicking the title bar ×), the continuation calls
`Close()` on an already-disposed `Window`, throwing `InvalidOperationException`. The outer
`try/catch` catches it and logs `"Failed to save settings"` — a false error log when settings
were in fact saved successfully.

**Required behaviour:**
1. `Save_Click` SHALL check `IsLoaded` (or a `_closed` flag set in `Closing` event) before
   calling `Close()` after the delay.
2. The success log SHALL be emitted immediately when settings are saved, not deferred until after
   the 2500 ms delay.

---

## REQ-M3: RefreshScheduler Must Configure HttpClient Timeout at Registration Time

**Broken:** `DownloadAndInstallUpdateAsync` calls `_httpFactory.CreateClient()` and then sets
`http.Timeout = TimeSpan.FromMinutes(10)`. `HttpClientFactory` returns a pooled client backed by
a shared `HttpMessageHandler`. Setting `Timeout` after the handler has already processed at least
one request throws `InvalidOperationException: This instance has already started one or more
requests. Properties can only be modified before sending the first request.`

**Required behaviour:**
1. The download `HttpClient` SHALL be registered in `App.xaml.cs` with `AddHttpClient("UpdateDownload")`
   with a 10-minute timeout configured via `services.AddHttpClient("UpdateDownload", c => c.Timeout = ...)`.
2. `DownloadAndInstallUpdateAsync` SHALL call `_httpFactory.CreateClient("UpdateDownload")` and
   SHALL NOT set `Timeout` on the returned instance.

---

## REQ-L1: DailyConceptScheduler Retry Must Not Bypass the Already-Ran-Today Guard

**Broken:** `ErrorRetry_Click` in `WidgetWindow` calls `_dailyScheduler.RunIfDueAsync(today, ...)` 
directly. `RunIfDueAsync` does not check `last_run.txt` — that guard lives in
`ConceptGenerationBackgroundService.ShouldSkipToday`. Clicking Retry on a day where generation
already succeeded appends a *second* `DailyConceptSet` to `History.md`, corrupting the history
and inflating the avoid-list with duplicate entries.

**Required behaviour:**
1. `ConceptGenerationBackgroundService` SHALL expose a `ForceRetryAsync(CancellationToken)` method
   that bypasses the `ShouldSkipToday` guard (for genuine retry scenarios) but is explicitly
   named and documented as a retry-only path.
2. `WidgetWindow.ErrorRetry_Click` SHALL call `ForceRetryAsync` for local mode rather than calling
   `_dailyScheduler.RunIfDueAsync` directly.

---

## REQ-L2: TrayIcon Must Use the Application's Own Icon

**Broken:** `TrayIcon` loads the tray icon via `LoadIcon(IntPtr.Zero, (IntPtr)32512)` which loads
`IDI_APPLICATION` — the generic Windows app icon. The widget appears in the system tray with an
unrecognizable default icon.

**Required behaviour:**
1. `TrayIcon` SHALL load the icon from the application's own `.exe` using
   `ExtractIcon(hInstance, exePath, 0)` or `LoadImage` from the embedded icon resource, rather
   than the generic system `IDI_APPLICATION` icon.

---

## REQ-L3: Updates Must Not Install Silently Without User Consent (Req 20.4)

**Broken:** `RefreshScheduler` downloads and immediately launches the installer with
`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS`. The build
brief Requirement 20.4 states: "The Widget SHALL notify the user with an in-widget banner and
SHALL NOT apply the update silently without user consent."

**Required behaviour:**
1. When a newer version is detected, `RefreshScheduler` SHALL raise an `UpdateAvailable` event
   carrying the new version string and the download URL. It SHALL NOT download or install anything.
2. `WidgetWindow` SHALL subscribe to `UpdateAvailable` and display an in-widget update banner
   showing the new version number with an "Update Now" button and a "Later" dismiss button.
3. Clicking "Update Now" SHALL trigger the download and silent install.
4. Clicking "Later" SHALL dismiss the banner; the check will recur on the next 24 h cycle.

---

## REQ-A1: WidgetWindow Must Not Use IServiceProvider Directly (Service Locator)

**Broken:** `WidgetWindow` receives `IServiceProvider _services` as a constructor parameter and
calls `_services.GetRequiredService<SettingsWindow>()` in two places. This is the service locator
anti-pattern — it hides dependencies and makes the class untestable.

**Required behaviour:**
1. A `Func<SettingsWindow>` factory SHALL be registered in `App.xaml.cs`:
   `services.AddSingleton<Func<SettingsWindow>>(sp => () => sp.GetRequiredService<SettingsWindow>())`.
2. `WidgetWindow` SHALL receive `Func<SettingsWindow> settingsWindowFactory` in its constructor
   instead of `IServiceProvider`.
3. All calls to `_services.GetRequiredService<SettingsWindow>()` SHALL be replaced with
   `_settingsWindowFactory()`.
