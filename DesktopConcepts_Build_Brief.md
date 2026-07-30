# DesktopConcepts — AI Build Brief
Hand this file directly to your coding agent. It contains the exact structure, interfaces, and starter
implementations — the goal is that building becomes mechanical, not exploratory.

Target: .NET 8, WPF, `Microsoft.Extensions.Hosting` for DI + background services, `System.Text.Json`.

---

## 0. Non-negotiables (check against these, not vibes)

| Metric | Target |
|---|---|
| Startup | < 300 ms |
| Idle memory | < 120 MB |
| Idle CPU | ~0% |
| CPU while generating | < 15% |
| Internet required | Only for cloud mode / update checks |

State machine — the only legal transitions, nothing implicit:
```
Compact  --click-->        Expanded
Expanded --outside click--> Compact
Expanded --timeout-->      Compact
Expanded --pin-->          Pinned
Pinned   --unpin-->        Compact
```

---

## 1. Build order (do this in this order, don't skip ahead)

1. Solution + project scaffolding (§2)
2. Domain layer — models & interfaces only, zero dependencies (§3)
3. Infrastructure — config + storage + local AI provider (§4)
4. Application — state machine + schedulers (§5)
5. Composition root / DI wiring (§6)
6. UI — compact/expanded/pinned views (§7)
7. Run the acceptance checklist (§8) before calling anything "done"

Each phase should compile and have its own unit tests passing before moving to the next. Don't write UI
against a stubbed domain — domain and infrastructure are real by the time UI starts.

---

## 2. Solution structure

```
DesktopConcepts.sln
├── src/
│   ├── DesktopConcepts.Domain/          (no NuGet deps beyond BCL)
│   ├── DesktopConcepts.Application/     (depends on Domain only)
│   ├── DesktopConcepts.Infrastructure/  (depends on Domain)
│   └── DesktopConcepts.UI/              (WPF, depends on Application + Infrastructure for DI wiring only)
└── tests/
    └── DesktopConcepts.Tests/
```

`DesktopConcepts.Domain.csproj` — target `net8.0`, no WPF reference. This project must never reference
`System.Windows` or `System.Net.Http`. If it needs to, something is wired wrong.

`DesktopConcepts.UI.csproj` — `<UseWPF>true</UseWPF>`, `<OutputType>WinExe</OutputType>`.

---

## 3. Domain layer (pure logic, no WPF/HTTP/JSON here)

```csharp
// Domain/Concept.cs
namespace DesktopConcepts.Domain;

public sealed record Concept(
    string Title,
    string Explanation,
    string Category,
    DateOnly GeneratedOn);
```

```csharp
// Domain/DailyConceptSet.cs
// Container for the three concepts generated each calendar day.
// The Rotation Scheduler cycles through Concepts[0→1→2→0] every 7 minutes.
namespace DesktopConcepts.Domain;

public sealed record DailyConceptSet(
    DateOnly Date,
    IReadOnlyList<Concept> Concepts)
{
    public int Count => Concepts.Count; // always 3

    /// <summary>Returns the concept at the given rotation index (wraps automatically).</summary>
    public Concept GetByIndex(int index) => Concepts[index % Concepts.Count];
}
```

```csharp
// Domain/Weekday.cs — maps System.DayOfWeek to a configured category, kept separate from config parsing
namespace DesktopConcepts.Domain;

public sealed record WeekdayTopicMap(IReadOnlyDictionary<DayOfWeek, string> Categories)
{
    public string CategoryFor(DayOfWeek day) =>
        Categories.TryGetValue(day, out var category)
            ? category
            : throw new InvalidOperationException($"No category configured for {day}");
}
```

```csharp
// Domain/WidgetState.cs
namespace DesktopConcepts.Domain;

public enum WidgetState { Compact, Expanded, Pinned }

public enum WidgetTrigger { Click, OutsideClick, Timeout, Pin, Unpin }
```

