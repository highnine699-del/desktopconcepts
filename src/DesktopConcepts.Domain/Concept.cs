namespace DesktopConcepts.Domain;

/// <summary>
/// A single AI-generated technical concept. Immutable value object.
/// </summary>
public sealed record Concept(
    string Title,
    string Explanation,
    string Category,
    DateOnly GeneratedOn);
