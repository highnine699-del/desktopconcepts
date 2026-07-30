namespace DesktopConcepts.Domain;

/// <summary>
/// The single gateway for all AI generation. Nothing else may call an HTTP client or SDK directly.
///
/// Contract: called exactly 3 times per calendar day to build a DailyConceptSet.
/// Each call produces one distinct Concept. The caller accumulates the avoid-list between
/// calls so all three concepts within the same day are distinct from each other and from history.
/// </summary>
public interface IConceptProvider
{
    Task<Concept> GenerateConceptAsync(
        string category,
        IReadOnlyCollection<string> recentTitlesToAvoid,
        CancellationToken cancellationToken);
}
