# Debug Audit — Design Document

## Overview

This document describes the exact code changes required to fix every issue identified in the
audit. Each section maps to one requirement from `requirements.md`, names the affected files,
and shows the replacement logic. Changes are designed to be minimal and surgical — no
refactors beyond what is required to fix the bug.

---

## Design for REQ-C1: Fix MarkdownHistoryStore.GetMostRecentSetAsync

**File:** `src/DesktopConcepts.Infrastructure/Storage/MarkdownHistoryStore.cs`

**Root cause:** The method computes concept positions as `startIndex + (i * 4)`. The actual
file format written by `AppendSetAsync` is:

```
[blank line]
## 2025-07-29 [1/3] — Title A        ← line 0 (headingLines[n])
*Category: AI*                         ← line 1
[blank line]                           ← line 2
Explanation text...                    ← line 3
[blank line]
## 2025-07-29 [2/3] — Title B        ← line 5 (headingLines[n+1])
...
```

Concepts 2 and 3 are in `headingLines` as separate entries. The fix is to use the already-built
`headingLines` list filtered by date, rather than a stride calculation.

**Replacement logic for `GetMostRecentSetAsync`:**

```csharp
public async Task<DailyConceptSet?> GetMostRecentSetAsync(CancellationToken cancellationToken)
{
    if (!File.Exists(_path)) return null;

    var lines = await File.ReadAllLinesAsync(_path, Encoding.UTF8, cancellationToken);

    // Collect all heading entries with their line indices
    var headingLines = lines
        .Select((line, index) => (Line: line, Index: index))
        .Where(x => x.Line.StartsWith(HeadingPrefix, StringComparison.Ordinal))
        .ToList();

    if (headingLines.Count == 0) return null;

    // Find the most recent date from the last heading
    var lastHeading = headingLines.Last();
    var dateMatch = Regex.Match(lastHeading.Line, @"## (\d{4}-\d{2}-\d{2})");
    if (!dateMatch.Success) return null;

    var date = DateOnly.ParseExact(dateMatch.Groups[1].Value, "yyyy-MM-dd");
    var datePrefix = $"{HeadingPrefix}{date:yyyy-MM-dd}";

    // Collect all headings for this specific date (should be 3)
    var dateHeadings = headingLines
        .Where(h => h.Line.StartsWith(datePrefix, StringComparison.Ordinal))
        .ToList();

    if (dateHeadings.Count == 0) return null;

    var concepts = new List<Concept>();

    for (int i = 0; i < dateHeadings.Count; i++)
    {
        var headingIndex = dateHeadings[i].Index;
        var headingLine  = dateHeadings[i].Line;

        var title = ExtractTitle(headingLine);
        if (string.IsNullOrWhiteSpace(title)) continue;

        // Category is always immediately after the heading
        var categoryLineIndex = headingIndex + 1;
        var category = "General";
        if (categoryLineIndex < lines.Length)
        {
            var catMatch = Regex.Match(lines[categoryLineIndex], @"\*Category: (.+)\*");
            if (catMatch.Success) category = catMatch.Groups[1].Value;
        }

        // Explanation starts at headingIndex + 3 (skip heading, category, blank line)
        // and extends until the next heading or end of file
        var explanationStart = headingIndex + 3;
        var nextHeadingIndex = i + 1 < dateHeadings.Count
            ? dateHeadings[i + 1].Index        // next concept in this date's group
            : (headingLines.Count > dateHeadings.Count
                ? headingLines.First(h => h.Index > headingIndex
                                       && !h.Line.StartsWith(datePrefix, StringComparison.Ordinal)).Index
                : lines.Length);               // end of file

        // Gather explanation lines — skip blank lines at boundaries but preserve internal content
        var explanationLines = new List<string>();
        for (int j = explanationStart; j < nextHeadingIndex && j < lines.Length; j++)
        {
            if (lines[j].StartsWith(HeadingPrefix, StringComparison.Ordinal)) break;
            explanationLines.Add(lines[j]);
        }

        // Trim leading/trailing blank lines, join preserving internal structure
        var explanation = string.Join("\n", explanationLines).Trim();
        if (string.IsNullOrWhiteSpace(explanation)) explanation = "(no explanation recorded)";

        concepts.Add(new Concept(title, explanation, category, date));
    }

    if (concepts.Count == 0) return null;

    if (concepts.Count < 3)
        _logger.LogWarning(
            "GetMostRecentSetAsync: expected 3 concepts for {Date}, found {Count}.",
            date, concepts.Count);

    return new DailyConceptSet(date, concepts.AsReadOnly());
}
```