```csharp
// Domain/IConceptProvider.cs — the single most important interface in the app.
// Everything AI-related goes through this. Nothing else may call an HTTP client or SDK directly.
//
// Contract: called exactly 3 times per calendar day to populate a DailyConceptSet.
// Each call produces one distinct Concept. The caller passes the accumulating avoid-list
// so each of the three concepts within a day is also distinct from the others.
namespace DesktopConcepts.Domain;

public interface IConceptProvider
{
    Task<Concept> GenerateConceptAsync(
        string category,
        IReadOnlyCollection<string> recentTitlesToAvoid,
        CancellationToken cancellationToken);
}
```

```csharp
// Domain/IConceptHistoryStore.cs
namespace DesktopConcepts.Domain;

public interface IConceptHistoryStore
{
    /// <summary>Appends all three concepts in a DailyConceptSet in a single operation.</summary>
    Task AppendSetAsync(DailyConceptSet conceptSet, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetRecentTitlesAsync(int count, CancellationToken cancellationToken);
}
```

```csharp
// Domain/ISettingsStore.cs
namespace DesktopConcepts.Domain;

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);
}

public sealed record AppSettings(
    string Mode,               // "local" | "cloud"
    string Theme,
    bool IsFirstRun,           // true until setup-time choice screen is completed
    ProviderSettings Provider,
    ProviderSettings CloudProvider,
    WeekdayTopicMap Topics)
{
    public static AppSettings Default() => new(
        Mode: "local",
        Theme: "dark",
        IsFirstRun: true,
        Provider: new ProviderSettings("http://localhost:1234/v1", "phi-3-mini", ApiKey: null),
        // Default cloud model: Claude Haiku 4.5 — cheap enough at this app's volume
        // (a few short generations/day) that cost is not a real constraint.
        CloudProvider: new ProviderSettings("https://api.anthropic.com/v1", "claude-haiku-4-5", ApiKey: null),
        Topics: new WeekdayTopicMap(new Dictionary<DayOfWeek, string>
        {
            [DayOfWeek.Monday] = "Programming",
            [DayOfWeek.Tuesday] = "Cybersecurity",
            [DayOfWeek.Wednesday] = "Networking",
            [DayOfWeek.Thursday] = "AI",
            [DayOfWeek.Friday] = "Operating Systems",
            [DayOfWeek.Saturday] = "Mathematics",
            [DayOfWeek.Sunday] = "Computer Engineering",
        }));
}

public sealed record ProviderSettings(string BaseUrl, string Model, string? ApiKey);
```

```csharp
// Domain/IConceptBufferStore.cs — cloud-mode prefetch queue (separate from History.md)
namespace DesktopConcepts.Domain;

public interface IConceptBufferStore
{
    Task AddRangeAsync(IReadOnlyList<DailyConceptSet> sets, CancellationToken cancellationToken);
    Task<DailyConceptSet?> TryTakeNextAsync(CancellationToken cancellationToken);
    Task<int> CountAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<DateOnly>> PeekDatesAsync(CancellationToken cancellationToken);
}
```

**Validate on load, never crash on bad config:** if `Settings.json` fails to parse, `ISettingsStore.LoadAsync`
catches the exception, logs it, and returns `AppSettings.Default()`. Never let a malformed file take the app down.

---

## 4. Infrastructure layer

### 4.1 Generic local/cloud AI provider (one implementation, not tied to any specific tool)

Both LM Studio and Ollama (in OpenAI-compat mode) — and any cloud provider that follows the same shape —
speak the same `/v1/chat/completions` request/response format. One implementation covers all of them;
only `BaseUrl`/`Model`/`ApiKey` change.

