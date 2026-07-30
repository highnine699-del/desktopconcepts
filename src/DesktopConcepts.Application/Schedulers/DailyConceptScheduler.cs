using DesktopConcepts.Domain;
using Microsoft.Extensions.Logging;

namespace DesktopConcepts.Application.Schedulers;

/// <summary>
/// One job: generate today's DailyConceptSet (exactly 3 concepts) once per calendar day.
///
/// Calls IConceptProvider 3 times, accumulating the avoid-list between calls so all three
/// concepts within the same day are distinct from each other and from history.
/// </summary>
public sealed class DailyConceptScheduler
{
    private readonly IConceptProvider _provider;
    private readonly IConceptHistoryStore _history;
    private readonly ISettingsStore _settings;
    private readonly ILogger<DailyConceptScheduler> _logger;

    /// <summary>Raised when a full DailyConceptSet has been generated and persisted.</summary>
    public event Action<DailyConceptSet>? ConceptSetGenerated;

    /// <summary>Raised when generation fails. Caller is responsible for UI (Retry / Open Settings).</summary>
    public event Action<Exception>? GenerationFailed;

    public DailyConceptScheduler(
        IConceptProvider provider,
        IConceptHistoryStore history,
        ISettingsStore settings,
        ILogger<DailyConceptScheduler> logger)
    {
        _provider = provider;
        _history  = history;
        _settings = settings;
        _logger   = logger;
    }

    /// <summary>
    /// Generates three concepts for <paramref name="today"/> if not already done.
    /// Called by <see cref="ConceptGenerationBackgroundService"/> on startup and daily check.
    /// </summary>
    public async Task RunIfDueAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var settings = await _settings.LoadAsync(cancellationToken);
        var category = settings.Topics.CategoryFor(today.DayOfWeek);

        // Seed avoid-list from history, then grow it within this call to prevent
        // intra-day duplicates across the 3 concepts.
        var avoidList = new List<string>(
            await _history.GetRecentTitlesAsync(30, cancellationToken));

        _logger.LogInformation(
            "Generating DailyConceptSet for {Date} ({Category}). Avoiding {Count} recent titles.",
            today, category, avoidList.Count);

        try
        {
            var concepts = new List<Concept>(3);
            for (var i = 0; i < 3; i++)
            {
                var concept = await _provider.GenerateConceptAsync(
                    category, avoidList, cancellationToken);

                concepts.Add(concept);
                avoidList.Add(concept.Title); // prevent intra-day duplicate
                _logger.LogInformation("  [{Slot}/3] Generated: {Title}", i + 1, concept.Title);
            }

            var set = new DailyConceptSet(today, concepts.AsReadOnly());
            await _history.AppendSetAsync(set, cancellationToken);

            ConceptSetGenerated?.Invoke(set);
        }
        catch (Exception ex)
        {
            // Never crash. Caller surfaces Retry / Open AI Settings UI.
            _logger.LogError(ex, "Concept generation failed for {Date}.", today);
            GenerationFailed?.Invoke(ex);
        }
    }
}