The key change: instead of `startIndex + (i * 4)`, we use `dateHeadings[i].Index` which is the
actual line index of each concept's heading as found by the initial scan.

---

## Design for REQ-C2: Fix Asset Name in RefreshScheduler

**File:** `src/DesktopConcepts.Application/Schedulers/RefreshScheduler.cs`

**Root cause:** Exact-match `"Setup.exe"` never matches `"DesktopConcepts-Setup.exe"`.

**One-line fix in `DownloadAndInstallUpdateAsync`:**

```csharp
// Before (broken):
var setupAsset = release.Assets?.FirstOrDefault(a =>
    a.Name?.Equals("Setup.exe", StringComparison.OrdinalIgnoreCase) == true);

// After (fixed):
var setupAsset = release.Assets?.FirstOrDefault(a =>
    a.Name?.Contains("DesktopConcepts-Setup", StringComparison.OrdinalIgnoreCase) == true
    && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
```

Also update the log message to reflect the correct expected name:

```csharp
_logger.LogWarning("Update available but no DesktopConcepts-Setup*.exe asset found in release.");
```

---

## Design for REQ-C3: Delete config.json

**File:** `src/DesktopConcepts.UI/config.json`

Delete the file. It is never loaded by any code path. No other changes required.

---

## Design for REQ-H1 + REQ-H2: Fix Version Comparison in RefreshScheduler

**File:** `src/DesktopConcepts.Application/Schedulers/RefreshScheduler.cs`

Two bugs fixed together since they are in the same method.

**Fix `GetCurrentVersion()`** — prefer `InformationalVersion`, strip build metadata suffix
(`+abc123` is valid in informational versions), ensure never returns 4-part string:

```csharp
private static string GetCurrentVersion()
{
    var assembly = Assembly.GetEntryAssembly();
    if (assembly is null) return "0.0.0";

    // Prefer InformationalVersion (e.g. "1.0.8") over AssemblyName.Version ("1.0.8.0")
    var informational = assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    if (!string.IsNullOrEmpty(informational))
    {
        // Strip build metadata suffix (e.g. "1.0.8+abc123" → "1.0.8")
        var plusIdx = informational.IndexOf('+');
        return plusIdx >= 0 ? informational[..plusIdx] : informational;
    }

    // Fallback: use assembly version but trim to 3 parts (strip revision)
    var v = assembly.GetName().Version;
    return v is not null ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
}
```

**Fix version comparison** — use `System.Version.TryParse`:

```csharp
// Before (broken):
if (string.Compare(latest, currentVersion, StringComparison.Ordinal) > 0)

// After (fixed):
if (IsNewerVersion(latest, currentVersion))
```

Add helper method:

```csharp
private bool IsNewerVersion(string latest, string current)
{
    if (Version.TryParse(latest, out var latestVer)
     && Version.TryParse(current, out var currentVer))
    {
        return latestVer > currentVer;
    }

    // Fallback: ordinal (only hit if version strings are malformed)
    _logger.LogWarning(
        "Could not parse version for comparison: latest='{Latest}', current='{Current}'. Using ordinal.",
        latest, current);
    return string.Compare(latest, current, StringComparison.Ordinal) > 0;
}
```

---

## Design for REQ-H3: Fix GetParent Win32 P/Invoke in WidgetWindow