```csharp
// Infrastructure/AI/OpenAiCompatibleProvider.cs
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DesktopConcepts.Domain;

namespace DesktopConcepts.Infrastructure.AI;

public sealed class OpenAiCompatibleProvider : IConceptProvider
{
    private readonly HttpClient _http;
    private readonly ProviderSettings _settings;

    public OpenAiCompatibleProvider(HttpClient http, ProviderSettings settings)
    {
        _http = http;
        _settings = settings;
        if (!string.IsNullOrEmpty(settings.ApiKey))
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.ApiKey);
    }

    public async Task<Concept> GenerateConceptAsync(
        string category,
        IReadOnlyCollection<string> recentTitlesToAvoid,
        CancellationToken cancellationToken)
    {
        var avoidList = recentTitlesToAvoid.Count > 0
            ? $" Avoid repeating any of these previous titles: {string.Join(", ", recentTitlesToAvoid)}."
            : string.Empty;

        var prompt = $"Explain one specific, highly technical {category} concept in 5-8 sentences, " +
                     $"in a way a curious beginner can follow. Respond as JSON: " +
                     $"{{\"title\": \"...\", \"explanation\": \"...\"}}.{avoidList}";

        var request = new ChatRequest(
            Model: _settings.Model,
            Messages: [new ChatMessage("user", prompt)],
            Temperature: 0.8);

        var response = await _http.PostAsJsonAsync(
            $"{_settings.BaseUrl.TrimEnd('/')}/chat/completions", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Empty response from AI provider.");

        var raw = payload.Choices[0].Message.Content;
        var parsed = System.Text.Json.JsonSerializer.Deserialize<ConceptPayload>(raw)
            ?? throw new InvalidOperationException("AI response was not valid JSON.");

        return new Concept(parsed.Title, parsed.Explanation, category, DateOnly.FromDateTime(DateTime.Now));
    }

    private sealed record ChatRequest(string Model, ChatMessage[] Messages, double Temperature);
    private sealed record ChatMessage(string Role, string Content);
    private sealed record ChatResponse([property: JsonPropertyName("choices")] ChatChoice[] Choices);
    private sealed record ChatChoice(ChatMessage Message);
    private sealed record ConceptPayload(string Title, string Explanation);
}
```

**Error handling contract:** this method throws on any failure (network down, bad JSON, non-2xx). The
caller (Application layer) is responsible for catching, logging, and surfacing the Retry/Settings UI —
this class stays dumb and honest about failures.

### 4.2 Settings store

```csharp
// Infrastructure/Storage/JsonSettingsStore.cs
using System.Text.Json;
using DesktopConcepts.Domain;

namespace DesktopConcepts.Infrastructure.Storage;

public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public JsonSettingsStore(string? overridePath = null)
    {
        _path = overridePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopConcepts", "Settings.json");
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_path)) return AppSettings.Default();
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, cancellationToken:cancellationToken)
                   ?? AppSettings.Default();
        }
        catch
        {
            // Corrupted or hand-edited config must never crash the app — fall back silently to defaults.
            return AppSettings.Default();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, settings, Options, cancellationToken);
    }
}
```

### 4.3 History store (append-only Markdown, plain-text dedupe list)

```csharp
// Infrastructure/Storage/MarkdownHistoryStore.cs
using DesktopConcepts.Domain;

namespace DesktopConcepts.Infrastructure.Storage;

public sealed class MarkdownHistoryStore : IConceptHistoryStore
{
    private readonly string _path;

    public MarkdownHistoryStore(string? overridePath = null)
    {
        _path = overridePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopConcepts", "History.md");
    }

    /// <summary>
    /// Appends all three concepts in the set atomically (single file open → write → close).
    /// Each concept gets its own heading, tagged with its index in the set (1/3, 2/3, 3/3).
    /// </summary>
    public async Task AppendSetAsync(DailyConceptSet conceptSet, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < conceptSet.Concepts.Count; i++)
        {
            var concept = conceptSet.Concepts[i];
            sb.AppendLine();
            sb.AppendLine($"## {concept.GeneratedOn:yyyy-MM-dd} [{i + 1}/3] — {concept.Title}");
            sb.AppendLine($"*Category: {concept.Category}*");
            sb.AppendLine();
            sb.AppendLine(concept.Explanation);
        }
        await File.AppendAllTextAsync(_path, sb.ToString(), cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetRecentTitlesAsync(int count, CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return [];
        var lines = await File.ReadAllLinesAsync(_path, cancellationToken);
        return lines
            .Where(l => l.StartsWith("## "))
            .Select(l => l[(l.IndexOf('—') + 1)..].Trim())
            .TakeLast(count)
            .ToList();
    }
}
```

---

## 5. Application layer — state machine & schedulers

