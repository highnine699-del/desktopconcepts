using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopConcepts.Application.Schedulers;

/// <summary>
/// Update checker: polls GitHub Releases every 24 hours and raises <see cref="UpdateAvailable"/>
/// when a newer version is found. Does NOT download or install anything autonomously —
/// the UI subscribes to the event and shows a consent banner before proceeding.
///
/// When the user consents, the UI calls <see cref="PerformUpdateAsync"/> directly.
/// </summary>
public sealed class RefreshScheduler : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly ILogger<RefreshScheduler> _logger;
    private readonly IHttpClientFactory        _httpFactory;

    /// <summary>
    /// Raised when a newer release is available on GitHub.
    /// Carries (newVersion, downloadUrl). The UI must show a consent banner before
    /// calling <see cref="PerformUpdateAsync"/>. Nothing is downloaded automatically.
    /// </summary>
    public event Action<string, string>? UpdateAvailable;

    public RefreshScheduler(IHttpClientFactory httpFactory, ILogger<RefreshScheduler> logger)
    {
        _httpFactory = httpFactory;
        _logger      = logger;
    }

    // ── Version helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns the current app version as a 3-part string (e.g. "1.0.8").
    /// Prefers AssemblyInformationalVersion; strips +metadata suffixes; trims to 3 parts.
    /// Falls back to "0.0.0" so a missing version triggers an update rather than hiding one.
    /// </summary>
    private static string GetCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly();
        if (assembly is null) return "0.0.0";

        // Prefer InformationalVersion ("1.0.8") over AssemblyName.Version ("1.0.8.0")
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrEmpty(informational))
        {
            // Strip build-metadata suffix: "1.0.8+abc123" → "1.0.8"
            var plusIdx = informational.IndexOf('+');
            return plusIdx >= 0 ? informational[..plusIdx] : informational;
        }

        // Fallback: use assembly version, but trim to 3 parts to match GitHub tag format
        var v = assembly.GetName().Version;
        return v is not null ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
    }

    /// <summary>
    /// Returns true when <paramref name="latest"/> is strictly greater than
    /// <paramref name="current"/> using semantic version comparison.
    /// Falls back to ordinal string comparison if either string cannot be parsed.
    /// </summary>
    private bool IsNewerVersion(string latest, string current)
    {
        if (Version.TryParse(latest, out var latestVer)
         && Version.TryParse(current, out var currentVer))
        {
            return latestVer > currentVer;
        }

        // One or both strings are not valid System.Version — fall back to ordinal
        _logger.LogWarning(
            "Could not parse version strings for semantic comparison: " +
            "latest='{Latest}', current='{Current}'. Using ordinal fallback.",
            latest, current);
        return string.Compare(latest, current, StringComparison.Ordinal) > 0;
    }

    // ── BackgroundService ─────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Check immediately at startup, then every 24 hours
        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckForUpdateAsync(stoppingToken);
            await Task.Delay(CheckInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    // ── Update check ──────────────────────────────────────────────────────────

    private async Task CheckForUpdateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var currentVersion = GetCurrentVersion();
            _logger.LogDebug("Checking for updates (current version: {Version})", currentVersion);

            using var http = _httpFactory.CreateClient("GitHubUpdate");

            const string apiUrl =
                "https://api.github.com/repos/highnine699-del/desktopconcepts/releases/latest";

            var response = await http.GetAsync(apiUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Update check returned {Status}.", response.StatusCode);
                return;
            }

            var json    = await response.Content.ReadAsStringAsync(cancellationToken);
            var release = JsonSerializer.Deserialize<GitHubRelease>(json);

            if (release?.TagName is null) return;

            var latest = release.TagName.TrimStart('v', 'V');

            _logger.LogDebug(
                "Version comparison: current='{Current}', latest='{Latest}' (raw tag='{Tag}')",
                currentVersion, latest, release.TagName);

            if (IsNewerVersion(latest, currentVersion))
            {
                _logger.LogInformation(
                    "Update available: v{Latest} (current: v{Current}). Notifying UI for consent.",
                    latest, currentVersion);

                // Find the installer asset — name contains "DesktopConcepts-Setup" and ends ".exe"
                var setupAsset = release.Assets?.FirstOrDefault(a =>
                    a.Name?.Contains("DesktopConcepts-Setup", StringComparison.OrdinalIgnoreCase) == true
                    && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

                if (setupAsset?.DownloadUrl is not null)
                {
                    // Raise event — UI shows consent banner; no download happens here
                    UpdateAvailable?.Invoke(latest, setupAsset.DownloadUrl);
                }
                else
                {
                    _logger.LogWarning(
                        "Update v{Latest} available but no DesktopConcepts-Setup*.exe asset found in release.",
                        latest);
                }
            }
            else
            {
                _logger.LogDebug("App is up to date (v{Current}).", currentVersion);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — not an error
        }
        catch (Exception ex)
        {
            // Never let an update check crash or surface to the user
            _logger.LogDebug(ex, "Update check failed silently.");
        }
    }

    // ── Public install entry point (called by UI after user consent) ──────────

    /// <summary>
    /// Downloads and silently installs the update from <paramref name="downloadUrl"/>.
    /// Called by <see cref="Views.WidgetWindow"/> after the user clicks "Update Now".
    /// All failures are logged and swallowed — the app stays running.
    /// </summary>
    public async Task PerformUpdateAsync(string downloadUrl, CancellationToken cancellationToken)
    {
        try
        {
            var tempPath = Path.Combine(
                Path.GetTempPath(), $"DesktopConcepts-Update-{Guid.NewGuid()}.exe");

            _logger.LogDebug("Downloading update to {TempPath}", tempPath);

            // Use the dedicated "UpdateDownload" client — timeout pre-configured at 10 minutes
            using var http = _httpFactory.CreateClient("UpdateDownload");

            var response = await http.GetAsync(
                downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to download installer: {Status}", response.StatusCode);
                return;
            }

            await using var fileStream    = File.Create(tempPath);
            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await contentStream.CopyToAsync(fileStream, cancellationToken);

            var downloadedSize = new FileInfo(tempPath).Length;
            _logger.LogInformation("Downloaded {Bytes} bytes to {TempPath}", downloadedSize, tempPath);

            if (downloadedSize == 0)
            {
                _logger.LogWarning("Downloaded file is empty — aborting update.");
                File.Delete(tempPath);
                return;
            }

            LaunchSilentInstall(tempPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Update download/install failed. Will retry on next check.");
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void LaunchSilentInstall(string setupPath)
    {
        try
        {
            _logger.LogInformation("Launching silent installer: {Path}", setupPath);

            // InnoSetup silent flags — no UI, no restart, restarts the app automatically
            var startInfo = new ProcessStartInfo
            {
                FileName        = setupPath,
                Arguments       = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
                UseShellExecute = true,
                WindowStyle     = ProcessWindowStyle.Hidden
            };

            Process.Start(startInfo);
            _logger.LogInformation("Silent installer launched. App may close and restart.");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to launch silent installer.");
        }
    }

    // ── GitHub API DTOs ───────────────────────────────────────────────────────

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string?        TagName { get; init; }
        [JsonPropertyName("html_url")] public string?        HtmlUrl { get; init; }
        [JsonPropertyName("assets")]   public GitHubAsset[]? Assets  { get; init; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]                   public string? Name        { get; init; }
        [JsonPropertyName("browser_download_url")]   public string? DownloadUrl { get; init; }
        [JsonPropertyName("size")]                   public long    Size        { get; init; }
    }
}