**File:** `src/DesktopConcepts.UI/Views/WidgetWindow.xaml.cs`

**Root cause:** Custom `GetParent` calls `GetWindowLong(hWnd, -8)` (owner handle) instead of
the Win32 `GetParent` API (parent handle).

**Add proper P/Invoke declaration** (alongside the other `DllImport` declarations):

```csharp
[DllImport("user32.dll", SetLastError = true, EntryPoint = "GetParent")]
private static extern IntPtr GetParentWin32(IntPtr hWnd);
```

**Remove the broken helper** and replace its two call sites:

```csharp
// Remove this entirely:
private IntPtr GetParent(IntPtr hWnd)
{
    return GetWindowLong(hWnd, -8); // WRONG
}
```

Replace `GetParent(hwnd)` call sites with `GetParentWin32(hwnd)`:

- In `ApplyWorkerWModeAsync`: `_originalParent = GetParentWin32(hwnd);`
- In `StartWorkerWWatchdog` tick: `var currentParent = GetParentWin32(hwnd);`

---

## Design for REQ-H4: Fix Event Wire-Up Race in App.xaml.cs

**File:** `src/DesktopConcepts.UI/App.xaml.cs`

**Root cause:** Events are subscribed after `_host.StartAsync()`, which starts
`ConceptGenerationBackgroundService` on a background thread.

**Fix:** Build the host, resolve `bgService` and `window`, wire events, *then* call `StartAsync`:

```csharp
_host = Host.CreateDefaultBuilder()
    // ... ConfigureServices unchanged ...
    .Build();

// Resolve services BEFORE starting — no background threads running yet
var bgService = _host.Services
    .GetServices<IHostedService>()
    .OfType<ConceptGenerationBackgroundService>()
    .First();

var window = _host.Services.GetRequiredService<WidgetWindow>();

// Wire events BEFORE StartAsync so no delivery can be missed
bgService.ConceptSetReady  += set => window.OnConceptSetReady(set);
bgService.GenerationFailed += ex  => window.OnGenerationFailed(ex);
bgService.QuotaExceeded    += ()  => window.OnQuotaExceeded();

// Now start — background services begin executing with events already wired
await _host.StartAsync();
```

---

## Design for REQ-M1: Implement Buffer Deduplication in CloudPrefetchService

**Files:**
- `src/DesktopConcepts.Domain/IConceptBufferStore.cs` — add `PeekConceptsAsync`
- `src/DesktopConcepts.Infrastructure/Storage/JsonConceptBufferStore.cs` — implement it
- `src/DesktopConcepts.Application/Schedulers/CloudPrefetchService.cs` — use it

**Step 1 — Add to `IConceptBufferStore`:**

```csharp
/// <summary>
/// Returns all concepts currently in the buffer without consuming them.
/// Used at fetch time to build the deduplication avoid-list.
/// </summary>
Task<IReadOnlyList<Concept>> PeekConceptsAsync(CancellationToken cancellationToken);
```

**Step 2 — Implement in `JsonConceptBufferStore`:**

```csharp
public async Task<IReadOnlyList<Concept>> PeekConceptsAsync(CancellationToken cancellationToken)
{
    await _lock.WaitAsync(cancellationToken);
    try
    {
        var buffer = await LoadBufferAsync(cancellationToken);
        return buffer.Entries
            .SelectMany(e => e.Concepts.Select(c => c.ToDomain()))
            .ToList()
            .AsReadOnly();
    }
    finally { _lock.Release(); }
}
```

**Step 3 — Use in `CloudPrefetchService.FillToTargetAsync`:**

Replace the unused `bufferedDates` block:

