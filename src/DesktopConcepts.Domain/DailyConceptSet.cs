namespace DesktopConcepts.Domain;

/// <summary>
/// The three concepts generated for a single calendar day.
/// The Rotation Scheduler cycles through Concepts[0 → 1 → 2 → 0] every 7 minutes.
/// Always contains exactly 3 concepts.
/// </summary>
public sealed record DailyConceptSet(
    DateOnly Date,
    IReadOnlyList<Concept> Concepts)
{
    /// <summary>Always 3.</summary>
    public int Count => Concepts.Count;

    /// <summary>
    /// Returns the concept at the given rotation index, wrapping automatically
    /// so callers never need to bounds-check.
    /// </summary>
    public Concept GetByIndex(int index) => Concepts[index % Concepts.Count];
}