```csharp
// Application/WidgetStateManager.cs
using DesktopConcepts.Domain;

namespace DesktopConcepts.Application;

public sealed class WidgetStateManager
{
    public WidgetState Current { get; private set; } = WidgetState.Compact;
    public event Action<WidgetState>? StateChanged;

    public void Fire(WidgetTrigger trigger)
    {
        var next = (Current, trigger) switch
        {
            (WidgetState.Compact, WidgetTrigger.Click) => WidgetState.Expanded,
            (WidgetState.Expanded, WidgetTrigger.OutsideClick) => WidgetState.Compact,
            (WidgetState.Expanded, WidgetTrigger.Timeout) => WidgetState.Compact,
            (WidgetState.Expanded, WidgetTrigger.Pin) => WidgetState.Pinned,
            (WidgetState.Pinned, WidgetTrigger.Unpin) => WidgetState.Compact,
            _ => Current // illegal transition for current state — ignored, not thrown
        };

        if (next == Current) return;
        Current = next;
        StateChanged?.Invoke(Current);
    }
}
```

```csharp
// Application/DailyConceptScheduler.cs
// One job: generate today's DailyConceptSet (exactly 3 concepts) once per calendar day.
// Calls IConceptProvider 3 times, accumulating the avoid-list between calls so all three
// concepts within the same day are also distinct from each other.
using DesktopConcepts.Domain;

namespace DesktopConcepts.Application;

public sealed class DailyConceptScheduler
{
    private readonly IConceptProvider _provider;
    private readonly IConceptHistoryStore _history;
    private readonly ISettingsStore _settings;

    public event Action<DailyConceptSet>? ConceptSetGenerated;
    public event Action<Exception>? GenerationFailed;

    public DailyConceptScheduler(
        IConceptProvider provider,
        IConceptHistoryStore history,
        ISettingsStore settings)
    {
        _provider = provider;
        _history = history;
        _settings = settings;
    }

    public async Task RunIfDueAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var settings = await _settings.LoadAsync(cancellationToken);
        var category = settings.Topics.CategoryFor(today.DayOfWeek);

        // Seed the avoid-list with recent history; grow it within this call to prevent
        // intra-day duplicates across the 3 concepts.
        var avoidList = new List<string>(
            await _history.GetRecentTitlesAsync(30, cancellationToken));

        try
        {
            var concepts = new List<Concept>(3);
            for (int i = 0; i < 3; i++)
            {
                var concept = await _provider.GenerateConceptAsync(
                    category, avoidList, cancellationToken);
                concepts.Add(concept);
                avoidList.Add(concept.Title); // avoid repeating within the same day's set
            }

            var set = new DailyConceptSet(today, concepts.AsReadOnly());
            await _history.AppendSetAsync(set, cancellationToken);
            ConceptSetGenerated?.Invoke(set);
        }
        catch (Exception ex)
        {
            // Caller surfaces Retry / Open AI Settings UI — never crash here.
            GenerationFailed?.Invoke(ex);
        }
    }
}
```

```csharp
// Application/RotationScheduler.cs
// One job: advance the active concept index (0→1→2→0) across the current DailyConceptSet
// every 7 minutes. Pausable when the widget is Pinned.
using DesktopConcepts.Domain;

namespace DesktopConcepts.Application;

public sealed class RotationScheduler : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(7);

    private readonly WidgetStateManager _stateManager;
    private System.Threading.Timer? _timer;
    private DailyConceptSet? _currentSet;
    private int _activeIndex;

    public event Action<Concept>? ConceptRotated;

    public RotationScheduler(WidgetStateManager stateManager)
    {
        _stateManager = stateManager;
    }

    public void LoadSet(DailyConceptSet set)
    {
        _currentSet = set;
        _activeIndex = 0;
        ConceptRotated?.Invoke(_currentSet.GetByIndex(_activeIndex));
    }

    public void Start()
    {
        _timer = new System.Threading.Timer(Tick, null, Interval, Interval);
    }

    public void Stop() => _timer?.Change(Timeout.Infinite, Timeout.Infinite);

    private void Tick(object? _)
    {
        // Pause rotation when the widget is Pinned (user is actively reading).
        if (_stateManager.Current == WidgetState.Pinned) return;
        if (_currentSet is null) return;

        _activeIndex = (_activeIndex + 1) % _currentSet.Count;
        ConceptRotated?.Invoke(_currentSet.GetByIndex(_activeIndex));
    }

    public void Dispose() => _timer?.Dispose();
}
```