```csharp
// Before (broken — bufferedDates never used):
var bufferedDates = await _buffer.PeekDatesAsync(cancellationToken);
// (titles in the buffer are already in history once appended; this covers
//  the window between buffer-fill and the daily History.md write)

// After (fixed — add buffered titles to avoid-list):
var bufferedConcepts = await _buffer.PeekConceptsAsync(cancellationToken);
foreach (var bc in bufferedConcepts)
    avoidList.Add(bc.Title);

_logger.LogDebug(
    "Avoid-list: {HistoryCount} from history + {BufferCount} from buffer = {Total} total.",
    avoidList.Count - bufferedConcepts.Count, bufferedConcepts.Count, avoidList.Count);
```

Also update `startDate` calculation — it already uses `current` (from `CountAsync`) which is
correct, so no change needed there.

---

## Design for REQ-M2: Fix SettingsWindow Save_Click Close-After-Dispose

**File:** `src/DesktopConcepts.UI/Views/SettingsWindow.xaml.cs`

**Fix:** Add a `_isClosed` flag, set it in `OnClosed`, check it before `Close()`:

```csharp
private bool _isClosed;

protected override void OnClosed(EventArgs e)
{
    _isClosed = true;
    base.OnClosed(e);
}
```

In `Save_Click`, replace:
```csharp
await Task.Delay(2500);
Close();
```
With:
```csharp
await Task.Delay(2500);
if (!_isClosed) Close();
```

Also move the success log before the delay so it is never suppressed:

```csharp
_logger.LogInformation("Settings saved. Mode={Mode}, Advanced={HasAdvanced}", ...);
SaveStatusText.Text       = "✓ Settings saved. Restart the app to apply AI mode changes.";
SaveStatusText.Visibility = Visibility.Visible;

await Task.Delay(2500);
if (!_isClosed) Close();
```

---

## Design for REQ-M3: Fix HttpClient Timeout Registration

**Files:**
- `src/DesktopConcepts.UI/App.xaml.cs` — register named client with timeout
- `src/DesktopConcepts.Application/Schedulers/RefreshScheduler.cs` — use named client, remove Timeout set

**In `App.xaml.cs` `ConfigureServices`:**

```csharp
// Register a separate named client for update downloads with a long timeout
services.AddHttpClient("UpdateDownload", client =>
{
    client.Timeout = TimeSpan.FromMinutes(10);
});
```

**In `RefreshScheduler.DownloadAndInstallUpdateAsync`:**

```csharp
// Before (broken):
using var http = _httpFactory.CreateClient();
http.Timeout = TimeSpan.FromMinutes(10); // throws if handler already used

// After (fixed):
using var http = _httpFactory.CreateClient("UpdateDownload");
// Timeout is pre-configured at registration — do NOT set it here
```

---

## Design for REQ-L1: Add ForceRetryAsync to ConceptGenerationBackgroundService

**Files:**
- `src/DesktopConcepts.Application/Schedulers/ConceptGenerationBackgroundService.cs`
- `src/DesktopConcepts.UI/Views/WidgetWindow.xaml.cs`

**Add to `ConceptGenerationBackgroundService`:**

```csharp
/// <summary>
/// Explicitly reruns the daily generation, bypassing the ShouldSkipToday guard.
/// Call this only from error-retry UI paths — not from the normal daily schedule.
/// </summary>
public async Task ForceRetryAsync(CancellationToken cancellationToken)
{
    var settings = await _settings.LoadAsync(cancellationToken);
    var today    = DateOnly.FromDateTime(DateTime.Now);

    _logger.LogInformation("ForceRetry: re-running generation for {Today} on user request.", today);

    if (settings.Mode == "cloud")
        await RunCloudDayAsync(today, cancellationToken);
    else
        await _scheduler.RunIfDueAsync(today, cancellationToken);
}
```

**In `WidgetWindow`:**

- Replace `IServiceProvider _services` with `Func<SettingsWindow> _settingsWindowFactory`
  (see REQ-A1 design) AND add `ConceptGenerationBackgroundService _bgService` as a constructor
  parameter so `ErrorRetry_Click` can call `ForceRetryAsync`.

  Actually, to avoid touching the constructor too much, `WidgetWindow` already has
  `_dailyScheduler` and `_prefetchService`. The cleanest fix is to inject
  `ConceptGenerationBackgroundService` directly:

