using Quire.Domain;
using Microsoft.Extensions.Logging;

namespace Quire.Application.Schedulers;

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

        // Build the combined avoid-list: history titles + all titles already in the buffer.
        // Adding buffered titles prevents the new batch from repeating concepts that are
        // already queued but not yet appended to History.md.
        var avoidList = new List<string>(
            await _history.GetRecentTitlesAsync(90, cancellationToken));

        var bufferedConcepts = await _buffer.PeekConceptsAsync(cancellationToken);
        foreach (var bc in bufferedConcepts)
            avoidList.Add(bc.Title);

        _logger.LogDebug(
            "Avoid-list: {HistoryCount} from history + {BufferCount} from buffer = {Total} total.",
            avoidList.Count - bufferedConcepts.Count, bufferedConcepts.Count, avoidList.Count);

        // startDate = today + current days.
        // When buffer is empty (current=0), startDate = today so today's concept is slot 0.
        // When buffer has 4 entries (current=4), startDate = today+4 so we top-up from day 4 onward.
        var startDate = DateOnly.FromDateTime(DateTime.Now).AddDays(current);
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

                    // Small delay between calls to avoid hitting provider rate limits.
                    // 21 calls in < 1 second reliably triggers 429 on the shared proxy.
                    // 300 ms between calls = ~6 s for a full 7-day batch — imperceptible.
                    if (slot < 2)
                        await Task.Delay(300, cancellationToken);
                }

                newSets.Add(new DailyConceptSet(date, concepts.AsReadOnly()));

                // Commit each completed day immediately — partial batches are saved
                // so a quota hit mid-run doesn't discard already-generated sets.
                await _buffer.AddRangeAsync(
                    new List<DailyConceptSet> { newSets[^1] }, cancellationToken);
                _logger.LogDebug("Committed day {Day}/{Total} to buffer.", dayOffset + 1, needed);

                // Delay between days too
                if (dayOffset < needed - 1)
                    await Task.Delay(400, cancellationToken);
            }

            _logger.LogInformation("Prefetch complete. Added {Count} sets.", newSets.Count);
        }
        catch (QuotaExceededException)
        {
            // Partial batch already committed above — log how much was saved
            _logger.LogWarning(
                "Quota exceeded during prefetch after {Done}/{Total} days — partial batch committed.",
                newSets.Count, needed);
            throw;
        }
        catch (Exception ex)
        {
            // Network unavailable or other transient failure — partial batch already committed.
            _logger.LogWarning(ex, "Prefetch failed after {Done}/{Total} days — will retry on next trigger.",
                newSets.Count, needed);
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

    /// <summary>
    /// Checks connectivity by sending an HTTP HEAD request to the proxy endpoint.
    /// A DNS-only check always succeeds for Cloudflare-hosted Workers even when
    /// the Worker itself is down — a real HTTP probe confirms the endpoint is up.
    /// Times out after 5 seconds to keep the background refill non-blocking.
    /// </summary>
    private static async Task<bool> IsInternetAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Use a short, separate HttpClient — not the injected provider client —
            // so a timeout here doesn't interfere with ongoing concept generation requests.
            using var cts  = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            using var http = new HttpClient();
            // HEAD to the proxy root — no body transferred, minimal quota impact.
            // We only need a 2xx/3xx/4xx status; any HTTP response means the worker is up.
            var response = await http.SendAsync(
                new HttpRequestMessage(HttpMethod.Head, AppSettings.DefaultProxyBaseUrl),
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token);

            // Any HTTP response (even 4xx) means the host is reachable and responding.
            // Only OperationCanceledException / HttpRequestException mean offline.
            return true;
        }
        catch
        {
            return false;
        }
    }
}
