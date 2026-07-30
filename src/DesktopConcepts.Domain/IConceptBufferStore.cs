namespace DesktopConcepts.Domain;

/// <summary>
/// Cloud-mode-only prefetch buffer — completely separate from the append-only History.md.
///
/// Stores DailyConceptSets fetched ahead of time so the app can serve concepts when
/// the cloud API is temporarily unreachable (e.g. no internet for a day or two).
///
/// Contract:
///   - AddRangeAsync  : write a batch of pre-fetched sets (overwrites nothing; appends to queue)
///   - TryTakeNextAsync: remove and return the next unused set, or null if the buffer is empty
///   - CountAsync     : how many unused sets remain (used to decide when to refill)
///   - PeekDatesAsync : dates already in the buffer (used for deduplication at fetch time)
/// </summary>
public interface IConceptBufferStore
{
    /// <summary>Appends a batch of pre-fetched sets to the tail of the buffer queue.</summary>
    Task AddRangeAsync(IReadOnlyList<DailyConceptSet> sets, CancellationToken cancellationToken);

    /// <summary>
    /// Removes and returns the next unconsumed set, advancing the queue.
    /// Returns null when the buffer is empty.
    /// </summary>
    Task<DailyConceptSet?> TryTakeNextAsync(CancellationToken cancellationToken);

    /// <summary>Number of sets remaining in the buffer (not yet consumed).</summary>
    Task<int> CountAsync(CancellationToken cancellationToken);

    /// <summary>
    /// All dates already present in the buffer (consumed or not).
    /// Used at fetch time so the prefetch batch doesn't assign duplicate dates.
    /// </summary>
    Task<IReadOnlyList<DateOnly>> PeekDatesAsync(CancellationToken cancellationToken);
}