```csharp
// Constructor parameter added:
private readonly ConceptGenerationBackgroundService _bgService;

// ErrorRetry_Click local mode path, replace:
await _dailyScheduler.RunIfDueAsync(DateOnly.FromDateTime(DateTime.Now), CancellationToken.None);
// With:
await _bgService.ForceRetryAsync(CancellationToken.None);
```

---

## Design for REQ-L2: Fix TrayIcon to Use Application Icon

**File:** `src/DesktopConcepts.UI/TrayIcon.cs`

**Fix:** Use `ExtractIconEx` / `LoadImage` from the running executable instead of
`IDI_APPLICATION`. The simplest reliable approach on Windows is `ExtractIcon`:

```csharp
// Add P/Invoke:
[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
private static extern IntPtr ExtractIcon(IntPtr hInst, string pszExeFileName, int nIconIndex);

// In constructor, replace:
hIcon = LoadIcon(IntPtr.Zero, (IntPtr)32512), // IDI_APPLICATION (generic)

// With:
hIcon = ExtractIcon(IntPtr.Zero, System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName, 0),
```

If `ExtractIcon` returns `IntPtr.Zero` (no icon embedded), fall back to `IDI_APPLICATION` so
the tray icon still appears rather than failing silently.

---

## Design for REQ-L3: Update Flow — Event + Banner Instead of Silent Install

**Files:**
- `src/DesktopConcepts.Application/Schedulers/RefreshScheduler.cs` — raise event, don't install
- `src/DesktopConcepts.UI/Views/WidgetWindow.xaml` — add UpdateBanner panel
- `src/DesktopConcepts.UI/Views/WidgetWindow.xaml.cs` — subscribe to event, show banner
- `src/DesktopConcepts.UI/App.xaml.cs` — wire UpdateAvailable event

**`RefreshScheduler` changes:**

1. Add event and a public method to trigger the install:

```csharp
/// <summary>Raised when a newer version is available. Carries version string and download URL.</summary>
public event Action<string, string>? UpdateAvailable;  // (version, downloadUrl)
```

2. Replace `DownloadAndInstallUpdateAsync` call with event raise:

```csharp
// Before:
await DownloadAndInstallUpdateAsync(release, cancellationToken);

// After:
var setupAsset = release.Assets?.FirstOrDefault(a =>
    a.Name?.Contains("DesktopConcepts-Setup", StringComparison.OrdinalIgnoreCase) == true
    && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

if (setupAsset?.DownloadUrl is not null)
{
    _logger.LogInformation(
        "Update v{Latest} available — notifying UI for user consent.", latest);
    UpdateAvailable?.Invoke(latest, setupAsset.DownloadUrl);
}
else
{
    _logger.LogWarning("Update available but no DesktopConcepts-Setup*.exe asset found.");
}
```

3. Rename `DownloadAndInstallUpdateAsync` to `PerformUpdateAsync(string downloadUrl, CancellationToken)` 
   and make it `public` so `WidgetWindow` can call it after user consent.

**`WidgetWindow.xaml` — add UpdateBanner** (inside the root grid, above other views):

```xml
<!-- Update available banner (shown when RefreshScheduler detects a new version) -->
<Border x:Name="UpdateBanner" Visibility="Collapsed"
        Background="{StaticResource BrushAccent}" CornerRadius="6"
        Margin="8,8,8,0" Padding="10,8" HorizontalAlignment="Stretch">
    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="*"/>
            <ColumnDefinition Width="Auto"/>
            <ColumnDefinition Width="Auto"/>
        </Grid.ColumnDefinitions>
        <TextBlock x:Name="UpdateBannerText" Grid.Column="0"
                   Text="A new version is available."
                   Foreground="{StaticResource BrushBackground}"
                   VerticalAlignment="Center" FontSize="11" TextWrapping="Wrap"/>
        <Button x:Name="UpdateNowButton" Grid.Column="1"
                Content="Update Now" Margin="6,0,4,0"
                Click="UpdateNow_Click"
                Style="{StaticResource ButtonPrimary}"/>
        <Button Grid.Column="2" Content="Later"
                Click="UpdateLater_Click"
                Style="{StaticResource ButtonBase}"/>
    </Grid>
</Border>
```