Wire `DailyConceptScheduler.RunIfDueAsync` to a `Microsoft.Extensions.Hosting.BackgroundService` that checks "has today already run" (persist last-run date next to `Settings.json`) rather than a naive 24h timer — laptops sleep, timers drift.

**Cloud mode — `CloudPrefetchService`** layers on top of this:
- On startup (cloud mode): `FillToTargetAsync` fetches up to 7 days of `DailyConceptSet`s in a single batch.
- Deduplication runs across the **whole batch** at fetch time — `avoidList` grows concept-by-concept across all 7 days so nothing repeats within the batch or against `History.md`.
- `ConceptGenerationBackgroundService` drains the buffer via `TryConsumeAsync` each day instead of calling the API live.
- After each consume, if remaining count < 3 (threshold), `RefillIfConnectedAsync` fires as a background task — DNS check first, then `FillToTargetAsync`. Never blocks the UI.
- Exhausted buffer + no internet → raises `GenerationFailed`, surfaces existing error view.

`RotationScheduler` and the refresh scheduler follow the same one-job pattern; don't merge them into one "do everything" timer class.

---

## 6. Composition root

```csharp
// UI/App.xaml.cs (excerpt)
var host = Host.CreateDefaultBuilder()
    .ConfigureServices((ctx, services) =>
    {
        services.AddSingleton<ISettingsStore>(_ => settingsStore);  // pre-loaded instance
        services.AddSingleton<IConceptHistoryStore, MarkdownHistoryStore>();
        services.AddSingleton<IConceptBufferStore, JsonConceptBufferStore>(); // cloud buffer
        services.AddSingleton(_ => providerSettings);               // resolved from loaded settings
        services.AddHttpClient<IConceptProvider, OpenAiCompatibleProvider>();
        services.AddHttpClient<ModelDownloadService>();
        services.AddSingleton<WidgetStateManager>();
        services.AddSingleton<DailyConceptScheduler>();
        services.AddSingleton<CloudPrefetchService>();               // cloud mode only, safe always
        services.AddSingleton<RotationScheduler>();
        services.AddHostedService<ConceptGenerationBackgroundService>(); // drives both modes
        services.AddHostedService<RefreshScheduler>();
        services.AddSingleton<WidgetWindow>();
    })
    .Build();
```

**Startup sequence note:** `OpenAiCompatibleProvider` needs `ProviderSettings` from the loaded config, but DI containers build graphs before you've loaded anything async. Resolve `ISettingsStore.LoadAsync()` first in `OnStartup`, then register `ProviderSettings` as an instance (or use a factory in `AddHttpClient`) before the container builds the provider. Don't fight the DI container — load config synchronously-enough at boot, then build the host.

`ConceptGenerationBackgroundService` exposes `ConceptSetReady` and `GenerationFailed` events. Wire these to `WidgetWindow.OnConceptSetReady` / `OnGenerationFailed` **after** the host starts — both modes deliver through these two entry points so `WidgetWindow` never needs to know which mode is active.

---

## 7. UI — minimum viable views

- **Setup choice view** (shown first, only when `AppSettings.IsFirstRun == true`): two clickable option cards — "Local AI (free, offline)" and "Cloud AI (lightweight, needs API key)". Clicking either saves `Mode` + `IsFirstRun=false`, then continues into the appropriate first-run flow. This screen never shows again after the first run. The AI Settings screen still lets the user switch modes later.
- **Compact**: single `Border` + `TextBlock`, `Topmost="True"`, no taskbar entry (`ShowInTaskbar="False"` +
  `WS_EX_TOOLWINDOW` via `HwndSource` interop for full Alt-Tab hiding). v1 is a standard **always-on-top
  floating window** — the WorkerW "pin behind desktop icons" trick is explicitly deferred to v2 (see §9).
