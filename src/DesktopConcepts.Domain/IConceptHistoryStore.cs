namespace DesktopConcepts.Domain;

/// <summary>
/// Append-only store for generated concept sets.
/// All three concepts in a DailyConceptSet are written in a single operation.
/// </summary>
public interface IConceptHistoryStore
{
    /// <summary>
    /// Appends all three concepts in the set to the history store atomically.
    /// </summary>
    Task AppendSetAsync(DailyConceptSet conceptSet, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the most recent <paramref name="count"/> concept titles, oldest-first.
    /// Used to build the avoid-list fed back into the prompt.
    /// </summary>
    Task<IReadOnlyList<string>> GetRecentTitlesAsync(int count, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the most recent DailyConceptSet from the history, or null if history is empty.
    /// Used to reload today's concepts when the app restarts after already generating.
    /// </summary>
    Task<DailyConceptSet?> GetMostRecentSetAsync(CancellationToken cancellationToken);
}