**`WidgetWindow.xaml.cs` — handle UpdateAvailable:**

```csharp
private string? _pendingUpdateUrl;

public void OnUpdateAvailable(string version, string downloadUrl)
{
    _pendingUpdateUrl = downloadUrl;
    Dispatcher.Invoke(() =>
    {
        UpdateBannerText.Text    = $"Version {version} is available.";
        UpdateBanner.Visibility  = Visibility.Visible;
    });
}

private void UpdateNow_Click(object sender, RoutedEventArgs e)
{
    UpdateBanner.Visibility = Visibility.Collapsed;
    if (_pendingUpdateUrl is null) return;
    var url = _pendingUpdateUrl;
    _pendingUpdateUrl = null;
    _ = Task.Run(() => _refreshScheduler.PerformUpdateAsync(url, CancellationToken.None));
}

private void UpdateLater_Click(object sender, RoutedEventArgs e)
{
    UpdateBanner.Visibility = Visibility.Collapsed;
    _logger.LogInformation("User deferred update.");
}
```

`WidgetWindow` constructor needs `RefreshScheduler _refreshScheduler` added as a parameter, and
`App.xaml.cs` must wire `UpdateAvailable`:

```csharp
var refreshScheduler = _host.Services
    .GetServices<IHostedService>()
    .OfType<RefreshScheduler>()
    .First();
refreshScheduler.UpdateAvailable += (ver, url) => window.OnUpdateAvailable(ver, url);
```

---

## Design for REQ-A1: Remove Service Locator from WidgetWindow

**Files:**
- `src/DesktopConcepts.UI/App.xaml.cs` — register factory
- `src/DesktopConcepts.UI/Views/WidgetWindow.xaml.cs` — replace `IServiceProvider` with factory

**In `App.xaml.cs` `ConfigureServices`:**

```csharp
// Register a factory so WidgetWindow gets new SettingsWindow instances without IServiceProvider
services.AddSingleton<Func<SettingsWindow>>(sp => () => sp.GetRequiredService<SettingsWindow>());
```

**In `WidgetWindow` constructor, replace:**

```csharp
// Before:
private readonly IServiceProvider _services;

public WidgetWindow(
    ...
    IServiceProvider      services,
    ...)
{
    ...
    _services = services;
    ...
}
```

**After:**

```csharp
private readonly Func<SettingsWindow> _settingsWindowFactory;

public WidgetWindow(
    ...
    Func<SettingsWindow>  settingsWindowFactory,
    ...)
{
    ...
    _settingsWindowFactory = settingsWindowFactory;
    ...
}
```

**Replace all `_services.GetRequiredService<SettingsWindow>()` calls with `_settingsWindowFactory()`:**

- `OpenSettings_Click`: `var settingsWin = _settingsWindowFactory();`
- `SkipToCloud_Click`: `var settingsWin = _settingsWindowFactory();`

---

## Cross-Cutting: Constructor Parameter Changes Summary

`WidgetWindow` constructor will gain two parameters and lose one:

| Change | Before | After |
|--------|--------|-------|
| Remove | `IServiceProvider services` | — |
| Add | — | `Func<SettingsWindow> settingsWindowFactory` |
| Add | — | `ConceptGenerationBackgroundService bgService` |
| Add | — | `RefreshScheduler refreshScheduler` |

`App.xaml.cs` must register:
```csharp
services.AddSingleton<Func<SettingsWindow>>(sp => () => sp.GetRequiredService<SettingsWindow>());
```
And wire two more events after build, before `StartAsync`.

All other DI registrations remain unchanged — DI auto-resolves the new constructor parameters.