- **Expanded**: `Title` / `Explanation` / three buttons (`Read More`, `Copy`, `Next`) bound to commands
  that call `WidgetStateManager.Fire` and clipboard APIs. `Next` advances `RotationScheduler` to the next
  concept in the `DailyConceptSet` immediately (without waiting for the 7-min timer).
- **Pin toggle**: a single `ToggleButton` firing `WidgetTrigger.Pin` / `Unpin`. When Pinned, the
  `RotationScheduler` skips ticks so the user can read without the concept swapping under them.
- Collapse-on-outside-click: handle `Window.Deactivated`, not a global mouse hook.
- **First-run download view** (local mode): shown when no local model is present. Must display: named progress bar (bytes downloaded / total), a Cancel button, a Retry button on failure, and a plain-language error message — never a raw exception. On successful download, transitions directly into the normal Compact state without requiring a restart.
- **Error view** (both modes): plain-language message + Retry + AI Settings buttons. Retry in cloud mode attempts `RefillIfConnectedAsync` then `TryConsumeAsync`; in local mode retries `DailyConceptScheduler.RunIfDueAsync`.
- **System tray icon**: `Shell_NotifyIcon` Win32 interop (zero NuGet deps). Left-click toggles visibility; right-click shows context menu (Open/Hide, AI Settings, Quit).
- **Right-click context menu** on the window: Pin/Unpin, Copy, AI Settings, Quit.

Keep animations in one `ResourceDictionary` (`Animations.xaml`) — Fade/Scale/Slide/Expand/Collapse, all
200–250ms, one easing function reused everywhere.

---

## 8. Acceptance checklist (run before calling v1 done)

- [ ] Cold start < 300ms on a mid-range laptop
- [ ] Idle memory/CPU within target over a 1-hour soak
- [ ] Every row of the transition table in §0 exercised by a unit test
- [ ] `OpenAiCompatibleProvider` verified against **two** different local servers (proves the generic-endpoint bet actually holds)
- [ ] Airplane mode + local mode → still works
- [ ] Airplane mode + cloud mode → Retry/Settings message, no crash
- [ ] Hand-corrupt `Settings.json` → app still launches on defaults
- [ ] 30-day simulated run → no topic repeats across days, correct weekday categories, exactly 3 distinct concepts per day in history
- [ ] `RotationScheduler` advances index 0→1→2→0 every 7 minutes; pauses when widget is Pinned
- [ ] Fresh machine, no local model present → first-run download flow shows progress, retry works on simulated failure, completes without crash
- [ ] Uninstall → nothing left outside `%AppData%\DesktopConcepts`
- [ ] **Setup choice: selecting Local sets `Mode="local"` and `IsFirstRun=false` before any other first-run logic runs**
- [ ] **Setup choice: selecting Cloud sets `Mode="cloud"` and `IsFirstRun=false` before any other first-run logic runs**
- [ ] **Both modes work end-to-end with fake providers: local generates 3 concepts on demand; cloud consumes from prefetch buffer**
- [ ] **7-day cloud prefetch produces exactly 21 unique concepts with no duplicates against history or within the batch**
- [ ] **Buffer refill triggers (without blocking UI) when remaining count drops below 3 days**
- [ ] **Exhausted buffer + no internet → `GenerationFailed` raised, error view shown, no crash**

---

## 9. Explicit non-goals for v1
Multiple widgets, favorites, search history, voice narration, notifications, multi-language, cloud sync,
plugin system, community prompt packs. These are why the interfaces above are generic — so adding them
later is additive, not a rewrite.

**Confirmed in scope (not a non-goal):** both local mode and cloud mode ship fully functional in v1. The hybrid architecture is the whole point — do not remove either mode.

**Explicitly deferred to v2 (experimental):** "pin behind desktop icons" via the WorkerW layer. Relies on
undocumented Windows internals, breaks on Explorer restart, inconsistent across Windows 10/11 builds. v1
ships as a standard always-on-top floating window. Revisit if/when Microsoft exposes a stable API for it.
