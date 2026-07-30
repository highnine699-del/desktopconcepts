namespace DesktopConcepts.Domain;

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);
}

/// <summary>
/// Full application settings. Validated on load — corrupt JSON falls back to Default().
///
/// IsFirstRun: true until the user completes the setup-time mode choice screen.
/// The setup screen writes false and saves before any other first-run logic runs.
/// </summary>
public sealed record AppSettings(
    string Mode,                    // "local" | "cloud"
    string Theme,
    bool IsFirstRun,                // true = setup choice not yet made
    ProviderSettings Provider,
    ProviderSettings CloudProvider,
    WeekdayTopicMap Topics)
{
    public static AppSettings Default() => new(
        Mode: "local",
        Theme: "dark",
        IsFirstRun: true,
        Provider: new ProviderSettings(
            BaseUrl: "http://localhost:1234/v1",
            Model: "phi-3-mini",
            ApiKey: null),
        // Default cloud model: Claude Haiku 4.5
        // Cheap enough at this app's volume (a few short generations/day) that cost is negligible.
        CloudProvider: new ProviderSettings(
            BaseUrl: "https://api.anthropic.com/v1",
            Model: "claude-haiku-4-5",
            ApiKey: null),
        Topics: new WeekdayTopicMap(new Dictionary<DayOfWeek, string>
        {
            [DayOfWeek.Monday]    = "Programming",
            [DayOfWeek.Tuesday]   = "Cybersecurity",
            [DayOfWeek.Wednesday] = "Networking",
            [DayOfWeek.Thursday]  = "AI",
            [DayOfWeek.Friday]    = "Operating Systems",
            [DayOfWeek.Saturday]  = "Mathematics",
            [DayOfWeek.Sunday]    = "Computer Engineering",
        }));
}

public sealed record ProviderSettings(
    string BaseUrl,
    string Model,
    string? ApiKey);
