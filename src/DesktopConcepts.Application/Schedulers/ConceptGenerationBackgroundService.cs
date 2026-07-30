using DesktopConcepts.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DesktopConcepts.Application.Schedulers;

/// <summary>
/// BackgroundService that drives daily concept delivery for BOTH modes.
///
/// Local mode:
///   Calls DailyConceptScheduler.RunIfDueAsync exactly as before — generates
///   3 concepts on demand, appends to History.md.
///
/// Cloud mode:
///   1. On startup, asks CloudPrefetchService to fill the buffer to 7 days.
///   2. Each day, consumes the next buffered DailyConceptSet instead of calling
///      the AI API live. After consuming, CloudPrefetchService triggers a silent
///      background refill if the buffer drops below the threshold (3 days).
///   3. If the buffer is empty (internet was unavailable for too long), falls back
///      to the existing GenerationFailed / error-view behavior.
///
/// Uses a date-file check rather than a naive 24h timer — laptops sleep, timers drift.
/// </summary>
public class ConceptGenerationBackgroundService : BackgroundService
{
    private readonly DailyConceptScheduler  _scheduler;
    private readonly CloudPrefetchService   _prefetch;
    private readonly ISettingsStore         _settings;
    private readonly ILogger<ConceptGenerationBackgroundService> _logger;

    // Raised for both modes when a set is ready — WidgetWindow subscribes to this
    public event Action<DailyConceptSet>? ConceptSetReady;
    public event Action<Exception>?       GenerationFailed;

    private static readonly string LastRunPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DesktopConcepts", "last_run.txt");

    public ConceptGenerationBackgroundService(
        DailyConceptScheduler  scheduler,
        CloudPrefetchService   prefetch,
        ISettingsStore         settings,
        ILogger<ConceptGenerationBackgroundService> logger)
    {
        _scheduler = scheduler;
        _prefetch  = prefetch;
        _settings  = settings;
        _logger    = logger;

        // Forward DailyConceptScheduler events (local mode)
        _scheduler.ConceptSetGenerated += set => ConceptSetReady?.Invoke(set);
        _scheduler.GenerationFailed    += ex  => GenerationFailed?.Invoke(ex);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = await _settings.LoadAsync(stoppingToken);

        // Cloud mode: pre-fill the buffer on startup before the first daily check
        if (settings.Mode == "cloud")
        {
            _logger.LogInformation("Cloud mode: checking prefetch buffer on startup.");
            await _prefetch.FillToTargetAsync(stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);

            if (!ShouldSkipToday())
            {
                _logger.LogInformation("Daily generation due for {Today}.", today);

                // Re-read settings each iteration — user may have switched mode
                settings = await _settings.LoadAsync(stoppingToken);

                if (settings.Mode == "cloud")
                    await RunCloudDayAsync(today, stoppingToken);
                else
                    await _scheduler.RunIfDueAsync(today, stoppingToken);

                PersistLastRun(today);
            }
            else
            {
                _logger.LogInformation("Already ran for {Today}. Sleeping until tomorrow.", today);
            }

            // Sleep until 1 minute after next midnight
            var nextRun = DateTime.Today.AddDays(1).AddMinutes(1);
            var delay   = nextRun - DateTime.Now;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, stoppingToken);
        }
    }

    // ── Cloud daily flow ──────────────────────────────────────────────────────

    private async Task RunCloudDayAsync(DateOnly today, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Cloud mode: consuming next buffered set for {Today}.", today);

        var set = await _prefetch.TryConsumeAsync(cancellationToken);

        if (set is not null)
        {
            _logger.LogInformation("Consumed buffered set for {Date}.", set.Date);
            ConceptSetReady?.Invoke(set);
        }
        else
        {
            // Buffer exhausted and internet unavailable — surface the existing error view
            _logger.LogWarning("Cloud buffer exhausted and prefetch unavailable for {Today}.", today);
            GenerationFailed?.Invoke(
                new InvalidOperationException(
                    "Cloud concept buffer is empty. Connect to the internet to refill."));
        }
    }

    // ── Date persistence ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the daily generation has already run for today.
    /// Protected virtual so tests can override it to force execution.
    /// </summary>
    protected virtual bool ShouldSkipToday()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        return AlreadyRanToday(today);
    }

    private static bool AlreadyRanToday(DateOnly today)
    {
        if (!File.Exists(LastRunPath)) return false;
        var raw = File.ReadAllText(LastRunPath).Trim();
        return DateOnly.TryParse(raw, out var last) && last == today;
    }

    private static void PersistLastRun(DateOnly date)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LastRunPath)!);
        File.WriteAllText(LastRunPath, date.ToString("yyyy-MM-dd"));
    }
}
