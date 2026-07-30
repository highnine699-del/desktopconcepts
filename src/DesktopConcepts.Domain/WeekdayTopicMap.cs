namespace DesktopConcepts.Domain;

/// <summary>
/// Maps each day of the week to a configured topic category.
/// Kept separate from config parsing so Domain stays JSON-free.
/// </summary>
public sealed record WeekdayTopicMap(IReadOnlyDictionary<DayOfWeek, string> Categories)
{
    public string CategoryFor(DayOfWeek day) =>
        Categories.TryGetValue(day, out var category)
            ? category
            : throw new InvalidOperationException($"No category configured for {day}");
}
