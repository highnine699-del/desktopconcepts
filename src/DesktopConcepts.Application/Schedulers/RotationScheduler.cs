using DesktopConcepts.Domain;
using Microsoft.Extensions.Logging;

namespace DesktopConcepts.Application.Schedulers;

/// <summary>
/// One job: advance the active concept index (0 → 1 → 2 → 0) across the current
/// DailyConceptSet every 7 minutes.
///
/// Rotation pauses automatically when the widget is Pinned so the user can read
/// without the concept swapping under them.
/// </summary>
public class RotationScheduler : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(7);

    private readonly WidgetStateManager _stateManager;
    private readonly ILogger<RotationScheduler> _logger;
    private System.Threading.Timer? _timer;
    private DailyConceptSet? _currentSet;
    private int _activeIndex;

    /// <summary>Raised whenever the active concept changes (on load and on each tick).</summary>
    public event Action<Concept>? ConceptRotated;

    public RotationScheduler(
        WidgetStateManager stateManager,
        ILogger<RotationScheduler> logger)
    {
        _stateManager = stateManager;
        _logger       = logger;
    }

    /// <summary>
    /// Loads a new set and immediately fires <see cref="ConceptRotated"/> with index 0.
    /// Call this whenever a new DailyConceptSet is generated.
    /// </summary>
    public void LoadSet(DailyConceptSet set)
    {
        _currentSet  = set;
        _activeIndex = 0;
        _logger.LogInformation("RotationScheduler loaded set for {Date}.", set.Date);
        ConceptRotated?.Invoke(_currentSet.GetByIndex(_activeIndex));
    }

    /// <summary>Advances to the next concept immediately (used by the Next button in UI).</summary>
    public void AdvanceNow()
    {
        if (_currentSet is null) return;
        _activeIndex = (_activeIndex + 1) % _currentSet.Count;
        _logger.LogDebug("Manual advance to index {Index}.", _activeIndex);
        ConceptRotated?.Invoke(_currentSet.GetByIndex(_activeIndex));
    }

    public void Start()
    {
        _timer = new System.Threading.Timer(Tick, null, Interval, Interval);
        _logger.LogInformation("RotationScheduler started ({Interval} interval).", Interval);
    }

    public void Stop()
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _logger.LogInformation("RotationScheduler stopped.");
    }

    private void Tick(object? _)
    {
        // Pause when the user has pinned the widget — they're actively reading.
        if (_stateManager.Current == WidgetState.Pinned)
        {
            _logger.LogDebug("RotationScheduler tick skipped (widget is Pinned).");
            return;
        }

        if (_currentSet is null) return;

        _activeIndex = (_activeIndex + 1) % _currentSet.Count;
        _logger.LogDebug("RotationScheduler ticked to index {Index}.", _activeIndex);
        ConceptRotated?.Invoke(_currentSet.GetByIndex(_activeIndex));
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
