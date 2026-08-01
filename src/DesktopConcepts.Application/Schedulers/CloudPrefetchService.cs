using DesktopConcepts.Domain;
using Microsoft.Extensions.Logging;

namespace DesktopConcepts.Application.Schedulers;

/// <summary>
/// Cloud-mode-only prefetch buffer service.
///
/// Responsibilities:
///   1. On startup (cloud mode): fill the buffer to 7 days if it has fewer than 7.
///   2. After every daily consumption: if remaining count drops below the refill
///      threshold (3), silently refill in the background — never blocking the UI.
///   3. Deduplication runs across the WHOLE batch at fetch time, not per-day, so
///      nothing repeats within the batch or against History.md.
///
/// This is NOT a BackgroundService — it is called explicitly by
/// ConceptGenerationBackgroundService so the two schedulers stay in sync.
/// </summary>
public class CloudPrefetchService
{
    public const int TargetBufferDays    = 7;
    public const int RefillThresholdDays = 3;

    private readonly IConceptProvider      _provider;
    private readonly IConceptBufferStore   _buffer;
    private readonly IConceptHistoryStore  _history;
    private readonly ISettingsStore        _settings;
    private readonly ILogger<CloudPrefetchService> _logger;

    // Prevents concurrent refills racing each other
    private int _refillInProgress; // 0 = idle, 1 = running (Interlocked)

    public CloudPrefetchService(
        IConceptProvider             provider,
        IConceptBufferStore          buffer,
        IConceptHistoryStore         history,
        ISettingsStore               settings,
        ILogger<CloudPrefetchService> logger)
    {
        _provider = provider;
        _buffer   = buffer;
        _history  = history;
        _settings = settings;
        _logger   = logger;
    }

    /// <summary>
    /// Returns the next buffered set for today.
    /// If the buffer is empty, returns null — caller shows the GenerationFailed/error view.
    /// After consuming, triggers a background refill if the buffer is running low.
    /// </summary>
    public async Task<DailyConceptSet?> TryConsumeAsync(CancellationToken cancellationToken)
    {
        var set = await _buffer.TryTakeNextAsync(cancellationToken);

        // Check threshold after every consume — fire-and-forget refill if needed
        var remaining = await _buffer.CountAsync(cancellationToken);
        _logger.LogInformation("Buffer: {Remaining} sets remaining after consume.", remaining);

        if (remaining < RefillThresholdDays)
        {
            _ = Task.Run(() => RefillIfConnectedAsync(CancellationToken.None),
                CancellationToken.None);
        }

        return set;
    }

    /// <summary>
    /// Fills the buffer up to <see cref="TargetBufferDays"/> sets.
    /// Called on startup and triggered automatically when the buffer drops below threshold.
    /// Safe to call from any thread — uses Interlocked to prevent concurrent runs.
    /// </summary>
    public virtual async Task FillToTargetAsync(CancellationToken cancellationToken)
    {
        var current = await _buffer.CountAsync(cancellationToken);
        var needed  = TargetBufferDays - current;

        if (needed <= 0)
        {
            _logger.LogInformation("Buffer already at target ({Current} days). No fetch needed.", current);
            return;
        }

        _logger.LogInformation("Prefetching {Needed} days into buffer (current: {Current}).", needed, current);

        var settings = await _settings.LoadAsync(cancellationToken);

        // Build the combined avoid-list: history titles + all titles already in the buffer
        var avoidList = new List<string>(
            await _history.GetRecentTitlesAsync(90, cancellationToken));

        // Also avoid titles already buffered so the whole batch is duplicate-free
        var bufferedDates = await _buffer.PeekDatesAsync(cancellationToken);
        // (titles in the buffer are already in history once appended; this covers
        //  the window between buffer-fill and the daily History.md write)

        var startDate = DateOnly.FromDateTime(DateTime.Now).AddDays(current + 1);
        var newSets   = new List<DailyConceptSet>(needed);

        try
        {
            for (var dayOffset = 0; dayOffset < needed; dayOffset++)
            {
                var date     = startDate.AddDays(dayOffset);
                var category = settings.Topics.CategoryFor(date.DayOfWeek);

                var concepts = new List<Concept>(3);
                for (var slot = 0; slot < 3; slot++)
                {
                    var concept = await _provider.GenerateConceptAsync(
                        category, avoidList, cancellationToken);
                    concepts.Add(concept);
                    avoidList.Add(concept.Title); // grow avoid-list across the whole batch
                    _logger.LogDebug("  Prefetch [{Day}/{Total}] slot [{Slot}/3]: {Title}",
                        dayOffset + 1, needed, slot + 1, concept.Title);
                }

                newSets.Add(new DailyConceptSet(date, concepts.AsReadOnly()));
            }

            await _buffer.AddRangeAsync(newSets, cancellationToken);
            _logger.LogInformation("Prefetch complete. Added {Count} sets.", newSets.Count);
        }
        catch (QuotaExceededException)
        {
            // Re-throw so callers can distinguish quota from network failures
            _logger.LogWarning("Quota exceeded during prefetch — propagating to caller.");
            throw;
        }
        catch (Exception ex)
        {
            // Network unavailable or other transient failure — log and surface nothing to UI.
            _logger.LogWarning(ex, "Prefetch failed — will retry on next trigger.");
        }
    }

    /// <summary>
    /// Checks for internet connectivity by attempting a lightweight DNS resolution,
    /// then refills if connected. Non-blocking — swallows all exceptions.
    /// </summary>
    public virtual async Task RefillIfConnectedAsync(CancellationToken cancellationToken)
    {
        // Interlocked.Exchange returns old value; if already 1, another refill is running
        if (Interlocked.Exchange(ref _refillInProgress, 1) == 1)
        {
            _logger.LogDebug("Refill already in progress — skipping duplicate trigger.");
            return;
        }

        try
        {
            if (!await IsInternetAvailableAsync(cancellationToken))
            {
                _logger.LogDebug("No internet detected — skipping prefetch refill.");
                return;
            }

            await FillToTargetAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Background refill failed silently.");
        }
        finally
        {
            Interlocked.Exchange(ref _refillInProgress, 0);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static async Task<bool> IsInternetAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Resolve the proxy's own host — if this succeeds the Worker is reachable.
            // Using the proxy host rather than a third-party domain means a positive result
            // directly implies the default cloud endpoint is up, not just "internet exists".
            var proxyHost = new Uri(AppSettings.DefaultProxyBaseUrl).Host;
            await System.Net.Dns.GetHostAddressesAsync(proxyHost, cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
