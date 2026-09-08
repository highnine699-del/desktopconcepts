# Debug Audit — Implementation Tasks

Tasks are ordered from highest severity to lowest. Each task is atomic — it touches the minimum
set of files required to close the corresponding requirement.

---

- [x] **Task 1 — REQ-C1: Fix MarkdownHistoryStore.GetMostRecentSetAsync stride bug**
  - File: `src/Quire.Infrastructure/Storage/MarkdownHistoryStore.cs`
  - Replace the `startIndex + (i * 4)` stride loop with a date-filtered heading scan.
  - Add `using System.Text.RegularExpressions;` if not already present.

- [x] **Task 2 — REQ-C2: Fix RefreshScheduler asset name lookup**
  - File: `src/Quire.Application/Schedulers/RefreshScheduler.cs`
  - Change `"Setup.exe"` exact match to `Contains("Quire-Setup") && EndsWith(".exe")`.
  - Update the log warning message to match.

- [x] **Task 3 — REQ-C3: Delete stale config.json**
  - File: `src/Quire.UI/config.json`
  - Delete the file entirely. No code changes required.

- [x] **Task 4 — REQ-H1 + REQ-H2: Fix version comparison and normalisation**
  - File: `src/Quire.Application/Schedulers/RefreshScheduler.cs`
  - Rewrite `GetCurrentVersion()` to prefer `InformationalVersion`, strip `+metadata`, trim to 3 parts.
  - Add `IsNewerVersion(string, string)` helper using `System.Version.TryParse`.
  - Replace `string.Compare(Ordinal)` call with `IsNewerVersion`.

- [x] **Task 5 — REQ-H3: Fix GetParent Win32 P/Invoke**
  - File: `src/Quire.UI/Views/WidgetWindow.xaml.cs`
  - Add `[DllImport("user32.dll", EntryPoint = "GetParent")] private static extern IntPtr GetParentWin32(IntPtr hWnd);`
  - Delete the broken `GetParent(IntPtr hWnd)` helper method.
  - Replace both call sites with `GetParentWin32(hwnd)`.

- [x] **Task 6 — REQ-H4: Fix event wire-up race in App.xaml.cs**
  - File: `src/Quire.UI/App.xaml.cs`
  - Move `bgService` resolution and all three event subscriptions to before `_host.StartAsync()`.

- [x] **Task 7 — REQ-M1: Implement buffer deduplication**
  - File 1: `src/Quire.Domain/IConceptBufferStore.cs` — add `PeekConceptsAsync`.
  - File 2: `src/Quire.Infrastructure/Storage/JsonConceptBufferStore.cs` — implement it.
  - File 3: `src/Quire.Application/Schedulers/CloudPrefetchService.cs` — replace
    unused `bufferedDates` block with `PeekConceptsAsync` call and title addition to avoid-list.

- [x] **Task 8 — REQ-M2: Fix SettingsWindow Close-after-dispose**
  - File: `src/Quire.UI/Views/SettingsWindow.xaml.cs`
  - Add `private bool _isClosed;` field.
  - Override `OnClosed` to set `_isClosed = true`.
  - In `Save_Click`: wrap `Close()` with `if (!_isClosed)`.

- [x] **Task 9 — REQ-M3: Fix HttpClient Timeout registration**
  - File 1: `src/Quire.UI/App.xaml.cs` — add `AddHttpClient("UpdateDownload", c => c.Timeout = TimeSpan.FromMinutes(10))`.
  - File 2: `src/Quire.Application/Schedulers/RefreshScheduler.cs` — change
    `CreateClient()` to `CreateClient("UpdateDownload")` and remove the `http.Timeout =` line.

- [x] **Task 10 — REQ-L1: Add ForceRetryAsync, wire to ErrorRetry_Click**
  - File 1: `src/Quire.Application/Schedulers/ConceptGenerationBackgroundService.cs`
    — add public `ForceRetryAsync(CancellationToken)`.
  - File 2: `src/Quire.UI/Views/WidgetWindow.xaml.cs` — add
    `ConceptGenerationBackgroundService _bgService` constructor parameter; replace
    `_dailyScheduler.RunIfDueAsync` call in `ErrorRetry_Click` with `_bgService.ForceRetryAsync`.

- [x] **Task 11 — REQ-L2: Fix TrayIcon to use application icon**
  - File: `src/Quire.UI/TrayIcon.cs`
  - Add `ExtractIcon` P/Invoke.
  - Replace `IDI_APPLICATION` load with `ExtractIcon` from current process exe path.
  - Fall back to `IDI_APPLICATION` if `ExtractIcon` returns `IntPtr.Zero`.

- [x] **Task 12 — REQ-L3: Replace silent install with user-consent banner**
  - File 1: `src/Quire.Application/Schedulers/RefreshScheduler.cs`
    — add `UpdateAvailable` event; rename `DownloadAndInstallUpdateAsync` to `PerformUpdateAsync`
    and make it `public`; replace the call site with the event raise.
  - File 2: `src/Quire.UI/Views/WidgetWindow.xaml` — add `UpdateBanner` panel.
  - File 3: `src/Quire.UI/Views/WidgetWindow.xaml.cs` — add
    `RefreshScheduler _refreshScheduler` constructor parameter; add `OnUpdateAvailable`,
    `UpdateNow_Click`, `UpdateLater_Click` handlers.
  - File 4: `src/Quire.UI/App.xaml.cs` — wire `UpdateAvailable` event.

- [x] **Task 13 — REQ-A1: Remove service locator from WidgetWindow**
  - File 1: `src/Quire.UI/App.xaml.cs` — register `Func<SettingsWindow>` factory.
  - File 2: `src/Quire.UI/Views/WidgetWindow.xaml.cs`
    — replace `IServiceProvider _services` with `Func<SettingsWindow> _settingsWindowFactory`;
    replace both `_services.GetRequiredService<SettingsWindow>()` calls.

- [x] **Task 14 — Build verification**
  - Run `dotnet build` on the solution; confirm zero errors and zero warnings related to the
    changed files.
  - Run `dotnet test` to confirm all existing tests still pass.
