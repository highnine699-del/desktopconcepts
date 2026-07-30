using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopConcepts.Application.Schedulers;

/// <summary>
/// One job: periodically check for a newer version of the app and log when one is available.
/// Checks once at startup, then every 24 hours.
///
/// Uses the GitHub Releases API. Replace the repo slug to match the actual repo.
/// Never crashes the app — all failures are logged and swallowed.
/// </summary>
public sealed class RefreshScheduler : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    // Bump this with each release; checked against the GitHub latest-release tag.
    public const string CurrentVersion = "1.0.0";

    private readonly ILogger<RefreshScheduler> _logger;
    private readonly IHttpClientFactory        _httpFactory;

    public RefreshScheduler(IHttpClientFactory httpFactory, ILogger<RefreshScheduler> logger)
    {
        _httpFactory = httpFactory;
        _logger      = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Check immediately at startup, then on the 24h cadence
        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckForUpdateAsync(stoppingToken);
            await Task.Delay(CheckInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task CheckForUpdateAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var http    = _httpFactory.CreateClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopConcepts/" + CurrentVersion);

            // GitHub API: latest release for the repo
            const string apiUrl =
                "https://api.github.com/repos/kevwe/DesktopConcepts/releases/latest";

            var response = await http.GetAsync(apiUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Update check returned {Status}.", response.StatusCode);
                return;
            }

            var json    = await response.Content.ReadAsStringAsync(cancellationToken);
            var release = JsonSerializer.Deserialize<GitHubRelease>(json);

            if (release?.TagName is null) return;

            var latest = release.TagName.TrimStart('v');

            if (string.Compare(latest, CurrentVersion, StringComparison.Ordinal) > 0)
            {
                _logger.LogWarning(
                    "Update available: v{Latest} (current: v{Current}). Download: {Url}",
                    latest, CurrentVersion, release.HtmlUrl);
            }
            else
            {
                _logger.LogInformation("App is up to date (v{Current}).", CurrentVersion);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            // Never let an update check crash or surface to the user
            _logger.LogDebug(ex, "Update check failed silently.");
        }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]  public string? TagName  { get; init; }
        [JsonPropertyName("html_url")]  public string? HtmlUrl  { get; init; }
    }
}
